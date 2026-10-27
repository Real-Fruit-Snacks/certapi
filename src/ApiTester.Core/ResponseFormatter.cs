using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace ApiTester.Core;

public sealed class ResponseFormatter
{
    public FormattedResponse Format(ApiResponse response)
    {
        var body = response.Body;
        if (body is null || body.Length == 0)
            return new FormattedResponse(BodyKind.Empty, string.Empty);

        var ct = response.ContentType?.ToLowerInvariant() ?? string.Empty;

        // Trust a matching content-type first, but fall through if the body doesn't parse.
        if (ct.Contains("json") && TryJson(body, out var cj)) return new(BodyKind.Json, cj);
        if (ct.Contains("xml") && TryXml(body, out var cx)) return new(BodyKind.Xml, cx);
        if (ct.Contains("html") && IsPrintable(body)) return new(BodyKind.Html, Encoding.UTF8.GetString(body));
        if (ct.StartsWith("text/") && IsPrintable(body)) return new(BodyKind.Text, Encoding.UTF8.GetString(body));

        // Content-type missing or misleading: sniff.
        if (TryJson(body, out var sj)) return new(BodyKind.Json, sj);
        if (TryXml(body, out var sx)) return new(BodyKind.Xml, sx);
        if (IsPrintable(body)) return new(BodyKind.Text, Encoding.UTF8.GetString(body));
        return new(BodyKind.Binary, HexDump(body));
    }

    private static readonly JsonSerializerOptions PrettyOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static bool TryJson(byte[] body, out string pretty)
    {
        try
        {
            using var doc = JsonDocument.Parse(Utf8Json.Body(body));
            // Relaxed escaping: the pretty view shows José and <b> as written, not as \u00E9 and \u003C.
            pretty = JsonSerializer.Serialize(doc.RootElement, PrettyOptions);
            return true;
        }
        catch (JsonException)
        {
            pretty = string.Empty;
            return false;
        }
    }

    private static bool TryXml(byte[] body, out string pretty)
    {
        try
        {
            var doc = XDocument.Parse(Encoding.UTF8.GetString(body));
            pretty = doc.ToString();
            return true;
        }
        catch (System.Xml.XmlException)
        {
            pretty = string.Empty;
            return false;
        }
    }

    private static bool IsPrintable(byte[] body)
    {
        foreach (var b in body)
            if (b == 0) return false; // NUL byte => treat as binary
        return true;
    }

    public static string HexDump(byte[] body)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < body.Length; i += 16)
        {
            sb.Append(i.ToString("x8")).Append("  ");
            var ascii = new StringBuilder();
            for (int j = 0; j < 16; j++)
            {
                if (i + j < body.Length)
                {
                    byte b = body[i + j];
                    sb.Append(b.ToString("x2")).Append(' ');
                    ascii.Append(b is >= 0x20 and < 0x7f ? (char)b : '.');
                }
                else
                {
                    sb.Append("   ");
                }
            }
            sb.Append(' ').Append(ascii).Append('\n');
        }
        return sb.ToString();
    }
}
