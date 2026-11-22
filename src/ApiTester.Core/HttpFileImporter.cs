using System.Text;

namespace ApiTester.Core;

/// <summary>Imports VS Code REST Client / JetBrains HTTP Client (<c>.http</c> / <c>.rest</c>) files
/// into a <see cref="ParsedCollection"/>. Supports <c>### [name]</c> request separators, file-scoped
/// <c>@var = value</c> definitions, multi-line query continuation lines, headers, and bodies.</summary>
public static class HttpFileImporter
{
    private static readonly HashSet<string> HttpMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS", "TRACE"
    };

    public static bool LooksLikeHttpFile(string? path, string content)
    {
        if (path is not null &&
            (path.EndsWith(".http", StringComparison.OrdinalIgnoreCase) ||
             path.EndsWith(".rest", StringComparison.OrdinalIgnoreCase)))
            return true;
        if (string.IsNullOrWhiteSpace(content)) return false;
        foreach (var rawLine in content.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith("//") || line.StartsWith('@')) continue;
            return TryParseRequestLine(line, out _, out _);
        }
        return false;
    }

    public static ParsedCollection Parse(string content, string? collectionName = null)
    {
        var collection = new ParsedCollection { Name = string.IsNullOrWhiteSpace(collectionName) ? "HTTP Requests" : collectionName };
        var fileVars = new Dictionary<string, string>(StringComparer.Ordinal);

        // First pass: collect file-level @var = value definitions (kept as literal substitution
        // when referenced in the same file, leaving unknown {{vars}} intact for environments).
        var lines = content.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
        var blocks = new List<(string? Name, List<string> Lines)>();
        string? currentName = null;
        var currentLines = new List<string>();

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("###", StringComparison.Ordinal))
            {
                if (currentLines.Count > 0)
                    blocks.Add((currentName, currentLines));
                currentName = trimmed[3..].Trim();
                if (currentName.Length == 0) currentName = null;
                currentLines = new List<string>();
                continue;
            }
            if (currentLines.Count == 0 && trimmed.StartsWith('@'))
            {
                int eq = trimmed.IndexOf('=');
                if (eq > 1)
                {
                    string key = trimmed[1..eq].Trim();
                    string val = trimmed[(eq + 1)..].Trim();
                    if (key.Length > 0) fileVars[key] = val;
                    continue;
                }
            }
            currentLines.Add(line);
        }
        if (currentLines.Count > 0)
            blocks.Add((currentName, currentLines));

        foreach (var (blockName, blockLines) in blocks)
        {
            if (ParseBlock(blockName, blockLines, fileVars) is { } req)
                collection.Requests.Add(req);
        }

        if (collection.Requests.Count == 0)
            throw new FormatException("No HTTP requests found in the .http file.");

        return collection;
    }

    private static ParsedRequest? ParseBlock(string? blockName, List<string> lines, Dictionary<string, string> fileVars)
    {
        int i = 0;
        string? name = blockName;
        while (i < lines.Count)
        {
            var t = lines[i].Trim();
            if (t.Length == 0) { i++; continue; }
            if (t.StartsWith("# @name ", StringComparison.OrdinalIgnoreCase))
            {
                name = t["# @name ".Length..].Trim();
                i++;
                continue;
            }
            if (t.StartsWith('#') || t.StartsWith("//")) { i++; continue; }
            if (t.StartsWith('@') && t.IndexOf('=') is int eq && eq > 1)
            {
                string key = t[1..eq].Trim();
                string val = t[(eq + 1)..].Trim();
                if (key.Length > 0) fileVars[key] = val;
                i++;
                continue;
            }
            break;
        }
        if (i >= lines.Count) return null;

        if (!TryParseRequestLine(lines[i].Trim(), out string method, out string url))
            return null;
        i++;

        // Multi-line query continuations (lines starting with ? or & right after the request line).
        var urlSb = new StringBuilder(url);
        while (i < lines.Count)
        {
            var t = lines[i].Trim();
            if (t.StartsWith('?') || t.StartsWith('&'))
            {
                urlSb.Append(t);
                i++;
                continue;
            }
            break;
        }
        url = SubstituteFileVars(urlSb.ToString(), fileVars);

        var (baseUrl, path) = SplitBaseAndPath(url);
        var req = new ParsedRequest
        {
            Method = method,
            BaseUrl = baseUrl,
            Url = path,
            Name = string.IsNullOrWhiteSpace(name) ? $"{method} {path}" : name
        };

        // Headers until the first blank line.
        while (i < lines.Count)
        {
            var raw = lines[i];
            if (string.IsNullOrWhiteSpace(raw)) { i++; break; }
            var t = raw.Trim();
            if (t.StartsWith('#') || t.StartsWith("//")) { i++; continue; }
            int colon = raw.IndexOf(':');
            if (colon <= 0) break;
            string hName = raw[..colon].Trim();
            string hVal = SubstituteFileVars(raw[(colon + 1)..].Trim(), fileVars);
            if (hName.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
                req.ContentType = hVal;
            else if (hName.Equals("Authorization", StringComparison.OrdinalIgnoreCase) &&
                     hVal.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                req.BearerToken = hVal["Bearer ".Length..].Trim();
            else
                req.Headers.Add(new(hName, hVal));
            i++;
        }

        // Remaining non-empty lines are the body.
        if (i < lines.Count)
        {
            // Trim trailing blank lines only.
            int end = lines.Count - 1;
            while (end >= i && string.IsNullOrWhiteSpace(lines[end])) end--;
            if (end >= i)
            {
                var body = string.Join("\n", lines.GetRange(i, end - i + 1));
                req.Body = SubstituteFileVars(body, fileVars);
            }
        }

        return req;
    }

    private static bool TryParseRequestLine(string line, out string method, out string url)
    {
        method = "GET";
        url = "";
        int sp = line.IndexOf(' ');
        if (sp <= 0)
        {
            // Bare URL line (shorthand GET in VS Code REST Client).
            if (line.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                url = line;
                return true;
            }
            return false;
        }

        string first = line[..sp];
        if (!HttpMethods.Contains(first)) return false;
        method = first.ToUpperInvariant();
        string rest = line[(sp + 1)..].Trim();
        // Strip optional trailing "HTTP/1.1" / "HTTP/2" version token.
        int lastSp = rest.LastIndexOf(' ');
        if (lastSp > 0 && rest[(lastSp + 1)..].StartsWith("HTTP/", StringComparison.OrdinalIgnoreCase))
            rest = rest[..lastSp].TrimEnd();
        if (rest.Length == 0) return false;
        url = rest;
        return true;
    }

    private static string SubstituteFileVars(string input, Dictionary<string, string> fileVars)
    {
        if (fileVars.Count == 0 || !input.Contains("{{", StringComparison.Ordinal)) return input;
        var (resolved, _) = VariableResolver.Resolve(input, fileVars);
        return resolved;
    }

    private static (string? BaseUrl, string Path) SplitBaseAndPath(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme is "http" or "https"))
        {
            string baseUrl = uri.GetLeftPart(UriPartial.Authority);
            string path = url[baseUrl.Length..];
            return (baseUrl, string.IsNullOrEmpty(path) ? "/" : path);
        }
        return (null, url);
    }
}
