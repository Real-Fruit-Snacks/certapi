namespace ApiTester.Core;

/// <summary>One request/response seen during a capture session, for the "save as requests" step.</summary>
public sealed record ObservedCall(string Method, string Url, int StatusCode, string? ContentType)
{
    /// <summary>Deduplication key: upper-case method plus URL path without query string.</summary>
    public string Key =>
        Method.ToUpperInvariant() + " " +
        (Uri.TryCreate(Url, UriKind.Absolute, out var u) ? u.GetLeftPart(UriPartial.Path) : Url);

    /// <summary>One entry per method+path (query ignored); the last occurrence wins, first-seen order kept.</summary>
    public static IReadOnlyList<ObservedCall> Dedup(IEnumerable<ObservedCall> calls)
    {
        var order = new List<string>();
        var map = new Dictionary<string, ObservedCall>(StringComparer.Ordinal);
        foreach (var c in calls)
        {
            string key = c.Key;
            if (!map.ContainsKey(key)) order.Add(key);
            map[key] = c;
        }
        var result = new List<ObservedCall>(order.Count);
        foreach (var k in order) result.Add(map[k]);
        return result;
    }
}
