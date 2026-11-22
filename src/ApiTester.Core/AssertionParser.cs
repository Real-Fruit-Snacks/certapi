namespace ApiTester.Core;

/// <summary>Parses a compact command-line assertion expression into an <see cref="AssertionRule"/>,
/// for <c>certapi send --assert</c>. Grammar:
/// <code>&lt;target&gt; [path] &lt;op&gt; [value]</code>
/// <list type="bullet">
///   <item>targets: <c>status</c>, <c>time</c>, <c>header &lt;name&gt;</c>, <c>body &lt;jsonpath&gt;</c>, <c>body-text</c>,
///     and <c>cors &lt;origin&gt; allowed|credentials|blocked</c> (no operator; <c>!=</c> may precede the outcome)</item>
///   <item>ops: <c>==</c> <c>!=</c> <c>contains</c> <c>matches</c> <c>exists</c> <c>!exists</c> <c>&lt;</c> <c>&gt;</c>
///     <c>schema</c> (the value is a JSON Schema, or <c>@file.json</c>; <c>body $ schema …</c> checks the whole body)
///     (aliases: <c>equals</c>, <c>notequals</c>, <c>lt</c>, <c>gt</c>, <c>missing</c>)</item>
/// </list>
/// Examples: <c>status == 200</c>, <c>time &lt; 500</c>, <c>header Content-Type contains json</c>,
/// <c>body data.id exists</c>, <c>body-text matches "id":\s*\d+</c>. Throws
/// <see cref="FormatException"/> with a helpful message on malformed input.</summary>
public static class AssertionParser
{
    public static AssertionRule Parse(string expr)
    {
        if (string.IsNullOrWhiteSpace(expr))
            throw new FormatException("empty assertion");

        var parts = expr.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts[0].Equals("cors", StringComparison.OrdinalIgnoreCase)) return ParseCors(expr, parts);
        var target = ParseTarget(parts[0], out bool needsPath);

        int i = 1;
        string path = "";
        if (needsPath)
        {
            if (parts.Length < 2)
                throw new FormatException($"'{parts[0]}' needs a name/path, e.g. \"{parts[0].ToLowerInvariant()} <name> exists\"");
            path = parts[1];
            i = 2;
        }

        if (parts.Length <= i)
            throw new FormatException($"assertion '{expr}' is missing an operator (== != contains matches exists !exists < >)");

        var op = ParseOp(parts[i]);
        string value = i + 1 < parts.Length ? string.Join(' ', parts[(i + 1)..]) : "";
        if (op == AssertOp.MatchesSchema)
        {
            // The schema exactly as typed (whitespace inside strings matters), or read from a file.
            int at = IndexAfterToken(expr, parts, i);
            value = at < expr.Length ? expr[at..].Trim() : "";
            if (value.StartsWith('@'))
            {
                string file = value[1..].Trim().Trim('"');
                try { value = File.ReadAllText(file); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new FormatException($"cannot read the schema file '{file}': {ex.Message}"); }
            }
            if (value.Length > 0)
                try { using var _ = System.Text.Json.JsonDocument.Parse(value); }
                catch (System.Text.Json.JsonException ex) { throw new FormatException($"the schema in '{expr}' is not valid JSON: {ex.Message}"); }
        }

        bool needsValue = op is not (AssertOp.Exists or AssertOp.NotExists);
        if (needsValue && value.Length == 0)
            throw new FormatException($"assertion '{expr}' is missing a value after '{parts[i]}'");

        return new AssertionRule { Enabled = true, Target = target, Path = path, Op = op, Value = value };
    }

    private static AssertionRule ParseCors(string expr, string[] parts)
    {
        // cors <origin> [==|!=] allowed|credentials|blocked
        // cors <origin> [!=] exposes <Header-Name>
        // cors <origin> !exposes <Header-Name>
        if (parts.Length < 3)
            throw new FormatException($"'{expr}': write cors <origin> allowed, cors <origin> credentials, cors <origin> blocked or cors <origin> exposes <header>");
        string origin = parts[1];
        if (origin != "null" && (!Uri.TryCreate(origin, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https")))
            throw new FormatException($"'{expr}': '{origin}' is not an origin (scheme://host[:port], or null)");
        if (origin != "null") origin = new Uri(origin).GetLeftPart(UriPartial.Authority);
        int i = 2;
        var op = AssertOp.Equals;
        if (parts[i] is "==" or "=" or "is") i++;
        else if (parts[i] is "!=" or "not") { op = AssertOp.NotEquals; i++; }
        if (i >= parts.Length)
            throw new FormatException($"'{expr}': the outcome is allowed, credentials, blocked or exposes <header>");
        string outcome = parts[i].ToLowerInvariant();
        if (outcome is "exposes" or "!exposes")
        {
            if (outcome == "!exposes") op = op == AssertOp.NotEquals ? AssertOp.Equals : AssertOp.NotEquals;
            if (parts.Length != i + 2 || string.IsNullOrWhiteSpace(parts[i + 1]))
                throw new FormatException($"'{expr}': write cors <origin> exposes <Header-Name>");
            return new AssertionRule { Enabled = true, Target = AssertTarget.Cors, Path = origin, Op = op, Value = "exposes " + parts[i + 1].Trim() };
        }
        if (parts.Length > i + 1)
            throw new FormatException($"'{expr}': the outcome is allowed, credentials, blocked or exposes <header>");
        if (outcome is not ("allowed" or "credentials" or "blocked"))
            throw new FormatException($"'{expr}': '{parts[i]}' is not an outcome; use allowed, credentials, blocked or exposes <header>");
        return new AssertionRule { Enabled = true, Target = AssertTarget.Cors, Path = origin, Op = op, Value = outcome };
    }

    private static AssertTarget ParseTarget(string t, out bool needsPath)
    {
        needsPath = false;
        switch (t.ToLowerInvariant())
        {
            case "status": return AssertTarget.Status;
            case "time": return AssertTarget.Time;
            case "header": needsPath = true; return AssertTarget.Header;
            case "body": needsPath = true; return AssertTarget.Body;
            case "body-text": case "bodytext": case "text": return AssertTarget.BodyText;
            default:
                throw new FormatException($"unknown assertion target '{t}' (expected status, time, header, body, body-text or cors)");
        }
    }

    private static AssertOp ParseOp(string o) => o.ToLowerInvariant() switch
    {
        "==" or "=" or "eq" or "equals" => AssertOp.Equals,
        "!=" or "ne" or "notequals" => AssertOp.NotEquals,
        "contains" or "has" => AssertOp.Contains,
        "matches" or "~" or "regex" => AssertOp.Matches,
        "exists" or "present" => AssertOp.Exists,
        "!exists" or "notexists" or "missing" or "absent" => AssertOp.NotExists,
        "<" or "lt" => AssertOp.LessThan,
        ">" or "gt" => AssertOp.GreaterThan,
        "schema" or "matches-schema" => AssertOp.MatchesSchema,
        _ => throw new FormatException($"unknown assertion operator '{o}' (== != contains matches exists !exists < > schema)")
    };

    /// <summary>Where the text after the <paramref name="opIndex"/>th token starts in the original
    /// expression.</summary>
    private static int IndexAfterToken(string expr, string[] parts, int opIndex)
    {
        int pos = 0;
        for (int k = 0; k <= opIndex; k++)
        {
            pos = expr.IndexOf(parts[k], pos, StringComparison.Ordinal);
            pos += parts[k].Length;
        }
        return pos;
    }
}
