using System.Text.Json;

namespace ApiTester.Core;

/// <summary>One site as written in a `serve --routes` file or a profile's <c>serve.sites</c>,
/// before its certificate and files are loaded. Paths are already resolved against the file that
/// named them when a base directory was known.</summary>
public sealed record GatewayRouteSpec(
    string Prefix, Uri Upstream,
    string? Cert = null, string? CertFile = null, string? CertPassword = null, string? KeyFile = null)
{
    public bool HasCertificate => Cert is not null || CertFile is not null;

    /// <summary>The site's name (and virtual-host label); null for a site written only as a prefix.</summary>
    public string? Name { get; init; }

    public bool? Insecure { get; init; }
    public IReadOnlyList<string> CaFiles { get; init; } = Array.Empty<string>();
    public string? Revocation { get; init; }
    /// <summary>true: answer Windows-auth as the signed-in account (or as <see cref="WindowsUser"/>);
    /// false: never, even with --windows-auth; null: inherit.</summary>
    public bool? WindowsAuth { get; init; }
    public string? WindowsUser { get; init; }
    public string? WindowsPassword { get; init; }
    public IReadOnlyList<KeyValuePair<string, string>> Headers { get; init; } =
        Array.Empty<KeyValuePair<string, string>>();

    /// <summary>Whether the site changes anything beyond where it points and which certificate it shows.</summary>
    public bool HasSettings =>
        Insecure is not null || CaFiles.Count > 0 || Revocation is not null || WindowsAuth is not null ||
        WindowsUser is not null || Headers.Count > 0;
}

/// <summary>The site format shared by `serve --routes <file>` and a profile's <c>serve.sites</c>:
/// a JSON array of objects, or (in a file) an object with a <c>routes</c> or <c>sites</c> array.
/// <code>
/// { "name": "hr", "upstream": "https://hr.corp:8443", "cert": "CN=svc-hr",
///   "insecure": false, "caFile": "hr-root.pem", "revocation": "online",
///   "windowsAuth": true, "headers": { "X-Team": "people" } }
/// </code>
/// <c>name</c> mounts the site at <c>/name</c> and, under --vhost, at <c>name.gw.localhost</c>;
/// <c>prefix</c> mounts it at that path instead of <c>/name</c>; the name still labels the site and its
/// virtual host. Every setting is an
/// override of the gateway's flags; left out, the site inherits them.</summary>
public static class GatewayRoutesFile
{
    private static readonly string[] Known =
    {
        "name", "prefix", "path", "upstream", "url",
        "cert", "certFile", "certPassword", "keyFile",
        "insecure", "caFile", "revocation", "windowsAuth", "windowsUser", "windowsPassword", "headers",
    };

    /// <summary>A routes file. <paramref name="baseDirectory"/> resolves relative <c>certFile</c>,
    /// <c>keyFile</c> and <c>caFile</c> paths (null leaves them as written);
    /// <paramref name="environment"/> resolves <c>${env:NAME}</c> in values.</summary>
    public static IReadOnlyList<GatewayRouteSpec> Parse(
        string json, string? fileName = null, string? baseDirectory = null, Func<string, string?>? environment = null)
    {
        string where = fileName is null ? "--routes" : $"--routes {fileName}";
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }); }
        catch (JsonException ex) { throw new InvalidDataException($"{where}: not valid JSON: {ex.Message}"); }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (!(root.TryGetProperty("routes", out var inner) || root.TryGetProperty("sites", out inner)) ||
                    inner.ValueKind != JsonValueKind.Array)
                    throw new InvalidDataException($"{where}: expected a JSON array of sites, or an object with a \"routes\" (or \"sites\") array.");
                root = inner;
            }
            else if (root.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException($"{where}: expected a JSON array of sites, or an object with a \"routes\" (or \"sites\") array.");

            var env = environment ?? Environment.GetEnvironmentVariable;
            var specs = ParseSites(root, where, baseDirectory, (value, field) => ExpandEnvironment(value, field, where, env));
            if (specs.Count == 0) throw new InvalidDataException($"{where}: no sites.");
            return specs;
        }
    }

    /// <summary>Sites from a JSON array — the routes file and the profile both end up here, so the
    /// two can never accept different shapes. <paramref name="expand"/> resolves a value's
    /// <c>${env:…}</c> references, given the value and the field it came from.</summary>
    public static List<GatewayRouteSpec> ParseSites(
        JsonElement array, string where, string? baseDirectory, Func<string, string, string> expand)
    {
        if (array.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException($"{where}: \"sites\" must be an array.");
        var specs = new List<GatewayRouteSpec>();
        int index = 0;
        foreach (var item in array.EnumerateArray())
        {
            index++;
            if (item.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException($"{where}: site #{index} is not an object.");
            string? Str(string name) => StrOf(item, name, where, index, expand);

            string? name = Str("name");
            string? prefix = Str("prefix") ?? Str("path");
            string label = name ?? prefix ?? $"#{index}";
            string upstreamRaw = Str("upstream") ?? Str("url") ?? "";

            if (name is not null && !GatewayRoute.IsValidName(name))
                throw new InvalidDataException($"{where}: site #{index}: name '{name}' must be letters, digits and inner hyphens (it is also a host name label).");
            if (name is null && string.IsNullOrWhiteSpace(prefix))
                throw new InvalidDataException($"{where}: site #{index} needs a \"name\" (for example \"api\") or a \"prefix\" (for example \"/api\").");
            if (!Uri.TryCreate(upstreamRaw, UriKind.Absolute, out var upstream) ||
                (upstream.Scheme != Uri.UriSchemeHttp && upstream.Scheme != Uri.UriSchemeHttps))
                throw new InvalidDataException($"{where}: site '{label}' needs an absolute http(s) \"upstream\".");
            if (upstream.UserInfo.Length > 0)
                throw new InvalidDataException($"{where}: site '{label}': credentials do not belong in the upstream URL; use a certificate, \"windowsUser\" or \"headers\".");

            foreach (var property in item.EnumerateObject())
                if (!Known.Contains(property.Name))
                    throw new InvalidDataException($"{where}: site '{label}' has an unknown field \"{property.Name}\" (known: {string.Join(", ", Known)}).");

            string? Path_(string? raw) => raw is null || baseDirectory is null || Path.IsPathRooted(raw)
                ? raw : Path.GetFullPath(Path.Combine(baseDirectory, raw));

            var caFiles = new List<string>();
            if (item.TryGetProperty("caFile", out var caElement))
            {
                IEnumerable<JsonElement> entries = caElement.ValueKind == JsonValueKind.Array ? caElement.EnumerateArray() : new[] { caElement };
                foreach (var entry in entries)
                {
                    if (entry.ValueKind != JsonValueKind.String)
                        throw new InvalidDataException($"{where}: site '{label}': \"caFile\" must be a path or a list of paths.");
                    caFiles.Add(Path_(expand(entry.GetString()!, "caFile"))!);
                }
            }

            var headers = new List<KeyValuePair<string, string>>();
            if (item.TryGetProperty("headers", out var headersElement))
            {
                if (headersElement.ValueKind != JsonValueKind.Object)
                    throw new InvalidDataException($"{where}: site '{label}': \"headers\" must be an object of name: value.");
                foreach (var h in headersElement.EnumerateObject())
                {
                    if (h.Value.ValueKind != JsonValueKind.String)
                        throw new InvalidDataException($"{where}: site '{label}': header \"{h.Name}\" must have a string value.");
                    headers.Add(new(h.Name, expand(h.Value.GetString()!, "headers." + h.Name)));
                }
            }

            var spec = new GatewayRouteSpec(
                prefix?.Trim() ?? "/" + name!.ToLowerInvariant(),
                new Uri(upstream.GetLeftPart(UriPartial.Authority)),
                Str("cert"), Path_(Str("certFile")), Str("certPassword"), Path_(Str("keyFile")))
            {
                Name = name,
                Insecure = BoolOf(item, "insecure", where, label),
                CaFiles = caFiles,
                Revocation = Str("revocation"),
                WindowsAuth = BoolOf(item, "windowsAuth", where, label),
                WindowsUser = Str("windowsUser"),
                WindowsPassword = Str("windowsPassword"),
                Headers = headers,
            };
            if (spec.Cert is not null && spec.CertFile is not null)
                throw new InvalidDataException($"{where}: site '{label}' names both \"cert\" and \"certFile\"; choose one.");
            if (spec.CertFile is null && (spec.CertPassword is not null || spec.KeyFile is not null))
                throw new InvalidDataException($"{where}: site '{label}': \"certPassword\" and \"keyFile\" only apply with \"certFile\".");
            if (spec.WindowsPassword is not null && spec.WindowsUser is null)
                throw new InvalidDataException($"{where}: site '{label}': \"windowsPassword\" needs \"windowsUser\".");
            if (spec.WindowsAuth == false && spec.WindowsUser is not null)
                throw new InvalidDataException($"{where}: site '{label}': \"windowsAuth\": false and \"windowsUser\" contradict each other.");
            if (spec.Revocation is not null && spec.Revocation.ToLowerInvariant() is not ("none" or "offline" or "online"))
                throw new InvalidDataException($"{where}: site '{label}': \"revocation\" is none, offline or online, got '{spec.Revocation}'.");
            specs.Add(spec);
        }
        return specs;
    }

    private static string? StrOf(JsonElement obj, string name, string where, int index, Func<string, string, string> expand)
    {
        if (!obj.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null) return null;
        if (v.ValueKind != JsonValueKind.String)
            throw new InvalidDataException($"{where}: site #{index}: \"{name}\" must be a string.");
        return expand(v.GetString()!, name);
    }

    private static bool? BoolOf(JsonElement obj, string name, string where, string label)
    {
        if (!obj.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null) return null;
        if (v.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new InvalidDataException($"{where}: site '{label}': \"{name}\" must be true or false.");
        return v.GetBoolean();
    }

    /// <summary><c>${env:NAME}</c> in a routes file, so a password or API key can stay out of a
    /// file that is shared. An unset variable is an error naming the field and the variable.</summary>
    private static string ExpandEnvironment(string value, string field, string where, Func<string, string?> lookup)
    {
        if (!value.Contains("${env:", StringComparison.Ordinal)) return value;
        var result = new System.Text.StringBuilder(value.Length);
        int index = 0;
        while (index < value.Length)
        {
            int start = value.IndexOf("${env:", index, StringComparison.Ordinal);
            int end = start < 0 ? -1 : value.IndexOf('}', start);
            if (start < 0 || end < 0) { result.Append(value, index, value.Length - index); break; }
            result.Append(value, index, start - index);
            string name = value[(start + 6)..end].Trim();
            string resolved = (name.Length == 0 ? null : lookup(name))
                ?? throw new InvalidDataException($"{where}: \"{field}\" needs the environment variable '{name}', and it is not set.");
            result.Append(resolved);
            index = end + 1;
        }
        return result.ToString();
    }
}
