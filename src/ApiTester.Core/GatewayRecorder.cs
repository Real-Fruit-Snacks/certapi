using System.Text;

namespace ApiTester.Core;

/// <summary>Accumulates the exchanges a gateway forwards into an HTTP Archive (HAR), for
/// `serve --record`. Thread-safe: the gateway handles connections concurrently, so entries arrive
/// from many threads and are appended under a lock. Nothing is written to disk until
/// <see cref="Save"/> at shutdown — a relay must not pay a file write per request.
/// <para>The recorded HAR is exactly what <see cref="HarReplaySource"/> reads back, so
/// `serve --record` today and `serve --replay` (or `mock --har`) tomorrow are two ends of one
/// format — capture a session against the live upstream, replay it when the upstream is gone.</para>
/// <para>Held in memory until shutdown, so bodies are kept within limits: one body up to
/// <see cref="MaxBodyBytes"/>, and all of them together, counted as the memory they take (a kept
/// body is a .NET string, two bytes a character) plus what requests still in flight have captured,
/// up to the budget (<see cref="DefaultBodyBudget"/>). An exchange past either limit is still
/// recorded, its body replaced by a comment saying why — a recording left running through a
/// working day must not grow until the machine pages.</para></summary>
public sealed class GatewayRecorder
{
    /// <summary>The largest single body kept (request or response).</summary>
    public const int MaxBodyBytes = 32 * 1024 * 1024;

    /// <summary>The memory all kept bodies may take together, by default.</summary>
    public const long DefaultBodyBudget = 128L * 1024 * 1024;

    private readonly object _lock = new();
    private readonly List<HarEntry> _entries = new();
    private readonly bool _includeSecrets;
    private readonly long _budget;
    private long _kept;       // memory the kept bodies take
    private long _reserved;   // memory captures still in flight have taken
    private int _omitted;

    public GatewayRecorder(bool includeSecrets = false, long bodyBudget = DefaultBodyBudget)
    {
        _includeSecrets = includeSecrets;
        _budget = bodyBudget;
    }

    public int Count { get { lock (_lock) return _entries.Count; } }

    /// <summary>Bodies left out so far because of the limits.</summary>
    public int OmittedBodies { get { lock (_lock) return _omitted; } }

    /// <summary>Whether a body may still be kept at all: once the budget is spent, the gateway stops
    /// capturing and simply relays.</summary>
    public bool KeepsBodies { get { lock (_lock) return _kept + _reserved < _budget; } }

    /// <summary>How much of one body to capture right now.</summary>
    public int CaptureLimit => KeepsBodies ? MaxBodyBytes : 0;

    /// <summary>A stream that relays <paramref name="inner"/> and keeps a copy of the body for this
    /// recording, within the per-body limit and the budget, which it shares with every other
    /// capture in flight.</summary>
    public CapturingStream Capture(Stream inner) => new(inner, MaxBodyBytes, this);

    internal bool TryReserve(long bytes)
    {
        lock (_lock)
        {
            if (_kept + _reserved + bytes > _budget) return false;
            _reserved += bytes;
            return true;
        }
    }

    internal void Release(long bytes)
    {
        if (bytes <= 0) return;
        lock (_lock) _reserved = Math.Max(0, _reserved - bytes);
    }

    /// <summary>Record one forwarded exchange. <paramref name="requestBody"/> and
    /// <paramref name="responseBody"/> are the bytes as relayed; headers are the lists the gateway
    /// actually sent and received. <paramref name="url"/> is the absolute upstream URL the request
    /// reached, so a replay keys off the same path and query.
    /// <para><paramref name="requestBodySize"/> and <paramref name="responseBodySize"/> are the full
    /// sizes when the bytes passed are not the whole body (it was larger than the capture limit);
    /// null means the bytes are all of it.</para></summary>
    public void Record(
        string method, string url,
        IReadOnlyList<KeyValuePair<string, string>> requestHeaders, byte[] requestBody, string? requestContentType,
        int status, string? reasonPhrase,
        IReadOnlyList<KeyValuePair<string, string>> responseHeaders, byte[] responseBody,
        double elapsedMs, long? requestBodySize = null, long? responseBodySize = null, long heldReservation = 0)
    {
        string? requestOmitted = TooLarge(requestBody.LongLength, requestBodySize);
        string? responseOmitted = TooLarge(responseBody.LongLength, responseBodySize);
        HarPostData? postData = requestOmitted is null && requestBody.Length > 0 ? HarPostData.From(requestBody, requestContentType) : null;
        HarContent? content = responseOmitted is null ? BuildContent(responseBody, ContentTypeOf(responseHeaders)) : null;
        // Charged by what keeping them costs: the text (or base64) the entry holds, as a string.
        requestOmitted ??= Charge(2L * (postData?.Text?.Length ?? 0), heldReservation);
        responseOmitted ??= Charge(2L * (content?.Text?.Length ?? 0), heldReservation);
        // The recorded URL is redacted for the same reason the headers below are: this archive is
        // written precisely so it can be replayed elsewhere or handed to someone.
        string recordedUrl = HarWriter.RecordedUrl(url, _includeSecrets);
        var (path, rawQuery) = SplitQuery(recordedUrl);
        var entry = new HarEntry
        {
            StartedDateTime = DateTimeOffset.UtcNow,
            Time = elapsedMs,
            Request = new HarRequest
            {
                Method = method,
                Url = recordedUrl,
                Headers = Redact(requestHeaders),
                QueryString = QueryString.Parse(rawQuery)
                    .Select(q => new HarNameValue(q.Key, q.Value)).ToList(),
                PostData = requestOmitted is not null
                    ? new HarPostData { MimeType = requestContentType ?? "", Text = "", Comment = requestOmitted }
                    : postData
            },
            Response = new HarResponse
            {
                Status = status,
                StatusText = reasonPhrase ?? "",
                Headers = Redact(responseHeaders),
                Content = responseOmitted is not null
                    ? new HarContent { Size = responseBodySize ?? responseBody.LongLength, MimeType = ContentTypeOf(responseHeaders) ?? "", Text = "", Comment = responseOmitted }
                    : content!,   // built whenever the body was not left out
                RedirectUrl = responseHeaders.FirstOrDefault(h =>
                    h.Key.Equals("Location", StringComparison.OrdinalIgnoreCase)).Value ?? ""
            },
            Timings = new HarTimings { Wait = elapsedMs, Send = -1, Receive = -1 }
        };
        lock (_lock) _entries.Add(entry);
    }

    /// <summary>Why a body that was not captured whole (<paramref name="fullSize"/> is more than
    /// the <paramref name="captured"/> bytes) is left out, or null when it was.</summary>
    private string? TooLarge(long captured, long? fullSize)
    {
        if (fullSize is not { } size || size <= captured) return null;
        lock (_lock) _omitted++;
        return size > MaxBodyBytes
            ? $"body not kept: {size:N0} bytes is over the {MaxBodyBytes / (1024 * 1024)} MB a recording keeps per body"
            : BudgetSpent(size);
    }

    /// <summary>Null when <paramref name="cost"/> fits in the budget, and counts it; otherwise why
    /// the body is left out. <paramref name="held"/> is what this exchange's own captures have
    /// reserved, about to be released: room the entry may use.</summary>
    private string? Charge(long cost, long held)
    {
        if (cost == 0) return null;
        lock (_lock)
        {
            if (_kept + Math.Max(0, _reserved - held) + cost > _budget)
            {
                _omitted++;
                return BudgetSpent(null);
            }
            _kept += cost;
            return null;
        }
    }

    private string BudgetSpent(long? size) =>
        "body not kept" + (size is { } n ? $" ({n:N0} bytes)" : "") +
        $": the recording reached its {_budget / (1024 * 1024)} MB limit for bodies";

    /// <summary>Write everything captured so far as a HAR document. Called once, at shutdown.</summary>
    public void Save(string path)
    {
        HarEntry[] snapshot;
        lock (_lock) snapshot = _entries.ToArray();
        // Written beside and moved into place: an interrupted save never leaves half a file where
        // the recording (or the one it replaces) was. Streamed, so the archive is not built a
        // second time as one string.
        string temp = path + ".tmp";
        using (var file = File.Create(temp)) HarWriter.WriteTo(file, snapshot, GatewayVersion);
        File.Move(temp, path, overwrite: true);
    }

    private const string GatewayVersion = "certapi serve --record";

    private List<HarNameValue> Redact(IReadOnlyList<KeyValuePair<string, string>> headers) =>
        headers
            // A recorded gateway session is a file people share; Authorization and Cookie are the
            // secrets, stripped by default and kept only under --record-include-secrets. Framing
            // headers a replay must not carry are dropped either way, matching HarReplaySource.
            .Where(h => !FramingHeaders.Contains(h.Key, StringComparer.OrdinalIgnoreCase))
            .Select(h => _includeSecrets || !SecretHeaders.IsSecret(h.Key)
                ? new HarNameValue(h.Key, h.Value)
                : new HarNameValue(h.Key, "REDACTED"))
            .ToList();

    private static readonly string[] FramingHeaders = { "Transfer-Encoding", "Connection", "Content-Length" };

    private static string? ContentTypeOf(IReadOnlyList<KeyValuePair<string, string>> headers) =>
        headers.FirstOrDefault(h => h.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)).Value;

    private static (string Path, string RawQuery) SplitQuery(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return (uri.AbsolutePath, uri.Query.StartsWith('?') ? uri.Query[1..] : uri.Query);
        return QueryString.Split(url);
    }

    private static HarContent BuildContent(byte[] body, string? contentType)
    {
        if (body.Length == 0) return new HarContent { Size = 0, MimeType = contentType ?? "", Text = "", Encoding = null };
        string text = Encoding.UTF8.GetString(body);
        bool binary = text.Contains('�');
        return binary
            ? new HarContent { Size = body.LongLength, MimeType = contentType ?? "", Text = Convert.ToBase64String(body), Encoding = "base64" }
            : new HarContent { Size = body.LongLength, MimeType = contentType ?? "", Text = text, Encoding = null };
    }
}

/// <summary>A stream that passes every byte through to (or from) another and keeps a copy of the
/// first <c>limit</c> bytes. Past the limit the copy is dropped and only the count goes on, so a
/// large body is still relayed as it streams but never held whole. Made by
/// <see cref="GatewayRecorder.Capture"/>, the copy also has to fit in the recording's budget,
/// reserved as it grows, so many large captures at once cannot add up past it.</summary>
public sealed class CapturingStream : Stream
{
    private readonly Stream _inner;
    private readonly int _limit;
    private readonly GatewayRecorder? _owner;
    private MemoryStream? _copy = new();
    private long _reserved;

    public CapturingStream(Stream inner, int limit) : this(inner, limit, null) { }

    internal CapturingStream(Stream inner, int limit, GatewayRecorder? owner)
    {
        _inner = inner;
        _limit = limit;
        _owner = owner;
    }

    /// <summary>Every byte that went through.</summary>
    public long Total { get; private set; }

    /// <summary>True when the copy was dropped (more went through than the limit, or the
    /// recording's budget had no room), so <see cref="Captured"/> is not the body.</summary>
    public bool Overflowed => _copy is null;

    /// <summary>What was kept: the whole body, or nothing once it overflowed.</summary>
    public byte[] Captured => _copy?.ToArray() ?? Array.Empty<byte>();

    /// <summary>What this capture holds of the recording's budget.</summary>
    public long Reserved => _reserved;

    /// <summary><see cref="Captured"/>, handing the reservation back: the recording charges the
    /// entry it builds from these bytes instead.</summary>
    public byte[] TakeCaptured()
    {
        var bytes = Captured;
        ReleaseReservation();
        return bytes;
    }

    private void Keep(ReadOnlySpan<byte> bytes)
    {
        Total += bytes.Length;
        if (_copy is null || bytes.Length == 0) return;
        // Reserved at two bytes a byte: what the copy becomes once it is kept as a string.
        if (_copy.Length + bytes.Length > _limit || (_owner is not null && !_owner.TryReserve(2L * bytes.Length)))
        {
            Drop();
            return;
        }
        if (_owner is not null) _reserved += 2L * bytes.Length;
        _copy.Write(bytes);
    }

    private void Drop()
    {
        _copy?.Dispose();
        _copy = null;
        ReleaseReservation();
    }

    private void ReleaseReservation()
    {
        _owner?.Release(_reserved);
        _reserved = 0;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        int n = _inner.Read(buffer, offset, count);
        Keep(buffer.AsSpan(offset, n));
        return n;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        int n = await _inner.ReadAsync(buffer, cancellationToken);
        Keep(buffer.Span[..n]);
        return n;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Write(byte[] buffer, int offset, int count)
    {
        _inner.Write(buffer, offset, count);
        Keep(buffer.AsSpan(offset, count));
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await _inner.WriteAsync(buffer, cancellationToken);
        Keep(buffer.Span);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush() => _inner.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);
    public override bool CanRead => _inner.CanRead;
    public override bool CanWrite => _inner.CanWrite;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => Total; set => throw new NotSupportedException(); }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        // The inner stream belongs to whoever opened it; only the copy is this stream's.
        if (disposing) Drop();
        base.Dispose(disposing);
    }
}
