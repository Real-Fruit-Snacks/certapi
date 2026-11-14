using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using ApiTester.Cli;
using ApiTester.Core;

namespace ApiTester.Tests;

/// <summary>The mock keeps a connection for the next request when its response can safely share
/// it, and closes it when it cannot (a stream, a body a client would not read, a fault, or
/// `--no-keep-alive`); `bench` says which of the two its numbers include.</summary>
public class MockKeepAliveTests
{
    /// <summary>Raw requests on one socket, so "the same connection" is a fact rather than a guess.</summary>
    private static async Task<(Socket Socket, NetworkStream Stream)> Connect(MockServer mock)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await socket.ConnectAsync(System.Net.IPAddress.Loopback, mock.Port);
        return (socket, new NetworkStream(socket, ownsSocket: false));
    }

    private static Task Send(Stream s, string request) => s.WriteAsync(Encoding.ASCII.GetBytes(request)).AsTask();

    /// <summary>One response: its head, and a body read by its Content-Length.</summary>
    private static async Task<(string Head, string Body)> ReadResponse(Stream s)
    {
        var bytes = new List<byte>();
        var one = new byte[1];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!(bytes.Count >= 4 && bytes[^4] == 13 && bytes[^3] == 10 && bytes[^2] == 13 && bytes[^1] == 10))
        {
            if (await s.ReadAsync(one, cts.Token) == 0) throw new EndOfStreamException("closed before a head");
            bytes.Add(one[0]);
        }
        string head = Encoding.ASCII.GetString(bytes.ToArray());
        int length = 0;
        foreach (var line in head.Split("\r\n"))
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) length = int.Parse(line[15..].Trim());
        var body = new byte[length];
        int read = 0;
        while (read < length)
        {
            int n = await s.ReadAsync(body.AsMemory(read), cts.Token);
            if (n == 0) throw new EndOfStreamException("closed inside a body");
            read += n;
        }
        return (head, Encoding.UTF8.GetString(body));
    }

    private static async Task<bool> ClosedByPeer(Stream s)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { return await s.ReadAsync(new byte[1], cts.Token) == 0; }
        catch (IOException) { return true; }
    }

    [Fact]
    public async Task Two_requests_share_one_connection()
    {
        await using var mock = MockServer.Start(0, MockTlsMode.Http);
        var (socket, s) = await Connect(mock);
        using (socket)
        {
            await Send(s, "GET /first HTTP/1.1\r\nHost: x\r\n\r\n");
            var first = await ReadResponse(s);
            Assert.Contains("Connection: keep-alive", first.Head);
            Assert.Contains("/first", first.Body);

            await Send(s, "POST /second HTTP/1.1\r\nHost: x\r\nContent-Length: 5\r\n\r\nhello");
            var second = await ReadResponse(s);
            Assert.Contains("/second", second.Body);
            Assert.Contains("hello", second.Body);
        }
    }

    [Fact]
    public async Task Pipelined_requests_in_one_write_are_each_answered()
    {
        await using var mock = MockServer.Start(0, MockTlsMode.Http);
        var (socket, s) = await Connect(mock);
        using (socket)
        {
            await Send(s, "POST /a HTTP/1.1\r\nHost: x\r\nContent-Length: 3\r\n\r\nabc" +
                          "POST /b HTTP/1.1\r\nHost: x\r\nTransfer-Encoding: chunked\r\n\r\n3\r\ndef\r\n0\r\n\r\n" +
                          "GET /c HTTP/1.1\r\nHost: x\r\n\r\n");
            var a = await ReadResponse(s);
            var b = await ReadResponse(s);
            var c = await ReadResponse(s);
            Assert.Contains("\"abc\"", a.Body);
            Assert.Contains("\"def\"", b.Body);
            Assert.Contains("/c", c.Body);
        }
    }

    [Theory]
    [InlineData("GET /x HTTP/1.1\r\nHost: x\r\nConnection: close\r\n\r\n")]   // the client asks to close
    [InlineData("GET /x HTTP/1.0\r\nHost: x\r\n\r\n")]                        // HTTP/1.0 has no keep-alive by default
    [InlineData("HEAD /x HTTP/1.1\r\nHost: x\r\n\r\n")]                       // the body sent would be misread
    [InlineData("GET /status/204 HTTP/1.1\r\nHost: x\r\n\r\n")]               // a 204 has no body to frame
    [InlineData("GET /sse HTTP/1.1\r\nHost: x\r\n\r\n")]                      // a stream ends with its connection
    public async Task A_response_that_cannot_share_the_connection_closes_it(string request)
    {
        await using var mock = MockServer.Start(0, MockTlsMode.Http);
        var (socket, s) = await Connect(mock);
        using (socket)
        {
            await Send(s, request);
            var buffer = new byte[64 * 1024];
            var all = new StringBuilder();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            int n;
            while ((n = await s.ReadAsync(buffer, cts.Token)) > 0) all.Append(Encoding.ASCII.GetString(buffer, 0, n));
            Assert.Contains("Connection: close", all.ToString());
            Assert.DoesNotContain("keep-alive", all.ToString());
        }
    }

    [Fact]
    public async Task No_keep_alive_answers_every_response_with_connection_close()
    {
        await using var mock = MockServer.Start(0, MockTlsMode.Http, keepAlive: false);
        var (socket, s) = await Connect(mock);
        using (socket)
        {
            await Send(s, "GET /x HTTP/1.1\r\nHost: x\r\n\r\n");
            var r = await ReadResponse(s);
            Assert.Contains("Connection: close", r.Head);
            Assert.True(await ClosedByPeer(s));
        }
    }

    [Fact]
    public async Task A_cors_response_keeps_its_headers_and_the_connection()
    {
        await using var mock = MockServer.Start(0, MockTlsMode.Http, cors: new MockCors(null));
        var (socket, s) = await Connect(mock);
        using (socket)
        {
            await Send(s, "OPTIONS /x HTTP/1.1\r\nHost: x\r\nOrigin: https://app.example\r\nAccess-Control-Request-Method: PUT\r\n\r\n");
            var preflight = await ReadResponse(s);
            Assert.StartsWith("HTTP/1.1 204", preflight.Head);
            Assert.Contains("Connection: keep-alive", preflight.Head);
            await Send(s, "PUT /x HTTP/1.1\r\nHost: x\r\nOrigin: https://app.example\r\nContent-Length: 0\r\n\r\n");
            var put = await ReadResponse(s);
            Assert.Contains("Access-Control-Allow-Origin: https://app.example", put.Head);
            Assert.Contains("Connection: keep-alive", put.Head);
        }
    }

    [Fact]
    public async Task A_route_fault_keeps_its_own_connection_behaviour()
    {
        var scenario = MockScenario.Parse("""
            { "routes": [ { "match": { "path": "/abort" }, "respond": { "status": 200, "body": "partial body", "then": "abort" } } ] }
            """);
        await using var mock = MockServer.Start(0, MockTlsMode.Http, scenario: scenario);
        var (socket, s) = await Connect(mock);
        using (socket)
        {
            await Send(s, "GET /abort HTTP/1.1\r\nHost: x\r\n\r\n");
            var buffer = new byte[4096];
            var all = new StringBuilder();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try { int n; while ((n = await s.ReadAsync(buffer, cts.Token)) > 0) all.Append(Encoding.ASCII.GetString(buffer, 0, n)); }
            catch (IOException) { }
            Assert.Contains("Connection: close", all.ToString());
        }
    }

    [Fact]
    public async Task Bench_says_the_pool_was_used_against_a_keep_alive_mock_and_not_against_one_that_closes()
    {
        await using var keeps = MockServer.Start(0, MockTlsMode.Http);
        var pooled = await Bench.RunAsync(new ApiRequest { Method = HttpMethod.Get, Url = keeps.BaseUrl },
            null, new TransportOptions { Proxy = ProxyMode.None }, new BenchOptions(20, 2), null, CancellationToken.None);
        Assert.Equal(20, pooled.Succeeded);
        Assert.Equal(0, pooled.ClosedByServer);

        await using var closes = MockServer.Start(0, MockTlsMode.Http, keepAlive: false);
        var unpooled = await Bench.RunAsync(new ApiRequest { Method = HttpMethod.Get, Url = closes.BaseUrl },
            null, new TransportOptions { Proxy = ProxyMode.None }, new BenchOptions(20, 2), null, CancellationToken.None);
        Assert.Equal(20, unpooled.Succeeded);
        Assert.Equal(20, unpooled.ClosedByServer);

        var so = new StringWriter();
        CliApp.Run(new[] { "bench", keeps.BaseUrl, "-n", "10", "-c", "2", "--no-proxy", "--no-config" }, so, new StringWriter(), new MemoryStream(),
            new CliServices { LiveStatePath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json"), UserConfigPath = null });
        Assert.Contains("connections are pooled and reused", so.ToString());
    }
}
