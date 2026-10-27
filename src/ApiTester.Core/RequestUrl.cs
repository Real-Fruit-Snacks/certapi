namespace ApiTester.Core;

/// <summary>URL composition shared by the request editor: base URL + path + query parameters.</summary>
public static class RequestUrl
{
    /// <summary>Combine a base URL, a path, and the enabled query parameters into a full URL.</summary>
    public static string Effective(string? baseUrl, string path,
        IEnumerable<KeyValuePair<string, string>> enabledParams)
    {
        var combined = UrlCombine(baseUrl, path);
        var (bare, _) = QueryString.Split(combined);
        var q = QueryString.Build(enabledParams);
        return q.Length == 0 ? bare : bare + "?" + q;
    }

    /// <summary>Like <see cref="Effective"/>, but for an export: <c>{{variable}}</c> tokens in the
    /// parameters stay literal instead of being percent-escaped.</summary>
    public static string EffectiveTemplate(string? baseUrl, string path,
        IEnumerable<KeyValuePair<string, string>> enabledParams)
    {
        var combined = UrlCombine(baseUrl, path);
        var (bare, _) = QueryString.Split(combined);
        var q = QueryString.BuildTemplate(enabledParams);
        return q.Length == 0 ? bare : bare + "?" + q;
    }

    /// <summary>Split a path or full URL into its path and the parsed query parameters.</summary>
    public static (string PathNoQuery, List<KeyValuePair<string, string>> Params) SplitForEditing(string pathOrUrl)
    {
        var (path, query) = QueryString.Split(pathOrUrl ?? "");
        return (path, QueryString.Parse(query));
    }

    /// <summary>Append query parameters to a URL, percent-encoded, after any query it already has
    /// and before any fragment. The existing query is left byte-for-byte as it was.</summary>
    public static string AppendQuery(string url, IEnumerable<KeyValuePair<string, string>> pairs)
    {
        var q = QueryString.Build(pairs);
        if (q.Length == 0) return url;
        int hash = url.IndexOf('#');
        string fragment = hash >= 0 ? url[hash..] : "";
        string head = hash >= 0 ? url[..hash] : url;
        string sep = !head.Contains('?') ? "?" : head.EndsWith('?') || head.EndsWith('&') ? "" : "&";
        return head + sep + q + fragment;
    }

    // Mirror of the App's UrlHelper.Combine, kept here so Core stays self-contained and testable.
    private static string UrlCombine(string? baseUrl, string? path)
    {
        path = (path ?? "").Trim();
        baseUrl = (baseUrl ?? "").Trim();
        if (path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return path;
        if (string.IsNullOrEmpty(baseUrl)) return path;
        if (string.IsNullOrEmpty(path)) return baseUrl;
        return baseUrl.TrimEnd('/') + "/" + path.TrimStart('/');
    }
}
