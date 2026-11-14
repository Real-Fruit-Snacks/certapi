using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ApiTester.Core;

/// <summary>Validates a JSON value against a JSON Schema, for the <c>matches schema</c> assertion.
/// It covers the keywords response schemas use in practice — <c>type</c>, <c>enum</c>,
/// <c>const</c>, <c>properties</c>, <c>required</c>, <c>additionalProperties</c>,
/// <c>patternProperties</c>, <c>items</c>, <c>prefixItems</c>, <c>minItems</c>,
/// <c>maxItems</c>, <c>uniqueItems</c>, <c>contains</c>, <c>minProperties</c>,
/// <c>maxProperties</c>, <c>minLength</c>, <c>maxLength</c>, <c>pattern</c>, <c>minimum</c>,
/// <c>maximum</c>, <c>exclusiveMinimum</c>, <c>exclusiveMaximum</c>, <c>multipleOf</c>,
/// <c>allOf</c>, <c>anyOf</c>, <c>oneOf</c>, <c>not</c>, <c>if</c>/<c>then</c>/<c>else</c>,
/// boolean schemas, and <c>$ref</c> to <c>#/$defs/…</c>, <c>#/definitions/…</c> or
/// <c>#/components/schemas/…</c> in the same document — plus OpenAPI 3.0's
/// <c>nullable: true</c>. Other keywords (<c>format</c>, annotations) are not checked.
/// Self-contained, so the product takes on no schema library.</summary>
public static class JsonSchemaCheck
{
    private const int MaxDepth = 64;

    /// <summary>The first way <paramref name="value"/> fails <paramref name="schema"/>, as
    /// "<c>at $.path: reason</c>", or null when it matches.</summary>
    public static string? FirstError(JsonElement schema, JsonElement value) =>
        Check(schema, value, "$", schema, 0);

    /// <summary>Parses <paramref name="schemaText"/> and checks <paramref name="value"/>.</summary>
    public static string? FirstError(string schemaText, JsonElement value)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(schemaText, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }); }
        catch (JsonException ex) { return "the schema is not valid JSON: " + ex.Message; }
        using (doc) return FirstError(doc.RootElement, value);
    }

    private static string? Check(JsonElement s, JsonElement v, string at, JsonElement root, int depth)
    {
        if (depth > MaxDepth) return $"at {at}: the schema nests or refers to itself too deeply";
        if (s.ValueKind == JsonValueKind.True) return null;
        if (s.ValueKind == JsonValueKind.False) return $"at {at}: no value is allowed here";
        if (s.ValueKind != JsonValueKind.Object) return null;

        if (s.TryGetProperty("$ref", out var reference) && reference.ValueKind == JsonValueKind.String)
        {
            if (Resolve(root, reference.GetString()!) is not { } target)
                return $"at {at}: $ref '{reference.GetString()}' does not point into this schema";
            if (Check(target, v, at, root, depth + 1) is { } refError) return refError;
        }

        bool nullable = s.TryGetProperty("nullable", out var n) && n.ValueKind == JsonValueKind.True;
        if (nullable && v.ValueKind == JsonValueKind.Null) return null;

        if (s.TryGetProperty("type", out var type))
        {
            var types = type.ValueKind == JsonValueKind.Array
                ? type.EnumerateArray().Where(t => t.ValueKind == JsonValueKind.String).Select(t => t.GetString()!).ToList()
                : type.ValueKind == JsonValueKind.String ? new List<string> { type.GetString()! } : new List<string>();
            if (types.Count > 0 && !types.Any(t => IsType(v, t)))
                return $"at {at}: expected {string.Join(" or ", types)}, got {Kind(v)}";
        }

        if (s.TryGetProperty("const", out var constant) && !JsonEqual(constant, v))
            return $"at {at}: expected {constant.GetRawText()}";
        if (s.TryGetProperty("enum", out var options) && options.ValueKind == JsonValueKind.Array &&
            !options.EnumerateArray().Any(o => JsonEqual(o, v)))
            return $"at {at}: {Short(v)} is not one of {Short(options)}";

        switch (v.ValueKind)
        {
            case JsonValueKind.Object:
                if (CheckObject(s, v, at, root, depth) is { } objectError) return objectError;
                break;
            case JsonValueKind.Array:
                if (CheckArray(s, v, at, root, depth) is { } arrayError) return arrayError;
                break;
            case JsonValueKind.String:
                if (CheckString(s, v.GetString()!, at) is { } stringError) return stringError;
                break;
            case JsonValueKind.Number:
                if (CheckNumber(s, v.GetDouble(), at) is { } numberError) return numberError;
                break;
        }

        if (s.TryGetProperty("allOf", out var all) && all.ValueKind == JsonValueKind.Array)
            foreach (var sub in all.EnumerateArray())
                if (Check(sub, v, at, root, depth + 1) is { } e) return e;
        if (s.TryGetProperty("anyOf", out var any) && any.ValueKind == JsonValueKind.Array &&
            !any.EnumerateArray().Any(sub => Check(sub, v, at, root, depth + 1) is null))
            return $"at {at}: matches none of the anyOf schemas";
        if (s.TryGetProperty("oneOf", out var one) && one.ValueKind == JsonValueKind.Array)
        {
            int matches = one.EnumerateArray().Count(sub => Check(sub, v, at, root, depth + 1) is null);
            if (matches != 1) return $"at {at}: matches {matches} of the oneOf schemas, not exactly one";
        }
        if (s.TryGetProperty("not", out var not) && Check(not, v, at, root, depth + 1) is null)
            return $"at {at}: matches the schema under 'not'";
        if (s.TryGetProperty("if", out var condition))
        {
            bool holds = Check(condition, v, at, root, depth + 1) is null;
            if (holds && s.TryGetProperty("then", out var then) && Check(then, v, at, root, depth + 1) is { } te) return te;
            if (!holds && s.TryGetProperty("else", out var otherwise) && Check(otherwise, v, at, root, depth + 1) is { } ee) return ee;
        }
        return null;
    }

    private static string? CheckObject(JsonElement s, JsonElement v, string at, JsonElement root, int depth)
    {
        if (s.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.Array)
            foreach (var r in required.EnumerateArray())
                if (r.ValueKind == JsonValueKind.String && !v.TryGetProperty(r.GetString()!, out _))
                    return $"at {at}: '{r.GetString()}' is required";

        int count = v.EnumerateObject().Count();
        if (Int(s, "minProperties") is { } minP && count < minP) return $"at {at}: has {count} properties, fewer than {minP}";
        if (Int(s, "maxProperties") is { } maxP && count > maxP) return $"at {at}: has {count} properties, more than {maxP}";

        s.TryGetProperty("properties", out var props);
        s.TryGetProperty("patternProperties", out var patternProps);
        s.TryGetProperty("additionalProperties", out var additional);
        foreach (var p in v.EnumerateObject())
        {
            string child = $"{at}.{p.Name}";
            bool covered = false;
            if (props.ValueKind == JsonValueKind.Object && props.TryGetProperty(p.Name, out var ps))
            {
                covered = true;
                if (Check(ps, p.Value, child, root, depth + 1) is { } e) return e;
            }
            if (patternProps.ValueKind == JsonValueKind.Object)
                foreach (var pp in patternProps.EnumerateObject())
                    if (SafeMatch(pp.Name, p.Name))
                    {
                        covered = true;
                        if (Check(pp.Value, p.Value, child, root, depth + 1) is { } e) return e;
                    }
            if (!covered && additional.ValueKind is JsonValueKind.False)
                return $"at {at}: '{p.Name}' is not allowed (additionalProperties is false)";
            if (!covered && additional.ValueKind == JsonValueKind.Object && Check(additional, p.Value, child, root, depth + 1) is { } ae)
                return ae;
        }
        return null;
    }

    private static string? CheckArray(JsonElement s, JsonElement v, string at, JsonElement root, int depth)
    {
        int length = v.GetArrayLength();
        if (Int(s, "minItems") is { } min && length < min) return $"at {at}: has {length} items, fewer than {min}";
        if (Int(s, "maxItems") is { } max && length > max) return $"at {at}: has {length} items, more than {max}";

        int prefix = 0;
        if (s.TryGetProperty("prefixItems", out var prefixItems) && prefixItems.ValueKind == JsonValueKind.Array)
        {
            var schemas = prefixItems.EnumerateArray().ToList();
            prefix = schemas.Count;
            for (int i = 0; i < Math.Min(prefix, length); i++)
                if (Check(schemas[i], v[i], $"{at}[{i}]", root, depth + 1) is { } e) return e;
        }
        if (s.TryGetProperty("items", out var items))
        {
            if (items.ValueKind == JsonValueKind.Array)   // draft-07 tuple form
            {
                var schemas = items.EnumerateArray().ToList();
                for (int i = 0; i < Math.Min(schemas.Count, length); i++)
                    if (Check(schemas[i], v[i], $"{at}[{i}]", root, depth + 1) is { } e) return e;
            }
            else
                for (int i = prefix; i < length; i++)
                    if (Check(items, v[i], $"{at}[{i}]", root, depth + 1) is { } e) return e;
        }
        if (s.TryGetProperty("contains", out var contains) &&
            !v.EnumerateArray().Any(item => Check(contains, item, at, root, depth + 1) is null))
            return $"at {at}: no item matches 'contains'";
        if (s.TryGetProperty("uniqueItems", out var unique) && unique.ValueKind == JsonValueKind.True)
        {
            var list = v.EnumerateArray().ToList();
            for (int i = 0; i < list.Count; i++)
                for (int j = i + 1; j < list.Count; j++)
                    if (JsonEqual(list[i], list[j])) return $"at {at}: items {i} and {j} are equal (uniqueItems)";
        }
        return null;
    }

    private static string? CheckString(JsonElement s, string text, string at)
    {
        int length = new StringInfo(text).LengthInTextElements;
        if (Int(s, "minLength") is { } min && length < min) return $"at {at}: is {length} characters, fewer than {min}";
        if (Int(s, "maxLength") is { } max && length > max) return $"at {at}: is {length} characters, more than {max}";
        if (s.TryGetProperty("pattern", out var pattern) && pattern.ValueKind == JsonValueKind.String && !SafeMatch(pattern.GetString()!, text))
            return $"at {at}: does not match the pattern {pattern.GetString()}";
        return null;
    }

    private static string? CheckNumber(JsonElement s, double x, string at)
    {
        if (Num(s, "minimum") is { } min && x < min) return $"at {at}: {x.ToString(CultureInfo.InvariantCulture)} is less than {min.ToString(CultureInfo.InvariantCulture)}";
        if (Num(s, "maximum") is { } max && x > max) return $"at {at}: {x.ToString(CultureInfo.InvariantCulture)} is more than {max.ToString(CultureInfo.InvariantCulture)}";
        // Draft 2020-12 writes exclusive bounds as numbers; OpenAPI 3.0 as booleans beside minimum/maximum.
        if (s.TryGetProperty("exclusiveMinimum", out var exMin))
        {
            if (exMin.ValueKind == JsonValueKind.Number && x <= exMin.GetDouble()) return $"at {at}: must be more than {exMin.GetRawText()}";
            if (exMin.ValueKind == JsonValueKind.True && Num(s, "minimum") is { } m && x <= m) return $"at {at}: must be more than {m.ToString(CultureInfo.InvariantCulture)}";
        }
        if (s.TryGetProperty("exclusiveMaximum", out var exMax))
        {
            if (exMax.ValueKind == JsonValueKind.Number && x >= exMax.GetDouble()) return $"at {at}: must be less than {exMax.GetRawText()}";
            if (exMax.ValueKind == JsonValueKind.True && Num(s, "maximum") is { } m && x >= m) return $"at {at}: must be less than {m.ToString(CultureInfo.InvariantCulture)}";
        }
        if (Num(s, "multipleOf") is { } step && step > 0)
        {
            double q = x / step;
            if (Math.Abs(q - Math.Round(q)) > 1e-9) return $"at {at}: is not a multiple of {step.ToString(CultureInfo.InvariantCulture)}";
        }
        return null;
    }

    private static JsonElement? Resolve(JsonElement root, string reference)
    {
        if (reference == "#") return root;
        if (!reference.StartsWith("#/", StringComparison.Ordinal)) return null;
        JsonElement current = root;
        foreach (var raw in reference[2..].Split('/'))
        {
            string token = Uri.UnescapeDataString(raw).Replace("~1", "/").Replace("~0", "~");
            if (current.ValueKind == JsonValueKind.Object && current.TryGetProperty(token, out var next)) current = next;
            else if (current.ValueKind == JsonValueKind.Array && int.TryParse(token, out int i) && i >= 0 && i < current.GetArrayLength()) current = current[i];
            else return null;
        }
        return current;
    }

    private static bool IsType(JsonElement v, string type) => type switch
    {
        "object" => v.ValueKind == JsonValueKind.Object,
        "array" => v.ValueKind == JsonValueKind.Array,
        "string" => v.ValueKind == JsonValueKind.String,
        "number" => v.ValueKind == JsonValueKind.Number,
        "integer" => v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) && Math.Abs(d % 1) < double.Epsilon,
        "boolean" => v.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "null" => v.ValueKind == JsonValueKind.Null,
        _ => true
    };

    private static string Kind(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.Object => "object", JsonValueKind.Array => "array", JsonValueKind.String => "string",
        JsonValueKind.Number => "number", JsonValueKind.True or JsonValueKind.False => "boolean",
        JsonValueKind.Null => "null", _ => "nothing"
    };

    private static bool JsonEqual(JsonElement a, JsonElement b)
    {
        if (a.ValueKind == JsonValueKind.Number && b.ValueKind == JsonValueKind.Number) return a.GetDouble() == b.GetDouble();
        if (a.ValueKind != b.ValueKind) return false;
        switch (a.ValueKind)
        {
            case JsonValueKind.String: return a.GetString() == b.GetString();
            case JsonValueKind.Array:
                if (a.GetArrayLength() != b.GetArrayLength()) return false;
                return a.EnumerateArray().Zip(b.EnumerateArray()).All(p => JsonEqual(p.First, p.Second));
            case JsonValueKind.Object:
                var ap = a.EnumerateObject().ToList();
                if (ap.Count != b.EnumerateObject().Count()) return false;
                return ap.All(p => b.TryGetProperty(p.Name, out var bv) && JsonEqual(p.Value, bv));
            default: return true;   // true, false, null
        }
    }

    private static bool SafeMatch(string pattern, string input)
    {
        try { return Regex.IsMatch(input, pattern, RegexOptions.None, TimeSpan.FromSeconds(1)); }
        catch (ArgumentException) { return false; }
        catch (RegexMatchTimeoutException) { return false; }
    }

    private static long? Int(JsonElement s, string name) =>
        s.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.Number && e.TryGetInt64(out var n) ? n : null;

    private static double? Num(JsonElement s, string name) =>
        s.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.Number ? e.GetDouble() : null;

    private static string Short(JsonElement e)
    {
        string text = e.GetRawText();
        return text.Length <= 80 ? text : text[..77] + "...";
    }
}
