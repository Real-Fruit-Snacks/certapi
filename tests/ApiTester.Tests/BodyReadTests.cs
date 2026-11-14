using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using ApiTester.Core;

namespace ApiTester.Tests;

/// <summary>The body is read by ApiClient itself, once, at its own size. These pin down what
/// HttpClient's own buffering used to guarantee: the whole body whatever its framing, the time and
/// timeout covering it, and a broken body reported as the network failure it is.</summary>
public class BodyReadTests
{
    /// <summary>A server that answers one connection with exactly these bytes, then closes.</summary>
    private static (string Url, Task Served) Raw(byte[] response)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var served = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            using var stream = client.GetStream();
            var buffer = new byte[8192];
            var head = new StringBuilder();
            while (!head.ToString().Contains("\r\n\r\n"))
            {
                int n = await stream.ReadAsync(buffer);
                if (n == 0) break;
                head.Append(Encoding.ASCII.GetString(buffer, 0, n));
            }
            await stream.WriteAsync(response);
            await stream.FlushAsync();
            listener.Stop();
        });
        return ($"http://127.0.0.1:{port}/", served);
    }

    private static Task<ApiResponse> Get(string url, TimeSpan? timeout = null, TransportOptions? transport = null) =>
        new ApiClient().SendAsync(new ApiRequest { Method = HttpMethod.Get, Url = url, Timeout = timeout ?? TimeSpan.FromSeconds(30) },
            null, transport ?? new TransportOptions { Proxy = ProxyMode.None });

    [Fact]
    public async Task A_body_with_a_length_comes_back_whole()
    {
        var payload = new byte[3_000_000];
        new Random(7).NextBytes(payload);
        var (url, served) = Raw(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n")
            .Concat(payload).ToArray());
        var r = await Get(url);
        await served;
        Assert.True(r.IsSuccess, r.Error?.Message);
        Assert.Equal(payload, r.Body);
    }

    [Fact]
    public async Task A_chunked_body_without_a_length_comes_back_whole()
    {
        var chunks = new StringBuilder("HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\nConnection: close\r\n\r\n");
        var expected = new StringBuilder();
        for (int i = 0; i < 2000; i++)
        {
            string part = $"part {i};";
            chunks.Append($"{part.Length:x}\r\n{part}\r\n");
            expected.Append(part);
        }
        chunks.Append("0\r\n\r\n");
        var (url, served) = Raw(Encoding.ASCII.GetBytes(chunks.ToString()));
        var r = await Get(url);
        await served;
        Assert.True(r.IsSuccess, r.Error?.Message);
        Assert.Equal(expected.ToString(), Encoding.ASCII.GetString(r.Body));
    }

    [Fact]
    public async Task A_compressed_body_decodes_to_more_than_its_declared_length()
    {
        var text = string.Concat(Enumerable.Repeat("compressible text ", 20000));
        using var packed = new MemoryStream();
        using (var gzip = new GZipStream(packed, CompressionLevel.Optimal, leaveOpen: true))
            gzip.Write(Encoding.ASCII.GetBytes(text));
        var body = packed.ToArray();
        var (url, served) = Raw(Encoding.ASCII.GetBytes(
            $"HTTP/1.1 200 OK\r\nContent-Encoding: gzip\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n").Concat(body).ToArray());
        var r = await Get(url);
        await served;
        Assert.True(r.IsSuccess, r.Error?.Message);
        Assert.Equal(text, Encoding.ASCII.GetString(r.Body));
    }

    [Fact]
    public async Task A_body_cut_short_is_a_network_failure_not_a_file_error()
    {
        var (url, served) = Raw(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 1000\r\nConnection: close\r\n\r\nonly a little"));
        var r = await Get(url);
        await served;
        Assert.NotNull(r.Error);
        Assert.Equal(ApiErrorKind.Network, r.Error!.Kind);
        Assert.DoesNotContain("file", r.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_body_still_arriving_when_the_timeout_runs_out_is_a_timeout()
    {
        var scenario = MockScenario.Parse("""
            { "routes": [ { "match": { "path": "/slow" }, "respond": { "status": 200, "body": "%BODY%", "dripBytesPerSec": 200 } } ] }
            """.Replace("%BODY%", new string('x', 2000)));
        await using var mock = MockServer.Start(0, MockTlsMode.Http, scenario: scenario);
        var started = System.Diagnostics.Stopwatch.StartNew();
        var r = await Get(mock.BaseUrl + "slow", timeout: TimeSpan.FromSeconds(1));
        Assert.Equal(ApiErrorKind.Timeout, r.Error?.Kind);
        Assert.InRange(started.Elapsed.TotalSeconds, 0.8, 5);
    }

    [Fact]
    public async Task The_time_reported_includes_the_body()
    {
        var scenario = MockScenario.Parse("""
            { "routes": [ { "match": { "path": "/drip" }, "respond": { "status": 200, "body": "%BODY%", "dripBytesPerSec": 1000 } } ] }
            """.Replace("%BODY%", new string('y', 500)));
        await using var mock = MockServer.Start(0, MockTlsMode.Http, scenario: scenario);
        var r = await Get(mock.BaseUrl + "drip");
        Assert.True(r.IsSuccess, r.Error?.Message);
        Assert.Equal(500, r.Body.Length);
        Assert.True(r.Elapsed >= TimeSpan.FromMilliseconds(300), $"elapsed {r.Elapsed.TotalMilliseconds} ms");
    }

    [Fact]
    public async Task A_redirect_with_a_body_is_followed_and_its_hop_is_recorded()
    {
        var scenario = MockScenario.Parse("""
            { "routes": [
                { "match": { "path": "/from" }, "respond": { "status": 302, "headers": { "Location": "/to" }, "body": "moved, with a body" } },
                { "match": { "path": "/to" }, "respond": { "status": 200, "body": "arrived" } } ] }
            """);
        await using var mock = MockServer.Start(0, MockTlsMode.Http, scenario: scenario);
        var client = new ApiClient();
        var transport = new TransportOptions { Proxy = ProxyMode.None };
        var first = await client.SendAsync(new ApiRequest { Method = HttpMethod.Get, Url = mock.BaseUrl + "from" }, null, transport);
        var second = await client.SendAsync(new ApiRequest { Method = HttpMethod.Get, Url = mock.BaseUrl + "from" }, null, transport);
        Assert.Equal("arrived", Encoding.UTF8.GetString(first.Body));
        Assert.Equal("arrived", Encoding.UTF8.GetString(second.Body));
        Assert.Single(first.Redirects);
        Assert.Equal(302, first.Redirects[0].StatusCode);
    }
}
