using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using ApiTester.Core;

namespace ApiTester.Tests;

/// <summary>A refused connection to this machine is reported at once. On Windows it used to take
/// about two seconds — the connect was retried twice after the refusal — on every send to a local
/// server that was down, and on every new connection to localhost when the server listened on
/// 127.0.0.1 only (localhost is tried as ::1 first).</summary>
public class LoopbackConnectTests
{
    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("127.5.6.7", true)]
    [InlineData("::1", true)]
    [InlineData("[::1]", true)]
    [InlineData("localhost", true)]
    [InlineData("LOCALHOST.", true)]
    [InlineData("api.localhost", true)]
    [InlineData("localhost.example.com", false)]
    [InlineData("10.0.0.1", false)]
    [InlineData("example.com", false)]
    public void Loopback_names_are_recognised(string host, bool loopback) =>
        Assert.Equal(loopback, LoopbackConnect.IsLoopbackName(host));

    private static int ClosedPort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    [Fact]
    public async Task A_send_to_a_closed_local_port_fails_at_once()
    {
        var clock = Stopwatch.StartNew();
        var r = await new ApiClient().SendAsync(
            new ApiRequest { Method = HttpMethod.Get, Url = $"http://127.0.0.1:{ClosedPort()}/" },
            clientCertificate: null, transport: new TransportOptions { Proxy = ProxyMode.None });
        Assert.False(r.IsSuccess);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(1), $"took {clock.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task Localhost_reaches_a_server_on_127_0_0_1_only_without_the_ipv6_wait()
    {
        // A server that listens on IPv4 alone, as many local development servers do.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var serve = Task.Run(async () =>
        {
            using var c = await listener.AcceptTcpClientAsync();
            var s = c.GetStream();
            var buf = new byte[4096];
            _ = await s.ReadAsync(buf);   // the request head; one read is enough for it
            var reply = "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok"u8.ToArray();
            await s.WriteAsync(reply);
        });
        try
        {
            var clock = Stopwatch.StartNew();
            var r = await new ApiClient().SendAsync(
                new ApiRequest { Method = HttpMethod.Get, Url = $"http://localhost:{port}/" },
                clientCertificate: null, transport: new TransportOptions { Proxy = ProxyMode.None });
            Assert.True(r.IsSuccess, r.Error?.Message);
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(1), $"took {clock.ElapsedMilliseconds} ms");
            await serve;
        }
        finally { listener.Stop(); }
    }

    [Fact]
    public async Task A_stream_handler_fails_at_once_too()
    {
        using var handler = StreamTransport.CreateHandler(null, new TransportOptions { Proxy = ProxyMode.None });
        using var http = new HttpClient(handler);
        var clock = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => http.GetAsync($"http://localhost:{ClosedPort()}/"));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(1), $"took {clock.ElapsedMilliseconds} ms");
    }
}
