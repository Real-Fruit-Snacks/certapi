using System.IO;
using ApiTester.Cli;
using ApiTester.Core;

namespace ApiTester.Tests.Cli;

/// <summary>The precedence rule as a user meets it: a profile supplies defaults, a typed flag
/// always beats it, and `--no-config` ignores the file entirely. Every test names its own config
/// file and disables per-user discovery, so a developer's own configuration can never change an
/// outcome here.</summary>
public class ConfigCommandTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("certapi-config-").FullName;

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    private string WriteConfig(string json)
    {
        string path = Path.Combine(_dir, "certapi.config.json");
        File.WriteAllText(path, json);
        return path;
    }

    /// <summary>Services with discovery sealed off from everything but this test's own directory:
    /// no per-user path, and an existence probe that only ever admits files under <c>_dir</c>.
    /// Without that last part, walking up from a temporary directory reaches the shared temporary
    /// root — where another process's leftover configuration would silently change the outcome.
    /// (That is not hypothetical: it is how this seam came to exist.)</summary>
    private CliServices Services() => new()
    {
        WorkingDirectory = _dir,
        UserConfigPath = null,
        // A CERTAPI_CONFIG set on the machine running the suite must not reach these tests.
        Environment = name => name == ConfigLoader.EnvironmentVariable ? null : System.Environment.GetEnvironmentVariable(name),
        LiveStatePath = Path.Combine(_dir, "state.json"),
        FileExists = path => path.StartsWith(_dir, StringComparison.OrdinalIgnoreCase) && File.Exists(path)
    };

    private (int Code, string Out, string Err) Run(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        int code = CliApp.Run(args, stdout, stderr, services: Services());
        return (code, stdout.ToString(), stderr.ToString());
    }

    [Fact]
    public void Config_path_reports_the_file_the_invocation_actually_used()
    {
        string path = WriteConfig("""{"profiles":{"a":{}}}""");

        var (code, output, _) = Run("config", "path", "--config", path);

        Assert.Equal(0, code);
        Assert.Contains(path, output);
        Assert.Contains("--config", output);   // and by which rule
    }

    [Fact]
    public void Config_profiles_lists_names_and_marks_the_default()
    {
        string path = WriteConfig("""{"defaultProfile":"corp","profiles":{"corp":{},"local":{}}}""");

        var (code, output, _) = Run("config", "profiles", "--config", path);

        Assert.Equal(0, code);
        Assert.Contains("corp", output);
        Assert.Contains("(default)", output);
        Assert.Contains("local", output);
    }

    [Fact]
    public void Config_show_prints_the_resolved_profile_but_never_a_secret_value()
    {
        string path = WriteConfig("""
            {"profiles":{"corp":{"timeout":45,"certPassword":"hunter2","proxyUser":"svc:pw"}}}
            """);

        var (code, output, _) = Run("config", "show", "--config", path, "--profile", "corp");

        Assert.Equal(0, code);
        Assert.Contains("45", output);
        // The fact that a secret is set is useful; the secret itself is not printed anywhere.
        Assert.Contains("(set)", output);
        Assert.DoesNotContain("hunter2", output);
        Assert.DoesNotContain("svc:pw", output);
    }

    [Fact]
    public void An_unknown_profile_is_a_data_error_naming_what_exists()
    {
        string path = WriteConfig("""{"profiles":{"corp":{}}}""");

        var (code, _, err) = Run("config", "show", "--config", path, "--profile", "ghost");

        Assert.Equal(3, code);
        Assert.Contains("ghost", err);
        Assert.Contains("corp", err);
    }

    [Fact]
    public void A_profile_named_with_no_configuration_file_anywhere_is_reported()
    {
        // Running on with the identity the user believed they had selected simply missing would be
        // the worse outcome.
        var (code, _, err) = Run("config", "path", "--profile", "corp");

        Assert.Equal(3, code);
        Assert.Contains("corp", err);
        Assert.Contains("no configuration file", err);
    }

    [Fact]
    public void No_config_ignores_a_file_that_is_right_there()
    {
        string path = WriteConfig("""{"defaultProfile":"corp","profiles":{"corp":{"timeout":45}}}""");

        // Explicitly named AND ignored is contradictory, so it is refused...
        var (conflict, _, _) = Run("config", "path", "--config", path, "--no-config");
        Assert.Equal(2, conflict);

        // ...and with discovery pointed at the file's own directory, --no-config still finds none.
        var (code, output, _) = Run("config", "path", "--no-config");

        Assert.Equal(0, code);
        Assert.Contains("no configuration file found", output);
    }

    [Fact]
    public void The_project_file_is_discovered_without_being_named()
    {
        WriteConfig("""{"defaultProfile":"corp","profiles":{"corp":{"timeout":45}}}""");

        var (code, output, _) = Run("config", "show");

        Assert.Equal(0, code);
        Assert.Contains("45", output);   // found by walking up from the working directory
    }

    [Fact]
    public void A_broken_configuration_file_is_a_data_error_not_a_crash()
    {
        string path = WriteConfig("{ this is not json");

        var (code, _, err) = Run("config", "path", "--config", path);

        Assert.Equal(3, code);
        Assert.Contains("not valid JSON", err);
    }

    [Fact]
    public void Config_needs_a_subcommand_and_rejects_an_unknown_one()
    {
        Assert.Equal(2, Run("config").Code);
        Assert.Equal(2, Run("config", "wat").Code);
    }
}
