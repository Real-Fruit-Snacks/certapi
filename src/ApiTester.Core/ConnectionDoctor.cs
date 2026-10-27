using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace ApiTester.Core;

/// <summary>How one stage of the connection went. <see cref="Detail"/> lines are the evidence a
/// reader acts on; <see cref="Advice"/> is the one sentence worth saying out loud when the stage
/// failed or found something worth warning about.</summary>
public sealed record DoctorStage(
    string Name, bool Ok, string Summary, IReadOnlyList<string> Detail, string? Advice, TimeSpan Elapsed)
{
    public static DoctorStage Pass(string name, string summary, TimeSpan elapsed,
                                   IReadOnlyList<string>? detail = null, string? advice = null) =>
        new(name, true, summary, detail ?? Array.Empty<string>(), advice, elapsed);

    public static DoctorStage Fail(string name, string summary, TimeSpan elapsed,
                                   IReadOnlyList<string>? detail = null, string? advice = null) =>
        new(name, false, summary, detail ?? Array.Empty<string>(), advice, elapsed);
}

/// <summary>The whole triage: the stages that ran, in order, and the per-phase timings. Data only —
/// rendering (text or JSON) belongs to the caller.</summary>
public sealed record DoctorReport(string Url, IReadOnlyList<DoctorStage> Stages)
{
    /// <summary>True when every stage that ran passed.</summary>
    public bool Ok => Stages.All(s => s.Ok);

    /// <summary>The advice for a 401/403 from a server that never asked for the client certificate
    /// during the handshake. Servers that require a certificate only for some paths ask for it
    /// after the handshake instead — TLS renegotiation, which HTTP/2 forbids, or TLS 1.3
    /// post-handshake authentication, which SChannel does not do — so the request is refused while
    /// the same URL works in a browser that quietly fell back to HTTP/1.1 and TLS 1.2.</summary>
    /// <summary>Clock skew large enough to matter for certificate validity. Five minutes is the
    /// Kerberos tolerance, which is the number every corporate network already lives with.</summary>
    public static readonly TimeSpan ClockSkewTolerance = TimeSpan.FromMinutes(5);

    /// <summary>The note for a clock that disagrees with the server's <c>Date</c> header, or null
    /// when they agree within tolerance. Pure, so the wording and the threshold are testable without
    /// a connection. <paramref name="chainNotTimeValid"/> is whether the handshake reported the
    /// certificate as not yet valid or expired — on a skewed clock that is the symptom, not the
    /// cause, and the note says so.</summary>
    public static string? ClockSkewNote(DateTimeOffset local, DateTimeOffset serverDate, bool chainNotTimeValid)
    {
        var skew = local - serverDate;
        if (skew.Duration() <= ClockSkewTolerance) return null;
        string amount = skew.Duration() switch
        {
            { TotalDays: >= 1 } d => $"{d.TotalDays:0.#} days",
            { TotalHours: >= 1 } h => $"{h.TotalHours:0.#} hours",
            var m => $"{m.TotalMinutes:0} minutes"
        };
        string direction = skew > TimeSpan.Zero ? "ahead of" : "behind";
        string note = $"This machine's clock is {amount} {direction} the server's (its Date header says {serverDate:yyyy-MM-dd HH:mm:ss} UTC).";
        return chainNotTimeValid
            ? note + " The certificate was reported as not time-valid; with this much skew that is the clock, not the certificate. Fix the system time (w32tm /resync) and try again."
            : note + " Certificate validity, Kerberos and signed tokens all depend on it; expect failures until the clock is corrected.";
    }

    public const string PerPathCertificateAdvice =
        "The server did not ask for a client certificate during the handshake and then refused the request. " +
        "A server that requires the certificate only for some paths asks for it after the handshake (renegotiation), " +
        "which HTTP/2 forbids and TLS 1.3 replaces with post-handshake auth this client cannot do. " +
        "Try --http1.1 so the server can renegotiate over TLS 1.2, or ask for the certificate to be required at the connection level.";

    /// <summary>The first stage that failed — what the reader should act on.</summary>
    public DoctorStage? FirstFailure => Stages.FirstOrDefault(s => !s.Ok);

    /// <summary>Certificates in <paramref name="candidates"/> whose issuer matches one of the
    /// distinguished names the server said it accepts. Pure, so the matching rule is testable
    /// without a handshake: comparison is on the issuer's distinguished name, whitespace-normalized
    /// and case-insensitive, because a server and a store rarely spell one identically.</summary>
    public static IReadOnlyList<X509Certificate2> MatchIssuers(
        IEnumerable<X509Certificate2> candidates, IEnumerable<string> acceptableIssuers)
    {
        var wanted = acceptableIssuers.Select(Normalize).Where(s => s.Length > 0).ToHashSet();
        if (wanted.Count == 0) return Array.Empty<X509Certificate2>();
        return candidates.Where(c => wanted.Contains(Normalize(c.Issuer))).ToList();
    }

    private static string Normalize(string distinguishedName)
    {
        var sb = new StringBuilder(distinguishedName.Length);
        bool lastWasSpace = false;
        foreach (char c in distinguishedName.Trim())
        {
            // Separator and case differences are noise here: "CN=A, O=B" and "cn=A,o=B" name the
            // same issuer, and a server's spelling of its own CA list is not something to police.
            if (c is ' ' or '\t') { lastWasSpace = true; continue; }
            if (lastWasSpace && sb.Length > 0 && sb[^1] != ',') sb.Append(' ');
            lastWasSpace = false;
            sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    /// <summary>Subject fragments of certificate authorities that belong to well-known TLS
    /// inspection products. A match is worth SAYING, never worth asserting: an organization may
    /// legitimately run a CA whose name resembles one of these.</summary>
    internal static readonly string[] InspectionVendors =
    {
        "zscaler", "netskope", "blue coat", "bluecoat", "palo alto", "fortinet", "fortigate",
        "forcepoint", "websense", "mcafee web gateway", "sophos", "cisco umbrella", "proxysg",
        "checkpoint", "check point", "trend micro", "barracuda", "sslinspect", "ssl inspection"
    };

    /// <summary>The note to print when a chain looks like it was issued by an interception
    /// appliance rather than the public web, or null when nothing about it stands out. Pure so the
    /// heuristics can be exercised as data.</summary>
    public static string? InterceptionNote(string rootSubject, bool rootIsLocallyTrusted)
    {
        string subject = rootSubject.ToLowerInvariant();
        string? vendor = InspectionVendors.FirstOrDefault(v => subject.Contains(v, StringComparison.Ordinal));
        if (vendor is not null)
            return $"The chain's root ({rootSubject}) matches a known TLS-inspection product. " +
                   "Traffic to this host is being decrypted and re-signed in the middle, which is " +
                   "why a client certificate cannot reach the server through it.";
        if (rootIsLocallyTrusted)
            return $"The chain's root ({rootSubject}) is trusted by this machine but is not a public " +
                   "certificate authority — consistent with SSL inspection on this network. A client " +
                   "certificate cannot traverse an intercepting proxy.";
        return null;
    }
}

/// <summary>Runs the connection this product would make, one stage at a time, and reports where it
/// broke and what was seen. Deliberately NOT built on <see cref="ApiClient"/>: it owns the socket
/// and the <see cref="SslStream"/> so it can report what a pooled, handler-managed request cannot —
/// the acceptable client-certificate authority list the server sends, the full chain as presented,
/// and honest per-phase timings.</summary>
public static class ConnectionDoctor
{
    /// <summary>Windows' own network-connectivity probe: a tiny endpoint that answers exactly
    /// "Microsoft Connect Test" over plain HTTP. Anything else answering is a captive portal.</summary>
    private const string NcsiUrl = "http://www.msftconnecttest.com/connecttest.txt";
    private const string NcsiExpected = "Microsoft Connect Test";

    public static async Task<DoctorReport> RunAsync(
        string url,
        X509Certificate2? clientCertificate,
        IReadOnlyList<X509Certificate2> storeCertificates,
        TransportOptions options,
        Func<string, Task<string?>>? probe = null,
        CancellationToken ct = default)
    {
        var stages = new List<DoctorStage>();
        var clock = Stopwatch.StartNew();

        // ---- stage 1: the URL itself ---------------------------------------------------------
        var start = clock.Elapsed;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            stages.Add(DoctorStage.Fail("url", $"'{url}' is not an absolute http(s) URL", clock.Elapsed - start,
                advice: "Write the whole address, scheme included: https://host/path"));
            return new DoctorReport(url, stages);
        }
        stages.Add(DoctorStage.Pass("url", $"{uri.Scheme}://{uri.Host}:{uri.Port}{uri.PathAndQuery}", clock.Elapsed - start));

        // ---- stage 2: the proxy decision -----------------------------------------------------
        // Decided BEFORE DNS on purpose: through a proxy the client never resolves the target at
        // all — the proxy does. Resolving it here anyway would fail an internal hostname that only
        // the proxy can see, and blame DNS for a connection that would have worked.
        start = clock.Elapsed;
        var (proxyUri, proxyWhy) = DecideProxy(uri, options);
        if (proxyUri is not null && proxyUri.Scheme.StartsWith("socks", StringComparison.OrdinalIgnoreCase))
        {
            // The staged probe below speaks HTTP CONNECT; a SOCKS proxy would read that as garbage
            // and the report would blame the proxy for a refusal that never happened. `send`
            // itself speaks SOCKS fine, so point there rather than pretend.
            stages.Add(DoctorStage.Fail("proxy",
                $"{MarkdownSecrets.RedactUrl(proxyUri.ToString())} ({proxyWhy}) is a SOCKS proxy, which doctor's staged probe cannot speak",
                clock.Elapsed - start,
                advice: "Run the request with `certapi send <url> --proxy socks5://… --debug --trace` for the SOCKS path, or `certapi doctor` with --no-proxy from a network that reaches the host directly."));
            return new DoctorReport(url, stages);
        }
        // Redacted, because a proxy URL may carry credentials — `--proxy http://svc:pw@proxy:8080`
        // is ordinary — and this line is printed to the terminal, serialised into --json, and
        // written into the markdown note that `--md-vault` files into a folder that syncs.
        stages.Add(DoctorStage.Pass("proxy", proxyUri is null
                ? $"DIRECT ({proxyWhy})"
                : $"{MarkdownSecrets.RedactUrl(proxyUri.ToString())} ({proxyWhy})",
            clock.Elapsed - start));

        // ---- stage 3: DNS, for whichever host is actually dialled ----------------------------
        var target = proxyUri is null
            ? (Host: uri.Host, Port: uri.Port)
            : (Host: proxyUri.Host, Port: proxyUri.Port);
        string dnsWhat = proxyUri is null ? target.Host : $"{target.Host} (the proxy; the target is resolved by it, not here)";

        start = clock.Elapsed;
        if (IPAddress.TryParse(target.Host, out _))
        {
            stages.Add(DoctorStage.Pass("dns", $"{dnsWhat} is an address literal — no lookup needed",
                clock.Elapsed - start));
        }
        else
        {
            try
            {
                var addresses = await Dns.GetHostAddressesAsync(target.Host, ct);
                if (addresses.Length == 0) throw new SocketException((int)SocketError.HostNotFound);
                stages.Add(DoctorStage.Pass("dns", $"{dnsWhat} → {addresses.Length} address{(addresses.Length == 1 ? "" : "es")}",
                    clock.Elapsed - start, addresses.Select(a => a.ToString()).ToList()));
            }
            catch (Exception ex)
            {
                stages.Add(DoctorStage.Fail("dns", $"{target.Host} did not resolve: {ex.Message}", clock.Elapsed - start,
                    advice: proxyUri is not null
                        ? "The PROXY's own hostname does not resolve — check the proxy address or the PAC script that named it."
                        : await AdviseOnReachabilityAsync(options.Offline ? OfflineProbe : probe, ct)));
                return new DoctorReport(url, stages);
            }
        }

        // ---- stage 4: TCP --------------------------------------------------------------------
        start = clock.Elapsed;
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        // --connect-timeout bounds this stage as it bounds a send's connect: on a network that
        // blackholes the host, the OS's own timeout is tens of seconds to minutes.
        using var connectBudget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (options.ConnectTimeout is { } connectTimeout) connectBudget.CancelAfter(connectTimeout);
        try
        {
            await socket.ConnectAsync(target.Host, target.Port, connectBudget.Token);
            stages.Add(DoctorStage.Pass("tcp", $"connected to {target.Host}:{target.Port}", clock.Elapsed - start));
        }
        catch (Exception ex)
        {
            socket.Dispose();
            string why = ex is OperationCanceledException && !ct.IsCancellationRequested && options.ConnectTimeout is { } t
                ? $"no answer within {t.TotalSeconds:0.#} s (--connect-timeout)"
                : ex.Message;
            stages.Add(DoctorStage.Fail("tcp", $"could not connect to {target.Host}:{target.Port} — {why}",
                clock.Elapsed - start,
                advice: proxyUri is not null
                    ? "The PROXY is what refused the connection, not the target host. Check the proxy address, or try --no-proxy."
                    : await AdviseOnReachabilityAsync(options.Offline ? OfflineProbe : probe, ct)));
            return new DoctorReport(url, stages);
        }

        try
        {
            Stream stream = new NetworkStream(socket, ownsSocket: true);

            // ---- stage 5: CONNECT through the proxy, when there is one -----------------------
            if (proxyUri is not null && uri.Scheme == Uri.UriSchemeHttps)
            {
                start = clock.Elapsed;
                var (ok, summary, detail, advice, upgraded) =
                    await TunnelAsync(stream, uri, proxyUri, options, ct);
                stages.Add(ok
                    ? DoctorStage.Pass("connect", summary, clock.Elapsed - start, detail)
                    : DoctorStage.Fail("connect", summary, clock.Elapsed - start, detail, advice));
                if (!ok) { stream.Dispose(); return new DoctorReport(url, stages); }
                stream = upgraded!;
            }

            // ---- stage 6: TLS ----------------------------------------------------------------
            // Hoisted out of the TLS block: the HTTP stage needs to know whether the server ever
            // asked for a client certificate, which is the fact that explains a per-path refusal.
            string[] acceptableIssuers = Array.Empty<string>();
            bool handshakeAskedForCertificate = false;
            bool seenChainNotTimeValid = false;
            if (uri.Scheme == Uri.UriSchemeHttps)
            {
                start = clock.Elapsed;
                var ssl = new SslStream(stream, leaveInnerStreamOpen: false);
                var detail = new List<string>();
                acceptableIssuers = Array.Empty<string>();
                X509Certificate2? presented = null;
                // A snapshot, not the chain object: SslStream disposes the X509Chain it handed to
                // the callback as soon as the callback returns, so reading ChainElements afterwards
                // finds nothing — and the chain lines and the interception note silently vanish.
                ChainSnapshot? seenChain = null;
                SslPolicyErrors seenErrors = SslPolicyErrors.None;

                var sslOptions = new SslClientAuthenticationOptions
                {
                    TargetHost = uri.Host,
                    RemoteCertificateValidationCallback = (_, _, chain, errors) =>
                    {
                        // Everything is recorded and nothing is refused: the point is to REPORT
                        // what a real connection would have hit, so a failing chain must still get
                        // far enough to be described.
                        seenChain = ChainSnapshot.Of(chain); seenErrors = errors;
                        return true;
                    },
                    LocalCertificateSelectionCallback = (_, _, _, _, issuers) =>
                    {
                        // The prize: the certificate-authority list the server actually asks for.
                        acceptableIssuers = issuers ?? Array.Empty<string>();
                        return clientCertificate!;
                    }
                };
                if (clientCertificate is not null)
                    sslOptions.ClientCertificates = new X509CertificateCollection { clientCertificate };
                RevocationCheck.Apply(sslOptions, options);
                // --tls-min and --sni, as a real send would use them: a doctor that negotiated
                // TLS 1.2 under --tls-min 1.3 would report a success the send never has.
                TlsPolicy.Apply(sslOptions, options);

                try
                {
                    await ssl.AuthenticateAsClientAsync(sslOptions, ct);
                    presented = ssl.RemoteCertificate is { } rc ? new X509Certificate2(rc) : null;
                    detail.Add($"protocol: {ssl.SslProtocol}");
                    detail.Add($"cipher: {ssl.NegotiatedCipherSuite}");
                    if (presented is not null)
                        detail.Add($"server certificate: {presented.Subject} (issued by {presented.Issuer}, expires {presented.NotAfter:yyyy-MM-dd})");
                    detail.AddRange(DescribeChain(seenChain));
                    // The selection callback can be invoked speculatively on some platforms, so
                    // "did the server take a certificate" is read from the finished handshake.
                    handshakeAskedForCertificate = ssl.LocalCertificate is not null;
                    detail.Add(ssl.LocalCertificate is not null
                        ? $"client certificate presented: {ssl.LocalCertificate.Subject}"
                        : "client certificate presented: none");
                    detail.AddRange(DescribeIssuerRequest(acceptableIssuers, storeCertificates, clientCertificate));

                    string? note = InterceptionNoteFor(seenChain);
                    seenChainNotTimeValid = seenChain?.NotTimeValid == true;
                    string summary = seenErrors == SslPolicyErrors.None
                        ? $"handshake succeeded ({ssl.SslProtocol})"
                        : $"handshake succeeded, but the certificate has problems: {seenErrors}";
                    stages.Add(DoctorStage.Pass("tls", summary, clock.Elapsed - start, detail, note));
                    stream = ssl;
                }
                catch (Exception ex)
                {
                    detail.AddRange(DescribeIssuerRequest(acceptableIssuers, storeCertificates, clientCertificate));
                    detail.AddRange(DescribeChain(seenChain));
                    stages.Add(DoctorStage.Fail("tls", "handshake failed — " + ex.Message, clock.Elapsed - start,
                        detail, AdviseOnHandshake(acceptableIssuers, storeCertificates, clientCertificate)));
                    ssl.Dispose();
                    return new DoctorReport(url, stages);
                }
            }

            // ---- stage 7: HTTP ---------------------------------------------------------------
            start = clock.Elapsed;
            try
            {
                var (status, line, serverDate) = await GetAsync(stream, uri, viaProxy: proxyUri is not null && uri.Scheme == Uri.UriSchemeHttp, ct);
                // The server's own clock, read from the response: the one way to tell a skewed
                // local clock from a genuinely expired certificate.
                string? skewNote = serverDate is { } date ? DoctorReport.ClockSkewNote(DateTimeOffset.UtcNow, date, seenChainNotTimeValid) : null;
                var httpDetail = skewNote is null ? null : new List<string> { skewNote };
                stages.Add(status is >= 200 and < 400
                    ? DoctorStage.Pass("http", line, clock.Elapsed - start, httpDetail, advice: skewNote)
                    : DoctorStage.Pass("http", line, clock.Elapsed - start, httpDetail,
                        advice: status == 407
                            ? "The proxy wants credentials — try --proxy-user, or check that Windows integrated authentication applies."
                            : status is 401 or 403
                                ? (clientCertificate is not null && uri.Scheme == Uri.UriSchemeHttps && !handshakeAskedForCertificate
                                    ? DoctorReport.PerPathCertificateAdvice
                                    : "The connection is fine; the server refused the request itself. Check the client certificate and any token.")
                                : skewNote));
            }
            catch (Exception ex)
            {
                stages.Add(DoctorStage.Fail("http", "request failed — " + ex.Message, clock.Elapsed - start));
            }
            finally { stream.Dispose(); }
        }
        catch (Exception ex)
        {
            stages.Add(DoctorStage.Fail("connection", ex.Message, TimeSpan.Zero));
        }

        return new DoctorReport(url, stages);
    }

    /// <summary>Which proxy this URL would go through, and why — the same three inputs
    /// <see cref="ProxyConfiguration"/> honors, reported rather than applied.</summary>
    private static (Uri? Proxy, string Why) DecideProxy(Uri uri, TransportOptions options)
    {
        if (options.Proxy == ProxyMode.None) return (null, "--no-proxy");
        if (ProxyBypass.Match(options.NoProxy, uri) is { } rule)
            return (null, $"bypassed by '{rule.Text}'");
        if (options.Proxy == ProxyMode.Explicit && options.ProxyUrl is { Length: > 0 } explicitUrl)
            return (Uri.TryCreate(explicitUrl, UriKind.Absolute, out var p) ? p : null, "--proxy");
        try
        {
            // WinHTTP evaluates WPAD/PAC behind DefaultProxy, so this is the machine's real answer.
            var system = HttpClient.DefaultProxy.GetProxy(uri);
            return system is null ? (null, "system proxy says direct") : (system, "system proxy (including any PAC/WPAD)");
        }
        catch (Exception ex) { return (null, "system proxy could not be evaluated: " + ex.Message); }
    }

    private static async Task<(bool Ok, string Summary, List<string> Detail, string? Advice, Stream? Stream)>
        TunnelAsync(Stream stream, Uri uri, Uri proxy, TransportOptions options, CancellationToken ct)
    {
        var detail = new List<string>();
        string request = $"CONNECT {uri.Host}:{uri.Port} HTTP/1.1\r\nHost: {uri.Host}:{uri.Port}\r\n";
        if (options.ProxyUser is { Length: > 0 })
        {
            string basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{options.ProxyUser}:{options.ProxyPassword}"));
            request += $"Proxy-Authorization: Basic {basic}\r\n";
        }
        request += "\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request), ct);

        string head = await ReadHeadAsync(stream, ct);
        string first = head.Split('\r', '\n').FirstOrDefault() ?? "";
        detail.Add(first);
        if (first.Contains(" 200", StringComparison.Ordinal))
            return (true, $"tunnel established through {proxy.Host}:{proxy.Port}", detail, null, stream);

        var offered = new List<string>();
        foreach (var line in head.Split("\r\n"))
            if (line.StartsWith("Proxy-Authenticate:", StringComparison.OrdinalIgnoreCase))
            {
                detail.Add(line.Trim());
                offered.Add(line["Proxy-Authenticate:".Length..].Trim());
            }

        bool needsAuth = first.Contains(" 407", StringComparison.Ordinal);
        // What `send` does when no --proxy-user was given: answer Negotiate/NTLM with the
        // signed-in account. Doctor used to stop here with Basic-only knowledge and blame a proxy
        // that `send` had just succeeded against.
        if (needsAuth && options.ProxyUser is not { Length: > 0 })
        {
            string? package = offered.Any(o => o.StartsWith("Negotiate", StringComparison.OrdinalIgnoreCase)) ? "Negotiate"
                            : offered.Any(o => o.StartsWith("NTLM", StringComparison.OrdinalIgnoreCase)) ? "NTLM" : null;
            if (package is not null)
            {
                await DrainBodyAsync(stream, head, ct);
                var (ok, why) = await NegotiateTunnelAsync(stream, uri, proxy, package, detail, ct);
                if (ok) return (true, $"tunnel established through {proxy.Host}:{proxy.Port} ({package}, signed-in account)", detail, null, stream);
                return (false, $"the proxy refused the tunnel after {package} authentication", detail,
                    why ?? $"The proxy offers {package} but did not accept the signed-in account. Check that this machine has a Kerberos ticket (klist) or that the proxy accepts NTLM; --proxy-user bypasses both.",
                    null);
            }
        }
        return (false, $"the proxy refused the tunnel: {first}", detail,
            needsAuth
                ? "The proxy requires authentication. The schemes it offers are listed above — supply --proxy-user, or use a proxy that accepts your Windows credentials."
                : "The proxy is reachable but would not connect to this host. Check the proxy's own allow rules.",
            null);
    }

    /// <summary>Run the Negotiate/NTLM exchange on the same connection — NTLM is connection-bound —
    /// up to the few rounds the protocols take. Returns why it could not run when the security
    /// package itself refused (no credentials here, no GSSAPI on this platform), so the advice
    /// names that instead of the proxy.</summary>
    private static async Task<(bool Ok, string? Why)> NegotiateTunnelAsync(
        Stream stream, Uri uri, Uri proxy, string package, List<string> detail, CancellationToken ct)
    {
        System.Net.Security.NegotiateAuthentication auth;
        try
        {
            auth = new System.Net.Security.NegotiateAuthentication(new System.Net.Security.NegotiateAuthenticationClientOptions
            {
                Package = package,
                TargetName = "HTTP/" + proxy.Host,
                Credential = CredentialCache.DefaultNetworkCredentials
            });
        }
        catch (Exception ex)
        {
            return (false, $"Windows credentials could not be used for {package} here: {ex.Message}");
        }
        using (auth)
        {
            byte[]? incoming = null;
            for (int round = 0; round < 4; round++)
            {
                byte[]? outgoing;
                System.Net.Security.NegotiateAuthenticationStatusCode status;
                try { outgoing = auth.GetOutgoingBlob(incoming, out status); }
                catch (Exception ex) { return (false, $"Windows credentials could not be used for {package} here: {ex.Message}"); }
                if (status is not (System.Net.Security.NegotiateAuthenticationStatusCode.Completed or System.Net.Security.NegotiateAuthenticationStatusCode.ContinueNeeded))
                    return (false, $"The {package} security package refused to produce a token ({status}) — no usable credential on this machine.");
                if (outgoing is null && status == System.Net.Security.NegotiateAuthenticationStatusCode.Completed) return (true, null);

                string request =
                    $"CONNECT {uri.Host}:{uri.Port} HTTP/1.1\r\nHost: {uri.Host}:{uri.Port}\r\n" +
                    $"Proxy-Authorization: {package} {Convert.ToBase64String(outgoing ?? Array.Empty<byte>())}\r\n" +
                    "Proxy-Connection: Keep-Alive\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(request), ct);
                string head = await ReadHeadAsync(stream, ct);
                string first = head.Split('\r', '\n').FirstOrDefault() ?? "";
                detail.Add($"{package} round {round + 1}: {first}");
                if (first.Contains(" 200", StringComparison.Ordinal)) return (true, null);
                if (!first.Contains(" 407", StringComparison.Ordinal)) return (false, null);

                string? challenge = head.Split("\r\n")
                    .Where(l => l.StartsWith("Proxy-Authenticate:", StringComparison.OrdinalIgnoreCase))
                    .Select(l => l["Proxy-Authenticate:".Length..].Trim())
                    .FirstOrDefault(v => v.StartsWith(package, StringComparison.OrdinalIgnoreCase) && v.Length > package.Length);
                if (challenge is null) return (false, null);   // a fresh 407 with no token: refused
                try { incoming = Convert.FromBase64String(challenge[package.Length..].Trim()); }
                catch (FormatException) { return (false, null); }
                await DrainBodyAsync(stream, head, ct);
            }
            return (false, null);
        }
    }

    /// <summary>Read past a 407's body so the next request on the same connection starts clean.</summary>
    private static async Task DrainBodyAsync(Stream stream, string head, CancellationToken ct)
    {
        int length = 0;
        foreach (var line in head.Split("\r\n"))
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                int.TryParse(line["Content-Length:".Length..].Trim(), out length);
        var buffer = new byte[4096];
        while (length > 0)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, length)), ct);
            if (n <= 0) break;
            length -= n;
        }
    }

    private static async Task<(int Status, string Line, DateTimeOffset? Date)> GetAsync(Stream stream, Uri uri, bool viaProxy, CancellationToken ct)
    {
        // Deliberately HTTP/1.1 with Connection: close — this is a diagnostic exchange on a socket
        // this class owns, not a pooled request, and closing keeps the socket's lifetime obvious.
        // Plain http through a forward proxy is written in absolute form, which is what a proxy
        // expects on that socket; origin form there is answered 400 by most of them.
        string target = viaProxy ? uri.AbsoluteUri : uri.PathAndQuery;
        string request =
            $"GET {target} HTTP/1.1\r\nHost: {uri.Authority}\r\n" +
            "User-Agent: certapi-doctor\r\nAccept: */*\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request), ct);
        string head = await ReadHeadAsync(stream, ct);
        string first = head.Split('\r', '\n').FirstOrDefault() ?? "";
        var parts = first.Split(' ');
        int status = parts.Length > 1 && int.TryParse(parts[1], out var s) ? s : 0;
        DateTimeOffset? date = null;
        foreach (var headerLine in head.Split("\r\n"))
            if (headerLine.StartsWith("Date:", StringComparison.OrdinalIgnoreCase) &&
                DateTimeOffset.TryParse(headerLine[5..].Trim(), System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed))
                date = parsed;
        return (status, first.Length > 0 ? first : "(no status line)", date);
    }

    private static async Task<string> ReadHeadAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new byte[1];
        var sb = new StringBuilder();
        // Byte at a time to the end of the headers: the body must stay unread on the wire, and a
        // diagnostic exchange is never large enough for this to matter.
        while (sb.Length < 64 * 1024)
        {
            int n = await stream.ReadAsync(buffer, ct);
            if (n == 0) break;
            sb.Append((char)buffer[0]);
            if (sb.Length >= 4 && sb[^4] == '\r' && sb[^3] == '\n' && sb[^2] == '\r' && sb[^1] == '\n') break;
        }
        return sb.ToString();
    }

    /// <summary>What the handshake's chain said, copied while it was still alive.</summary>
    private sealed record ChainSnapshot(IReadOnlyList<(string Subject, string Thumbprint, string Status)> Elements)
    {
        public bool NotTimeValid => Elements.Any(e => e.Status.Contains("NotTimeValid", StringComparison.Ordinal));

        public static ChainSnapshot? Of(X509Chain? chain)
        {
            if (chain is null) return null;
            try
            {
                var elements = new List<(string, string, string)>();
                foreach (var element in chain.ChainElements)
                    elements.Add((element.Certificate.Subject, element.Certificate.Thumbprint,
                        element.ChainElementStatus.Length == 0 ? "" : string.Join(", ", element.ChainElementStatus.Select(s => s.Status))));
                return new ChainSnapshot(elements);
            }
            catch { return null; }
        }
    }

    private static IReadOnlyList<string> DescribeChain(ChainSnapshot? chain)
    {
        if (chain is null || chain.Elements.Count == 0) return Array.Empty<string>();
        var lines = new List<string> { $"chain ({chain.Elements.Count} certificate{(chain.Elements.Count == 1 ? "" : "s")}):" };
        for (int i = 0; i < chain.Elements.Count; i++)
        {
            var (subject, thumbprint, status) = chain.Elements[i];
            lines.Add($"  {i + 1}. {subject}  {thumbprint}{(status.Length == 0 ? "" : " [" + status + "]")}");
        }
        return lines;
    }

    private static string? InterceptionNoteFor(ChainSnapshot? chain)
    {
        if (chain is null || chain.Elements.Count == 0) return null;
        var (rootSubject, rootThumbprint, _) = chain.Elements[^1];
        bool locallyTrusted = false;
        try
        {
            using var store = new X509Store(StoreName.Root, StoreLocation.LocalMachine);
            store.Open(OpenFlags.ReadOnly);
            locallyTrusted = store.Certificates.Any(c => c.Thumbprint == rootThumbprint);
        }
        catch { /* a store we cannot read simply contributes nothing */ }
        return DoctorReport.InterceptionNote(rootSubject, locallyTrusted);
    }

    /// <summary>The client-certificate request, described against what this machine actually has.
    /// This is the single most useful line doctor prints for an mTLS problem.</summary>
    private static IReadOnlyList<string> DescribeIssuerRequest(
        string[] acceptableIssuers, IReadOnlyList<X509Certificate2> store, X509Certificate2? chosen)
    {
        if (acceptableIssuers.Length == 0)
            return new[] { "client certificate: the server did not ask for one" };

        var lines = new List<string> { $"the server accepts client certificates from {acceptableIssuers.Length} authority/authorities:" };
        foreach (var issuer in acceptableIssuers.Take(10)) lines.Add("  " + issuer);
        if (acceptableIssuers.Length > 10) lines.Add($"  … and {acceptableIssuers.Length - 10} more");

        var matches = DoctorReport.MatchIssuers(store, acceptableIssuers);
        lines.Add(matches.Count == 0
            ? $"NONE of your {store.Count} certificate(s) are issued by any of those authorities"
            : $"{matches.Count} of your certificates match: " + string.Join(", ", matches.Take(5).Select(c => c.Subject)));
        if (chosen is not null)
            lines.Add(DoctorReport.MatchIssuers(new[] { chosen }, acceptableIssuers).Count > 0
                ? "the certificate you chose IS issued by one of them"
                : $"the certificate you chose is NOT — it is issued by {chosen.Issuer}");
        return lines;
    }

    private static string? AdviseOnHandshake(
        string[] acceptableIssuers, IReadOnlyList<X509Certificate2> store, X509Certificate2? chosen)
    {
        if (acceptableIssuers.Length > 0 && chosen is not null &&
            DoctorReport.MatchIssuers(new[] { chosen }, acceptableIssuers).Count == 0)
            return "The server refused the handshake and does not accept your certificate's issuer — " +
                   "that is very likely the cause. The authorities it does accept are listed above.";
        if (acceptableIssuers.Length > 0 && chosen is null)
            return "The server asked for a client certificate and none was supplied. Pass --cert (or --cert-file).";
        return "The handshake failed before HTTP. If this network inspects TLS, a client certificate cannot survive the middle.";
    }

    /// <summary>What to say when the host could not be reached at all: is anything reachable?
    /// <paramref name="probe"/> returns the probe URL's body, or null when it could not be
    /// fetched — injectable so tests never touch the internet.</summary>
    /// <summary>--offline: the probe is a network call the user said not to make.</summary>
    private static readonly Func<string, Task<string?>> OfflineProbe = _ => Task.FromResult<string?>(OfflineMarker);
    private const string OfflineMarker = "\0offline";

    private static async Task<string> AdviseOnReachabilityAsync(Func<string, Task<string?>>? probe, CancellationToken ct)
    {
        string? body;
        if (probe is not null) body = await probe(NcsiUrl);
        else
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                body = await http.GetStringAsync(NcsiUrl, ct);
            }
            catch { body = null; }
        }

        if (body == OfflineMarker)
            return "The host could not be reached. The internet-connectivity probe was not run (--offline).";
        if (body is null)
            return "This machine has no working internet connection at all — check the network, or the VPN if this host is internal.";
        if (!body.Contains(NcsiExpected, StringComparison.Ordinal))
            return "A captive portal answered the connectivity check — sign in to the network (hotel/guest Wi-Fi) and try again.";
        return "The internet is reachable, so the problem is specific to this host: check the spelling, " +
               "or connect the VPN if it only resolves on the corporate network.";
    }
}
