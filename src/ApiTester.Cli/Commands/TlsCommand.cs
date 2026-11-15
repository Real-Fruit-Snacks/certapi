using System.Text.Json;
using ApiTester.Core;

namespace ApiTester.Cli.Commands;

/// <summary>`certapi tls host[:port]`: a TLS handshake with any service, and what it showed.</summary>
public static class TlsCommand
{
    public const string Help = """
        Usage: certapi tls <host[:port] | url> [options]

        Handshakes with any TLS service — HTTPS, LDAPS, SMTPS, IMAPS, a database, a message broker —
        and reports what it saw: the protocol and cipher agreed, ALPN, the server's certificate chain
        (names, validity, key, the SHA-256 that --pinnedpubkey takes), whether the chain is trusted,
        and whether the server asked for a client certificate — with the list of issuers it will
        accept, which is the answer to "which of my certificates will this server take?". With a
        certificate, whether it was sent, and whether the server then refused it: a server judges
        the client's certificate only after the client has finished its side of the handshake, so
        a refusal shows as the connection being ended just after it. Nothing is sent after the
        handshake.

          --starttls <proto>      Upgrade a plain connection first: smtp, imap, pop3, ftp, ldap or
                                  postgres (the port defaults to that protocol's plain port)
          --alpn <a,b>            Offer these application protocols (e.g. h2,http/1.1)
          --sni <name>            Send this server name instead of the host
          --timeout <seconds>     Give up after this long (default 15)
          --json                  The result as JSON

          --cert <thumb|subject>  Offer this client certificate (and the other certificate
                                  options: --cert-file, --cert-password, --key-file, --cert-issuer,
                                  --cert-serial, --cert-pin, --store, and curl's -E <file[:password]>)
          --connect-to / --interface / --resolve / -4 / -6 / --tls-min / --ca-file
                                  As on send. A proxy is never used: this is not HTTP

        Global: --debug (verbose diagnostics) and --log-file <path> work here too.

        Examples:
          certapi tls api.example.com
          certapi tls ldap.corp:636 --cert "CN=svc-ldap"
          certapi tls mail.corp --starttls smtp
          certapi tls https://api.example.com/v1 --alpn h2,http/1.1 --json

        Exit 0 when the handshake completes, 1 when it fails or the certificate offered is refused
        (the report still says how far it got).
        """;

    public static int Run(Args args, TextWriter stdout, TextWriter stderr, CliServices services)
    {
        string? startRaw = args.Value("--starttls");
        string? alpnRaw = args.ListValue("--alpn");
        string? sni = args.Value("--sni");
        string? timeoutRaw = args.Value("--timeout");
        bool json = args.Flag("--json");
        string store = args.Value("--store") ?? services.Profile?.Store ?? "CurrentUser";
        var overrides = TransportFlags.Parse(args, out _, environment: null, services.Profile);
        var cert = CliCert.Resolve(args, store, services, stderr);
        var positionals = args.Positionals();
        if (positionals.Count != 1) throw new CliUsageException(Help);

        var startTls = startRaw?.ToLowerInvariant() switch
        {
            null => StartTls.None,
            "smtp" => StartTls.Smtp, "imap" => StartTls.Imap, "pop3" => StartTls.Pop3,
            "ftp" => StartTls.Ftp, "ldap" => StartTls.Ldap, "postgres" or "postgresql" => StartTls.Postgres,
            var other => throw new CliUsageException($"--starttls is smtp, imap, pop3, ftp, ldap or postgres, got '{other}'.")
        };
        int timeout = 15;
        if (timeoutRaw is not null && (!int.TryParse(timeoutRaw, out timeout) || timeout <= 0))
            throw new CliUsageException($"--timeout expects a positive number of seconds, got '{timeoutRaw}'.");
        var (host, port) = Target(positionals[0], startTls);
        var alpn = alpnRaw?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList() ?? new List<string>();
        var transport = overrides.ApplyTo(new TransportOptions());

        var result = TlsProbe.ProbeAsync(host, port, startTls, sni, cert, alpn, transport, TimeSpan.FromSeconds(timeout), services.Cancel)
            .GetAwaiter().GetResult();

        if (json) stdout.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        else Print(result, cert is not null, stdout);
        if (result.Error is not null && !json) stderr.WriteLine("error: " + result.Error);
        // A certificate offered and refused is the failure an mTLS check is run to catch; a server
        // that requires one when none was offered is what the report is for, not a failure.
        bool offeredAndRefused = cert is not null && result.RefusedAfterHandshake is not null;
        return result.Error is null && !offeredAndRefused ? ExitCodes.Ok : ExitCodes.Failure;
    }

    /// <summary>host, host:port, [v6]:port or a URL; the port defaults by scheme or STARTTLS protocol.</summary>
    internal static (string Host, int Port) Target(string raw, StartTls startTls)
    {
        int defaultPort = startTls switch
        {
            StartTls.Smtp => 587, StartTls.Imap => 143, StartTls.Pop3 => 110, StartTls.Ftp => 21,
            StartTls.Ldap => 389, StartTls.Postgres => 5432, _ => 443
        };
        if (raw.Contains("://") && Uri.TryCreate(raw, UriKind.Absolute, out var uri))
            return (uri.IdnHost, uri.IsDefaultPort && uri.Scheme is not ("https" or "http") ? defaultPort : uri.Port);
        if (Uri.TryCreate("tls://" + raw, UriKind.Absolute, out var hostPort) && hostPort.Host.Length > 0)
            return (hostPort.IdnHost.Trim('[', ']'), hostPort.Port > 0 ? hostPort.Port : defaultPort);
        throw new CliUsageException($"'{raw}' is not a host, host:port or URL.");
    }

    private static void Print(TlsProbeResult r, bool offered, TextWriter o)
    {
        o.WriteLine($"{r.Host}:{r.Port}{(r.RemoteAddress is null ? "" : $" ({r.RemoteAddress})")}  {r.Elapsed.TotalMilliseconds:0} ms");
        if (!r.Connected) return;
        if (r.Protocol is not null)
        {
            o.WriteLine($"  protocol   {r.Protocol}");
            o.WriteLine($"  cipher     {r.Cipher}");
            if (r.Alpn is not null) o.WriteLine($"  alpn       {r.Alpn}");
        }
        o.WriteLine($"  trusted    {(r.ServerTrusted ? "yes" : "NO — " + r.TrustProblem)}");
        for (int i = 0; i < r.Chain.Count; i++)
        {
            var c = r.Chain[i];
            o.WriteLine(i == 0 ? "  server certificate" : $"  issuer {i}");
            o.WriteLine($"    subject    {c.Subject}");
            if (i == 0 && c.SubjectAlternativeNames.Count > 0) o.WriteLine($"    names      {string.Join(", ", c.SubjectAlternativeNames)}");
            o.WriteLine($"    issuer     {c.Issuer}");
            o.WriteLine($"    valid      {c.NotBefore:yyyy-MM-dd} to {c.NotAfter:yyyy-MM-dd}{(c.NotAfter < DateTime.Now ? "  EXPIRED" : c.NotAfter < DateTime.Now.AddDays(30) ? "  expires soon" : "")}");
            o.WriteLine($"    key        {c.KeyAlgorithm} {c.KeySize}");
            if (i == 0) o.WriteLine($"    pin        sha256//{c.SpkiSha256}");
            o.WriteLine($"    thumbprint {c.Thumbprint}");
        }
        if (r.ClientCertificateRequested)
        {
            o.WriteLine("  client certificate requested" + (r.AcceptableIssuers.Count == 0 ? " (any issuer)" : ", issued by one of:"));
            foreach (var issuer in r.AcceptableIssuers) o.WriteLine($"    {issuer}");
            o.WriteLine(offered
                ? r.ClientCertificateSent is { } sent ? $"  sent       {sent}" : "  sent       nothing — the certificate offered does not match, or has no usable private key"
                : "  sent       nothing (give --cert to offer one)");
            if (r.RefusedAfterHandshake is { } why)
                o.WriteLine(r.ClientCertificateSent is null
                    ? $"  refused    {why} — it requires a client certificate"
                    : $"  refused    {why} — it did not accept the certificate sent");
            else if (r.ClientCertificateSent is not null)
                o.WriteLine("  kept       the server kept the connection open for half a second after the handshake: the certificate was not refused there");
        }
        else o.WriteLine("  client certificate not requested");
    }
}
