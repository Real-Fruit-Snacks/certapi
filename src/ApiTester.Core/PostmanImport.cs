using System.Text.Json;

namespace ApiTester.Core;

/// <summary>What a Postman collection parsed to: the folder tree, the collection's own variables
/// as an environment (null when it declares none), and warnings for anything that could not be
/// carried across faithfully — named rather than silently dropped, so the operator knows exactly
/// what to check by hand.</summary>
public sealed record PostmanImportResult(CollectionNode Root, ApiEnvironment? Variables, IReadOnlyList<string> Warnings);

/// <summary>Reads a Postman Collection (format v2.0 / v2.1) into this product's collections tree.
/// Pure text-in, tree-out: no file or network access, so every mapping rule is testable as data.
/// `{{variable}}` syntax is shared between the two products, so request text imports unchanged;
/// collection-level variables become an environment named after the collection.</summary>
public static class PostmanImport
{
    /// <summary>Throws <see cref="FormatException"/> when the text is not a Postman collection at
    /// all; anything less total is a warning on the result instead.</summary>
    public static PostmanImportResult Parse(string json)
    {
        JsonElement root;
        try { root = JsonDocument.Parse(json).RootElement; }
        catch (JsonException ex) { throw new FormatException("Not JSON: " + ex.Message); }

        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("info", out var info) ||
            !root.TryGetProperty("item", out var items) || items.ValueKind != JsonValueKind.Array)
            throw new FormatException(
                "Not a Postman collection: expected an object with 'info' and 'item'. " +
                "(Postman's export calls this Collection v2.0/v2.1.)");

        string name = Str(info, "name") ?? "Postman import";
        var warnings = new List<string>();
        var collectionAuth = root.TryGetProperty("auth", out var ca) ? ca : default;

        var rootNode = new CollectionNode { Name = name, IsFolder = true };
        foreach (var item in items.EnumerateArray())
            if (ParseItem(item, collectionAuth, warnings) is { } child)
                rootNode.Children.Add(child);

        ApiEnvironment? environment = null;
        if (root.TryGetProperty("variable", out var vars) && vars.ValueKind == JsonValueKind.Array)
        {
            environment = new ApiEnvironment { Name = name };
            foreach (var v in vars.EnumerateArray())
            {
                string? key = Str(v, "key");
                if (string.IsNullOrWhiteSpace(key)) continue;
                environment.Variables.Add(new Variable
                {
                    Key = key!,
                    Value = Str(v, "value") ?? "",
                    // Postman marks secrets with "type": "secret"; carrying that across means the
                    // value is encrypted at rest here exactly as it was masked there.
                    Secret = string.Equals(Str(v, "type"), "secret", StringComparison.OrdinalIgnoreCase)
                });
            }
            if (environment.Variables.Count == 0) environment = null;
        }

        return new PostmanImportResult(rootNode, environment, warnings);
    }

    private static CollectionNode? ParseItem(JsonElement item, JsonElement collectionAuth, List<string> warnings)
    {
        if (item.ValueKind != JsonValueKind.Object) return null;
        string name = Str(item, "name") ?? "(unnamed)";

        // An entry with "item" is a folder, however else it is decorated.
        if (item.TryGetProperty("item", out var children) && children.ValueKind == JsonValueKind.Array)
        {
            var folder = new CollectionNode { Name = name, IsFolder = true };
            // A folder-level auth becomes the effective fallback for its children.
            var folderAuth = item.TryGetProperty("auth", out var fa) ? fa : collectionAuth;
            foreach (var child in children.EnumerateArray())
                if (ParseItem(child, folderAuth, warnings) is { } node)
                    folder.Children.Add(node);
            return folder;
        }

        if (!item.TryGetProperty("request", out var request)) return null;
        // A request may be just a URL string — Postman allows it and so does this.
        if (request.ValueKind == JsonValueKind.String)
            return new CollectionNode
            {
                Name = name, IsFolder = false,
                Request = new RequestModel { Method = "GET", Path = request.GetString()! }
            };
        if (request.ValueKind != JsonValueKind.Object) return null;

        var model = new RequestModel { Method = (Str(request, "method") ?? "GET").ToUpperInvariant() };

        // ---- URL: string form, or object form whose "raw" wins ---------------------------
        if (request.TryGetProperty("url", out var url))
        {
            if (url.ValueKind == JsonValueKind.String) model.Path = url.GetString() ?? "";
            else if (url.ValueKind == JsonValueKind.Object)
            {
                string raw = Str(url, "raw") ?? JoinUrl(url);
                // Query params come across as rows; the raw URL keeps them too, and the product
                // treats rows as additive — so strip the query from the path to avoid doubling.
                int q = raw.IndexOf('?');
                model.Path = q < 0 ? raw : raw[..q];
                if (url.TryGetProperty("query", out var query) && query.ValueKind == JsonValueKind.Array)
                    foreach (var p in query.EnumerateArray())
                        model.QueryParams.Add(new ParamRow
                        {
                            Key = Str(p, "key") ?? "",
                            Value = Str(p, "value") ?? "",
                            Enabled = !(Bool(p, "disabled") ?? false)
                        });
            }
        }

        // ---- headers ----------------------------------------------------------------------
        if (request.TryGetProperty("header", out var headers) && headers.ValueKind == JsonValueKind.Array)
            foreach (var h in headers.EnumerateArray())
                model.Headers.Add(new HeaderRow
                {
                    Name = Str(h, "key") ?? "",
                    Value = Str(h, "value") ?? "",
                    Enabled = !(Bool(h, "disabled") ?? false)
                });

        // ---- body ---------------------------------------------------------------------------
        if (request.TryGetProperty("body", out var body) && body.ValueKind == JsonValueKind.Object)
            ApplyBody(name, body, model, warnings);

        // ---- auth: request-level wins, else the folder/collection fallback -----------------
        var auth = request.TryGetProperty("auth", out var ra) ? ra : collectionAuth;
        if (auth.ValueKind == JsonValueKind.Object) ApplyAuth(name, auth, model, warnings);

        return new CollectionNode { Name = name, IsFolder = false, Request = model };
    }

    private static void ApplyBody(string name, JsonElement body, RequestModel model, List<string> warnings)
    {
        switch (Str(body, "mode"))
        {
            case "raw":
                model.Body = Str(body, "raw") ?? "";
                model.ContentType = RawLanguage(body) switch
                {
                    "json" => "application/json",
                    "xml" => "application/xml",
                    "text" => "text/plain",
                    _ => model.Headers.FirstOrDefault(h =>
                             h.Enabled && h.Name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
                         ?.Value ?? "application/json"
                };
                break;

            case "urlencoded":
                if (body.TryGetProperty("urlencoded", out var fields) && fields.ValueKind == JsonValueKind.Array)
                    model.Body = string.Join("&", fields.EnumerateArray()
                        .Where(f => !(Bool(f, "disabled") ?? false))
                        // {{variables}} stay as written so they still resolve; the text around them is encoded.
                        .Select(f => $"{Esc(Str(f, "key") ?? "")}={Esc(Str(f, "value") ?? "")}"));
                model.ContentType = "application/x-www-form-urlencoded";
                break;

            case "formdata":
                model.IsMultipart = true;
                if (body.TryGetProperty("formdata", out var parts) && parts.ValueKind == JsonValueKind.Array)
                    foreach (var p in parts.EnumerateArray())
                    {
                        bool isFile = string.Equals(Str(p, "type"), "file", StringComparison.OrdinalIgnoreCase);
                        // A file part imports disabled: the path came from someone else's machine,
                        // and silently uploading whatever happens to sit there would be worse than
                        // asking the operator to point it somewhere real first.
                        if (isFile) warnings.Add($"'{name}': file part '{Str(p, "key")}' imported disabled — set its path before sending.");
                        model.FormParts.Add(new FormPart
                        {
                            Name = Str(p, "key") ?? "",
                            Value = isFile ? (Str(p, "src") ?? "") : (Str(p, "value") ?? ""),
                            IsFile = isFile,
                            Enabled = !isFile && !(Bool(p, "disabled") ?? false)
                        });
                    }
                break;

            case null:
                break;

            case var other:
                warnings.Add($"'{name}': body mode '{other}' is not supported — the request imported without a body.");
                break;
        }
    }

    private static void ApplyAuth(string name, JsonElement auth, RequestModel model, List<string> warnings)
    {
        string? type = Str(auth, "type");
        switch (type)
        {
            case "bearer":
                model.AuthType = "Bearer";
                model.AuthSecret = AuthValue(auth, "bearer", "token");
                break;
            case "basic":
                model.AuthType = "Basic";
                model.AuthUser = AuthValue(auth, "basic", "username");
                model.AuthSecret = AuthValue(auth, "basic", "password");
                break;
            case "apikey":
            {
                // An API key is a header or query row, whichever Postman said; there is no
                // dedicated auth slot for it here, and a row is exactly what gets sent anyway.
                string key = AuthValue(auth, "apikey", "key") ?? "X-Api-Key";
                string value = AuthValue(auth, "apikey", "value") ?? "";
                if (string.Equals(AuthValue(auth, "apikey", "in"), "query", StringComparison.OrdinalIgnoreCase))
                    model.QueryParams.Add(new ParamRow { Key = key, Value = value });
                else
                    model.Headers.Add(new HeaderRow { Name = key, Value = value });
                break;
            }
            case null:
                break;   // nothing said: the product's own default (Auto) stands
            case "noauth":
                // An explicit "no auth" must not become Auto, which would attach a captured token.
                model.AuthType = "None";
                break;
            default:
                model.AuthType = "None";
                warnings.Add($"'{name}': auth type '{type}' is not supported — the request imported without auth.");
                break;
        }
    }

    /// <summary>Postman writes auth parameters two ways: v2.1 as an array of {key,value} under the
    /// type's name, v2.0 as a plain object. Both are read.</summary>
    private static string? AuthValue(JsonElement auth, string section, string key)
    {
        if (!auth.TryGetProperty(section, out var s)) return null;
        if (s.ValueKind == JsonValueKind.Object) return Str(s, key);
        if (s.ValueKind == JsonValueKind.Array)
            foreach (var entry in s.EnumerateArray())
                if (string.Equals(Str(entry, "key"), key, StringComparison.OrdinalIgnoreCase))
                    return Str(entry, "value");
        return null;
    }

    private static string? RawLanguage(JsonElement body) =>
        body.TryGetProperty("options", out var o) && o.ValueKind == JsonValueKind.Object &&
        o.TryGetProperty("raw", out var r) && r.ValueKind == JsonValueKind.Object
            ? Str(r, "language") : null;

    /// <summary>The object form without a raw: protocol + host segments + path segments, the way
    /// Postman assembles it.</summary>
    private static string JoinUrl(JsonElement url)
    {
        string protocol = Str(url, "protocol") ?? "https";
        string host = url.TryGetProperty("host", out var h) && h.ValueKind == JsonValueKind.Array
            ? string.Join(".", h.EnumerateArray().Select(x => x.GetString()))
            : Str(url, "host") ?? "";
        string path = url.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.Array
            ? "/" + string.Join("/", p.EnumerateArray().Select(x => x.GetString()))
            : Str(url, "path") is { } sp ? "/" + sp.TrimStart('/') : "";
        string port = Str(url, "port") is { Length: > 0 } pt ? ":" + pt : "";
        return $"{protocol}://{host}{port}{path}";
    }

    private static string? Str(JsonElement o, string name) =>
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    private static bool? Bool(JsonElement o, string name) =>
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) &&
        v.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? v.GetBoolean() : null;

    private static string Esc(string s) => VariableResolver.EscapeOutsideTokens(s, Uri.EscapeDataString);
}
