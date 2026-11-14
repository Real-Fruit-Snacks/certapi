using ApiTester.Core;

namespace ApiTester.Cli;

public static class CliWorkspace
{
    /// <summary>Where the load reports what it read and how long that took (the --debug log). Per
    /// async flow, so two commands running in one process (tests, an MCP host) never write into
    /// each other's log.</summary>
    public static Action<string>? Debug { get => _debug.Value; set => _debug.Value = value; }
    private static readonly AsyncLocal<Action<string>?> _debug = new();

    public static AppState Load(string? workspacePath, string liveStatePath, TextWriter? warn = null)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        AppState state = LoadCore(workspacePath, liveStatePath);
        if (Debug is { } debug)
        {
            string path = workspacePath ?? liveStatePath;
            debug(File.Exists(path)
                ? $"workspace: {path} ({new FileInfo(path).Length / 1024} KB) read in {clock.Elapsed.TotalMilliseconds:0} ms"
                : $"workspace: none at {path}");
        }
        ReportSecretWarnings(state, warn);
        return state;
    }

    private static AppState LoadCore(string? workspacePath, string liveStatePath)
    {
        AppState state;
        if (workspacePath is null)
        {
            state = File.Exists(liveStatePath) ? SafeLoad(liveStatePath) : new AppState();
        }
        else
        {
            if (!File.Exists(workspacePath))
                throw new CliDataException($"Workspace file not found: {workspacePath}");
            state = SafeLoad(workspacePath);
        }
        return state;
    }

    /// <summary>Print the secrets this load could not decrypt. They have already been treated as
    /// absent, so this is a notice and never an error: the run continues and the exit code is
    /// unaffected. Only a file that cannot be read at all is a data error.</summary>
    public static void ReportSecretWarnings(AppState state, TextWriter? warn)
    {
        if (warn is null) return;
        foreach (var w in state.SecretWarnings)
            warn.WriteLine("warning: " + w);
    }

    /// <summary>Say what a save did that the user needs to know about: the one-time copy taken of the
    /// previous file before its secrets were first encrypted, and the case where per-user encryption
    /// was unavailable so secrets had to be written in the clear. Both are notices — the command has
    /// already succeeded and the exit code is unaffected.</summary>
    public static void ReportSaveResult(StateSaveResult result, string path, TextWriter? warn)
    {
        if (warn is null) return;
        if (result.BackupPath is not null)
            warn.WriteLine($"note: secrets in {path} are now encrypted — the previous file was copied to {Path.GetFileName(result.BackupPath)} first.");
        if (!result.SecretsEncrypted)
            warn.WriteLine($"warning: per-user encryption is unavailable on this system — secrets in {path} were saved in the clear.");
    }

    private static AppState SafeLoad(string path)
    {
        try { return AppState.LoadFrom(path); }
        catch (Exception ex) { throw new CliDataException($"Could not read '{path}': {ex.Message}"); }
    }

    public static Dictionary<string, string> BuildVars(
        AppState state, string? envName, IReadOnlyList<string> varOverrides)
    {
        var vars = new Dictionary<string, string>(StringComparer.Ordinal);
        if (envName is not null)
        {
            var env = state.Environments.FirstOrDefault(
                e => e.Name.Equals(envName, StringComparison.OrdinalIgnoreCase))
                ?? throw new CliDataException(
                    $"No environment named '{envName}'. Available: " +
                    (state.Environments.Count == 0 ? "(none)" : string.Join(", ", state.Environments.Select(e => e.Name))));
            foreach (var v in env.Variables)
                if (!string.IsNullOrWhiteSpace(v.Key)) vars[v.Key.Trim()] = v.Value ?? "";
        }
        foreach (var kv in varOverrides)
        {
            int eq = kv.IndexOf('=');
            string key = eq > 0 ? kv[..eq].Trim() : "";
            if (key.Length == 0) throw new CliUsageException($"--var expects key=value, got '{kv}'.");
            vars[key] = kv[(eq + 1)..];
        }
        return vars;
    }

    public static List<(string Path, CollectionNode Node)> ResolveTargets(AppState state, string? path, bool all)
    {
        if (!all && string.IsNullOrWhiteSpace(path))
            throw new CliUsageException("Give a collection path (e.g. \"api/Get todo\") or --all.");

        IEnumerable<CollectionNode> scope = state.Collections;
        var prefix = "";
        if (!all)
        {
            foreach (var segment in path!.Split('/', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                var matches = scope.Where(n => n.Name.Equals(segment, StringComparison.OrdinalIgnoreCase)).ToList();
                if (matches.Count == 0)
                    throw new CliDataException($"Nothing named '{segment}' under '{(prefix.Length == 0 ? "collections" : prefix.TrimEnd('/'))}'.");
                if (matches.Count > 1)
                    throw new CliDataException(
                        $"'{segment}' is ambiguous under '{(prefix.Length == 0 ? "collections" : prefix.TrimEnd('/'))}':\n" +
                        string.Join("\n", matches.Select(m => $"  {prefix}{m.Name}  ({(m.IsFolder ? "folder" : "request")})")));
                var node = matches[0];
                prefix += node.Name + "/";
                if (!node.IsFolder)
                {
                    var leaf = Leaves(node, prefix.TrimEnd('/'));
                    if (leaf.Count == 0)
                        throw new CliDataException($"'{prefix.TrimEnd('/')}' has no runnable request.");
                    return leaf;
                }
                scope = node.Children;
            }
        }

        var result = new List<(string, CollectionNode)>();
        foreach (var n in scope) result.AddRange(Leaves(n, prefix + n.Name));
        if (result.Count == 0) throw new CliDataException("No saved requests found to run.");
        return result;
    }

    private static List<(string, CollectionNode)> Leaves(CollectionNode node, string path)
    {
        if (!node.IsFolder)
            return node.Request is null ? new() : new() { (path, node) };
        var list = new List<(string, CollectionNode)>();
        foreach (var c in node.Children) list.AddRange(Leaves(c, path + "/" + c.Name));
        return list;
    }
}
