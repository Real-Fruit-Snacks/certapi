using System.Net.Http;
using System.Net.Http.Headers;

namespace ApiTester.Core;

/// <summary>Headers that describe the body (Content-Type, Content-Encoding, Content-Language, …)
/// belong on <see cref="HttpContent.Headers"/>: <see cref="HttpRequestHeaders.TryAddWithoutValidation(string, string?)"/>
/// refuses them and returns false, which every caller here used to ignore — so a forwarded
/// <c>Content-Encoding: gzip</c>, or a <c>-H "Content-Type: application/xml"</c>, silently vanished.</summary>
public static class ContentHeaders
{
    /// <summary>Adds each header to the request; the ones that describe a body are returned for
    /// <see cref="ApplyTo"/> once the content exists.</summary>
    public static List<KeyValuePair<string, string>> AddRequestHeaders(
        HttpRequestMessage message, IEnumerable<KeyValuePair<string, string>> headers)
    {
        var forContent = new List<KeyValuePair<string, string>>();
        foreach (var h in headers)
            if (!message.Headers.TryAddWithoutValidation(h.Key, h.Value))
                forContent.Add(h);
        return forContent;
    }

    /// <summary>Puts body headers on <paramref name="content"/>. Content-Length is never copied —
    /// the content frames itself — and a Content-Type is used only when the body does not already
    /// have one chosen for it (<paramref name="keepContentType"/>).</summary>
    public static void ApplyTo(HttpContent? content, IEnumerable<KeyValuePair<string, string>> headers, bool keepContentType)
    {
        if (content is null) return;   // no entity, so nothing for them to describe
        foreach (var h in headers)
        {
            if (h.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) continue;
            if (h.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
            {
                if (keepContentType) continue;
                content.Headers.Remove("Content-Type");
                if (MediaTypeHeaderValue.TryParse(h.Value, out var mt)) content.Headers.ContentType = mt;
                else content.Headers.TryAddWithoutValidation("Content-Type", h.Value);
                continue;
            }
            content.Headers.Remove(h.Key);
            content.Headers.TryAddWithoutValidation(h.Key, h.Value);
        }
    }
}
