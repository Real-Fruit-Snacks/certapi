namespace ApiTester.Cli;

/// <summary>Diagnostic sink for the global --debug / --log-file options. Debug lines go to
/// stderr when debug is on and to the log file always; the log file also receives every line
/// the command writes to stderr (via <see cref="WrapStderr"/>). Logging never throws — a
/// broken log file must not break the command.</summary>
public sealed class CliLog : IDisposable
{
    public static CliLog None { get; } = new(debug: false, file: null, stderr: TextWriter.Null);

    private readonly TextWriter _stderr;
    private readonly StreamWriter? _file;
    private readonly object _lock = new();

    public bool DebugEnabled { get; }

    private CliLog(bool debug, StreamWriter? file, TextWriter stderr)
    {
        DebugEnabled = debug;
        _file = file;
        _stderr = stderr;
    }

    /// <summary>Open the sink. A log file that cannot be opened is a one-line warning, not an error.</summary>
    public static CliLog Create(bool debug, string? logFilePath, TextWriter stderr)
    {
        StreamWriter? file = null;
        if (logFilePath is not null)
        {
            try
            {
                if (Path.GetDirectoryName(logFilePath) is { Length: > 0 } dir) Directory.CreateDirectory(dir);
                file = new StreamWriter(logFilePath, append: true) { AutoFlush = true };
            }
            catch (Exception ex)
            {
                stderr.WriteLine($"warning: could not open log file '{logFilePath}': {ex.Message}");
            }
        }
        return new CliLog(debug, file, stderr);
    }

    /// <summary>A debug diagnostic: stderr under --debug, log file always.</summary>
    public void Debug(string message)
    {
        if (DebugEnabled) _stderr.WriteLine("debug: " + message);
        ToFile("debug", message);
    }

    /// <summary>Record a line the command wrote to stderr (notes, warnings, errors).</summary>
    public void Note(string line) => ToFile("stderr", line);

    /// <summary>The error text for an exception: full chain and stack under --debug.</summary>
    public string Describe(Exception ex) => DebugEnabled ? ex.ToString() : ex.Message;

    /// <summary>Wrap stderr so every completed line is also recorded in the log file.</summary>
    public TextWriter WrapStderr(TextWriter stderr) => _file is null ? stderr : new TeeWriter(stderr, this);

    private void ToFile(string level, string message)
    {
        if (_file is null) return;
        lock (_lock)
        {
            try { _file.WriteLine($"{DateTime.UtcNow:yyyy-MM-dd'T'HH:mm:ss.fff'Z'} [{level}] {message}"); }
            catch { /* never break the command over logging */ }
        }
    }

    public void Dispose()
    {
        try { _file?.Dispose(); }
        catch { /* never break the command over logging */ }
    }

    private sealed class TeeWriter : TextWriter
    {
        private readonly TextWriter _inner;
        private readonly CliLog _log;
        private readonly System.Text.StringBuilder _line = new();

        public TeeWriter(TextWriter inner, CliLog log) { _inner = inner; _log = log; }

        public override System.Text.Encoding Encoding => _inner.Encoding;

        // Several threads write here at once (--trace's network events, bench and fuzz workers):
        // one lock keeps each write whole on the terminal and each line whole in the log.
        private readonly object _gate = new();

        public override void Write(char value)
        {
            lock (_gate)
            {
                _inner.Write(value);
                Track(value);
            }
        }

        public override void Write(string? value)
        {
            if (value is null) return;
            lock (_gate)
            {
                _inner.Write(value);
                foreach (var c in value) Track(c);
            }
        }

        public override void WriteLine(string? value)
        {
            lock (_gate)
            {
                _inner.WriteLine(value);
                foreach (var c in value ?? "") Track(c);
                Track('\n');
            }
        }

        private void Track(char value)
        {
            if (value == '\n')
            {
                _log.Note(_line.ToString().TrimEnd('\r'));
                _line.Clear();
            }
            else _line.Append(value);
        }

        public override void Flush() { lock (_gate) _inner.Flush(); }
    }
}

/// <summary>Extracts the global --debug / --log-file options, which are valid anywhere on any
/// command line, before command dispatch.</summary>
public static class GlobalOptions
{
    public static (string[] Remaining, bool Debug, string? LogFile) Extract(string[] args)
    {
        var extracted = ExtractAll(args);
        return (extracted.Remaining, extracted.Debug, extracted.LogFile);
    }

    /// <summary>Every option that applies to all commands alike, taken off the line before the
    /// command reads what is left: the diagnostics pair, and the configuration trio
    /// (<c>--config</c>, <c>--profile</c>, <c>--no-config</c>). Configuration is global for the
    /// same reason diagnostics are — a profile carries the identity and transport half of any
    /// command, so it must not have to be re-declared per command.</summary>
    public static (string[] Remaining, bool Debug, string? LogFile, string? Config, string? Profile, bool NoConfig)
        ExtractAll(string[] args) => ExtractEverything(args) is var e
            ? (e.Remaining, e.Debug, e.LogFile, e.Config, e.Profile, e.NoConfig)
            : default;

    /// <summary>Every global option, including the network trace. Trace is global for the same
    /// reason the diagnostics pair is: the question "what did the network stack actually do" is
    /// asked of whichever command happens to be failing.</summary>
    public static (string[] Remaining, bool Debug, string? LogFile, string? Config, string? Profile,
                   bool NoConfig, bool Trace, string? TraceFile, IReadOnlyList<string> TraceFilters,
                   bool TraceVerbose, bool TraceIncludeSecrets)
        ExtractEverything(string[] args)
    {
        var rest = new List<string>(args.Length);
        var traceFilters = new List<string>();
        bool debug = false, noConfig = false, trace = false, traceVerbose = false, traceIncludeSecrets = false;
        string? logFile = null, config = null, profile = null, traceFile = null;

        // The global options follow the same rules as every command's: --name=value is the same as
        // --name value, a single-value option given twice is refused rather than guessed at, and
        // nothing after "--" is an option at all.
        string[] valued = { "--log-file", "--config", "--profile", "--trace-file", "--trace-filter" };
        var expanded = new List<string>(args.Length + 2);
        bool ended = false;
        foreach (var arg in args)
        {
            if (ended) { expanded.Add(arg); continue; }
            if (arg == "--") { ended = true; expanded.Add(arg); continue; }
            int eq = arg.IndexOf('=');
            if (eq > 2 && arg.StartsWith("--", StringComparison.Ordinal) &&
                valued.Contains(arg[..eq], StringComparer.OrdinalIgnoreCase))
            {
                expanded.Add(arg[..eq]);
                expanded.Add(arg[(eq + 1)..]);
            }
            else expanded.Add(arg);
        }
        args = expanded.ToArray();
        string Once(string name, string? current, string value) =>
            current is null ? value : throw new CliUsageException($"Option '{name}' was given more than once, and it takes one value.");

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--") { rest.AddRange(args[i..]); break; }
            if (args[i].Equals("--debug", StringComparison.OrdinalIgnoreCase)) { debug = true; continue; }
            if (args[i].Equals("--no-config", StringComparison.OrdinalIgnoreCase)) { noConfig = true; continue; }
            if (args[i].Equals("--log-file", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length) throw new CliUsageException("Option --log-file needs a value.");
                logFile = Once("--log-file", logFile, args[++i]);
                continue;
            }
            if (args[i].Equals("--config", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length) throw new CliUsageException("Option --config needs a value.");
                config = Once("--config", config, args[++i]);
                continue;
            }
            if (args[i].Equals("--profile", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length) throw new CliUsageException("Option --profile needs a value.");
                profile = Once("--profile", profile, args[++i]);
                continue;
            }
            if (args[i].Equals("--trace", StringComparison.OrdinalIgnoreCase)) { trace = true; continue; }
            if (args[i].Equals("--trace-verbose", StringComparison.OrdinalIgnoreCase))
            { trace = true; traceVerbose = true; continue; }
            if (args[i].Equals("--trace-include-secrets", StringComparison.OrdinalIgnoreCase))
            { traceIncludeSecrets = true; continue; }
            if (args[i].Equals("--trace-file", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length) throw new CliUsageException("Option --trace-file needs a value.");
                traceFile = Once("--trace-file", traceFile, args[++i]);
                trace = true;
                continue;
            }
            if (args[i].Equals("--trace-filter", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length) throw new CliUsageException("Option --trace-filter needs a value.");
                traceFilters.AddRange(args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                trace = true;
                continue;
            }
            rest.Add(args[i]);
        }

        if (noConfig && (config is not null || profile is not null))
            throw new CliUsageException("--no-config cannot be combined with --config or --profile.");
        if (traceIncludeSecrets && !trace)
            throw new CliUsageException("--trace-include-secrets only applies together with --trace.");

        return (rest.ToArray(), debug, logFile, config, profile, noConfig,
                trace, traceFile, traceFilters, traceVerbose, traceIncludeSecrets);
    }
}
