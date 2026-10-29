using System.Text.Unicode;

namespace ApiTester.Cli;

/// <summary>How much of a response body <c>send</c> prints when its output is a console.
/// <para>The console draws every byte it is given. A 50 MB JSON export or a PDF written to a
/// console window kept the process (and, in a remote session, the whole desktop) busy drawing for
/// minutes, and binary bytes include control codes that rewrite the console's own state. So to a
/// console a binary body is not printed and a text body is cut at <see cref="MaxBytes"/>, each with
/// a note on stderr. Redirected output (a file, a pipe) and <c>-o -</c> get every byte.</para></summary>
internal static class ConsoleBody
{
    /// <summary>The most of a text body printed to a console.</summary>
    public const int MaxBytes = 1024 * 1024;

    /// <summary>The most of a binary body that <c>--pretty</c> shows as hex on a console.</summary>
    public const int MaxHexBytes = 64 * 1024;

    /// <summary>A body over this is not parsed for <c>--pretty</c> on a console: the start of it is
    /// printed as received, as without <c>--pretty</c>.</summary>
    public const int MaxFormatBytes = 4 * 1024 * 1024;

    /// <summary>True when the bytes are not text to show on a console: a NUL anywhere, or bytes that
    /// are not UTF-8 when the content type does not say the body is text (a text/* body in a legacy
    /// encoding is still text).</summary>
    public static bool LooksBinary(byte[] body, string? contentType)
    {
        if (body.AsSpan().IndexOf((byte)0) >= 0) return true;
        if (Utf8.IsValid(body)) return false;
        string ct = contentType?.ToLowerInvariant() ?? "";
        bool saysText = ct.StartsWith("text/", StringComparison.Ordinal) || ct.Contains("json") ||
                        ct.Contains("xml") || ct.Contains("javascript") || ct.Contains("html");
        return !saysText;
    }

    /// <summary>True when the body has a NUL byte, which is what makes <c>--pretty</c> show hex.</summary>
    public static bool HasNul(byte[] body) => body.AsSpan().IndexOf((byte)0) >= 0;

    /// <summary>The length of the first <paramref name="max"/> bytes or fewer that does not end in
    /// the middle of a UTF-8 character.</summary>
    public static int CutAt(byte[] body, int max)
    {
        if (body.Length <= max) return body.Length;
        int n = max;
        // body[n] is the first byte left out; while it continues a character, leave that character out too.
        for (int back = 0; back < 3 && n > 0 && (body[n] & 0xC0) == 0x80; back++) n--;
        return n;
    }
}
