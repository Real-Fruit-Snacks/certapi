using System.Text;

namespace ApiTester.Core;

/// <summary>Accumulates the exchanges a gateway forwards into an HTTP Archive (HAR), for
/// `serve --record`. Thread-safe: the gateway handles connections concurrently, so entries arrive
/// from many threads and are appended under a lock. Nothing is written to disk until
/// <see cref="Save"/> at shutdown — a relay must not pay a file write per request.
/// <para>The recorded HAR is exactly what <see cref="HarReplaySource"/> reads back, so
/// `serve --record` today and `serve --replay` (or `mock --har`) tomorrow are two ends of one
/// format — capture a session against the live upstream, replay it when the upstream is gone.</para></summary>
public sealed class GatewayRecorder
{
    private readonly object _lock = new();
    private readonly List<HarEntry> _entries = new();
    private readonly bool _includeSecrets;

    public GatewayRecorder(bool includeSecrets = false) => _includeSecrets = includeSecrets;

    public int Count { get { lock (_lock) return _entries.Count; } }

    /// <summary>Record one forwarded exchange. <paramref name="requestBody"/> and
    /// <paramref name="responseBody"/> are the bytes as relayed; headers are the lists the gateway
    /// actually sent and received. <paramref name="url"/> is the absolute upstream URL the request
    /// reached, so a replay keys off the same path and query.</summary>
    public void Record(
        string method, string url,
        IReadOnlyList<KeyValuePair<string, string>> requestHeaders, byte[] requestBody, string? requestContentType,
        int status, string? reasonPhrase,
        IReadOnlyList<KeyValuePair<string, string>> responseHeaders, byte[] responseBody,
        double elapsedMs)
    {
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
                PostData = requestBody.Length == 0 ? null : HarPostData.From(requestBody, requestContentType)
            },
            Response = new HarResponse
            {
                Status = status,
                StatusText = reasonPhrase ?? "",
                Headers = Redact(responseHeaders),
                Content = BuildContent(responseBody, ContentTypeOf(responseHeaders)),
                RedirectUrl = responseHeaders.FirstOrDefault(h =>
                    h.Key.Equals("Location", StringComparison.OrdinalIgnoreCase)).Value ?? ""
            },
            Timings = new HarTimings { Wait = elapsedMs, Send = -1, Receive = -1 }
        };
        lock (_lock) _entries.Add(entry);
    }

    /// <summary>Write everything captured so far as a HAR document. Called once, at shutdown.</summary>
    public void Save(string path)
    {
        HarEntry[] snapshot;
        lock (_lock) snapshot = _entries.ToArray();
        // Written beside and moved into place: an interrupted save never leaves half a file where
        // the recording (or the one it replaces) was.
        string temp = path + ".tmp";
        File.WriteAllText(temp, HarWriter.Write(snapshot, GatewayVersion));
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
