using System.Text;

namespace ApiTester.Core;

/// <summary>How an incoming request matched the recorded session.</summary>
public enum ReplayMatch { None, Exact, Path }

public sealed record RecordedResponse(
    int Status, string? StatusText,
    IReadOnlyList<KeyValuePair<string, string>> Headers,
    byte[] Body, string? ContentType);

public sealed record HarReplayOptions
{
    /// <summary>Match the query string too, not just method+path. Default true; falling back to a
    /// path-only match when no query match exists.</summary>
    public bool MatchQuery { get; init; } = true;
    /// <summary>Serve repeated calls to the same route in recorded order, then repeat the last one.
    /// Default true — a session that polled an endpoint replays its sequence.</summary>
    public bool SequentialRepeats { get; init; } = true;
    /// <summary>Status for a request that matches nothing. Default 404.</summary>
    public int NoMatchStatus { get; init; } = 404;
}

/// <summary>Serves a captured HAR back as recorded responses. Pure logic — no sockets; thread-safe,
/// because MockServer calls Match from concurrent connection handlers.</summary>
public sealed class HarReplaySource
{
    private sealed class Bucket
    {
        public readonly List<RecordedResponse> Items = new();
        public int Cursor;
    }

    private readonly object _lock = new();
    private readonly Dictionary<(string Method, string Path, string Query), Bucket> _exact = new();
    private readonly Dictionary<(string Method, string Path), Bucket> _byPath = new();

    public int Count { get; }
    public HarReplayOptions Options { get; }

    public HarReplaySource(Har har, HarReplayOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(har);
        Options = options ?? new HarReplayOptions();

        int count = 0;
        foreach (var entry in har.Log.Entries)
        {
            var (path, rawQuery) = SplitUrl(entry.Request.Url);
            string method = NormalizeMethod(entry.Request.Method);
            string normalizedQuery = NormalizeQuery(entry.Request.QueryString.Count > 0
                ? entry.Request.QueryString.Select(q => new KeyValuePair<string, string>(q.Name, q.Value))
                : QueryString.Parse(rawQuery));

            var recorded = BuildRecordedResponse(entry.Response);

            GetOrAdd(_exact, (method, path, normalizedQuery)).Items.Add(recorded);
            GetOrAdd(_byPath, (method, path)).Items.Add(recorded);
            count++;
        }
        Count = count;
    }

    /// <summary>The recorded response for this request, or null when nothing matches.</summary>
    public RecordedResponse? Match(string method, string pathAndQuery) => Match(method, pathAndQuery, out _);

    /// <summary>As <see cref="Match(string,string)"/>, also reporting how it matched — the mock's
    /// per-request log line says exact-match, path-match, or miss.</summary>
    public RecordedResponse? Match(string method, string pathAndQuery, out ReplayMatch kind)
    {
        string normalizedMethod = NormalizeMethod(method);
        var (path, rawQuery) = SplitUrl(pathAndQuery ?? "");
        string normalizedQuery = NormalizeQuery(QueryString.Parse(rawQuery));

        if (Options.MatchQuery && _exact.TryGetValue((normalizedMethod, path, normalizedQuery), out var exactBucket))
        {
            kind = ReplayMatch.Exact;
            return Serve(exactBucket);
        }

        if (_byPath.TryGetValue((normalizedMethod, path), out var pathBucket))
        {
            kind = ReplayMatch.Path;
            return Serve(pathBucket);
        }

        kind = ReplayMatch.None;
        return null;
    }

    private RecordedResponse Serve(Bucket bucket)
    {
        lock (_lock)
        {
            if (!Options.SequentialRepeats) return bucket.Items[0];
            var item = bucket.Items[bucket.Cursor];
            if (bucket.Cursor < bucket.Items.Count - 1) bucket.Cursor++;
            return item;
        }
    }

    private static TBucket GetOrAdd<TKey, TBucket>(Dictionary<TKey, TBucket> dict, TKey key) where TKey : notnull where TBucket : new()
    {
        if (!dict.TryGetValue(key, out var bucket))
        {
            bucket = new TBucket();
            dict[key] = bucket;
        }
        return bucket;
    }

    private static (string Path, string RawQuery) SplitUrl(string url)
    {
        url ??= "";
        string path, rawQuery;
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            path = uri.AbsolutePath;
            rawQuery = uri.Query.StartsWith('?') ? uri.Query[1..] : uri.Query;
        }
        else
        {
            (path, rawQuery) = QueryString.Split(url);
        }
        return (string.IsNullOrEmpty(path) ? "/" : path, rawQuery);
    }

    private static string NormalizeMethod(string? method) =>
        string.IsNullOrWhiteSpace(method) ? "GET" : method.ToUpperInvariant();

    private static string NormalizeQuery(IEnumerable<KeyValuePair<string, string>> pairs)
    {
        var ordered = pairs
            .OrderBy(p => p.Key, StringComparer.Ordinal)
            .ThenBy(p => p.Value, StringComparer.Ordinal)
            .Select(p => $"{p.Key}={p.Value}");
        return string.Join("&", ordered);
    }

    private static readonly string[] ExcludedHeaders = { "Transfer-Encoding", "Connection", "Content-Length" };

    private static RecordedResponse BuildRecordedResponse(HarResponse response)
    {
        var headers = response.Headers
            .Where(h => !h.Name.StartsWith(':') &&
                        !ExcludedHeaders.Any(e => string.Equals(e, h.Name, StringComparison.OrdinalIgnoreCase)))
            .Select(h => new KeyValuePair<string, string>(h.Name, h.Value))
            .ToList();

        byte[] body;
        if (string.Equals(response.Content.Encoding, "base64", StringComparison.OrdinalIgnoreCase))
        {
            try { body = string.IsNullOrEmpty(response.Content.Text) ? Array.Empty<byte>() : Convert.FromBase64String(response.Content.Text); }
            catch (FormatException) { body = Array.Empty<byte>(); }
        }
        else
        {
            body = string.IsNullOrEmpty(response.Content.Text) ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(response.Content.Text);
        }
        // No body, so nothing is encoded: a recording that left a gzip body out (too large to keep)
        // would otherwise answer "Content-Encoding: gzip" with zero bytes, which a browser fails to decode.
        if (body.Length == 0)
            headers.RemoveAll(h => string.Equals(h.Key, "Content-Encoding", StringComparison.OrdinalIgnoreCase));

        string? contentType = !string.IsNullOrEmpty(response.Content.MimeType)
            ? response.Content.MimeType
            : headers.FirstOrDefault(h => string.Equals(h.Key, "Content-Type", StringComparison.OrdinalIgnoreCase)).Value;

        return new RecordedResponse(
            response.Status,
            string.IsNullOrEmpty(response.StatusText) ? null : response.StatusText,
            headers,
            body,
            string.IsNullOrEmpty(contentType) ? null : contentType);
    }
}
