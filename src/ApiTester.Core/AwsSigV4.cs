using System.Globalization;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;

namespace ApiTester.Core;

/// <summary>AWS Signature Version 4 credentials and scope. <see cref="SessionToken"/> is for
/// temporary credentials (STS, an assumed role, SSO): sent as <c>X-Amz-Security-Token</c> and signed.</summary>
public sealed record AwsSigV4Options(string AccessKeyId, string SecretAccessKey, string Region, string Service,
    string? SessionToken = null)
{
    /// <summary>Region and service read from an AWS host name when they are not given:
    /// <c>execute-api.eu-west-1.amazonaws.com</c>, <c>abc.execute-api.eu-west-1.amazonaws.com</c>,
    /// <c>sqs.us-east-1.amazonaws.com.cn</c>. A bare <c>service.amazonaws.com</c> is us-east-1.
    /// Null when the host is not one of these shapes.</summary>
    public static (string Region, string Service)? ScopeFromHost(string host)
    {
        var labels = host.ToLowerInvariant().TrimEnd('.').Split('.');
        int aws = Array.IndexOf(labels, "amazonaws");
        if (aws < 1) return null;
        if (aws == 1) return ("us-east-1", labels[0]);
        string candidate = labels[aws - 1];
        bool looksLikeRegion = candidate.Count(c => c == '-') >= 2 && char.IsDigit(candidate[^1]);
        return looksLikeRegion ? (candidate, labels[aws - 2]) : ("us-east-1", candidate);
    }
}

/// <summary>Signs a request with AWS Signature Version 4: the canonical request, the string to
/// sign, the derived key and the <c>Authorization</c> header, as AWS documents them. S3 is the one
/// service whose path is encoded once rather than twice and that wants
/// <c>x-amz-content-sha256</c>; both follow from the service name.</summary>
public static class AwsSigV4
{
    private const string Algorithm = "AWS4-HMAC-SHA256";

    /// <summary>Add the signature headers to a message that is ready to send. The body is read
    /// (and buffered) to hash it, so what is signed is exactly what is sent.</summary>
    public static async Task SignAsync(HttpRequestMessage message, AwsSigV4Options options, DateTimeOffset now, CancellationToken ct = default)
    {
        byte[] body = Array.Empty<byte>();
        if (message.Content is not null)
        {
            await message.Content.LoadIntoBufferAsync(ct);
            body = await message.Content.ReadAsByteArrayAsync(ct);
        }
        // A session token typed as a header is the caller's way of giving one: kept, and signed.
        if (string.IsNullOrEmpty(options.SessionToken) && message.Headers.TryGetValues("X-Amz-Security-Token", out var typed))
            options = options with { SessionToken = typed.First() };
        foreach (var h in new[] { "Authorization", "X-Amz-Date", "X-Amz-Content-Sha256", "X-Amz-Security-Token" })
            message.Headers.Remove(h);

        var headers = new List<KeyValuePair<string, string>>();
        foreach (var h in message.Headers) headers.Add(new(h.Key, string.Join(",", h.Value)));
        if (message.Content is not null)
            foreach (var h in message.Content.Headers) headers.Add(new(h.Key, string.Join(",", h.Value)));

        foreach (var h in Sign(message.Method.Method, message.RequestUri!, headers, body, options, now))
            message.Headers.TryAddWithoutValidation(h.Key, h.Value);
    }

    /// <summary>The headers to add: <c>X-Amz-Date</c>, <c>X-Amz-Security-Token</c> (temporary
    /// credentials), <c>X-Amz-Content-Sha256</c> (S3) and <c>Authorization</c>. Pure, for tests
    /// against AWS's published vectors.</summary>
    public static IReadOnlyList<KeyValuePair<string, string>> Sign(string method, Uri uri,
        IEnumerable<KeyValuePair<string, string>> headers, byte[] body, AwsSigV4Options options, DateTimeOffset now)
    {
        string amzDate = now.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        string date = amzDate[..8];
        bool s3 = options.Service.Equals("s3", StringComparison.OrdinalIgnoreCase);
        string payloadHash = Hex(SHA256.HashData(body));

        var added = new List<KeyValuePair<string, string>> { new("X-Amz-Date", amzDate) };
        if (!string.IsNullOrEmpty(options.SessionToken)) added.Add(new("X-Amz-Security-Token", options.SessionToken));
        if (s3) added.Add(new("X-Amz-Content-Sha256", payloadHash));

        // Host, Content-Type and every x-amz-* header are signed; anything else a proxy or the
        // stack may change (User-Agent, Accept-Encoding) is not, so it cannot break the signature.
        var signed = new SortedDictionary<string, List<string>>(StringComparer.Ordinal)
        {
            ["host"] = new() { HostValue(uri) }
        };
        foreach (var h in headers.Concat(added))
        {
            string name = h.Key.Trim().ToLowerInvariant();
            if (name == "host") continue;
            if (name != "content-type" && !name.StartsWith("x-amz-", StringComparison.Ordinal)) continue;
            if (!signed.TryGetValue(name, out var values)) signed[name] = values = new List<string>();
            values.Add(Collapse(h.Value));
        }
        string canonicalHeaders = string.Concat(signed.Select(kv => kv.Key + ":" + string.Join(",", kv.Value) + "\n"));
        string signedHeaders = string.Join(";", signed.Keys);

        string canonicalRequest = string.Join("\n",
            method.ToUpperInvariant(),
            CanonicalPath(uri, s3),
            CanonicalQuery(uri),
            canonicalHeaders,
            signedHeaders,
            payloadHash);

        string scope = $"{date}/{options.Region}/{options.Service}/aws4_request";
        string stringToSign = string.Join("\n", Algorithm, amzDate, scope, Hex(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRequest))));

        byte[] key = Hmac(Encoding.UTF8.GetBytes("AWS4" + options.SecretAccessKey), date);
        key = Hmac(key, options.Region);
        key = Hmac(key, options.Service);
        key = Hmac(key, "aws4_request");
        string signature = Hex(Hmac(key, stringToSign));

        added.Add(new("Authorization",
            $"{Algorithm} Credential={options.AccessKeyId}/{scope}, SignedHeaders={signedHeaders}, Signature={signature}"));
        return added;
    }

    private static string HostValue(Uri uri) => uri.IsDefaultPort ? uri.IdnHost : $"{uri.IdnHost}:{uri.Port}";

    /// <summary>Header values are trimmed and runs of spaces collapse to one.</summary>
    private static string Collapse(string value)
    {
        var sb = new StringBuilder(value.Length);
        bool space = false;
        foreach (char c in value.Trim())
        {
            if (c == ' ' || c == '\t') { if (!space) sb.Append(' '); space = true; }
            else { sb.Append(c); space = false; }
        }
        return sb.ToString();
    }

    /// <summary>The path as sent, encoded once more for every service but S3 (which signs the
    /// path as it travels). An empty path is "/".</summary>
    private static string CanonicalPath(Uri uri, bool s3)
    {
        string path = uri.AbsolutePath;
        if (path.Length == 0) path = "/";
        if (s3) return path;
        return string.Join("/", path.Split('/').Select(segment => Encode(segment)));
    }

    /// <summary>Each name and value decoded then strictly re-encoded, sorted by name then value.</summary>
    private static string CanonicalQuery(Uri uri)
    {
        string query = uri.Query.TrimStart('?');
        if (query.Length == 0) return "";
        var pairs = new List<(string Name, string Value)>();
        foreach (var part in query.Split('&'))
        {
            if (part.Length == 0) continue;
            int eq = part.IndexOf('=');
            string name = eq < 0 ? part : part[..eq];
            string value = eq < 0 ? "" : part[(eq + 1)..];
            pairs.Add((Encode(Decode(name)), Encode(Decode(value))));
        }
        return string.Join("&", pairs.OrderBy(p => p.Name, StringComparer.Ordinal).ThenBy(p => p.Value, StringComparer.Ordinal)
            .Select(p => p.Name + "=" + p.Value));
    }

    /// <summary>A '+' stays a plus, as AWS's own SDKs treat it, and is signed as %2B.</summary>
    private static string Decode(string s) => Uri.UnescapeDataString(s);

    /// <summary>RFC 3986: everything but unreserved characters is percent-encoded, upper-case hex.</summary>
    private static string Encode(string s)
    {
        var sb = new StringBuilder();
        foreach (byte b in Encoding.UTF8.GetBytes(s))
        {
            char c = (char)b;
            if (c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_' or '.' or '~') sb.Append(c);
            else sb.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    private static byte[] Hmac(byte[] key, string data) => HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(data));

    private static string Hex(byte[] bytes) => Convert.ToHexStringLower(bytes);
}
