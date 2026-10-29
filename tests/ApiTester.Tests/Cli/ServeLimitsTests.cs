using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using ApiTester.Cli;
using ApiTester.Core;

namespace ApiTester.Tests.Cli;

/// <summary>The gateway holds a bounded share of the machine: a bounded number of connections per
/// site and of requests at once, and it says what it holds once a minute.</summary>
public class ServeLimitsTests
{
    /// <summary>An upstream that answers each request after <c>hold</c>, or when released, and
    /// remembers how many it was answering at once.</summary>
    private sealed class SlowUpstream : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly TimeSpan? _hold;
        private readonly SemaphoreSlim _release = new(0);
        private int _current;
        public int Max;
        public readonly SemaphoreSlim Arrived = new(0);
        public string Url { get; }

        public SlowUpstream(TimeSpan? hold)
        {
            _hold = hold;
            int port = ServeFixture.FreePort();
            Url = $"http://127.0.0.1:{port}/";
            _listener.Prefixes.Add(Url);
            _listener.Start();
            _ = Task.Run(async () =>
            {
                while (_listener.IsListening)
                {
                    HttpListenerContext ctx;
                    try { ctx = await _listener.GetContextAsync(); } catch { return; }
                    _ = Task.Run(async () =>
                    {
                        int now = Interlocked.Increment(ref _current);
                        lock (this) Max = Math.Max(Max, now);
                        Arrived.Release();
                        if (_hold is { } h) await Task.Delay(h); else await _release.WaitAsync();
                        Interlocked.Decrement(ref _current);
                        try { ctx.Response.StatusCode = 200; ctx.Response.Close(); } catch { }
                    });
                }
            });
        }

        public void Release(int n) => _release.Release(n);
        public void Dispose() { try { _listener.Stop(); } catch { } }
    }

    [Fact]
    public async Task A_site_gets_no_more_connections_than_max_connections()
    {
        using var upstream = new SlowUpstream(TimeSpan.FromMilliseconds(300));
        await using var gateway = ServeHost.Start(ServeFixture.Certs().client, upstream.Url, "--max-connections", "2");
        using var http = ServeFixture.NewClient();
        await ServeFixture.Poll(() => http.GetAsync(gateway.Origin + "/warm"));

        var responses = await Task.WhenAll(Enumerable.Range(0, 10).Select(i => http.GetAsync($"{gateway.Origin}/r{i}")));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        Assert.InRange(upstream.Max, 1, 2);   // ten at once from the browser, two at once upstream
    }

    [Fact]
    public async Task Past_max_requests_the_next_is_answered_503_at_once()
    {
        using var upstream = new SlowUpstream(hold: null);
        await using var gateway = ServeHost.Start(ServeFixture.Certs().client, upstream.Url, "--max-requests", "1");
        using var http = ServeFixture.NewClient();
        await WaitListening(gateway.Port);

        var held = http.GetAsync(gateway.Origin + "/held");
        Assert.True(await upstream.Arrived.WaitAsync(ServeFixture.Limit), "the first request never reached the upstream");

        var started = DateTime.UtcNow;
        var turnedAway = await http.GetAsync(gateway.Origin + "/second");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, turnedAway.StatusCode);
        Assert.Contains("--max-requests", await turnedAway.Content.ReadAsStringAsync());
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(5), "the busy answer waited");

        // The gateway's own endpoints and CORS preflights reach no upstream: answered regardless.
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync(gateway.Origin + "/_certapi/health")).StatusCode);

        upstream.Release(1);
        Assert.Equal(HttpStatusCode.OK, (await held).StatusCode);
        // With the first one done, the next is served again. Its slot is given back just after the
        // response is sent, so the very next request may still find it taken.
        upstream.Release(1);
        HttpResponseMessage third;
        var giveUp = DateTime.UtcNow + ServeFixture.Limit;
        int tries = 0;
        do
        {
            if (tries++ > 0) await Task.Delay(50);
            third = await http.GetAsync(gateway.Origin + "/third");
        } while (third.StatusCode == HttpStatusCode.ServiceUnavailable && DateTime.UtcNow < giveUp);
        Assert.Equal(HttpStatusCode.OK, third.StatusCode);
    }

    [Fact]
    public async Task A_cors_preflight_is_answered_with_every_slot_taken()
    {
        using var upstream = new SlowUpstream(hold: null);
        await using var gateway = ServeHost.Start(ServeFixture.Certs().client, upstream.Url, "--max-requests", "1", "--cors");
        using var http = ServeFixture.NewClient();
        await WaitListening(gateway.Port);

        var held = http.GetAsync(gateway.Origin + "/held");
        Assert.True(await upstream.Arrived.WaitAsync(ServeFixture.Limit), "the first request never reached the upstream");

        using var preflight = new HttpRequestMessage(HttpMethod.Options, gateway.Origin + "/api/orders");
        preflight.Headers.Add("Origin", "http://localhost:3000");
        preflight.Headers.Add("Access-Control-Request-Method", "POST");
        var answer = await http.SendAsync(preflight);
        Assert.True(answer.IsSuccessStatusCode, $"preflight answered {(int)answer.StatusCode}");
        Assert.Equal("http://localhost:3000", answer.Headers.GetValues("Access-Control-Allow-Origin").Single());

        upstream.Release(1);
        Assert.Equal(HttpStatusCode.OK, (await held).StatusCode);
    }

    [Fact]
    public async Task Load_is_reported_at_certapi_health_and_in_a_health_line()
    {
        await using var upstream = MockServer.Start(0, MockTlsMode.Http);
        var log = new StringWriter();
        using var cts = new CancellationTokenSource();
        int port = ServeFixture.FreePort();
        var run = Task.Run(() => CliApp.Run(
            ["serve", upstream.BaseUrl, "--port", port.ToString(), "--no-config", "-q"],
            TextWriter.Null, log,
            services: new CliServices { Cancel = cts.Token, UserConfigPath = null, ServeHealthInterval = TimeSpan.FromMilliseconds(300) }));
        try
        {
            using var http = ServeFixture.NewClient();
            for (int i = 0; i < 3; i++)
                Assert.Equal(HttpStatusCode.OK, (await ServeFixture.Poll(() => http.GetAsync($"http://127.0.0.1:{port}/echo/{i}"))).StatusCode);

            using var health = JsonDocument.Parse(await http.GetStringAsync($"http://127.0.0.1:{port}/_certapi/health"));
            var load = health.RootElement.GetProperty("load");
            Assert.True(load.GetProperty("requests").GetInt64() >= 3);
            Assert.True(load.GetProperty("connections").GetProperty("opened").GetInt64() >= 1);
            Assert.InRange(load.GetProperty("connections").GetProperty("open").GetInt64(), 0, 16);
            Assert.True(load.GetProperty("process").GetProperty("threads").GetInt32() > 0);
            Assert.Equal(MtlsGateway.DefaultMaxConnectionsPerSite, health.RootElement.GetProperty("maxConnectionsPerSite").GetInt32());
            await Task.Delay(1000);   // at least one health tick with that traffic behind it
        }
        finally
        {
            cts.Cancel();
            await run.WaitAsync(ServeFixture.Limit);
        }
        // Under -q too: the health line is the record of what the gateway held.
        string text = log.ToString();
        Assert.Contains("health ", text);
        Assert.Contains("in the last minute", text);
        Assert.Contains("connections ", text);
        Assert.DoesNotContain("/echo/", text);   // -q still drops the per-request lines
    }

    [Fact]
    public async Task A_tunnel_the_proxy_refuses_is_passed_on_and_counted_as_a_failed_connection()
    {
        // A proxy that refuses every CONNECT, and counts them.
        var proxy = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        proxy.Start();
        int proxyPort = ((IPEndPoint)proxy.LocalEndpoint).Port;
        int connects = 0;
        using var stop = new CancellationTokenSource();
        var serving = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                System.Net.Sockets.TcpClient client;
                try { client = await proxy.AcceptTcpClientAsync(stop.Token); } catch { return; }
                _ = Task.Run(async () =>
                {
                    using (client)
                    {
                        var stream = client.GetStream();
                        var reader = new StreamReader(stream);
                        string? line;
                        while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync())) { }
                        Interlocked.Increment(ref connects);
                        var answer = System.Text.Encoding.ASCII.GetBytes("HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                        await stream.WriteAsync(answer);
                    }
                });
            }
        });
        try
        {
            await using var gateway = ServeHost.Start(ServeFixture.Certs().client, "https://blocked.test/", "--proxy", $"http://127.0.0.1:{proxyPort}", "--allow-upgrade");
            using var http = ServeFixture.NewClient();
            await WaitListening(gateway.Port);
            // The proxy's answer, each time: the gateway does not refuse in its place (which would
            // have .NET's proxy failover mark a proxy that works failed), and the proxy is asked
            // once a request.
            int n = GatewayConnectionGuard.FailuresToPause + 2;
            for (int i = 0; i < n; i++)
            {
                var refused = await http.GetAsync(gateway.Origin + $"/r{i}");
                Assert.Equal(HttpStatusCode.BadGateway, refused.StatusCode);
                string body = await refused.Content.ReadAsStringAsync();
                Assert.Contains("403", body);
                Assert.DoesNotContain("certapi paused", body);
            }
            Assert.Equal(n, Volatile.Read(ref connects));

            // A WebSocket's tunnel the same way: the browser hears why in the close, and it is counted.
            using (var ws = new System.Net.WebSockets.ClientWebSocket())
            {
                using var wsLimit = new CancellationTokenSource(ServeFixture.Limit);
                await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{gateway.Port}/live"), wsLimit.Token);
                var close = await ws.ReceiveAsync(new byte[256], wsLimit.Token);
                Assert.Equal(System.Net.WebSockets.WebSocketMessageType.Close, close.MessageType);
                Assert.Equal(System.Net.WebSockets.WebSocketCloseStatus.InternalServerError, ws.CloseStatus);
            }
            Assert.Equal(n + 1, Volatile.Read(ref connects));

            using var health = JsonDocument.Parse(await http.GetStringAsync(gateway.Origin + "/_certapi/health"));
            var connections = health.RootElement.GetProperty("load").GetProperty("connections");
            Assert.Equal(n + 1, connections.GetProperty("failed").GetInt64());
            Assert.Equal(0, connections.GetProperty("paused").GetArrayLength());
        }
        finally
        {
            stop.Cancel();
            proxy.Stop();
            await serving.WaitAsync(ServeFixture.Limit);
        }
    }

    [ListenQueueFact]
    public async Task A_connect_that_gets_no_answer_is_answered_504_naming_the_site()
    {
        // A listener that never accepts: once its queue is full, a new connect gets no answer at all.
        using var listener = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork,
            System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(1);
        int port = ((IPEndPoint)listener.LocalEndPoint!).Port;
        var filling = new List<System.Net.Sockets.Socket>();
        try
        {
            for (int i = 0; i < 16; i++)
            {
                var client = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork,
                    System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);
                filling.Add(client);
                var connect = client.ConnectAsync(IPAddress.Loopback, port);
                if (await Task.WhenAny(connect, Task.Delay(500)) != connect) break;   // this one hangs: the queue is full
            }

            await using var gateway = ServeHost.Start(ServeFixture.Certs().client, $"http://127.0.0.1:{port}/", "--connect-timeout", "1");
            using var http = ServeFixture.NewClient();
            await WaitListening(gateway.Port);
            var started = DateTime.UtcNow;
            var answer = await http.GetAsync(gateway.Origin + "/x");
            string body = await answer.Content.ReadAsStringAsync();
            Assert.True(answer.StatusCode == HttpStatusCode.GatewayTimeout, $"{(int)answer.StatusCode}: {body}");
            Assert.Contains("within 1 s", body);
            Assert.Contains($"127.0.0.1:{port}", body);
            Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10), "the connect was not cut off");
        }
        finally { foreach (var c in filling) c.Dispose(); }
    }

    [Theory]
    [InlineData("--max-connections", "0")]
    [InlineData("--max-connections", "many")]
    [InlineData("--max-requests", "0")]
    public void Limits_must_be_positive_numbers(string option, string value)
    {
        var err = new StringWriter();
        int code = CliApp.Run(["serve", "https://api.example", "--port", "8443", option, value, "--no-config", "--check"],
            new StringWriter(), err, services: new CliServices { UserConfigPath = null });
        Assert.Equal(ExitCodes.Usage, code);
        Assert.Contains(option, err.ToString());
    }

    private static async Task WaitListening(int port)
    {
        using var probe = ServeFixture.NewClient();
        await ServeFixture.Poll(() => probe.GetAsync($"http://127.0.0.1:{port}/_certapi/health"));
    }
}

/// <summary>A test that needs a listener whose full accept queue drops new connections without an
/// answer, as Linux does. Windows refuses them instead, so there the test is reported as skipped.</summary>
public sealed class ListenQueueFactAttribute : FactAttribute
{
    public ListenQueueFactAttribute()
    {
        if (!OperatingSystem.IsLinux())
            Skip = "Needs Linux: a full listen queue there drops a new connection without an answer, which Windows refuses instead.";
    }
}
