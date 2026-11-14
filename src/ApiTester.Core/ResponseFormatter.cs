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

    private static bool TryJson(byte[] body, out string pretty)
    {
        var output = new System.Buffers.ArrayBufferWriter<byte>(Math.Max(256, body.Length + body.Length / 2));
        if (TryWriteJson(body, output))
        {
            pretty = Encoding.UTF8.GetString(output.WrittenSpan);
            return true;
        }
        pretty = string.Empty;
        return false;
    }

    private static readonly JsonWriterOptions PrettyWriter = new()
    {
        Indented = true,
        // Relaxed escaping: the pretty view shows José and <b> as written, not as \u00E9 and \u003C.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        SkipValidation = true
    };

    /// <summary>The body re-indented, as UTF-8, straight into <paramref name="output"/>: one pass of
    /// a reader into a writer, with no document or string built in between — the same text the
    /// document-based formatter produced (strings unescaped and re-escaped the relaxed way, numbers
    /// exactly as written), in a fraction of the time and memory for a large body. False when the
    /// body is not one JSON value, in which case <paramref name="output"/> holds a partial write
    /// the caller discards.</summary>
    public static bool TryWriteJson(byte[] body, System.Buffers.IBufferWriter<byte> output) =>
        TryWriteJson(body, output, long.MaxValue, out _);

    /// <summary>As <see cref="TryWriteJson(byte[], System.Buffers.IBufferWriter{byte})"/>, stopping
    /// once <paramref name="maxOutputBytes"/> of formatted text are written — for a view that shows
    /// only the start of a large body, which then costs that start rather than the whole body.
    /// <paramref name="stoppedEarly"/> says it stopped, and that the rest of the body was not read,
    /// so not checked either.</summary>
    public static bool TryWriteJson(byte[] body, System.Buffers.IBufferWriter<byte> output, long maxOutputBytes, out bool stoppedEarly)
    {
        stoppedEarly = false;
        var json = Utf8Json.Body(body).Span;
        try
        {
            var reader = new Utf8JsonReader(json, new JsonReaderOptions());
            using var writer = new Utf8JsonWriter(output, PrettyWriter);
            bool any = false;
            while (reader.Read())
            {
                any = true;
                switch (reader.TokenType)
                {
                    case JsonTokenType.StartObject: writer.WriteStartObject(); break;
                    case JsonTokenType.EndObject: writer.WriteEndObject(); break;
                    case JsonTokenType.StartArray: writer.WriteStartArray(); break;
                    case JsonTokenType.EndArray: writer.WriteEndArray(); break;
                    case JsonTokenType.PropertyName:
                        if (reader.ValueIsEscaped) writer.WritePropertyName(reader.GetString()!);
                        else writer.WritePropertyName(reader.ValueSpan);
                        break;
                    case JsonTokenType.String:
                        if (reader.ValueIsEscaped) writer.WriteStringValue(reader.GetString());
                        else writer.WriteStringValue(reader.ValueSpan);
                        break;
                    case JsonTokenType.Number: WriteNumberAsWritten(writer, reader.ValueSpan); break;
                    case JsonTokenType.True: writer.WriteBooleanValue(true); break;
                    case JsonTokenType.False: writer.WriteBooleanValue(false); break;
                    case JsonTokenType.Null: writer.WriteNullValue(); break;
                }
                if (writer.BytesCommitted + writer.BytesPending >= maxOutputBytes)
                {
                    stoppedEarly = reader.BytesConsumed < json.Length;
                    break;
                }
            }
            writer.Flush();
            return any;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;   // an escaped string the reader could not decode
        }
        catch (ArgumentException)
        {
            return false;   // a value the writer refuses outright
        }
    }

    /// <summary>Whether <see cref="Format"/> would try JSON before anything else for this
    /// response: a JSON content type, or one that names no other format it would try first. When
    /// false, the caller formats through <see cref="Format"/> itself, whose rules decide.</summary>
    public static bool TriesJsonFirst(ApiResponse response)
    {
        var ct = response.ContentType?.ToLowerInvariant() ?? string.Empty;
        if (ct.Contains("json")) return true;
        return !(ct.Contains("xml") || ct.Contains("html") || ct.StartsWith("text/"));
    }

    /// <summary>One read of the body, writing nothing: true when <see cref="TryWriteJson"/> would
    /// succeed, so a caller can stream the formatted text knowing it will not stop half-way.</summary>
    public static bool IsWellFormedJson(byte[] body)
    {
        var json = Utf8Json.Body(body).Span;
        try
        {
            var reader = new Utf8JsonReader(json, new JsonReaderOptions());
            bool any = false;
            while (reader.Read())
            {
                any = true;
                // An escaped string is decoded here exactly as the writer will decode it, so one that
                // cannot be fails here too; unescaped bytes the writer copies, replacing any
                // invalid UTF-8 with U+FFFD as the document-based formatter did.
                if (reader.TokenType is JsonTokenType.String or JsonTokenType.PropertyName && reader.ValueIsEscaped)
                    _ = reader.GetString();
            }
            return any;
        }
        catch (JsonException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    /// <summary>A number exactly as the body wrote it (1e10 stays 1e10, 1.50 stays 1.50), indented
    /// like any other value. The writer's own number formatting is used whenever it reproduces the
    /// text byte for byte — an integer, or a decimal whose shortest form is what was written;
    /// anything else (an exponent, -0, more digits than a decimal holds) goes through a one-token
    /// document, which writes the raw text with the indentation the writer would give it.</summary>
    private static void WriteNumberAsWritten(Utf8JsonWriter writer, ReadOnlySpan<byte> raw)
    {
        if (raw.Length <= 18 && System.Buffers.Text.Utf8Parser.TryParse(raw, out long whole, out int used) && used == raw.Length &&
            Canonical(raw, whole))
        {
            writer.WriteNumberValue(whole);
            return;
        }
        if (System.Buffers.Text.Utf8Parser.TryParse(raw, out decimal fraction, out used) && used == raw.Length)
        {
            Span<byte> formatted = stackalloc byte[64];
            if (System.Buffers.Text.Utf8Formatter.TryFormat(fraction, formatted, out int written) && formatted[..written].SequenceEqual(raw))
            {
                writer.WriteNumberValue(fraction);
                return;
            }
        }
        using var single = JsonDocument.Parse(raw.ToArray());
        single.RootElement.WriteTo(writer);

        static bool Canonical(ReadOnlySpan<byte> raw, long value)
        {
            Span<byte> formatted = stackalloc byte[24];
            return System.Buffers.Text.Utf8Formatter.TryFormat(value, formatted, out int written) && formatted[..written].SequenceEqual(raw);
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
