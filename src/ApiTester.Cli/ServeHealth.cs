using System.Diagnostics;
using ApiTester.Core;

namespace ApiTester.Cli;

/// <summary>What the gateway is holding, sampled once a minute: requests, connections, threads,
/// handles and memory. Written to the log as a timestamped <c>health</c> line while there is
/// traffic, served at <c>/_certapi/health</c>, and turned into a warning when something grows
/// past what a gateway should need.
/// <para>When a session freezes, the minutes before it are what tell its cause apart: a gateway
/// that piled up connections or threads, or one that sat quietly while the machine went down
/// around it. Written to a file on a local disk that survives a restart, these lines are that
/// record.</para></summary>
internal sealed class ServeHealth
{
    // Past these the gateway holds more than relaying a browser's traffic needs; each is said once
    // every ten minutes at most.
    public const int FailedConnectsPerMinute = 20;
    public const long OpenConnections = 200;
    public const int Threads = 400;
    public const int Handles = 20_000;
    public const long PrivateBytes = 2L * 1024 * 1024 * 1024;
    // Windows has about 16,000 ports for outgoing connections (49152-65535) by default; when they
    // are gone, every program's new connections fail until enough close (a closed one keeps its
    // port for about four minutes, in TIME_WAIT).
    public const int MachineTcp = 10_000;
    private static readonly TimeSpan WarnEvery = TimeSpan.FromMinutes(10);
    // The process's and the machine's figures each take a snapshot of every process, or of every
    // TCP connection, on the machine: read at most this often for /_certapi/health.
    private static readonly TimeSpan ReadEvery = TimeSpan.FromSeconds(5);

    private readonly GatewayConnectionGuard _guard;
    private readonly Func<int> _inFlight;
    private readonly Action<string> _log;
    private readonly Func<DateTime> _clock;
    private readonly Stopwatch _elapsed = Stopwatch.StartNew();   // intervals; the clock is only shown
    private readonly Dictionary<string, TimeSpan> _lastWarned = new();
    private long _requests, _busy;
    private long _lastRequests, _lastOpened, _lastFailed, _lastRefused, _lastBusy;
    private (int Threads, int Handles, long PrivateBytes) _process;
    private (int Total, int Closing) _machine = (-1, -1);
    private TimeSpan? _readAt;   // when the process's and the machine's figures were last read

    public ServeHealth(GatewayConnectionGuard guard, Func<int> inFlight, Action<string> log, Func<DateTime>? clock = null)
    {
        _guard = guard;
        _inFlight = inFlight;
        _log = log;
        _clock = clock ?? (() => DateTime.Now);
    }

    /// <summary>A request was accepted.</summary>
    public void Request() => Interlocked.Increment(ref _requests);

    /// <summary>A request was turned away because --max-requests were already in flight.</summary>
    public void Busy() => Interlocked.Increment(ref _busy);

    public sealed record Snapshot(
        DateTime At, long Requests, long RequestsSinceLast, int InFlight, long Busy,
        long ConnectionsOpen, long ConnectionsOpened, long OpenedSinceLast,
        long ConnectAttempting, long ConnectsFailed, long FailedSinceLast, long Refused,
        IReadOnlyList<string> PausedEndpoints, int Threads, int Handles, long PrivateBytes,
        int ThreadPoolThreads, long ThreadPoolQueued, int MachineTcp, int MachineTcpClosing);

    private readonly object _gate = new();

    /// <summary>The figures now, and since the previous minute's sample; <paramref name="advance"/>
    /// starts the next minute (the timer does; a /_certapi/health request does not).</summary>
    public Snapshot Sample(bool advance = false)
    {
        lock (_gate) return SampleLocked(advance);
    }

    private Snapshot SampleLocked(bool advance)
    {
        long requests = Interlocked.Read(ref _requests), opened = _guard.Opened, failed = _guard.Failed,
             refused = _guard.Refused, busy = Interlocked.Read(ref _busy);
        if (advance || _readAt is not { } readAt || _elapsed.Elapsed - readAt >= ReadEvery) ReadProcessAndMachine();

        var snapshot = new Snapshot(
            _clock(), requests, requests - _lastRequests, _inFlight(), busy,
            _guard.Open, opened, opened - _lastOpened,
            _guard.Attempting, failed, failed - _lastFailed, refused,
            _guard.Paused().Select(p => p.Remaining > TimeSpan.Zero
                ? $"{p.Endpoint} ({Math.Ceiling(p.Remaining.TotalSeconds):0} s)"
                : $"{p.Endpoint} (trial)").ToList(),
            _process.Threads, _process.Handles, _process.PrivateBytes,
            ThreadPool.ThreadCount, ThreadPool.PendingWorkItemCount, _machine.Total, _machine.Closing);
        if (advance) { _lastRequests = requests; _lastOpened = opened; _lastFailed = failed; _lastRefused = refused; _lastBusy = busy; }
        return snapshot;
    }

    private void ReadProcessAndMachine()
    {
        _readAt = _elapsed.Elapsed;
        try
        {
            using var process = Process.GetCurrentProcess();
            _process = (process.Threads.Count, process.HandleCount, process.PrivateMemorySize64);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception) { }
        // The whole machine's TCP table, not only this process's: running out of ports is the one
        // failure that takes every program's network with it.
        try
        {
            var connections = System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpConnections();
            _machine = (connections.Length, connections.Count(c => c.State is System.Net.NetworkInformation.TcpState.TimeWait
                or System.Net.NetworkInformation.TcpState.CloseWait or System.Net.NetworkInformation.TcpState.FinWait1
                or System.Net.NetworkInformation.TcpState.FinWait2 or System.Net.NetworkInformation.TcpState.LastAck
                or System.Net.NetworkInformation.TcpState.Closing));
        }
        catch (Exception ex) when (ex is System.Net.NetworkInformation.NetworkInformationException or PlatformNotSupportedException or IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Sample, log the health line when anything happened since the last one, and warn
    /// about what has grown too far.</summary>
    public void Tick()
    {
        long lastBusy, lastRefused;
        Snapshot s;
        lock (_gate)
        {
            lastBusy = _lastBusy; lastRefused = _lastRefused;
            s = SampleLocked(advance: true);
        }
        bool activity = s.RequestsSinceLast > 0 || s.OpenedSinceLast > 0 || s.FailedSinceLast > 0 ||
                        s.InFlight > 0 || s.Busy != lastBusy || s.Refused != lastRefused;
        if (activity) _log(Line(s));

        if (s.FailedSinceLast >= FailedConnectsPerMinute)
            Warn("failed", $"warning: {s.FailedSinceLast} connection attempts failed in the last minute" +
                 (s.PausedEndpoints.Count > 0 ? $" (paused: {string.Join(", ", s.PausedEndpoints)})" : "") +
                 ". Something between this machine and the sites is refusing or dropping connections.");
        if (s.ConnectionsOpen > OpenConnections)
            Warn("connections", $"warning: the gateway holds {s.ConnectionsOpen} open connections, " +
                 "more than relaying a browser's pages needs. --max-connections lowers the most per site.");
        if (s.Threads > Threads)
            Warn("threads", $"warning: the gateway is running {s.Threads} threads. If this keeps rising, stop it (Ctrl+C) and keep this log.");
        if (s.Handles > Handles)
            Warn("handles", $"warning: the gateway holds {s.Handles:N0} handles. If this keeps rising, stop it (Ctrl+C) and keep this log.");
        if (s.MachineTcp >= MachineTcp)
            Warn("ports", $"warning: this machine has {s.MachineTcp:N0} TCP connections ({s.MachineTcpClosing:N0} closing). " +
                 "Windows has about 16,000 ports for new ones; when they run out, every program's new connections fail. " +
                 "netstat -ano shows which process holds them.");
        if (s.PrivateBytes > PrivateBytes)
            Warn("memory", $"warning: the gateway uses {s.PrivateBytes / (1024 * 1024):N0} MB. If this keeps rising, stop it (Ctrl+C) and keep this log.");
    }

    private void Warn(string what, string message)
    {
        var now = _elapsed.Elapsed;
        if (_lastWarned.TryGetValue(what, out var last) && now - last < WarnEvery) return;
        _lastWarned[what] = now;
        _log(message);
    }

    internal static string Line(Snapshot s) =>
        $"health {s.At:HH:mm:ss}: {s.RequestsSinceLast} request{Plural(s.RequestsSinceLast)} in the last minute ({s.InFlight} now" +
        (s.Busy > 0 ? $", {s.Busy} turned away so far" : "") + ")" +
        $" · connections {s.ConnectionsOpen} open, {s.OpenedSinceLast} new, {s.FailedSinceLast} failed" +
        (s.PausedEndpoints.Count > 0 ? $", paused: {string.Join(", ", s.PausedEndpoints)}" : "") +
        $" · {s.Threads} threads · {s.Handles:N0} handles · {s.PrivateBytes / (1024 * 1024):N0} MB" +
        (s.MachineTcp >= 0 ? $" · machine {s.MachineTcp:N0} TCP ({s.MachineTcpClosing:N0} closing)" : "");

    private static string Plural(long n) => n == 1 ? "" : "s";

    /// <summary>The figures as the /_certapi/health JSON carries them.</summary>
    public object Describe()
    {
        var s = Sample();
        return new
        {
            requests = s.Requests, inFlight = s.InFlight, turnedAway = s.Busy,
            connections = new { open = s.ConnectionsOpen, opened = s.ConnectionsOpened, connecting = s.ConnectAttempting, failed = s.ConnectsFailed, refusedWhilePaused = s.Refused, paused = s.PausedEndpoints },
            process = new { threads = s.Threads, handles = s.Handles, privateMegabytes = s.PrivateBytes / (1024 * 1024), threadPoolThreads = s.ThreadPoolThreads, threadPoolQueued = s.ThreadPoolQueued },
            machine = new { tcpConnections = s.MachineTcp, tcpClosing = s.MachineTcpClosing },
        };
    }
}
