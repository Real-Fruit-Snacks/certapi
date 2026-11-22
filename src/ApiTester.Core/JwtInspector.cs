using System.Text;
using System.Text.Json;

namespace ApiTester.Core;

/// <summary>Decoded metadata and claims of a JSON Web Token (JWS). Inspection is offline and does
/// not verify the cryptographic signature — it exists to let a user read what a token claims and
/// when it expires without pasting corporate credentials into an external site.</summary>
public sealed record JwtInfo(
    string RawHeaderJson,
    string RawPayloadJson,
    string? Algorithm,
    string? Type,
    string? KeyId,
    string? Issuer,
    string? Subject,
    IReadOnlyList<string> Audiences,
    IReadOnlyList<string> Scopes,
    IReadOnlyList<string> Roles,
    DateTimeOffset? IssuedAtUtc,
    DateTimeOffset? NotBeforeUtc,
    DateTimeOffset? ExpiresUtc,
    string? JwtId,
    IReadOnlyDictionary<string, string> Claims)
{
    public bool IsExpired(DateTimeOffset? now = null) =>
        ExpiresUtc is { } exp && exp <= (now ?? DateTimeOffset.UtcNow);

    public bool IsNotYetValid(DateTimeOffset? now = null) =>
        NotBeforeUtc is { } nbf && nbf > (now ?? DateTimeOffset.UtcNow);

    public TimeSpan? Remaining(DateTimeOffset? now = null) =>
        ExpiresUtc is { } exp ? exp - (now ?? DateTimeOffset.UtcNow) : null;
}

/// <summary>Offline JWT (JWS) decoder and human-readable formatter.</summary>
public static class JwtInspector
{
    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };

    /// <summary>True when <paramref name="value"/> has the three-segment Base64Url shape of a JWT.</summary>
    public static bool LooksLikeJwt(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var span = StripBearer(value.AsSpan().Trim());
        int first = span.IndexOf('.');
        if (first <= 0) return false;
        var rest = span[(first + 1)..];
        int second = rest.IndexOf('.');
        if (second <= 0 || second == rest.Length - 1) return false;
        return rest[(second + 1)..].IndexOf('.') < 0;
    }

    public static bool TryParse(string? token, out JwtInfo? info, out string? error)
    {
        info = null;
        error = null;
        if (string.IsNullOrWhiteSpace(token))
        {
            error = "Token is empty.";
            return false;
        }

        var span = StripBearer(token.AsSpan().Trim());
        int first = span.IndexOf('.');
        if (first <= 0)
        {
            error = "Not a JWT: expected three dot-separated segments (header.payload.signature).";
            return false;
        }
        var afterFirst = span[(first + 1)..];
        int second = afterFirst.IndexOf('.');
        if (second <= 0 || afterFirst[(second + 1)..].IndexOf('.') >= 0)
        {
            error = "Not a JWT: expected three dot-separated segments (header.payload.signature).";
            return false;
        }

        if (!TryBase64UrlDecode(span[..first], out byte[]? headerBytes))
        {
            error = "JWT header is not valid Base64Url.";
            return false;
        }
        if (!TryBase64UrlDecode(afterFirst[..second], out byte[]? payloadBytes))
        {
            error = "JWT payload is not valid Base64Url.";
            return false;
        }

        try
        {
            using var headerDoc = JsonDocument.Parse(headerBytes!);
            using var payloadDoc = JsonDocument.Parse(payloadBytes!);
            if (headerDoc.RootElement.ValueKind != JsonValueKind.Object ||
                payloadDoc.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = "JWT header and payload must be JSON objects.";
                return false;
            }

            var h = headerDoc.RootElement;
            var p = payloadDoc.RootElement;

            var claims = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var prop in p.EnumerateObject())
                claims[prop.Name] = FormatClaimValue(prop.Value);

            var audiences = ReadStringOrArray(p, "aud");
            var scopes = ReadScopes(p);
            var roles = ReadStringOrArray(p, "roles")
                .Concat(ReadStringOrArray(p, "role"))
                .Distinct(StringComparer.Ordinal)
                .ToList();

            info = new JwtInfo(
                RawHeaderJson: JsonSerializer.Serialize(h, IndentedJson),
                RawPayloadJson: JsonSerializer.Serialize(p, IndentedJson),
                Algorithm: ReadString(h, "alg"),
                Type: ReadString(h, "typ"),
                KeyId: ReadString(h, "kid"),
                Issuer: ReadString(p, "iss"),
                Subject: ReadString(p, "sub"),
                Audiences: audiences,
                Scopes: scopes,
                Roles: roles,
                IssuedAtUtc: ReadUnixTime(p, "iat"),
                NotBeforeUtc: ReadUnixTime(p, "nbf"),
                ExpiresUtc: ReadUnixTime(p, "exp"),
                JwtId: ReadString(p, "jti"),
                Claims: claims);
            return true;
        }
        catch (JsonException ex)
        {
            error = $"JWT segment is not valid JSON: {ex.Message}";
            return false;
        }
    }

    /// <summary>Render a concise, human-readable summary of a decoded JWT.</summary>
    public static string FormatText(JwtInfo jwt, DateTimeOffset? now = null)
    {
        var clock = now ?? DateTimeOffset.UtcNow;
        var sb = new StringBuilder();
        sb.Append("Header:  alg=").Append(jwt.Algorithm ?? "(none)");
        if (jwt.Type is not null) sb.Append("  typ=").Append(jwt.Type);
        if (jwt.KeyId is not null) sb.Append("  kid=").Append(jwt.KeyId);
        sb.AppendLine();

        if (jwt.Issuer is not null) sb.Append("Issuer:  ").AppendLine(jwt.Issuer);
        if (jwt.Subject is not null) sb.Append("Subject: ").AppendLine(jwt.Subject);
        if (jwt.Audiences.Count > 0) sb.Append("Aud:     ").AppendLine(string.Join(", ", jwt.Audiences));
        if (jwt.Scopes.Count > 0) sb.Append("Scopes:  ").AppendLine(string.Join(" ", jwt.Scopes));
        if (jwt.Roles.Count > 0) sb.Append("Roles:   ").AppendLine(string.Join(", ", jwt.Roles));
        if (jwt.JwtId is not null) sb.Append("JTI:     ").AppendLine(jwt.JwtId);
        if (jwt.IssuedAtUtc is { } iat) sb.Append("Issued:  ").AppendLine(iat.ToString("yyyy-MM-dd HH:mm:ss 'UTC'"));
        if (jwt.NotBeforeUtc is { } nbf)
            sb.Append("ValidFrom: ").Append(nbf.ToString("yyyy-MM-dd HH:mm:ss 'UTC'"))
              .AppendLine(jwt.IsNotYetValid(clock) ? "  [NOT YET VALID]" : "");
        if (jwt.ExpiresUtc is { } exp)
        {
            var rem = exp - clock;
            string status = rem <= TimeSpan.Zero
                ? $"EXPIRED {FormatSpan(-rem)} ago"
                : $"valid for {FormatSpan(rem)}";
            sb.Append("Expires: ").Append(exp.ToString("yyyy-MM-dd HH:mm:ss 'UTC'"))
              .Append("  (").Append(status).AppendLine(")");
        }

        sb.AppendLine();
        sb.AppendLine("Payload:");
        sb.Append(jwt.RawPayloadJson);
        return sb.ToString();
    }

    private static string FormatSpan(TimeSpan ts)
    {
        if (ts.TotalDays >= 1) return $"{(int)ts.TotalDays}d {ts.Hours}h";
        if (ts.TotalHours >= 1) return $"{(int)ts.TotalHours}h {ts.Minutes}m";
        if (ts.TotalMinutes >= 1) return $"{(int)ts.TotalMinutes}m {ts.Seconds}s";
        return $"{Math.Max(0, (int)ts.TotalSeconds)}s";
    }

    private static ReadOnlySpan<char> StripBearer(ReadOnlySpan<char> s)
    {
        if (s.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return s["Bearer ".Length..].Trim();
        return s;
    }

    private static bool TryBase64UrlDecode(ReadOnlySpan<char> input, out byte[]? bytes)
    {
        bytes = null;
        if (input.Length == 0) return false;
        int mod = input.Length % 4;
        if (mod == 1) return false;
        int paddedLen = mod == 0 ? input.Length : input.Length + (4 - mod);
        char[] buf = new char[paddedLen];
        for (int i = 0; i < input.Length; i++)
        {
            char c = input[i];
            buf[i] = c switch
            {
                '-' => '+',
                '_' => '/',
                _ => c
            };
        }
        for (int i = input.Length; i < paddedLen; i++) buf[i] = '=';
        try
        {
            bytes = Convert.FromBase64CharArray(buf, 0, paddedLen);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string? ReadString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;

    private static DateTimeOffset? ReadUnixTime(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var el)) return null;
        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt64(out long sec))
            return DateTimeOffset.FromUnixTimeSeconds(sec);
        if (el.ValueKind == JsonValueKind.Number && el.TryGetDouble(out double d))
            return DateTimeOffset.FromUnixTimeSeconds((long)d);
        return null;
    }

    private static List<string> ReadStringOrArray(JsonElement obj, string name)
    {
        var list = new List<string>();
        if (!obj.TryGetProperty(name, out var el)) return list;
        if (el.ValueKind == JsonValueKind.String && el.GetString() is { Length: > 0 } s)
            list.Add(s);
        else if (el.ValueKind == JsonValueKind.Array)
            foreach (var item in el.EnumerateArray())
                if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } v)
                    list.Add(v);
        return list;
    }

    private static List<string> ReadScopes(JsonElement obj)
    {
        var list = new List<string>();
        foreach (var propName in new[] { "scp", "scope" })
        {
            if (!obj.TryGetProperty(propName, out var el)) continue;
            if (el.ValueKind == JsonValueKind.String && el.GetString() is { Length: > 0 } s)
            {
                foreach (var part in s.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    if (!list.Contains(part, StringComparer.Ordinal)) list.Add(part);
            }
            else if (el.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in el.EnumerateArray())
                    if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } v &&
                        !list.Contains(v, StringComparer.Ordinal))
                        list.Add(v);
            }
        }
        return list;
    }

    private static string FormatClaimValue(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.String => el.GetString() ?? "",
        JsonValueKind.Null => "null",
        _ => el.GetRawText()
    };
}
