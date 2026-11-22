using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using ApiTester.Core;

namespace ApiTester.Cli.Commands;

public static class CertsCommand
{
    public const string Help = """
        Usage: certapi certs [--filter <text>] [--store CurrentUser|LocalMachine]
                             [--valid-only] [--client-auth-only]
                             [--expiring-within <days> [--fail]] [--json]
               certapi certs --inspect <thumb|subject> [--store CurrentUser|LocalMachine]
                             [--check-revocation] [--json]
               certapi certs --inspect-file <path> [--cert-password <pw>] [--key-file <path>]
                             [--check-revocation] [--json]

        Lists client certificates from the Windows store (subject, thumbprint, expiry,
        client-auth EKU), or inspects one certificate in detail (SANs, EKUs, key algorithm/size,
        CNG provider / hardware backing, and chain status) without needing openssl.

          --filter <text>            Substring-match subject, issuer, or thumbprint
          --store <location>         CurrentUser (default); LocalMachine searches both stores
          --valid-only               Hide already-expired certificates
          --client-auth-only         Show only certificates carrying the Client Authentication EKU
          --expiring-within <days>   Show only certificates expired or expiring within <days> days
          --fail                     With --expiring-within: exit 1 when any matching certificate is found
          --inspect <thumb|subject>  Show full X.509 details for one store certificate
          --inspect-file <path>      Show full X.509 details for a .pfx/.p12/.pem/.crt/.cer file
          --cert-password <pw>       Password for an encrypted .pfx/.p12 file with --inspect-file
          --key-file <path>          Separate private key file for a PEM file with --inspect-file
          --check-revocation         Check online OCSP/CRL revocation when inspecting a certificate
          --json                     Emit JSON instead of text

        Global: --debug (verbose diagnostics) and --log-file <path> work here too.

        Examples:
          certapi certs
          certapi certs --store LocalMachine --valid-only
          certapi certs --expiring-within 30 --fail
          certapi certs --inspect "CN=My Client"
          certapi certs --inspect-file .\client.pfx --cert-password secret
          certapi certs --json
        """;

    public static int Run(Args args, TextWriter stdout, TextWriter stderr, CliServices services)
    {
        string? filter = args.Value("--filter");
        string store = args.Value("--store") ?? services.Profile?.Store ?? "CurrentUser";
        bool json = args.Flag("--json");
        bool validOnly = args.Flag("--valid-only");
        bool clientAuthOnly = args.Flag("--client-auth-only");
        string? expiringRaw = args.Value("--expiring-within");
        bool fail = args.Flag("--fail");
        string? inspectQuery = args.Value("--inspect");
        string? inspectFile = args.Value("--inspect-file");
        string? certPassword = args.Value("--cert-password");
        string? keyFile = args.Value("--key-file");
        bool checkRevocation = args.Flag("--check-revocation");
        if (args.Positionals().Count > 0) throw new CliUsageException(Help);

        bool includeLocalMachine = store.Equals("LocalMachine", StringComparison.OrdinalIgnoreCase);
        if (!includeLocalMachine && !store.Equals("CurrentUser", StringComparison.OrdinalIgnoreCase))
            throw new CliUsageException("--store must be CurrentUser or LocalMachine.");

        if (inspectQuery is not null && inspectFile is not null)
            throw new CliUsageException("--inspect and --inspect-file are mutually exclusive.");

        if (inspectQuery is not null || inspectFile is not null)
        {
            X509Certificate2? owned = null;
            X509Certificate2 target;
            try
            {
                if (inspectFile is not null)
                {
                    target = LoadInspectFile(inspectFile, certPassword, keyFile);
                    owned = target;
                }
                else
                {
                    var picked = CertPicker.Resolve(services.ListCertificates(includeLocalMachine), inspectQuery!, stderr);
                    target = picked.Certificate;
                }

                var details = CertificateInspector.Inspect(target, checkRevocation);
                if (json) stdout.WriteLine(JsonSerializer.Serialize(details, IndentedJson));
                else stdout.WriteLine(CertificateInspector.FormatText(details));
                return ExitCodes.Ok;
            }
            finally
            {
                owned?.Dispose();
            }
        }

        if (fail && expiringRaw is null)
            throw new CliUsageException("--fail only applies together with --expiring-within <days>.");

        int? expiringDays = null;
        if (expiringRaw is not null)
        {
            if (!int.TryParse(expiringRaw, out int d) || d < 0)
                throw new CliUsageException($"--expiring-within expects a non-negative integer number of days, got '{expiringRaw}'.");
            expiringDays = d;
        }

        var now = DateTime.Now;
        var certs = services.ListCertificates(includeLocalMachine)
            .Where(c => filter is null ||
                        c.Subject.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                        c.Issuer.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                        c.Thumbprint.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .Where(c => !validOnly || !c.IsExpired(now))
            .Where(c => !clientAuthOnly || c.HasClientAuthEku)
            .Where(c => expiringDays is null || c.NotAfter <= now.AddDays(expiringDays.Value))
            .ToList();

        if (certs.Count == 0) stderr.WriteLine("No client certificates found.");

        if (json)
        {
            stdout.WriteLine(JsonText.Indented(w =>
            {
                w.WriteStartArray();
                foreach (var c in certs)
                {
                    w.WriteStartObject();
                    w.WriteString("subject", c.Subject);
                    w.WriteString("issuer", c.Issuer);
                    w.WriteString("thumbprint", c.Thumbprint);
                    w.WriteString("notAfter", c.NotAfter);
                    w.WriteBoolean("expired", c.IsExpired(now));
                    w.WriteBoolean("clientAuthEku", c.HasClientAuthEku);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }));
            return fail && certs.Count > 0 ? ExitCodes.Failure : ExitCodes.Ok;
        }

        foreach (var c in certs)
        {
            string flags = (c.IsExpired(now) ? "  [EXPIRED]" : "") + (c.HasClientAuthEku ? "" : "  (no client-auth EKU)");
            stdout.WriteLine($"{c.Subject}\n  {c.Thumbprint}  ·  expires {c.NotAfter:yyyy-MM-dd}{flags}");
        }
        return fail && certs.Count > 0 ? ExitCodes.Failure : ExitCodes.Ok;
    }

    private static X509Certificate2 LoadInspectFile(string path, string? password, string? keyFile)
    {
        if (!File.Exists(path)) throw new CliDataException($"Certificate file not found: {path}");
        try
        {
            return CertificateFileLoader.Load(path, password, keyFile);
        }
        catch (CertificateFileException)
        {
            // Fall back to loading public-only certificates (.cer/.crt/.pem without private key).
            try
            {
                return X509CertificateLoader.LoadCertificateFromFile(path);
            }
            catch (Exception ex)
            {
                throw new CliDataException($"Could not read certificate '{path}': {ex.Message}");
            }
        }
    }

    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };
}
