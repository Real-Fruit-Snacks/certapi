using System.Text.Json;

namespace ApiTester.Core;

/// <summary>One named set of default option values. Every field is optional: a profile supplies
/// only what it wants to, and a command reads only the fields it understands — so a profile with a
/// proxy in it never breaks `certapi certs`.</summary>
public sealed record ConfigProfile
{
    /// <summary>The profile's name in the file, for messages that point at it.</summary>
    public string? Name { get; init; }

    /// <summary>The gateway this profile runs — `certapi serve --profile <name>`. Null when the
    /// profile has no <c>serve</c> section.</summary>
    public ServeProfile? Serve { get; init; }

    public string? Cert { get; init; }
    public string? Store { get; init; }
    public string? CertFile { get; init; }
    public string? CertPassword { get; init; }
    public string? KeyFile { get; init; }

    public string? Proxy { get; init; }
    public string? ProxyUser { get; init; }
    public bool? NoProxy { get; init; }
    public string? NoProxyList { get; init; }

    public string? Revocation { get; init; }
    public bool? RevocationStrict { get; init; }

    public int? Retry { get; init; }
    public int? Timeout { get; init; }
    public bool? Insecure { get; init; }

    /// <summary>Extra root certificates (PEM/DER paths, relative to the config file) trusted on top
    /// of the machine store — the profile form of <c>--ca-file</c>.</summary>
    public IReadOnlyList<string> CaFiles { get; init; } = Array.Empty<string>();
    /// <summary>"1.2" or "1.3" — the profile form of <c>--tls-min</c>.</summary>
    public string? TlsMin { get; init; }
    /// <summary>Seconds — the profile form of <c>--connect-timeout</c>.</summary>
    public int? ConnectTimeout { get; init; }
    public string? Workspace { get; init; }

    /// <summary>Headers added to every request a command sends, unless the command line sets the
    /// same name — the one place a profile contributes something a flag cannot simply replace.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Headers { get; init; } =
        Array.Empty<KeyValuePair<string, string>>();

    /// <summary>The request's headers with this profile's standing headers added — every profile
    /// header whose name the command line (or the collection entry) did not already set. A name set
    /// by the caller wins outright, so a profile's <c>X-Env: staging</c> never doubles a typed one
    /// and a typed <c>Authorization</c> replaces the profile's rather than joining it.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> WithStandingHeaders(IReadOnlyList<KeyValuePair<string, string>> requestHeaders)
    {
        if (Headers.Count == 0) return requestHeaders;
        var set = new HashSet<string>(requestHeaders.Select(h => h.Key), StringComparer.OrdinalIgnoreCase);
        var merged = new List<KeyValuePair<string, string>>(requestHeaders);
        foreach (var h in Headers)
            if (set.Add(h.Key)) merged.Add(h);
        return merged;
    }

    /// <summary>The header names whose values were written as <c>${env:…}</c> references, so
    /// `config show` can report them as set without printing what they resolved to.</summary>
    public IReadOnlySet<string> SecretHeaderNames { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>A profile's <c>serve</c> section: the gateway's own settings and its sites. Every
/// field is what the matching `serve` flag would say; a flag typed on the command line wins.
/// Certificate, proxy, CA files, revocation and the rest come from the profile itself, exactly as
/// they do for every other command.</summary>
public sealed record ServeProfile
{
    public int? Port { get; init; }
    public bool? Vhost { get; init; }
    public string? VhostSuffix { get; init; }
    public bool? Browser { get; init; }
    public bool? Cors { get; init; }
    public IReadOnlyList<string> CorsOrigins { get; init; } = Array.Empty<string>();
    public string? Token { get; init; }
    public IReadOnlyList<string> Allow { get; init; } = Array.Empty<string>();
    public string? Listen { get; init; }
    public bool? CookieJar { get; init; }
    public string? CookieJarFile { get; init; }
    public bool? AutoToken { get; init; }
    public bool? WindowsAuth { get; init; }
    public string? WindowsUser { get; init; }
    public string? WindowsPassword { get; init; }
    public bool? Forwarded { get; init; }
    public string? MaxBody { get; init; }
    public int? Timeout { get; init; }
    public int? MaxConnections { get; init; }
    public int? MaxRequests { get; init; }
    public bool? Tls { get; init; }
    public bool? RewriteBody { get; init; }
    public IReadOnlyList<GatewayRouteSpec> Sites { get; init; } = Array.Empty<GatewayRouteSpec>();

    public static readonly string[] Fields =
    {
        "port", "vhost", "browser", "cors", "token", "allow", "listen", "cookieJar", "autoToken",
        "windowsAuth", "windowsUser", "windowsPassword", "forwarded", "maxBody", "timeout",
        "maxConnections", "maxRequests", "tls", "rewriteBody", "sites",
    };
}

/// <summary>Where a configuration file was found, and by which rule — reported by
/// `certapi config path` so a surprising default can be traced to the file that set it.</summary>
public sealed record ConfigSource(string Path, string Rule);

/// <summary>A parsed configuration file: named profiles plus the one used when none is asked for.</summary>
public sealed record CertapiConfig(
    string? DefaultProfile,
    IReadOnlyDictionary<string, ConfigProfile> Profiles,
    ConfigSource? Source)
{
    public static CertapiConfig Empty { get; } =
        new(null, new Dictionary<string, ConfigProfile>(StringComparer.OrdinalIgnoreCase), null);

    /// <summary>The profile a command should use: the one named, else the file's default, else
    /// none. Throws <see cref="ConfigException"/> when a name is asked for and is not there —
    /// silently running with no profile would be worse than stopping, because the whole point is
    /// that the profile carries the identity.</summary>
    public ConfigProfile? Resolve(string? requested)
    {
        string? name = requested ?? DefaultProfile;
        if (name is null) return null;
        if (Profiles.TryGetValue(name, out var profile)) return profile;

        string known = Profiles.Count == 0 ? "(none)" : string.Join(", ", Profiles.Keys);
        throw new ConfigException(
            requested is null
                ? $"the configuration file names '{name}' as its default profile, but no such profile is defined. Profiles: {known}."
                : $"no profile named '{name}'. Profiles: {known}.");
    }
}

/// <summary>A configuration file that cannot be used as written — bad JSON, an unknown profile, or
/// an unresolvable <c>${env:…}</c>. Always names the file and what is wrong with it.</summary>
public sealed class ConfigException(string message) : Exception(message);

/// <summary>Finds, reads, and expands the configuration file. Pure with respect to the machine:
/// discovery and the environment are both injectable, so every rule is testable without touching
/// the real filesystem or the real environment.</summary>
public static class ConfigLoader
{
    public const string FileName = "certapi.config.json";
    public const string EnvironmentVariable = "CERTAPI_CONFIG";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        // These files are edited by people: a trailing comma and a // note are ordinary, and
        // refusing them would be pedantry rather than safety.
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    /// <summary>The file to read, by the documented precedence: an explicit path, then
    /// <c>CERTAPI_CONFIG</c>, then <c>certapi.config.json</c> found by walking up from
    /// <paramref name="startDirectory"/>, then the per-user file. Null when nothing is found.</summary>
    public static ConfigSource? Discover(
        string? explicitPath,
        string startDirectory,
        string? userConfigPath,
        Func<string, string?>? environment = null,
        Func<string, bool>? fileExists = null)
    {
        var exists = fileExists ?? File.Exists;
        var env = environment ?? Environment.GetEnvironmentVariable;

        if (explicitPath is { Length: > 0 })
        {
            // An explicit path that is not there is an error, not a silent fall-through to a
            // different file: the user named this one.
            if (!exists(explicitPath)) throw new ConfigException($"configuration file not found: {explicitPath}");
            return new ConfigSource(explicitPath, "--config");
        }

        if (env(EnvironmentVariable) is { Length: > 0 } fromEnv)
        {
            if (!exists(fromEnv)) throw new ConfigException($"{EnvironmentVariable} names a file that does not exist: {fromEnv}");
            return new ConfigSource(fromEnv, EnvironmentVariable);
        }

        // Walking up finds the project's own file from anywhere inside it, which is what makes a
        // per-repository configuration work without every command being run from the root.
        var directory = startDirectory;
        while (!string.IsNullOrEmpty(directory))
        {
            string candidate = Path.Combine(directory, FileName);
            if (exists(candidate)) return new ConfigSource(candidate, FileName + " (found by walking up)");
            var parent = Path.GetDirectoryName(directory);
            if (parent == directory) break;
            directory = parent ?? "";
        }

        if (userConfigPath is { Length: > 0 } && exists(userConfigPath))
            return new ConfigSource(userConfigPath, "per-user configuration");

        return null;
    }

    /// <summary>The conventional per-user location, or null on a platform without one.</summary>
    public static string? DefaultUserConfigPath()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return string.IsNullOrEmpty(appData) ? null : Path.Combine(appData, "certapi", "config.json");
    }

    /// <summary>Parse a configuration document. <paramref name="environment"/> resolves
    /// <c>${env:NAME}</c> inside values — injectable so a test never mutates the process's own.</summary>
    public static CertapiConfig Parse(string json, ConfigSource? source = null, Func<string, string?>? environment = null)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        }); }
        catch (JsonException ex)
        {
            throw new ConfigException($"{source?.Path ?? "the configuration"} is not valid JSON: {ex.Message}");
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new ConfigException($"{source?.Path ?? "the configuration"} must contain a JSON object.");

            string? defaultProfile = root.TryGetProperty("defaultProfile", out var dp) && dp.ValueKind == JsonValueKind.String
                ? dp.GetString() : null;

            // Each profile is read on first use, not up front: an unset ${env:…} in profile "prod"
            // must not stop `--profile dev` from running. The dictionary is populated with lazy
            // readers and the resolution step unwraps the one that was asked for.
            var deferred = new Dictionary<string, Func<ConfigProfile>>(StringComparer.OrdinalIgnoreCase);
            if (root.TryGetProperty("profiles", out var profilesElement) &&
                profilesElement.ValueKind == JsonValueKind.Object)
                foreach (var property in profilesElement.EnumerateObject())
                    if (property.Value.ValueKind == JsonValueKind.Object)
                    {
                        var elementCopy = property.Value.Clone();
                        string name = property.Name;
                        deferred[name] = () => ReadProfile(elementCopy, name, source, environment);
                    }

            return new CertapiConfig(defaultProfile, new LazyProfiles(deferred), source);
        }
    }

    private static ConfigProfile ReadProfile(
        JsonElement element, string profileName, ConfigSource? source, Func<string, string?>? environment)
    {
        // A field of the wrong JSON type is an error naming it, not silently ignored: "timeout": "60"
        // used to leave the default in force with nothing said, which is a profile that lies.
        string where = $"profile '{profileName}' in {source?.Path ?? "the configuration"}";
        bool Present(string name, out JsonElement v) =>
            element.TryGetProperty(name, out v) && v.ValueKind != JsonValueKind.Null;

        string? Str(string name)
        {
            if (!Present(name, out var v)) return null;
            if (v.ValueKind != JsonValueKind.String) throw new ConfigException($"{where}: \"{name}\" must be a string.");
            return Expand(v.GetString(), name, profileName, source, environment);
        }

        bool? Bool(string name)
        {
            if (!Present(name, out var v)) return null;
            if (v.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new ConfigException($"{where}: \"{name}\" must be true or false (without quotes).");
            return v.GetBoolean();
        }

        int? Int(string name)
        {
            if (!Present(name, out var v)) return null;
            if (v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out var n))
                throw new ConfigException($"{where}: \"{name}\" must be a whole number (without quotes).");
            return n;
        }

        // Accepts one path or a list; relative paths are resolved against the configuration file,
        // which is where a committed PEM bundle naturally lives.
        var caFiles = new List<string>();
        if (element.TryGetProperty("caFile", out var caElement))
        {
            IEnumerable<JsonElement> items = caElement.ValueKind == JsonValueKind.Array
                ? caElement.EnumerateArray() : new[] { caElement };
            foreach (var item in items)
                if (item.ValueKind == JsonValueKind.String &&
                    Expand(item.GetString(), "caFile", profileName, source, environment) is { Length: > 0 } raw)
                    caFiles.Add(source is null || Path.IsPathRooted(raw)
                        ? raw
                        : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source.Path) ?? ".", raw)));
        }

        var headers = new List<KeyValuePair<string, string>>();
        var secretHeaders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (element.TryGetProperty("headers", out var headersElement) &&
            headersElement.ValueKind == JsonValueKind.Object)
            foreach (var header in headersElement.EnumerateObject())
                if (header.Value.ValueKind == JsonValueKind.String)
                {
                    string? raw = header.Value.GetString();
                    if (HasEnvironmentReference(raw)) secretHeaders.Add(header.Name);
                    headers.Add(new(header.Name,
                        Expand(raw, "headers." + header.Name, profileName, source, environment) ?? ""));
                }

        ServeProfile? serve = null;
        if (element.TryGetProperty("serve", out var serveElement))
        {
            if (serveElement.ValueKind != JsonValueKind.Object)
                throw new ConfigException($"profile '{profileName}' in {source?.Path ?? "the configuration"}: \"serve\" must be an object.");
            serve = ReadServe(serveElement, profileName, source, environment);
        }

        return new ConfigProfile
        {
            Name = profileName,
            Serve = serve,
            Cert = Str("cert"),
            Store = Str("store"),
            CertFile = Str("certFile"),
            CertPassword = Str("certPassword"),
            KeyFile = Str("keyFile"),
            Proxy = Str("proxy"),
            ProxyUser = Str("proxyUser"),
            NoProxy = Bool("noProxy"),
            NoProxyList = Str("noProxyList"),
            Revocation = Str("revocation"),
            RevocationStrict = Bool("revocationStrict"),
            Retry = Int("retry"),
            Timeout = Int("timeout"),
            Insecure = Bool("insecure"),
            CaFiles = caFiles,
            TlsMin = Str("tlsMin"),
            ConnectTimeout = Int("connectTimeout"),
            Workspace = Str("workspace"),
            Headers = headers,
            SecretHeaderNames = secretHeaders
        };
    }

    private static ServeProfile ReadServe(
        JsonElement element, string profileName, ConfigSource? source, Func<string, string?>? environment)
    {
        string where = $"profile '{profileName}' in {source?.Path ?? "the configuration"}";
        foreach (var property in element.EnumerateObject())
            if (!ServeProfile.Fields.Contains(property.Name, StringComparer.OrdinalIgnoreCase))
                throw new ConfigException($"{where}: \"serve\" has an unknown field \"{property.Name}\" (known: {string.Join(", ", ServeProfile.Fields)}).");

        JsonElement? Get(string name) =>
            element.EnumerateObject().FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is { Value.ValueKind: not JsonValueKind.Undefined } p
                ? p.Value : null;
        string? Str(string name) => Get(name) is { ValueKind: JsonValueKind.String } v
            ? Expand(v.GetString(), "serve." + name, profileName, source, environment) : null;
        bool? Bool(string name) => Get(name) is { ValueKind: JsonValueKind.True or JsonValueKind.False } v ? v.GetBoolean() : null;
        int? Int(string name) => Get(name) is { ValueKind: JsonValueKind.Number } v && v.TryGetInt32(out var n) ? n : null;
        // true, a string, or a list of strings — the shapes a flag with an optional list value takes.
        (bool? on, List<string> values) Switch(string name)
        {
            var values = new List<string>();
            switch (Get(name))
            {
                case null: return (null, values);
                case { ValueKind: JsonValueKind.True or JsonValueKind.False } b: return (b.GetBoolean(), values);
                case { ValueKind: JsonValueKind.String } s:
                    values.Add(Expand(s.GetString(), "serve." + name, profileName, source, environment)!);
                    return (true, values);
                case { ValueKind: JsonValueKind.Array } a:
                    foreach (var item in a.EnumerateArray())
                        if (item.ValueKind == JsonValueKind.String) values.Add(Expand(item.GetString(), "serve." + name, profileName, source, environment)!);
                        else throw new ConfigException($"{where}: \"serve.{name}\" must be true, a string, or a list of strings.");
                    return (true, values);
                default: throw new ConfigException($"{where}: \"serve.{name}\" must be true, a string, or a list of strings.");
            }
        }

        var (vhostOn, vhostValues) = Switch("vhost");
        var (corsOn, corsValues) = Switch("cors");
        var (_, allow) = Switch("allow");
        var (jarOn, jarValues) = Switch("cookieJar");
        string? baseDirectory = source is null ? null : Path.GetDirectoryName(Path.GetFullPath(source.Path));

        var sites = new List<GatewayRouteSpec>();
        if (Get("sites") is { } sitesElement)
        {
            try
            {
                sites = GatewayRoutesFile.ParseSites(sitesElement, $"{where}: serve.sites", baseDirectory,
                    (value, field) => Expand(value, "serve.sites." + field, profileName, source, environment)!);
            }
            catch (InvalidDataException ex) { throw new ConfigException(ex.Message); }
        }

        string? jarFile = jarValues.Count > 0 ? jarValues[0] : null;
        if (jarFile is not null && baseDirectory is not null && !Path.IsPathRooted(jarFile))
            jarFile = Path.GetFullPath(Path.Combine(baseDirectory, jarFile));

        return new ServeProfile
        {
            Port = Int("port"),
            Vhost = vhostOn,
            VhostSuffix = vhostValues.Count > 0 ? vhostValues[0] : null,
            Browser = Bool("browser"),
            Cors = corsOn,
            CorsOrigins = corsValues,
            Token = Str("token"),
            Allow = allow,
            Listen = Str("listen"),
            CookieJar = jarOn,
            CookieJarFile = jarFile,
            AutoToken = Bool("autoToken"),
            WindowsAuth = Bool("windowsAuth"),
            WindowsUser = Str("windowsUser"),
            WindowsPassword = Str("windowsPassword"),
            Forwarded = Bool("forwarded"),
            RewriteBody = Bool("rewriteBody"),
            MaxBody = Get("maxBody") is { ValueKind: JsonValueKind.Number } mb ? mb.GetRawText() : Str("maxBody"),
            Timeout = Int("timeout"),
            MaxConnections = Int("maxConnections"),
            MaxRequests = Int("maxRequests"),
            Tls = Bool("tls"),
            Sites = sites,
        };
    }

    /// <summary>Expand <c>${env:NAME}</c> references so a secret can live in the environment rather
    /// than in a file that gets committed. A reference to a variable that is not set is an error
    /// naming both the field and the variable — silently substituting nothing would send an empty
    /// credential, which fails later and further away.</summary>
    internal static string? Expand(
        string? value, string field, string profileName, ConfigSource? source, Func<string, string?>? environment)
    {
        if (value is null || !value.Contains("${env:", StringComparison.Ordinal)) return value;
        var lookup = environment ?? Environment.GetEnvironmentVariable;

        var result = new System.Text.StringBuilder(value.Length);
        int index = 0;
        while (index < value.Length)
        {
            int start = value.IndexOf("${env:", index, StringComparison.Ordinal);
            if (start < 0) { result.Append(value, index, value.Length - index); break; }
            int end = value.IndexOf('}', start);
            if (end < 0) { result.Append(value, index, value.Length - index); break; }

            result.Append(value, index, start - index);
            string name = value[(start + 6)..end].Trim();
            string? resolved = name.Length == 0 ? null : lookup(name);
            if (resolved is null)
                throw new ConfigException(
                    $"profile '{profileName}' in {source?.Path ?? "the configuration"} needs the environment variable " +
                    $"'{name}' for '{field}', and it is not set.");
            result.Append(resolved);
            index = end + 1;
        }
        return result.ToString();
    }

    /// <summary>Whether a value carries an <c>${env:…}</c> reference — used by
    /// `certapi config show` to report a reference as resolved-or-missing without ever printing
    /// the secret it resolves to.</summary>
    public static bool HasEnvironmentReference(string? value) =>
        value is not null && value.Contains("${env:", StringComparison.Ordinal);
}

/// <summary>A profile table whose entries are read — and their <c>${env:…}</c> references
/// expanded — on first access rather than when the file is parsed. The names are known up front
/// (so `config profiles` lists them all without touching the environment), and an unset variable
/// in one profile is an error only when that profile is asked for. Each profile is read once.</summary>
internal sealed class LazyProfiles : IReadOnlyDictionary<string, ConfigProfile>
{
    private readonly Dictionary<string, Func<ConfigProfile>> _readers;
    private readonly Dictionary<string, ConfigProfile> _read = new(StringComparer.OrdinalIgnoreCase);

    public LazyProfiles(Dictionary<string, Func<ConfigProfile>> readers) => _readers = readers;

    public ConfigProfile this[string key] =>
        TryGetValue(key, out var p) ? p : throw new KeyNotFoundException(key);

    public IEnumerable<string> Keys => _readers.Keys;
    public IEnumerable<ConfigProfile> Values => _readers.Keys.Select(k => this[k]);
    public int Count => _readers.Count;
    public bool ContainsKey(string key) => _readers.ContainsKey(key);

    public bool TryGetValue(string key, out ConfigProfile value)
    {
        if (_read.TryGetValue(key, out value!)) return true;
        if (!_readers.TryGetValue(key, out var read)) { value = null!; return false; }
        value = read();
        _read[key] = value;
        return true;
    }

    public IEnumerator<KeyValuePair<string, ConfigProfile>> GetEnumerator() =>
        _readers.Keys.Select(k => new KeyValuePair<string, ConfigProfile>(k, this[k])).GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}
