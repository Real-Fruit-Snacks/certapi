using System.IO;
using ApiTester.Core;

namespace ApiTester.Tests;

/// <summary>The configuration file as data: discovery precedence, parsing, `${env:…}` expansion,
/// and profile resolution. Discovery and the environment are both injected, so no test touches the
/// real filesystem, the real environment, or a developer's own configuration.</summary>
public class ConfigProfileTests
{
    private static Func<string, bool> Exists(params string[] paths) =>
        p => paths.Contains(p, StringComparer.OrdinalIgnoreCase);

    private static Func<string, string?> Env(params (string Name, string Value)[] entries) =>
        name => entries.FirstOrDefault(e => e.Name == name).Value;

    // ---------------------------------------------------------------- discovery precedence

    [Fact]
    public void An_explicit_path_wins_over_every_other_rule()
    {
        var source = ConfigLoader.Discover(
            "C:/named.json", "C:/work", "C:/user/config.json",
            Env((ConfigLoader.EnvironmentVariable, "C:/from-env.json")),
            Exists("C:/named.json", "C:/from-env.json", "C:/work/certapi.config.json", "C:/user/config.json"));

        Assert.Equal("C:/named.json", source!.Path);
        Assert.Equal("--config", source.Rule);
    }

    [Fact]
    public void The_environment_variable_beats_the_walked_up_file()
    {
        var source = ConfigLoader.Discover(
            null, "C:/work", "C:/user/config.json",
            Env((ConfigLoader.EnvironmentVariable, "C:/from-env.json")),
            Exists("C:/from-env.json", "C:/work/certapi.config.json"));

        Assert.Equal("C:/from-env.json", source!.Path);
        Assert.Equal(ConfigLoader.EnvironmentVariable, source.Rule);
    }

    [Fact]
    public void The_project_file_is_found_by_walking_up_from_a_nested_directory()
    {
        // The rule that makes a per-repository configuration usable from anywhere inside it.
        var source = ConfigLoader.Discover(
            null, Path.Combine("C:", "work", "src", "deep"), "C:/user/config.json",
            Env(), Exists(Path.Combine("C:", "work", ConfigLoader.FileName)));

        Assert.Equal(Path.Combine("C:", "work", ConfigLoader.FileName), source!.Path);
        Assert.Contains("walking up", source.Rule);
    }

    [Fact]
    public void The_per_user_file_is_the_last_resort()
    {
        var source = ConfigLoader.Discover(
            null, "C:/work", "C:/user/config.json", Env(), Exists("C:/user/config.json"));

        Assert.Equal("C:/user/config.json", source!.Path);
        Assert.Contains("per-user", source.Rule);
    }

    [Fact]
    public void Nothing_anywhere_is_null_rather_than_an_error()
    {
        Assert.Null(ConfigLoader.Discover(null, "C:/work", "C:/user/config.json", Env(), Exists()));
    }

    [Fact]
    public void A_named_file_that_does_not_exist_is_an_error_rather_than_a_fall_through()
    {
        // Falling through to a different file would silently run with an identity the user did not
        // choose — worse than stopping.
        var ex = Assert.Throws<ConfigException>(() => ConfigLoader.Discover(
            "C:/missing.json", "C:/work", "C:/user/config.json", Env(), Exists("C:/work/certapi.config.json")));
        Assert.Contains("missing.json", ex.Message);

        Assert.Throws<ConfigException>(() => ConfigLoader.Discover(
            null, "C:/work", null, Env((ConfigLoader.EnvironmentVariable, "C:/gone.json")), Exists()));
    }

    // ---------------------------------------------------------------- parsing

    [Fact]
    public void A_profile_reads_every_field_it_understands()
    {
        var config = ConfigLoader.Parse("""
            {
              "defaultProfile": "corp",
              "profiles": {
                "corp": {
                  "cert": "CN=My Client", "store": "LocalMachine",
                  "proxy": "socks5://127.0.0.1:1080", "noProxyList": "internal.corp",
                  "revocation": "online", "revocationStrict": true,
                  "retry": 3, "timeout": 60, "insecure": false,
                  "workspace": "C:/work/suite.json",
                  "headers": { "X-Env": "staging" }
                }
              }
            }
            """);

        var p = config.Resolve(null)!;
        Assert.Equal("CN=My Client", p.Cert);
        Assert.Equal("LocalMachine", p.Store);
        Assert.Equal("socks5://127.0.0.1:1080", p.Proxy);
        Assert.Equal("internal.corp", p.NoProxyList);
        Assert.Equal("online", p.Revocation);
        Assert.True(p.RevocationStrict);
        Assert.Equal(3, p.Retry);
        Assert.Equal(60, p.Timeout);
        Assert.False(p.Insecure);
        Assert.Equal("C:/work/suite.json", p.Workspace);
        Assert.Equal("staging", p.Headers.Single(h => h.Key == "X-Env").Value);
    }

    [Fact]
    public void Comments_and_trailing_commas_are_tolerated_because_people_edit_these_files()
    {
        var config = ConfigLoader.Parse("""
            {
              // the profile our team uses
              "profiles": { "corp": { "timeout": 30, } },
            }
            """);

        Assert.Equal(30, config.Profiles["corp"].Timeout);
    }

    [Fact]
    public void A_profile_name_is_matched_case_insensitively()
    {
        var config = ConfigLoader.Parse("""{"profiles":{"Corp":{"timeout":10}}}""");

        Assert.Equal(10, config.Resolve("CORP")!.Timeout);
    }

    [Fact]
    public void Broken_json_names_the_file_rather_than_throwing_a_parser_error()
    {
        var ex = Assert.Throws<ConfigException>(() =>
            ConfigLoader.Parse("{ not json", new ConfigSource("C:/x/certapi.config.json", "test")));

        Assert.Contains("certapi.config.json", ex.Message);
        Assert.Contains("not valid JSON", ex.Message);
    }

    // ---------------------------------------------------------------- profile resolution

    [Fact]
    public void An_unknown_profile_is_an_error_naming_the_ones_that_exist()
    {
        var config = ConfigLoader.Parse("""{"profiles":{"a":{},"b":{}}}""");

        var ex = Assert.Throws<ConfigException>(() => config.Resolve("nope"));
        Assert.Contains("nope", ex.Message);
        Assert.Contains("a", ex.Message);
        Assert.Contains("b", ex.Message);
    }

    [Fact]
    public void A_default_profile_that_is_not_defined_says_which_is_wrong()
    {
        // A different mistake from asking for a missing profile, and worth a different message:
        // the file itself is inconsistent.
        var config = ConfigLoader.Parse("""{"defaultProfile":"ghost","profiles":{"a":{}}}""");

        var ex = Assert.Throws<ConfigException>(() => config.Resolve(null));
        Assert.Contains("default profile", ex.Message);
        Assert.Contains("ghost", ex.Message);
    }

    [Fact]
    public void No_default_and_no_request_resolves_to_no_profile()
    {
        Assert.Null(ConfigLoader.Parse("""{"profiles":{"a":{}}}""").Resolve(null));
    }

    // ---------------------------------------------------------------- ${env:…}

    [Fact]
    public void An_env_reference_is_expanded_when_the_file_is_read()
    {
        var config = ConfigLoader.Parse(
            """{"profiles":{"corp":{"proxyUser":"svc:${env:PROXY_PASS}"}}}""",
            source: null,
            environment: Env(("PROXY_PASS", "s3cret")));

        Assert.Equal("svc:s3cret", config.Profiles["corp"].ProxyUser);
    }

    [Fact]
    public void A_missing_env_reference_names_the_profile_the_field_and_the_variable()
    {
        // Substituting nothing would send an empty credential and fail later, further away.
        // Raised when the profile is resolved, not when the file is parsed, so an unset variable
        // in one profile never stops a different profile from loading.
        var ex = Assert.Throws<ConfigException>(() => ConfigLoader.Parse(
            """{"profiles":{"corp":{"cert":"${env:NOT_SET}"}}}""",
            new ConfigSource("C:/x/certapi.config.json", "test"),
            Env()).Resolve("corp"));

        Assert.Contains("corp", ex.Message);
        Assert.Contains("cert", ex.Message);
        Assert.Contains("NOT_SET", ex.Message);
    }

    [Fact]
    public void An_env_reference_inside_a_header_value_is_expanded_too()
    {
        var config = ConfigLoader.Parse(
            """{"profiles":{"corp":{"headers":{"Authorization":"Bearer ${env:TOK}"}}}}""",
            source: null,
            environment: Env(("TOK", "abc123")));

        Assert.Equal("Bearer abc123", config.Profiles["corp"].Headers.Single().Value);
    }

    [Fact]
    public void A_value_with_no_reference_is_left_exactly_as_written()
    {
        var config = ConfigLoader.Parse("""{"profiles":{"a":{"cert":"CN=Plain $ Value"}}}""");

        Assert.Equal("CN=Plain $ Value", config.Profiles["a"].Cert);
        Assert.False(ConfigLoader.HasEnvironmentReference("CN=Plain $ Value"));
        Assert.True(ConfigLoader.HasEnvironmentReference("x ${env:Y} z"));
    }
}
