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

    /// <summary>The fastest of three tries. Windows' retries make every try of a refused connect
    /// take at least two seconds, so one fast try proves they are off, while a machine busy enough
    /// to delay one try (a loaded test run) does not fail the test.</summary>
    private static async Task<TimeSpan> Fastest(Func<Task> attempt)
    {
        var best = TimeSpan.MaxValue;
        for (int i = 0; i < 3 && best >= TimeSpan.FromSeconds(1); i++)
        {
            var clock = Stopwatch.StartNew();
            await attempt();
            if (clock.Elapsed < best) best = clock.Elapsed;
        }
        return best;
    }

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
        var took = await Fastest(async () =>
        {
            var r = await new ApiClient().SendAsync(
                new ApiRequest { Method = HttpMethod.Get, Url = $"http://127.0.0.1:{ClosedPort()}/" },
                clientCertificate: null, transport: new TransportOptions { Proxy = ProxyMode.None });
            Assert.False(r.IsSuccess);
        });
        Assert.True(took < TimeSpan.FromSeconds(1), $"took {took.TotalMilliseconds:0} ms");
    }

    [Fact]
    public async Task Localhost_reaches_a_server_on_127_0_0_1_only_without_the_ipv6_wait()
    {
        // A server that listens on IPv4 alone, as many local development servers do.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var stop = new CancellationTokenSource();
        var serve = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                using var c = await listener.AcceptTcpClientAsync(stop.Token);
                var s = c.GetStream();
                var buf = new byte[4096];
                _ = await s.ReadAsync(buf);   // the request head; one read is enough for it
                var reply = "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok"u8.ToArray();
                await s.WriteAsync(reply);
            }
        });
        try
        {
            var took = await Fastest(async () =>
            {
                var r = await new ApiClient().SendAsync(
                    new ApiRequest { Method = HttpMethod.Get, Url = $"http://localhost:{port}/" },
                    clientCertificate: null, transport: new TransportOptions { Proxy = ProxyMode.None });
                Assert.True(r.IsSuccess, r.Error?.Message);
            });
            Assert.True(took < TimeSpan.FromSeconds(1), $"took {took.TotalMilliseconds:0} ms");
        }
        finally
        {
            stop.Cancel();
            listener.Stop();
            try { await serve; } catch { /* stopped */ }
        }
    }

    [Fact]
    public async Task A_busy_server_with_a_full_queue_still_takes_every_connection()
    {
        // A refusal is also Windows' answer while a listener's queue is full, and retrying is what
        // let those connections in: a server with a queue of one, accepting slowly, and five
        // connections at once — every one has to get through, as it did before.
        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(1);
        int port = ((IPEndPoint)listener.LocalEndPoint!).Port;
        using var stop = new CancellationTokenSource();
        var accepted = new List<Socket>();
        var acceptor = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    var c = await listener.AcceptAsync(stop.Token);
                    lock (accepted) accepted.Add(c);
                    await Task.Delay(150, stop.Token);
                }
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
        });
        var connects = Enumerable.Range(0, 5).Select(_ => LoopbackConnect.ConnectAsync(
            () => new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp),
            new IPEndPoint(IPAddress.Loopback, port), CancellationToken.None).AsTask()).ToArray();
        try
        {
            var sockets = await Task.WhenAll(connects);
            Assert.All(sockets, s => Assert.True(s.Connected));
            foreach (var socket in sockets) socket.Dispose();
        }
        finally
        {
            stop.Cancel();
            listener.Close();
            lock (accepted) foreach (var c in accepted) c.Dispose();
        }
    }

    [Fact]
    public void Only_a_listener_a_connection_could_reach_counts()
    {
        var v4 = new TcpListener(IPAddress.Loopback, 0);
        v4.Start();
        int port = ((IPEndPoint)v4.LocalEndpoint).Port;
        try
        {
            Assert.True(LoopbackConnect.SomethingListens(new IPEndPoint(IPAddress.Loopback, port)));
            // A server on 127.0.0.1 alone: ::1 is refused, and that refusal is final, so "localhost"
            // goes straight on to 127.0.0.1.
            Assert.False(LoopbackConnect.SomethingListens(new IPEndPoint(IPAddress.IPv6Loopback, port)));
            Assert.True(LoopbackConnect.SomethingListens(new DnsEndPoint("localhost", port)));
        }
        finally { v4.Stop(); }
        Assert.False(LoopbackConnect.SomethingListens(new IPEndPoint(IPAddress.Loopback, port)));
    }

    [Fact]
    public async Task A_stream_handler_fails_at_once_too()
    {
        using var handler = StreamTransport.CreateHandler(null, new TransportOptions { Proxy = ProxyMode.None });
        using var http = new HttpClient(handler);
        var took = await Fastest(() => Assert.ThrowsAnyAsync<HttpRequestException>(() => http.GetAsync($"http://localhost:{ClosedPort()}/")));
        Assert.True(took < TimeSpan.FromSeconds(1), $"took {took.TotalMilliseconds:0} ms");
    }
}
