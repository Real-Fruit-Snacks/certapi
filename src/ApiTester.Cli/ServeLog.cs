using System.Collections.Concurrent;
using System.Diagnostics;

namespace ApiTester.Cli;

/// <summary>The gateway's log: one line per request, written by a thread of its own so that the
/// console can never hold up a request.
/// <para>A console write blocks while the window is in QuickEdit selection (a click in it) and is
/// slow wherever the console is drawn remotely (a VDI). Written on the request path under one lock,
/// a single blocked write stopped every request the gateway was relaying, and the browser and every
/// page in it hung with it. Here requests only queue a line; the queue is bounded, and a line that
/// does not fit is counted and reported instead of waited for. The writer is a dedicated thread,
/// not a thread-pool task, so request handlers that block pool threads (a slow proxy lookup) cannot
/// starve it.</para>
/// <para>To a terminal at most <see cref="MaxLinesPerSecond"/> request lines a second are shown, and
/// the rest are summed up once a second: a page load through ten sites is hundreds of requests,
/// and a console scrolling at that rate costs the remote session's CPU and graphics far more than
/// the requests do. Warnings, 5xx answers and failed requests have a limit of their own, five
/// times as high, so ordinary traffic cannot crowd them out and an upstream that fails every
/// request still cannot scroll the console at the full request rate. A line held back from the
/// console, or refused by a full queue, still goes to <c>--log-file</c>. To a file or a pipe every
/// line is written.</para></summary>
internal sealed class ServeLog : IDisposable
{
    public const int MaxLinesPerSecond = 20;
    public const int QueueCapacity = 10_000;

    private readonly TextWriter _out;
    private readonly bool _rateLimit;
    private readonly int _maxPerSecond;
    private readonly Action<string>? _heldBack;
    private readonly BlockingCollection<(string Line, bool Request)> _queue;
    private readonly Thread _writer;
    private readonly Func<long> _now;
    private long _dropped;          // did not fit in the queue
    private int _suppressed;        // over the per-second limit, in the current window
    private int _shownThisWindow;
    private int _importantThisWindow;
    private long _windowStart;

    /// <param name="rateLimit">True when the output is a terminal.</param>
    /// <param name="heldBack">Gets each line the rate limit keeps off the console (the
    /// <c>--log-file</c> sink), so a log file still has every line.</param>
    /// <param name="now">Milliseconds, for tests; the default is a monotonic clock.</param>
    public ServeLog(TextWriter output, bool rateLimit, int maxPerSecond = MaxLinesPerSecond,
                    int capacity = QueueCapacity, Func<long>? now = null, Action<string>? heldBack = null)
    {
        _out = output;
        _rateLimit = rateLimit;
        _maxPerSecond = maxPerSecond;
        _heldBack = heldBack;
        var clock = Stopwatch.StartNew();
        _now = now ?? (() => clock.ElapsedMilliseconds);
        _windowStart = _now();
        _queue = new BlockingCollection<(string Line, bool Request)>(new ConcurrentQueue<(string, bool)>(), capacity);
        _writer = new Thread(WriteLoop) { IsBackground = true, Name = "certapi serve log" };
        _writer.Start();
    }

    /// <summary>Queue a line about the gateway itself (startup, shutdown): never rate-limited.
    /// Never blocks.</summary>
    public void Write(string line) => Enqueue(line, request: false);

    /// <summary>Queue a line about one request: rate-limited to a terminal. Never blocks.</summary>
    public void WriteRequest(string line) => Enqueue(line, request: true);

    private void Enqueue(string line, bool request)
    {
        bool added;
        try { added = _queue.TryAdd((line, request)); }
        catch (InvalidOperationException) { return; }   // after shutdown: nothing is written any more
        if (!added)
        {
            Interlocked.Increment(ref _dropped);
            try { _heldBack?.Invoke(line); } catch { /* the log file is a convenience; never break a request */ }
        }
    }

    /// <summary>Lines with the higher limit: warnings and errors, and requests that failed or got a
    /// 5xx answer ("GET /x  -> 502 (…)", "-> websocket failed").</summary>
    internal static bool Important(string line)
    {
        if (line.StartsWith("warning", StringComparison.OrdinalIgnoreCase) ||
            line.StartsWith("error", StringComparison.OrdinalIgnoreCase))
            return true;
        int arrow = line.IndexOf("  -> ", StringComparison.Ordinal);
        if (arrow < 0) return false;
        var after = line.AsSpan(arrow + 5);
        if (after.StartsWith("websocket failed", StringComparison.Ordinal)) return true;
        return after.Length >= 3 && after[0] == '5' && char.IsAsciiDigit(after[1]) && char.IsAsciiDigit(after[2]) &&
               (after.Length == 3 || !char.IsAsciiDigit(after[3]));
    }

    private void WriteLoop()
    {
        while (true)
        {
            bool got;
            (string Line, bool Request) item;
            // Wake at least once a second, so a burst's summary is shown when the traffic stops too.
            try { got = _queue.TryTake(out item, 1000); }
            catch (InvalidOperationException) { break; }
            // A failure here must not end the thread, which would end the process with it.
            try
            {
                if (got) Emit(item.Line, item.Request);
                else if (_queue.IsCompleted) break;
                if (_queue.Count == 0) Flush(final: false);
            }
            catch (Exception) { /* a line lost is better than a gateway lost */ }
        }
        try { Flush(final: true); } catch (Exception) { }
    }

    private void Emit(string line, bool request)
    {
        RollWindow();
        if (_rateLimit && request)
        {
            bool important = Important(line);
            ref int shown = ref important ? ref _importantThisWindow : ref _shownThisWindow;
            if (shown >= (important ? _maxPerSecond * 5 : _maxPerSecond))
            {
                _suppressed++;
                try { _heldBack?.Invoke(line); } catch { }
                return;
            }
            shown++;
        }
        WriteLine(line);
    }

    private void RollWindow()
    {
        long now = _now();
        if (now - _windowStart < 1000) return;
        Summarize();
        _windowStart = now;
        _shownThisWindow = 0;
        _importantThisWindow = 0;
    }

    private void Summarize()
    {
        if (_suppressed > 0)
        {
            WriteLine($"… and {_suppressed} more request{(_suppressed == 1 ? "" : "s")} in that second " +
                      $"(the console shows {_maxPerSecond} a second; 2> file keeps every line, -q none).");
            _suppressed = 0;
        }
        long dropped = Interlocked.Exchange(ref _dropped, 0);
        if (dropped > 0)
            WriteLine($"… {dropped} log line{(dropped == 1 ? "" : "s")} not written: the console could not keep up " +
                      "(is the window in a selection? Esc ends it).");
    }

    private void Flush(bool final)
    {
        if (final || _now() - _windowStart >= 1000) { Summarize(); _windowStart = _now(); _shownThisWindow = 0; _importantThisWindow = 0; }
    }

    private void WriteLine(string line)
    {
        try { _out.WriteLine(line); }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or UnauthorizedAccessException)
        { /* the console is gone; nothing to tell */ }
    }

    /// <summary>Stops taking lines and waits until everything queued is written, for at most
    /// <paramref name="timeout"/>. Used at shutdown, while QuickEdit is still off, and by tests.
    /// Lines a still-blocked console has not taken by then are not written.</summary>
    public bool Drain(TimeSpan timeout)
    {
        try { _queue.CompleteAdding(); } catch (ObjectDisposedException) { }
        return _writer.Join(timeout);
    }

    public void Dispose() => Drain(TimeSpan.FromSeconds(2));
}
