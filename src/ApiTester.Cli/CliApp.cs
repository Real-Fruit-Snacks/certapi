using System.Reflection;
using ApiTester.Core;

namespace ApiTester.Cli;

/// <summary>Injectable seams so tests can run every command in-process.</summary>
public sealed class CliServices
{
    public Func<bool, IReadOnlyList<CertificateInfo>> ListCertificates { get; init; } =
        includeLocalMachine => new CertificateStoreService().ListClientCertificates(includeLocalMachine);

    public Func<bool> IsGuiRunning { get; init; } =
        () => System.Diagnostics.Process.GetProcessesByName("ApiTester.App").Length > 0;

    public string LiveStatePath { get; init; } = AppState.DefaultPath;

    public ApiClient Client { get; init; } = new();

    /// <summary>Wired to Ctrl+C by Program.cs so in-flight requests cancel cleanly.</summary>
    public CancellationToken Cancel { get; init; } = CancellationToken.None;

    public Func<string, System.Security.Cryptography.X509Certificates.X509Certificate2?> FindCertificate { get; init; } =
        thumbprint => new CertificateStoreService().FindByThumbprint(thumbprint, includeLocalMachine: true);

    public Func<GatewayRoutes, System.Security.Cryptography.X509Certificates.X509Certificate2?, bool, TimeSpan, TransportOptions?,
        Func<string, Func<System.Security.Cryptography.X509Certificates.X509Certificate2?, bool>>?, ApiTester.Core.MtlsGateway> GatewayFactory
    { get; init; } = (routes, cert, insecure, timeout, transport, trustForHost) =>
        new ApiTester.Core.MtlsGateway(routes, cert, insecure, timeout, transport, trustForHost);

    /// <summary>Diagnostic sink for --debug / --log-file; set per invocation by CliApp.</summary>
    public CliLog Log { get; set; } = CliLog.None;

    /// <summary>Open a file with whatever the shell has registered for it. Injected so a test can
    /// assert the request was made without a viewer opening on the machine running the suite.
    /// <para><c>UseShellExecute</c> is what makes this the shell's decision rather than an attempt
    /// to execute the file — which for a .md is exactly what is wanted, and is also why the caller
    /// treats any failure here as a note rather than an error.</para></summary>
    public Action<string> OpenFile { get; init; } = path =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();

    /// <summary>The configuration profile in effect, resolved once per invocation by CliApp from
    /// <c>--profile</c>/<c>--config</c> and the discovered file. Null when there is no
    /// configuration, or when <c>--no-config</c> was given — which is exactly the state every
    /// command had before configuration files existed, so nothing changes for a user without one.
    /// <para>Where the working directory is, for discovery. A test sets it rather than relying on
    /// the process's own.</para></summary>
    public ConfigProfile? Profile { get; set; }

    /// <summary>Which file the profile came from, and by which discovery rule — set alongside
    /// <see cref="Profile"/> so `certapi config path` reports what this invocation actually used
    /// rather than re-running discovery and possibly disagreeing with it.</summary>
    public ConfigSource? ProfileSource { get; set; }

    /// <summary>The directory discovery walks up from. Overridable so a test never depends on
    /// where the test host happens to be running.</summary>
    public string WorkingDirectory { get; init; } = Directory.GetCurrentDirectory();

    /// <summary>The per-user configuration path, or null to disable that discovery rule. A test
    /// sets it to null so a developer's own file can never change a test's outcome.</summary>
    public string? UserConfigPath { get; init; } = ConfigLoader.DefaultUserConfigPath();

    /// <summary>How discovery decides a candidate file is there. Injectable for the same reason the
    /// certificate store and the gateway factory are: walking up from a directory reaches shared
    /// ancestors — a test running under the temporary directory would otherwise discover whatever
    /// another process happened to leave there, which is exactly the flake this seam prevents.</summary>
    public Func<string, bool> FileExists { get; init; } = File.Exists;
}

public static class CliApp
{
    public const string Usage = """
        Usage: certapi <command> [options]

        Commands:
          send <url>        Send a one-off request (client cert from the Windows store)
          token             Fetch an OAuth 2.0 access token (and optionally save it)
          run <path>        Run saved requests from your collections (or --all)
          fuzz <base-url>   Discover endpoints from a wordlist (which ones exist?)
          bench <url>       Measure an endpoint's latency under load (how fast is it?)
          sse <url>         Stream Server-Sent Events (text/event-stream)
          ws <url>          Open a WebSocket, send messages, print what arrives
          doctor <url>      Diagnose a connection stage by stage (why can't I reach this?)
          proxy [<url>]     Show the machine's proxy settings, and which proxy a URL gets
          connections <url> Are connections actually being reused? (pool inspector)
          config            Show the configuration file and profile in effect
          certs             List client certificates
          selftest          Prove the mTLS path end-to-end against a loopback server
          mock              Run a local test server to fire requests at (http/tls/mtls)
          import            Import a cURL command or an OpenAPI file into collections
          export            Export collections as OpenAPI, or the whole workspace
          trust             Manage per-site trusted (pinned) server certificates
          serve             Run a local mTLS gateway in front of one site or many
          grpc              Discover and call a gRPC service (all four method kinds)
          mcp               Run an MCP server so AI agents can make mTLS calls
          help [command]    Show help (for one command, or this overview)

        Global options (work on every command, anywhere on the line):
          --debug           Rich diagnostics on stderr: resolved URLs, headers (Authorization
                            masked), certificate lookup, TLS details, timings, full stack traces
          --log-file <path> Append everything (diagnostics + all stderr output) to a log file
          --profile <name>  Use this profile's defaults from the configuration file, so a long
                            command line becomes a short one (see 'certapi help config')
          --config <path>   Read configuration from this file instead of discovering one
          --no-config       Ignore configuration entirely, whatever is on disk
          --trace           Report what the network stack itself did: DNS, TCP, TLS handshake,
                            connection established or reused, and the request lifecycle. In-process
                            only, so no driver and no admin rights are involved
          --trace-verbose   Add the runtime's internal diagnostics — far more detail, far less
                            stable; useful when --trace is not enough, never something to parse
          --trace-file <p>  Write the trace to a file instead of streaming it to stderr
          --trace-filter <s>  Keep only lines containing any of these comma-separated substrings
                            (repeatable)
          --trace-include-secrets  Keep credential values in the trace. By default every
                            Authorization/Cookie-style header and URL secret is redacted

        Examples:
          certapi certs
          certapi send https://api.example.com/health --cert "CN=My Client"
          certapi send https://api.example.com/login -X POST -d '{"user":"me"}'
              # a token in the response (access_token / id_token / …) is captured
              # automatically and reused for later requests to the same host
          certapi run smoke-suite --env Staging
          certapi bench https://api.example.com/health --cert "CN=My Client" -n 500 -c 20
          certapi selftest
          certapi serve --port 8819 --cert "CN=My Client" --upstream api=https://api.internal
          certapi serve --profile corp --check
          certapi send https://api.example.com/x --debug --log-file certapi.log

        Run 'certapi <command> --help' (or 'certapi help <command>') for options. 'certapi --version' prints the version.
        """;

    public static int Run(string[] args, TextReader input, TextWriter stdout, TextWriter stderr,
                          Stream? bodyOut = null, CliServices? services = null)
    {
        services ??= new CliServices();
        // Commands that read stdin (mcp/fuzz/ws/grpc) or stream to stdout (sse) run through here so
        // they get the reader; everything else falls through to the reader-less overload below.
        if (args.Length > 0 && IsStreamingCommand(args[0]))
        {
            string cmd = args[0].ToLowerInvariant();
            (string[] Remaining, bool Debug, string? LogFile, string? Config, string? Profile, bool NoConfig,
             bool Trace, string? TraceFile, IReadOnlyList<string> TraceFilters, bool TraceVerbose, bool TraceIncludeSecrets) g;
            try { g = GlobalOptions.ExtractEverything(args.Skip(1).ToArray()); }
            catch (CliUsageException ex) { stderr.WriteLine(ex.Message); return ExitCodes.Usage; }

            using var log = CliLog.Create(g.Debug, g.LogFile, stderr);
            services.Log = log;
            var err = log.WrapStderr(stderr);
        TransportFlags.Warn = err.WriteLine;
            // The streaming commands trace too: a hanging WebSocket handshake is exactly the case
            // this exists for, and a flag that worked on `send` but not `ws` would be a trap.
            using var trace = StartTrace(g.Trace, g.TraceVerbose, g.TraceFilters, g.TraceFile, g.TraceIncludeSecrets, err);
            try
            {
                // The streaming commands take the same profile as everything else: a configuration
                // that applies to `send` but not to `ws` would be a trap rather than a convenience.
                if (PrintedHelp(cmd, g.Remaining, stdout)) return ExitCodes.Ok;
                services.Profile = ResolveProfile(g.Config, g.Profile, g.NoConfig, services, log);
                return cmd switch
                {
                    "mcp"  => Commands.McpCommand.Run(new Args(g.Remaining), input, stdout, err, services),
                    "fuzz" => Commands.FuzzCommand.Run(new Args(g.Remaining), input, stdout, err, services),
                    "ws"   => Commands.WsCommand.Run(new Args(g.Remaining), input, stdout, err, services),
                    "sse"  => Commands.SseCommand.Run(new Args(g.Remaining), stdout, err, services),
                    "grpc" => Commands.GrpcCommand.Run(new Args(g.Remaining), input, stdout, err, services),
                    _      => throw new CliUsageException($"Unknown command '{args[0]}'.\n{Usage}")
                };
            }
            catch (CliUsageException ex) { err.WriteLine(ex.Message); return ExitCodes.Usage; }
            catch (CliDataException ex) { err.WriteLine(ex.Message); return ExitCodes.Data; }
            catch (Exception ex) { err.WriteLine("error: " + log.Describe(ex)); return ExitCodes.Failure; }
        }
        return Run(args, stdout, stderr, bodyOut, services);
    }

    private static bool IsStreamingCommand(string arg) =>
        arg.Equals("mcp", StringComparison.OrdinalIgnoreCase) ||
        arg.Equals("fuzz", StringComparison.OrdinalIgnoreCase) ||
        arg.Equals("ws", StringComparison.OrdinalIgnoreCase) ||
        arg.Equals("sse", StringComparison.OrdinalIgnoreCase) ||
        arg.Equals("grpc", StringComparison.OrdinalIgnoreCase);

    public static int Run(string[] args, TextWriter stdout, TextWriter stderr,
                          Stream? bodyOut = null, CliServices? services = null)
    {
        services ??= new CliServices();
        if (args.Length == 0) { stderr.WriteLine(Usage); return ExitCodes.Usage; }

        (string[] Remaining, bool Debug, string? LogFile, string? Config, string? Profile, bool NoConfig,
         bool Trace, string? TraceFile, IReadOnlyList<string> TraceFilters, bool TraceVerbose, bool TraceIncludeSecrets) g;
        try { g = GlobalOptions.ExtractEverything(args); }
        catch (CliUsageException ex) { stderr.WriteLine(ex.Message); return ExitCodes.Usage; }

        using var log = CliLog.Create(g.Debug, g.LogFile, stderr);
        services.Log = log;
        var err = log.WrapStderr(stderr);
        // Started before anything connects, so the very first DNS lookup and handshake are seen.
        using var trace = StartTrace(g.Trace, g.TraceVerbose, g.TraceFilters, g.TraceFile, g.TraceIncludeSecrets, err);
        try
        {
            // Resolved once, here, so every command below sees the same profile — and so a broken
            // configuration is reported before a command starts doing work with half of it.
            if (g.Remaining.Length == 0) { err.WriteLine(Usage); return ExitCodes.Usage; }
            string command = g.Remaining[0].ToLowerInvariant();
            var rest = g.Remaining.Skip(1).ToArray();
            // Before the profile is resolved: help must work even when the configuration is broken.
            if (PrintedHelp(command, rest, stdout)) return ExitCodes.Ok;
            services.Profile = ResolveProfile(g.Config, g.Profile, g.NoConfig, services, log);
            return command switch
            {
                "--version" or "-v" => Version(stdout),
                "help" or "--help" or "-h" => Help(rest, stdout),
                "certs" => Commands.CertsCommand.Run(new Args(rest), stdout, err, services),
                "send" => Commands.SendCommand.Run(new Args(rest), stdout, err, bodyOut ?? new MemoryStream(), services),
                "run" => Commands.RunCommand.Run(new Args(rest), stdout, err, services),
                "token" => Commands.TokenCommand.Run(new Args(rest), stdout, err, services),
                "fuzz" => Commands.FuzzCommand.Run(new Args(rest), TextReader.Null, stdout, err, services),
                "bench" => Commands.BenchCommand.Run(new Args(rest), stdout, err, services),
                "selftest" => Commands.SelfTestCommand.Run(new Args(rest), stdout, err),
                "mock" => Commands.MockCommand.Run(new Args(rest), stdout, err, services),
                "import" => Commands.ImportCommand.Run(new Args(rest), stdout, err, services),
                "export" => Commands.ExportCommand.Run(new Args(rest), stdout, err, services),
                "trust" => Commands.TrustCommand.Run(new Args(rest), stdout, err, services),
                "serve" => Commands.ServeCommand.Run(new Args(rest), stdout, err, services),
                "grpc" => Commands.GrpcCommand.Run(new Args(rest), TextReader.Null, stdout, err, services),
                "doctor" => Commands.DoctorCommand.Run(new Args(rest), stdout, err, services),
                "proxy" => Commands.ProxyCommand.Run(new Args(rest), stdout, err, services),
                "connections" => Commands.ConnectionsCommand.Run(new Args(rest), stdout, err, services),
                "config" => Commands.ConfigCommand.Run(new Args(rest), stdout, err, services),
                _ => throw new CliUsageException($"Unknown command '{g.Remaining[0]}'.\n{Usage}")
            };
        }
        catch (CliUsageException ex) { err.WriteLine(ex.Message); return ExitCodes.Usage; }
        catch (CliDataException ex) { err.WriteLine(ex.Message); return ExitCodes.Data; }
        catch (Exception ex) { err.WriteLine("error: " + log.Describe(ex)); return ExitCodes.Failure; }
    }

    /// <summary>The network trace for this invocation, or null when it was not asked for. Writing
    /// to a file is deferred to disposal so a relay never pays a write per event; streaming to
    /// stderr happens live, because a trace of a hanging request is only useful as it happens.</summary>
    private static TraceSession? StartTrace(
        bool enabled, bool verbose, IReadOnlyList<string> filters, string? file, bool includeSecrets, TextWriter stderr) =>
        enabled ? new TraceSession(verbose, filters, file, includeSecrets, stderr) : null;

    /// <summary>Owns a <see cref="NetworkTrace"/> and, when asked, writes it out at the end.</summary>
    private sealed class TraceSession : IDisposable
    {
        private readonly NetworkTrace _trace;
        private readonly string? _file;
        private readonly TextWriter _stderr;

        public TraceSession(bool verbose, IReadOnlyList<string> filters, string? file, bool includeSecrets, TextWriter stderr)
        {
            _file = file;
            _stderr = stderr;
            _trace = new NetworkTrace(
                verbose ? TraceLevel.Verbose : TraceLevel.Normal,
                filters,
                // With no file, the trace is the output: stream it, so a request that never
                // finishes still shows how far it got.
                onLine: file is null ? line => stderr.WriteLine("trace " + line) : null,
                includeSecrets: includeSecrets);
        }

        public void Dispose()
        {
            _trace.Dispose();
            if (_file is null) return;
            try
            {
                File.WriteAllLines(_file, _trace.Lines.Select(l => l.ToString()));
                _stderr.WriteLine($"wrote {_trace.Lines.Count} trace line(s) to {_file}");
            }
            catch (Exception ex) { _stderr.WriteLine("warning: could not write the trace: " + ex.Message); }
        }
    }

    /// <summary>The profile in effect for this invocation, or null when there is no configuration
    /// to apply. <c>--no-config</c> short-circuits every discovery rule, which is what makes a run
    /// reproducible regardless of what happens to sit in the working directory.</summary>
    internal static ConfigProfile? ResolveProfile(
        string? configPath, string? profileName, bool noConfig, CliServices services, CliLog log)
    {
        if (noConfig) return null;

        ConfigSource? source;
        try
        {
            source = ConfigLoader.Discover(
                configPath, services.WorkingDirectory, services.UserConfigPath,
                environment: null, fileExists: services.FileExists);
        }
        catch (ConfigException ex) { throw new CliDataException(ex.Message); }

        services.ProfileSource = source;

        if (source is null)
        {
            // A named profile with no file to name it in is a mistake worth reporting: the command
            // would otherwise run with the identity the user believed they had selected missing.
            if (profileName is not null)
                throw new CliDataException(
                    $"--profile {profileName} was given, but no configuration file was found. " +
                    $"Create {ConfigLoader.FileName} here, or pass --config <path>.");
            return null;
        }

        try
        {
            var config = ConfigLoader.Parse(File.ReadAllText(source.Path), source);
            var profile = config.Resolve(profileName);
            if (profile is not null)
                log.Debug($"configuration: {source.Path} ({source.Rule}), profile '{profileName ?? config.DefaultProfile}'");
            return profile;
        }
        catch (ConfigException ex) { throw new CliDataException(ex.Message); }
        catch (IOException ex) { throw new CliDataException($"could not read {source.Path}: {ex.Message}"); }
    }

    /// <summary>The SemVer this build carries, without build metadata.</summary>
    public static string ProductVersion
    {
        get
        {
            var version = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
            int plus = version.IndexOf('+');                      // strip build metadata
            return plus > 0 ? version[..plus] : version;
        }
    }

    private static int Version(TextWriter stdout)
    {
        stdout.WriteLine($"certapi {ProductVersion}");
        return ExitCodes.Ok;
    }

    private static int Help(string[] rest, TextWriter stdout)
    {
        stdout.WriteLine(HelpFor(rest.FirstOrDefault()) ?? Usage);
        return ExitCodes.Ok;
    }

    /// <summary>The help text for one command, or null when <paramref name="command"/> is not one.</summary>
    internal static string? HelpFor(string? command) => command?.ToLowerInvariant() switch
    {
        "send" => Commands.SendCommand.Help,
        "certs" => Commands.CertsCommand.Help,
        "run" => Commands.RunCommand.Help,
        "token" => Commands.TokenCommand.Help,
        "fuzz" => Commands.FuzzCommand.Help,
        "bench" => Commands.BenchCommand.Help,
        "sse" => Commands.SseCommand.Help,
        "ws" => Commands.WsCommand.Help,
        "selftest" => Commands.SelfTestCommand.Help,
        "mock" => Commands.MockCommand.Help,
        "import" => Commands.ImportCommand.Help,
        "export" => Commands.ExportCommand.Help,
        "trust" => Commands.TrustCommand.Help,
        "serve" => Commands.ServeCommand.Help,
        "mcp" => Commands.McpCommand.Help,
        "grpc" => Commands.GrpcCommand.Help,
        "doctor" => Commands.DoctorCommand.Help,
        "proxy" => Commands.ProxyCommand.Help,
        "connections" => Commands.ConnectionsCommand.Help,
        "config" => Commands.ConfigCommand.Help,
        _ => null
    };

    /// <summary>True when the words after a command ask for its help: <c>--help</c> or <c>-h</c>
    /// anywhere before a literal <c>--</c>. <c>-h</c> is matched exactly, so it never collides with
    /// <c>-H</c> (a header), which the option reader would otherwise match case-insensitively.</summary>
    internal static bool AsksForHelp(IEnumerable<string> words)
    {
        foreach (var w in words)
        {
            if (w == "--") return false;
            if (w == "-h" || w.Equals("--help", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>Prints the command's help and returns true when it was asked for.</summary>
    private static bool PrintedHelp(string command, IEnumerable<string> words, TextWriter stdout)
    {
        if (!AsksForHelp(words) || HelpFor(command) is not { } help) return false;
        stdout.WriteLine(help);
        return true;
    }
}
