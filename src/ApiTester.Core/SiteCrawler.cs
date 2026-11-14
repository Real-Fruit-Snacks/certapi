using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace ApiTester.Core;

/// <summary>Links on a page: the href/src/action/srcset values of HTML, and url(…) in CSS,
/// resolved against the page (and its &lt;base href&gt;).</summary>
public static class LinkExtractor
{
    private static readonly Regex Attribute = new(
        @"\b(?:href|src|action|data-src|poster)\s*=\s*(?:""([^""]*)""|'([^']*)'|([^\s>""']+))",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
    private static readonly Regex SrcSet = new(@"\bsrcset\s*=\s*(?:""([^""]*)""|'([^']*)')",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
    private static readonly Regex CssUrl = new(@"url\(\s*(?:""([^""]*)""|'([^']*)'|([^)\s]+))\s*\)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
    private static readonly Regex Base = new(@"<base\b[^>]*\bhref\s*=\s*(?:""([^""]*)""|'([^']*)')",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
    private static readonly Regex Comments = new(@"<!--.*?-->", RegexOptions.Singleline, TimeSpan.FromSeconds(2));

    public static IReadOnlyList<Uri> Extract(string text, Uri page, bool css)
    {
        var found = new List<Uri>();
        var baseUri = page;
        try
        {
            if (!css)
            {
                text = Comments.Replace(text, "");
                if (Base.Match(text) is { Success: true } b && Uri.TryCreate(page, WebUtility.HtmlDecode(Value(b)), out var declared)) baseUri = declared;
                foreach (Match m in Attribute.Matches(text)) Add(Value(m));
                foreach (Match m in SrcSet.Matches(text))
                    foreach (var candidate in Value(m).Split(','))
                        Add(candidate.Trim().Split(' ')[0]);
            }
            foreach (Match m in CssUrl.Matches(text)) Add(Value(m));
        }
        catch (RegexMatchTimeoutException) { /* a pathological page yields what was found so far */ }
        return found.Distinct().ToList();

        void Add(string raw)
        {
            string v = WebUtility.HtmlDecode(raw.Trim());
            if (v.Length == 0 || v.StartsWith('#')) return;
            if (v.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase) || v.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ||
                v.StartsWith("data:", StringComparison.OrdinalIgnoreCase) || v.StartsWith("tel:", StringComparison.OrdinalIgnoreCase) ||
                v.StartsWith("blob:", StringComparison.OrdinalIgnoreCase)) return;
            if (!Uri.TryCreate(baseUri, v, out var abs) || abs.Scheme is not ("http" or "https")) return;
            found.Add(new UriBuilder(abs) { Fragment = "" }.Uri);
        }
    }

    private static string Value(Match m) =>
        m.Groups.Cast<Group>().Skip(1).FirstOrDefault(g => g.Success)?.Value ?? "";
}

/// <summary>robots.txt's rules for every robot (<c>User-agent: *</c>): Allow and Disallow by
/// longest matching prefix, with <c>*</c> and <c>$</c>.</summary>
public sealed class RobotsRules
{
    private readonly List<(string Pattern, bool Allow)> _rules = new();

    public static RobotsRules Parse(string text)
    {
        var rules = new RobotsRules();
        bool applies = false, sawRule = false;
        foreach (var raw in text.Split('\n'))
        {
            string line = raw.Split('#')[0].Trim();
            int colon = line.IndexOf(':');
            if (colon <= 0) continue;
            string field = line[..colon].Trim().ToLowerInvariant(), value = line[(colon + 1)..].Trim();
            if (field == "user-agent")
            {
                if (sawRule) { applies = false; sawRule = false; }
                if (value == "*" || value.Contains("certapi", StringComparison.OrdinalIgnoreCase)) applies = true;
            }
            else if (field is "allow" or "disallow")
            {
                sawRule = true;
                if (applies && value.Length > 0) rules._rules.Add((value, field == "allow"));
            }
        }
        return rules;
    }

    public bool Allows(Uri uri)
    {
        string path = uri.PathAndQuery;
        (string Pattern, bool Allow)? best = null;
        foreach (var rule in _rules)
            if (Matches(rule.Pattern, path) && (best is null || rule.Pattern.Length > best.Value.Pattern.Length ||
                                                (rule.Pattern.Length == best.Value.Pattern.Length && rule.Allow)))
                best = rule;
        return best?.Allow ?? true;
    }

    private static bool Matches(string pattern, string path)
    {
        bool anchored = pattern.EndsWith('$');
        string body = anchored ? pattern[..^1] : pattern;
        string regex = "^" + string.Join(".*", body.Split('*').Select(Regex.Escape)) + (anchored ? "$" : "");
        return Regex.IsMatch(path, regex, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    }
}

/// <summary>Where a crawled URL's body is saved under a mirror folder: host/path, with a folder's
/// own page as index.html and a query folded into the file name.</summary>
public static class MirrorPath
{
    public static string For(string root, Uri uri, string? contentType)
    {
        var parts = new List<string> { Safe(uri.Port == (uri.Scheme == "https" ? 443 : 80) ? uri.Host : $"{uri.Host}_{uri.Port}") };
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(s => Safe(Uri.UnescapeDataString(s))).ToList();
        bool folder = uri.AbsolutePath.EndsWith('/') || segments.Count == 0;
        if (folder) segments.Add("index.html");
        else if (!Path.HasExtension(segments[^1]) && contentType?.Contains("html", StringComparison.OrdinalIgnoreCase) == true)
            segments[^1] += ".html";
        if (uri.Query.Length > 1)
        {
            string name = segments[^1];
            string ext = Path.GetExtension(name);
            segments[^1] = Path.GetFileNameWithoutExtension(name) + "_" + Safe(uri.Query[1..]) + ext;
        }
        parts.AddRange(segments);
        string full = Path.GetFullPath(Path.Combine(new[] { root }.Concat(parts).ToArray()));
        // A path that climbs out of the mirror is never written.
        if (!full.StartsWith(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("unsafe path");
        return full;
    }

    private static string Safe(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s) sb.Append(Path.GetInvalidFileNameChars().Contains(c) || c is ':' or '?' or '*' ? '_' : c);
        string r = sb.ToString().Trim('.', ' ');
        if (r.Length == 0 || r is "." or "..") r = "_";
        return r.Length > 120 ? r[..120] : r;
    }
}
