using System.Text.Json;

namespace ApiTester.Core;

/// <summary>One capture rule in UI-free terms: write <see cref="Variable"/> from either a response
/// header (<see cref="FromHeader"/>) or a JSON body path (<see cref="Path"/>).</summary>
public sealed record CaptureSpec(string Variable, bool FromHeader, string Path);

/// <summary>Extracts a single string value from a response for a capture rule.</summary>
public static class ResponseCapture
{
    public static (string? Value, string? Error) Extract(
        CaptureSpec rule, byte[] body, string? contentType,
        IReadOnlyList<KeyValuePair<string, string>> headers)
    {
        if (rule.FromHeader)
        {
            foreach (var h in headers)
                if (h.Key.Equals(rule.Path, StringComparison.OrdinalIgnoreCase))
                    return (h.Value, null);
            return (null, $"header '{rule.Path}' not found");
        }

        JsonDocument doc;
        try { doc = JsonDocument.Parse(Utf8Json.Body(body)); }
        catch (JsonException) { return (null, "response body is not JSON"); }

        using (doc)
        {
            var found = JsonPath.Evaluate(doc.RootElement, rule.Path);
            if (found is not { } el) return (null, $"no value at '{rule.Path}'");
            return el.ValueKind switch
            {
                JsonValueKind.String => (el.GetString(), null),
                JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => (el.GetRawText(), null),
                _ => (null, $"no string value at '{rule.Path}'")
            };
        }
    }
}
