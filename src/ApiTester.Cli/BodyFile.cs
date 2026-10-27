using System.Text;

namespace ApiTester.Cli;

/// <summary>A request body read from a file (<c>-d @file</c>, <c>--data-file</c>): text when the
/// file is UTF-8 (or UTF-16/32 with a byte-order mark), so {{variables}} in it still resolve, and
/// the file's exact bytes otherwise — an image, an archive, a signed payload — so nothing is
/// decoded and re-encoded on the way out.</summary>
internal sealed record BodyFile(string? Text, byte[]? Bytes)
{
    public bool IsBinary => Bytes is not null;

    /// <summary>Reads <paramref name="path"/>; <paramref name="source"/> names the option in the
    /// error when the file is missing.</summary>
    public static BodyFile Read(string path, string source)
    {
        if (!File.Exists(path)) throw new CliDataException($"Body file not found: {path} (from {source})");
        return FromBytes(File.ReadAllBytes(path));
    }

    internal static BodyFile FromBytes(byte[] bytes)
    {
        // A byte-order mark says what text this is — Windows PowerShell 5.1's `>` writes UTF-16
        // with one — so such a file is decoded as text, exactly as it was before bodies could be binary.
        if (BomEncoding(bytes) is { } bom)
        {
            try { return new BodyFile(bom.Encoding.GetString(bytes, bom.Length, bytes.Length - bom.Length), null); }
            catch (DecoderFallbackException) { /* a BOM on something that is not that text: bytes */ }
        }
        // A NUL is never part of a text body anyone means to send as text, even where it decodes.
        if (Array.IndexOf(bytes, (byte)0) < 0)
        {
            try
            {
                string text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
                return new BodyFile(text.Length > 0 && text[0] == '\uFEFF' ? text[1..] : text, null);
            }
            catch (DecoderFallbackException) { /* not UTF-8: sent as bytes */ }
        }
        return new BodyFile(null, bytes);
    }

    private static (Encoding Encoding, int Length)? BomEncoding(byte[] b)
    {
        static Encoding Strict(Encoding e) => Encoding.GetEncoding(e.CodePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        if (b.Length >= 4 && b[0] == 0xFF && b[1] == 0xFE && b[2] == 0 && b[3] == 0) return (Strict(Encoding.UTF32), 4);
        if (b.Length >= 4 && b[0] == 0 && b[1] == 0 && b[2] == 0xFE && b[3] == 0xFF) return (Strict(new UTF32Encoding(true, true)), 4);
        if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE) return (Strict(Encoding.Unicode), 2);
        if (b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF) return (Strict(Encoding.BigEndianUnicode), 2);
        return null;
    }

    /// <summary>For a <c>-d</c>/<c>--data</c> value: <c>@file</c> reads the file, anything else is
    /// the body itself. Null stays null.</summary>
    public static BodyFile? FromDataOption(string? data, string option = "-d")
    {
        if (data is null) return null;
        return data.Length > 1 && data[0] == '@' ? Read(data[1..], $"{option} {data}") : new BodyFile(data, null);
    }
}
