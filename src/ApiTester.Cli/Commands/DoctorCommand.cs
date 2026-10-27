using System.Text.Json;
using ApiTester.Core;

namespace ApiTester.Cli.Commands;

public static class DoctorCommand
{
    public const string Help = """
        Usage: certapi doctor <url> [options]

        Diagnoses a connection one stage at a time — URL, DNS, proxy decision, TCP, the proxy
        tunnel, the TLS handshake, and finally an HTTP GET — and reports the stage that broke
        rather than a single error line. Every stage is timed.

        What it can tell you that a normal request cannot:
          * the certificate authorities the server accepts client certificates from, matched
            against the certificates you actually have ("none of yours are issued by those")
          * whether this network is decrypting TLS in the middle, which is why a client
            certificate cannot reach the server through it
          * which proxy the machine picks for this URL, including one chosen by a PAC script,
            and what the proxy said if it refused
          * whether the internet is reachable at all, or a captive portal is in the way

        Options:
          --cert <thumb|subject>  Client certificate from the Windows store
          --store <location>      CurrentUser (default); LocalMachine searches both stores
          --cert-file <path>      Client certificate from a file (.pfx/.p12 or .pem/.crt) instead
          --cert-password <pw>    Password for a .pfx/.p12 certificate file
          --key-file <path>       Private-key file for a PEM certificate whose key is separate
          --cert-issuer <substr>  Narrow --cert to certificates issued by a matching CA (or use alone)
          --cert-serial <hex>     Pick the certificate with this serial number (or use alone)
          --cert-pin <pin>        Smart-card / HSM PIN for the key, supplied ahead of time so a
                                  headless job is not stopped by the prompt. Prefer CERTAPI_CERT_PIN,
                                  which keeps it out of the shell history. Windows (CNG) only
          --insecure              Ignore server certificate errors while probing
          --json                  Print the whole report as JSON instead of text
          -q, --quiet             Only print stages that failed or carry advice
          --md <file>             Also write the diagnosis as a markdown note — the stage table,
                                  every detail line, the advice, and (the part worth keeping) the
                                  server's acceptable client-certificate authorities and any
                                  TLS-interception finding, verbatim
          --md-vault <folder>     Write that note into a vault instead, as
                                  certapi/investigations/<host>-<timestamp>.md. A new note per run:
                                  a diagnosis is history, so nothing overwrites a past one
          --md-open               Open the written note in the default application for it
          --include-secrets       Keep credential-looking query values in the note (redacted by
                                  default — a vault syncs, so the note is likely to leave the
                                  machine)

        """ + "\n" + TransportFlags.StreamHelp + "\n" + """

        Global: --debug (verbose diagnostics) and --log-file <path> work here too.

        Examples:
          certapi doctor https://api.example.com/health
          certapi doctor https://api.example.com --cert "CN=My Client"
          certapi doctor https://api.example.com --proxy http://proxy.corp:8080 --json
          certapi doctor https://api.example.com/health --md investigation.md
          certapi doctor https://api.example.com/health --md-vault C:\Users\me\Vault

        Exit 0 when every stage passed, 1 when one failed, 2 usage, 3 data errors.
        """;

    public static int Run(Args args, TextWriter stdout, TextWriter stderr, CliServices services)
    {
        string store = args.Value("--store") ?? services.Profile?.Store ?? "CurrentUser";
        bool json = args.Flag("--json");
        bool quiet = args.Flag("-q", "--quiet");
        bool insecure = args.Switch("--insecure", services.Profile?.Insecure);
        string? mdFile = args.Value("--md");
        string? mdVault = args.Value("--md-vault");
        bool mdOpen = args.Flag("--md-open");
        bool includeSecrets = args.Flag("--include-secrets");
        if (mdFile is not null && mdVault is not null)
            throw new CliUsageException("--md and --md-vault both name where the note goes; pick one.");
        if ((mdOpen || includeSecrets) && mdFile is null && mdVault is null)
            throw new CliUsageException("--md-open and --include-secrets only apply with --md or --md-vault.");
        var transport = TransportFlags.ParseStreamSubset(args, insecure, environment: null, services.Profile);
        var cert = CliCert.Resolve(args, store, services, stderr);

        var positionals = args.Positionals();
        if (positionals.Count != 1) throw new CliUsageException(Help);
        string url = positionals[0];

        bool includeLocalMachine = store.Equals("LocalMachine", StringComparison.OrdinalIgnoreCase);
        // The whole store, not just the chosen certificate: the point is to answer "do I have
        // anything the server would accept?", which needs the candidates it did not pick.
        var storeCerts = new List<System.Security.Cryptography.X509Certificates.X509Certificate2>();
        try
        {
            foreach (var info in services.ListCertificates(includeLocalMachine))
                if (services.FindCertificate(info.Thumbprint) is { } found) storeCerts.Add(found);
        }
        catch (Exception ex) { services.Log.Debug("could not enumerate the certificate store: " + ex.Message); }

        var report = ConnectionDoctor
            .RunAsync(url, cert, storeCerts, transport, probe: null, services.Cancel)
            .GetAwaiter().GetResult();

        if (json) stdout.WriteLine(ToJson(report));
        else Render(report, stdout, quiet);

        if (mdFile is not null || mdVault is not null)
            WriteNote(report, mdFile, mdVault, mdOpen, includeSecrets, stderr, services);

        return report.Ok ? ExitCodes.Ok : ExitCodes.Failure;
    }

    private static void Render(DoctorReport report, TextWriter stdout, bool quiet)
    {
        // The URL is echoed back, and it may carry `user:password@` — which is printed to a
        // terminal, pasted into tickets, and serialised into --json.
        stdout.WriteLine($"certapi doctor · {MarkdownSecrets.RedactUrl(report.Url)}");
        stdout.WriteLine();
        foreach (var stage in report.Stages)
        {
            bool interesting = !stage.Ok || stage.Advice is not null;
            if (quiet && !interesting) continue;

            string mark = stage.Ok ? "ok  " : "FAIL";
            stdout.WriteLine($"  [{mark}] {stage.Name,-8} {stage.Summary}  ({stage.Elapsed.TotalMilliseconds:F0} ms)");
            if (!quiet)
                foreach (var line in stage.Detail) stdout.WriteLine($"           {line}");
            if (stage.Advice is not null) stdout.WriteLine($"           → {stage.Advice}");
        }
        stdout.WriteLine();
        stdout.WriteLine(report.Ok
            ? $"All stages passed in {report.Stages.Sum(s => s.Elapsed.TotalMilliseconds):F0} ms."
            : $"Stopped at '{report.FirstFailure!.Name}': {report.FirstFailure.Summary}");
    }

    /// <summary>Write the note, then say where it went. A failure to write must not change the
    /// command's exit code: the diagnosis already ran and its result is the answer the user asked
    /// for — losing it because a folder was read-only would be the wrong trade.</summary>
    private static void WriteNote(DoctorReport report, string? file, string? vault, bool open,
                                  bool includeSecrets, TextWriter stderr, CliServices services)
    {
        var when = DateTimeOffset.UtcNow;
        string path = file ?? Path.Combine(vault!,
            DoctorMarkdown.VaultPath(report, when).Replace('/', Path.DirectorySeparatorChar));

        try
        {
            if (Path.GetDirectoryName(path) is { Length: > 0 } folder) Directory.CreateDirectory(folder);
            File.WriteAllText(path, DoctorMarkdown.Render(report, when, includeSecrets));
            stderr.WriteLine($"wrote the investigation note to {path}");
        }
        catch (Exception ex)
        {
            stderr.WriteLine($"warning: could not write the note to {path}: {ex.Message}");
            return;
        }

        if (!open) return;
        try { services.OpenFile(path); }
        catch (Exception ex)
        {
            // Degrading to the path is the whole contract here: --md-open is a convenience, and a
            // machine with no application registered for .md must not turn that into a failure.
            stderr.WriteLine($"note: could not open it ({ex.Message}) — it is at {path}");
        }
    }

    private static string ToJson(DoctorReport report) =>
        JsonSerializer.Serialize(new
        {
            url = MarkdownSecrets.RedactUrl(report.Url),
            ok = report.Ok,
            failedStage = report.FirstFailure?.Name,
            stages = report.Stages.Select(s => new
            {
                name = s.Name,
                ok = s.Ok,
                summary = s.Summary,
                detail = s.Detail,
                advice = s.Advice,
                elapsedMs = (int)s.Elapsed.TotalMilliseconds
            })
        });
}
