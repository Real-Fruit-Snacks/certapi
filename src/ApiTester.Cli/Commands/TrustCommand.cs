using System.Net.Http;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using ApiTester.Core;

namespace ApiTester.Cli.Commands;

/// <summary>Manages per-site trusted (pinned) server certificates: <c>send</c>/<c>run</c> already
/// consult <see cref="TrustService"/> automatically (a pinned host lets a request through without
/// the blanket <c>--insecure</c> bypass) — this command is how the pins themselves get listed,
/// added, and removed.</summary>
public static class TrustCommand
{
    public const string Help = """
        Usage: certapi trust list   [--workspace <file>] [--json]
               certapi trust add    <host> (--thumbprint <t> | --ca <t> | --spki <b64> | --from-url <https-url> [--ca]) [--workspace <file>]
               certapi trust remove <host> [--thumbprint <t>] [--workspace <file>]

        Lists, adds, or removes pinned server-certificate thumbprints for a host. A pinned host
        lets certapi send/run reach it without --insecure. Reads/writes the live GUI state by
        default, or a --workspace file.

        add:
          --thumbprint <t>         Pin this exact SHA-1 thumbprint for <host> (colons/spaces fine)
          --ca <t>                 Pin an ISSUER instead: any certificate above the leaf in the
                                   presented chain with this SHA-1 thumbprint is accepted. Survives
                                   renewals; the right pin for a TLS-intercepting proxy that
                                   re-mints a leaf per site (pin its CA once). `certapi doctor`
                                   prints every chain thumbprint
          --spki <base64>          Pin the leaf's PUBLIC KEY: base64 SHA-256 of its
                                   SubjectPublicKeyInfo (the HPKP form). Survives a renewal that
                                   keeps the key
          --from-url <https-url>  Connect once, capture the presented server certificate, and
                                   pin its thumbprint; with --ca (no value), pin its issuer
                                   instead; with --spki (no value), its public key

        remove:
          --thumbprint <t>        Remove only this pin; omit to remove every pin for <host>

        The certificate options below only matter for --from-url, in case <https-url> itself
        requires a client certificate (mTLS):
        """ + "\n" + CliCert.HelpLines + """

        """ + TransportFlags.StreamHelp + """
          (--from-url only. A proxy tunnels the TLS session, so the certificate can only be
          captured on a direct connection; --no-proxy or --noproxy <host> when the host is
          reachable without one.)

        Global: --debug (verbose diagnostics) and --log-file <path> work here too.

        Examples:
          certapi trust list
          certapi trust list --json
          certapi trust add internal.corp --thumbprint 0123456789ABCDEF0123456789ABCDEF01234567
          certapi trust add internal.corp --from-url https://internal.corp
          certapi trust remove internal.corp
          certapi trust remove internal.corp --thumbprint 0123456789ABCDEF0123456789ABCDEF01234567
        """;

    public static int Run(Args args, TextWriter stdout, TextWriter stderr, CliServices services)
    {
        string? workspace = args.Value("--workspace");
        bool json = args.Flag("--json");
        string? thumbprint = args.Value("--thumbprint");
        string? fromUrl = args.Value("--from-url");
        bool caFlag = args.OptionalValue(out var caThumbprint, "--ca");
        bool spkiFlag = args.OptionalValue(out var spkiValue, "--spki");
        string store = args.Value("--store") ?? services.Profile?.Store ?? "CurrentUser";
        // --from-url is a connection, so it takes the same proxy / bypass / --ca-file flags as
        // `send` — behind an explicit corporate proxy there is no other way to reach the host whose
        // certificate is being captured. Redirects, retries and version pins are not consumed:
        // the capture is one handshake.
        var fromUrlTransport = TransportFlags.ParseStreamSubset(args, ignoreServerCertificateErrors: true, environment: null, services.Profile);
        // Only --from-url ever needs a client certificate, but options are consumed here (before
        // Positionals()) for every subcommand, the same way CertsCommand/SendCommand always do.
        var cert = CliCert.Resolve(args, store, services, stderr);

        var positionals = args.Positionals();
        if (positionals.Count == 0) throw new CliUsageException(Help);
        string sub = positionals[0].ToLowerInvariant();

        var state = CliWorkspace.Load(workspace, services.LiveStatePath, stderr);
        string targetPath = workspace ?? services.LiveStatePath;

        return sub switch
        {
            "list" when positionals.Count == 1 => List(state, json, stdout, stderr),
            "add" when positionals.Count == 2 =>
                Add(state, positionals[1], thumbprint, fromUrl, cert, fromUrlTransport,
                    caFlag ? (caThumbprint ?? "") : null, spkiFlag ? (spkiValue ?? "") : null, workspace, targetPath, services, stderr),
            "remove" when positionals.Count == 2 =>
                Remove(state, positionals[1], thumbprint, workspace, targetPath, services, stderr),
            _ => throw new CliUsageException(Help)
        };
    }

    private static int List(AppState state, bool json, TextWriter stdout, TextWriter stderr)
    {
        if (state.TrustedServerCerts.Count == 0) stderr.WriteLine("No trusted server certificates.");

        if (json)
        {
            stdout.WriteLine(JsonSerializer.Serialize(state.TrustedServerCerts.Select(t => new
            {
                host = t.Host,
                thumbprint = t.Thumbprint,
                kind = t.EffectiveKind.ToString().ToLowerInvariant(),
                subject = t.Subject,
                addedUtc = t.AddedUtc
            }), new JsonSerializerOptions { WriteIndented = true }));
            return ExitCodes.Ok;
        }

        foreach (var t in state.TrustedServerCerts)
            stdout.WriteLine($"{t.Host}\n  {t.Thumbprint}  ·  {Describe(t.EffectiveKind)}  ·  {t.Subject ?? "—"}  ·  added {t.AddedUtc:yyyy-MM-dd}");
        return ExitCodes.Ok;
    }

    private static int Add(
        AppState state, string host, string? thumbprint, string? fromUrl, X509Certificate2? cert,
        TransportOptions fromUrlTransport, string? ca, string? spki, string? workspace, string targetPath,
        CliServices services, TextWriter stderr)
    {
        // The kind of pin: --ca / --spki with a value are standalone; with no value they qualify
        // --from-url. --thumbprint is the leaf, as it always was.
        var kind = PinKind.Leaf;
        if (ca is not null && spki is not null) throw new CliUsageException("--ca and --spki are different kinds of pin; use one.");
        if (ca is not null) { kind = PinKind.Issuer; if (ca.Length > 0) thumbprint = ca; }
        if (spki is not null) { kind = PinKind.Spki; if (spki.Length > 0) thumbprint = spki; }
        if ((ca?.Length > 0 || spki?.Length > 0) && (fromUrl is not null || args_thumbprint_given(thumbprint, ca, spki)))
            throw new CliUsageException("Give one pin: --thumbprint <t>, --ca <t>, --spki <b64>, or --from-url (optionally with a bare --ca or --spki).");
        if (thumbprint is null && fromUrl is null)
            throw new CliUsageException("trust add needs --thumbprint <t>, --ca <t>, --spki <b64>, or --from-url <https-url>.");

        // A pin is looked up by the URL's host alone, so that is what is stored: a scheme, a port
        // or a path typed here would be kept verbatim and then never match anything.
        host = NormalizeHost(host, out var dropped);
        if (dropped is not null) stderr.WriteLine($"note: pins are per host; {dropped} ignored, pinning '{host}'.");
        if (fromUrl is not null)
        {
            if (!Uri.TryCreate(fromUrl, UriKind.Absolute, out var fromUri) || fromUri.Scheme != Uri.UriSchemeHttps)
                throw new CliUsageException($"--from-url expects an https:// URL, got '{fromUrl}'.");
            if (!fromUri.Host.Equals(host, StringComparison.OrdinalIgnoreCase))
                throw new CliUsageException(
                    $"--from-url connects to '{fromUri.Host}' but the pin is for '{host}'. A certificate seen at one host must not be trusted for another.");
        }

        if (workspace is null && services.IsGuiRunning())
            throw new CliDataException(
                "The GUI is running and would overwrite this change when it closes — close the app, or write into a --workspace file.");

        string finalThumbprint;
        string? subject = null;
        if (thumbprint is not null)
        {
            // The same spelling X509Certificate2.Thumbprint has — 40 upper-case hex digits — however
            // it was pasted: with colons, spaces, or lower-case. Anything else cannot ever match a
            // certificate and is refused instead of being stored as a pin that does nothing. An
            // SPKI pin is base64 of 32 bytes and is checked the same way.
            finalThumbprint = kind == PinKind.Spki
                ? NormalizeSpki(thumbprint) ?? throw new CliUsageException($"--spki expects base64 of a SHA-256 (44 characters), got '{thumbprint}'.")
                : NormalizeThumbprint(thumbprint)
                  ?? throw new CliUsageException(
                      $"{(kind == PinKind.Issuer ? "--ca" : "--thumbprint")} expects a SHA-1 thumbprint (40 hex digits, separators allowed), got '{thumbprint}'.");
        }
        else
        {
            // --from-url: connect once purely to observe the presented server certificate. The
            // blanket IgnoreServerCertificateErrors is deliberate here — it only forces the
            // handshake to complete so the cert is observable, and is never itself stored as trust.
            var request = new ApiRequest { Method = HttpMethod.Get, Url = fromUrl! };
            // Through a proxy there is no handshake this client can see, so the capture needs the
            // direct path; a proxy named on the command line or in the profile is still honoured
            // when the user asked for one, and the resulting "no certificate observed" says why.
            var transport = fromUrlTransport with { IgnoreServerCertificateErrors = true, FollowRedirects = false };
            var response = services.Client.SendAsync(
                request, cert, transport: transport, cancellationToken: services.Cancel).GetAwaiter().GetResult();

            if (response.Connection?.ServerCertificateThumbprint is not { } thumb)
            {
                stderr.WriteLine(response.Error?.Message ??
                    (response.Connection?.ViaProxy == true
                        ? $"Connected to {fromUrl} through a proxy, which tunnels the TLS session: the server certificate cannot be observed from here. Use --no-proxy or --noproxy {host} if the host is reachable directly, or pin with --thumbprint."
                        : $"Could not connect to {fromUrl}."));
                foreach (var d in response.Error?.Detail ?? Array.Empty<ErrorDetail>())
                    stderr.WriteLine($"  caused by: {d.ExceptionType}: {d.Message}"
                        + (d.NativeErrorCode is { } native ? $" (native error {native})" : ""));
                return ExitCodes.Failure;
            }

            switch (kind)
            {
                case PinKind.Issuer:
                    // The issuer directly above the leaf, which the connection reports by thumbprint.
                    if (response.Connection.ServerCertificateIssuerThumbprint is not { } issuerThumb)
                        return Refuse(stderr, $"The chain {fromUrl} presented has no issuer above the leaf to pin (a self-signed certificate); pin it with --thumbprint instead.");
                    finalThumbprint = issuerThumb;
                    subject = response.Connection.ServerCertificateIssuer;
                    break;
                case PinKind.Spki:
                    if (response.Connection.ServerCertificateSpkiSha256 is not { } spkiHash)
                        return Refuse(stderr, $"Could not read the public key of the certificate {fromUrl} presented.");
                    finalThumbprint = spkiHash;
                    subject = response.Connection.ServerCertificateSubject;
                    break;
                default:
                    finalThumbprint = thumb;
                    subject = response.Connection.ServerCertificateSubject;
                    break;
            }
            stderr.WriteLine($"Captured {Describe(kind)} for {host}: {finalThumbprint}  ·  {subject ?? "—"}");
        }

        TrustService.Trust(state, host, finalThumbprint, subject, kind);
        CliWorkspace.ReportSaveResult(state.SaveTo(targetPath), targetPath, stderr);
        stderr.WriteLine($"Trusted {Describe(kind)} {finalThumbprint} for {host}.");
        return ExitCodes.Ok;
    }

    private static int Refuse(TextWriter stderr, string message)
    {
        stderr.WriteLine(message);
        return ExitCodes.Failure;
    }

    private static string Describe(PinKind kind) => kind switch
    {
        PinKind.Issuer => "issuer",
        PinKind.Spki => "public key",
        _ => "certificate"
    };

    private static bool args_thumbprint_given(string? thumbprint, string? ca, string? spki) =>
        thumbprint is not null && thumbprint != ca && thumbprint != spki;

    /// <summary>Base64 of exactly 32 bytes, re-encoded canonically — or null.</summary>
    internal static string? NormalizeSpki(string raw)
    {
        try
        {
            var bytes = Convert.FromBase64String(raw.Trim());
            return bytes.Length == 32 ? Convert.ToBase64String(bytes) : null;
        }
        catch (FormatException) { return null; }
    }

    /// <summary>40 hex digits, upper-case, separators removed — or null when it is not a SHA-1
    /// thumbprint at all (a SHA-256 fingerprint is 64 digits and is refused here on purpose).</summary>
    internal static string? NormalizeThumbprint(string raw)
    {
        var digits = new string(raw.Where(c => !char.IsWhiteSpace(c) && c != ':' && c != '-').ToArray());
        if (digits.Length != 40 || !digits.All(Uri.IsHexDigit)) return null;
        return digits.ToUpperInvariant();
    }

    /// <summary>The bare host out of whatever was typed: "https://internal.corp:8443/x" → internal.corp,
    /// with <paramref name="dropped"/> naming what was ignored, so the user learns pins are per host.</summary>
    internal static string NormalizeHost(string raw, out string? dropped)
    {
        dropped = null;
        string value = raw.Trim();
        // A bare IPv6 literal has colons of its own, which the port-splitting below would read as a
        // port; recognised first and stored without brackets, the way pins are looked up.
        if (System.Net.IPAddress.TryParse(value.TrimStart('[').TrimEnd(']'), out var ip) &&
            ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
            return TrustService.HostKey(value);
        if (value.Contains("://", StringComparison.Ordinal) && Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            dropped = "the scheme" + (uri.IsDefaultPort ? "" : ", port") + (uri.PathAndQuery.Length > 1 ? " and path" : "");
            return TrustService.HostKey(uri.Host);
        }
        if (Uri.TryCreate("https://" + value, UriKind.Absolute, out var bare) && bare.Host.Length > 0)
        {
            var parts = new List<string>();
            if (!bare.IsDefaultPort) parts.Add("port " + bare.Port);
            if (bare.PathAndQuery.Length > 1) parts.Add("the path");
            if (parts.Count > 0) dropped = string.Join(" and ", parts);
            return TrustService.HostKey(bare.Host);
        }
        return value.ToLowerInvariant();
    }

    private static int Remove(
        AppState state, string host, string? thumbprint, string? workspace, string targetPath,
        CliServices services, TextWriter stderr)
    {
        host = NormalizeHost(host, out _);
        if (thumbprint is not null) thumbprint = NormalizeThumbprint(thumbprint) ?? thumbprint;
        if (workspace is null && services.IsGuiRunning())
            throw new CliDataException(
                "The GUI is running and would overwrite this change when it closes — close the app, or write into a --workspace file.");

        int before = state.TrustedServerCerts.Count;
        TrustService.Untrust(state, host, thumbprint);
        int removed = before - state.TrustedServerCerts.Count;
        CliWorkspace.ReportSaveResult(state.SaveTo(targetPath), targetPath, stderr);
        stderr.WriteLine($"Removed {removed} pin(s) for {host}.");
        return ExitCodes.Ok;
    }
}
