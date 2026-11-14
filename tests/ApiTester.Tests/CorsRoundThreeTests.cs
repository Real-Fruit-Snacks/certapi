using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using ApiTester.Cli;
using ApiTester.Core;

namespace ApiTester.Tests;

/// <summary>CORS, third round: `cors --from-har`, `run --origin`, `cors --suggest`,
/// `--preflight-without-cert`, and the WebSocket Origin check and audit.</summary>
public class CorsRoundThreeTests
{
    private static string TempState() => Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");

    private static (int Code, string Out, string Err) Cli(params string[] args) => CliWith(new CliServices { LiveStatePath = TempState(), UserConfigPath = null }, args);

    private static (int Code, string Out, string Err) CliWith(CliServices services, params string[] args)
    {
        var so = new StringWriter();
        var se = new StringWriter();
        int code = CliApp.Run(args.Append("--no-config").ToArray(), new StringReader(""), so, se, new MemoryStream(), services);
        return (code, so.ToString(), se.ToString());
    }

    private static HarEntry Entry(string method, string url, string[] requestHeaders, int status, string[] responseHeaders, int second) => new()
    {
        StartedDateTime = new DateTimeOffset(2026, 11, 14, 10, 0, second, TimeSpan.Zero),
        Request = new HarRequest { Method = method, Url = url, Headers = requestHeaders.Select(Nv).ToList() },
        Response = new HarResponse { Status = status, Headers = responseHeaders.Select(Nv).ToList() }
    };

    private static HarNameValue Nv(string line) { int c = line.IndexOf(':', 1); return new HarNameValue(line[..c].Trim(), line[(c + 1)..].Trim()); }

    private static Har Capture() => new()
    {
        Log = new HarLog
        {
            Entries =
            {
                Entry("GET", "https://api.example/list", new[] { "Origin: https://app.example", "Cookie: s=1" }, 200,
                    new[] { "Access-Control-Allow-Origin: https://app.example", "Access-Control-Allow-Credentials: true" }, 1),
                Entry("OPTIONS", "https://api.example/item", new[] { "Origin: https://app.example", "Access-Control-Request-Method: PUT", "Access-Control-Request-Headers: x-api-key" }, 204,
                    new[] { "Access-Control-Allow-Origin: https://app.example", "Access-Control-Allow-Methods: PUT" }, 2),
                Entry("PUT", "https://api.example/item", new[] { "Origin: https://app.example", "X-Api-Key: k" }, 0, Array.Empty<string>(), 3),
                Entry("GET", "https://app.example/page", new[] { "Origin: https://app.example" }, 200, Array.Empty<string>(), 4),
                Entry("GET", "https://cdn.example/lib.js", Array.Empty<string>(), 200, Array.Empty<string>(), 5),
                Entry("GET", "https://api.example/star", new[] { ":authority: api.example", "Origin: https://app.example" }, 200, new[] { "Access-Control-Allow-Origin: *" }, 6)
            }
        }
    };

    // ---- --from-har ----

    [Fact]
    public void A_capture_is_judged_request_by_request_from_its_recorded_headers()
    {
        var rows = CorsHar.Check(Capture(), null, credentials: false);
        Assert.Equal(3, rows.Count);   // the preflight itself, the same-origin page and the plain script are not cross-origin requests
        var list = rows.Single(r => r.Url.EndsWith("/list"));
        Assert.True(list.Allowed);
        Assert.True(list.Credentials);   // it carried a cookie
        var put = rows.Single(r => r.Method == "PUT");
        Assert.False(put.Allowed);
        Assert.True(put.PreflightRecorded);
        Assert.Contains(put.Problems, p => p.Contains("x-api-key"));
        Assert.True(rows.Single(r => r.Url.EndsWith("/star")).Allowed);
    }

    [Fact]
    public void An_origin_given_judges_every_request_as_if_from_that_page()
    {
        var rows = CorsHar.Check(Capture(), "https://other.example", credentials: true);
        Assert.Contains(rows, r => r.Url.EndsWith("/page") && !r.Allowed);   // now cross-origin
        Assert.Contains(rows, r => r.Url.EndsWith("/star") && r.Problems.Any(p => p.Contains("credentials")));
    }

    [Fact]
    public async Task Cors_from_har_prints_each_request_and_live_rechecks_against_the_server()
    {
        await using var mock = MockServer.Start(0, MockTlsMode.Http, cors: new MockCors(new[] { "https://app.example" }));
        var har = new Har { Log = new HarLog { Entries =
        {
            Entry("GET", mock.BaseUrl + "orders", new[] { "Origin: https://app.example" }, 200, Array.Empty<string>(), 1)
        } } };
        string file = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".har");
        File.WriteAllText(file, HarWriter.Write(har.Log.Entries, "test"));
        try
        {
            var recorded = Cli("cors", "--from-har", file);
            Assert.Equal(1, recorded.Code);   // the recorded response carried no CORS headers
            Assert.Contains("BLOCKED", recorded.Out);
            Assert.Contains("0 of 1 cross-origin request(s) allowed", recorded.Out);

            var live = Cli("cors", "--from-har", file, "--live", "--json");
            var j = JsonDocument.Parse(live.Out).RootElement;
            Assert.True(j.GetProperty("live")[0].GetProperty("allowed").GetBoolean(), live.Out);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void From_har_refuses_a_url_and_options_it_does_not_use()
    {
        Assert.Equal(2, Cli("cors", "--from-har", "x.har", "http://127.0.0.1:9/").Code);
        Assert.Equal(2, Cli("cors", "--from-har", "x.har", "--audit").Code);
        Assert.Equal(2, Cli("cors", "http://127.0.0.1:9/", "--origin", "https://a.example", "--live").Code);
        Assert.Equal(3, Cli("cors", "--from-har", Path.Combine(Path.GetTempPath(), "no-such-capture.har")).Code);
    }

    // ---- run --origin ----

    [Fact]
    public async Task Run_origin_adds_a_cors_check_to_every_request_and_leaves_the_workspace_unchanged()
    {
        await using var allowing = MockServer.Start(0, MockTlsMode.Http, cors: new MockCors(new[] { "https://app.example" }));
        await using var plain = new RawHttpServer(_ => RawHttpServer.Response(200, "OK", "{}"));
        var state = new AppState();
        var folder = new CollectionNode { Name = "suite", IsFolder = true };
        folder.Children.Add(new CollectionNode { Name = "cors-ok", Request = new RequestModel { Method = "GET", Path = allowing.BaseUrl + "a" } });
        folder.Children.Add(new CollectionNode { Name = "no-cors", Request = new RequestModel { Method = "GET", Path = plain.BaseUrl + "/b" } });
        state.Collections.Add(folder);
        string workspace = Path.Combine(Path.GetTempPath(), $"certapi-cors-run-{Guid.NewGuid():N}.json");
        state.SaveTo(workspace);
        string before = File.ReadAllText(workspace);
        string junit = Path.GetTempFileName();
        try
        {
            var r = Cli("run", "suite", "--workspace", workspace, "--origin", "https://app.example", "--cors-credentials", "--record", "--junit", junit);
            Assert.Equal(1, r.Code);
            Assert.Contains("CORS for https://app.example", r.Out + r.Err);
            Assert.Equal("https://app.example", plain.Requests.Last().Header("Origin"));
            Assert.Contains("CORS for https://app.example == credentials", File.ReadAllText(junit));
            Assert.DoesNotContain("\"Target\": 5", File.ReadAllText(workspace));
            Assert.Equal(before.Contains("Assertions"), File.ReadAllText(workspace).Contains("Assertions"));
        }
        finally { File.Delete(workspace); File.Delete(junit); }
    }

    [Fact]
    public void Run_cors_credentials_needs_an_origin() =>
        Assert.Equal(2, Cli("run", "--all", "--cors-credentials").Code);

    // ---- --suggest ----

    [Fact]
    public void Suggestions_grant_exactly_what_was_checked()
    {
        var needs = new CorsNeeds(new[] { "https://app.example" }, new[] { "PUT" }, new[] { "x-api-key" }, new[] { "X-Total" }, true, true);
        string all = CorsSuggest.For(needs);
        Assert.Contains(".WithOrigins(\"https://app.example\")", all);
        Assert.Contains(".WithHeaders(\"x-api-key\")", all);
        Assert.Contains(".AllowCredentials()", all);
        Assert.Contains("add_header Access-Control-Allow-Private-Network \"true\" always;", all);
        Assert.Contains("allowedHeaders: ['x-api-key']", all);
        Assert.Contains(".allowPrivateNetwork(true)", all);
        Assert.Contains("<add origin=\"https://app.example\" allowCredentials=\"true\" maxAge=\"600\">", all);
        Assert.Contains("\"AllowCredentials\": true", all);
        Assert.Contains("--preflight-without-cert", all);
        string nginx = CorsSuggest.For(needs, "nginx");
        Assert.Contains("nginx", nginx);
        Assert.DoesNotContain("ASP.NET", nginx);
        var several = CorsSuggest.For(needs with { Origins = new[] { "https://a.example", "https://b.example" } }, "nginx");
        Assert.Contains("map $http_origin $cors_origin", several);
    }

    [Fact]
    public async Task Cors_suggest_prints_the_fix_after_the_check()
    {
        await using var mock = MockServer.Start(0, MockTlsMode.Http);
        var r = Cli("cors", mock.BaseUrl + "x", "--origin", "https://app.example", "-X", "PUT", "-H", "X-Api-Key: k", "--suggest", "aspnet");
        Assert.Contains("needs      origin https://app.example; methods PUT; headers x-api-key", r.Out);
        Assert.Contains("builder.Services.AddCors", r.Out);
        Assert.Equal(2, Cli("cors", mock.BaseUrl + "x", "--origin", "https://app.example", "--suggest", "rails").Code);
    }

    // ---- --preflight-without-cert ----

    [Fact]
    public async Task A_server_that_demands_the_certificate_in_the_handshake_refuses_a_certificateless_preflight()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CORS CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        using var client = SelfSignedCertificateFactory.CreateSignedCertificate("Page User", ca, false, true);
        string pfx = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".pfx");
        File.WriteAllBytes(pfx, client.Export(X509ContentType.Pfx, "pw"));
        await using var mock = MockServer.Start(0, MockTlsMode.Mtls, serverCert, cors: new MockCors(null));
        try
        {
            var withCert = Cli("cors", mock.BaseUrl + "x", "--origin", "https://app.example", "-X", "PUT", "--cert-file", pfx, "--cert-password", "pw", "--insecure");
            Assert.True(withCert.Code == 0, withCert.Out);
            var without = Cli("cors", mock.BaseUrl + "x", "--origin", "https://app.example", "-X", "PUT", "--cert-file", pfx, "--cert-password", "pw", "--insecure", "--preflight-without-cert");
            Assert.Equal(1, without.Code);
            Assert.Contains("the preflight got no answer", without.Out);
        }
        finally { File.Delete(pfx); }
    }

    // ---- WebSockets ----

    /// <summary>A WebSocket endpoint that accepts the handshake only from the origins it lists.</summary>
    private sealed class OriginCheckingSocket : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _cts = new();
        public List<string?> Origins { get; } = new();

        public OriginCheckingSocket(Func<string?, bool> accept)
        {
            _listener.Start();
            _ = Task.Run(async () =>
            {
                while (!_cts.IsCancellationRequested)
                {
                    TcpClient c;
                    try { c = await _listener.AcceptTcpClientAsync(_cts.Token); } catch { return; }
                    _ = Task.Run(async () =>
                    {
                        using (c)
                        {
                            var s = c.GetStream();
                            var buf = new List<byte>();
                            var one = new byte[1];
                            while (!(buf.Count >= 4 && buf[^4] == 13 && buf[^3] == 10 && buf[^2] == 13 && buf[^1] == 10) && await s.ReadAsync(one) == 1) buf.Add(one[0]);
                            var lines = Encoding.ASCII.GetString(buf.ToArray()).Split("\r\n");
                            string? Get(string n) => lines.FirstOrDefault(l => l.StartsWith(n + ":", StringComparison.OrdinalIgnoreCase))?[(n.Length + 1)..].Trim();
                            string? origin = Get("Origin");
                            lock (Origins) Origins.Add(origin);
                            if (!accept(origin))
                            {
                                await s.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"));
                                return;
                            }
                            string key = Get("Sec-WebSocket-Key")!;
                            string acceptKey = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
                            await s.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {acceptKey}\r\n\r\n"));
                            try { _ = await s.ReadAsync(new byte[256]); } catch { }   // the client's close frame
                        }
                    });
                }
            });
        }

        public string Url => $"ws://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/socket";

        public ValueTask DisposeAsync() { _cts.Cancel(); _listener.Stop(); return ValueTask.CompletedTask; }
    }

    [Fact]
    public async Task A_websocket_origin_check_reports_accepted_and_refused_handshakes()
    {
        await using var socket = new OriginCheckingSocket(o => o == "https://app.example");
        var ok = Cli("cors", socket.Url, "--origin", "https://app.example");
        Assert.True(ok.Code == 0, ok.Out);
        Assert.Contains("accepted", ok.Out);
        Assert.Contains("WebSockets ignore CORS", ok.Out);
        var refused = Cli("cors", socket.Url, "--origin", "https://evil.example", "--json");
        Assert.Equal(1, refused.Code);
        var probe = JsonDocument.Parse(refused.Out).RootElement.GetProperty("probes")[0];
        Assert.False(probe.GetProperty("accepted").GetBoolean());
        Assert.Equal(403, probe.GetProperty("status").GetInt32());
    }

    [Fact]
    public async Task The_websocket_audit_flags_a_socket_any_origin_can_open()
    {
        await using var open = new OriginCheckingSocket(_ => true);
        var r = Cli("cors", open.Url, "--audit", "--origin", "https://app.example", "--json");
        Assert.Equal(1, r.Code);
        var j = JsonDocument.Parse(r.Out).RootElement;
        Assert.Equal("high", j.GetProperty("worst").GetString());
        Assert.Contains(j.GetProperty("findings").EnumerateArray(), f => f.GetProperty("title").GetString() == "Any origin may open the socket");
        Assert.Contains("null", open.Origins);

        await using var strict = new OriginCheckingSocket(o => o == "https://app.example");
        var ok = Cli("cors", strict.Url, "--audit", "--origin", "https://app.example");
        Assert.True(ok.Code == 0, ok.Out);
        Assert.Contains("findings   none", ok.Out);
    }

    [Fact]
    public void Websocket_urls_refuse_the_http_only_options() =>
        Assert.Equal(2, Cli("cors", "wss://x.example/s", "--origin", "https://a.example", "-X", "PUT").Code);

    [Fact]
    public async Task Ws_origin_sends_the_header_and_explains_a_refusal()
    {
        await using var socket = new OriginCheckingSocket(o => o == "https://app.example");
        var ok = Cli("ws", socket.Url, "--origin", "https://app.example", "--expect", "0");
        Assert.True(ok.Code == 0, ok.Err);
        Assert.Contains("https://app.example", socket.Origins);
        var refused = Cli("ws", socket.Url, "--origin", "https://evil.example", "--expect", "0");
        Assert.Equal(1, refused.Code);
        Assert.Contains("may not accept the Origin", refused.Err);
    }
}
