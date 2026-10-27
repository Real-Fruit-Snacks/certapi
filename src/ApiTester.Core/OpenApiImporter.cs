using System.Text.Json;

namespace ApiTester.Core;

/// <summary>Builds a <see cref="ParsedCollection"/> from a JSON OpenAPI/Swagger document.
/// Supports OpenAPI 3.x (<c>servers</c>) and Swagger 2.0 (<c>host</c>/<c>basePath</c>/<c>schemes</c>);
/// operations are grouped into folders by their first tag.</summary>
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
                FolderFor(Tag(op)).Requests.Add(req);
            }
        }

        return collection;
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
