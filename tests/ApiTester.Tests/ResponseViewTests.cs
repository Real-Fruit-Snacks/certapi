using System.Text;
using ApiTester.App;
using ApiTester.Core;

namespace ApiTester.Tests;

/// <summary>The app's response views show the start of a large body, never all of it: laying out
/// megabytes of text on the UI thread is what stopped the window from responding.</summary>
public class ResponseViewTests
{
    private static ApiResponse With(byte[] body, string contentType) =>
        new() { StatusCode = 200, ContentType = contentType, Body = body };

    [Fact]
    public void A_large_binary_body_is_shown_as_hex_for_its_first_64_KB_only()
    {
        var body = new byte[1024 * 1024];
        new Random(3).NextBytes(body);
        body[10] = 0;

        var shown = ResponseView.Pretty(With(body, "application/octet-stream"));

        Assert.Equal(BodyKind.Binary, shown.Kind);
        var lines = shown.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(ResponseView.MaxHexBytes / 16 + 1, lines.Length);   // the dump, then the note
        Assert.Contains("the other 960 KB of this 1.0 MB body are not shown", lines[^1]);
    }

    [Fact]
    public void A_large_json_body_is_formatted_for_as_much_as_the_view_shows()
    {
        var json = Encoding.UTF8.GetBytes("[" + string.Join(",", Enumerable.Repeat("{\"name\":\"café\",\"n\":1}", 250_000)) + "]");
        Assert.True(json.Length > ResponseView.MaxFormatBytes);

        var shown = ResponseView.Pretty(With(json, "application/json"));

        Assert.Equal(BodyKind.Json, shown.Kind);
        Assert.StartsWith("[" + System.Environment.NewLine + "  {" + System.Environment.NewLine + "    \"name\": \"café\",", shown.Text);
        Assert.Contains("is formatted here in part", shown.Text);
        Assert.True(shown.Text.Length < ResponseView.MaxPrettyChars + 300);
        Assert.DoesNotContain('\uFFFD', shown.Text);   // the cut did not split the é
        // What is shown is exactly the start of the full formatting.
        string full = new ResponseFormatter().Format(With(json, "application/json")).Text;
        Assert.StartsWith(shown.Text[..shown.Text.IndexOf("\n\n… this", StringComparison.Ordinal)], full);
    }

    [Fact]
    public void A_large_body_that_is_not_json_from_the_start_is_shown_as_received()
    {
        var text = Encoding.UTF8.GetBytes("not json: [" + string.Join(",", Enumerable.Repeat("{\"name\":\"café\",\"n\":1}", 250_000)) + "]");
        Assert.True(text.Length > ResponseView.MaxFormatBytes);

        var shown = ResponseView.Pretty(With(text, "application/json"));

        Assert.Equal(BodyKind.Text, shown.Kind);
        Assert.StartsWith("not json: [{\"name\":\"café\"", shown.Text);   // as received: not indented
        Assert.Contains("is too large to format here", shown.Text);
        Assert.True(shown.Text.Length < ResponseView.MaxPrettyChars + 300);
        Assert.DoesNotContain('\uFFFD', shown.Text);
    }

    [Theory]
    [InlineData("a")]
    [InlineData("é")]
    [InlineData("😀")]
    [InlineData("中")]
    public void A_cut_formatted_view_never_ends_in_half_a_character(string ch)
    {
        // Strings of one repeated character, so some cut lands inside it whatever its width.
        foreach (int count in new[] { 1_100_000, 1_100_001, 1_100_002, 1_100_003 })
        {
            var json = Encoding.UTF8.GetBytes("[\"" + string.Concat(Enumerable.Repeat(ch, count / Encoding.UTF8.GetByteCount(ch) + 1)) + "\"]");
            if (json.Length <= ResponseView.MaxFormatBytes) json = Encoding.UTF8.GetBytes("[" + string.Join(",", Enumerable.Repeat("\"" + new string('x', 1000) + ch + "\"", 5000)) + "]");
            var shown = ResponseView.Pretty(With(json, "application/json"));
            Assert.Equal(BodyKind.Json, shown.Kind);
            Assert.DoesNotContain('\uFFFD', shown.Text);
        }
    }

    [Fact]
    public void A_body_of_ordinary_size_is_formatted_as_before()
    {
        var response = With(Encoding.UTF8.GetBytes("{\"a\":[1,2]}"), "application/json");
        Assert.Equal(new ResponseFormatter().Format(response), ResponseView.Pretty(response));
    }

    [Fact]
    public void Raw_shows_the_first_megabyte_and_says_how_much_is_left()
    {
        string text = new string('x', ResponseView.MaxRawChars + 12_345);
        string shown = ResponseView.Raw(text);
        Assert.StartsWith(new string('x', 1000), shown);
        Assert.EndsWith($"{12_345:N0} more characters are not shown here. Copy and Save… take all of it.", shown);

        Assert.Same("short", ResponseView.Raw("short"));
    }

    [Fact]
    public void A_cut_never_splits_a_surrogate_pair()
    {
        string text = new string('a', 9) + "😀" + "tail";   // the emoji takes chars 9 and 10
        string shown = ResponseView.Cut(text, 10);
        Assert.StartsWith(new string('a', 9) + "\n", shown);
    }
}
