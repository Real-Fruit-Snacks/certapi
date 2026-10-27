using System.Text.Json;

namespace ApiTester.Core;

/// <summary>Parses a HAR document and maps its entries onto the tool's own import DTO. Pure — no
/// I/O, no sockets.</summary>
public static class HarReader
{
    /// <summary>Parse a HAR document. Throws <see cref="HarFormatException"/> when the text is not
    /// valid JSON, or does not describe a HAR archive (missing "log").</summary>
    public static Har Parse(string json)
    {
        Har? har;
        try
        {
            // Har.Log has a non-null default (new HarLog()), so a plain Deserialize<Har> can never
            // observe a missing "log" — it just leaves the default in place. Check the raw document
            // for the property first, which is the only way to distinguish "no log at all" (a
            // malformed file) from "log present but empty".
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("log", out _))
                throw new HarFormatException("The file is not a valid HAR archive: missing \"log\".");

            har = JsonSerializer.Deserialize<Har>(json, HarWriter.Options);
        }
        catch (JsonException ex)
        {
            throw new HarFormatException($"The file is not a valid HAR archive: {ex.Message}", ex);
        }

        if (har is null)
            throw new HarFormatException("The file is not a valid HAR archive: no content.");

        return har;
    }

    /// <summary>Map one HAR entry onto the tool's cURL/OpenAPI import DTO, for replay. HTTP/2
    /// pseudo-headers (<c>:method</c>, <c>:authority</c>, <c>:path</c>, <c>:scheme</c>, …) and
    /// <c>Host</c> are dropped — everything else, including a redacted secret value, passes through
    /// verbatim so the user can re-supply it on replay.</summary>
    public static ParsedRequest ToParsedRequest(HarEntry entry)
    {
        string method = string.IsNullOrEmpty(entry.Request.Method) ? "GET" : entry.Request.Method;
        string url = entry.Request.Url;
        var (path, _) = QueryString.Split(url);
        string name = $"{method} {(string.IsNullOrEmpty(path) ? url : path)}";

        var headers = entry.Request.Headers
            .Where(h => !h.Name.StartsWith(':') && !string.Equals(h.Name, "Host", StringComparison.OrdinalIgnoreCase))
            .Select(h => new KeyValuePair<string, string>(h.Name, h.Value))
            .ToList();

        string? body = entry.Request.PostData?.TextOrNull();
        // A binary body has no text form for a saved request; its type would only mislabel the
        // empty body left in its place.
        string? contentType = body is not null && entry.Request.PostData is { MimeType.Length: > 0 } pd ? pd.MimeType : null;

        return new ParsedRequest
        {
            Method = method,
            BaseUrl = null,
            Url = url,
            Name = name,
            Headers = headers,
            Body = body,
            ContentType = contentType
        };
    }
}
