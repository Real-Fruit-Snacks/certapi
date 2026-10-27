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

        Normalize(har);
        return har;
    }

    /// <summary>JSON nulls where the format has lists or objects ("entries": null, "headers": null,
    /// a null entry) become what an absent value would have been, or a named error — so every
    /// reader downstream (import, replay, diff, mock) sees a well-formed archive or a clear message,
    /// never a null-reference failure.</summary>
    private static void Normalize(Har har)
    {
        har.Log ??= new HarLog();
        har.Log.Entries ??= new List<HarEntry>();
        for (int i = 0; i < har.Log.Entries.Count; i++)
        {
            var entry = har.Log.Entries[i]
                ?? throw new HarFormatException($"The file is not a valid HAR archive: entry {i + 1} is null.");
            if (entry.Request is null)
                throw new HarFormatException($"The file is not a valid HAR archive: entry {i + 1} has no request.");
            entry.Request.Method ??= "GET";
            entry.Request.Url ??= "";
            entry.Request.Headers = (entry.Request.Headers ?? new()).Where(h => h is not null && !string.IsNullOrEmpty(h.Name))
                .Select(h => h.Value is null ? h with { Value = "" } : h).ToList();
            entry.Request.QueryString = (entry.Request.QueryString ?? new()).Where(q => q is not null && !string.IsNullOrEmpty(q.Name))
                .Select(q => q.Value is null ? q with { Value = "" } : q).ToList();
            if (entry.Request.PostData is { } pd) { pd.MimeType ??= ""; pd.Text ??= ""; }
            entry.Response ??= new HarResponse();
            entry.Response.StatusText ??= "";
            entry.Response.RedirectUrl ??= "";
            entry.Response.Headers = (entry.Response.Headers ?? new()).Where(h => h is not null && !string.IsNullOrEmpty(h.Name))
                .Select(h => h.Value is null ? h with { Value = "" } : h).ToList();
            entry.Response.Content ??= new HarContent();
            entry.Timings ??= new HarTimings();
        }
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
