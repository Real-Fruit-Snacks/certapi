using System.Text.RegularExpressions;
using System.Diagnostics.Tracing;
using System.Text;

namespace ApiTester.Core;

/// <summary>How much of the network stack's own chatter to surface.</summary>
public enum TraceLevel
{
    /// <summary>The lifecycle events with stable names: request, connection, DNS, TCP, TLS.
    /// Enough to answer "was this connection reused, and where did the time go".</summary>
    Normal,
    /// <summary>Adds the runtime's internal diagnostic sources, which are far more detailed and
    /// far less stable — free-text handler messages, security-context buffers. Useful when the
    /// normal level is not enough, never something to parse.</summary>
    Verbose
}

/// <summary>One line of network trace: when it happened, which source emitted it, and what it
/// said.</summary>
public sealed record TraceLine(TimeSpan At, string Source, string Event, string Detail)
{
    public override string ToString() =>
        $"[{At.TotalMilliseconds,8:F1} ms] {Source,-28} {Event,-24} {Detail}";
}

/// <summary>Surfaces what .NET's own networking stack reports about the connections this process
/// makes: connection establishment and reuse, DNS and TCP and TLS timing, and the request
/// lifecycle. Everything is in-process — an <see cref="EventListener"/> over the runtime's own
/// event sources — so it needs no driver, no administrator rights, and no capture tooling. That is
/// strictly more than a packet sniffer can see on an encrypted connection, and strictly less than
/// one sees below TCP; the documentation says so rather than implying otherwise.
/// <para>The source and event names here were observed from a running .NET 9 process rather than
/// taken from documentation — see the program's plan for the recorded list.</para></summary>
public sealed class NetworkTrace : EventListener
{
    // Observed on .NET 9: the sources whose event NAMES are stable enough to key behaviour off.
    private static readonly string[] StableSources =
    {
        "System.Net.Http", "System.Net.Sockets", "System.Net.Security", "System.Net.NameResolution"
    };

    private readonly object _gate = new();
    private readonly List<TraceLine> _lines = new();
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

    // Set before any source is enabled; OnEventSourceCreated can fire during base construction,
    // which is why every field it touches must be readable at that moment.
    private readonly TraceLevel _level;
    private readonly IReadOnlyList<string> _filters;
    private readonly Action<TraceLine>? _onLine;
    private readonly bool _includeSecrets;

    /// <param name="filters">Case-insensitive substrings; a line is kept when it contains any of
    /// them. Empty keeps everything — this is a firehose, and narrowing it is often the only way
    /// to read it.</param>
    /// <param name="onLine">Called as each line is produced, for a caller that wants to stream
    /// rather than collect. Collection happens either way.</param>
    /// <param name="includeSecrets">Keep credential-looking payload values instead of redacting
    /// them. Off by default, because a trace is a file people paste into tickets and this feature
    /// leaking a token would be it causing the very problem it helps diagnose.</param>
    public NetworkTrace(TraceLevel level = TraceLevel.Normal,
                        IReadOnlyList<string>? filters = null,
                        Action<TraceLine>? onLine = null,
                        bool includeSecrets = false)
    {
        _level = level;
        _filters = filters ?? Array.Empty<string>();
        _onLine = onLine;
        _includeSecrets = includeSecrets;

        // EventListener's own constructor calls OnEventSourceCreated for every source that
        // already exists — and it does so BEFORE this derived constructor's fields are assigned,
        // so those callbacks cannot decide anything and deliberately do nothing. Sweeping the
        // existing sources here, now that the settings exist, is what actually subscribes to them.
        // (In practice the System.Net sources are created lazily on first use, so most arrive
        // later through the callback; this covers the case where a request already ran.)
        foreach (var source in EventSource.GetSources()) EnableIfWanted(source);
    }

    public IReadOnlyList<TraceLine> Lines { get { lock (_gate) return _lines.ToArray(); } }

    protected override void OnEventSourceCreated(EventSource eventSource)
    {
        // Fires from the base constructor for sources that already exist, at which point this
        // instance's fields are still null — nothing can be decided then, and the constructor's
        // own sweep handles those. Everything created afterwards arrives here properly built.
        if (_filters is null) return;
        EnableIfWanted(eventSource);
    }

    private void EnableIfWanted(EventSource source)
    {
        if (!Wanted(source.Name)) return;
        EnableEvents(source, EventLevel.Verbose, EventKeywords.All);
    }

    /// <summary>Which sources this level subscribes to. Pure, so the rule is testable without a
    /// live event source.</summary>
    internal bool Wanted(string sourceName)
    {
        if (StableSources.Contains(sourceName, StringComparer.Ordinal)) return true;
        return _level == TraceLevel.Verbose &&
               sourceName.StartsWith("Private.InternalDiagnostics.System.Net", StringComparison.Ordinal);
    }

    protected override void OnEventWritten(EventWrittenEventArgs eventData)
    {
        if (_filters is null || eventData.EventSource is null) return;
        if (!Wanted(eventData.EventSource.Name)) return;

        var line = new TraceLine(
            _clock.Elapsed,
            eventData.EventSource.Name,
            eventData.EventName ?? $"event {eventData.EventId}",
            Describe(eventData, _includeSecrets));

        if (!Keep(line)) return;

        lock (_gate) _lines.Add(line);
        _onLine?.Invoke(line);
    }

    /// <summary>Whether a line survives the filters. Pure and internal so the rule is testable.</summary>
    internal bool Keep(TraceLine line)
    {
        if (_filters.Count == 0) return true;
        foreach (var filter in _filters)
            if (line.Source.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                line.Event.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                line.Detail.Contains(filter, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    /// <summary>An event's payload as one line, with anything that looks like a credential
    /// redacted. A trace is a file people paste into tickets; a bearer token appearing in one
    /// would be this feature causing the leak it exists to help diagnose.</summary>
    internal static string Describe(EventWrittenEventArgs eventData, bool includeSecrets = false)
    {
        if (eventData.Payload is null || eventData.Payload.Count == 0) return "";

        var sb = new StringBuilder();
        for (int i = 0; i < eventData.Payload.Count; i++)
        {
            if (sb.Length > 0) sb.Append(' ');
            string name = eventData.PayloadNames is { } names && i < names.Count ? names[i] : $"arg{i}";
            string value = eventData.Payload[i]?.ToString() ?? "";
            sb.Append(name).Append('=').Append(includeSecrets ? value : Redact(name, value));
        }
        return sb.ToString();
    }

    private static readonly string[] SecretNames = { "authorization", "cookie", "token", "password", "secret" };

    /// <summary>Headers whose value is a credential wherever they appear in a payload. Challenges
    /// (<c>WWW-Authenticate</c>, <c>Proxy-Authenticate</c>) are deliberately not here: they are what
    /// a trace of a failing proxy or SSO handshake is read for, and they carry no secret.</summary>
    private static readonly Regex CredentialHeader = new(
        @"(?<=^|[\r\n])(?<name>Authorization|Proxy-Authorization|Cookie|Set-Cookie|X-Api-Key|X-Auth-Token|X-Csrf-Token|X-XSRF-Token|[A-Za-z0-9-]*(?:token|secret|password|api-?key)[A-Za-z0-9-]*):[^\r\n]*",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Anything shaped like a URL inside a payload: the runtime's events carry request
    /// and proxy addresses, and those can hold <c>user:pass@</c> or an <c>access_token=</c>.</summary>
    private static readonly Regex UrlInPayload = new(
        @"[A-Za-z][A-Za-z0-9+.-]*://[^\s""'<>]+", RegexOptions.CultureInvariant);

    internal static string Redact(string name, string value)
    {
        if (SecretNames.Any(s => name.Contains(s, StringComparison.OrdinalIgnoreCase)))
            return "(redacted)";
        // A payload can carry a whole header block as one string; every credential line in it
        // loses its value and keeps its name — the fact it was sent is what a reader needs.
        value = CredentialHeader.Replace(value, m => m.Groups["name"].Value + ": (redacted)");
        // A URL keeps its address and loses its userinfo password and credential-named query values.
        return UrlInPayload.Replace(value, m => MarkdownSecrets.RedactUrl(m.Value));
    }
}
