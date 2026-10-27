using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace ApiTester.Core;

/// <summary>`serve --rewrite-body`: upstream URLs written into a page — links, script sources,
/// form actions, a meta refresh, a JSON config's API base — turned into URLs through the gateway,
/// so a site that names its own host keeps the browser on the certificate. The same mapping as a
/// Location (<see cref="BrowserRewriter.GatewayUrlFor"/>), applied to text.</summary>
public static class BodyRewriter
{
    /// <summary>Bodies larger than this are relayed as they are: rewriting needs the whole body in
    /// memory, and a download that size is not a page.</summary>
    public const int MaxBytes = 32 * 1024 * 1024;

    // scheme://authority or //authority, also with JSON's escaped slashes (https:\/\/host). A bare
    // //authority only counts right after a quote, '(' or '=', where it is a URL and not a comment.
    private static readonly Regex Origin = new(
        @"(?:(?<scheme>https?:)|(?<=[""'(=]))(?<slashes>//|\\/\\/)(?<authority>[A-Za-z0-9](?:[A-Za-z0-9.\-]*[A-Za-z0-9])?(?::\d{1,5})?)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Whether a response with this Content-Type is text this rewriter reads. JSON and XML
    /// only with a Content-Length: without one they may be a stream (a watch, a feed) that must
    /// reach the client as it arrives, and buffering it would hold every event until it ends.</summary>
    public static bool Applies(string? contentType, bool hasLength = true)
    {
        if (string.IsNullOrWhiteSpace(contentType)) return false;
        string media = contentType.Split(';')[0].Trim().ToLowerInvariant();
        bool data = media.Contains("json", StringComparison.Ordinal) || media.EndsWith("xml", StringComparison.Ordinal);
        if (data && media != "image/svg+xml" && media != "application/xhtml+xml" && !hasLength) return false;
        return media is "text/html" or "application/xhtml+xml" or "text/css" or "text/javascript"
                   or "application/javascript" or "application/x-javascript" or "application/ecmascript"
                   or "application/json" or "application/manifest+json" or "text/xml" or "application/xml"
                   or "image/svg+xml"
               || media.EndsWith("+json", StringComparison.Ordinal) || media.EndsWith("+xml", StringComparison.Ordinal);
    }

    /// <summary>The rewritten body, always uncompressed UTF-8, or null when nothing changed or the
    /// body could not be read (an unknown encoding, a charset other than UTF-8, invalid UTF-8) —
    /// in which case the caller relays the original bytes and headers untouched.</summary>
    public static byte[]? Rewrite(byte[] body, string? contentEncoding, string? contentType,
                                  GatewayRoute route, BrowserOptions options)
    {
        if (Charset(contentType) is { } charset && charset is not ("utf-8" or "utf8" or "us-ascii"))
            return null;
        byte[]? plain = Decode(body, contentEncoding);
        if (plain is null) return null;
        string text;
        try { text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(plain); }
        catch (DecoderFallbackException) { return null; }

        bool changed = false;
        string result = Origin.Replace(text, m =>
        {
            string scheme = m.Groups["scheme"].Success ? m.Groups["scheme"].Value : route.Upstream.Scheme + ":";
            if (!Uri.TryCreate(scheme + "//" + m.Groups["authority"].Value + "/", UriKind.Absolute, out var target))
                return m.Value;
            if (BrowserRewriter.GatewayUrlFor(target, route, options) is not { } through) return m.Value;
            string replacement = through.EndsWith('/') ? through[..^1] : through;
            if (m.Groups["slashes"].Value != "//") replacement = replacement.Replace("/", "\\/");
            changed = true;
            return replacement;
        });
        return changed ? new UTF8Encoding(false).GetBytes(result) : null;
    }

    private static string? Charset(string? contentType)
    {
        if (contentType is null) return null;
        foreach (var part in contentType.Split(';').Skip(1))
        {
            var kv = part.Split('=', 2);
            if (kv.Length == 2 && kv[0].Trim().Equals("charset", StringComparison.OrdinalIgnoreCase))
                return kv[1].Trim().Trim('"').ToLowerInvariant();
        }
        return null;
    }

    /// <summary>The body without its Content-Encoding, or null for an encoding not read here (or
    /// more than one).</summary>
    internal static byte[]? Decode(byte[] body, string? contentEncoding)
    {
        string encoding = (contentEncoding ?? "").Trim().ToLowerInvariant();
        if (encoding is "" or "identity") return body;
        if (encoding.Contains(',')) return null;
        try
        {
            using var input = new MemoryStream(body);
            using Stream decoder = encoding switch
            {
                "gzip" or "x-gzip" => new GZipStream(input, CompressionMode.Decompress),
                "deflate" => new ZLibStream(input, CompressionMode.Decompress),
                "br" => new BrotliStream(input, CompressionMode.Decompress),
                _ => Stream.Null
            };
            if (decoder == Stream.Null) return null;
            // Capped as the compressed body is: a small body that inflates to gigabytes is not a page.
            using var output = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = decoder.Read(chunk, 0, chunk.Length)) > 0)
            {
                output.Write(chunk, 0, read);
                if (output.Length > MaxBytes) return null;
            }
            return output.ToArray();
        }
        // Corrupt gzip/deflate throws InvalidDataException; corrupt brotli throws
        // InvalidOperationException ("Decoder ran into invalid data"). Either way the body is
        // relayed untouched rather than turned into a 502.
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or IOException) { return null; }
    }
}
