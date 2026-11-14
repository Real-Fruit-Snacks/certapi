using System.Globalization;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace ApiTester.Core;

/// <summary>Which HMAC scheme signs a request.</summary>
public enum HmacScheme
{
    /// <summary>A string to sign built from a template, signed, and put in a header of your choosing.</summary>
    Template,
    /// <summary>HTTP Message Signatures, RFC 9421, with <c>hmac-sha256</c>.</summary>
    Rfc9421,
    /// <summary>The older HTTP Signatures draft (draft-cavage-http-signatures-12) many APIs still use.</summary>
    Cavage
}

/// <summary>Everything about an HMAC signature except the secret and key id, which a saved request
/// keeps in its encrypted secret and its user field. Stored as JSON in the request's auth scope.</summary>
public sealed record HmacSettings
{
    public HmacScheme Scheme { get; init; } = HmacScheme.Template;
    /// <summary>sha256 (default), sha384, sha512 or sha1. RFC 9421 defines hmac-sha256 only.</summary>
    public string Algorithm { get; init; } = "sha256";
    /// <summary>How the secret is written: utf8 (default), base64 or hex.</summary>
    public string SecretEncoding { get; init; } = "utf8";

    // Template scheme
    /// <summary>The string to sign, with placeholders such as {method}, {path}, {timestamp}, {bodySha256}.</summary>
    public string? StringToSign { get; init; }
    /// <summary>"Name: value" for the signature header; the value may use {signature}, {keyId}, {timestamp}, {nonce}.</summary>
    public string Header { get; init; } = "X-Signature: {signature}";
    /// <summary>hex (default), base64 or base64url.</summary>
    public string Encoding { get; init; } = "hex";
    /// <summary>More headers to send, "Name: value template" each, filled before the string to sign
    /// is built so it can refer to them ({header:X-Timestamp}).</summary>
    public List<string> AlsoSend { get; init; } = new();

    // RFC 9421 and draft-cavage
    /// <summary>What the signature covers, space-separated; empty for the default set.</summary>
    public string? Components { get; init; }
    /// <summary>RFC 9421's signature label.</summary>
    public string Label { get; init; } = "sig1";

    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    public string ToScope() => JsonSerializer.Serialize(this, Json);

    public static HmacSettings FromScope(string? scope)
    {
        if (string.IsNullOrWhiteSpace(scope)) return new HmacSettings();
        try { return JsonSerializer.Deserialize<HmacSettings>(scope, Json) ?? new HmacSettings(); }
        catch (JsonException) { return new HmacSettings(); }
    }
}

/// <summary>An HMAC signature for one request: the settings plus the key.</summary>
public sealed record HmacOptions(HmacSettings Settings, string Secret, string? KeyId = null)
{
    /// <summary>What is wrong with these options before anything is sent, or null.</summary>
    public string? Problem()
    {
        var s = Settings;
        if (string.IsNullOrEmpty(Secret)) return "HMAC signing needs a secret.";
        try { _ = HmacSigner.KeyBytes(Secret, s.SecretEncoding); }
        catch (FormatException ex) { return ex.Message; }
        if (HmacSigner.Hash(s.Algorithm) is null) return $"HMAC algorithm '{s.Algorithm}' is not one of sha256, sha384, sha512, sha1.";
        switch (s.Scheme)
        {
            case HmacScheme.Template:
                if (string.IsNullOrEmpty(s.StringToSign)) return "Template HMAC needs a string to sign, e.g. \"{method}\\n{path}\\n{timestamp}\\n{bodySha256}\".";
                if (s.Encoding is not ("hex" or "base64" or "base64url")) return $"HMAC encoding '{s.Encoding}' is not one of hex, base64, base64url.";
                if (HmacSigner.SplitHeader(s.Header) is null) return $"The signature header must be \"Name: value\", got '{s.Header}'.";
                foreach (var h in s.AlsoSend)
                    if (HmacSigner.SplitHeader(h) is null) return $"An extra header must be \"Name: value\", got '{h}'.";
                return HmacSigner.UnknownPlaceholder(s.StringToSign, allowSignature: false)
                    ?? HmacSigner.UnknownPlaceholder(s.Header, allowSignature: true)
                    ?? s.AlsoSend.Select(h => HmacSigner.UnknownPlaceholder(h, allowSignature: false)).FirstOrDefault(p => p is not null);
            case HmacScheme.Rfc9421:
                if (!s.Algorithm.Equals("sha256", StringComparison.OrdinalIgnoreCase))
                    return "RFC 9421 defines HMAC with SHA-256 only (hmac-sha256).";
                if (string.IsNullOrWhiteSpace(KeyId)) return "HTTP Message Signatures need a key id.";
                if (!Regex.IsMatch(s.Label, "^[a-z*][a-z0-9_\\-.*]*$")) return $"'{s.Label}' is not a valid signature label.";
                return HmacSigner.ComponentProblem(s.Components, rfc9421: true);
            case HmacScheme.Cavage:
                if (string.IsNullOrWhiteSpace(KeyId)) return "HTTP Signatures need a key id.";
                return HmacSigner.ComponentProblem(s.Components, rfc9421: false);
            default:
                return "Unknown HMAC scheme.";
        }
    }
}

/// <summary>Signs a request with an HMAC: a template you describe, RFC 9421 HTTP Message
/// Signatures, or the draft-cavage HTTP Signatures many APIs adopted before the RFC.</summary>
public static partial class HmacSigner
{
    private static readonly Regex Placeholder = PlaceholderRegex();
    [GeneratedRegex(@"\{([A-Za-z][A-Za-z0-9]*)(?::([^}]+))?\}", RegexOptions.CultureInvariant)]
    private static partial Regex PlaceholderRegex();

    private static readonly HashSet<string> Known = new(StringComparer.Ordinal)
    {
        "method", "path", "query", "sortedQuery", "pathAndQuery", "url", "host", "scheme",
        "timestamp", "timestampMs", "isoTimestamp", "httpDate", "nonce", "uuid",
        "body", "bodySha256", "bodySha256Base64", "bodySha512", "bodyMd5", "bodyMd5Base64",
        "contentType", "contentLength", "keyId", "header"
    };

    /// <summary>The placeholder names a template may use, for help and errors.</summary>
    public static IEnumerable<string> PlaceholderNames => Known.Where(k => k != "header").Append("header:<Name>");

    internal static string? UnknownPlaceholder(string template, bool allowSignature)
    {
        foreach (Match m in Placeholder.Matches(template))
        {
            string name = m.Groups[1].Value;
            if (name == "signature" && allowSignature) continue;
            if (!Known.Contains(name) || (name == "header") != m.Groups[2].Success)
                return $"Unknown placeholder '{m.Value}'. Known: {string.Join(", ", PlaceholderNames.Select(p => "{" + p + "}"))}" +
                       (allowSignature ? ", {signature}." : ".");
        }
        return null;
    }

    internal static (string Name, string Value)? SplitHeader(string spec)
    {
        int colon = spec.IndexOf(':');
        if (colon <= 0) return null;
        string name = spec[..colon].Trim();
        if (name.Length == 0 || name.Any(c => c <= ' ' || c is '(' or ')' or ',' or ';' or '"' or ':')) return null;
        return (name, spec[(colon + 1)..].Trim());
    }

    internal static Func<byte[], byte[], byte[]>? Hash(string algorithm) => algorithm.ToLowerInvariant() switch
    {
        "sha256" => HMACSHA256.HashData,
        "sha384" => HMACSHA384.HashData,
        "sha512" => HMACSHA512.HashData,
        "sha1" => HMACSHA1.HashData,
        _ => null
    };

    internal static byte[] KeyBytes(string secret, string encoding) => encoding.ToLowerInvariant() switch
    {
        "utf8" or "" => Encoding.UTF8.GetBytes(secret),
        "base64" => Base64(secret),
        "hex" => Hex(secret),
        _ => throw new FormatException($"Secret encoding '{encoding}' is not one of utf8, base64, hex.")
    };

    private static byte[] Base64(string s)
    {
        try { return Convert.FromBase64String(s.Trim().Replace('-', '+').Replace('_', '/').PadRight((s.Trim().Length + 3) / 4 * 4, '=')); }
        catch (FormatException) { throw new FormatException("The HMAC secret is not valid base64."); }
    }

    private static byte[] Hex(string s)
    {
        try { return Convert.FromHexString(s.Trim()); }
        catch (FormatException) { throw new FormatException("The HMAC secret is not valid hex."); }
    }

    /// <summary>The request as signing sees it.</summary>
    public sealed record SignInput(string Method, Uri Uri, IReadOnlyList<KeyValuePair<string, string>> Headers, byte[] Body);

    /// <summary>Add the signature (and any headers it needs) to a message ready to send. The body
    /// is buffered and read so the signature covers exactly what is sent.</summary>
    public static async Task SignAsync(HttpRequestMessage message, HmacOptions options, DateTimeOffset now, CancellationToken ct = default)
    {
        byte[] body = Array.Empty<byte>();
        if (message.Content is not null)
        {
            await message.Content.LoadIntoBufferAsync(ct);
            body = await message.Content.ReadAsByteArrayAsync(ct);
        }
        var headers = new List<KeyValuePair<string, string>>();
        foreach (var h in message.Headers) headers.Add(new(h.Key, string.Join(", ", h.Value)));
        if (message.Content is not null)
            foreach (var h in message.Content.Headers) headers.Add(new(h.Key, string.Join(", ", h.Value)));

        foreach (var (name, value) in Sign(new SignInput(message.Method.Method, message.RequestUri!, headers, body), options, now, Nonce()))
        {
            message.Headers.Remove(name);
            if (!message.Headers.TryAddWithoutValidation(name, value) && message.Content is not null)
            {
                message.Content.Headers.Remove(name);
                message.Content.Headers.TryAddWithoutValidation(name, value);
            }
        }
    }

    private static string Nonce() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

    /// <summary>The headers to set, in order. Pure given the clock and nonce, for tests.</summary>
    public static IReadOnlyList<(string Name, string Value)> Sign(SignInput input, HmacOptions options, DateTimeOffset now, string nonce)
    {
        if (options.Problem() is { } problem) throw new HmacSigningException(problem);
        return options.Settings.Scheme switch
        {
            HmacScheme.Template => SignTemplate(input, options, now, nonce),
            HmacScheme.Rfc9421 => SignRfc9421(input, options, now),
            _ => SignCavage(input, options, now)
        };
    }

    // ------------------------------------------------------------------ template

    private static IReadOnlyList<(string, string)> SignTemplate(SignInput input, HmacOptions options, DateTimeOffset now, string nonce)
    {
        var s = options.Settings;
        var added = new List<(string, string)>();
        var headers = input.Headers.ToList();
        var ctx = new TemplateContext(input, options, now, nonce, headers);

        // Extra headers first: the string to sign may refer to them.
        foreach (var spec in s.AlsoSend)
        {
            var (name, valueTemplate) = SplitHeader(spec)!.Value;
            string value = ctx.Fill(Unescape(valueTemplate), null);
            headers.RemoveAll(h => h.Key.Equals(name, StringComparison.OrdinalIgnoreCase));
            headers.Add(new(name, value));
            added.Add((name, value));
        }

        string toSign = ctx.Fill(Unescape(s.StringToSign!), null);
        byte[] mac = Hash(s.Algorithm)!(KeyBytes(options.Secret, s.SecretEncoding), Encoding.UTF8.GetBytes(toSign));
        string signature = s.Encoding switch
        {
            "base64" => Convert.ToBase64String(mac),
            "base64url" => Convert.ToBase64String(mac).TrimEnd('=').Replace('+', '-').Replace('/', '_'),
            _ => Convert.ToHexStringLower(mac)
        };
        var (headerName, headerTemplate) = SplitHeader(s.Header)!.Value;
        added.Add((headerName, ctx.Fill(headerTemplate, signature)));
        return added;
    }

    /// <summary>"\n", "\r", "\t" and "\\" in a template, which a command line cannot type as themselves.</summary>
    internal static string Unescape(string template)
    {
        var sb = new StringBuilder(template.Length);
        for (int i = 0; i < template.Length; i++)
        {
            if (template[i] == '\\' && i + 1 < template.Length)
            {
                char next = template[i + 1];
                string? mapped = next switch { 'n' => "\n", 'r' => "\r", 't' => "\t", '\\' => "\\", _ => null };
                if (mapped is not null) { sb.Append(mapped); i++; continue; }
            }
            sb.Append(template[i]);
        }
        return sb.ToString();
    }

    private sealed class TemplateContext(SignInput input, HmacOptions options, DateTimeOffset now, string nonce, List<KeyValuePair<string, string>> headers)
    {
        private readonly string _uuid = Guid.NewGuid().ToString();

        public string Fill(string template, string? signature) => Placeholder.Replace(template, m =>
        {
            string name = m.Groups[1].Value;
            var uri = input.Uri;
            return name switch
            {
                "signature" => signature ?? m.Value,
                "method" => input.Method.ToUpperInvariant(),
                "path" => uri.AbsolutePath,
                "query" => uri.Query.TrimStart('?'),
                "sortedQuery" => string.Join("&", uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries).OrderBy(p => p, StringComparer.Ordinal)),
                "pathAndQuery" => uri.PathAndQuery,
                "url" => uri.AbsoluteUri,
                "host" => Authority(uri),
                "scheme" => uri.Scheme,
                "timestamp" => now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
                "timestampMs" => now.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture),
                "isoTimestamp" => now.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
                "httpDate" => now.UtcDateTime.ToString("r", CultureInfo.InvariantCulture),
                "nonce" => nonce,
                "uuid" => _uuid,
                "body" => Encoding.UTF8.GetString(input.Body),
                "bodySha256" => Convert.ToHexStringLower(SHA256.HashData(input.Body)),
                "bodySha256Base64" => Convert.ToBase64String(SHA256.HashData(input.Body)),
                "bodySha512" => Convert.ToHexStringLower(SHA512.HashData(input.Body)),
                "bodyMd5" => Convert.ToHexStringLower(MD5.HashData(input.Body)),
                "bodyMd5Base64" => Convert.ToBase64String(MD5.HashData(input.Body)),
                "contentType" => HeaderValue(headers, "Content-Type") ?? "",
                "contentLength" => input.Body.Length.ToString(CultureInfo.InvariantCulture),
                "keyId" => options.KeyId ?? "",
                "header" => HeaderValue(headers, m.Groups[2].Value) ?? "",
                _ => m.Value
            };
        });
    }

    private static string? HeaderValue(IEnumerable<KeyValuePair<string, string>> headers, string name)
    {
        var values = headers.Where(h => h.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Select(h => h.Value.Trim()).ToList();
        return values.Count == 0 ? null : string.Join(", ", values);
    }

    private static string Authority(Uri uri) => (uri.IsDefaultPort ? uri.IdnHost : $"{uri.IdnHost}:{uri.Port}").ToLowerInvariant();

    // ------------------------------------------------------------------ RFC 9421

    private static readonly HashSet<string> DerivedRfc = new(StringComparer.Ordinal)
        { "@method", "@target-uri", "@authority", "@scheme", "@request-target", "@path", "@query" };

    internal static string? ComponentProblem(string? components, bool rfc9421)
    {
        if (string.IsNullOrWhiteSpace(components)) return null;
        foreach (var c in components.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (rfc9421 && c.StartsWith('@') && !DerivedRfc.Contains(c))
                return $"'{c}' is not a derived component this signs (known: {string.Join(" ", DerivedRfc)}).";
            if (!rfc9421 && c.StartsWith('(') && c != "(request-target)")
                return $"'{c}' is not supported; use (request-target) and header names.";
            if (!c.StartsWith('@') && !c.StartsWith('(') && c.Any(ch => ch <= ' ' || ch is '"' or ':' or '(' or ')'))
                return $"'{c}' is not a header name.";
        }
        return null;
    }

    private static IReadOnlyList<(string, string)> SignRfc9421(SignInput input, HmacOptions options, DateTimeOffset now)
    {
        var s = options.Settings;
        var headers = input.Headers.ToList();
        var added = new List<(string, string)>();
        bool hasBody = input.Body.Length > 0;

        var covered = string.IsNullOrWhiteSpace(s.Components)
            ? DefaultRfc9421(input, headers, hasBody)
            : s.Components.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(c => c.ToLowerInvariant()).ToList();

        // A covered Content-Digest (RFC 9530) that the request does not carry yet is made here.
        if (covered.Contains("content-digest") && HeaderValue(headers, "Content-Digest") is null)
        {
            string digest = "sha-256=:" + Convert.ToBase64String(SHA256.HashData(input.Body)) + ":";
            headers.Add(new("Content-Digest", digest));
            added.Add(("Content-Digest", digest));
        }

        long created = now.ToUnixTimeSeconds();
        string parameters = "(" + string.Join(" ", covered.Select(c => "\"" + c + "\"")) + ")" +
                            $";created={created.ToString(CultureInfo.InvariantCulture)};keyid=\"{options.KeyId}\";alg=\"hmac-sha256\"";
        string signatureBase = SignatureBase(input, headers, covered, parameters);
        byte[] mac = HMACSHA256.HashData(KeyBytes(options.Secret, s.SecretEncoding), Encoding.UTF8.GetBytes(signatureBase));
        added.Add(("Signature-Input", $"{s.Label}={parameters}"));
        added.Add(("Signature", $"{s.Label}=:{Convert.ToBase64String(mac)}:"));
        return added;
    }

    private static List<string> DefaultRfc9421(SignInput input, List<KeyValuePair<string, string>> headers, bool hasBody)
    {
        var list = new List<string> { "@method", "@authority", "@path" };
        if (input.Uri.Query.Length > 1) list.Add("@query");
        if (hasBody)
        {
            if (HeaderValue(headers, "Content-Type") is not null) list.Add("content-type");
            list.Add("content-digest");
        }
        return list;
    }

    /// <summary>RFC 9421 §2.5: one line per covered component, then the signature parameters.
    /// Internal so a test can hold it against the RFC's own examples.</summary>
    internal static string SignatureBase(SignInput input, IReadOnlyList<KeyValuePair<string, string>> headers,
        IReadOnlyList<string> covered, string parameters)
    {
        var sb = new StringBuilder();
        var uri = input.Uri;
        foreach (var c in covered)
        {
            string value = c switch
            {
                "@method" => input.Method.ToUpperInvariant(),
                "@target-uri" => uri.AbsoluteUri,
                "@authority" => Authority(uri),
                "@scheme" => uri.Scheme.ToLowerInvariant(),
                "@request-target" => uri.PathAndQuery,
                "@path" => uri.AbsolutePath.Length == 0 ? "/" : uri.AbsolutePath,
                "@query" => uri.Query.Length == 0 ? "?" : uri.Query,
                _ => (c == "host" ? Authority(uri) : HeaderValue(headers, c))
                     ?? throw new HmacSigningException($"The signature covers '{c}', but the request has no such header.")
            };
            sb.Append('"').Append(c).Append("\": ").Append(value).Append('\n');
        }
        sb.Append("\"@signature-params\": ").Append(parameters);
        return sb.ToString();
    }

    /// <summary>The RFC's own B.2.5 shape: a signature with exactly these components and parameters.</summary>
    internal static string Rfc9421Signature(SignInput input, IReadOnlyList<string> covered, string parameters, byte[] key) =>
        Convert.ToBase64String(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(SignatureBase(input, input.Headers, covered, parameters))));

    // ------------------------------------------------------------------ draft-cavage

    private static IReadOnlyList<(string, string)> SignCavage(SignInput input, HmacOptions options, DateTimeOffset now)
    {
        var s = options.Settings;
        var headers = input.Headers.ToList();
        var added = new List<(string, string)>();
        bool hasBody = input.Body.Length > 0;

        var covered = string.IsNullOrWhiteSpace(s.Components)
            ? new List<string> { "(request-target)", "host", "date" }.Concat(hasBody ? new[] { "digest" } : Array.Empty<string>()).ToList()
            : s.Components.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(c => c.ToLowerInvariant()).ToList();

        if (covered.Contains("date") && HeaderValue(headers, "Date") is null)
        {
            string date = now.UtcDateTime.ToString("r", CultureInfo.InvariantCulture);
            headers.Add(new("Date", date));
            added.Add(("Date", date));
        }
        if (covered.Contains("digest") && HeaderValue(headers, "Digest") is null)
        {
            string digest = "SHA-256=" + Convert.ToBase64String(SHA256.HashData(input.Body));
            headers.Add(new("Digest", digest));
            added.Add(("Digest", digest));
        }

        var lines = covered.Select(c => c switch
        {
            "(request-target)" => $"(request-target): {input.Method.ToLowerInvariant()} {input.Uri.PathAndQuery}",
            "host" => "host: " + Authority(input.Uri),
            _ => $"{c}: " + (HeaderValue(headers, c) ?? throw new HmacSigningException($"The signature covers '{c}', but the request has no such header."))
        });
        string signingString = string.Join("\n", lines);
        byte[] mac = Hash(s.Algorithm)!(KeyBytes(options.Secret, s.SecretEncoding), Encoding.UTF8.GetBytes(signingString));
        added.Add(("Signature",
            $"keyId=\"{options.KeyId}\",algorithm=\"hmac-{s.Algorithm.ToLowerInvariant()}\",headers=\"{string.Join(" ", covered)}\",signature=\"{Convert.ToBase64String(mac)}\""));
        return added;
    }
}

/// <summary>A request cannot be signed as configured (a covered header is missing, an option is wrong).</summary>
public sealed class HmacSigningException(string message) : Exception(message);
