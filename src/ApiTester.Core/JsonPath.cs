using System.Text.Json;

namespace ApiTester.Core;

/// <summary>A minimal dotted JSON path: segments split on '.', each `name`, `name[index]`, or
/// `[index]`. Returns null on any missing segment, wrong kind, or out-of-range index.</summary>
public static class JsonPath
{
    public static JsonElement? Evaluate(JsonElement root, string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        JsonElement current = root;
        ReadOnlySpan<char> remaining = path.AsSpan();

        while (remaining.Length > 0)
        {
            int dot = remaining.IndexOf('.');
            ReadOnlySpan<char> segment;
            if (dot < 0)
            {
                segment = remaining;
                remaining = ReadOnlySpan<char>.Empty;
            }
            else
            {
                segment = remaining[..dot];
                remaining = remaining[(dot + 1)..];
            }
            if (segment.IsEmpty) continue;

            ReadOnlySpan<char> s = segment.Trim();

            // Split "name[1][2]" into a leading name (optional) and any trailing [index] parts.
            int bracket = s.IndexOf('[');
            ReadOnlySpan<char> name = bracket < 0 ? s : s[..bracket];
            if (name.Length > 0)
            {
                if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(name, out current))
                    return null;
            }

            if (bracket >= 0)
            {
                ReadOnlySpan<char> rest = s[bracket..];
                bool anyIndex = false;
                while (rest.Length > 0)
                {
                    if (rest[0] == '[')
                    {
                        rest = rest[1..];
                        if (rest.Length == 0) break;
                        if (rest[0] == '[') continue;
                    }
                    int nextOpen = rest.IndexOf('[');
                    ReadOnlySpan<char> idxToken = nextOpen < 0 ? rest : rest[..nextOpen];
                    rest = nextOpen < 0 ? ReadOnlySpan<char>.Empty : rest[nextOpen..];
                    if (idxToken.IsEmpty) continue;

                    anyIndex = true;
                    if (idxToken[^1] != ']') return null;   // unclosed like "name[0"
                    ReadOnlySpan<char> t = idxToken.TrimEnd(']');
                    if (!int.TryParse(t, out int index) || current.ValueKind != JsonValueKind.Array) return null;
                    if (index < 0 || index >= current.GetArrayLength()) return null;
                    current = current[index];
                }
                if (!anyIndex) return null;   // "name[" with nothing after
            }
        }
        return current;
    }
}
