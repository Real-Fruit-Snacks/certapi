using System.Text;
using System.Text.Json;
using ApiTester.Core;

namespace ApiTester.Tests;

/// <summary>The streaming pretty-printer writes exactly what the document-based one did, for every
/// shape of JSON, and still refuses what is not JSON.</summary>
public class PrettyJsonTests
{
    /// <summary>The formatter as it was: parse a document, serialize its root indented.</summary>
    private static string? Reference(byte[] body)
    {
        try
        {
            ReadOnlyMemory<byte> json = body.Length >= 3 && body[0] == 0xEF && body[1] == 0xBB && body[2] == 0xBF ? body.AsMemory(3) : body;
            using var doc = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            });
        }
        catch (JsonException) { return null; }
    }

    private static string? Streamed(byte[] body)
    {
        var output = new System.Buffers.ArrayBufferWriter<byte>();
        return ResponseFormatter.TryWriteJson(body, output) ? Encoding.UTF8.GetString(output.WrittenSpan) : null;
    }

    public static TheoryData<string> Valid => new()
    {
        "{}", "[]", "[[]]", "{\"a\":{}}", "{\"a\":[]}", "[{},{}]", "\"just a string\"", "42", "-0", "true", "false", "null",
        "  {\"spaced\" : [ 1 , 2 ] }  ",
        "{\"n\":[0,-1,1.5,1e10,1.5E-7,-2.5e+3,12345678901234567890123,0.000001]}",
        "{\"esc\":\"line\\nbreak \\\"quote\\\" back\\\\slash \\/ tab\\t\"}",
        "{\"uni\":\"caf\\u00e9 \\ud83d\\ude00 and raw café 😀\"}",
        "{\"html\":\"<b>&amp;</b> 'x' +\"}",
        "{\"ctl\":\"\\u0001\\u001f\"}",
        "{\"k\\u0065y\":\"escaped name\",\"plain\":1}",
        "{\"deep\":" + string.Concat(Enumerable.Repeat("[", 60)) + string.Concat(Enumerable.Repeat("]", 60)) + "}",
        "[\"a\",[\"b\",[\"c\",{\"d\":[1,{\"e\":null}]}]]]",
        "{\"dup\":1,\"dup\":2}",
        "[1.50,1.0,-0.0,0e0,1E400,123.456e-2,9007199254740993,100,-100,0.1,-12.5,99999999999999999999,1.000000000000000000001]",
    };

    [Theory]
    [MemberData(nameof(Valid))]
    public void Writes_what_the_document_formatter_wrote(string json)
    {
        var body = Encoding.UTF8.GetBytes(json);
        var expected = Reference(body);
        Assert.NotNull(expected);
        Assert.True(ResponseFormatter.IsWellFormedJson(body));
        Assert.Equal(expected, Streamed(body));
    }

    [Fact]
    public void A_byte_order_mark_is_skipped_as_before()
    {
        var body = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("{\"bom\":true}")).ToArray();
        Assert.Equal(Reference(body), Streamed(body));
    }

    [Fact]
    public void A_large_generated_document_matches()
    {
        var sb = new StringBuilder("[");
        for (int i = 0; i < 20000; i++)
            sb.Append(i == 0 ? "" : ",").Append($"{{\"id\":{i},\"name\":\"item {i} é\",\"tags\":[\"a\",\"b\"],\"v\":{i * 1.25},\"ok\":{(i % 2 == 0 ? "true" : "false")},\"x\":null}}");
        var body = Encoding.UTF8.GetBytes(sb.Append(']').ToString());
        Assert.Equal(Reference(body), Streamed(body));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{")]
    [InlineData("[1,]")]
    [InlineData("{}x")]
    [InlineData("{'a':1}")]
    [InlineData("[1 2]")]
    [InlineData("{\"a\":1,}")]
    [InlineData("// comment\n{}")]
    [InlineData("NaN")]
    public void What_is_not_json_is_still_not_json(string text)
    {
        var body = Encoding.UTF8.GetBytes(text);
        Assert.Null(Reference(body));
        Assert.Null(Streamed(body));
    }

    [Fact]
    public void Too_deep_is_refused_by_both()
    {
        var body = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("[", 70)) + string.Concat(Enumerable.Repeat("]", 70)));
        Assert.Null(Reference(body));
        Assert.Null(Streamed(body));
    }

    [Fact]
    public void Invalid_utf8_inside_a_string_comes_out_as_the_document_formatter_wrote_it()
    {
        var body = Encoding.UTF8.GetBytes("{\"bad\":\"xyz\"}");
        body[9] = 0xFF;   // the y: inside the string, so the structure is still valid JSON
        Assert.Equal(Reference(body), Streamed(body));
        Assert.True(ResponseFormatter.IsWellFormedJson(body));
    }

    [Theory]
    [InlineData("{\"lone\":\"\\ud800\"}")]
    [InlineData("{\"a\":\"\\u12\"}")]
    public void The_check_and_the_writer_agree_on_awkward_escapes(string json)
    {
        var body = Encoding.UTF8.GetBytes(json);
        string? streamed = Streamed(body);
        Assert.Equal(streamed is not null, ResponseFormatter.IsWellFormedJson(body));
        if (Reference(body) is { } expected && streamed is not null) Assert.Equal(expected, streamed);
    }
}

public class PrettyJsonLimitTests
{
    [Fact]
    public void A_limit_stops_the_formatting_and_says_so()
    {
        var body = Encoding.UTF8.GetBytes("[" + string.Join(",", Enumerable.Repeat("{\"a\":1}", 100_000)) + "]");
        var all = new System.Buffers.ArrayBufferWriter<byte>();
        Assert.True(ResponseFormatter.TryWriteJson(body, all));
        var some = new System.Buffers.ArrayBufferWriter<byte>();
        Assert.True(ResponseFormatter.TryWriteJson(body, some, 10_000, out bool stopped));
        Assert.True(stopped);
        Assert.InRange(some.WrittenCount, 10_000, 30_000);
        Assert.True(all.WrittenSpan.StartsWith(some.WrittenSpan));

        var whole = new System.Buffers.ArrayBufferWriter<byte>();
        Assert.True(ResponseFormatter.TryWriteJson(body, whole, long.MaxValue, out bool notStopped));
        Assert.False(notStopped);
        Assert.Equal(all.WrittenCount, whole.WrittenCount);
    }
}
