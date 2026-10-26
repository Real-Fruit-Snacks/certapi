namespace ApiTester.Cli;

/// <summary>Tiny argument reader: commands consume the options they know, then call
/// <see cref="Positionals"/>, which rejects anything option-shaped that is left over.</summary>
public sealed class Args
{
    private readonly string?[] _tokens;   // consumed slots become null

    /// <summary>"--name=value" is split into two tokens up front, so every reader below sees the
    /// one spelling. Only long options qualify, and only when the part before '=' looks like an
    /// option name — a positional such as "a=b" or a header value is left alone. "--" on its own
    /// ends option parsing: everything after it is positional, which is the only way to pass a
    /// positional that starts with a dash.</summary>
    public Args(IReadOnlyList<string> tokens)
    {
        var split = new List<string?>(tokens.Count);
        int literalFrom = int.MaxValue;
        foreach (var t in tokens)
        {
            if (literalFrom != int.MaxValue) { split.Add(t); continue; }
            if (t == "--") { literalFrom = split.Count; continue; }
            int eq = t.IndexOf('=');
            if (eq > 2 && t.StartsWith("--", StringComparison.Ordinal) &&
                t[2..eq].All(c => char.IsLetterOrDigit(c) || c == '-' || c == '.'))
            {
                split.Add(t[..eq]);
                split.Add(t[(eq + 1)..]);
                continue;
            }
            split.Add(t);
        }
        _tokens = split.ToArray();
        _literalFrom = literalFrom;   // tokens from here on are never option-shaped
    }

    private readonly int _literalFrom;
    private readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);

    private bool IsOptionShaped(int index, string token) =>
        index < _literalFrom && token.StartsWith('-') && token.Length > 1 && !char.IsDigit(token[1]);

    /// <summary>Consume one occurrence of a flag (name match is case-insensitive). A repeated,
    /// unconsumed occurrence is later rejected by <see cref="Positionals"/> as an unknown option.</summary>
    public bool Flag(params string[] names)
    {
        for (int i = 0; i < _tokens.Length; i++)
            if (_tokens[i] is { } t && names.Contains(t, StringComparer.OrdinalIgnoreCase))
            {
                _tokens[i] = null;
                _seen.Add(t);
                return true;
            }
        return false;
    }

    /// <summary>As <see cref="Flag"/>, but distinguishing "not given" (null) from "given" (true).
    /// That distinction is what lets a configuration profile supply a boolean default which an
    /// explicitly typed flag still overrides: <c>args.FlagOrNull("--insecure") ?? profile?.Insecure
    /// ?? false</c> reads exactly as the precedence rule is written.</summary>
    public bool? FlagOrNull(params string[] names) => Flag(names) ? true : null;

    /// <summary>Consume one option and its value (name match is case-insensitive); null when absent.
    /// An option present without a value is a usage error.</summary>
    public string? Value(params string[] names)
    {
        for (int i = 0; i < _tokens.Length; i++)
        {
            if (_tokens[i] is not { } t || !names.Contains(t, StringComparer.OrdinalIgnoreCase)) continue;
            if (i + 1 >= _tokens.Length || _tokens[i + 1] is null)
                throw new CliUsageException($"Option {t} needs a value.");
            var value = _tokens[i + 1]!;
            // "--cert --insecure" is a missing value, not a certificate called "--insecure". Only
            // a long-option shape is refused: a single-dash value ("-d -1", "-d -body") has always
            // been accepted adjacent to its option and still is.
            if (i + 1 < _literalFrom && value.StartsWith("--", StringComparison.Ordinal) && value.Length > 2)
                throw new CliUsageException($"Option {t} needs a value, but the next token is '{value}' (write {t}=... if the value really starts with a dash).");
            _tokens[i] = _tokens[i + 1] = null;
            _seen.Add(t);
            return value;
        }
        return null;
    }

    /// <summary>Consume an option whose value is optional: "--cors" alone, or "--cors a,b". Returns
    /// false when the option is absent; true when present, with <paramref name="value"/> set to the
    /// following token when there is one that is not itself option-shaped.</summary>
    public bool OptionalValue(out string? value, params string[] names)
    {
        value = null;
        for (int i = 0; i < _tokens.Length; i++)
        {
            if (_tokens[i] is not { } t || !names.Contains(t, StringComparer.OrdinalIgnoreCase)) continue;
            _tokens[i] = null;
            // The next token belongs to this option only when it could not be an option itself —
            // the same shape rule Positionals() uses — so "--cors --insecure" leaves --insecure be.
            if (i + 1 < _tokens.Length && _tokens[i + 1] is { } next && !IsOptionShaped(i + 1, next))
            {
                value = next;
                _tokens[i + 1] = null;
            }
            return true;
        }
        return false;
    }

    /// <summary>A comma-list option that may also be repeated: <c>--noproxy a,b --noproxy c</c> is
    /// <c>a,b,c</c>. Null when absent. For the options whose value is already a list, where a second
    /// occurrence can only mean "and these too" — unlike a single-value option, where it is a
    /// mistake and refused.</summary>
    public string? ListValue(params string[] names)
    {
        var all = Values(names);
        return all.Count == 0 ? null : string.Join(",", all);
    }

    /// <summary>Consume every occurrence of a repeatable option, in order.</summary>
    public List<string> Values(params string[] names)
    {
        var all = new List<string>();
        while (Value(names) is { } v) all.Add(v);
        return all;
    }

    /// <summary>The remaining non-option tokens; call last. Unknown options are usage errors.</summary>
    public List<string> Positionals()
    {
        var positionals = new List<string>();
        for (int i = 0; i < _tokens.Length; i++)
        {
            var t = _tokens[i];
            if (t is null) continue;
            if (IsOptionShaped(i, t))
                throw new CliUsageException(UnsupportedOptions.Explain(t) is { } why
                    ? $"Option '{t}' does not exist. {why}"
                    : _seen.Contains(t)
                        ? $"Option '{t}' was given more than once, and this command takes it once."
                        : $"Unknown option '{t}'.");
            positionals.Add(t);
        }
        return positionals;
    }
}
