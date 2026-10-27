using System.Text;
using System.Text.Json;

namespace ApiTester.Core;

/// <summary>How a single value differs between two responses.</summary>
public enum DiffKind { Added, Removed, Changed, TypeChanged }

/// <summary>One header that differs. A null side means the header was absent there, which is why
/// there is no kind: the nulls already say added, removed, or changed.</summary>
public sealed record HeaderDiff(string Name, string? Before, string? After);

/// <summary>One difference in the body, located by a <see cref="JsonPath"/> path (empty for a
/// whole-body summary when the body is not JSON).</summary>
public sealed record BodyDiff(string Path, DiffKind Kind, string? Before, string? After);

/// <summary>A response reduced to what can be compared — built from an <see cref="ApiResponse"/>,
/// a <see cref="HarEntry"/>, or a stored known-good snapshot, so all three baselines flow through
/// one comparison. Deliberately a plain record with no transport detail: it is persisted into
/// state.json, and only the comparable facts should survive a reload.</summary>
public sealed record ResponseSnapshot(
    int StatusCode,
    IReadOnlyList<KeyValuePair<string, string>> Headers,
    byte[] Body,
    string? ContentType)
{
    /// <summary>Snapshot a live response. A transport failure has no status, so it records 0 —
    /// the honest "none", and never equal to a real code.</summary>
    public static ResponseSnapshot From(ApiResponse response) =>
        new(response.StatusCode ?? 0,
            response.Headers ?? Array.Empty<KeyValuePair<string, string>>(),
            response.Body ?? Array.Empty<byte>(),
            response.ContentType);

    /// <summary>Snapshot a recorded exchange, so a HAR captured yesterday is a baseline for a send
    /// today. Never throws: a snapshot that crashed on a damaged archive would take the diff with
    /// it, and a diff is exactly what someone reaches for when things are already wrong.</summary>
    public static ResponseSnapshot From(HarEntry entry)
    {
        var response = entry?.Response ?? new HarResponse();
        var content = response.Content ?? new HarContent();

        var headers = new List<KeyValuePair<string, string>>();
        foreach (var header in response.Headers ?? new List<HarNameValue>())
            headers.Add(new KeyValuePair<string, string>(header.Name, header.Value));

        string text = content.Text ?? "";
        byte[] body;
        if (text.Length == 0)
        {
            body = Array.Empty<byte>();
        }
        else if (string.Equals(content.Encoding, "base64", StringComparison.OrdinalIgnoreCase))
        {
            try { body = Convert.FromBase64String(text); }
            catch (FormatException) { body = Encoding.UTF8.GetBytes(text); }  // mislabelled, not fatal
        }
        else
        {
            body = Encoding.UTF8.GetBytes(text);
        }

        return new ResponseSnapshot(
            response.Status,
            headers,
            body,
            string.IsNullOrEmpty(content.MimeType) ? null : content.MimeType);
    }
}

/// <summary>What to overlook when comparing. Every default exists because a response that is
/// "the same" in the way a user means still differs byte for byte on each send.</summary>
public sealed record DiffOptions
{
    /// <summary>Headers that change on every response and would drown a real difference.</summary>
    public static readonly IReadOnlyList<string> DefaultIgnoredHeaders = new[]
        { "Date", "Set-Cookie", "ETag", "Age", "X-Request-Id", "X-Correlation-Id", "Server-Timing" };

    /// <summary>JSON paths to ignore (dates, request ids, generated tokens). Default: empty.</summary>
    public IReadOnlyList<string> IgnorePaths { get; init; } = Array.Empty<string>();

    /// <summary>Header names to ignore, matched case-insensitively on the whole name.</summary>
    public IReadOnlyList<string> IgnoreHeaders { get; init; } = DefaultIgnoredHeaders;

    /// <summary>Compare header values, not just presence. Turn it off when a gateway rewrites
    /// values you don't control but their absence would still be a regression.</summary>
    public bool CompareHeaderValues { get; init; } = true;
}

/// <summary>The result of comparing two responses.</summary>
/// <param name="StatusBefore">The baseline's status — always populated, even when the statuses
/// match. These two fields are the two values, not "the difference"; they are nullable only so a
/// caller can build a partial result.</param>
/// <param name="StatusAfter">The actual response's status — always populated. See
/// <paramref name="StatusBefore"/>.</param>
public sealed record DiffResult(
    bool Identical,
    int? StatusBefore, int? StatusAfter,
    IReadOnlyList<HeaderDiff> Headers,
    IReadOnlyList<BodyDiff> Body);

/// <summary>Compares a response against a baseline: what changed, not merely whether it responded.
/// Pure — no I/O, no clock, no randomness — so the same two snapshots always diff the same way.</summary>
public static class ResponseDiff
{
    public static DiffResult Compare(ResponseSnapshot baseline, ResponseSnapshot actual, DiffOptions? options = null)
    {
        options ??= new DiffOptions();

        var headers = CompareHeaders(baseline, actual, options);
        var body = CompareBodies(baseline, actual, options);

        bool identical = baseline.StatusCode == actual.StatusCode && headers.Count == 0 && body.Count == 0;
        return new DiffResult(identical, baseline.StatusCode, actual.StatusCode, headers, body);
    }

    private static IReadOnlyList<HeaderDiff> CompareHeaders(
        ResponseSnapshot baseline, ResponseSnapshot actual, DiffOptions options)
    {
        var ignore = new HashSet<string>(
            options.IgnoreHeaders ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var before = Collapse(baseline.Headers, ignore);
        var after = Collapse(actual.Headers, ignore);

        // Union of the two name sets, then sorted, so the same pair of responses always prints the
        // same diff in the same order — a CI log people can diff against yesterday's.
        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in before.Keys) if (seen.Add(name)) names.Add(name);
        foreach (var name in after.Keys) if (seen.Add(name)) names.Add(name);
        names.Sort(StringComparer.Ordinal);

        var diffs = new List<HeaderDiff>();
        foreach (var name in names)
        {
            bool inBefore = before.TryGetValue(name, out var b);
            bool inAfter = after.TryGetValue(name, out var a);
            if (inBefore && !inAfter) diffs.Add(new HeaderDiff(name, b, null));
            else if (!inBefore && inAfter) diffs.Add(new HeaderDiff(name, null, a));
            else if (options.CompareHeaderValues && !string.Equals(b, a, StringComparison.Ordinal))
                diffs.Add(new HeaderDiff(name, b, a));
        }
        return diffs;
    }

    /// <summary>Pick the comparison the two bodies deserve: structural when both are JSON, bytes
    /// when either is binary, text otherwise. No pretending to diff a PDF.</summary>
    private static IReadOnlyList<BodyDiff> CompareBodies(
        ResponseSnapshot baseline, ResponseSnapshot actual, DiffOptions options)
    {
        byte[] before = baseline.Body ?? Array.Empty<byte>();
        byte[] after = actual.Body ?? Array.Empty<byte>();

        JsonDocument? beforeJson = TryParseJson(before);
        JsonDocument? afterJson = TryParseJson(after);
        try
        {
            if (beforeJson is not null && afterJson is not null)
            {
                var diffs = new List<BodyDiff>();
                DiffJson("", beforeJson.RootElement, afterJson.RootElement, options, diffs);
                return diffs;
            }
        }
        finally
        {
            beforeJson?.Dispose();
            afterJson?.Dispose();
        }

        return IsBinary(before) || IsBinary(after)
            ? CompareBytes(before, after)
            : CompareText(before, after);
    }

    /// <summary>Bytes we cannot honestly show as text: the same rule the CLI's JSON envelope uses,
    /// so a body called binary in one place is binary in the other.</summary>
    private static bool IsBinary(byte[] body)
    {
        foreach (byte b in body) if (b == 0) return true;
        // UTF8.GetString never throws — invalid bytes decode to U+FFFD, which marks the body binary.
        return Encoding.UTF8.GetString(body).Contains('�');
    }

    /// <summary>Size and equality only. A caller who needs to know *where* two PDFs differ needs a
    /// different tool, and inventing a line diff for them would only mislead.</summary>
    private static IReadOnlyList<BodyDiff> CompareBytes(byte[] before, byte[] after)
    {
        if (before.AsSpan().SequenceEqual(after)) return Array.Empty<BodyDiff>();
        return new[]
        {
            before.Length == after.Length
                ? new BodyDiff("", DiffKind.Changed, $"{before.Length} bytes", $"{after.Length} bytes, content differs")
                : new BodyDiff("", DiffKind.Changed, $"{before.Length} bytes", $"{after.Length} bytes")
        };
    }

    /// <summary>A one-line summary of two texts that differ. Deliberately not a line-by-line diff:
    /// the value here is "it changed, and by roughly this much", which fits one line of CI log.</summary>
    private static IReadOnlyList<BodyDiff> CompareText(byte[] before, byte[] after)
    {
        string beforeText = Encoding.UTF8.GetString(before);
        string afterText = Encoding.UTF8.GetString(after);
        if (string.Equals(beforeText, afterText, StringComparison.Ordinal)) return Array.Empty<BodyDiff>();

        string beforeSummary = $"{LineCount(beforeText)} lines, {before.Length} bytes";
        string afterSummary = $"{LineCount(afterText)} lines, {after.Length} bytes";

        // A change that alters neither the line count nor the byte count — `<status>OK</status>`
        // becoming `<status>NO</status>` — would otherwise print the identical summary on both
        // sides: a diff line that appears to show no difference while claiming one. The byte
        // comparison already says "content differs" for exactly this case; text can do better,
        // because it can say where.
        if (string.Equals(beforeSummary, afterSummary, StringComparison.Ordinal))
            afterSummary += $", content differs from line {FirstDifferingLine(beforeText, afterText)}";

        return new[] { new BodyDiff("", DiffKind.Changed, beforeSummary, afterSummary) };
    }

    /// <summary>The 1-based line on which two differing texts first diverge. When one is a prefix of
    /// the other they never diverge character by character, so the answer is the line where the
    /// shorter one runs out — which is where a reader should start looking.</summary>
    private static int FirstDifferingLine(string before, string after)
    {
        int limit = Math.Min(before.Length, after.Length);
        int line = 1;
        for (int i = 0; i < limit; i++)
        {
            if (before[i] != after[i]) return line;
            if (before[i] == '\n') line++;
        }
        return line;
    }

    /// <summary>Split on '\n' only, so CRLF and LF bodies count the same; an empty body is 0 lines
    /// rather than the 1 that a naive split would claim.</summary>
    private static int LineCount(string text) => text.Length == 0 ? 0 : text.Split('\n').Length;

    /// <summary>Parse a body as JSON, or null when it is empty or isn't JSON. An empty body is not
    /// JSON — comparing "nothing" structurally would invent a difference that isn't there.</summary>
    private static JsonDocument? TryParseJson(byte[] body)
    {
        if (body.Length == 0) return null;
        try { return JsonDocument.Parse(Utf8Json.Body(body)); }
        catch (JsonException) { return null; }
    }

    /// <summary>Walk both trees together, emitting a diff at each leaf path that differs.</summary>
    private static void DiffJson(string path, JsonElement before, JsonElement after,
        DiffOptions options, List<BodyDiff> into)
    {
        if (IsIgnored(path, options.IgnorePaths)) return;

        var beforeKind = ComparableKind(before);
        var afterKind = ComparableKind(after);
        if (beforeKind != afterKind)
        {
            // One entry for the whole subtree: an object that became an array has not changed in
            // fifty places, it has changed in one.
            into.Add(new BodyDiff(path, DiffKind.TypeChanged, Render(before), Render(after)));
            return;
        }

        switch (beforeKind)
        {
            case JsonValueKind.Object:
                DiffObjects(path, before, after, options, into);
                break;
            case JsonValueKind.Array:
                DiffArrays(path, before, after, options, into);
                break;
            default:
                // Raw text, so 1 and 1.0 count as changed: honest about what the server sent.
                if (!string.Equals(before.GetRawText(), after.GetRawText(), StringComparison.Ordinal))
                    into.Add(new BodyDiff(path, DiffKind.Changed, Render(before), Render(after)));
                break;
        }
    }

    private static void DiffObjects(string path, JsonElement before, JsonElement after,
        DiffOptions options, List<BodyDiff> into)
    {
        var beforeProps = Properties(before);
        var afterProps = Properties(after);

        // Baseline order first, then anything new from the actual response: property order in JSON
        // carries no meaning, but a stable diff order does.
        var names = new List<string>(beforeProps.Keys);
        foreach (var name in afterProps.Keys) if (!beforeProps.ContainsKey(name)) names.Add(name);

        foreach (var name in names)
        {
            string child = path.Length == 0 ? name : $"{path}.{name}";
            bool inBefore = beforeProps.TryGetValue(name, out var b);
            bool inAfter = afterProps.TryGetValue(name, out var a);
            if (inBefore && inAfter) DiffJson(child, b, a, options, into);
            else if (inBefore) Add(into, child, DiffKind.Removed, Render(b), null, options);
            else Add(into, child, DiffKind.Added, null, Render(a), options);
        }
    }

    private static void DiffArrays(string path, JsonElement before, JsonElement after,
        DiffOptions options, List<BodyDiff> into)
    {
        var beforeItems = before.EnumerateArray().ToList();
        var afterItems = after.EnumerateArray().ToList();
        int shared = Math.Min(beforeItems.Count, afterItems.Count);

        for (int i = 0; i < shared; i++)
            DiffJson($"{path}[{i}]", beforeItems[i], afterItems[i], options, into);
        for (int i = shared; i < afterItems.Count; i++)
            Add(into, $"{path}[{i}]", DiffKind.Added, null, Render(afterItems[i]), options);
        for (int i = shared; i < beforeItems.Count; i++)
            Add(into, $"{path}[{i}]", DiffKind.Removed, Render(beforeItems[i]), null, options);
    }

    private static void Add(List<BodyDiff> into, string path, DiffKind kind,
        string? before, string? after, DiffOptions options)
    {
        if (IsIgnored(path, options.IgnorePaths)) return;
        into.Add(new BodyDiff(path, kind, before, after));
    }

    /// <summary>Property values by name, first occurrence winning — a duplicate key is a malformed
    /// document, not a reason to throw mid-diff.</summary>
    private static Dictionary<string, JsonElement> Properties(JsonElement element)
    {
        var map = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
            if (!map.ContainsKey(property.Name)) map[property.Name] = property.Value;
        return map;
    }

    /// <summary>True and False are one kind (boolean), so <c>true</c> → <c>false</c> reads as a
    /// changed value rather than a changed type.</summary>
    private static JsonValueKind ComparableKind(JsonElement element) =>
        element.ValueKind == JsonValueKind.False ? JsonValueKind.True : element.ValueKind;

    /// <summary>A value as it appeared, capped so an added 4 MB object doesn't become a 4 MB
    /// diff line.</summary>
    private static string Render(JsonElement element)
    {
        string raw = element.GetRawText();
        return raw.Length <= MaxRenderedValue ? raw : raw[..MaxRenderedValue] + "…";
    }

    private const int MaxRenderedValue = 200;

    /// <summary>Whether a path is suppressed by <see cref="DiffOptions.IgnorePaths"/>. A pattern
    /// matches a path with at least as many segments where every pattern segment matches exactly,
    /// or is a prefix written with a trailing <c>*</c>. So <c>data</c> suppresses everything under
    /// data, <c>data.token*</c> suppresses <c>data.tokenId</c> but not <c>data.other</c>, and
    /// <c>items[0].id</c> suppresses exactly that one path.</summary>
    private static bool IsIgnored(string path, IReadOnlyList<string>? patterns)
    {
        if (patterns is null || patterns.Count == 0 || path.Length == 0) return false;
        var candidate = Segments(path);

        foreach (var pattern in patterns)
        {
            if (string.IsNullOrWhiteSpace(pattern)) continue;
            var wanted = Segments(pattern.Trim());
            if (wanted.Length == 0 || wanted.Length > candidate.Length) continue;

            bool matched = true;
            for (int i = 0; i < wanted.Length; i++)
            {
                string segment = wanted[i];
                bool ok = segment.EndsWith('*')
                    ? candidate[i].StartsWith(segment[..^1], StringComparison.Ordinal)
                    : string.Equals(candidate[i], segment, StringComparison.Ordinal);
                if (!ok) { matched = false; break; }
            }
            if (matched) return true;
        }
        return false;
    }

    private static string[] Segments(string path) => path.Split('.', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>One value per header name: a header sent twice is one header with two values as far
    /// as HTTP is concerned, so join them the way a client would read them.</summary>
    private static Dictionary<string, string> Collapse(
        IReadOnlyList<KeyValuePair<string, string>>? headers, HashSet<string> ignore)
    {
        var grouped = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in headers ?? Array.Empty<KeyValuePair<string, string>>())
        {
            if (header.Key is null || ignore.Contains(header.Key)) continue;
            if (!grouped.TryGetValue(header.Key, out var values)) grouped[header.Key] = values = new List<string>();
            values.Add(header.Value ?? "");
        }
        var joined = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in grouped) joined[pair.Key] = string.Join(", ", pair.Value);
        return joined;
    }
}
