using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace ApiTester.Core;

/// <summary>Transport for <see cref="MockServer"/>: plain HTTP, HTTPS, or HTTPS requiring a client
/// certificate (mTLS).</summary>
public enum MockTlsMode { Http, Https, Mtls }

/// <summary>Ways `mock --cors-fault` gets CORS wrong on purpose, so a client's (or a checker's)
/// handling of a broken server can be exercised.</summary>
public enum MockCorsFault
{
    None,
    /// <summary>Two Access-Control-Allow-Origin values, which a browser refuses outright.</summary>
    DoubleOrigin,
    /// <summary>Preflights answered 401, as by a server that wants authentication on OPTIONS.</summary>
    PreflightUnauthorized,
    /// <summary>Preflights answered with a redirect, which a browser never follows.</summary>
    PreflightRedirect,
    /// <summary>CORS headers on the preflight only, none on the response itself.</summary>
    PreflightOnly,
    /// <summary>The origin echoed without Vary: Origin.</summary>
    NoVary
}

/// <summary>`mock --cors`: answer preflights and put CORS headers on every response, as a server
/// with a real CORS policy would — so a front end can be tested against both outcomes.
/// <paramref name="Origins"/> null allows any origin (echoed back, credentials allowed); a list
/// allows only those, and every other origin gets no CORS headers at all, which a browser blocks.
/// The lists below narrow what is granted; null means grant whatever is asked.</summary>
public sealed record MockCors(IReadOnlyList<string>? Origins)
{
    /// <summary>The request headers a preflight is allowed; null echoes what it asked for.</summary>
    public IReadOnlyList<string>? AllowHeaders { get; init; }
    /// <summary>The methods a preflight is allowed; null grants the one asked for and the usual ones.</summary>
    public IReadOnlyList<string>? AllowMethods { get; init; }
    /// <summary>The response headers a script may read; null exposes every header of the response.</summary>
    public IReadOnlyList<string>? Expose { get; init; }
    public int MaxAge { get; init; } = 600;
    /// <summary>Answer Access-Control-Allow-Origin: * and no Access-Control-Allow-Credentials — fine
    /// for public data, and the classic failure for a request sent with credentials.</summary>
    public bool Star { get; init; }
    public MockCorsFault Fault { get; init; }
    /// <summary>No CORS at all: a route declared with "cors": false inside a mock that has a policy.</summary>
    public bool Off { get; init; }

    public bool Allows(string origin) =>
        Star || Origins is null || Origins.Any(o => o == "*" || o.Equals(origin, StringComparison.OrdinalIgnoreCase));

    /// <summary>The answer to a preflight: 204 with the grants, or 403 for an origin not allowed.</summary>
    public (int Status, IReadOnlyList<KeyValuePair<string, string>> Headers) Preflight(string origin, string method, string? requestHeaders,
        bool privateNetwork = false, string path = "/")
    {
        if (Off) return (405, new[] { new KeyValuePair<string, string>("Allow", "GET, HEAD, POST, PUT, PATCH, DELETE") });
        if (!Allows(origin)) return (403, new[] { new KeyValuePair<string, string>("Vary", "Origin") });
        if (Fault == MockCorsFault.PreflightUnauthorized)
            return (401, new[] { new KeyValuePair<string, string>("WWW-Authenticate", "Bearer realm=\"certapi mock\"") });
        if (Fault == MockCorsFault.PreflightRedirect)
            return (302, new[] { new KeyValuePair<string, string>("Location", "/login?next=" + Uri.EscapeDataString(path)) });
        var headers = new List<KeyValuePair<string, string>>();
        AddOrigin(headers, origin);
        headers.Add(new("Access-Control-Allow-Methods", string.Join(", ", AllowMethods ??
            new[] { method.ToUpperInvariant(), "GET", "HEAD", "POST", "PUT", "PATCH", "DELETE", "OPTIONS" }.Distinct())));
        if (AllowHeaders is { } fixedHeaders)
        {
            if (fixedHeaders.Count > 0) headers.Add(new("Access-Control-Allow-Headers", string.Join(", ", fixedHeaders)));
        }
        else if (!string.IsNullOrWhiteSpace(requestHeaders)) headers.Add(new("Access-Control-Allow-Headers", requestHeaders));
        if (privateNetwork) headers.Add(new("Access-Control-Allow-Private-Network", "true"));
        headers.Add(new("Access-Control-Max-Age", MaxAge.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        return (204, headers);
    }

    private void AddOrigin(List<KeyValuePair<string, string>> headers, string origin)
    {
        if (Star) headers.Add(new("Access-Control-Allow-Origin", "*"));
        else
        {
            headers.Add(new("Access-Control-Allow-Origin", origin));
            headers.Add(new("Access-Control-Allow-Credentials", "true"));
        }
        if (Fault == MockCorsFault.DoubleOrigin) headers.Add(new("Access-Control-Allow-Origin", Star ? origin : "*"));
        if (!Star && Fault != MockCorsFault.NoVary) headers.Add(new("Vary", "Origin"));
    }

    /// <summary>A response head with any Access-Control-* lines replaced by this policy's for
    /// <paramref name="origin"/>; every other header the response carries is exposed to scripts
    /// unless <see cref="Expose"/> says which.</summary>
    public string Rewrite(string head, string origin)
    {
        var lines = head.Split("\r\n").ToList();
        if (lines.Count == 0) return head;
        var kept = new List<string> { lines[0] };
        var names = new List<string>();
        foreach (var line in lines.Skip(1))
        {
            if (line.Length == 0) continue;
            int colon = line.IndexOf(':');
            string name = colon > 0 ? line[..colon].Trim() : line;
            if (name.StartsWith("Access-Control-", StringComparison.OrdinalIgnoreCase)) continue;
            kept.Add(line);
            if (!CorsRules.SafelistedResponse.Contains(name) && !name.Equals("Connection", StringComparison.OrdinalIgnoreCase) &&
                !name.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase) && !names.Contains(name, StringComparer.OrdinalIgnoreCase))
                names.Add(name);
        }
        if (!Off && Fault != MockCorsFault.PreflightOnly && Allows(origin))
        {
            var headers = new List<KeyValuePair<string, string>>();
            AddOrigin(headers, origin);
            var expose = Expose ?? names;
            if (expose.Count > 0) headers.Add(new("Access-Control-Expose-Headers", string.Join(", ", expose)));
            kept.AddRange(headers.Select(h => $"{h.Key}: {h.Value}"));
        }
        return string.Join("\r\n", kept) + "\r\n\r\n";
    }

    /// <summary>Read a policy from a scenario route's "cors": false (none), true (any origin), or
    /// an object with origins, headers, methods, expose, maxAge, star and fault.</summary>
    public static MockCors? FromJson(System.Text.Json.JsonElement element, string where, List<string> warnings)
    {
        switch (element.ValueKind)
        {
            case System.Text.Json.JsonValueKind.False: return new MockCors((IReadOnlyList<string>?)null) { Off = true };
            case System.Text.Json.JsonValueKind.True: return new MockCors((IReadOnlyList<string>?)null);
            case System.Text.Json.JsonValueKind.Object: break;
            default:
                warnings.Add($"{where}: \"cors\" must be true, false or an object; ignored.");
                return null;
        }
        IReadOnlyList<string>? List(string name)
        {
            if (!element.TryGetProperty(name, out var v)) return null;
            if (v.ValueKind == System.Text.Json.JsonValueKind.String) return new[] { v.GetString()! };
            if (v.ValueKind == System.Text.Json.JsonValueKind.Array)
                return v.EnumerateArray().Where(x => x.ValueKind == System.Text.Json.JsonValueKind.String).Select(x => x.GetString()!).ToList();
            warnings.Add($"{where}: \"cors.{name}\" must be a string or a list of strings; ignored.");
            return null;
        }
        var fault = MockCorsFault.None;
        if (element.TryGetProperty("fault", out var f) && f.ValueKind == System.Text.Json.JsonValueKind.String)
            if (!TryParseFault(f.GetString()!, out fault))
                warnings.Add($"{where}: unknown cors.fault '{f.GetString()}' (known: {string.Join(", ", FaultNames)}); ignored.");
        return new MockCors(List("origins"))
        {
            AllowHeaders = List("headers"),
            AllowMethods = List("methods")?.Select(m => m.ToUpperInvariant()).ToList(),
            Expose = List("expose"),
            MaxAge = element.TryGetProperty("maxAge", out var age) && age.TryGetInt32(out var seconds) && seconds >= 0 ? seconds : 600,
            Star = element.TryGetProperty("star", out var star) && star.ValueKind == System.Text.Json.JsonValueKind.True,
            Fault = fault
        };
    }

    public static readonly IReadOnlyList<string> FaultNames =
        new[] { "double-origin", "preflight-401", "preflight-redirect", "preflight-only", "no-vary" };

    public static bool TryParseFault(string name, out MockCorsFault fault)
    {
        fault = name.ToLowerInvariant() switch
        {
            "double-origin" => MockCorsFault.DoubleOrigin,
            "preflight-401" => MockCorsFault.PreflightUnauthorized,
            "preflight-redirect" => MockCorsFault.PreflightRedirect,
            "preflight-only" => MockCorsFault.PreflightOnly,
            "no-vary" => MockCorsFault.NoVary,
            _ => (MockCorsFault)(-1)
        };
        return fault != (MockCorsFault)(-1);
    }
}

/// <summary>Generates the self-signed certificates a TLS/mTLS mock server uses and writes the public
/// certs (and, for mTLS, a ready-to-use client .pfx) to a folder. Shared by the CLI and the GUI so
/// both produce identical files.</summary>
/// <summary>How the mock's own server certificate should misbehave. Every client-side TLS error
/// this product reports is otherwise only reachable from inside the test suite; these make each of
/// them reproducible at a terminal, which is what turns the mock into a test bed for `doctor` and
/// for `send`'s error paths.</summary>
public enum MockTlsDefect
{
    /// <summary>A valid certificate for localhost — the historical behaviour.</summary>
    None,
    /// <summary>Validity ended yesterday.</summary>
    Expired,
    /// <summary>Valid, but issued for a different host, so the name check fails.</summary>
    WrongHost,
    /// <summary>Not issued by the mock's own certificate authority either — a bare self-signed
    /// leaf, the shape a device or an appliance usually presents.</summary>
    SelfSigned
}

public static class MockCertificates
{
    /// <param name="Reused">True when the certificate authority (and so mock-ca.cer) is the one
    /// a previous start wrote, rather than a new one a client has to be told to trust again.</param>
    public sealed record Generated(X509Certificate2 ServerCertificate, IReadOnlyList<string> WrittenFiles, bool Reused = false);

    // The private keys a later start needs to sign with and to serve. They are kept encrypted
    // to the current Windows user (DPAPI), never in the clear: anyone holding the authority's key
    // could mint a certificate that a machine which trusts mock-ca.cer would accept. Where DPAPI
    // is not available nothing is kept, and every start makes new certificates as it always did.
    internal const string CaKeyFile = "mock-ca.key";
    internal const string ServerKeyFile = "mock-server.key";

    /// <summary>The mock's certificates, from <paramref name="certDir"/> when the ones a previous
    /// start left there are still good, otherwise new. Reusing them keeps mock-ca.cer the same
    /// from one start to the next, so a client trusts it once, and saves generating three RSA
    /// keys on every start.</summary>
    /// <param name="fresh">Make new certificates (a new authority) whatever is there.</param>
    public static Generated Generate(MockTlsMode mode, string certDir, MockTlsDefect defect = MockTlsDefect.None, bool fresh = false)
    {
        Directory.CreateDirectory(certDir);
        var protector = SecretProtection.Default;
        bool keep = protector.IsAvailable;
        var files = new List<string>();
        void Write(string name, byte[] bytes)
        {
            string path = Path.Combine(certDir, name);
            if (!File.Exists(path) || !File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes)) File.WriteAllBytes(path, bytes);
            files.Add(path);
        }
        void WriteKey(string name, X509Certificate2 cert)
        {
            if (!keep) return;
            string? sealedPfx = protector.Protect(Convert.ToBase64String(cert.Export(X509ContentType.Pfx)));
            if (sealedPfx is not null) File.WriteAllText(Path.Combine(certDir, name), sealedPfx);
        }

        X509Certificate2? ca = keep && !fresh ? LoadKey(certDir, CaKeyFile, protector) : null;
        if (ca is not null && !(ca.HasPrivateKey && IsAuthority(ca) && Lasts(ca, TimeSpan.FromDays(400))))
        {
            ca.Dispose();
            ca = null;
        }
        bool reused = ca is not null;
        if (ca is null)
        {
            ca = SelfSignedCertificateFactory.CreateCertificateAuthority("certapi mock CA");
            WriteKey(CaKeyFile, ca);
        }

        try
        {
            X509Certificate2 serverCert;
            if (defect == MockTlsDefect.None)
            {
                var kept = reused ? LoadKey(certDir, ServerKeyFile, protector) : null;
                if (kept is not null && kept.HasPrivateKey && kept.Subject == "CN=localhost" && NamesLoopback(kept) &&
                    Lasts(kept, TimeSpan.FromDays(1)) && IssuedBy(kept, ca))
                    serverCert = kept;
                else
                {
                    kept?.Dispose();
                    serverCert = SelfSignedCertificateFactory.CreateSignedCertificate(
                        "localhost", ca, serverAuth: true, clientAuth: false, dnsNames: new[] { "localhost" },
                        ipAddresses: LoopbackAddresses);
                    WriteKey(ServerKeyFile, serverCert);
                }
            }
            else
            {
                // A deliberately broken certificate is made fresh every time and its key is not
                // kept: the next ordinary start goes back to the kept good one.
                serverCert = defect switch
                {
                    // A certificate that WAS valid and has since lapsed — the realistic case, and a
                    // different client message than "not yet valid".
                    //
                    // The window has to sit inside the issuer's: X.509 refuses a leaf that begins
                    // before its certificate authority does, and an authority is minted one day
                    // before it is made. So the leaf runs from twelve hours ago to one hour ago —
                    // comfortably expired, comfortably inside the authority's own validity.
                    MockTlsDefect.Expired => SelfSignedCertificateFactory.CreateSignedCertificate(
                        "localhost", ca, serverAuth: true, clientAuth: false, dnsNames: new[] { "localhost" },
                        crlDistributionPoint: null,
                        notBefore: DateTimeOffset.UtcNow.AddHours(-12),
                        notAfter: DateTimeOffset.UtcNow.AddHours(-1)),

                    // Perfectly valid — for somewhere else. The client is connecting to localhost.
                    MockTlsDefect.WrongHost => SelfSignedCertificateFactory.CreateSignedCertificate(
                        "not-the-host.invalid", ca, serverAuth: true, clientAuth: false,
                        dnsNames: new[] { "not-the-host.invalid" }),

                    // Its own issuer: nothing chains to the mock's certificate authority at all.
                    MockTlsDefect.SelfSigned => SelfSignedCertificateFactory.CreateSelfSignedServerCertificate(
                        "localhost", new[] { "localhost" }),

                    _ => throw new ArgumentOutOfRangeException(nameof(defect)),
                };
            }

            Write("mock-ca.cer", ca.Export(X509ContentType.Cert));
            Write("mock-server.cer", serverCert.Export(X509ContentType.Cert));
            if (mode == MockTlsMode.Mtls)
            {
                string clientPath = Path.Combine(certDir, "mock-client.pfx");
                bool clientGood = false;
                if (reused && File.Exists(clientPath))
                {
                    try
                    {
                        using var client = X509CertificateLoader.LoadPkcs12FromFile(clientPath, (string?)null, X509KeyStorageFlags.EphemeralKeySet);
                        clientGood = client.HasPrivateKey && Lasts(client, TimeSpan.FromDays(1)) && IssuedBy(client, ca);
                    }
                    catch (CryptographicException) { }
                }
                if (clientGood) files.Add(clientPath);
                else
                {
                    using var client = SelfSignedCertificateFactory.CreateSignedCertificate(
                        "certapi mock client", ca, serverAuth: false, clientAuth: true);
                    Write("mock-client.pfx", client.Export(X509ContentType.Pfx));
                }
            }
            return new Generated(serverCert, files, reused);
        }
        finally
        {
            ca.Dispose();
        }
    }

    private static X509Certificate2? LoadKey(string certDir, string name, ISecretProtector protector)
    {
        string path = Path.Combine(certDir, name);
        try
        {
            if (!File.Exists(path)) return null;
            string? base64 = protector.Unprotect(File.ReadAllText(path).Trim());
            if (base64 is null) return null;
            // Exportable, as the factory makes them: SChannel cannot use an ephemeral key, and a
            // kept authority is exported again when its certificates are renewed.
            return X509CertificateLoader.LoadPkcs12(Convert.FromBase64String(base64), (string?)null, X509KeyStorageFlags.Exportable);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or CryptographicException)
        {
            return null;   // unreadable or from another user: make new ones
        }
    }

    /// <summary>The addresses the mock listens on, named in its certificate as well as
    /// localhost, so https://127.0.0.1 passes a trust check too.</summary>
    private static readonly IPAddress[] LoopbackAddresses = [IPAddress.Loopback, IPAddress.IPv6Loopback];

    private static bool NamesLoopback(X509Certificate2 cert) =>
        cert.Extensions.OfType<X509SubjectAlternativeNameExtension>().FirstOrDefault() is { } san &&
        LoopbackAddresses.All(a => san.EnumerateIPAddresses().Contains(a));

    private static bool Lasts(X509Certificate2 cert, TimeSpan margin) =>
        cert.NotBefore.ToUniversalTime() <= DateTime.UtcNow && cert.NotAfter.ToUniversalTime() > DateTime.UtcNow + margin;

    private static bool IsAuthority(X509Certificate2 cert) =>
        cert.Extensions.OfType<X509BasicConstraintsExtension>().Any(b => b.CertificateAuthority);

    internal static bool IssuedBy(X509Certificate2 leaf, X509Certificate2 ca)
    {
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(ca);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.DisableCertificateDownloads = true;
        return chain.Build(leaf) && chain.ChainElements.Count == 2 &&
               chain.ChainElements[1].Certificate.Thumbprint == ca.Thumbprint;
    }
}

/// <summary>A one-line record of a handled request, for the console log.</summary>
public sealed record MockRequestLog(string Method, string Path, int Status, string? ClientCertSubject)
{
    /// <summary>In replay mode: "exact", "path", or "miss"; null when not replaying.</summary>
    public string? Replay { get; init; }
}

/// <summary>
/// A small standing local test server you can fire requests at — the persistent counterpart to the
/// one-shot self-test. It reflects each request back as JSON (method, path, query, headers, body, and
/// the presented client certificate under mTLS), and serves a few fixed routes so the app's own
/// features can be exercised end-to-end without a real API:
/// <list type="bullet">
///   <item><c>/status/{code}</c> — responds with that HTTP status.</item>
///   <item><c>/sse</c> — a short <c>text/event-stream</c>.</item>
///   <item><c>/token</c> — an OAuth 2.0 token response.</item>
///   <item><c>/windows-auth</c> — a 401 NTLM challenge, then an authenticated response.</item>
///   <item><c>/cookie-auth</c> — sets a session cookie, then reports authenticated once it returns.</item>
///   <item><c>Upgrade: websocket</c> (any path) — a WebSocket echo.</item>
///   <item>anything else — the JSON echo.</item>
/// </list>
/// Built on <see cref="TcpListener"/> + <see cref="SslStream"/> so TLS and mTLS work on loopback with
/// no netsh URL/cert reservation. Connections are kept alive between requests when the response
/// is framed by its length (not a stream, an upgrade or a deliberate fault), so a client measures
/// requests rather than handshakes; <c>keepAlive: false</c> answers <c>Connection: close</c> to each.
/// </summary>
public sealed class MockServer : IAsyncDisposable
{
    private const string WebSocketGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

    private readonly TcpListener _listener;
    // The same port on ::1. "localhost" resolves to ::1 first on Windows, and a connection refused
    // there costs about two seconds before the client falls back to 127.0.0.1 — on every new
    // connection to https://localhost, the one name the mock's certificate is for.
    private readonly TcpListener? _listener6;
    private readonly Task? _acceptLoop6;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _acceptLoop;
    // Live connections only: each removes itself when it ends, so a mock that serves a million
    // requests holds a handful of tasks rather than a million finished ones.
    private readonly ConcurrentDictionary<Task, byte> _handlers = new();
    private readonly MockTlsMode _tls;
    private readonly X509Certificate2? _serverCert;
    private readonly Action<MockRequestLog>? _onRequest;
    private readonly HarReplaySource? _replay;
    private readonly MockScenario? _scenario;
    private readonly MockCors? _cors;
    private bool _disposed;

    public string BaseUrl { get; }
    public int Port { get; }

    private MockServer(TcpListener listener, TcpListener? listener6, int port, MockTlsMode tls,
        X509Certificate2? serverCert, Action<MockRequestLog>? onRequest, HarReplaySource? replay,
        MockScenario? scenario, MockCors? cors, bool keepAlive)
    {
        _keepAlive = keepAlive;   // before the accept loop starts, so the first connection sees it
        _cors = cors;
        _listener = listener;
        _listener6 = listener6;
        Port = port;
        _tls = tls;
        _serverCert = serverCert;
        _onRequest = onRequest;
        _replay = replay;
        _scenario = scenario;
        BaseUrl = $"{(tls == MockTlsMode.Http ? "http" : "https")}://127.0.0.1:{port}/";
        _acceptLoop = AcceptLoopAsync(listener);
        if (listener6 is not null) _acceptLoop6 = AcceptLoopAsync(listener6);
    }

    /// <summary>Start listening. <paramref name="port"/> 0 picks a free port (see <see cref="Port"/>).
    /// A server certificate is required for HTTPS/mTLS.
    /// <para>Three answering modes, tried in this order: a declared <paramref name="scenario"/>,
    /// then a recorded <paramref name="replay"/> session, then the built-in echo routes. Supplying
    /// both a scenario and a replay is deliberate and useful — the scenario states the handful of
    /// routes you care about, and the recording covers everything else.</para></summary>
    public static MockServer Start(int port, MockTlsMode tls,
        X509Certificate2? serverCert = null, Action<MockRequestLog>? onRequest = null,
        HarReplaySource? replay = null, MockScenario? scenario = null, MockCors? cors = null,
        bool keepAlive = true)
    {
        if (tls != MockTlsMode.Http && serverCert is null)
            throw new ArgumentException("A server certificate is required for HTTPS/mTLS.", nameof(serverCert));

        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        int actualPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        TcpListener? listener6 = null;
        if (Socket.OSSupportsIPv6)
        {
            try
            {
                listener6 = new TcpListener(IPAddress.IPv6Loopback, actualPort);
                listener6.Start();
            }
            catch (SocketException)
            {
                // ::1 unavailable, or that port taken there by something else: IPv4 alone, as before.
                listener6?.Stop();
                listener6 = null;
            }
        }
        return new MockServer(listener, listener6, actualPort, tls, serverCert, onRequest, replay, scenario, cors, keepAlive);
    }

    /// <summary>The largest request body the mock reads; a larger one closes the connection.</summary>
    internal const int MaxRequestBody = 16 * 1024 * 1024;

    /// <summary>How long a client has to send its request (or finish a TLS handshake) before the
    /// connection is dropped, so a client that sends headers and stalls cannot hold it forever.</summary>
    internal static TimeSpan RequestReadTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Connections served at once; more are closed as they arrive.</summary>
    internal const int MaxConnections = 256;

    private readonly SemaphoreSlim _connectionSlots = new(MaxConnections, MaxConnections);

    /// <summary>How long a kept-alive connection may sit idle between requests before the mock
    /// closes it — shorter than the first request's allowance, so idle clients free their slot.</summary>
    internal static TimeSpan KeepAliveIdle { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>False under `mock --no-keep-alive`: one request per connection, every response
    /// <c>Connection: close</c>, as a server that does not keep connections would answer.</summary>
    private readonly bool _keepAlive;

    private async Task AcceptLoopAsync(TcpListener listener)
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client;
                // A connection reset while it waited in the backlog (a scanner, a client that gave
                // up) is that connection's problem; the server keeps accepting. Any other accept
                // error (out of file handles) is retried after a pause, not in a tight loop.
                try { client = await listener.AcceptTcpClientAsync(_cts.Token); }
                catch (SocketException ex) when (!_cts.IsCancellationRequested)
                {
                    if (ex.SocketErrorCode is not (SocketError.ConnectionReset or SocketError.ConnectionAborted))
                        await Task.Delay(100, _cts.Token);
                    continue;
                }
                if (!_connectionSlots.Wait(0)) { client.Dispose(); continue; }
                // Responses go out as a head write and a body write: with Nagle on, the body waits
                // for an ACK the peer may delay by 40 ms, on every response of a kept-alive
                // connection. Each write here is a complete piece, so send it at once.
                client.NoDelay = true;
                var handler = Task.Run(async () =>
                {
                    try { await HandleAsync(client, _cts.Token); }
                    catch { /* a broken connection must not take the server down */ }
                    finally { client.Dispose(); _connectionSlots.Release(); }
                });
                _handlers.TryAdd(handler, 0);
                _ = handler.ContinueWith(t => _handlers.TryRemove(t, out _), TaskScheduler.Default);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
    }

    private async Task HandleAsync(TcpClient client, CancellationToken ct)
    {
        Stream stream = client.GetStream();
        string? clientCertSubject = null;
        // Kept alongside the subject so a scenario can require a specific issuer or thumbprint,
        // not merely "some certificate".
        string? clientCertIssuer = null, clientCertThumbprint = null;

        if (_tls != MockTlsMode.Http)
        {
            // Accept any presented client cert (ignore chain trust), but under mTLS a cert must be
            // presented — reject the handshake when none is (otherwise --mtls wouldn't require one).
            var ssl = new SslStream(stream, leaveInnerStreamOpen: false,
                userCertificateValidationCallback: (_, cert, _, _) => _tls != MockTlsMode.Mtls || cert is not null);
            var options = new SslServerAuthenticationOptions
            {
                ServerCertificate = _serverCert,
                ClientCertificateRequired = _tls == MockTlsMode.Mtls,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
            };
            using (var handshake = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                handshake.CancelAfter(RequestReadTimeout);
                await ssl.AuthenticateAsServerAsync(options, handshake.Token);
            }
            if (ssl.RemoteCertificate is not null)
            {
                using var c = new X509Certificate2(ssl.RemoteCertificate);
                clientCertSubject = c.Subject;
                clientCertIssuer = c.Issuer;
                clientCertThumbprint = c.Thumbprint;
            }
            stream = ssl;
        }

        // Requests on this connection, one after another, for as long as each response can share
        // it. Bytes that arrived past one request (a pipelining client) are carried to the next.
        var carry = new List<byte>();
        try
        {
            for (int served = 0; ; served++)
            {
                var request = await ReadRequestAsync(stream, carry, served == 0 ? RequestReadTimeout : KeepAliveIdle, ct);
                if (request is null) return;
                bool clientKeeps = _keepAlive && !request.Value.Http10 &&
                    !(request.Value.Headers.TryGetValue("Connection", out var connectionHeader) &&
                      connectionHeader.Contains("close", StringComparison.OrdinalIgnoreCase));
                bool kept = await ServeAsync(stream, request.Value, client, carry, clientKeeps,
                    clientCertSubject, clientCertIssuer, clientCertThumbprint, ct);
                if (!kept || !client.Connected) return;
            }
        }
        finally { await stream.DisposeAsync(); }
    }

    /// <summary>Answer one request. True when the response left the connection open for another.</summary>
    private async Task<bool> ServeAsync(Stream connection,
        (string Method, string Target, Dictionary<string, string> Headers, string Body, bool Http10) request,
        TcpClient client, List<byte> carry, bool clientKeeps,
        string? clientCertSubject, string? clientCertIssuer, string? clientCertThumbprint, CancellationToken ct)
    {
        var (method, target, headers, body, _) = request;
        // Every response head goes through here: the CORS policy (once it is known) rewrites its
        // Access-Control-* lines, and a head that can share the connection says keep-alive.
        bool kept = false;
        Func<string, string>? cors = null;
        var stream = new HeadRewriteStream(connection, head =>
        {
            if (cors is not null) head = cors(head);
            if (clientKeeps && CanKeepAlive(head, method))
            {
                kept = true;
                head = head.Replace("\r\nConnection: close\r\n", "\r\nConnection: keep-alive\r\n", StringComparison.Ordinal);
            }
            return head;
        });
        {
            string path = target.Split('?')[0];
            string query = target.Contains('?') ? target[(target.IndexOf('?') + 1)..] : "";

            // --cors: a preflight is answered by the policy before anything else (a browser sends
            // it without credentials, so not even a scenario's requirements apply), and every
            // other response to a page's request has its CORS headers set by the policy.
            // The policy for this request: the matched route's own "cors", when it declares one,
            // else the mock's. For a preflight, the route is the one the real request would reach.
            var corsPolicy = _cors;
            bool isPreflight = method == "OPTIONS" && headers.ContainsKey("Origin") && headers.ContainsKey("Access-Control-Request-Method");
            if (_scenario is not null &&
                _scenario.Match(isPreflight ? headers["Access-Control-Request-Method"] : method, target, headers) is { Cors: { } routePolicy })
                corsPolicy = routePolicy;
            if (corsPolicy is not null && headers.TryGetValue("Origin", out var corsOrigin))
            {
                if (isPreflight)
                {
                    var (preflightStatus, preflightHeaders) = corsPolicy.Preflight(corsOrigin, headers["Access-Control-Request-Method"],
                        headers.TryGetValue("Access-Control-Request-Headers", out var asked) ? asked : null,
                        headers.TryGetValue("Access-Control-Request-Private-Network", out var pna) && pna.Trim() == "true", path);
                    var head = new StringBuilder();
                    head.Append("HTTP/1.1 ").Append(preflightStatus).Append(' ').Append(ReasonPhrase(preflightStatus)).Append("\r\n");
                    foreach (var h in preflightHeaders) head.Append(h.Key).Append(": ").Append(h.Value).Append("\r\n");
                    head.Append("Content-Length: 0\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()), ct);
                    await stream.FlushAsync(ct);
                    _onRequest?.Invoke(new MockRequestLog(method, path, preflightStatus, clientCertSubject) { Replay = "preflight" });
                    return kept;
                }
                if (!(headers.TryGetValue("Upgrade", out var upgrade) && upgrade.Contains("websocket", StringComparison.OrdinalIgnoreCase)))
                {
                    var policy = corsPolicy;
                    cors = head => policy.Rewrite(head, corsOrigin);
                }
            }

            // Before any route: what the scenario says every caller must present. A protected
            // endpoint refuses before it routes, and a mock that echoed instead would teach a
            // client's authentication path nothing.
            if (_scenario?.Require is { } require &&
                require.Refuse(clientCertSubject, clientCertIssuer, clientCertThumbprint, headers) is { } reason)
            {
                var challenge = require.Challenge();
                var refusal = new MockResponse(
                    require.OnFail,
                    new[] { challenge, new KeyValuePair<string, string>("Content-Type", "application/json") },
                    Encoding.UTF8.GetBytes(
                        $"{{\"server\":\"certapi mock\",\"refused\":{JsonSerializer.Serialize(reason)}}}"),
                    "application/json");
                await WriteMockResponseAsync(stream, refusal, client, ct);
                _onRequest?.Invoke(new MockRequestLog(method, path, require.OnFail, clientCertSubject)
                {
                    Replay = "refused"
                });
                return kept;
            }

            // WebSocket upgrade (any path) — after the scenario's requirements, so a protected
            // mock refuses an unauthenticated socket as it refuses an unauthenticated request.
            if (headers.TryGetValue("Upgrade", out var up) && up.Contains("websocket", StringComparison.OrdinalIgnoreCase))
            {
                await HandleWebSocketAsync(stream, headers, ct);
                _onRequest?.Invoke(new(method, path, 101, clientCertSubject));
                return kept;
            }

            // A declared scenario is consulted first: it states the routes someone deliberately
            // wrote, which must win over both a recording and the built-in echo routes. A miss
            // falls through — to the recording when there is one (scenario for what you care
            // about, capture for the rest), and otherwise to the scenario's own fallback.
            if (_scenario is not null)
            {
                var route = _scenario.Match(method, target, headers);
                if (route is not null)
                {
                    // Next() advances the route's own call counter, so a declared sequence answers
                    // differently each time — which is the whole point of testing a retry policy.
                    var answer = route.Next();
                    // A deliberate fault, or a route that states its own Connection header, keeps
                    // the connection behaviour it asked for rather than this server's.
                    if (answer.Fault != MockFault.None ||
                        answer.Headers.Any(h => h.Key.Equals("Connection", StringComparison.OrdinalIgnoreCase)))
                        clientKeeps = false;
                    await WriteMockResponseAsync(stream, answer, client, ct);
                    _onRequest?.Invoke(new MockRequestLog(method, path, answer.Status, clientCertSubject)
                    {
                        Replay = route.Sequence.Count > 0 ? $"route #{route.Calls}" : "route"
                    });
                    return kept;
                }
                if (_replay is null)
                {
                    var fallback = _scenario.Fallback
                        ?? new MockResponse(404, Array.Empty<KeyValuePair<string, string>>(),
                            Encoding.UTF8.GetBytes("{\"server\":\"certapi mock\",\"scenario\":\"no route\"}"),
                            "application/json");
                    await WriteMockResponseAsync(stream, fallback, client, ct);
                    _onRequest?.Invoke(new MockRequestLog(method, path, fallback.Status, clientCertSubject)
                    {
                        Replay = "no route"
                    });
                    return kept;
                }
            }

            // Replay mode: the recorded HAR session is the whole surface — a miss answers
            // NoMatchStatus rather than falling through to the built-in routes below.
            if (_replay is not null)
            {
                var recorded = _replay.Match(method, target, out var kind);
                int replayStatus;
                if (recorded is not null)
                {
                    await WriteRecordedAsync(stream, recorded, ct);
                    replayStatus = recorded.Status;
                }
                else
                {
                    replayStatus = _replay.Options.NoMatchStatus;
                    await WriteResponseAsync(stream, replayStatus, "application/json",
                        "{\"server\":\"certapi mock\",\"replay\":\"no match\"}", ct);
                }
                _onRequest?.Invoke(new MockRequestLog(method, path, replayStatus, clientCertSubject)
                {
                    Replay = kind switch { ReplayMatch.Exact => "exact", ReplayMatch.Path => "path", _ => "miss" }
                });
                return kept;
            }

            // Windows Integrated Auth (Negotiate/NTLM) challenge on this route.
            if (path.Equals("/windows-auth", StringComparison.Ordinal))
            {
                await HandleWindowsAuthAsync(stream, method, headers, clientCertSubject, carry, ct);
                return kept;
            }

            int status;
            if (path.StartsWith("/status/", StringComparison.Ordinal) &&
                int.TryParse(path["/status/".Length..], out var code) && code is >= 100 and <= 599)
            {
                status = code;
                await WriteResponseAsync(stream, status, "application/json",
                    $"{{\"status\":{code},\"server\":\"certapi mock\"}}", ct);
            }
            else if (path.Equals("/sse", StringComparison.Ordinal) || path.StartsWith("/sse/", StringComparison.Ordinal))
            {
                status = 200;
                await WriteSseAsync(stream, ct);
            }
            else if (path.Equals("/token", StringComparison.Ordinal) || path.StartsWith("/token/", StringComparison.Ordinal))
            {
                status = 200;
                await WriteResponseAsync(stream, 200, "application/json",
                    "{\"access_token\":\"mock-access-token\",\"token_type\":\"Bearer\"," +
                    "\"expires_in\":3600,\"refresh_token\":\"mock-refresh-token\",\"scope\":\"mock\"}", ct);
            }
            else if (path.Equals("/cookie-auth", StringComparison.Ordinal))
            {
                // Emulates a cookie-session-protected endpoint: hands out a cookie until the client
                // presents it, then reports the request as authenticated. Lets session capture be
                // exercised end to end.
                status = 200;
                bool hasCookie = headers.TryGetValue("Cookie", out var cookie) &&
                                 cookie.Contains("MOCKSID=ok", StringComparison.Ordinal);
                if (hasCookie)
                    await WriteResponseAsync(stream, 200, "application/json",
                        "{\"server\":\"certapi mock\",\"authenticated\":\"cookie\"}", ct);
                else
                    await WriteResponseAsync(stream, 200, "application/json",
                        "{\"server\":\"certapi mock\",\"authenticated\":false}", ct,
                        setCookie: "MOCKSID=ok; Path=/");
            }
            else
            {
                status = 200;
                await WriteResponseAsync(stream, 200, "application/json",
                    BuildEcho(method, path, query, headers, body, clientCertSubject), ct);
            }

            _onRequest?.Invoke(new(method, path, status, clientCertSubject));
            return kept;
        }
    }

    private static string BuildEcho(string method, string path, string query,
        IReadOnlyDictionary<string, string> headers, string body, string? clientCertSubject)
    {
        var obj = new Dictionary<string, object?>
        {
            ["server"] = "certapi mock",
            ["method"] = method,
            ["path"] = path,
            ["query"] = query.Length > 0 ? query : null,
            ["headers"] = headers.ToDictionary(h => h.Key, h => h.Value),
            ["body"] = body.Length > 0 ? body : null,
            ["clientCertificate"] = clientCertSubject
        };
        // Relaxed escaping keeps the echoed body readable (\" not ") — this is a dev tool.
        return JsonSerializer.Serialize(obj, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
    }

    /// <summary>Whether a response head can leave its connection open for the next request: it must
    /// say how long its body is, and that body must be one the client will actually read — not the
    /// body of a HEAD, and not a body on a status that has none (1xx, 204, 304), which a client would
    /// otherwise take for the start of the next response.</summary>
    internal static bool CanKeepAlive(string head, string method)
    {
        if (!head.Contains("\r\nConnection: close\r\n", StringComparison.Ordinal)) return false;
        if (!head.StartsWith("HTTP/1.1 ", StringComparison.Ordinal) || head.Length < 12 ||
            !int.TryParse(head.AsSpan(9, 3), out int status)) return false;
        long? length = null;
        foreach (var line in head.Split("\r\n"))
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase) &&
                long.TryParse(line.AsSpan(15).Trim(), out long n)) length = n;
        if (length is not { } bodyLength) return false;
        if (bodyLength == 0) return true;
        if (method.Equals("HEAD", StringComparison.OrdinalIgnoreCase)) return false;
        return status >= 200 && status != 204 && status != 304;
    }

    /// <summary>Passes one response through, rewriting its head (everything up to the blank line) on
    /// the way: every writer here sends the whole head in its first write. Made per request and
    /// never closes the connection under it, which outlives the request.</summary>
    private sealed class HeadRewriteStream : Stream
    {
        private readonly Stream _inner;
        private readonly Func<string, string> _rewrite;
        private bool _headDone;

        public HeadRewriteStream(Stream inner, Func<string, string> rewrite)
        {
            _inner = inner;
            _rewrite = rewrite;
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            if (!_headDone)
            {
                _headDone = true;
                string text = Encoding.ASCII.GetString(buffer.Span);
                int end = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                if (end >= 0 && text.StartsWith("HTTP/", StringComparison.Ordinal))
                {
                    var head = Encoding.ASCII.GetBytes(_rewrite(text[..(end + 4)]));
                    await _inner.WriteAsync(head, ct);
                    if (end + 4 < buffer.Length) await _inner.WriteAsync(buffer[(end + 4)..], ct);
                    return;
                }
            }
            await _inner.WriteAsync(buffer, ct);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();

        public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer, offset, count).GetAwaiter().GetResult();
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => _inner.ReadAsync(buffer, ct);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => _inner.ReadAsync(buffer, offset, count, ct);
        public override void Flush() => _inner.Flush();
        public override Task FlushAsync(CancellationToken ct) => _inner.FlushAsync(ct);
        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => _inner.CanWrite;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        // The connection is the caller's to close.
        public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static async Task WriteResponseAsync(Stream stream, int status, string contentType, string body,
        CancellationToken ct, string? setCookie = null)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        var head =
            $"HTTP/1.1 {status} {ReasonPhrase(status)}\r\n" +
            $"Content-Type: {contentType}; charset=utf-8\r\n" +
            $"Content-Length: {bytes.Length}\r\n" +
            (setCookie is null ? "" : $"Set-Cookie: {setCookie}\r\n") +
            "Access-Control-Allow-Origin: *\r\n" +
            "Connection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head), ct);
        await stream.WriteAsync(bytes, ct);
        await stream.FlushAsync(ct);
    }

    /// <summary>Writes a recorded HAR response verbatim: its own status/headers/body, framed with a
    /// fresh Content-Length and Connection: close (MockServer frames its own responses — the indexer
    /// already dropped any recorded framing headers).</summary>
    private static async Task WriteRecordedAsync(Stream stream, RecordedResponse recorded, CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.Append("HTTP/1.1 ").Append(recorded.Status).Append(' ')
          .Append(recorded.StatusText ?? ReasonPhrase(recorded.Status)).Append("\r\n");
        foreach (var h in recorded.Headers)
        {
            if (ContainsCrLf(h.Key) || ContainsCrLf(h.Value)) continue;   // defensive: never let a recorded header inject a response
            sb.Append(h.Key).Append(": ").Append(h.Value).Append("\r\n");
        }
        sb.Append("Content-Length: ").Append(recorded.Body.Length).Append("\r\n");
        sb.Append("Connection: close\r\n\r\n");

        await stream.WriteAsync(Encoding.ASCII.GetBytes(sb.ToString()), ct);
        await stream.WriteAsync(recorded.Body, ct);
        await stream.FlushAsync(ct);
    }

    /// <summary>Write a declared scenario response. Mirrors <see cref="WriteRecordedAsync"/>,
    /// including its defence against a header value that would inject a second response.</summary>
    private static async Task WriteMockResponseAsync(
        Stream stream, MockResponse response, TcpClient client, CancellationToken ct)
    {
        // Before the first byte: the pause a timeout test needs. Jitter keeps repeated calls from
        // being a metronome, which is what a retry-with-backoff test wants to see.
        int delay = response.DelayMs;
        if (response.JitterMs > 0) delay += Random.Shared.Next(0, response.JitterMs + 1);
        if (delay > 0) await Task.Delay(delay, ct);

        // A reset must not be preceded by a graceful close, so it is decided before anything is
        // written: LingerState with a zero timeout makes Dispose send RST rather than FIN.
        if (response.Fault == MockFault.Reset)
        {
            client.LingerState = new LingerOption(true, 0);
            client.Close();
            return;
        }

        var sb = new StringBuilder();
        sb.Append("HTTP/1.1 ").Append(response.Status).Append(' ')
          .Append(ReasonPhrase(response.Status)).Append("\r\n");
        foreach (var h in response.Headers)
        {
            if (ContainsCrLf(h.Key) || ContainsCrLf(h.Value)) continue;
            sb.Append(h.Key).Append(": ").Append(h.Value).Append("\r\n");
        }
        // The declared length is always the whole body, even when the body is dripped or the
        // connection is about to be abandoned: that mismatch is exactly what a client under test
        // has to cope with, and correcting it here would hide the fault being injected.
        sb.Append("Content-Length: ").Append(response.Body.Length).Append("\r\n");
        sb.Append("Connection: close\r\n\r\n");

        await stream.WriteAsync(Encoding.ASCII.GetBytes(sb.ToString()), ct);
        await stream.FlushAsync(ct);

        if (response.Fault == MockFault.Abort)
        {
            // Headers promised a body; the connection goes away instead.
            client.Close();
            return;
        }

        if (response.DripBytesPerSecond > 0 && response.Body.Length > 0)
        {
            await DripAsync(stream, response.Body, response.DripBytesPerSecond, ct);
            return;
        }

        await stream.WriteAsync(response.Body, ct);
        await stream.FlushAsync(ct);
    }

    /// <summary>Write a body at a stated rate, so a client's read timeout has something to time
    /// out against. Chunked into tenths of a second rather than byte-by-byte: the same average
    /// rate, without a syscall per byte.</summary>
    private static async Task DripAsync(Stream stream, byte[] body, int bytesPerSecond, CancellationToken ct)
    {
        int chunk = Math.Max(1, bytesPerSecond / 10);
        for (int offset = 0; offset < body.Length; offset += chunk)
        {
            int count = Math.Min(chunk, body.Length - offset);
            await stream.WriteAsync(body.AsMemory(offset, count), ct);
            await stream.FlushAsync(ct);
            if (offset + count < body.Length) await Task.Delay(100, ct);
        }
    }

    private static bool ContainsCrLf(string s) => s.Contains('\r') || s.Contains('\n');

    // Emulates a Windows-auth-protected endpoint: challenges with 401 WWW-Authenticate: NTLM until
    // the client presents an Authorization header, then accepts it (short-circuiting the handshake).
    // The challenge is sent keep-alive so the client can continue on the same connection.
    private async Task HandleWindowsAuthAsync(Stream stream, string method,
        Dictionary<string, string> headers, string? clientCertSubject, List<byte> carry, CancellationToken ct)
    {
        var currentHeaders = headers;
        string currentMethod = method;
        for (int leg = 0; leg < 4; leg++)   // bound the handshake
        {
            if (currentHeaders.TryGetValue("Authorization", out var auth) && auth.Length > 0)
            {
                string scheme = auth.Split(' ')[0];
                await WriteResponseAsync(stream, 200, "application/json",
                    $"{{\"server\":\"certapi mock\",\"authenticated\":\"{scheme}\",\"method\":\"{currentMethod}\"}}", ct);
                _onRequest?.Invoke(new(currentMethod, "/windows-auth", 200, clientCertSubject));
                return;
            }

            // Keep-alive 401 (no Connection: close) so the handshake stays on this connection.
            var head = "HTTP/1.1 401 Unauthorized\r\nWWW-Authenticate: NTLM\r\nContent-Length: 0\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(head), ct);
            await stream.FlushAsync(ct);
            _onRequest?.Invoke(new(currentMethod, "/windows-auth", 401, clientCertSubject));

            var next = await ReadRequestAsync(stream, carry, RequestReadTimeout, ct);
            if (next is null) return;
            currentMethod = next.Value.Method;
            currentHeaders = next.Value.Headers;
        }
    }

    private static async Task WriteSseAsync(Stream stream, CancellationToken ct)
    {
        var head =
            "HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\n" +
            "Cache-Control: no-cache\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head), ct);
        await stream.FlushAsync(ct);
        for (int i = 1; i <= 3; i++)
        {
            var evt = $"event: tick\ndata: {{\"n\":{i},\"server\":\"certapi mock\"}}\n\n";
            await stream.WriteAsync(Encoding.UTF8.GetBytes(evt), ct);
            await stream.FlushAsync(ct);
            try { await Task.Delay(150, ct); } catch (OperationCanceledException) { break; }
        }
    }

    // ---------- WebSocket echo ----------

    private async Task HandleWebSocketAsync(Stream stream, IReadOnlyDictionary<string, string> headers, CancellationToken ct)
    {
        if (!headers.TryGetValue("Sec-WebSocket-Key", out var key)) return;
        string accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + WebSocketGuid)));
        var handshake =
            "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n" +
            $"Sec-WebSocket-Accept: {accept}\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(handshake), ct);
        await stream.FlushAsync(ct);

        // A message may arrive in several frames (the gateway relays in 8 KB pieces): continuation
        // frames are collected until the final one, and the whole message is echoed back.
        int messageOpcode = 0;
        var message = new MemoryStream();
        while (!ct.IsCancellationRequested)
        {
            var frame = await ReadFrameAsync(stream, ct);
            if (frame is null) return;
            var (opcode, fin, payload) = frame.Value;
            if (opcode == 0x8) { await stream.WriteAsync(BuildFrame(0x8, payload), ct); await stream.FlushAsync(ct); return; }
            if (opcode == 0x9) { await stream.WriteAsync(BuildFrame(0xA, payload), ct); await stream.FlushAsync(ct); continue; }
            if (opcode is 0x1 or 0x2) { messageOpcode = opcode; message.SetLength(0); }
            else if (opcode != 0x0 || messageOpcode == 0) continue;   // a pong, or a continuation of nothing
            message.Write(payload);
            if (!fin) continue;
            await stream.WriteAsync(BuildFrame(messageOpcode, message.ToArray()), ct);
            await stream.FlushAsync(ct);
            messageOpcode = 0;
            message.SetLength(0);
        }
    }

    private static async Task<(int Opcode, bool Fin, byte[] Payload)?> ReadFrameAsync(Stream s, CancellationToken ct)
    {
        var h = new byte[2];
        if (!await ReadExactAsync(s, h, 2, ct)) return null;
        int opcode = h[0] & 0x0F;
        bool fin = (h[0] & 0x80) != 0;
        bool masked = (h[1] & 0x80) != 0;
        long len = h[1] & 0x7F;
        if (len == 126) { var e = new byte[2]; if (!await ReadExactAsync(s, e, 2, ct)) return null; len = (e[0] << 8) | e[1]; }
        else if (len == 127) { var e = new byte[8]; if (!await ReadExactAsync(s, e, 8, ct)) return null; len = 0; for (int i = 0; i < 8; i++) len = (len << 8) | e[i]; }
        byte[] mask = Array.Empty<byte>();
        if (masked) { mask = new byte[4]; if (!await ReadExactAsync(s, mask, 4, ct)) return null; }
        var payload = new byte[len];
        if (len > 0 && !await ReadExactAsync(s, payload, (int)len, ct)) return null;
        if (masked) for (int i = 0; i < payload.Length; i++) payload[i] ^= mask[i % 4];
        return (opcode, fin, payload);
    }

    private static byte[] BuildFrame(int opcode, byte[] payload)
    {
        var header = new List<byte> { (byte)(0x80 | opcode) };
        if (payload.Length < 126) header.Add((byte)payload.Length);
        else if (payload.Length <= 0xFFFF) { header.Add(126); header.Add((byte)(payload.Length >> 8)); header.Add((byte)(payload.Length & 0xFF)); }
        else { header.Add(127); for (int i = 7; i >= 0; i--) header.Add((byte)(((long)payload.Length >> (8 * i)) & 0xFF)); }
        var frame = new byte[header.Count + payload.Length];
        header.CopyTo(frame);
        Array.Copy(payload, 0, frame, header.Count, payload.Length);
        return frame;
    }

    // ---------- request parsing ----------

    /// <summary>Read one request. <paramref name="carry"/> holds bytes already read past the
    /// previous request on this connection; whatever arrives past this one is left in it.</summary>
    private static async Task<(string Method, string Target, Dictionary<string, string> Headers, string Body, bool Http10)?>
        ReadRequestAsync(Stream stream, List<byte> carry, TimeSpan timeout, CancellationToken outer)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(outer);
        deadline.CancelAfter(timeout);
        var ct = deadline.Token;
        var buffer = new byte[8192];
        var acc = new List<byte>(carry);
        carry.Clear();
        int headerEnd = acc.Count > 0 ? IndexOfHeaderEnd(acc) : -1;
        while (headerEnd < 0)
        {
            int n = await stream.ReadAsync(buffer, ct);
            if (n == 0) break;
            acc.AddRange(new ArraySegment<byte>(buffer, 0, n));
            headerEnd = IndexOfHeaderEnd(acc);
            if (acc.Count > 1_048_576) break;   // 1 MB header guard
        }
        if (headerEnd < 0) return null;

        string headerText = Encoding.ASCII.GetString(acc.ToArray(), 0, headerEnd);
        var lines = headerText.Split("\r\n");
        var requestLine = lines[0].Split(' ');
        if (requestLine.Length < 2) return null;
        string method = requestLine[0];
        string target = requestLine[1];
        bool http10 = requestLine.Length > 2 && requestLine[2].Equals("HTTP/1.0", StringComparison.OrdinalIgnoreCase);

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 1; i < lines.Length; i++)
        {
            int colon = lines[i].IndexOf(':');
            if (colon > 0) headers[lines[i][..colon].Trim()] = lines[i][(colon + 1)..].Trim();
        }

        var bodyBytes = new List<byte>(acc.Skip(headerEnd + 4));
        if (headers.TryGetValue("Transfer-Encoding", out var te) && te.Contains("chunked", StringComparison.OrdinalIgnoreCase))
        {
            // A streamed body: decoded, so the echo shows what was sent rather than its framing,
            // and read to its end, so the mock never answers while the client is still sending.
            var decoded = await ReadChunkedAsync(bodyBytes, stream, buffer, carry, ct);
            if (decoded is null) return null;   // over the size limit
            return (method, target, headers, Encoding.UTF8.GetString(decoded), http10);
        }
        int contentLength = headers.TryGetValue("Content-Length", out var clRaw) && int.TryParse(clRaw, out var cl) ? cl : 0;
        if (contentLength > MaxRequestBody || contentLength < 0) return null;
        while (bodyBytes.Count < contentLength)
        {
            int n = await stream.ReadAsync(buffer, ct);
            if (n == 0) break;
            bodyBytes.AddRange(new ArraySegment<byte>(buffer, 0, n));
        }
        if (bodyBytes.Count > contentLength)
        {
            carry.AddRange(bodyBytes.Skip(contentLength));
            bodyBytes.RemoveRange(contentLength, bodyBytes.Count - contentLength);
        }
        string body = Encoding.UTF8.GetString(bodyBytes.ToArray());
        return (method, target, headers, body, http10);
    }

    /// <summary>Decodes a chunked body: <paramref name="pending"/> holds what already arrived with
    /// the headers, the rest is read from <paramref name="stream"/>. Trailers are read and dropped.</summary>
    private static async Task<byte[]?> ReadChunkedAsync(List<byte> pending, Stream stream, byte[] buffer, List<byte> carry, CancellationToken ct)
    {
        var body = new List<byte>();
        int pos = 0;

        async Task<bool> EnsureAsync(int count)
        {
            while (pending.Count - pos < count)
            {
                int n = await stream.ReadAsync(buffer, ct);
                if (n == 0) return false;
                pending.AddRange(new ArraySegment<byte>(buffer, 0, n));
            }
            return true;
        }

        async Task<string?> ReadLineAsync()
        {
            while (true)
            {
                for (int i = pos; i + 1 < pending.Count; i++)
                    if (pending[i] == '\r' && pending[i + 1] == '\n')
                    {
                        string line = Encoding.ASCII.GetString(pending.GetRange(pos, i - pos).ToArray());
                        pos = i + 2;
                        return line;
                    }
                if (pending.Count - pos > 64 * 1024) return null;   // no chunk-size line is that long
                int n = await stream.ReadAsync(buffer, ct);
                if (n == 0) return null;
                pending.AddRange(new ArraySegment<byte>(buffer, 0, n));
            }
        }

        while (true)
        {
            string? sizeLine = await ReadLineAsync();
            if (sizeLine is null) break;
            int semi = sizeLine.IndexOf(';');
            if (!int.TryParse(semi >= 0 ? sizeLine[..semi] : sizeLine, System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out int size) || size < 0) break;
            if (size == 0)
            {
                while (await ReadLineAsync() is { Length: > 0 }) { }   // trailers, up to the blank line
                break;
            }
            if ((long)body.Count + size > MaxRequestBody) return null;
            if (!await EnsureAsync(size + 2)) { body.AddRange(pending.Skip(pos)); break; }
            body.AddRange(pending.GetRange(pos, size));
            pos += size + 2;
        }
        if (pos < pending.Count) carry.AddRange(pending.Skip(pos));
        return body.ToArray();
    }

    private static int IndexOfHeaderEnd(List<byte> data)
    {
        for (int i = 0; i + 3 < data.Count; i++)
            if (data[i] == 13 && data[i + 1] == 10 && data[i + 2] == 13 && data[i + 3] == 10)
                return i;
        return -1;
    }

    private static async Task<bool> ReadExactAsync(Stream s, byte[] buf, int count, CancellationToken ct)
    {
        int off = 0;
        while (off < count)
        {
            int n = await s.ReadAsync(buf.AsMemory(off, count - off), ct);
            if (n == 0) return false;
            off += n;
        }
        return true;
    }

    private static string ReasonPhrase(int status) => status switch
    {
        200 => "OK", 201 => "Created", 202 => "Accepted", 204 => "No Content",
        301 => "Moved Permanently", 302 => "Found", 304 => "Not Modified",
        400 => "Bad Request", 401 => "Unauthorized", 403 => "Forbidden", 404 => "Not Found",
        405 => "Method Not Allowed", 409 => "Conflict", 418 => "I'm a teapot", 429 => "Too Many Requests",
        500 => "Internal Server Error", 502 => "Bad Gateway", 503 => "Service Unavailable",
        _ => "Status"
    };

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        _listener.Stop();
        _listener6?.Stop();
        try { await _acceptLoop; } catch { /* ignore */ }
        if (_acceptLoop6 is not null) try { await _acceptLoop6; } catch { /* ignore */ }
        try { await Task.WhenAll(_handlers.Keys.ToArray()); } catch { /* ignore */ }
        _cts.Dispose();
    }
}
