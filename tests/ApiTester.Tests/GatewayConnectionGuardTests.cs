using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Authentication;
using ApiTester.Core;

namespace ApiTester.Tests;

/// <summary>An endpoint that keeps failing is left alone for a while instead of being dialled again
/// and again: a burst of failing connections is what endpoint protection reads as a scan, and each
/// one that hangs holds something in the software under the network stack.</summary>
public class GatewayConnectionGuardTests
{
    private sealed class FakeNetwork
    {
        public int Attempts;
        public Exception? Failure = new SocketException((int)SocketError.ConnectionRefused);
        public TaskCompletionSource? Gate;   // when set, every connect waits for it

        public async ValueTask<Stream> Connect(SocketsHttpConnectionContext context, CancellationToken ct)
        {
            Interlocked.Increment(ref Attempts);
            if (Gate is { } gate) await gate.Task.WaitAsync(ct);
            if (Failure is { } f) throw f;
            return new MemoryStream();   // connected; the exchange then fails, which is fine here
        }
    }

    private static (HttpClient Http, GatewayConnectionGuard Guard, FakeNetwork Net) Rig(Func<long> clock)
    {
        var net = new FakeNetwork();
        var handler = new SocketsHttpHandler { ConnectCallback = net.Connect, MaxConnectionsPerServer = 100 };
        var guard = new GatewayConnectionGuard(clock);
        guard.Install(handler);
        return (new HttpClient(handler), guard, net);
    }

    private static async Task<string> Fails(HttpClient http, string url = "http://site.test/") =>
        (await Assert.ThrowsAnyAsync<HttpRequestException>(() => http.GetAsync(url))).Message;

    [Fact]
    public async Task Five_failures_in_a_row_pause_the_endpoint_and_a_trial_follows()
    {
        long now = 0;
        var (http, guard, net) = Rig(() => now);
        for (int i = 0; i < GatewayConnectionGuard.FailuresToPause; i++) await Fails(http);
        Assert.Equal(5, net.Attempts);

        // Paused: refused without dialling, saying for how long. The handler adds the endpoint.
        string paused = await Fails(http);
        Assert.Contains("certapi paused new connections for 10 s after 5 failed attempts in a row", paused);
        Assert.Contains("site.test:80", paused);
        Assert.Equal(5, net.Attempts);
        Assert.Equal(1, guard.Refused);
        Assert.Single(guard.Paused());

        // After the pause one trial: it fails, and the next pause is twice as long.
        now += 10_001;
        await Fails(http);
        Assert.Equal(6, net.Attempts);
        Assert.Contains("for 20 s", await Fails(http));

        // A trial that succeeds clears it: the next failures start counting from nothing.
        now += 20_001;
        net.Failure = null;
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => http.GetAsync("http://site.test/"));   // connected, no HTTP answer
        Assert.True(guard.Opened >= 1);   // the handler may retry an exchange that got no answer
        Assert.Empty(guard.Paused());
        net.Failure = new SocketException((int)SocketError.TimedOut);
        for (int i = 0; i < 4; i++) await Fails(http);
        Assert.Empty(guard.Paused());
        Assert.Equal(5 + 1 + 4, guard.Failed);
    }

    [Fact]
    public async Task A_burst_that_fails_together_pauses_once_for_the_first_pause()
    {
        long now = 0;
        var (http, guard, net) = Rig(() => now);
        net.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var burst = Enumerable.Range(0, 12).Select(i => http.GetAsync($"http://site.test/{i}")).ToList();
        while (Volatile.Read(ref net.Attempts) < 12) await Task.Delay(10);
        net.Gate.SetResult();   // all twelve fail at once
        foreach (var request in burst) await Assert.ThrowsAnyAsync<HttpRequestException>(() => request);

        Assert.Equal(12, guard.Failed);
        var pause = Assert.Single(guard.Paused());
        Assert.Equal(GatewayConnectionGuard.FirstPause, pause.Remaining);   // not doubled by the seven that came after
        net.Gate = null;

        // After it, a single trial is dialled; everything asked for meanwhile is refused.
        now += 10_001;
        net.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var trial = http.GetAsync("http://site.test/trial");
        while (Volatile.Read(ref net.Attempts) < 13) await Task.Delay(10);
        for (int i = 0; i < 5; i++)
            Assert.Contains("a trial connection is under way", await Fails(http, $"http://site.test/wait{i}"));
        Assert.Equal(13, net.Attempts);
        net.Gate.SetResult();
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => trial);
        Assert.Contains("for 20 s", await Fails(http));
    }

    [Fact]
    public async Task A_success_during_a_pause_ends_it()
    {
        long now = 0;
        var (http, guard, net) = Rig(() => now);
        // One connect is slow and will succeed; five quick ones fail meanwhile.
        net.Failure = null;
        net.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slow = http.GetAsync("http://site.test/slow");
        while (Volatile.Read(ref net.Attempts) < 1) await Task.Delay(10);
        var gate = net.Gate;
        net.Gate = null;
        net.Failure = new SocketException((int)SocketError.ConnectionRefused);
        for (int i = 0; i < 5; i++) await Fails(http);
        Assert.Single(guard.Paused());

        net.Failure = null;   // the slow connect now succeeds
        gate.SetResult();
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => slow);   // connected, no HTTP answer
        Assert.Empty(guard.Paused());
    }

    [Fact]
    public async Task Failures_spread_out_do_not_pause()
    {
        long now = 0;
        var (http, guard, net) = Rig(() => now);
        for (int i = 0; i < 8; i++)
        {
            await Fails(http);
            now += 31_000;   // each outside the previous one's window
        }
        Assert.Equal(8, net.Attempts);
        Assert.Empty(guard.Paused());
    }

    [Fact]
    public async Task A_refused_certificate_is_not_counted_as_a_network_failure()
    {
        var (http, guard, net) = Rig(() => 0);
        net.Failure = new AuthenticationException("the remote certificate is invalid");
        for (int i = 0; i < 7; i++) await Fails(http);
        Assert.Equal(7, net.Attempts);
        Assert.Empty(guard.Paused());
        Assert.Equal(0, guard.Failed);
    }

    [Fact]
    public async Task A_connect_the_handler_cancels_is_not_a_failure()
    {
        var guard = new GatewayConnectionGuard(() => 0);
        var context = new SocketsHttpConnectionContextProbe("site.test", 443).Context;
        for (int i = 0; i < 7; i++)
        {
            using var cts = new CancellationTokenSource();
            var connect = guard.ConnectAsync(context, async (_, t) => { await Task.Delay(Timeout.Infinite, t); return new MemoryStream(); },
                connectLimit: null, handlerLimit: Timeout.InfiniteTimeSpan, cts.Token);
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connect.AsTask());
        }
        Assert.Empty(guard.Paused());
        Assert.Equal(0, guard.Failed);
    }

    [Fact]
    public async Task Endpoints_are_paused_separately()
    {
        var (http, _, net) = Rig(() => 0);
        for (int i = 0; i < 5; i++) await Fails(http);
        Assert.Contains("certapi paused", await Fails(http));
        // Another host is dialled as usual.
        var other = (await Assert.ThrowsAnyAsync<HttpRequestException>(() => http.GetAsync("http://other.test/"))).Message;
        Assert.DoesNotContain("certapi paused", other);
        Assert.Equal(6, net.Attempts);
    }

    [Fact]
    public async Task A_connect_that_hangs_fails_at_the_limit_as_a_connection_failure()
    {
        // Not the handler's own timeout (a cancellation, which no proxy failover moves on from): an
        // IOException, which the handler reports as this endpoint failing.
        var timedOut = await Assert.ThrowsAsync<GatewayConnectTimeoutException>(() =>
            GatewayConnectionGuard.ConnectWithinAsync(t => new ValueTask(Task.Delay(Timeout.Infinite, t)),
                TimeSpan.FromMilliseconds(100), CancellationToken.None).AsTask());
        Assert.Equal("The connection attempt got no answer within 0.1 s", timedOut.Message);

        // A cancellation from outside stays a cancellation.
        using var cts = new CancellationTokenSource(50);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            GatewayConnectionGuard.ConnectWithinAsync(t => new ValueTask(Task.Delay(Timeout.Infinite, t)),
                TimeSpan.FromSeconds(30), cts.Token).AsTask());

        // Through a handler: a failure of the endpoint, counted, with the endpoint named.
        var guard = new GatewayConnectionGuard();
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (_, ct) =>
            {
                await GatewayConnectionGuard.ConnectWithinAsync(t => new ValueTask(Task.Delay(Timeout.Infinite, t)), TimeSpan.FromMilliseconds(100), ct);
                return new MemoryStream();
            }
        };
        guard.Install(handler);
        using var http = new HttpClient(handler);
        var failed = await Assert.ThrowsAsync<HttpRequestException>(() => http.GetAsync("http://site.test/"));
        Assert.IsType<GatewayConnectTimeoutException>(failed.InnerException);
        Assert.Contains("site.test:80", failed.Message);
        Assert.Equal(1, guard.Failed);
    }

    [ApiTester.Tests.Cli.ListenQueueFact]
    public async Task An_address_that_hangs_gives_way_to_the_next_one_the_name_has()
    {
        // 127.0.0.1 never answers (its listener's queue is full); 127.0.0.2 does. Windows would
        // wait about 21 seconds on the first before trying the second; an overall limit shorter
        // than that would never reach it.
        using var full = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        full.Bind(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0));
        full.Listen(1);
        int port = ((System.Net.IPEndPoint)full.LocalEndPoint!).Port;
        var filling = new List<Socket>();
        using var open = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        open.Bind(new System.Net.IPEndPoint(System.Net.IPAddress.Parse("127.0.0.2"), port));
        open.Listen(16);
        try
        {
            for (int i = 0; i < 16; i++)
            {
                var c = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                filling.Add(c);
                var connect = c.ConnectAsync(System.Net.IPAddress.Loopback, port);
                if (await Task.WhenAny(connect, Task.Delay(500)) != connect) break;
            }

            var started = System.Diagnostics.Stopwatch.StartNew();
            using var socket = await GatewayConnectionGuard.ConnectSocketAsync("two.test", port, TimeSpan.FromMilliseconds(500),
                (_, _) => Task.FromResult(new[] { System.Net.IPAddress.Loopback, System.Net.IPAddress.Parse("127.0.0.2") }),
                CancellationToken.None);
            Assert.Equal(System.Net.IPAddress.Parse("127.0.0.2"), ((System.Net.IPEndPoint)socket.RemoteEndPoint!).Address);
            Assert.InRange(started.Elapsed, TimeSpan.FromMilliseconds(400), TimeSpan.FromSeconds(10));

            // Every address hanging: the time-out, as an IOException.
            await Assert.ThrowsAsync<GatewayConnectTimeoutException>(() => GatewayConnectionGuard.ConnectSocketAsync("one.test", port,
                TimeSpan.FromMilliseconds(300), (_, _) => Task.FromResult(new[] { System.Net.IPAddress.Loopback }), CancellationToken.None).AsTask());
        }
        finally { foreach (var c in filling) c.Dispose(); }
    }

    [Fact]
    public async Task A_name_lookup_that_hangs_is_bounded_too()
    {
        await Assert.ThrowsAsync<GatewayConnectTimeoutException>(() => GatewayConnectionGuard.ConnectSocketAsync("slow.test", 443,
            TimeSpan.FromMilliseconds(200), async (_, t) => { await Task.Delay(Timeout.Infinite, t); return Array.Empty<System.Net.IPAddress>(); },
            CancellationToken.None).AsTask());
    }

    private static readonly Func<SocketsHttpConnectionContext, CancellationToken, ValueTask<Stream>> Connects =
        (_, _) => ValueTask.FromResult<Stream>(new MemoryStream());

    private static readonly Func<SocketsHttpConnectionContext, CancellationToken, ValueTask<Stream>> Refused =
        (_, _) => throw new SocketException((int)SocketError.ConnectionRefused);

    [Fact]
    public async Task A_tunnel_the_proxy_refuses_is_counted_and_nothing_is_paused()
    {
        // Refused from the connect step, a tunnel would make .NET's proxy failover mark a proxy that
        // works failed (for half an hour); the proxy's answer is passed on instead, and counted.
        var guard = new GatewayConnectionGuard(() => 0);
        var tunnel = new SocketsHttpConnectionContextProbe("app.corp", 443, throughProxy: true).Context;
        Assert.Equal(HttpMethod.Connect, tunnel.InitialRequestMessage.Method);
        for (int i = 0; i < 12; i++) guard.CountRefusedTunnel();
        Assert.Equal(12, guard.Failed);
        Assert.Empty(guard.Paused());
        (await guard.ConnectAsync(tunnel, Connects, null, Timeout.InfiniteTimeSpan, default)).Dispose();
        Assert.Equal(0, guard.Refused);
    }

    [Fact]
    public async Task A_socket_connection_stays_a_network_stream_and_closing_it_closes_the_socket()
    {
        // .NET reads the peer address it reports (--trace, the connections command) from a
        // NetworkStream: the counted stream of a socket has to be one.
        using var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        var guard = new GatewayConnectionGuard();
        var context = new SocketsHttpConnectionContextProbe("127.0.0.1", port).Context;

        // The handler's own connect, and a callback that hands back a socket's stream.
        var plain = await guard.ConnectAsync(context, null, TimeSpan.FromSeconds(10), Timeout.InfiniteTimeSpan, default);
        using var plainServer = await listener.AcceptSocketAsync();
        var fromCallback = await guard.ConnectAsync(context, async (c, t) => new NetworkStream(
                await GatewayConnectionGuard.ConnectSocketAsync(c.DnsEndPoint.Host, c.DnsEndPoint.Port, TimeSpan.FromSeconds(10), null, t),
                ownsSocket: true),
            null, Timeout.InfiniteTimeSpan, default);
        using var callbackServer = await listener.AcceptSocketAsync();
        foreach (var stream in new[] { plain, fromCallback })
        {
            var network = Assert.IsAssignableFrom<NetworkStream>(stream);
            Assert.Equal(port, ((System.Net.IPEndPoint)network.Socket.RemoteEndPoint!).Port);
        }
        Assert.Equal(2, guard.Open);

        // Closed: the socket with it (the other end reads the end of the stream), and the count.
        var buffer = new byte[1];
        plain.Dispose();
        Assert.Equal(0, await plainServer.ReceiveAsync(buffer, SocketFlags.None).WaitAsync(TimeSpan.FromSeconds(10)));
        await fromCallback.DisposeAsync();
        Assert.Equal(0, await callbackServer.ReceiveAsync(buffer, SocketFlags.None).WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(0, guard.Open);
    }

    [Fact]
    public async Task A_trial_that_takes_longer_than_the_connect_limit_lets_another_through()
    {
        long now = 0;
        var guard = new GatewayConnectionGuard(() => now);
        var context = new SocketsHttpConnectionContextProbe("site.test", 80).Context;
        var limit = TimeSpan.FromSeconds(15);
        for (int i = 0; i < 5; i++)
            await Assert.ThrowsAsync<SocketException>(() => guard.ConnectAsync(context, Refused, limit, Timeout.InfiniteTimeSpan, default).AsTask());
        now += 10_001;

        var slow = new TaskCompletionSource<Stream>(TaskCreationOptions.RunContinuationsAsynchronously);
        var trial = guard.ConnectAsync(context, (_, _) => new ValueTask<Stream>(slow.Task), limit, Timeout.InfiniteTimeSpan, default);
        Assert.Contains("a trial connection is under way", (await Assert.ThrowsAsync<GatewayEndpointPausedException>(() =>
            guard.ConnectAsync(context, Connects, limit, Timeout.InfiniteTimeSpan, default).AsTask())).Message);

        now += 15_001;   // the trial still has no answer
        (await guard.ConnectAsync(context, Connects, limit, Timeout.InfiniteTimeSpan, default)).Dispose();
        Assert.Empty(guard.Paused());
        slow.SetResult(new MemoryStream());
        (await trial).Dispose();
    }

    [Fact]
    public async Task A_connect_cancelled_after_hanging_a_whole_limit_counts_as_a_failure()
    {
        // A name whose addresses hang one after another can outlast the request it was opened for;
        // the handler then cancels it, and it still failed.
        var guard = new GatewayConnectionGuard();
        var context = new SocketsHttpConnectionContextProbe("site.test", 80).Context;
        for (int i = 0; i < 5; i++)
        {
            // Cancelled 150 ms into the attempt itself, so a pause before it began cannot make it
            // look shorter than the 100 ms limit.
            using var cts = new CancellationTokenSource();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => guard.ConnectAsync(context,
                async (_, t) => { cts.CancelAfter(TimeSpan.FromMilliseconds(150)); await Task.Delay(Timeout.Infinite, t); return new MemoryStream(); },
                TimeSpan.FromMilliseconds(100), Timeout.InfiniteTimeSpan, cts.Token).AsTask());
        }
        Assert.Equal(5, guard.Failed);
        Assert.Single(guard.Paused());
    }

    [Fact]
    public async Task Open_counts_the_connections_until_they_close()
    {
        var guard = new GatewayConnectionGuard();
        var context = new SocketsHttpConnectionContextProbe("site.test", 80).Context;
        var a = await guard.ConnectAsync(context, Connects, null, Timeout.InfiniteTimeSpan, default);
        var b = await guard.ConnectAsync(context, Connects, null, Timeout.InfiniteTimeSpan, default);
        Assert.Equal(2, guard.Open);
        a.Dispose();
        a.Dispose();
        Assert.Equal(1, guard.Open);
        await b.DisposeAsync();
        Assert.Equal(0, guard.Open);
        Assert.Equal(2, guard.Opened);
    }

    /// <summary>A connection context for calling the guard directly: the handler's own, for a
    /// request to the site, or (through a proxy) for the CONNECT that asks the proxy for a tunnel.</summary>
    private sealed class SocketsHttpConnectionContextProbe
    {
        public SocketsHttpConnectionContext Context { get; }

        public SocketsHttpConnectionContextProbe(string host, int port, bool throughProxy = false)
        {
            SocketsHttpConnectionContext? captured = null;
            var handler = new SocketsHttpHandler
            {
                ConnectCallback = (c, _) => { captured = c; throw new IOException("captured"); }
            };
            if (throughProxy) handler.Proxy = new System.Net.WebProxy("http://proxy.test:8080");
            using var http = new HttpClient(handler);
            string url = throughProxy ? $"https://{host}:{port}/" : $"http://{host}:{port}/";
            try { http.GetAsync(url).GetAwaiter().GetResult(); } catch (HttpRequestException) { }
            Context = captured!;
        }
    }
}
