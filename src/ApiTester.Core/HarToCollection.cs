using System.Text.RegularExpressions;

namespace ApiTester.Core;

/// <summary>Options controlling how <see cref="HarToCollection.Group"/> turns HAR entries into a
/// <see cref="ParsedCollection"/>.</summary>
public sealed record HarGroupOptions
{
    /// <summary>Collapse path segments that look like identifiers into {param}. On by default.</summary>
    public bool TemplateIds { get; init; } = true;
    /// <summary>Keep only entries whose host matches (case-insensitive). Null = every host.</summary>
    public string? Host { get; init; }
    /// <summary>Skip entries whose response status is >= this. Default 400 — a capture full of 404s
    /// from a fuzz run should not become documented operations.</summary>
    public int MaxStatus { get; init; } = 400;
}

/// <summary>Group HAR entries into a collection suitable for OpenAPI export: one request per
/// distinct method+templated path, hosts split into folders when the archive spans several.
/// Pure — no I/O, no sockets.</summary>
public static class HarToCollection
{
    private static readonly Regex UuidPattern = new(
        "^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$",
        RegexOptions.Compiled);

    private const string RedactedMarker = "[redacted]";

    private sealed record Member(HarEntry Entry, Uri Uri, string[] Segments);

    public static ParsedCollection Group(Har har, HarGroupOptions? options = null)
    {
        options ??= new HarGroupOptions();

        // Step 1 — select entries, keeping order.
        var selected = new List<(HarEntry Entry, Uri Uri, string Method)>();
        foreach (var entry in har.Log.Entries)
        {
            if (!Uri.TryCreate(entry.Request.Url, UriKind.Absolute, out var uri)) continue;
            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) continue;
            if (!string.IsNullOrWhiteSpace(options.Host) &&
                !uri.Host.Equals(options.Host, StringComparison.OrdinalIgnoreCase)) continue;
            if (entry.Response.Status < 100 || entry.Response.Status >= options.MaxStatus) continue;

            string method = string.IsNullOrWhiteSpace(entry.Request.Method)
                ? "GET"
                : entry.Request.Method.ToUpperInvariant();

            selected.Add((entry, uri, method));
        }

        if (selected.Count == 0) return new ParsedCollection { Name = "HAR capture" };

        // Step 2/3 — shape key per entry, preserving first-seen order of groups and members.
        var shapeOrder = new List<string>();
        var shapeGroups = new Dictionary<string, (string Method, List<Member> Members)>();

        foreach (var (entry, uri, method) in selected)
        {
            string[] segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            bool[] isCandidate = segments.Select(s => options.TemplateIds && IsIdentifierCandidate(s)).ToArray();

            string shapeKey = method + " /" + string.Join("/",
                segments.Select((s, i) => isCandidate[i] ? "" : s));

            if (!shapeGroups.TryGetValue(shapeKey, out var group))
            {
                group = (method, new List<Member>());
                shapeGroups[shapeKey] = group;
                shapeOrder.Add(shapeKey);
            }
            group.Members.Add(new Member(entry, uri, segments));
        }

        // Step 4/5 — resolve each shape group's templated path, then fold members into a final
        // (origin, method, templatedPath) bucket — the defensive merge that catches two shape keys
        // resolving to the same operation.
        var finalKeyOrder = new List<(string Origin, string Method, string Path)>();
        var finalMembers = new Dictionary<(string Origin, string Method, string Path), List<Member>>();

        foreach (var shapeKey in shapeOrder)
        {
            var (method, members) = shapeGroups[shapeKey];
            var first = members[0];
            string origin = first.Uri.GetLeftPart(UriPartial.Authority);

            int segmentCount = first.Segments.Length;
            var resolvedSegments = new string[segmentCount];
            int templateCounter = 0;
            for (int i = 0; i < segmentCount; i++)
            {
                bool candidatePosition = options.TemplateIds && IsIdentifierCandidate(first.Segments[i]);
                if (!candidatePosition)
                {
                    resolvedSegments[i] = first.Segments[i];
                    continue;
                }

                var distinctValues = members.Select(m => m.Segments[i]).Distinct(StringComparer.Ordinal).ToList();
                if (distinctValues.Count == 1)
                {
                    resolvedSegments[i] = distinctValues[0];
                }
                else
                {
                    templateCounter++;
                    resolvedSegments[i] = templateCounter == 1 ? "{id}" : $"{{id{templateCounter}}}";
                }
            }

            string templatedPath = resolvedSegments.Length == 0 ? "/" : "/" + string.Join("/", resolvedSegments);
            var key = (origin, method, templatedPath);

            if (!finalMembers.TryGetValue(key, out var list))
            {
                list = new List<Member>();
                finalMembers[key] = list;
                finalKeyOrder.Add(key);
            }
            list.AddRange(members);
        }

        // Step 6 — build one ParsedRequest per final key, then Step 7 — assemble the collection,
        // grouping requests by origin in first-seen order.
        var originOrder = new List<string>();
        var requestsByOrigin = new Dictionary<string, List<ParsedRequest>>();

        foreach (var (origin, method, templatedPath) in finalKeyOrder)
        {
            var request = BuildRequest(finalMembers[(origin, method, templatedPath)], method, origin, templatedPath);

            if (!requestsByOrigin.TryGetValue(origin, out var list))
            {
                list = new List<ParsedRequest>();
                requestsByOrigin[origin] = list;
                originOrder.Add(origin);
            }
            list.Add(request);
        }

        if (originOrder.Count == 1)
        {
            string origin = originOrder[0];
            return new ParsedCollection
            {
                Name = new Uri(origin).Host,
                BaseUrl = origin,
                Requests = requestsByOrigin[origin]
            };
        }

        var collection = new ParsedCollection { Name = "HAR capture", BaseUrl = null };
        foreach (var origin in originOrder)
        {
            collection.Folders.Add(new ParsedCollection
            {
                Name = new Uri(origin).Host,
                BaseUrl = origin,
                Requests = requestsByOrigin[origin]
            });
        }
        return collection;
    }

    private static bool IsIdentifierCandidate(string segment)
    {
        if (segment.Length == 0) return false;
        if (segment.All(c => c is >= '0' and <= '9')) return true;
        if (UuidPattern.IsMatch(segment)) return true;
        if (segment.Length >= 16 && segment.All(IsHexDigit)) return true;
        return false;
    }

    private static bool IsHexDigit(char c) =>
        (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');

    private static ParsedRequest BuildRequest(List<Member> members, string method, string origin, string templatedPath)
    {
        var entries = members.Select(m => m.Entry).ToList();

        // Headers: candidate list per entry (pseudo-headers, Host, Content-Length, Cookie, and
        // [redacted] values excluded), then keep only names present in every entry with the same
        // value, in the first entry's order and casing.
        var candidateLists = entries.Select(BuildCandidateHeaders).ToList();
        var headers = new List<KeyValuePair<string, string>>();
        foreach (var (name, value) in candidateLists[0])
        {
            bool consistent = true;
            foreach (var otherList in candidateLists.Skip(1))
            {
                var match = otherList.FirstOrDefault(h =>
                    string.Equals(h.Name, name, StringComparison.OrdinalIgnoreCase));
                if (match.Name is null || !string.Equals(match.Value, value, StringComparison.Ordinal))
                {
                    consistent = false;
                    break;
                }
            }
            if (consistent && !headers.Any(h => string.Equals(h.Key, name, StringComparison.OrdinalIgnoreCase)))
                headers.Add(new KeyValuePair<string, string>(name, value));
        }

        // Body: first entry with non-empty PostData.Text.
        string? body = null;
        string? contentType = null;
        foreach (var entry in entries)
        {
            if (entry.Request.PostData is { } postData && postData.TextOrNull() is { } text)
            {
                body = text;
                contentType = string.IsNullOrEmpty(postData.MimeType) ? null : postData.MimeType;
                break;
            }
        }

        // Query: union of keys in first-seen order, first-seen value per key.
        var queryPairs = new List<KeyValuePair<string, string>>();
        var queryKeys = new HashSet<string>();
        foreach (var entry in entries)
        {
            var entryQuery = entry.Request.QueryString.Count > 0
                ? entry.Request.QueryString.Select(q => new KeyValuePair<string, string>(q.Name, q.Value)).ToList()
                : QueryString.Parse(QueryString.Split(entry.Request.Url).Query);

            foreach (var q in entryQuery)
            {
                if (queryKeys.Add(q.Key))
                    queryPairs.Add(q);
            }
        }

        string url = QueryString.Compose(origin + templatedPath, queryPairs);

        // Description: entry count + first success.
        int count = entries.Count;
        string description = $"Captured from HAR ({count} call{(count == 1 ? "" : "s")})";
        var firstSuccess = entries.FirstOrDefault(e => e.Response.Status is >= 200 and < 300);
        if (firstSuccess is not null)
            description = $"{description}; first success {firstSuccess.Response.Status} {firstSuccess.Response.StatusText}".TrimEnd();

        return new ParsedRequest
        {
            Method = method,
            BaseUrl = origin,
            Url = url,
            Name = $"{method} {templatedPath}",
            Description = description,
            Headers = headers,
            Body = body,
            ContentType = contentType
        };
    }

    private static List<(string Name, string Value)> BuildCandidateHeaders(HarEntry entry)
    {
        var result = new List<(string Name, string Value)>();
        foreach (var h in entry.Request.Headers)
        {
            if (h.Name.StartsWith(':')) continue;
            if (string.Equals(h.Name, "Host", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.Equals(h.Name, "Content-Length", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.Equals(h.Name, "Cookie", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.Equals(h.Value, RedactedMarker, StringComparison.Ordinal)) continue;
            result.Add((h.Name, h.Value));
        }
        return result;
    }
}
