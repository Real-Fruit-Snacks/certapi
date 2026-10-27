using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using ApiTester.Core;

namespace ApiTester.Cli.Commands;

public static class FuzzCommand
{
    public const string Help = """
        Usage: certapi fuzz <base-url> [-w <wordlist>] [options]

        Probes candidate endpoints against <base-url> and reports which ones exist — the fastest
        way to map an undocumented API. With no -w it uses a built-in starter list; a line is
        "PATH" or "METHOD PATH"; blank lines and #comments are ignored; a full https:// line
        overrides the base URL.

        Wordlist:
          -w, --wordlist <file|->  Endpoints to probe (omit for the built-in starter list — a
                                   curated ~270 paths by category; '-' reads them from stdin).
                                   For an exhaustive sweep, supply your own larger list.
          -X, --methods <list>     Comma-separated methods to try per path (default GET)

        TLS / certificates:
          --cert <thumb|subject>   Client certificate from the Windows store
          --store <location>       CurrentUser (default); LocalMachine searches both stores
          --cert-file <path>       Client certificate from a file (.pfx/.p12 or .pem/.crt) instead
          --cert-password <pw>     Password for a .pfx/.p12 certificate file
          --key-file <path>        Private-key file for a PEM cert whose key is separate
          --cert-issuer <substr>   Narrow --cert to certificates issued by a matching CA (or use alone)
          --cert-serial <hex>      Pick the certificate with this serial number (or use alone)
          --cert-pin <pin>         Smart-card / HSM PIN for the key, supplied ahead of time so a
                                   headless job is not stopped by the prompt. Prefer CERTAPI_CERT_PIN,
                                   which keeps it out of the shell history. Windows (CNG) only
          --insecure               Ignore server certificate errors
          --timeout <seconds>      Per-probe timeout (default 100)

        Auth & variables:
          -H, --header "k: v"      Add a header to every probe (repeatable)
          --bearer <token>         Authorization: Bearer … on every probe
          --env <name> / --var k=v Resolve {{variables}} in the base URL and paths
          --no-auto-token          Don't attach or capture session tokens


        """ + TransportFlags.Help + """


        Probes never follow redirects — a 3xx is itself a discovery signal — so the redirect
        options above have no effect here.

        Discovery:
          --concurrency <n>        Parallel probes, 1–50 (default 8)
          --delay <ms>             Pause between probes (be polite; default 0)
          --hide <codes>           Hide only these status codes (overrides the default view)
          --match <codes>          Show only these status codes
          --all                    Show every probe, including 404s and errors

        By default (no --all, --match, or --hide) the table hides 404s and connection errors,
        the same noise the GUI hides.

        Output:
          --json                   JSON { results, summary } instead of the table
          -o, --output <file>      Write discovered paths as a wordlist (or the JSON report)
          --save-collection <name> Save discovered endpoints as requests in a collection
          --workspace <file>       Use a workspace file instead of the live GUI state
          -q, --quiet              No progress counter on stderr
          --har <file>             Capture every probe as a HAR file, written once when the run
                                   finishes
          --har-include-secrets    Don't redact Authorization/Proxy-Authorization/Cookie/
                                   Set-Cookie values in the captured HAR (redacted by default)

        Global: --debug (verbose diagnostics) and --log-file <path> work here too.

        Examples:
          # Quick look with the built-in starter list — no wordlist needed
          certapi fuzz https://api.example.com --cert "CN=My Client"

          # Probe your own wordlist with a client certificate
          certapi fuzz https://api.example.com -w .\endpoints.txt --cert "CN=My Client"

          # Try several methods, show everything, go faster
          certapi fuzz https://api.example.com -w .\endpoints.txt -X GET,POST,PUT --all --concurrency 16

          # Log in first (token is captured), then discover authenticated endpoints
          certapi send https://api.example.com/login -X POST -d '{"user":"me"}'
          certapi fuzz https://api.example.com -w .\endpoints.txt

          # Pipe a wordlist in and save what you find as a collection
          type .\big-list.txt | certapi fuzz https://api.example.com -w - --save-collection Discovered

          # Machine-readable, only interesting results, into a file
          certapi fuzz https://api.example.com -w .\endpoints.txt --match 200,401,403 --json -o hits.json

          # Capture every probe as a HAR file for later replay or review
          certapi fuzz https://api.example.com -w .\endpoints.txt --har probes.har

        Exit 0 on completion, 1 if every probe failed to connect, 2 usage, 3 data error.
        """;

    public static int Run(Args args, TextReader input, TextWriter stdout, TextWriter stderr, CliServices services)
    {
        string? wordlist = args.Value("-w", "--wordlist");
        string? methodsRaw = args.Value("-X", "--methods");
        var headers = args.Values("-H", "--header");
        string? bearer = args.Value("--bearer");
        string store = args.Value("--store") ?? services.Profile?.Store ?? "CurrentUser";
        bool insecure = args.Switch("--insecure", services.Profile?.Insecure);
        string? timeoutRaw = args.Value("--timeout");
        string? envName = args.Value("--env");
        var varOverrides = args.Values("--var");
        bool noAutoToken = args.Flag("--no-auto-token");
        string? concurrencyRaw = args.Value("--concurrency");
        string? delayRaw = args.Value("--delay");
        string? hideRaw = args.Value("--hide");
        string? matchRaw = args.Value("--match");
        bool all = args.Flag("--all");
        bool json = args.Flag("--json");
        string? outFile = args.Value("-o", "--output");
        string? saveCollection = args.Value("--save-collection");
        string? workspace = args.Value("--workspace");
        bool quiet = args.Flag("-q", "--quiet");
        string? harPath = args.Value("--har");
        bool harIncludeSecrets = args.Flag("--har-include-secrets");
        // --show-redirects is accepted and has nothing to report: probes never follow redirects.
        var transportOverrides = TransportFlags.Parse(args, out _, environment: null, services.Profile);
        // Resolve the certificate (store or file) before Positionals() rejects its options.
        var cert = CliCert.Resolve(args, store, services, stderr);

        var positionals = args.Positionals();
        if (positionals.Count != 1) throw new CliUsageException(Help);
        string baseUrl = positionals[0];

        // An explicit --workspace file that doesn't exist is an error — unless --save-collection is
        // creating it (a discovery write starts from an empty workspace).
        if (workspace is not null && saveCollection is null && !File.Exists(workspace))
            throw new CliDataException($"Workspace file not found: {workspace}");

        // --har's directory must exist before the first probe goes out, not merely before the write.
        if (harPath is not null)
        {
            var harDir = Path.GetDirectoryName(Path.GetFullPath(harPath));
            if (!Directory.Exists(harDir)) throw new CliUsageException($"--har directory does not exist: {harDir}");
        }

        int timeout = ParsePositive(timeoutRaw, 100, "--timeout");
        int concurrency = ParsePositive(concurrencyRaw, 8, "--concurrency");
        int delay = ParseNonNegative(delayRaw, 0, "--delay");
        var methods = (methodsRaw ?? "GET").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(m => m.ToUpperInvariant()).ToArray();
        MethodOption.RequireList(methodsRaw, "-X/--methods", methods.Length);

        // ---- variables ----
        var state = LoadState(workspace, services, stderr);
        // Every probe targets the same base URL, so one predicate covers the whole run — and,
        // unlike a fresh lambda per probe, lets probes to the same host share a pooled connection.
        var predicates = new TrustPredicates(state);
        var vars = CliWorkspace.BuildVars(state, envName, varOverrides);
        string R(string s) => VariableResolver.Resolve(s ?? "", vars).Result;   // (Result, Unresolved) tuple
        baseUrl = R(baseUrl);

        // Probes never follow redirects — a 3xx is a discovery signal, not something to chase — so
        // that is the baseline the flags apply over. Every probe shares this host, so one check
        // against the base URL settles whether the combination is usable at all.
        var transport = transportOverrides.ApplyTo(
            new TransportOptions { FollowRedirects = false, IgnoreServerCertificateErrors = insecure });
        if (ApiClient.ValidateTransport(transport, baseUrl) is { } transportProblem)
            throw new CliUsageException(transportProblem);

        // ---- wordlist ----
        string listText;
        if (wordlist == "-") listText = input.ReadToEnd();
        else if (wordlist is null) listText = BuiltInWordlist.Text;   // no -w: use the built-in starter list
        else if (!File.Exists(wordlist)) throw new CliDataException($"Wordlist not found: {wordlist}");
        else listText = File.ReadAllText(wordlist);
        var entries = EndpointList.Parse(listText);
        if (entries.Count == 0) throw new CliDataException("The wordlist has no endpoints (only blanks/comments).");
        if (wordlist is null && !quiet)
            stderr.WriteLine($"note: no wordlist given — using the built-in starter list ({entries.Count} endpoints; pass -w <file> for a thorough sweep)");

        // ---- headers / auth ----
        var headerPairs = new List<KeyValuePair<string, string>>();
        foreach (var raw in headers)
        {
            int colon = raw.IndexOf(':');
            if (colon <= 0) throw new CliUsageException($"Header must be \"Name: value\", got '{raw}'.");
            headerPairs.Add(new(R(raw[..colon].Trim()), R(raw[(colon + 1)..].Trim())));
        }
        if (bearer is not null) headerPairs.Add(new("Authorization", "Bearer " + R(bearer)));

        var plan = new FuzzPlan
        {
            BaseUrl = baseUrl,
            Entries = entries,
            Methods = methods,
            Headers = headerPairs,
            Concurrency = concurrency,
            DelayMs = delay
        };

        // The send delegate owns transport: per-request auto-token attach + capture, the cert,
        // insecure, and the timeout.
        var captureLock = new object();
        // A pinned server-certificate thumbprint lets a probe through even without --insecure;
        // --insecure (above) already trusts anything and is untouched.
        var harEntries = harPath is not null ? new List<HarEntry>() : null;
        async Task<ApiResponse> Send(ApiRequest request, CancellationToken ct)
        {
            var reqHeaders = request.Headers.ToList();
            if (!noAutoToken) { lock (captureLock) TokenService.AutoAttach(state, request.Url, reqHeaders, out _); }
            var probe = request with { Headers = reqHeaders, Timeout = TimeSpan.FromSeconds(timeout) };
            var response = await services.Client.SendAsync(probe, cert,
                transport: transport,
                trustServerCertificate: predicates.For(TokenService.HostOf(request.Url)),
                cancellationToken: ct);
            if (!noAutoToken && response.Error is null)
                lock (captureLock) TokenService.Capture(state, request.Url, response.Body, response.ContentType, response.Headers);
            // Probes never follow redirects, so one entry per probe (no redirect hops to unfold).
            if (harEntries is not null)
                lock (captureLock) harEntries.Add(HarWriter.FromExchange(probe, response, harIncludeSecrets));
            return response;
        }

        int lastReported = 0;
        var progressLock = new object();
        var progress = quiet ? null : new Progress<FuzzProgress>(p =>
        {
            // Workers report from concurrent threads with no SynchronizationContext on a console,
            // so serialize the counter update and the \r write to stop them interleaving.
            lock (progressLock)
            {
                if (p.Completed - lastReported >= 10 || p.Completed == p.Total)
                { lastReported = p.Completed; stderr.Write($"\r  probing {p.Completed}/{p.Total}…"); stderr.Flush(); }
            }
        });

        services.Log.Debug($"fuzz {MarkdownSecrets.RedactUrl(baseUrl)} · {entries.Count} entries × {methods.Length} method(s) · concurrency {concurrency}");
        FuzzReport report;
        try { report = EndpointFuzzer.RunAsync(plan, Send, progress, services.Cancel).GetAwaiter().GetResult(); }
        catch (OperationCanceledException)
        {
            stderr.WriteLine("\ncancelled.");
            return ExitCodes.Ok;
        }
        if (!quiet) stderr.WriteLine();

        // ---- persist captured tokens / discovered collection ----
        bool dirty = false;
        if (saveCollection is not null)
        {
            SaveDiscovered(state, report, baseUrl, cert, saveCollection);
            dirty = true;
        }
        if (dirty)
        {
            if (workspace is null && services.IsGuiRunning())
                stderr.WriteLine("note: the GUI is running — the discovered collection was not saved (it would overwrite it on close).");
            else
            {
                string path = workspace ?? services.LiveStatePath;
                try { CliWorkspace.ReportSaveResult(state.SaveTo(path), path, stderr); }
                catch (Exception ex) { stderr.WriteLine($"warning: could not save: {ex.Message}"); }
            }
        }

        if (harPath is not null && harEntries is not null)
        {
            File.WriteAllText(harPath, HarWriter.Write(harEntries, HarCreatorVersion()));
            if (!quiet) stderr.WriteLine($"wrote HAR to {harPath} ({harEntries.Count} entr{(harEntries.Count == 1 ? "y" : "ies")})");
        }

        // ---- output ----
        var shown = Filter(report.Results, all, matchRaw, hideRaw);
        if (json)
        {
            string js = BuildJson(report, shown);
            if (outFile is not null) { File.WriteAllText(outFile, js); stderr.WriteLine($"wrote {outFile}"); }
            else stdout.WriteLine(js);
        }
        else
        {
            foreach (var r in shown.OrderByDescending(r => FuzzClassifier.IsDiscovery(r.Outcome)).ThenBy(r => r.StatusCode ?? 999))
                stdout.WriteLine(
                    $"{Label(r.Outcome),-8} {(r.StatusCode?.ToString() ?? "ERR"),4}  {r.Method,-6} {OutputText.Size(r.SizeBytes),9}  {r.Path}{(r.Error is not null ? $"  ({r.Error})" : "")}");
            stdout.WriteLine($"----\n{report.Total} probed · {report.Discovered} discovered · " +
                string.Join(" · ", report.CountsByOutcome.OrderBy(k => k.Key).Select(k => $"{Label(k.Key)} {k.Value}")));
            if (outFile is not null)
            {
                var paths = report.Results.Where(r => FuzzClassifier.IsDiscovery(r.Outcome)).Select(r => r.Path).Distinct();
                File.WriteAllLines(outFile, paths);
                stderr.WriteLine($"wrote discovered paths to {outFile}");
            }
        }

        return report.AllErrored ? ExitCodes.Failure : ExitCodes.Ok;
    }

    private static AppState LoadState(string? workspace, CliServices services, TextWriter stderr) =>
        workspace is not null && !File.Exists(workspace) ? new AppState()
        : workspace is null && !File.Exists(services.LiveStatePath) ? new AppState()
        : CliWorkspace.Load(workspace, services.LiveStatePath, stderr);

    private static void SaveDiscovered(AppState state, FuzzReport report, string baseUrl,
        System.Security.Cryptography.X509Certificates.X509Certificate2? cert, string name)
    {
        var folder = state.Collections.FirstOrDefault(c => c.IsFolder && c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (folder is null) { folder = new CollectionNode { Name = name, IsFolder = true }; state.Collections.Add(folder); }
        foreach (var r in report.Results.Where(x => FuzzClassifier.IsDiscovery(x.Outcome)))
        {
            var model = new RequestModel { Method = r.Method, BaseUrl = baseUrl, Path = r.Path, CertThumbprint = cert?.Thumbprint };
            folder.Children.Add(new CollectionNode { Name = $"{r.Method} {r.Path}", IsFolder = false, Request = model });
        }
    }

    private static IReadOnlyList<FuzzResult> Filter(IReadOnlyList<FuzzResult> results, bool all, string? match, string? hide)
    {
        if (all) return results;
        if (match is not null)
        {
            var codes = ParseCodes(match);
            return results.Where(r => r.StatusCode is { } s && codes.Contains(s)).ToList();
        }
        if (hide is not null)
        {
            var hidden = ParseCodes(hide);
            return results.Where(r => r.StatusCode is not { } s || !hidden.Contains(s)).ToList();
        }
        // Default view: hide the noise — 404s and transport errors — matching the GUI's "Hide 404s / errors".
        return results.Where(r => r.Outcome is not (FuzzOutcome.NotFound or FuzzOutcome.Error)).ToList();
    }

    private static HashSet<int> ParseCodes(string csv) =>
        csv.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
           .Select(x => int.TryParse(x, out var n) ? n : -1).Where(n => n > 0).ToHashSet();

    private static string BuildJson(FuzzReport report, IReadOnlyList<FuzzResult> shown)
    {
        var obj = new
        {
            results = shown.Select(r => new
            {
                method = r.Method, path = r.Path, url = r.Url, status = r.StatusCode,
                outcome = r.Outcome.ToString(), discovered = FuzzClassifier.IsDiscovery(r.Outcome),
                sizeBytes = r.SizeBytes, elapsedMs = Math.Round(r.Elapsed.TotalMilliseconds), error = r.Error
            }),
            summary = new
            {
                total = report.Total, discovered = report.Discovered,
                byOutcome = report.CountsByOutcome.ToDictionary(k => k.Key.ToString(), v => v.Value)
            }
        };
        return JsonSerializer.Serialize(obj, new JsonSerializerOptions { WriteIndented = true });
    }

    private static string Label(FuzzOutcome o) => o switch
    {
        FuzzOutcome.Found => "found",
        FuzzOutcome.Redirect => "redirect",
        FuzzOutcome.Unauthorized => "auth",
        FuzzOutcome.MethodNotAllowed => "method",
        FuzzOutcome.NotFound => "404",
        FuzzOutcome.ServerError => "5xx",
        FuzzOutcome.OtherStatus => "other",
        _ => "error"
    };

    private static int ParsePositive(string? raw, int fallback, string opt)
    {
        if (raw is null) return fallback;
        if (!int.TryParse(raw, out var n) || n <= 0) throw new CliUsageException($"{opt} expects a positive number, got '{raw}'.");
        return n;
    }

    private static int ParseNonNegative(string? raw, int fallback, string opt)
    {
        if (raw is null) return fallback;
        if (!int.TryParse(raw, out var n) || n < 0) throw new CliUsageException($"{opt} expects a non-negative number, got '{raw}'.");
        return n;
    }

    /// <summary>The creator version written into a captured HAR document — the same idiom as
    /// <c>certapi --version</c>: the assembly's informational version with any <c>+build</c>
    /// metadata stripped.</summary>
    private static string HarCreatorVersion()
    {
        var version = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
        int plus = version.IndexOf('+');
        return plus > 0 ? version[..plus] : version;
    }
}
