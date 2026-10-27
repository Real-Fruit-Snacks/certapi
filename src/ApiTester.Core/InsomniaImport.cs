using System.Text.Json;
using System.Text.RegularExpressions;

namespace ApiTester.Core;

/// <summary>What an Insomnia export parsed to: the folder tree, its environments, and warnings for
/// anything that could not be carried across faithfully — named rather than silently dropped.</summary>
public sealed record InsomniaImportResult(
    CollectionNode Root, IReadOnlyList<ApiEnvironment> Environments, IReadOnlyList<string> Warnings);

/// <summary>Reads an Insomnia v4 export (`_type: "export"`, `__export_format: 4`) into this
/// product's collections tree. Pure text-in, tree-out: no file or network access, so every mapping
/// rule is testable as data — the same shape <see cref="PostmanImport"/> takes.
/// <para>Insomnia's export is a flat <c>resources</c> array whose entries point at their parent by
/// id, so the tree is rebuilt here rather than read off the file. Its template syntax
/// <c>{{ _.name }}</c> is translated to this product's <c>{{name}}</c>; a tag template
/// (<c>{% … %}</c>, used for generated values and response chaining) has no equivalent and is
/// reported as a warning naming the request it was found in.</para></summary>
public static class InsomniaImport
{
    // Insomnia writes {{ _.varName }} in v4 (and bare {{ varName }} in older exports); both mean
    // "substitute this variable", which is exactly what {{varName}} means here.
    private static readonly Regex UnderscoreToken = new(@"\{\{\s*_\.([A-Za-z0-9_\-]+)\s*\}\}", RegexOptions.Compiled);
    private static readonly Regex TagTemplate = new(@"\{%.*?%\}", RegexOptions.Compiled | RegexOptions.Singleline);

    /// <summary>Throws <see cref="FormatException"/> when the text is not an Insomnia export at
    /// all; anything less total is a warning on the result instead.</summary>
    public static InsomniaImportResult Parse(string json)
    {
        JsonElement root;
        try { root = JsonDocument.Parse(json).RootElement; }
        catch (JsonException ex) { throw new FormatException("Not JSON: " + ex.Message); }

        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("resources", out var resources) ||
            resources.ValueKind != JsonValueKind.Array)
            throw new FormatException(
                "Not an Insomnia export: expected an object with a 'resources' array. " +
                "(Insomnia's menu calls this Export Data → Insomnia v4 (JSON).)");

        var warnings = new List<string>();
        var all = resources.EnumerateArray().Where(r => r.ValueKind == JsonValueKind.Object).ToList();

        // The workspace names the import. An export can carry more than one; the first names the
        // root, and everything else still lands inside it, so nothing is dropped either way.
        var workspace = all.FirstOrDefault(r => TypeOf(r) == "workspace");
        string rootName = Str(workspace, "name") ?? "Insomnia import";

        var rootNode = new CollectionNode { Name = rootName, IsFolder = true };

        // Folders first, so a request can be attached to one; parentage is by id, and a parent that
        // is a workspace (or missing entirely) lands at the root.
        var folders = new Dictionary<string, CollectionNode>(StringComparer.Ordinal);
        foreach (var group in all.Where(r => TypeOf(r) == "request_group"))
        {
            string? id = Str(group, "_id");
            if (id is null) continue;
            folders[id] = new CollectionNode { Name = Str(group, "name") ?? "(unnamed folder)", IsFolder = true };
        }
        // Attach folders to their parents after they all exist, so nesting order in the file does
        // not matter — Insomnia does not guarantee parents come first.
        foreach (var group in all.Where(r => TypeOf(r) == "request_group"))
        {
            string? id = Str(group, "_id");
            if (id is null || !folders.TryGetValue(id, out var node)) continue;
            string? parent = Str(group, "parentId");
            if (parent is not null && folders.TryGetValue(parent, out var parentNode)) parentNode.Children.Add(node);
            else rootNode.Children.Add(node);
        }

        foreach (var request in all.Where(r => TypeOf(r) == "request"))
        {
            var node = ParseRequest(request, warnings);
            if (node is null) continue;
            string? parent = Str(request, "parentId");
            if (parent is not null && folders.TryGetValue(parent, out var parentNode)) parentNode.Children.Add(node);
            else rootNode.Children.Add(node);
        }

        var environments = new List<ApiEnvironment>();
        foreach (var env in all.Where(r => TypeOf(r) == "environment"))
        {
            if (!env.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object) continue;
            var apiEnv = new ApiEnvironment { Name = Str(env, "name") ?? "Insomnia" };
            foreach (var property in data.EnumerateObject())
                apiEnv.Variables.Add(new Variable
                {
                    Key = property.Name,
                    // A non-string value (number, bool, nested object) is written back as its JSON
                    // text: it is still the value the user set, and dropping it would lose it.
                    Value = property.Value.ValueKind == JsonValueKind.String
                        ? property.Value.GetString() ?? ""
                        : property.Value.GetRawText()
                });
            if (apiEnv.Variables.Count > 0) environments.Add(apiEnv);
        }

        return new InsomniaImportResult(rootNode, environments, warnings);
    }

    private static CollectionNode? ParseRequest(JsonElement request, List<string> warnings)
    {
        string name = Str(request, "name") ?? "(unnamed)";
        var model = new RequestModel { Method = (Str(request, "method") ?? "GET").ToUpperInvariant() };

        string url = Translate(Str(request, "url") ?? "", name, warnings);
        int q = url.IndexOf('?');
        model.Path = q < 0 ? url : url[..q];

        if (request.TryGetProperty("parameters", out var parameters) && parameters.ValueKind == JsonValueKind.Array)
            foreach (var p in parameters.EnumerateArray())
                model.QueryParams.Add(new ParamRow
                {
                    Key = Translate(Str(p, "name") ?? "", name, warnings),
                    Value = Translate(Str(p, "value") ?? "", name, warnings),
                    Enabled = !(Bool(p, "disabled") ?? false)
                });
        // A query typed into the URL itself, with no parameter rows, is the request's query too.
        // (With rows, the rows are the query — the established reading of an export.)
        if (model.QueryParams.Count == 0 && q >= 0)
            foreach (var kv in RequestUrl.SplitForEditing(url).Params)
                model.QueryParams.Add(new ParamRow { Key = kv.Key, Value = kv.Value });

        if (request.TryGetProperty("headers", out var headers) && headers.ValueKind == JsonValueKind.Array)
            foreach (var h in headers.EnumerateArray())
                model.Headers.Add(new HeaderRow
                {
                    Name = Translate(Str(h, "name") ?? "", name, warnings),
                    Value = Translate(Str(h, "value") ?? "", name, warnings),
                    Enabled = !(Bool(h, "disabled") ?? false)
                });

        if (request.TryGetProperty("body", out var body) && body.ValueKind == JsonValueKind.Object)
            ApplyBody(name, body, model, warnings);

        if (request.TryGetProperty("authentication", out var auth) && auth.ValueKind == JsonValueKind.Object)
            ApplyAuth(name, auth, model, warnings);

        return new CollectionNode { Name = name, IsFolder = false, Request = model };
    }

    private static void ApplyBody(string name, JsonElement body, RequestModel model, List<string> warnings)
    {
        string? mimeType = Str(body, "mimeType");
        if (body.TryGetProperty("params", out var formParams) && formParams.ValueKind == JsonValueKind.Array)
        {
            bool multipart = mimeType is not null &&
                mimeType.Contains("multipart", StringComparison.OrdinalIgnoreCase);
            if (multipart)
            {
                model.IsMultipart = true;
                foreach (var p in formParams.EnumerateArray())
                {
                    bool isFile = Str(p, "type") == "file";
                    if (isFile) warnings.Add($"'{name}': file part '{Str(p, "name")}' imported disabled — set its path before sending.");
                    model.FormParts.Add(new FormPart
                    {
                        Name = Str(p, "name") ?? "",
                        Value = isFile ? (Str(p, "fileName") ?? "") : Translate(Str(p, "value") ?? "", name, warnings),
                        IsFile = isFile,
                        Enabled = !isFile && !(Bool(p, "disabled") ?? false)
                    });
                }
            }
            else
            {
                model.Body = string.Join("&", formParams.EnumerateArray()
                    .Where(p => !(Bool(p, "disabled") ?? false))
                    // {{variables}} stay as written so they still resolve; the text around them is encoded.
                    .Select(p => $"{VariableResolver.EscapeOutsideTokens(Str(p, "name") ?? "", Uri.EscapeDataString)}=" +
                                 $"{VariableResolver.EscapeOutsideTokens(Translate(Str(p, "value") ?? "", name, warnings), Uri.EscapeDataString)}"));
                model.ContentType = "application/x-www-form-urlencoded";
            }
            return;
        }

        if (Str(body, "text") is { } text)
        {
            model.Body = Translate(text, name, warnings);
            model.ContentType = mimeType is { Length: > 0 } ? mimeType : "application/json";
            return;
        }

        if (Str(body, "fileName") is { } fileName)
            warnings.Add($"'{name}': body is a file upload ('{fileName}') — the file itself is not imported.");
    }

    private static void ApplyAuth(string name, JsonElement auth, RequestModel model, List<string> warnings)
    {
        // Insomnia marks a switched-off block rather than removing it.
        if (Bool(auth, "disabled") == true) return;

        switch (Str(auth, "type"))
        {
            case "bearer":
                model.AuthType = "Bearer";
                model.AuthSecret = Translate(Str(auth, "token") ?? "", name, warnings);
                break;
            case "basic":
                model.AuthType = "Basic";
                model.AuthUser = Translate(Str(auth, "username") ?? "", name, warnings);
                model.AuthSecret = Translate(Str(auth, "password") ?? "", name, warnings);
                break;
            case null or "none":
                break;
            case var other:
                model.AuthType = "None";
                warnings.Add($"'{name}': authentication type '{other}' is not supported — the request imported without auth.");
                break;
        }
    }

    /// <summary>Insomnia's template syntax to this product's. <c>{{ _.name }}</c> becomes
    /// <c>{{name}}</c>; a tag template (<c>{% … %}</c>) has no equivalent here — it is a small
    /// program, not a value — so it is left in the text verbatim and reported once per request,
    /// which is more useful than a silent drop that would fail at send time instead.</summary>
    private static string Translate(string value, string requestName, List<string> warnings)
    {
        if (string.IsNullOrEmpty(value)) return value ?? "";

        string translated = UnderscoreToken.Replace(value, m => "{{" + m.Groups[1].Value + "}}");
        if (TagTemplate.IsMatch(translated))
        {
            string warning = $"'{requestName}': contains an Insomnia tag template ({{% … %}}) that has no equivalent here — " +
                             "it is left as text and must be replaced by hand.";
            if (!warnings.Contains(warning)) warnings.Add(warning);
        }
        return translated;
    }

    private static string? TypeOf(JsonElement resource) => Str(resource, "_type");

    private static string? Str(JsonElement o, string name) =>
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    /// <summary>As <see cref="Str(JsonElement,string)"/>, for an element that may not exist at all
    /// (<c>default</c>), which is what <c>FirstOrDefault</c> hands back for an export with no
    /// workspace resource.</summary>
    private static string? Str(JsonElement? o, string name) =>
        o is { } element ? Str(element, name) : null;

    private static bool? Bool(JsonElement o, string name) =>
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) &&
        v.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? v.GetBoolean() : null;
}
