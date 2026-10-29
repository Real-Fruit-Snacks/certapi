using System.Text;
using ApiTester.Core;

namespace ApiTester.App;

/// <summary>How much of a response body the Pretty and Raw views show.
/// <para>WPF lays out all of a TextBox's or RichTextBox's text on the UI thread. A body of several
/// megabytes (an export, a download) put in either one in full kept the window from responding for
/// as long as that took, which on a VDI's software-drawn desktop was minutes; a binary body was
/// first turned into a hex dump about five times its size. The views now show the start of a large
/// body and say how much is left out. Copy and Save… still take all of it.</para></summary>
public static class ResponseView
{
    /// <summary>The most of a body the Raw view shows.</summary>
    public const int MaxRawChars = 1024 * 1024;

    /// <summary>The most text the Pretty view shows.</summary>
    public const int MaxPrettyChars = 512 * 1024;

    /// <summary>The most of a binary body the Pretty view shows as hex.</summary>
    public const int MaxHexBytes = 64 * 1024;

    /// <summary>A body over this is not parsed to be pretty-printed: the start of it is shown as received.</summary>
    public const int MaxFormatBytes = 4 * 1024 * 1024;

    private static readonly ResponseFormatter Formatter = new();

    /// <summary>What the Pretty view shows for a response: what <see cref="ResponseFormatter"/>
    /// makes of it, without parsing or hex-dumping a body too large to show.</summary>
    public static FormattedResponse Pretty(ApiResponse response)
    {
        var body = response.Body ?? Array.Empty<byte>();
        // A NUL never parses as JSON or XML, so the formatter would dump this body as hex.
        if (body.Length > MaxHexBytes && body.AsSpan().IndexOf((byte)0) >= 0)
            return new(BodyKind.Binary, ResponseFormatter.HexDump(body[..MaxHexBytes]) +
                $"\n… the other {Size(body.Length - MaxHexBytes)} of this {Size(body.Length)} body are not shown. Save… keeps all of it.");
        if (body.Length > MaxFormatBytes)
        {
            // Short of the Pretty view's own limit by room for the note, so the view keeps this note
            // instead of cutting it off and adding its own.
            int take = Utf8Cut(body, MaxPrettyChars - 1024);
            return new(BodyKind.Text, Encoding.UTF8.GetString(body, 0, take) +
                $"\n\n… this {Size(body.Length)} body is too large to format here; the first {Size(take)} is shown as received. Copy and Save… take all of it.");
        }
        return Formatter.Format(response);
    }

    /// <summary>What the Raw view shows of a body's text.</summary>
    public static string Raw(string text) => Cut(text, MaxRawChars);

    /// <summary>The text, or its start with a line saying how much is left out.</summary>
    public static string Cut(string text, int max)
    {
        if (text.Length <= max) return text;
        int cut = char.IsHighSurrogate(text[max - 1]) ? max - 1 : max;
        return text[..cut] + $"\n\n… {text.Length - cut:N0} more characters are not shown here. Copy and Save… take all of it.";
    }

    private static int Utf8Cut(byte[] body, int max)
    {
        if (body.Length <= max) return body.Length;
        int n = max;
        for (int back = 0; back < 3 && n > 0 && (body[n] & 0xC0) == 0x80; back++) n--;
        return n;
    }

    private static string Size(long bytes) =>
        bytes < 1024 ? $"{bytes} bytes" :
        bytes < 1024 * 1024 ? $"{bytes / 1024.0:F0} KB" :
        $"{bytes / (1024.0 * 1024.0):F1} MB";
}
