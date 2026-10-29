using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace ApiTester.Core;

/// <summary>Every new connection the gateway opens goes through here: it is counted, and an
/// endpoint that keeps failing is left alone for a while instead of being dialled again and again.
/// <para>On a managed work machine every connection is seen, and often held, by software that sits
/// under the network stack: antivirus and endpoint protection, data-loss and web filters, the VDI's
/// own drivers. A gateway relaying ten sites can ask for a lot of connections, and when a site is
/// unreachable a browser keeps asking, so the gateway kept dialling: a burst of connection attempts
/// that all fail looks like a scan to endpoint protection, and each one that hangs holds something
/// in that software until it times out.</para>
/// <para>The TCP connect is watched by host and port: the site's, or the proxy's when there is one
/// (through a proxy, the proxy is what certapi connects to, so a pause of the proxy holds back every
/// site behind it). After <see cref="FailuresToPause"/> failures within <see cref="FailureWindow"/>
/// new attempts are refused for <see cref="FirstPause"/>, failing at once with a message saying so.
/// When the pause is over, one attempt is let through as a trial while the rest are still refused
/// (another, if the trial takes longer than the connect limit): if it fails, the endpoint is paused
/// again for twice as long (up to <see cref="LongestPause"/>); if it, or any attempt, succeeds, the
/// endpoint is as it was before. Failures of attempts that began before a pause, which arrive during
/// it, are not counted again, so a burst that fails together pauses an endpoint once.</para>
/// <para>A site the proxy refuses (its answer to the CONNECT) is the proxy's to report: it is
/// counted (<see cref="CountRefusedTunnel"/>), not paused. Refusing it from the connect step would
/// make .NET's own proxy failover mark a healthy proxy failed for half an hour, and a SOCKS proxy is
/// never asked for a CONNECT at all.</para>
/// <para>How many connections a site may have at once is the handler's
/// <c>MaxConnectionsPerServer</c>; this class only counts and pauses.</para></summary>
public sealed class GatewayConnectionGuard
{
    public const int FailuresToPause = 5;
    public static readonly TimeSpan FailureWindow = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan FirstPause = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan LongestPause = TimeSpan.FromSeconds(60);

    /// <summary>How long a trial attempt may take before another is let through, when the handler
    /// gave no connect limit of its own.</summary>
    public static readonly TimeSpan DefaultTrialLimit = TimeSpan.FromSeconds(15);

    /// <summary>What an attempt came to, for <see cref="End"/>.</summary>
    internal enum Outcome { Succeeded, Failed, Abandoned }

    private sealed class Endpoint
    {
        public int Generation;          // moves on at every pause and every recovery
        public int Failures;
        public long FirstFailureAt;
        public bool Paused;
        public long PausedUntil;
        public bool TrialOut;           // after a pause: the attempt let through is under way
        public long TrialSince;
        public TimeSpan NextPause = FirstPause;
    }

    /// <summary>An attempt the guard let through: the state its endpoint was in when it began, and
    /// whether it is the trial after a pause.</summary>
    internal readonly struct Ticket
    {
        internal Ticket(object endpoint, int generation, bool trial)
        {
            Endpoint = endpoint;
            Generation = generation;
            Trial = trial;
        }

        internal object Endpoint { get; }
        internal int Generation { get; }
        internal bool Trial { get; }
    }

    private readonly ConcurrentDictionary<string, Endpoint> _endpoints = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<long> _now;
    private long _opened, _failed, _refused, _attempting, _open;

    /// <param name="now">Milliseconds on a monotonic clock, for tests.</param>
    public GatewayConnectionGuard(Func<long>? now = null)
    {
        var clock = Stopwatch.StartNew();
        _now = now ?? (() => clock.ElapsedMilliseconds);
    }

    /// <summary>Connections opened so far (TCP connected; for an https upstream a TLS handshake follows).</summary>
    public long Opened => Interlocked.Read(ref _opened);

    /// <summary>The TCP connections the gateway holds open right now, to sites and proxies.</summary>
    public long Open => Interlocked.Read(ref _open);

    /// <summary>Connection attempts that failed so far, a proxy's refused tunnel included.</summary>
    public long Failed => Interlocked.Read(ref _failed);

    /// <summary>Attempts refused without dialling because their endpoint was paused.</summary>
    public long Refused => Interlocked.Read(ref _refused);

    /// <summary>Connection attempts in progress right now.</summary>
    public long Attempting => Interlocked.Read(ref _attempting);

    /// <summary>The endpoints paused right now: for how much longer, or (at zero) waiting for the
    /// trial attempt to finish.</summary>
    public IReadOnlyList<(string Endpoint, TimeSpan Remaining)> Paused()
    {
        long now = _now();
        var list = new List<(string, TimeSpan)>();
        foreach (var (key, e) in _endpoints)
            lock (e)
                if (e.Paused) list.Add((key, TimeSpan.FromMilliseconds(Math.Max(0, e.PausedUntil - now))));
        return list;
    }

    /// <summary>Wraps a handler's connect step: its own callback, or the plain socket connect the
    /// handler would otherwise make, given <paramref name="connectLimit"/> per step.</summary>
    public void Install(SocketsHttpHandler handler, TimeSpan? connectLimit = null)
    {
        var inner = handler.ConnectCallback;
        // The handler's own budget for the whole connection setup (--connect-timeout): when it runs
        // out, the handler cancels the connect, and that is the endpoint's failure, not a cancellation.
        var handlerLimit = handler.ConnectTimeout;
        handler.ConnectCallback = (context, ct) => ConnectAsync(context, inner, connectLimit, handlerLimit, ct);
    }

    internal async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context,
        Func<SocketsHttpConnectionContext, CancellationToken, ValueTask<Stream>>? inner,
        TimeSpan? connectLimit, TimeSpan handlerLimit, CancellationToken ct)
    {
        string key = context.DnsEndPoint.Host + ":" + context.DnsEndPoint.Port;
        var ticket = Begin(key, connectLimit ?? DefaultTrialLimit);
        Interlocked.Increment(ref _attempting);
        var started = Stopwatch.StartNew();
        var outcome = Outcome.Abandoned;
        try
        {
            var stream = inner is not null
                ? Counted(await inner(context, ct))
                : new CountedNetworkStream(await PlainConnectAsync(context, connectLimit, ct), this);
            Interlocked.Increment(ref _opened);
            outcome = Outcome.Succeeded;
            return stream;
        }
        // The handler stopped waiting for this connect. Usually that is not the endpoint's doing:
        // the request it was opened for was answered on another connection, or was cancelled. But a
        // connect that had already waited a whole limit without an answer (a name whose addresses
        // hang one after another), or that the handler's own connect timeout cut off, failed.
        catch (OperationCanceledException ex) when (ct.IsCancellationRequested)
        {
            bool hung = connectLimit is { } limit && started.Elapsed >= limit ||
                        handlerLimit != Timeout.InfiniteTimeSpan && started.Elapsed >= handlerLimit - TimeSpan.FromMilliseconds(100);
            if (hung)
            {
                Interlocked.Increment(ref _failed);
                outcome = Outcome.Failed;
            }
            // Under the handler's own token, which is how it knows the cancellation for its own.
            if (ex.CancellationToken != ct) throw new OperationCanceledException(ex.Message, ex, ct);
            throw;
        }
        // A refused certificate, or a handshake the server ended, from an inner step that does TLS:
        // the endpoint answered. Counting it would pause a site for a reason a pause cannot fix, and
        // hide the message that says why.
        catch (System.Security.Authentication.AuthenticationException)
        {
            Interlocked.Increment(ref _opened);
            outcome = Outcome.Succeeded;
            throw;
        }
        catch (Exception)
        {
            Interlocked.Increment(ref _failed);
            outcome = Outcome.Failed;
            throw;
        }
        finally
        {
            Interlocked.Decrement(ref _attempting);
            End(ticket, outcome);
        }
    }

    /// <summary>A tunnel the proxy refused to open (<see cref="HttpRequestError.ProxyTunnelError"/>):
    /// counted among the failed connection attempts.</summary>
    internal void CountRefusedTunnel() => Interlocked.Increment(ref _failed);

    /// <summary>Lets an attempt through, or refuses it because its endpoint is paused.</summary>
    internal Ticket Begin(string key, TimeSpan trialLimit)
    {
        var e = _endpoints.GetOrAdd(key, _ => new Endpoint());
        long now = _now();
        lock (e)
        {
            if (!e.Paused) return new Ticket(e, e.Generation, trial: false);
            bool trialUnderWay = e.TrialOut && now - e.TrialSince < trialLimit.TotalMilliseconds;
            if (now < e.PausedUntil || trialUnderWay)
            {
                Interlocked.Increment(ref _refused);
                throw new GatewayEndpointPausedException(key, trialUnderWay ? null : TimeSpan.FromMilliseconds(e.PausedUntil - now));
            }
            e.TrialOut = true;
            e.TrialSince = now;
            return new Ticket(e, e.Generation, trial: true);
        }
    }

    /// <summary>What an attempt came to. Called exactly once per <see cref="Begin"/> that returned.</summary>
    internal void End(Ticket ticket, Outcome outcome)
    {
        var e = (Endpoint)ticket.Endpoint;
        long now = _now();
        lock (e)
        {
            switch (outcome)
            {
                case Outcome.Succeeded:
                    // Any success means the endpoint works: a pause it is in ends now.
                    if (e.Paused) { e.Paused = false; e.TrialOut = false; e.Generation++; }
                    e.Failures = 0;
                    e.NextPause = FirstPause;
                    break;

                case Outcome.Failed:
                    if (ticket.Generation != e.Generation) break;   // began before the latest pause or recovery
                    if (ticket.Trial) { Pause(e, now); break; }
                    if (e.Paused) break;
                    if (e.Failures == 0 || now - e.FirstFailureAt > FailureWindow.TotalMilliseconds)
                    {
                        e.Failures = 0;
                        e.FirstFailureAt = now;
                    }
                    if (++e.Failures >= FailuresToPause) Pause(e, now);
                    break;

                case Outcome.Abandoned:
                    // A trial that ended without an answer either way: the next attempt is the trial.
                    if (ticket.Trial && ticket.Generation == e.Generation) e.TrialOut = false;
                    break;
            }
        }
    }

    private static void Pause(Endpoint e, long now)
    {
        e.Paused = true;
        e.PausedUntil = now + (long)e.NextPause.TotalMilliseconds;
        e.NextPause = TimeSpan.FromTicks(Math.Min(e.NextPause.Ticks * 2, LongestPause.Ticks));
        e.Failures = 0;
        e.TrialOut = false;
        e.Generation++;
    }

    /// <summary>What the handler does with no callback of its own: one socket, connected (within
    /// <paramref name="limit"/> per step, when there is one).</summary>
    private static async ValueTask<Socket> PlainConnectAsync(SocketsHttpConnectionContext context, TimeSpan? limit, CancellationToken ct)
    {
        if (limit is { } within)
            return await ConnectSocketAsync(context.DnsEndPoint.Host, context.DnsEndPoint.Port, within, null, ct);
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try { await socket.ConnectAsync(context.DnsEndPoint, ct); }
        catch { socket.Dispose(); throw; }
        return socket;
    }

    /// <summary>A TCP connection to <paramref name="host"/>:<paramref name="port"/>, as the gateway
    /// opens one: the name is looked up, then each address is tried in turn, and the lookup and each
    /// attempt get at most <paramref name="limit"/>. A connect that hangs — a route that drops it,
    /// or security software under the network stack holding it — then gives up instead of waiting
    /// for Windows' own timeout (about 21 seconds an address) or, held, for ever; and a name with an
    /// address that hangs (an IPv6 route that goes nowhere) still reaches the next one.</summary>
    /// <param name="resolve">Looks a name up; null for the system's own lookup.</param>
    public static async ValueTask<Socket> ConnectSocketAsync(string host, int port, TimeSpan limit,
        Func<string, CancellationToken, Task<IPAddress[]>>? resolve, CancellationToken ct)
    {
        IPAddress[] addresses = IPAddress.TryParse(host, out var literal)
            ? [literal]
            : await WithinAsync(t => (resolve ?? Dns.GetHostAddressesAsync)(host, t), limit, ct);
        if (addresses.Length == 0)
            throw new SocketException((int)SocketError.HostNotFound, $"{host} has no address.");
        Exception? last = null;
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await WithinAsync(async t => { await socket.ConnectAsync(new IPEndPoint(address, port), t); return true; }, limit, ct);
                return socket;
            }
            catch (Exception ex) when (ex is SocketException or GatewayConnectTimeoutException)
            {
                socket.Dispose();
                last = ex;   // the next address, if there is one
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
        throw last!;
    }

    /// <summary>Runs a TCP connect (name lookup included) for at most <paramref name="limit"/>. Past
    /// it the connect fails with <see cref="GatewayConnectTimeoutException"/>, an
    /// <see cref="IOException"/>: the handler reports that as a failed connection to this endpoint,
    /// which moves a proxy failover list on to its next proxy. Its own connect timeout instead
    /// surfaces as a cancellation, which does not.</summary>
    public static async ValueTask ConnectWithinAsync(Func<CancellationToken, ValueTask> connect, TimeSpan limit, CancellationToken ct) =>
        await WithinAsync(async t => { await connect(t); return true; }, limit, ct);

    private static async Task<T> WithinAsync<T>(Func<CancellationToken, Task<T>> step, TimeSpan limit, CancellationToken ct)
    {
        using var timer = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timer.CancelAfter(limit);
        try { return await step(timer.Token); }
        // Cancelled from outside: said with the caller's own token. The handler tells its own
        // cancellation (its connect timeout, or a request no longer waiting) by the token, and
        // reports a cancellation under any other token as a failed connection.
        catch (OperationCanceledException ex) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ex.Message, ex, ct);
        }
        catch (OperationCanceledException) when (timer.IsCancellationRequested)
        {
            throw new GatewayConnectTimeoutException(limit);
        }
    }

    /// <summary>The connection's stream, counted in <see cref="Open"/> until it is closed: .NET's own
    /// connection counter counts a tunnel through a proxy twice, one of them as busy for as long as
    /// the tunnel is open. A socket's stream stays a <see cref="NetworkStream"/>, which is where .NET
    /// reads the peer address it reports (--trace shows it) and the socket it polls before reusing
    /// an idle connection.</summary>
    private Stream Counted(Stream stream)
    {
        if (stream.GetType() != typeof(NetworkStream)) return new CountedStream(stream, this);
        // The callbacks here hand back a socket's own stream, which owns it: the counted one takes
        // the socket over, and the plain wrapper is let go (finalizing it would not close the socket).
        GC.SuppressFinalize(stream);
        return new CountedNetworkStream(((NetworkStream)stream).Socket, this);
    }

    private sealed class CountedNetworkStream : NetworkStream
    {
        private readonly GatewayConnectionGuard _owner;
        private int _closed;

        public CountedNetworkStream(Socket socket, GatewayConnectionGuard owner) : base(socket, ownsSocket: true)
        {
            _owner = owner;
            Interlocked.Increment(ref owner._open);
        }

        protected override void Dispose(bool disposing)
        {
            if (Interlocked.Exchange(ref _closed, 1) == 0) Interlocked.Decrement(ref _owner._open);
            base.Dispose(disposing);
        }
    }

    /// <summary>Any other stream, wrapped; its bytes pass through unchanged. Never a TLS stream: the
    /// handler tells a connection whose TLS is done by its type, and would negotiate TLS again over a
    /// wrapped one. That is why the --sni handshake is installed around the guard, not inside it
    /// (see MtlsGateway.CreateHandler).</summary>
    private sealed class CountedStream : Stream
    {
        private readonly Stream _inner;
        private readonly GatewayConnectionGuard _owner;
        private int _closed;

        public CountedStream(Stream inner, GatewayConnectionGuard owner)
        {
            _inner = inner;
            _owner = owner;
            Interlocked.Increment(ref owner._open);
        }

        private void Closed()
        {
            if (Interlocked.Exchange(ref _closed, 1) == 0) Interlocked.Decrement(ref _owner._open);
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanWrite => _inner.CanWrite;
        public override bool CanSeek => false;
        public override bool CanTimeout => _inner.CanTimeout;
        public override int ReadTimeout { get => _inner.ReadTimeout; set => _inner.ReadTimeout = value; }
        public override int WriteTimeout { get => _inner.WriteTimeout; set => _inner.WriteTimeout = value; }
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => _inner.Read(buffer);
        public override int ReadByte() => _inner.ReadByte();
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => _inner.ReadAsync(buffer, offset, count, ct);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => _inner.ReadAsync(buffer, ct);

        public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);
        public override void Write(ReadOnlySpan<byte> buffer) => _inner.Write(buffer);
        public override void WriteByte(byte value) => _inner.WriteByte(value);
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) => _inner.WriteAsync(buffer, offset, count, ct);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) => _inner.WriteAsync(buffer, ct);

        public override void Flush() => _inner.Flush();
        public override Task FlushAsync(CancellationToken ct) => _inner.FlushAsync(ct);
        public override Task CopyToAsync(Stream destination, int bufferSize, CancellationToken ct) => _inner.CopyToAsync(destination, bufferSize, ct);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Closed();
                _inner.Dispose();
            }
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            Closed();
            await _inner.DisposeAsync();
            GC.SuppressFinalize(this);
        }
    }
}

/// <summary>An attempt refused without dialling, because its endpoint kept failing. Thrown from the
/// connect step, where the handler adds the host and port it was dialling to the message itself.</summary>
public sealed class GatewayEndpointPausedException : IOException
{
    public GatewayEndpointPausedException(string endpoint, TimeSpan? remaining)
        : base(Describe(remaining)) => Endpoint = endpoint;

    /// <summary>host:port.</summary>
    public string Endpoint { get; }

    private static string Describe(TimeSpan? remaining)
    {
        int n = GatewayConnectionGuard.FailuresToPause;
        return remaining is { } r
            ? $"certapi paused new connections for {Math.Max(1, Math.Ceiling(r.TotalSeconds)):0} s after {n} failed attempts in a row"
            : $"certapi paused new connections after {n} failed attempts in a row; a trial connection is under way";
    }
}

/// <summary>A TCP connect that got no answer within the gateway's limit (15 s, or --connect-timeout).</summary>
public sealed class GatewayConnectTimeoutException(TimeSpan limit)
    : IOException($"The connection attempt got no answer within {limit.TotalSeconds:0.#} s")
{
    public TimeSpan Limit { get; } = limit;
}
