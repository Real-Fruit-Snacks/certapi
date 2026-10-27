using System.Diagnostics.Tracing;

namespace ApiTester.Core;

/// <summary>Where the time of one request went. A phase that did not happen is null: DNS, connect
/// and TLS are all null on a reused connection, and DNS is null for an IP literal.</summary>
public sealed record PhaseTimings
{
    /// <summary>Name resolution.</summary>
    public TimeSpan? Dns { get; init; }
    /// <summary>TCP connect — to the proxy when there is one.</summary>
    public TimeSpan? Connect { get; init; }
    /// <summary>TLS handshake, client certificate included. Through a proxy this is the handshake
    /// with the origin inside the tunnel (plus the one with an https proxy, when there is one).</summary>
    public TimeSpan? Tls { get; init; }
    /// <summary>From the request being written to the response headers having arrived: the server's
    /// own time plus one round trip.</summary>
    public TimeSpan? Wait { get; init; }
    /// <summary>Reading the response body.</summary>
    public TimeSpan? Download { get; init; }
    /// <summary>From the start of the request to the response headers (time to first byte).</summary>
    public TimeSpan? FirstByte { get; init; }
    /// <summary>The whole request, as the HTTP stack measured it.</summary>
    public TimeSpan? Total { get; init; }
    /// <summary>True when the request went out on a connection that already existed.</summary>
    public bool Reused { get; init; }
    /// <summary>How many requests the measurement saw (redirect hops and retries each count); the
    /// phases above describe the last one, the one whose response was returned.</summary>
    public int Requests { get; init; }

    /// <summary>One line: <c>dns 2 ms · connect 1 ms · tls 14 ms · wait 31 ms · download 1 ms · total 52 ms</c>.</summary>
    public string Render()
    {
        var parts = new List<string>();
        void Add(string name, TimeSpan? t) { if (t is { } v) parts.Add($"{name} {Ms(v)}"); }
        if (Reused) parts.Add("connection reused");
        Add("dns", Dns);
        Add("connect", Connect);
        Add("tls", Tls);
        Add("wait", Wait);
        Add("first byte", FirstByte);
        Add("download", Download);
        Add("total", Total);
        if (Requests > 1) parts.Add($"last of {Requests} requests");
        return string.Join(" · ", parts);
    }

    private static string Ms(TimeSpan t) =>
        t.TotalMilliseconds < 10 ? $"{t.TotalMilliseconds:0.0} ms" : $"{t.TotalMilliseconds:0} ms";
}

/// <summary>Measures <see cref="PhaseTimings"/> from the events .NET's own HTTP, socket, TLS and
/// name-resolution sources write while a request runs — the same in-process events
/// <see cref="NetworkTrace"/> prints, read as durations. No packet capture and no administrator
/// rights; nothing is measured that the runtime itself does not report.
/// <para>The listener is process-wide, so it is meant for one request at a time, which is how the
/// command line sends.</para></summary>
public sealed class PhaseTimer : EventListener
{
    private static readonly string[] Sources =
        { "System.Net.Http", "System.Net.Sockets", "System.Net.Security", "System.Net.NameResolution" };

    private readonly object _gate = new();
    private readonly bool _ready;
    private Request? _current;
    private int _requests;

    private sealed class Request
    {
        public DateTime Start;
        public DateTime? DnsStart, DnsStop, ConnectStart, ConnectStop, TlsStart, TlsStop;
        public DateTime? Sent, HeadersIn, End;
        public TimeSpan Tls;    // summed: an https proxy and the origin both handshake
        public bool NewConnection;
    }

    public PhaseTimer()
    {
        _ready = true;
        // As in NetworkTrace: sources that already exist were offered before this constructor ran.
        foreach (var source in EventSource.GetSources()) Enable(source);
    }

    protected override void OnEventSourceCreated(EventSource eventSource)
    {
        if (!_ready) return;
        Enable(eventSource);
    }

    private void Enable(EventSource source)
    {
        if (Sources.Contains(source.Name, StringComparer.Ordinal))
            EnableEvents(source, EventLevel.Verbose, EventKeywords.All);
    }

    protected override void OnEventWritten(EventWrittenEventArgs e)
    {
        if (!_ready || e.EventSource is null || e.EventName is null) return;
        var at = e.TimeStamp;
        lock (_gate)
        {
            switch (e.EventSource.Name, e.EventName)
            {
                case ("System.Net.Http", "RequestStart"):
                    _current = new Request { Start = at };
                    _requests++;
                    break;
                case ("System.Net.NameResolution", "ResolutionStart"):
                    if (_current is { DnsStart: null } d) d.DnsStart = at;
                    break;
                case ("System.Net.NameResolution", "ResolutionStop"):
                    if (_current is { } d2) d2.DnsStop = at;
                    break;
                case ("System.Net.Sockets", "ConnectStart"):
                    if (_current is { ConnectStart: null } c) { c.ConnectStart = at; c.NewConnection = true; }
                    break;
                case ("System.Net.Sockets", "ConnectStop"):
                    if (_current is { } c2) c2.ConnectStop = at;
                    break;
                case ("System.Net.Security", "HandshakeStart"):
                    if (_current is { } t) { t.TlsStart = at; t.NewConnection = true; }
                    break;
                case ("System.Net.Security", "HandshakeStop"):
                    if (_current is { TlsStart: { } begun } t2) { t2.Tls += at - begun; t2.TlsStop = at; }
                    break;
                case ("System.Net.Http", "RequestHeadersStop"):
                case ("System.Net.Http", "RequestContentStop"):
                    if (_current is { } s) s.Sent = at;
                    break;
                case ("System.Net.Http", "ResponseHeadersStop"):
                    if (_current is { } h) h.HeadersIn = at;
                    break;
                case ("System.Net.Http", "ResponseContentStop"):
                case ("System.Net.Http", "RequestStop"):
                    if (_current is { } r) r.End = at > (r.End ?? DateTime.MinValue) ? at : r.End;
                    break;
            }
        }
    }

    /// <summary>The phases of the last request seen, or null when none was.</summary>
    public PhaseTimings? Result()
    {
        lock (_gate)
        {
            if (_current is not { } r) return null;
            TimeSpan? Span(DateTime? a, DateTime? b) => a is { } x && b is { } y && y >= x ? y - x : null;
            return new PhaseTimings
            {
                Dns = Span(r.DnsStart, r.DnsStop),
                Connect = Span(r.ConnectStart, r.ConnectStop),
                Tls = r.TlsStop is null ? null : r.Tls,
                Wait = Span(r.Sent, r.HeadersIn),
                FirstByte = Span(r.Start, r.HeadersIn),
                Download = Span(r.HeadersIn, r.End),
                Total = Span(r.Start, r.End ?? r.HeadersIn),
                Reused = !r.NewConnection,
                Requests = _requests,
            };
        }
    }
}
