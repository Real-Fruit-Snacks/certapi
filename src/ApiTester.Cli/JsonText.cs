using System.Buffers;
using System.Text;
using System.Text.Json;

namespace ApiTester.Cli;

/// <summary>Indented JSON written field by field. The commands' --json output used to be built from
/// anonymous objects and handed to the reflection serializer, which spends tens of milliseconds
/// building metadata for each shape before writing anything; this writes the same text directly
/// (same names, order, escaping and number and date forms — the writer is the one the serializer
/// itself uses).</summary>
internal static class JsonText
{
    public static string Indented(Action<Utf8JsonWriter> write, int sizeHint = 4096)
    {
        var buffer = new ArrayBufferWriter<byte>(sizeHint);
        using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
            write(w);
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    public static void StringOrNull(this Utf8JsonWriter w, string name, string? value)
    {
        if (value is null) w.WriteNull(name); else w.WriteString(name, value);
    }

    public static void NumberOrNull(this Utf8JsonWriter w, string name, int? value)
    {
        if (value is { } v) w.WriteNumber(name, v); else w.WriteNull(name);
    }
}
