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
        var attached = new HashSet<int>();
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
                attached.Add(split.Count);   // this value was written --name=value: it is a value, whatever it looks like
                split.Add(t[(eq + 1)..]);
                continue;
            }
            split.Add(t);
        }
        _tokens = split.ToArray();
        _literalFrom = literalFrom;   // tokens from here on are never option-shaped
        _attached = attached;
    }

    private readonly int _literalFrom;
    private readonly HashSet<int> _attached;

    /// <summary>Where options can be: everything after a bare "--" is positional, whatever it looks like.</summary>
    private int OptionEnd => Math.Min(_tokens.Length, _literalFrom);
    private readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);
    // Every option name a command asked about, aliases included: what "did you mean" picks from.
    private readonly HashSet<string> _asked = new(StringComparer.Ordinal);

    /// <summary>curl spellings accepted wherever the option they stand for is accepted. They only
    /// apply where the command reads the canonical option, so <c>-k</c> means <c>--insecure</c> on
    /// every command that has <c>--insecure</c> and is unknown everywhere else.</summary>
    internal static readonly IReadOnlyDictionary<string, string[]> Aliases =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["--insecure"] = ["-k"],
            ["--include"] = ["-i"],
            ["--quiet"] = ["-s", "--silent"],
            ["--basic"] = ["-u", "--user"],
            ["--proxy"] = ["-x"],
            ["--proxy-user"] = ["-U"],
            ["--user-agent"] = ["-A"],
            ["--head"] = ["-I"],
            ["--fail"] = ["-f"],
            ["--timeout"] = ["--max-time"],
            ["--ca-file"] = ["--cacert"],
            ["--key-file"] = ["--key"],
        };

    /// <summary>Single-letter options are case-sensitive (<c>-H</c> is a header, <c>-h</c> is help;
    /// <c>-X</c> is the method, <c>-x</c> the proxy; <c>-i</c> includes headers, <c>-I</c> is HEAD).
    /// Long options are not.</summary>
    private static bool NameMatches(string token, string name) =>
        name.Length == 2 && name[0] == '-' && name[1] != '-'
            ? string.Equals(token, name, StringComparison.Ordinal)
            : string.Equals(token, name, StringComparison.OrdinalIgnoreCase);

    /// <summary>The names a call asked for, plus the curl aliases of each.</summary>
    private string[] Expand(string[] names)
    {
        var all = new List<string>(names);
        foreach (var n in names)
            if (Aliases.TryGetValue(n, out var more)) all.AddRange(more);
        foreach (var n in all) _asked.Add(n);
        return all.ToArray();
    }

    private static bool AnyMatches(string token, string[] names)
    {
        foreach (var n in names) if (NameMatches(token, n)) return true;
        return false;
    }

    private bool IsOptionShaped(int index, string token) =>
        index < _literalFrom && token.StartsWith('-') && token.Length > 1 && !char.IsDigit(token[1]);

    /// <summary>Consume one occurrence of a flag (name match is case-insensitive). A repeated,
    /// unconsumed occurrence is later rejected by <see cref="Positionals"/> as an unknown option.</summary>
    public bool Flag(params string[] names)
    {
        names = Expand(names);
        for (int i = 0; i < OptionEnd; i++)
            if (!_attached.Contains(i) && _tokens[i] is { } t && AnyMatches(t, names))
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

    /// <summary>An on/off switch a configuration profile may also set: <c>--name</c> turns it on,
    /// <c>--no-name</c> turns it off, and with neither typed the profile decides (off when it says
    /// nothing). This is what lets a command line undo a profile's <c>"insecure": true</c> without
    /// dropping the whole profile with --no-config.</summary>
    public bool Switch(string name, bool? profile, params string[] aliases)
    {
        bool on = Flag([name, .. aliases]);
        string negative = NegativeOf(name);
        bool off = Flag(negative);
        if (on && off) throw new CliUsageException($"{name} and {negative} contradict each other; give one.");
        return on || (!off && profile == true);
    }

    /// <summary>Consumes <c>--no-name</c> for a switch read some other way (one with an optional
    /// value, say). True when it was given.</summary>
    public bool Negated(string name) => Flag(NegativeOf(name));

    internal static string NegativeOf(string name) => "--no-" + name.TrimStart('-');

    /// <summary>Consume one option and its value (name match is case-insensitive); null when absent.
    /// An option present without a value is a usage error.</summary>
    public string? Value(params string[] names)
    {
        names = Expand(names);
        for (int i = 0; i < OptionEnd; i++)
        {
            if (_attached.Contains(i) || _tokens[i] is not { } t || !AnyMatches(t, names)) continue;
            if (i + 1 >= _tokens.Length || _tokens[i + 1] is null)
                throw new CliUsageException($"Option {t} needs a value.");
            var value = _tokens[i + 1]!;
            // "--cert --insecure" is a missing value, not a certificate called "--insecure". Only
            // a long-option shape is refused: a single-dash value ("-d -1", "-d -body") has always
            // been accepted adjacent to its option and still is.
            if (i + 1 < _literalFrom && !_attached.Contains(i + 1) && value.StartsWith("--", StringComparison.Ordinal) && value.Length > 2)
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
        names = Expand(names);
        for (int i = 0; i < OptionEnd; i++)
        {
            if (_attached.Contains(i) || _tokens[i] is not { } t || !AnyMatches(t, names)) continue;
            _tokens[i] = null;
            // The next token belongs to this option only when it could not be an option itself —
            // the same shape rule Positionals() uses — so "--cors --insecure" leaves --insecure be.
            if (i + 1 < _tokens.Length && _tokens[i + 1] is { } next && (_attached.Contains(i + 1) || !IsOptionShaped(i + 1, next)))
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
                throw new CliUsageException(UnknownOptionMessage(t));
            positionals.Add(t);
        }
        return positionals;
    }

    /// <summary>What to say about an option nothing consumed: a repeat, a deliberate omission with
    /// its alternative, or a near miss of an option this command does have.</summary>
    private string UnknownOptionMessage(string t)
    {
        if (_seen.Any(s => NameMatches(t, s)))
            return $"Option '{t}' was given more than once, and this command takes it once.";
        if (UnsupportedOptions.Explain(t) is { } why)
            return $"Option '{t}' does not exist. {why}";
        if (Suggest(t) is { } near)
            return $"Unknown option '{t}'. Did you mean '{near}'?";
        return $"Unknown option '{t}'.";
    }

    /// <summary>The closest long option this command asked about, when it is close enough to be the
    /// one meant: within two edits, or one for a short name. Null when nothing is close, or when
    /// two candidates are equally close (a guess between them would be a coin toss).</summary>
    internal string? Suggest(string token)
    {
        if (!token.StartsWith("--", StringComparison.Ordinal) || token.Length < 4) return null;
        string typed = token.ToLowerInvariant();
        string? best = null;
        int bestDistance = int.MaxValue;
        bool tie = false;
        foreach (var name in _asked)
        {
            if (!name.StartsWith("--", StringComparison.Ordinal)) continue;
            int d = Distance(typed, name.ToLowerInvariant());
            if (d < bestDistance) { best = name; bestDistance = d; tie = false; }
            else if (d == bestDistance && !string.Equals(best, name, StringComparison.OrdinalIgnoreCase)) tie = true;
        }
        int allowed = typed.Length <= 6 ? 1 : 2;
        return best is not null && bestDistance <= allowed && !tie ? best : null;
    }

    /// <summary>Optimal-string-alignment distance: insertions, deletions, substitutions and
    /// adjacent transpositions ("--insceure") each cost one.</summary>
    internal static int Distance(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (int j = 0; j <= b.Length; j++) d[0, j] = j;
        for (int i = 1; i <= a.Length; i++)
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                    d[i, j] = Math.Min(d[i, j], d[i - 2, j - 2] + 1);
            }
        return d[a.Length, b.Length];
    }
}
