using System.Text.Json;

namespace ApiTester.Core;

/// <summary>Builds a <see cref="ParsedCollection"/> from a JSON OpenAPI/Swagger document.
/// Supports OpenAPI 3.x (<c>servers</c>) and Swagger 2.0 (<c>host</c>/<c>basePath</c>/<c>schemes</c>);
/// operations are grouped into folders by their first tag.</summary>
/// <summary>One parameter of an operation: query, path, header or cookie.</summary>
public sealed record OpenApiParameter(string Name, string In, bool Required);

/// <summary>An operation found by <see cref="OpenApiImporter.FindOperation"/>. <see cref="ResponseSchemas"/>
/// maps each documented status (or "default", "2XX") to its JSON body schema, null when it has none.</summary>
public sealed record OpenApiOperation(string? OperationId, string Method, string PathTemplate, string? BaseUrl,
    IReadOnlyList<OpenApiParameter> Parameters, string? RequestContentType, bool RequestBodyRequired,
    IReadOnlyDictionary<string, string?> ResponseSchemas)
{
    /// <summary>The schema documented for a status: the exact code, then "2XX"-style, then "default".</summary>
    public (string? Key, string? Schema) SchemaForStatus(int status)
    {
        string code = status.ToString(System.Globalization.CultureInfo.InvariantCulture);
        foreach (var key in new[] { code, code[0] + "XX", code[0] + "xx", "default" })
            if (ResponseSchemas.TryGetValue(key, out var schema)) return (key, schema);
        return (null, null);
    }
}

public static class OpenApiImporter
{
    private static readonly HashSet<string> Methods =
        new(StringComparer.OrdinalIgnoreCase) { "get", "post", "put", "patch", "delete", "head", "options" };

    public static ParsedCollection Parse(string json)
    {
        // YAML is the other common way an OpenAPI document is written; this importer reads JSON,
        // and says so rather than reporting "'o' is an invalid start of a value".
        string start = json.TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
        if (start.Length > 0 && start[0] is not ('{' or '['))
            throw new FormatException("this looks like YAML; certapi imports OpenAPI documents in JSON. Convert it first (for example with a YAML-to-JSON converter, or your API tool's \"export as JSON\").");
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var collection = new ParsedCollection { Name = Title(root), BaseUrl = ResolveBaseUrl(root) };

        if (!root.TryGetProperty("paths", out var paths) || paths.ValueKind != JsonValueKind.Object)
            return collection;

        var folders = new Dictionary<string, ParsedCollection>(StringComparer.OrdinalIgnoreCase);
        ParsedCollection FolderFor(string tag)
        {
            if (!folders.TryGetValue(tag, out var f))
            {
                f = new ParsedCollection { Name = tag, BaseUrl = collection.BaseUrl };
                folders[tag] = f;
                collection.Folders.Add(f);
            }
            return f;
        }

        foreach (var pathProp in paths.EnumerateObject())
        {
            var path = pathProp.Name;
            if (pathProp.Value.ValueKind != JsonValueKind.Object) continue;

            foreach (var opProp in pathProp.Value.EnumerateObject())
            {
                if (!Methods.Contains(opProp.Name)) continue;
                var op = opProp.Value;
                if (op.ValueKind != JsonValueKind.Object) continue;

                var req = new ParsedRequest
                {
                    Method = opProp.Name.ToUpperInvariant(),
                    BaseUrl = collection.BaseUrl,
                    Url = path,
                    Name = OperationName(op, opProp.Name, path),
                    Description = op.TryGetProperty("description", out var desc) &&
                                  desc.ValueKind == JsonValueKind.String ? desc.GetString() : null
                };
                AddResponseAssertions(req, op, root);
                FolderFor(Tag(op)).Requests.Add(req);
            }
        }

        return collection;
    }

    /// <summary>The operation's first documented 2xx response becomes "status == N" and, when it
    /// declares a JSON body schema, "body $ matches schema" — so an imported suite checks the API
    /// against its own spec from its first run. The schema carries the document's
    /// components/definitions so its $refs resolve. Nothing is added when the spec documents no
    /// success response.</summary>
    private static void AddResponseAssertions(ParsedRequest req, JsonElement op, JsonElement root)
    {
        if (!op.TryGetProperty("responses", out var responses) || responses.ValueKind != JsonValueKind.Object) return;
        var success = responses.EnumerateObject()
            .Where(r => r.Name.Length == 3 && r.Name[0] == '2' && int.TryParse(r.Name, out _))
            .OrderBy(r => r.Name, StringComparer.Ordinal).FirstOrDefault();
        if (success.Value.ValueKind != JsonValueKind.Object) return;
        req.Assertions.Add(new AssertionRule { Target = AssertTarget.Status, Op = AssertOp.Equals, Value = success.Name });

        if (SchemaFor(success.Value, root) is not { } schemaText) return;
        req.Assertions.Add(new AssertionRule
        {
            Target = AssertTarget.Body, Path = "$", Op = AssertOp.MatchesSchema, Value = schemaText
        });
    }

    /// <summary>A response's JSON body schema as standalone JSON (with the components it reaches),
    /// or null when it documents none.</summary>
    private static string? SchemaFor(JsonElement response, JsonElement root)
    {
        JsonElement? schema = null;
        if (response.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Object)
        {
            // OpenAPI 3: the JSON media type's schema.
            var json = content.EnumerateObject().FirstOrDefault(c =>
                c.Name.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) ||
                c.Name.EndsWith("+json", StringComparison.OrdinalIgnoreCase));
            if (json.Value.ValueKind == JsonValueKind.Object && json.Value.TryGetProperty("schema", out var s3)) schema = s3;
        }
        else if (response.TryGetProperty("schema", out var s2)) schema = s2;   // Swagger 2
        if (schema is not { } found || found.ValueKind != JsonValueKind.Object) return null;

        var node = System.Text.Json.Nodes.JsonNode.Parse(found.GetRawText())!.AsObject();
        // Only the schemas this response reaches, so a large spec does not put its whole
        // components section into every saved request.
        if (root.TryGetProperty("components", out var components) && components.ValueKind == JsonValueKind.Object &&
            components.TryGetProperty("schemas", out var schemas) && schemas.ValueKind == JsonValueKind.Object &&
            Reachable(found, schemas, "#/components/schemas/") is { Count: > 0 } used3)
            node["components"] = new System.Text.Json.Nodes.JsonObject { ["schemas"] = Subset(schemas, used3) };
        if (root.TryGetProperty("definitions", out var definitions) && definitions.ValueKind == JsonValueKind.Object &&
            Reachable(found, definitions, "#/definitions/") is { Count: > 0 } used2)
            node["definitions"] = Subset(definitions, used2);
        return node.ToJsonString();
    }

    /// <summary>One operation of a document, by operationId or "METHOD /path", for
    /// <c>certapi call</c>: where it lives, what it takes, and what each response should look like.</summary>
    public static OpenApiOperation? FindOperation(string json, string idOrMethodPath)
    {
        string start = json.TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
        if (start.Length > 0 && start[0] is not ('{' or '['))
            throw new FormatException("this looks like YAML; certapi reads OpenAPI documents in JSON. Convert it first.");
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("paths", out var paths) || paths.ValueKind != JsonValueKind.Object) return null;
        string want = idOrMethodPath.Trim();
        foreach (var pathProp in paths.EnumerateObject())
        {
            if (pathProp.Value.ValueKind != JsonValueKind.Object) continue;
            foreach (var opProp in pathProp.Value.EnumerateObject())
            {
                if (!Methods.Contains(opProp.Name) || opProp.Value.ValueKind != JsonValueKind.Object) continue;
                var op = opProp.Value;
                string? id = op.TryGetProperty("operationId", out var oid) && oid.ValueKind == JsonValueKind.String ? oid.GetString() : null;
                bool match = string.Equals(id, want, StringComparison.Ordinal) ||
                             string.Equals($"{opProp.Name} {pathProp.Name}", want, StringComparison.OrdinalIgnoreCase);
                if (!match) continue;

                var parameters = new List<OpenApiParameter>();
                void Collect(JsonElement holder)
                {
                    if (!holder.TryGetProperty("parameters", out var list) || list.ValueKind != JsonValueKind.Array) return;
                    foreach (var raw in list.EnumerateArray())
                    {
                        var p = Deref(raw, root);
                        if (p.ValueKind != JsonValueKind.Object) continue;
                        string? name = p.TryGetProperty("name", out var n) ? n.GetString() : null;
                        string? where = p.TryGetProperty("in", out var i) ? i.GetString() : null;
                        if (name is null || where is null) continue;
                        bool required = where == "path" || (p.TryGetProperty("required", out var r) && r.ValueKind == JsonValueKind.True);
                        parameters.RemoveAll(x => x.Name == name && x.In == where);   // operation level beats path level
                        parameters.Add(new OpenApiParameter(name, where, required));
                    }
                }
                Collect(pathProp.Value);
                Collect(op);

                var responses = new Dictionary<string, string?>(StringComparer.Ordinal);
                if (op.TryGetProperty("responses", out var resp) && resp.ValueKind == JsonValueKind.Object)
                    foreach (var r in resp.EnumerateObject())
                        responses[r.Name] = SchemaFor(Deref(r.Value, root), root);

                string? bodyType = null;
                bool bodyRequired = false;
                if (op.TryGetProperty("requestBody", out var rb) && Deref(rb, root) is { ValueKind: JsonValueKind.Object } body)
                {
                    bodyRequired = body.TryGetProperty("required", out var req) && req.ValueKind == JsonValueKind.True;
                    if (body.TryGetProperty("content", out var bc) && bc.ValueKind == JsonValueKind.Object)
                        bodyType = bc.EnumerateObject().Select(c => c.Name).OrderBy(c => c.Contains("json") ? 0 : 1).FirstOrDefault();
                }
                else if (parameters.Any(p => p.In == "body")) { bodyType = "application/json"; bodyRequired = parameters.First(p => p.In == "body").Required; }

                return new OpenApiOperation(id, opProp.Name.ToUpperInvariant(), pathProp.Name, ResolveBaseUrl(root),
                    parameters.Where(p => p.In != "body").ToList(), bodyType, bodyRequired, responses);
            }
        }
        return null;
    }

    /// <summary>The operations a document has, "operationId  METHOD /path", for an error message.</summary>
    public static IReadOnlyList<string> ListOperations(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var list = new List<string>();
        if (!doc.RootElement.TryGetProperty("paths", out var paths) || paths.ValueKind != JsonValueKind.Object) return list;
        foreach (var path in paths.EnumerateObject())
            if (path.Value.ValueKind == JsonValueKind.Object)
                foreach (var op in path.Value.EnumerateObject().Where(o => Methods.Contains(o.Name)))
                    list.Add((op.Value.TryGetProperty("operationId", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() + "  " : "") +
                             op.Name.ToUpperInvariant() + " " + path.Name);
        return list;
    }

    private static JsonElement Deref(JsonElement e, JsonElement root)
    {
        for (int hops = 0; hops < 8 && e.ValueKind == JsonValueKind.Object && e.TryGetProperty("$ref", out var r) &&
                           r.ValueKind == JsonValueKind.String && r.GetString()!.StartsWith("#/", StringComparison.Ordinal); hops++)
        {
            var node = root;
            foreach (var part in r.GetString()![2..].Split('/'))
                if (!node.TryGetProperty(part.Replace("~1", "/").Replace("~0", "~"), out node)) return e;
            e = node;
        }
        return e.Clone();
    }

    /// <summary>Every schema name under <paramref name="prefix"/> reachable by $ref from
    /// <paramref name="start"/>, following refs inside the referenced schemas too.</summary>
    private static HashSet<string> Reachable(JsonElement start, JsonElement pool, string prefix)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<JsonElement>();
        queue.Enqueue(start);
        while (queue.Count > 0)
        {
            foreach (var reference in Refs(queue.Dequeue()))
            {
                if (!reference.StartsWith(prefix, StringComparison.Ordinal)) continue;
                string name = Uri.UnescapeDataString(reference[prefix.Length..].Split('/')[0]).Replace("~1", "/").Replace("~0", "~");
                if (found.Add(name) && pool.TryGetProperty(name, out var next)) queue.Enqueue(next);
            }
        }
        return found;
    }

    private static IEnumerable<string> Refs(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in e.EnumerateObject())
            {
                if (p.Name == "$ref" && p.Value.ValueKind == JsonValueKind.String) yield return p.Value.GetString()!;
                else foreach (var r in Refs(p.Value)) yield return r;
            }
        }
        else if (e.ValueKind == JsonValueKind.Array)
            foreach (var item in e.EnumerateArray())
                foreach (var r in Refs(item)) yield return r;
    }

    private static System.Text.Json.Nodes.JsonObject Subset(JsonElement pool, HashSet<string> names)
    {
        var o = new System.Text.Json.Nodes.JsonObject();
        foreach (var p in pool.EnumerateObject())
            if (names.Contains(p.Name)) o[p.Name] = System.Text.Json.Nodes.JsonNode.Parse(p.Value.GetRawText());
        return o;
    }

    private static string Title(JsonElement root) =>
        root.TryGetProperty("info", out var info) &&
        info.TryGetProperty("title", out var title) &&
        title.ValueKind == JsonValueKind.String
            ? title.GetString()!
            : "Imported API";

    private static string Tag(JsonElement op) =>
        op.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Array && tags.GetArrayLength() > 0 &&
        tags[0].ValueKind == JsonValueKind.String
            ? tags[0].GetString()!
            : "default";

    private static string OperationName(JsonElement op, string method, string path)
    {
        if (op.TryGetProperty("summary", out var s) && s.ValueKind == JsonValueKind.String && s.GetString()!.Length > 0)
            return s.GetString()!;
        if (op.TryGetProperty("operationId", out var oid) && oid.ValueKind == JsonValueKind.String && oid.GetString()!.Length > 0)
            return oid.GetString()!;
        return $"{method.ToUpperInvariant()} {path}";
    }

    private static string? ResolveBaseUrl(JsonElement root)
    {
        // OpenAPI 3.x
        if (root.TryGetProperty("servers", out var servers) && servers.ValueKind == JsonValueKind.Array &&
            servers.GetArrayLength() > 0 && servers[0].TryGetProperty("url", out var url) &&
            url.ValueKind == JsonValueKind.String)
        {
            return url.GetString();
        }

        // Swagger 2.0
        if (root.TryGetProperty("host", out var host) && host.ValueKind == JsonValueKind.String)
        {
            string scheme = "https";
            if (root.TryGetProperty("schemes", out var schemes) && schemes.ValueKind == JsonValueKind.Array &&
                schemes.GetArrayLength() > 0 && schemes[0].ValueKind == JsonValueKind.String)
                scheme = schemes[0].GetString()!;
            string basePath = root.TryGetProperty("basePath", out var bp) && bp.ValueKind == JsonValueKind.String
                ? bp.GetString()! : "";
            return $"{scheme}://{host.GetString()}{basePath}";
        }

        return null;
    }
}
