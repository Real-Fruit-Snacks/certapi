namespace ApiTester.Core;

/// <summary>Checks that keep a caller who controls part of a request — an agent driving the MCP
/// server — from moving it somewhere the operator did not choose. Every message names hosts only:
/// a resolved URL can carry a secret variable in its path or query.</summary>
public static class RequestGuards
{
    /// <summary>The host of <paramref name="url"/> for a message, never the rest of it.</summary>
    public static string DescribeHost(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Host.Length > 0 ? u.Host : "(not an absolute URL)";

    /// <summary>scheme://host:port, lowercase, or null when <paramref name="url"/> is not absolute.</summary>
    public static string? OriginOf(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Host.Length > 0
            ? $"{u.Scheme.ToLowerInvariant()}://{u.Host.ToLowerInvariant()}:{u.Port}"
            : null;

    /// <summary>The problem when <paramref name="actualUrl"/> goes to a different scheme, host or
    /// port than <paramref name="savedUrl"/> — the same saved request resolved without the
    /// caller's variables — or null when both go to the same place.</summary>
    public static string? OriginChanged(string savedUrl, string actualUrl)
    {
        var saved = OriginOf(savedUrl);
        var actual = OriginOf(actualUrl);
        if (saved is null)
            return "the saved request's address only becomes a URL with the vars given; vars may fill in values " +
                   "but may not choose where a saved request is sent — put the host in an environment instead";
        if (actual is null || !string.Equals(saved, actual, StringComparison.Ordinal))
            return $"vars may not change where a saved request is sent: it goes to {saved}, " +
                   $"and the vars given would send it to {actual ?? "(not an absolute URL)"}";
        return null;
    }

    /// <summary>The problem when a <c>Host</c> header names a different authority than the URL —
    /// which would let a request through an allow-list that checks the URL reach another name on a
    /// shared front end — or null.</summary>
    public static string? HostHeaderMismatch(string url, IEnumerable<KeyValuePair<string, string>> headers)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return null;
        foreach (var h in headers)
        {
            if (!h.Key.Trim().Equals("Host", StringComparison.OrdinalIgnoreCase)) continue;
            string value = (h.Value ?? "").Trim();
            bool same = value.Equals(u.Authority, StringComparison.OrdinalIgnoreCase) ||
                        value.Equals(u.Host, StringComparison.OrdinalIgnoreCase) && u.IsDefaultPort ||
                        value.Equals($"{u.Host}:{u.Port}", StringComparison.OrdinalIgnoreCase);
            if (!same)
                return $"a Host header must name the URL's own host ({u.Authority}); the allow-list judges the URL";
        }
        return null;
    }
}
