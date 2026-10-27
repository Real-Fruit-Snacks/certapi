using System.Text.Json;
using System.Text.Json.Nodes;

namespace ApiTester.Core;

/// <summary>Builds an OpenAPI 3.0 JSON document from a <see cref="ParsedCollection"/> — the
/// reverse of <see cref="OpenApiImporter"/>. Folders become tags, each request becomes an
/// operation with its query parameters, headers, and body example. Auth is exported as a
/// security *scheme* only — tokens and passwords are never written to the file.</summary>
public static class OpenApiExporter
{
    public static string ToJson(ParsedCollection collection)
    {
        var root = new JsonObject
        {
            ["openapi"] = "3.0.3",
            ["info"] = new JsonObject
            {
                ["title"] = string.IsNullOrWhiteSpace(collection.Name) ? "API collection" : collection.Name,
                ["version"] = "1.0.0"
            }
        };

        string? server = NormalizeServer(collection.BaseUrl) ?? FirstBaseUrl(collection);
        if (server is not null)
            root["servers"] = new JsonArray(new JsonObject { ["url"] = server });

        var paths = new JsonObject();
        var tags = new List<string>();
        var authSchemes = new HashSet<string>();

        void Walk(ParsedCollection c, string? tag)
        {
            foreach (var r in c.Requests) AddOperation(paths, r, tag, server, authSchemes);
            foreach (var f in c.Folders)
            {
                var t = string.IsNullOrWhiteSpace(f.Name) ? tag : f.Name;
                if (t is not null && !tags.Contains(t)) tags.Add(t);
                Walk(f, t);
            }
        }
        Walk(collection, null);

        if (tags.Count > 0)
        {
            var arr = new JsonArray();
            foreach (var t in tags) arr.Add(new JsonObject { ["name"] = t });
            root["tags"] = arr;
        }
        root["paths"] = paths;

        if (authSchemes.Count > 0)
        {
            var schemes = new JsonObject();
            if (authSchemes.Contains("bearer"))
                schemes["bearerAuth"] = new JsonObject { ["type"] = "http", ["scheme"] = "bearer" };
            if (authSchemes.Contains("basic"))
                schemes["basicAuth"] = new JsonObject { ["type"] = "http", ["scheme"] = "basic" };
            root["components"] = new JsonObject { ["securitySchemes"] = schemes };
        }

        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static void AddOperation(
        JsonObject paths, ParsedRequest r, string? tag, string? server, HashSet<string> authSchemes)
    {
        // Judged against the document's server, not the request's own base: a request whose URL
        // lives elsewhere (another host in the same HAR, another base) gets a path-level override.
        var (pathKey, query, opServer) = SplitUrl(r.Url, server);
        if (opServer is null && server is not null && NormalizeServer(r.BaseUrl) is { } own &&
            !own.Equals(server, StringComparison.OrdinalIgnoreCase) &&
            !Uri.TryCreate(QueryString.Split(r.Url ?? "").Path, UriKind.Absolute, out _))
            opServer = own;

        var op = new JsonObject();
        if (tag is not null) op["tags"] = new JsonArray(tag);
        if (!string.IsNullOrWhiteSpace(r.Name)) op["summary"] = r.Name;
        if (!string.IsNullOrWhiteSpace(r.Description)) op["description"] = r.Description;

        var parameters = new JsonArray();
        foreach (var q in query)
            parameters.Add(new JsonObject
            {
                ["name"] = q.Key,
                ["in"] = "query",
                ["required"] = false,
                ["schema"] = new JsonObject { ["type"] = "string" },
                // A spec is shared; an api_key's value is not part of the contract.
                ["example"] = MarkdownSecrets.IsSecretParameter(q.Key) ? null : q.Value
            });
        foreach (var h in r.Headers)
        {
            // Content-Type belongs to the request body; Authorization is a security scheme.
            if (h.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase) ||
                h.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase)) continue;
            parameters.Add(new JsonObject
            {
                ["name"] = h.Key,
                ["in"] = "header",
                ["required"] = false,
                ["schema"] = new JsonObject { ["type"] = "string" },
                ["example"] = SecretHeaders.IsSecret(h.Key) ? null : h.Value
            });
        }
        if (parameters.Count > 0) op["parameters"] = parameters;

        if (!string.IsNullOrEmpty(r.Body))
        {
            string ct = string.IsNullOrWhiteSpace(r.ContentType) ? "application/json" : r.ContentType!;
            JsonNode example;
            string bodyExample = MarkdownSecrets.RedactBody(r.Body!, ct);
            try { example = JsonNode.Parse(bodyExample) ?? JsonValue.Create(bodyExample)!; }
            catch (JsonException) { example = JsonValue.Create(bodyExample)!; }
            op["requestBody"] = new JsonObject
            {
                ["content"] = new JsonObject { [ct] = new JsonObject { ["example"] = example } }
            };
        }

        if (r.BearerToken is not null)
        {
            authSchemes.Add("bearer");
            op["security"] = new JsonArray(new JsonObject { ["bearerAuth"] = new JsonArray() });
        }
        else if (r.BasicUser is not null || r.BasicPassword is not null)
        {
            authSchemes.Add("basic");
            op["security"] = new JsonArray(new JsonObject { ["basicAuth"] = new JsonArray() });
        }

        op["responses"] = new JsonObject { ["default"] = new JsonObject { ["description"] = "Response" } };

        if (paths[pathKey] is not JsonObject pathItem)
        {
            pathItem = new JsonObject();
            if (opServer is not null)
                pathItem["servers"] = new JsonArray(new JsonObject { ["url"] = opServer });
            paths[pathKey] = pathItem;
        }
        string method = r.Method.ToLowerInvariant();
        if (!pathItem.ContainsKey(method)) pathItem[method] = op;   // first one wins on duplicates
    }

    /// <summary>Split a request URL into the OpenAPI path key, its query pairs, and — when the
    /// URL is absolute and lives outside <paramref name="server"/> — a path-level server override.</summary>
    private static (string PathKey, List<KeyValuePair<string, string>> Query, string? OpServer)
        SplitUrl(string url, string? server)
    {
        var (rawPath, rawQuery) = QueryString.Split(url ?? "");
        var query = QueryString.Parse(rawQuery);

        if (Uri.TryCreate(rawPath, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            string origin = uri.GetLeftPart(UriPartial.Authority);
            // A prefix at a segment boundary: https://a/v1 contains https://a/v1/items, not https://a/v10.
            if (server is not null && rawPath.StartsWith(server, StringComparison.OrdinalIgnoreCase) &&
                (rawPath.Length == server.Length || rawPath[server.Length] == '/'))
            {
                string rest = rawPath[server.Length..];
                return (EnsureLeadingSlash(rest), query, null);
            }
            return (EnsureLeadingSlash(uri.AbsolutePath), query,
                    server is null ? null : origin);
        }

        return (EnsureLeadingSlash(rawPath), query, null);
    }

    private static string EnsureLeadingSlash(string p) =>
        string.IsNullOrEmpty(p) ? "/" : p.StartsWith('/') ? p : "/" + p;

    private static string? NormalizeServer(string? baseUrl)
    {
        baseUrl = baseUrl?.Trim();
        return string.IsNullOrEmpty(baseUrl) ? null : baseUrl.TrimEnd('/');
    }

    private static string? FirstBaseUrl(ParsedCollection c)
    {
        foreach (var r in c.Requests)
            if (NormalizeServer(r.BaseUrl) is { } b) return b;
        foreach (var f in c.Folders)
            if (FirstBaseUrl(f) is { } b) return b;
        return null;
    }
}
