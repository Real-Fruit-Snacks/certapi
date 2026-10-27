using System.Text.RegularExpressions;
using ApiTester.Core;

namespace ApiTester.Cli.Commands;

/// <summary><c>certapi completion powershell</c> prints a script that registers tab completion, and
/// the hidden <c>certapi __complete</c> answers it. The script stays tiny and asks the binary
/// itself, so completion always matches the version that is installed: commands, subcommands,
/// options (read from each command's own help), and values that are known in advance or can be
/// looked up — certificates in your store, profile names, environments, collections.</summary>
public static class CompletionCommand
{
    public const string Help = """
        Usage: certapi completion powershell

        Prints a PowerShell script that adds tab completion for certapi: commands, subcommands,
        every option of the command being typed, and values where they can be known — methods,
        certificates in your store (thumbprint or subject), profile names from your configuration,
        environment and collection names from your workspace, and the fixed choices of options such
        as --store, --revocation, --tls-min and --grant. Works in Windows PowerShell 5.1 and
        PowerShell 7.

        Load it for this session:
          certapi completion powershell | Out-String | Invoke-Expression

        Load it in every session, by adding that line to your profile:
          Add-Content $PROFILE 'certapi completion powershell | Out-String | Invoke-Expression'

        The script calls certapi to work out each completion, so it stays right after an update.

        Global: --debug (verbose diagnostics) and --log-file <path> work here too.

        Examples:
          certapi completion powershell | Out-String | Invoke-Expression
          certapi send https://api.example.com --cert <Tab>   # certificates in your store
        """;

    public const string PowerShellScript = """
        # certapi tab completion for Windows PowerShell 5.1 and PowerShell 7.
        # Load with: certapi completion powershell | Out-String | Invoke-Expression
        Register-ArgumentCompleter -Native -CommandName 'certapi', 'certapi.exe' -ScriptBlock {
            param($wordToComplete, $commandAst, $cursorPosition)
            $words = New-Object System.Collections.Generic.List[string]
            foreach ($e in ($commandAst.CommandElements | Select-Object -Skip 1)) {
                if ($e.Extent.StartOffset -ge $cursorPosition) { break }
                if ($e.Extent.EndOffset -le $cursorPosition -and $e -is [System.Management.Automation.Language.StringConstantExpressionAst]) {
                    $words.Add($e.Value)
                } else {
                    $t = $e.Extent.Text
                    if ($e.Extent.EndOffset -gt $cursorPosition) { $t = $t.Substring(0, $cursorPosition - $e.Extent.StartOffset) }
                    $words.Add($t.Trim('"', "'"))
                }
            }
            $exe = $commandAst.CommandElements[0].Extent.Text.Trim('"', "'")
            # Windows PowerShell drops an empty argument on its way to a program, so an empty word
            # under the cursor is said with --empty instead.
            $flag = if ($wordToComplete -eq '') { '--empty' } else { '--word' }
            $found = & $exe __complete $flag -- @words 2>$null
            foreach ($line in $found) {
                if ([string]::IsNullOrEmpty($line)) { continue }
                # A line may carry a description after a tab: shown as the tooltip.
                $r, $tip = $line -split "`t", 2
                if (-not $tip) { $tip = $r }
                $text = if ($r -match '[\s''"`$;,(){}@&|<>]') { "'" + ($r -replace "'", "''") + "'" } else { $r }
                $kind = if ($r.StartsWith('-')) { 'ParameterName' } else { 'ParameterValue' }
                [System.Management.Automation.CompletionResult]::new($text, $r, $kind, $tip)
            }
        }
        """;

    public static int Run(Args args, TextWriter stdout, TextWriter stderr)
    {
        var positionals = args.Positionals();
        if (positionals.Count != 1 || !positionals[0].Equals("powershell", StringComparison.OrdinalIgnoreCase))
            throw new CliUsageException(positionals.Count == 1
                ? $"No completion script for '{positionals[0]}'. certapi completes in PowerShell: certapi completion powershell"
                : Help);
        stdout.WriteLine(PowerShellScript);
        return ExitCodes.Ok;
    }

    /// <summary>The hidden entry point the script calls: <c>__complete (--empty|--word) -- words…</c>.
    /// Never fails loudly: a completion that cannot be worked out is simply no completion.</summary>
    public static int RunComplete(string[] raw, TextWriter stdout, CliServices services)
    {
        int dashes = Array.IndexOf(raw, "--");
        bool empty = raw.Take(dashes < 0 ? raw.Length : dashes).Contains("--empty");
        var words = (dashes < 0 ? Array.Empty<string>() : raw[(dashes + 1)..]).ToList();
        if (empty || words.Count == 0) words.Add("");
        try
        {
            foreach (var c in Complete(words, services))
                stdout.WriteLine(Descriptions.TryGetValue(c, out var tip) ? c + "\t" + tip : c);
        }
        catch { /* no completions rather than an error in the middle of someone's typing */ }
        return ExitCodes.Ok;
    }

    internal static readonly string[] CommandNames =
    [
        "send", "run", "token", "fuzz", "bench", "sse", "ws", "doctor", "proxy", "connections", "config",
        "certs", "selftest", "mock", "import", "export", "trust", "serve", "grpc", "mcp", "completion",
        "update", "help",
    ];

    private static readonly string[] GlobalValueOptions = ["--log-file", "--profile", "--config", "--trace-file", "--trace-filter"];
    private static readonly string[] GlobalFlags = ["--debug", "--no-config", "--trace", "--trace-verbose", "--trace-include-secrets", "--help"];

    /// <summary>Completions for the last word of <paramref name="words"/> (the words typed after
    /// <c>certapi</c>, the last one possibly partial or empty).</summary>
    internal static IEnumerable<string> Complete(IReadOnlyList<string> words, CliServices services)
    {
        // Global options may come before the command (certapi --profile corp send …): set them
        // aside, with their values, so the command is still the first word.
        var rest = words.ToList();
        while (rest.Count > 1 && rest[0].StartsWith('-'))
        {
            string option = rest[0];
            rest.RemoveAt(0);
            if (!GlobalValueOptions.Contains(option, StringComparer.OrdinalIgnoreCase)) continue;
            // The word being typed is this global option's value.
            if (rest.Count == 1)
                return option.Equals("--profile", StringComparison.OrdinalIgnoreCase) ? Filter(ProfileNames(services), rest[0]) : [];
            rest.RemoveAt(0);
        }
        words = rest;
        string current = words.Count == 0 ? "" : words[^1];
        var prior = words.Take(Math.Max(0, words.Count - 1)).ToList();

        if (prior.Count == 0)
            return Filter(CommandNames.Concat(["--version", "--help"]).Where(c => CliApp.HelpFor(c) is not null || c is "help" or "--version" or "--help"), current);

        string command = prior[0].ToLowerInvariant();
        if (command == "help")
            return prior.Count == 1 ? Filter(CommandNames.Where(c => c != "help"), current) : [];
        if (CliApp.HelpFor(command) is not { } help) return [];

        string? previous = prior.Count > 1 ? prior[^1] : null;
        if (previous is not null && previous.StartsWith('-') && TakesValue(help, Canonical(previous)))
            return Filter(ValuesFor(Canonical(previous), services), current);

        if (current.StartsWith('-'))
            return Filter(OptionsOf(help).Concat(GlobalValueOptions).Concat(GlobalFlags), current);

        // The first word after the command may be a subcommand (trust add, config show, …).
        var positionalsSoFar = prior.Skip(1).Where((w, i) => !w.StartsWith('-')).ToList();
        if (positionalsSoFar.Count == 0 && SubcommandsOf(command, help) is { Count: > 0 } subs)
            return Filter(subs, current);

        if (command == "run" && positionalsSoFar.Count == 0)
            return Filter(CollectionNames(services), current);
        return [];
    }

    private static IEnumerable<string> Filter(IEnumerable<string> candidates, string prefix) =>
        candidates.Where(c => c.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                  .Distinct(StringComparer.Ordinal);

    /// <summary>A curl alias stands for its option when deciding what value comes next.</summary>
    private static string Canonical(string option)
    {
        foreach (var (canonical, aliases) in Args.Aliases)
            if (aliases.Contains(option, StringComparer.Ordinal)) return canonical;
        return option;
    }

    /// <summary>Every option a help text names, in the order it names them.</summary>
    internal static List<string> OptionsOf(string help)
    {
        var found = new List<string>();
        foreach (Match m in Regex.Matches(help, @"(?<![\w-])(--[a-z0-9][a-z0-9.\-]*[a-z0-9]|-[A-Za-z0-9])(?![\w-])"))
            if (!found.Contains(m.Value, StringComparer.Ordinal)) found.Add(m.Value);
        return found;
    }

    /// <summary>True when the help shows the option followed by a value placeholder
    /// (<c>--cert &lt;thumb|subject&gt;</c>, <c>-X, --method &lt;m&gt;</c>, <c>--query name=value</c>).</summary>
    private static readonly string[] TransportValueOptions =
    {
        "--proxy", "--proxy-user", "--noproxy", "--pac", "--ca-file", "--cacert", "--revocation", "--tls-min",
        "--connect-timeout", "--sni", "--resolve", "--max-redirs", "--timeout", "--max-time", "-x", "-U",
    };

    internal static bool TakesValue(string help, string option)
    {
        if (GlobalValueOptions.Contains(option, StringComparer.OrdinalIgnoreCase)) return true;
        if (KnownChoices.Contains(option, StringComparer.OrdinalIgnoreCase)) return true;
        // The shared transport options take a value everywhere, including in the helps (mcp,
        // update) that list them by name only, with no <placeholder> to recognise.
        if (TransportValueOptions.Contains(option, StringComparer.OrdinalIgnoreCase)) return true;
        string o = Regex.Escape(option);
        return Regex.IsMatch(help, $@"(?<![\w-]){o}(?:,\s*--?[\w.\-]+)*(?: |=)(?:<|\[<|[a-z]+=|""[a-z])",
            RegexOptions.IgnoreCase);
    }

    /// <summary>Subcommands from a help's usage lines: <c>certapi trust add …</c> → add.</summary>
    internal static List<string> SubcommandsOf(string command, string help)
    {
        if (command == "completion") return ["powershell"];
        var subs = new List<string>();
        // Only the usage block at the top: examples further down name URLs, not subcommands.
        var blank = Regex.Match(help, @"\r?\n[ \t]*\r?\n");
        string usage = blank.Success ? help[..blank.Index] : help;
        foreach (Match m in Regex.Matches(usage, $@"^\s*(?:Usage:\s*)?certapi {Regex.Escape(command)} ([a-z][a-z\-]+)\b", RegexOptions.Multiline))
            if (!subs.Contains(m.Groups[1].Value)) subs.Add(m.Groups[1].Value);
        // A usage line that only shows options ("certapi mock [options]") names no subcommand.
        return subs.Count > 1 || (subs.Count == 1 && !usage.Contains($"certapi {command} [", StringComparison.Ordinal)) ? subs : [];
    }

    private static readonly string[] KnownChoices =
        ["-X", "--method", "--store", "--revocation", "--tls-min", "--grant", "--client-auth", "--tls-mode", "--cert", "--ca", "--profile", "--env"];

    private static IEnumerable<string> ValuesFor(string option, CliServices services) => option.ToLowerInvariant() switch
    {
        "-x" or "--method" => ["GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS"],
        "--store" => ["CurrentUser", "LocalMachine"],
        "--revocation" => ["none", "offline", "online"],
        "--tls-min" => ["1.2", "1.3"],
        "--grant" => ["client_credentials", "password", "refresh", "device"],
        "--client-auth" => ["body", "basic"],
        "--tls-mode" => ["valid", "expired", "wrong-host", "self-signed"],
        "--cert" or "--ca" => Certificates(services),
        "--profile" => ProfileNames(services),
        "--env" => Safe(() => AppState.LoadFrom(services.LiveStatePath).Environments.Select(e => e.Name)),
        _ => [],
    };

    // Tooltips for values whose text alone says little (a thumbprint), filled while completing.
    [ThreadStatic] private static Dictionary<string, string>? _descriptions;
    private static Dictionary<string, string> Descriptions => _descriptions ??= new(StringComparer.Ordinal);

    private static IEnumerable<string> Certificates(CliServices services) => Safe(() =>
    {
        var certs = services.ListCertificates(false);
        foreach (var c in certs)
            Descriptions[c.Thumbprint] = $"{c.Subject}  (expires {c.NotAfter:yyyy-MM-dd})";
        return certs.Select(c => c.Thumbprint).Concat(certs.Select(c => c.Subject));
    });

    private static IEnumerable<string> ProfileNames(CliServices services) => Safe(() =>
    {
        var source = ConfigLoader.Discover(null, services.WorkingDirectory, services.UserConfigPath,
            environment: null, fileExists: services.FileExists);
        if (source is null) return [];
        return ConfigLoader.Parse(File.ReadAllText(source.Path), source).Profiles.Keys;
    });

    private static IEnumerable<string> CollectionNames(CliServices services) => Safe(() =>
        AppState.LoadFrom(services.LiveStatePath).Collections.Select(c => c.Name));

    private static IEnumerable<string> Safe(Func<IEnumerable<string>> read)
    {
        try { return read().Where(s => !string.IsNullOrEmpty(s)).ToList(); }
        catch { return []; }
    }
}
