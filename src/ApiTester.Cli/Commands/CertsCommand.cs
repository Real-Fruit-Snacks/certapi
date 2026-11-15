using System.Text.Json;
using ApiTester.Core;

namespace ApiTester.Cli.Commands;

public static class CertsCommand
{
    public const string Help = """
        Usage: certapi certs [--filter <text>] [--store CurrentUser|LocalMachine] [--json]

        Lists client certificates from the Windows store (subject, thumbprint, expiry,
        client-auth EKU). --filter substring-matches subject/issuer/thumbprint.
        --store LocalMachine searches the machine store in addition to your user store.

        Global: --debug (verbose diagnostics) and --log-file <path> work here too.

        Examples:
          certapi certs
          certapi certs --store LocalMachine
          certapi certs --json
        """;

    public static int Run(Args args, TextWriter stdout, TextWriter stderr, CliServices services)
    {
        string? filter = args.Value("--filter");
        string store = args.Value("--store") ?? services.Profile?.Store ?? "CurrentUser";
        bool json = args.Flag("--json");
        if (args.Positionals().Count > 0) throw new CliUsageException(Help);

        bool includeLocalMachine = store.Equals("LocalMachine", StringComparison.OrdinalIgnoreCase);
        if (!includeLocalMachine && !store.Equals("CurrentUser", StringComparison.OrdinalIgnoreCase))
            throw new CliUsageException("--store must be CurrentUser or LocalMachine.");

        var certs = services.ListCertificates(includeLocalMachine)
            .Where(c => filter is null ||
                        c.Subject.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                        c.Issuer.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                        c.Thumbprint.Contains(filter, StringComparison.OrdinalIgnoreCase))
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
                    w.WriteBoolean("expired", c.IsExpired());
                    w.WriteBoolean("clientAuthEku", c.HasClientAuthEku);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }));
            return ExitCodes.Ok;
        }

        foreach (var c in certs)
        {
            string flags = (c.IsExpired() ? "  [EXPIRED]" : "") + (c.HasClientAuthEku ? "" : "  (no client-auth EKU)");
            stdout.WriteLine($"{c.Subject}\n  {c.Thumbprint}  ·  expires {c.NotAfter:yyyy-MM-dd}{flags}");
        }
        return ExitCodes.Ok;
    }
}
