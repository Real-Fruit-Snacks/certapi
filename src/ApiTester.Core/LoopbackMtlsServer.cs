using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace ApiTester.Core;

public sealed class LoopbackMtlsServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _acceptLoop;
    private readonly System.Collections.Concurrent.ConcurrentBag<Task> _handlers = new();
    private bool _disposed;

    public string BaseUrl { get; }

    /// <summary>The same loopback endpoint addressed as a WebSocket (wss://) rather than https://.</summary>
    public string WebSocketUrl => "wss://" + BaseUrl["https://".Length..];

    /// <summary>The server name the last client asked for in the TLS handshake (SNI), or null when
    /// it sent none. The handshake is the only place a server can observe it, and it is the only way
    /// to tell an address pinned by --resolve from the hostname the request was really for.
    /// Recorded by the echo endpoint.</summary>
    public string? LastSniHost { get; private set; }

    /// <summary>How many requests this server has answered — the ground truth a retry test checks
    /// its attempt count against.</summary>
    public int RequestCount { get; private set; }

    private int _keepAliveConnectionCount;

    /// <summary>How many TLS handshakes a StartKeepAliveAsync server has completed — 1-based ordinals
    /// handed out with Interlocked.Increment directly against the backing field, so the getter can
    /// never observe a value written out of order by two handshakes racing to complete. Recorded only
    /// once AuthenticateAsServerAsync returns: a failed handshake is not a connection.</summary>
    public int ConnectionCount => _keepAliveConnectionCount;

    private int _keepAliveTotalRequestCount;

    /// <summary>How many requests a StartKeepAliveAsync server has answered across every connection it
    /// has accepted — contrast with the per-connection ordinal ("req=") embedded in each response
    /// body, which restarts at 1 on every new connection.</summary>
    public int TotalRequestCount => _keepAliveTotalRequestCount;

    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, string?>
        _keepAliveThumbprintsByConnection = new();

    /// <summary>One entry per TLS handshake a StartKeepAliveAsync server has completed, in handshake
    /// order: the client certificate presented on that connection, as GetCertHashString() renders it
    /// (the same uppercase SHA-1 form X509Certificate2.Thumbprint uses), or null when none was
    /// presented. A snapshot is built fresh on every read from a concurrent dictionary, so a caller can
    /// read it while other connections are still being accepted without tearing.</summary>
    public IReadOnlyList<string?> ClientCertificateThumbprintsByConnection =>
        _keepAliveThumbprintsByConnection.OrderBy(kv => kv.Key).Select(kv => kv.Value).ToList();

    private LoopbackMtlsServer(TcpListener listener, int port,
        Func<TcpClient, CancellationToken, Task> handleClient)
    {
        _listener = listener;
        BaseUrl = $"https://127.0.0.1:{port}/";
        _acceptLoop = AcceptLoopAsync(handleClient);
    }

    public static Task<LoopbackMtlsServer> StartAsync(
        X509Certificate2 serverCertificate,
        string expectedClientThumbprint,
        string responseBody = "{\"ok\":true}",
        string responseContentType = "application/json")
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var server = new LoopbackMtlsServer(listener, port,
            (client, ct) => HandleClientAsync(
                client, serverCertificate, expectedClientThumbprint,
                responseBody, responseContentType, ct));

        return Task.FromResult(server);
    }

    /// <summary>A server that answers 200 with a fixed body plus whatever extra response headers the
    /// test needs — the honest way for a test to stand up an upstream that sets a cookie or does its
    /// own CORS, rather than smuggling headers through another field.</summary>
    public static Task<LoopbackMtlsServer> StartWithHeadersAsync(
        X509Certificate2 serverCertificate,
        string expectedClientThumbprint,
        IReadOnlyList<KeyValuePair<string, string>> extraHeaders,
        string responseBody = "{\"ok\":true}",
        string responseContentType = "application/json",
        int status = 200)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var server = new LoopbackMtlsServer(listener, port,
            (client, ct) => HandleWithHeadersAsync(
                client, serverCertificate, expectedClientThumbprint,
                extraHeaders, responseBody, responseContentType, status, ct));

        return Task.FromResult(server);
    }

    /// <summary>A server that echoes the request line, headers, and body back in its 200 response —
    /// lets a test assert exactly what the client sent through.</summary>
    public static Task<LoopbackMtlsServer> StartEchoAsync(
        X509Certificate2 serverCertificate, string expectedClientThumbprint)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        // The handler is built before the instance exists, so the observed SNI name is written back
        // through the captured variable once it does.
        LoopbackMtlsServer? started = null;
        started = new LoopbackMtlsServer(listener, port,
            (client, ct) => HandleEchoAsync(client, serverCertificate, expectedClientThumbprint, ct,
                sni => { if (started is not null) started.LastSniHost = sni; }));
        return Task.FromResult(started);
    }

    /// <summary>A server that answers every request with a 302 to <paramref name="location"/>.</summary>
    public static Task<LoopbackMtlsServer> StartRedirectAsync(
        X509Certificate2 serverCertificate, string expectedClientThumbprint, string location)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = new LoopbackMtlsServer(listener, port,
            (client, ct) => HandleRedirectAsync(client, serverCertificate, expectedClientThumbprint, location, ct));
        return Task.FromResult(server);
    }

    /// <summary>A server that redirects <paramref name="hops"/> times before answering 200 with a body
    /// echoing "&lt;METHOD&gt; &lt;path&gt;|&lt;request body&gt;" — lets a test assert both the hop count and the
    /// per-status method/body rules the client applied on the way.</summary>
    public static Task<LoopbackMtlsServer> StartRedirectChainAsync(
        X509Certificate2 serverCertificate, string expectedClientThumbprint,
        int hops = 1, int statusCode = 302)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        // The port is known before the handler is built, so every hop can point at this same server.
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = new LoopbackMtlsServer(listener, port,
            (client, ct) => HandleRedirectChainAsync(
                client, serverCertificate, expectedClientThumbprint, port, hops, statusCode, ct));
        return Task.FromResult(server);
    }

    private static async Task HandleRedirectChainAsync(
        TcpClient client, X509Certificate2 serverCert, string expectedClientThumbprint,
        int port, int hops, int statusCode, CancellationToken ct)
    {
        await using var ssl = await AuthenticateServerAsync(client, serverCert, expectedClientThumbprint, ct);

        var buffer = new byte[8192];
        var request = new StringBuilder();
        int headerEnd = -1;
        while (headerEnd < 0)
        {
            int n = await ssl.ReadAsync(buffer, ct);
            if (n == 0) break;
            request.Append(Encoding.UTF8.GetString(buffer, 0, n));
            headerEnd = request.ToString().IndexOf("\r\n\r\n", StringComparison.Ordinal);
        }
        // Drain the body the client actually sent: "the body was dropped" is only observable if a
        // body that *was* sent would have been read back.
        int contentLength = ParseContentLength(request.ToString());
        int bodyHave = headerEnd >= 0 ? request.Length - (headerEnd + 4) : 0;
        while (bodyHave < contentLength)
        {
            int n = await ssl.ReadAsync(buffer, ct);
            if (n == 0) break;
            request.Append(Encoding.UTF8.GetString(buffer, 0, n));
            bodyHave += n;
        }

        string text = request.ToString();
        var requestLine = text.Split("\r\n")[0].Split(' ');
        string method = requestLine.Length > 0 ? requestLine[0] : "";
        string path = requestLine.Length > 1 ? requestLine[1] : "/";
        string body = headerEnd >= 0 ? text[(headerEnd + 4)..] : "";

        // "/" is hop 0; each redirect names the next one, so the path says how far along we are.
        int reached = path.StartsWith("/hop", StringComparison.Ordinal) &&
                      int.TryParse(path["/hop".Length..], out var parsed) ? parsed : 0;
        if (reached < hops)
        {
            var redirect =
                $"HTTP/1.1 {statusCode} {ReasonPhrase(statusCode)}\r\n" +
                $"Location: https://127.0.0.1:{port}/hop{reached + 1}\r\n" +
                "Content-Length: 0\r\n" +
                "Connection: close\r\n\r\n";
            await ssl.WriteAsync(Encoding.ASCII.GetBytes(redirect), ct);
            await ssl.FlushAsync(ct);
            return;
        }

        var bodyBytes = Encoding.UTF8.GetBytes($"{method} {path}|{body}");
        var head =
            "HTTP/1.1 200 OK\r\n" +
            "Content-Type: text/plain\r\n" +
            $"Content-Length: {bodyBytes.Length}\r\n" +
            "Connection: close\r\n\r\n";
        await ssl.WriteAsync(Encoding.ASCII.GetBytes(head), ct);
        await ssl.WriteAsync(bodyBytes, ct);
        await ssl.FlushAsync(ct);
    }

    /// <summary>A server that answers <paramref name="failures"/> requests with
    /// <paramref name="failStatus"/> before answering 200 — the only honest way to test that a retry
    /// actually retried, rather than that the code path merely ran. The response body is the 1-based
    /// attempt number, so a test can also see how many times it was really called.</summary>
    public static Task<LoopbackMtlsServer> StartFlakyAsync(
        X509Certificate2 serverCertificate, string expectedClientThumbprint,
        int failures, int failStatus = 503, string? retryAfter = null)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        // Every request arrives on its own connection (the answer says Connection: close), so the
        // only place a count of them can live is outside the handler, incremented atomically.
        int seen = 0;
        // The handler is built before the instance exists, so the count is written back through the
        // captured variable once it does.
        LoopbackMtlsServer? started = null;
        started = new LoopbackMtlsServer(listener, port,
            (client, ct) => HandleFlakyAsync(
                client, serverCertificate, expectedClientThumbprint, failures, failStatus, retryAfter,
                () =>
                {
                    int n = Interlocked.Increment(ref seen);
                    if (started is not null) started.RequestCount = n;
                    return n;
                }, ct));
        return Task.FromResult(started);
    }

    private static async Task HandleFlakyAsync(
        TcpClient client, X509Certificate2 serverCert, string expectedClientThumbprint,
        int failures, int failStatus, string? retryAfter, Func<int> countRequest, CancellationToken ct)
    {
        await using var ssl = await AuthenticateServerAsync(client, serverCert, expectedClientThumbprint, ct);

        var buffer = new byte[8192];
        var request = new StringBuilder();
        int headerEnd = -1;
        while (headerEnd < 0)
        {
            int n = await ssl.ReadAsync(buffer, ct);
            if (n == 0) break;
            request.Append(Encoding.UTF8.GetString(buffer, 0, n));
            headerEnd = request.ToString().IndexOf("\r\n\r\n", StringComparison.Ordinal);
        }
        // Drain whatever body the client sent before answering: a client still writing its request
        // when the connection closes never observes the status this server exists to send.
        int contentLength = ParseContentLength(request.ToString());
        int bodyHave = headerEnd >= 0 ? request.Length - (headerEnd + 4) : 0;
        while (bodyHave < contentLength)
        {
            int n = await ssl.ReadAsync(buffer, ct);
            if (n == 0) break;
            request.Append(Encoding.UTF8.GetString(buffer, 0, n));
            bodyHave += n;
        }

        // Counted only once the request is fully read, so the number is requests answered rather
        // than connections opened.
        int attempt = countRequest();
        bool fail = attempt <= failures;

        var bodyBytes = Encoding.UTF8.GetBytes(attempt.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var head = new StringBuilder()
            .Append(fail ? $"HTTP/1.1 {failStatus} {FailureReason(failStatus)}\r\n" : "HTTP/1.1 200 OK\r\n")
            .Append("Content-Type: text/plain\r\n")
            .Append($"Content-Length: {bodyBytes.Length}\r\n")
            .Append("Connection: close\r\n");
        if (fail && retryAfter is not null)
            head.Append("Retry-After: ").Append(retryAfter).Append("\r\n");
        head.Append("\r\n");

        await ssl.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()), ct);
        await ssl.WriteAsync(bodyBytes, ct);
        await ssl.FlushAsync(ct);
    }

    private static string FailureReason(int statusCode) => statusCode switch
    {
        404 => "Not Found",
        429 => "Too Many Requests",
        500 => "Internal Server Error",
        502 => "Bad Gateway",
        503 => "Service Unavailable",
        504 => "Gateway Timeout",
        _ => "Error"
    };

    private static string ReasonPhrase(int statusCode) => statusCode switch
    {
        301 => "Moved Permanently",
        302 => "Found",
        303 => "See Other",
        307 => "Temporary Redirect",
        308 => "Permanent Redirect",
        _ => "Found"
    };

    /// <summary>A server that sets a cookie (Set-Cookie: srv=ok) and echoes any Cookie header it
    /// received in the response body — lets a test prove a cookie jar carries cookies across calls.</summary>
    public static Task<LoopbackMtlsServer> StartCookieEchoAsync(
        X509Certificate2 serverCertificate, string expectedClientThumbprint)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = new LoopbackMtlsServer(listener, port,
            (client, ct) => HandleCookieEchoAsync(client, serverCertificate, expectedClientThumbprint, ct));
        return Task.FromResult(server);
    }

    /// <summary>A server that answers with a gzip-encoded body (Content-Encoding: gzip) — lets a test
    /// prove automatic decompression is on by default and off under --no-decompress.</summary>
    public static Task<LoopbackMtlsServer> StartGzipAsync(
        X509Certificate2 serverCertificate, string expectedClientThumbprint,
        string body = "{\"gzip\":\"ok\"}", string contentType = "application/json")
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = new LoopbackMtlsServer(listener, port,
            (client, ct) => HandleGzipAsync(
                client, serverCertificate, expectedClientThumbprint, body, contentType, ct));
        return Task.FromResult(server);
    }

    private static async Task HandleGzipAsync(
        TcpClient client, X509Certificate2 serverCert, string expectedClientThumbprint,
        string body, string contentType, CancellationToken ct)
    {
        await using var ssl = await AuthenticateServerAsync(client, serverCert, expectedClientThumbprint, ct);

        var buffer = new byte[4096];
        var request = new StringBuilder();
        while (!request.ToString().Contains("\r\n\r\n"))
        {
            int n = await ssl.ReadAsync(buffer, ct);
            if (n == 0) break;
            request.Append(Encoding.ASCII.GetString(buffer, 0, n));
        }

        var compressed = new MemoryStream();
        using (var gzip = new System.IO.Compression.GZipStream(
            compressed, System.IO.Compression.CompressionMode.Compress, leaveOpen: true))
        {
            var raw = Encoding.UTF8.GetBytes(body);
            await gzip.WriteAsync(raw, ct);
        }
        var bodyBytes = compressed.ToArray();

        var head =
            "HTTP/1.1 200 OK\r\n" +
            $"Content-Type: {contentType}\r\n" +
            "Content-Encoding: gzip\r\n" +
            $"Content-Length: {bodyBytes.Length}\r\n" +
            "Connection: close\r\n\r\n";
        await ssl.WriteAsync(Encoding.ASCII.GetBytes(head), ct);
        await ssl.WriteAsync(bodyBytes, ct);
        await ssl.FlushAsync(ct);
    }

    private static async Task HandleCookieEchoAsync(
        TcpClient client, X509Certificate2 serverCert, string expectedClientThumbprint, CancellationToken ct)
    {
        await using var ssl = await AuthenticateServerAsync(client, serverCert, expectedClientThumbprint, ct);
        var buffer = new byte[4096];
        var request = new StringBuilder();
        while (!request.ToString().Contains("\r\n\r\n"))
        {
            int n = await ssl.ReadAsync(buffer, ct);
            if (n == 0) break;
            request.Append(Encoding.ASCII.GetString(buffer, 0, n));
        }
        string received = "none";
        foreach (var line in request.ToString().Split("\r\n"))
            if (line.StartsWith("Cookie:", StringComparison.OrdinalIgnoreCase))
                received = line["Cookie:".Length..].Trim();

        var bodyBytes = Encoding.UTF8.GetBytes(received);
        var head =
            "HTTP/1.1 200 OK\r\n" +
            "Content-Type: text/plain\r\n" +
            "Set-Cookie: srv=ok; Path=/\r\n" +
            $"Content-Length: {bodyBytes.Length}\r\n" +
            "Connection: close\r\n\r\n";
        await ssl.WriteAsync(Encoding.ASCII.GetBytes(head), ct);
        await ssl.WriteAsync(bodyBytes, ct);
        await ssl.FlushAsync(ct);
    }

    private async Task AcceptLoopAsync(Func<TcpClient, CancellationToken, Task> handleClient)
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                _handlers.Add(Task.Run(async () =>
                {
                    try { await handleClient(client, _cts.Token); }
                    catch { /* handshake/read errors are expected in tests */ }
                    finally { client.Dispose(); }
                }));
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
    }

    /// <summary>Complete the server-side mTLS handshake, requiring a client cert whose thumbprint
    /// matches. A mismatch throws from AuthenticateAsServerAsync (swallowed by the accept loop).</summary>
    private static async Task<SslStream> AuthenticateServerAsync(
        TcpClient client, X509Certificate2 serverCert, string expectedClientThumbprint, CancellationToken ct,
        Action<string?>? onSni = null)
    {
        var ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: false,
            userCertificateValidationCallback: (_, cert, _, _) =>
                cert is not null &&
                cert.GetCertHashString().Equals(expectedClientThumbprint, StringComparison.OrdinalIgnoreCase));
        var options = new SslServerAuthenticationOptions
        {
            ClientCertificateRequired = true,
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
        };
        if (onSni is null)
            options.ServerCertificate = serverCert;
        else
            // The selection callback's hostName argument is the SNI name the client sent; the same
            // certificate is served either way.
            options.ServerCertificateSelectionCallback = (_, hostName) => { onSni(hostName); return serverCert; };
        await ssl.AuthenticateAsServerAsync(options, ct);
        return ssl;
    }

    /// <summary>A server that streams a fixed list of Server-Sent Events (text/event-stream) and closes.</summary>
    public static Task<LoopbackMtlsServer> StartSseAsync(
        X509Certificate2 serverCertificate, string expectedClientThumbprint,
        IReadOnlyList<(string? Event, string Data)> events)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = new LoopbackMtlsServer(listener, port,
            (client, ct) => HandleSseAsync(client, serverCertificate, expectedClientThumbprint, events, ct));
        return Task.FromResult(server);
    }

    /// <summary>A WebSocket server (over mTLS) that echoes every text/binary frame back and mirrors a close.</summary>
    public static Task<LoopbackMtlsServer> StartWebSocketEchoAsync(
        X509Certificate2 serverCertificate, string expectedClientThumbprint)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = new LoopbackMtlsServer(listener, port,
            (client, ct) => HandleWebSocketEchoAsync(client, serverCertificate, expectedClientThumbprint, ct));
        return Task.FromResult(server);
    }

    /// <summary>An OAuth 2.0 token endpoint: validates client credentials (from the body or a Basic
    /// header) and issues a JSON token whose access_token/refresh_token encode the grant type, so a
    /// test can assert the right grant flowed through. Bad credentials get a 401 error response.</summary>
    public static Task<LoopbackMtlsServer> StartOAuthTokenAsync(
        X509Certificate2 serverCertificate, string expectedClientThumbprint,
        string expectedClientId, string expectedClientSecret)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = new LoopbackMtlsServer(listener, port,
            (client, ct) => HandleOAuthTokenAsync(
                client, serverCertificate, expectedClientThumbprint, expectedClientId, expectedClientSecret, ct));
        return Task.FromResult(server);
    }

    private static async Task HandleOAuthTokenAsync(
        TcpClient client, X509Certificate2 serverCert, string expectedClientThumbprint,
        string expectedClientId, string expectedClientSecret, CancellationToken ct)
    {
        await using var ssl = await AuthenticateServerAsync(client, serverCert, expectedClientThumbprint, ct);

        var buffer = new byte[8192];
        var sb = new StringBuilder();
        int headerEnd = -1;
        while (headerEnd < 0)
        {
            int n = await ssl.ReadAsync(buffer, ct);
            if (n == 0) break;
            sb.Append(Encoding.UTF8.GetString(buffer, 0, n));
            headerEnd = sb.ToString().IndexOf("\r\n\r\n", StringComparison.Ordinal);
        }
        string text = sb.ToString();
        int contentLength = ParseContentLength(text);
        int bodyHave = headerEnd >= 0 ? text.Length - (headerEnd + 4) : 0;
        while (bodyHave < contentLength)
        {
            int n = await ssl.ReadAsync(buffer, ct);
            if (n == 0) break;
            sb.Append(Encoding.UTF8.GetString(buffer, 0, n));
            bodyHave += n;
        }
        text = sb.ToString();
        string headerPart = headerEnd >= 0 ? text[..headerEnd] : text;
        string bodyPart = headerEnd >= 0 ? text[(headerEnd + 4)..] : "";

        var form = ParseForm(bodyPart);
        form.TryGetValue("client_id", out var cid);
        form.TryGetValue("client_secret", out var csecret);

        // client_secret_basic: credentials arrive in the Authorization header instead of the body.
        foreach (var line in headerPart.Split("\r\n"))
        {
            if (line.StartsWith("Authorization: Basic ", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    string decoded = Encoding.UTF8.GetString(Convert.FromBase64String(line["Authorization: Basic ".Length..].Trim()));
                    int colon = decoded.IndexOf(':');
                    if (colon >= 0) { cid = decoded[..colon]; csecret = decoded[(colon + 1)..]; }
                }
                catch (FormatException) { /* leave creds as they were */ }
            }
        }

        bool ok = cid == expectedClientId && csecret == expectedClientSecret;
        form.TryGetValue("grant_type", out var grant);
        form.TryGetValue("scope", out var scope);
        grant ??= "";

        string json = ok
            ? $"{{\"access_token\":\"at-{grant}\",\"token_type\":\"Bearer\",\"expires_in\":3600," +
              $"\"refresh_token\":\"rt-{grant}\",\"scope\":\"{scope ?? ""}\"}}"
            : "{\"error\":\"invalid_client\",\"error_description\":\"bad client credentials\"}";
        var bodyBytes = Encoding.UTF8.GetBytes(json);
        string head =
            $"HTTP/1.1 {(ok ? "200 OK" : "401 Unauthorized")}\r\n" +
            "Content-Type: application/json\r\n" +
            $"Content-Length: {bodyBytes.Length}\r\n" +
            "Connection: close\r\n\r\n";
        await ssl.WriteAsync(Encoding.ASCII.GetBytes(head), ct);
        await ssl.WriteAsync(bodyBytes, ct);
        await ssl.FlushAsync(ct);
    }

    private static Dictionary<string, string> ParseForm(string body)
    {
        var form = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in body.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = pair.IndexOf('=');
            string key = eq >= 0 ? pair[..eq] : pair;
            string val = eq >= 0 ? pair[(eq + 1)..] : "";
            form[Uri.UnescapeDataString(key.Replace('+', ' '))] = Uri.UnescapeDataString(val.Replace('+', ' '));
        }
        return form;
    }

    private static async Task HandleSseAsync(
        TcpClient client, X509Certificate2 serverCert, string expectedClientThumbprint,
        IReadOnlyList<(string? Event, string Data)> events, CancellationToken ct)
    {
        await using var ssl = await AuthenticateServerAsync(client, serverCert, expectedClientThumbprint, ct);

        var buffer = new byte[4096];
        var request = new StringBuilder();
        while (request.ToString().IndexOf("\r\n\r\n", StringComparison.Ordinal) < 0)
        {
            int n = await ssl.ReadAsync(buffer, ct);
            if (n == 0) return;
            request.Append(Encoding.ASCII.GetString(buffer, 0, n));
        }

        var head =
            "HTTP/1.1 200 OK\r\n" +
            "Content-Type: text/event-stream\r\n" +
            "Cache-Control: no-cache\r\n" +
            "Connection: close\r\n\r\n";
        await ssl.WriteAsync(Encoding.ASCII.GetBytes(head), ct);
        await ssl.FlushAsync(ct);

        foreach (var (ev, data) in events)
        {
            var sb = new StringBuilder();
            if (ev is not null) sb.Append("event: ").Append(ev).Append('\n');
            foreach (var dataLine in data.Split('\n')) sb.Append("data: ").Append(dataLine).Append('\n');
            sb.Append('\n');   // blank line dispatches the event
            await ssl.WriteAsync(Encoding.UTF8.GetBytes(sb.ToString()), ct);
            await ssl.FlushAsync(ct);
        }
    }

    private sealed record WsFrame(int Opcode, byte[] Payload);

    private static async Task HandleWebSocketEchoAsync(
        TcpClient client, X509Certificate2 serverCert, string expectedClientThumbprint, CancellationToken ct)
    {
        await using var ssl = await AuthenticateServerAsync(client, serverCert, expectedClientThumbprint, ct);

        // --- HTTP upgrade handshake ---
        var buffer = new byte[8192];
        var request = new StringBuilder();
        while (request.ToString().IndexOf("\r\n\r\n", StringComparison.Ordinal) < 0)
        {
            int n = await ssl.ReadAsync(buffer, ct);
            if (n == 0) return;
            request.Append(Encoding.ASCII.GetString(buffer, 0, n));
        }
        string? key = null;
        string? subProtocol = null;
        foreach (var line in request.ToString().Split("\r\n"))
        {
            if (line.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase))
                key = line["Sec-WebSocket-Key:".Length..].Trim();
            // A client that offers subprotocols must be told which one was chosen — ClientWebSocket
            // fails the connection outright when a 101 confirms none — so the first is taken.
            else if (line.StartsWith("Sec-WebSocket-Protocol:", StringComparison.OrdinalIgnoreCase))
                subProtocol = line["Sec-WebSocket-Protocol:".Length..].Split(',')[0].Trim();
        }
        if (key is null) return;

        string accept = Convert.ToBase64String(System.Security.Cryptography.SHA1.HashData(
            Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
        string handshake =
            "HTTP/1.1 101 Switching Protocols\r\n" +
            "Upgrade: websocket\r\n" +
            "Connection: Upgrade\r\n" +
            $"Sec-WebSocket-Accept: {accept}\r\n" +
            (string.IsNullOrEmpty(subProtocol) ? "" : $"Sec-WebSocket-Protocol: {subProtocol}\r\n") +
            "\r\n";
        await ssl.WriteAsync(Encoding.ASCII.GetBytes(handshake), ct);
        await ssl.FlushAsync(ct);

        // --- frame loop: echo text/binary, mirror a close ---
        while (!ct.IsCancellationRequested)
        {
            var frame = await ReadFrameAsync(ssl, ct);
            if (frame is null) return;                       // connection closed abruptly
            if (frame.Opcode == 0x8)                          // close
            {
                await ssl.WriteAsync(BuildFrame(0x8, frame.Payload), ct);
                await ssl.FlushAsync(ct);
                return;
            }
            if (frame.Opcode is 0x1 or 0x2)                   // text or binary
            {
                await ssl.WriteAsync(BuildFrame(frame.Opcode, frame.Payload), ct);
                await ssl.FlushAsync(ct);
            }
            // ping/pong (0x9 / 0xA) are ignored by this test server
        }
    }

    private static async Task<WsFrame?> ReadFrameAsync(SslStream ssl, CancellationToken ct)
    {
        var h = new byte[2];
        if (!await ReadExactAsync(ssl, h, 2, ct)) return null;
        int opcode = h[0] & 0x0F;
        bool masked = (h[1] & 0x80) != 0;
        long len = h[1] & 0x7F;
        if (len == 126)
        {
            var ext = new byte[2];
            if (!await ReadExactAsync(ssl, ext, 2, ct)) return null;
            len = (ext[0] << 8) | ext[1];
        }
        else if (len == 127)
        {
            var ext = new byte[8];
            if (!await ReadExactAsync(ssl, ext, 8, ct)) return null;
            len = 0; for (int i = 0; i < 8; i++) len = (len << 8) | ext[i];
        }
        byte[] mask = Array.Empty<byte>();
        if (masked)
        {
            mask = new byte[4];
            if (!await ReadExactAsync(ssl, mask, 4, ct)) return null;
        }
        var payload = new byte[len];
        if (len > 0 && !await ReadExactAsync(ssl, payload, (int)len, ct)) return null;
        if (masked) for (int i = 0; i < payload.Length; i++) payload[i] ^= mask[i % 4];
        return new WsFrame(opcode, payload);
    }

    private static async Task<bool> ReadExactAsync(SslStream ssl, byte[] buf, int count, CancellationToken ct)
    {
        int off = 0;
        while (off < count)
        {
            int n = await ssl.ReadAsync(buf.AsMemory(off, count - off), ct);
            if (n == 0) return false;
            off += n;
        }
        return true;
    }

    private static byte[] BuildFrame(int opcode, byte[] payload)
    {
        // Server-to-client frames are never masked.
        var header = new List<byte> { (byte)(0x80 | opcode) };
        if (payload.Length < 126) header.Add((byte)payload.Length);
        else if (payload.Length <= 0xFFFF)
        {
            header.Add(126);
            header.Add((byte)(payload.Length >> 8));
            header.Add((byte)(payload.Length & 0xFF));
        }
        else
        {
            header.Add(127);
            for (int i = 7; i >= 0; i--) header.Add((byte)(((long)payload.Length >> (8 * i)) & 0xFF));
        }
        var frame = new byte[header.Count + payload.Length];
        header.CopyTo(frame);
        Array.Copy(payload, 0, frame, header.Count, payload.Length);
        return frame;
    }

    private static async Task HandleClientAsync(
        TcpClient client, X509Certificate2 serverCert, string expectedClientThumbprint,
        string body, string contentType, CancellationToken ct)
    {
        await using var ssl = await AuthenticateServerAsync(client, serverCert, expectedClientThumbprint, ct);

        // Read request headers (until blank line). Body of request is ignored.
        var buffer = new byte[4096];
        var request = new StringBuilder();
        while (!request.ToString().Contains("\r\n\r\n"))
        {
            int n = await ssl.ReadAsync(buffer, ct);
            if (n == 0) break;
            request.Append(Encoding.ASCII.GetString(buffer, 0, n));
        }

        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var header =
            "HTTP/1.1 200 OK\r\n" +
            $"Content-Type: {contentType}\r\n" +
            $"Content-Length: {bodyBytes.Length}\r\n" +
            "Connection: close\r\n\r\n";
        await ssl.WriteAsync(Encoding.ASCII.GetBytes(header), ct);
        await ssl.WriteAsync(bodyBytes, ct);
        await ssl.FlushAsync(ct);
    }

    /// <summary>A server that never asks for a client certificate during the handshake and answers
    /// every request with <paramref name="status"/> — the shape of a server that requires the
    /// certificate only for some paths and refuses when it was not renegotiated.</summary>
    public static Task<LoopbackMtlsServer> StartNoClientCertStatusAsync(X509Certificate2 serverCertificate, int status)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = new LoopbackMtlsServer(listener, port, async (client, ct) =>
        {
            await using var ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
            await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ClientCertificateRequired = false,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                ServerCertificate = serverCertificate
            }, ct);
            var buffer = new byte[4096];
            var request = new StringBuilder();
            while (!request.ToString().Contains("\r\n\r\n"))
            {
                int n = await ssl.ReadAsync(buffer, ct);
                if (n == 0) break;
                request.Append(Encoding.ASCII.GetString(buffer, 0, n));
            }
            var body = Encoding.UTF8.GetBytes("{\"refused\":true}");
            string head = $"HTTP/1.1 {status} {(status == 403 ? "Forbidden" : status == 401 ? "Unauthorized" : "Status")}\r\n" +
                          $"Content-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n";
            await ssl.WriteAsync(Encoding.ASCII.GetBytes(head), ct);
            await ssl.WriteAsync(body, ct);
            await ssl.FlushAsync(ct);
        });
        return Task.FromResult(server);
    }

    private static async Task HandleWithHeadersAsync(
        TcpClient client, X509Certificate2 serverCert, string expectedClientThumbprint,
        IReadOnlyList<KeyValuePair<string, string>> extraHeaders,
        string body, string contentType, int status, CancellationToken ct)
    {
        await using var ssl = await AuthenticateServerAsync(client, serverCert, expectedClientThumbprint, ct);

        // Read request headers (until blank line). Body of request is ignored.
        var buffer = new byte[4096];
        var request = new StringBuilder();
        while (!request.ToString().Contains("\r\n\r\n"))
        {
            int n = await ssl.ReadAsync(buffer, ct);
            if (n == 0) break;
            request.Append(Encoding.ASCII.GetString(buffer, 0, n));
        }

        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var head = new StringBuilder()
            .Append($"HTTP/1.1 {status} {(status == 200 ? "OK" : status == 401 ? "Unauthorized" : "Status")}\r\n")
            .Append($"Content-Type: {contentType}\r\n")
            .Append($"Content-Length: {bodyBytes.Length}\r\n")
            .Append("Connection: close\r\n");
        // One line per extra header in the order given, repeated names included: a header that may
        // legitimately appear twice — Set-Cookie above all — has to reach the client as two headers,
        // because collapsing it would hide the very duplication a browser-facing test is checking.
        foreach (var (name, value) in extraHeaders)
            head.Append(name).Append(": ").Append(value).Append("\r\n");
        head.Append("\r\n");

        await ssl.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()), ct);
        await ssl.WriteAsync(bodyBytes, ct);
        await ssl.FlushAsync(ct);
    }

    private static async Task HandleEchoAsync(
        TcpClient client, X509Certificate2 serverCertificate, string expectedClientThumbprint, CancellationToken ct,
        Action<string?>? onSni = null)
    {
        await using var ssl = await AuthenticateServerAsync(
            client, serverCertificate, expectedClientThumbprint, ct, onSni);

        var buffer = new byte[8192];
        var request = new StringBuilder();
        int headerEnd = -1;
        while (headerEnd < 0)
        {
            int n = await ssl.ReadAsync(buffer, ct);
            if (n == 0) break;
            request.Append(Encoding.UTF8.GetString(buffer, 0, n));
            headerEnd = request.ToString().IndexOf("\r\n\r\n", StringComparison.Ordinal);
        }
        int contentLength = ParseContentLength(request.ToString());
        int bodyHave = headerEnd >= 0 ? request.Length - (headerEnd + 4) : 0;
        while (bodyHave < contentLength)
        {
            int n = await ssl.ReadAsync(buffer, ct);
            if (n == 0) break;
            request.Append(Encoding.UTF8.GetString(buffer, 0, n));
            bodyHave += n;
        }

        var bodyBytes = Encoding.UTF8.GetBytes(request.ToString());
        var head =
            "HTTP/1.1 200 OK\r\n" +
            "Content-Type: text/plain\r\n" +
            $"Content-Length: {bodyBytes.Length}\r\n" +
            "Connection: close\r\n\r\n";
        await ssl.WriteAsync(Encoding.UTF8.GetBytes(head), ct);
        await ssl.WriteAsync(bodyBytes, ct);
        await ssl.FlushAsync(ct);
    }

    private static async Task HandleRedirectAsync(
        TcpClient client, X509Certificate2 serverCertificate, string expectedClientThumbprint,
        string location, CancellationToken ct)
    {
        await using var ssl = await AuthenticateServerAsync(client, serverCertificate, expectedClientThumbprint, ct);

        var buffer = new byte[4096];
        var request = new StringBuilder();
        while (!request.ToString().Contains("\r\n\r\n"))
        {
            int n = await ssl.ReadAsync(buffer, ct);
            if (n == 0) break;
            request.Append(Encoding.ASCII.GetString(buffer, 0, n));
        }
        var head =
            "HTTP/1.1 302 Found\r\n" +
            $"Location: {location}\r\n" +
            "Content-Length: 0\r\n" +
            "Connection: close\r\n\r\n";
        await ssl.WriteAsync(Encoding.ASCII.GetBytes(head), ct);
        await ssl.FlushAsync(ct);
    }

    /// <summary>A server that keeps the connection open (HTTP/1.1 keep-alive) and records what it saw
    /// per connection: how many TLS handshakes it completed, which client certificate was presented on
    /// each, and how many requests arrived. "The server completed one handshake for N requests" is the
    /// only honest — and the only non-timing — way to prove connection pooling.</summary>
    /// <param name="requireClientCertificate">When true the handshake demands a client certificate;
    /// whichever one is presented is accepted and recorded, so one server can observe two different
    /// client certificates arriving on two different connections.</param>
    public static Task<LoopbackMtlsServer> StartKeepAliveAsync(
        X509Certificate2 serverCertificate,
        bool requireClientCertificate = true)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        // The handler is built before the instance exists, so the per-connection ordinal and the
        // per-request counters it hands out are written back through the captured variable once the
        // instance exists — the same trick StartFlakyAsync uses for its single counter.
        LoopbackMtlsServer? started = null;
        started = new LoopbackMtlsServer(listener, port,
            (client, ct) => HandleKeepAliveAsync(
                client, serverCertificate, requireClientCertificate,
                thumbprint => started!.RecordKeepAliveHandshake(thumbprint),
                () => started!.RecordKeepAliveRequest(),
                ct));
        return Task.FromResult(started);
    }

    /// <summary>Assigns the next 1-based connection ordinal and records the thumbprint presented on it.
    /// Called only after AuthenticateAsServerAsync returns, so a failed handshake never consumes an
    /// ordinal or appears in ClientCertificateThumbprintsByConnection.</summary>
    private int RecordKeepAliveHandshake(string? clientCertificateThumbprint)
    {
        int ordinal = Interlocked.Increment(ref _keepAliveConnectionCount);
        _keepAliveThumbprintsByConnection[ordinal] = clientCertificateThumbprint;
        return ordinal;
    }

    /// <summary>Bumps TotalRequestCount for a request answered on a keep-alive connection.</summary>
    private int RecordKeepAliveRequest() => Interlocked.Increment(ref _keepAliveTotalRequestCount);

    private static async Task HandleKeepAliveAsync(
        TcpClient client, X509Certificate2 serverCert, bool requireClientCertificate,
        Func<string?, int> recordHandshake, Func<int> recordRequest, CancellationToken ct)
    {
        // The validation callback fires during the handshake below and is the only place the
        // presented certificate (or its absence) can be observed; accepting unconditionally is what
        // lets one server see two different client certificates on two different connections rather
        // than rejecting the second.
        string? clientCertThumbprint = null;
        await using var ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: false,
            userCertificateValidationCallback: (_, cert, _, _) =>
            {
                clientCertThumbprint = cert?.GetCertHashString();
                return true;
            });
        var options = new SslServerAuthenticationOptions
        {
            ClientCertificateRequired = requireClientCertificate,
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            ServerCertificate = serverCert
        };
        await ssl.AuthenticateAsServerAsync(options, ct);
        // Recorded only now — after the handshake completes — never before.
        int connectionOrdinal = recordHandshake(clientCertThumbprint);

        // Raw bytes, not the StringBuilder-of-UTF8 pattern the other handlers use: keep-alive requires
        // knowing exactly where one request ends and the next begins, and an incremental UTF-8 decode
        // can split a multi-byte character across two reads. Latin1 is byte-preserving for the header
        // block, which is what lets the split be found and re-sliced safely.
        var pending = new List<byte>();
        var readBuffer = new byte[8192];
        int requestOrdinalOnThisConnection = 0;

        while (!ct.IsCancellationRequested)
        {
            int headerEnd = FindHeaderEnd(pending);
            int contentLength = 0;
            while (true)
            {
                if (headerEnd >= 0)
                {
                    contentLength = ParseContentLength(
                        Encoding.Latin1.GetString(pending.GetRange(0, headerEnd).ToArray()));
                    if (pending.Count >= headerEnd + 4 + contentLength) break;
                }
                int n = await ssl.ReadAsync(readBuffer, ct);
                if (n == 0) return;   // client closed the connection — not an error, just done
                for (int i = 0; i < n; i++) pending.Add(readBuffer[i]);
                if (headerEnd < 0) headerEnd = FindHeaderEnd(pending);
            }

            int totalLen = headerEnd + 4 + contentLength;
            string headerText = Encoding.Latin1.GetString(pending.GetRange(0, headerEnd).ToArray());
            string requestLine = headerText.Split("\r\n")[0];
            var requestLineParts = requestLine.Split(' ');
            string path = requestLineParts.Length > 1 ? requestLineParts[1] : "/";
            string cookie = "none";
            foreach (var line in headerText.Split("\r\n"))
                if (line.StartsWith("Cookie:", StringComparison.OrdinalIgnoreCase))
                    cookie = line["Cookie:".Length..].Trim();

            requestOrdinalOnThisConnection++;
            recordRequest();

            string bodyText =
                $"conn={connectionOrdinal} req={requestOrdinalOnThisConnection} " +
                $"cert={clientCertThumbprint ?? "none"} cookie={cookie} path={path}";
            var bodyBytes = Encoding.UTF8.GetBytes(bodyText);
            var head =
                "HTTP/1.1 200 OK\r\n" +
                "Content-Type: text/plain\r\n" +
                "Set-Cookie: srv=ok; Path=/\r\n" +
                $"Content-Length: {bodyBytes.Length}\r\n\r\n";
            // Deliberately no Connection: close — HTTP/1.1 keep-alive is the entire point of this
            // server, and is what every other handler in this file opts out of.
            await ssl.WriteAsync(Encoding.ASCII.GetBytes(head), ct);
            await ssl.WriteAsync(bodyBytes, ct);
            await ssl.FlushAsync(ct);

            // Carry over whatever bytes of the next request already arrived in this same read.
            pending.RemoveRange(0, totalLen);
        }
    }

    /// <summary>Finds the byte offset of the blank-line sequence (\r\n\r\n) that ends an HTTP header
    /// block, or -1 if it has not arrived yet. Scanning raw bytes (rather than a decoded string) is
    /// what makes it safe to call before a multi-byte body has fully arrived.</summary>
    private static int FindHeaderEnd(List<byte> data)
    {
        for (int i = 0; i + 3 < data.Count; i++)
            if (data[i] == (byte)'\r' && data[i + 1] == (byte)'\n' &&
                data[i + 2] == (byte)'\r' && data[i + 3] == (byte)'\n')
                return i;
        return -1;
    }

    private static int ParseContentLength(string requestText)
    {
        foreach (var line in requestText.Split("\r\n"))
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(line["Content-Length:".Length..].Trim(), out var len))
                return len;
        return 0;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _cts.Cancel();
        _listener.Stop();
        try { await _acceptLoop; } catch { /* ignore */ }
        try { await Task.WhenAll(_handlers.ToArray()); } catch { /* ignore */ }
        _cts.Dispose();
    }
}
