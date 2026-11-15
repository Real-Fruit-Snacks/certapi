using System.Text;
using System.Text.RegularExpressions;

namespace ApiTester.Core;

/// <summary>Expands <c>{{name}}</c> tokens in request fields from the active environment's
/// variables. Unknown tokens are left intact and reported so the user can see what's missing.
/// <para>The <c>env:</c> namespace — <c>{{env:NAME}}</c> — reads an environment variable instead,
/// which is how a secret reaches a request without ever being stored: nothing in the workspace,
/// nothing in an exported file, nothing in source control. A workspace variable of that exact name
/// still wins, so a saved value can override the ambient one deliberately.</para></summary>
public static partial class VariableResolver
{
    // A property over the generated method (which keeps its own instance), not a static field: a
    // field would be set up by the type's static constructor on the first call of any method here,
    // the "{{"-less fast path included, and that is the cost the fast path exists to avoid.
    private static Regex Token => TokenRegex();
    [GeneratedRegex(@"\{\{\s*([^{}]*?)\s*\}\}", RegexOptions.None)]
    private static partial Regex TokenRegex();

    /// <summary>The prefix that routes a token to the process environment rather than the
    /// workspace. Matched case-insensitively — <c>{{env:HOME}}</c> and <c>{{ENV:HOME}}</c> are the
    /// same token — while the variable NAME after it keeps whatever case the platform uses.</summary>
    public const string EnvironmentPrefix = "env:";

    public static (string Result, IReadOnlyList<string> Unresolved) Resolve(
        string template, IReadOnlyDictionary<string, string> vars) =>
        Resolve(template, vars, environment: null);

    /// <param name="environment">Where <c>{{env:NAME}}</c> is read from. Null — every ordinary call
    /// site — uses the process's own environment; a test supplies its own so it never has to mutate
    /// global state that a parallel run shares.</param>
    public static (string Result, IReadOnlyList<string> Unresolved) Resolve(
        string template, IReadOnlyDictionary<string, string> vars, Func<string, string?>? environment)
    {
        if (string.IsNullOrEmpty(template)) return (template ?? "", Array.Empty<string>());
        // Nothing to resolve without "{{" — the case for most URLs, headers and bodies. Checked
        // first because the first use of any generated expression in this assembly sets up shared
        // search tables for all of them, about 15 ms that a run with no variables need not pay.
        if (!template.Contains("{{", StringComparison.Ordinal)) return (template, Array.Empty<string>());

        var lookup = environment ?? Environment.GetEnvironmentVariable;
        var unresolved = new List<string>();
        var result = Token.Replace(template, m =>
        {
            var key = m.Groups[1].Value;
            if (key.Length == 0) return m.Value;               // leave "{{}}" untouched

            // A workspace variable wins even for an env:-prefixed name, so someone who genuinely
            // saved a variable called "env:FOO" is not overruled by the namespace.
            if (vars.TryGetValue(key, out var v)) return v;

            if (key.StartsWith(EnvironmentPrefix, StringComparison.OrdinalIgnoreCase))
            {
                var name = key[EnvironmentPrefix.Length..].Trim();
                // An empty name ("{{env:}}") resolves nothing and is reported like any other miss,
                // rather than silently expanding to the whole environment or to blank.
                if (name.Length > 0 && lookup(name) is { } value) return value;
                if (!unresolved.Contains(key)) unresolved.Add(key);
                return m.Value;
            }

            if (!unresolved.Contains(key)) unresolved.Add(key);
            return m.Value;                                    // leave "{{unknown}}" intact
        });
        return (result, unresolved);
    }

    /// <summary>Apply <paramref name="escape"/> to every span *outside* a <c>{{variable}}</c> token,
    /// leaving the tokens themselves verbatim. Exports use this: an exported artifact is a template
    /// someone fills in later, so its tokens must survive escaping intact.</summary>
    public static string EscapeOutsideTokens(string value, Func<string, string> escape)
    {
        if (string.IsNullOrEmpty(value)) return value ?? "";

        var sb = new StringBuilder();
        int last = 0;
        foreach (Match m in Token.Matches(value))
        {
            sb.Append(escape(value[last..m.Index]));
            sb.Append(m.Value);
            last = m.Index + m.Length;
        }
        sb.Append(escape(value[last..]));
        return sb.ToString();
    }
}
