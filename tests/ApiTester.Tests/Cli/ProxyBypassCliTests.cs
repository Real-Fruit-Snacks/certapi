using System.IO;
using System.Linq;
using ApiTester.Cli;
using ApiTester.Core;

namespace ApiTester.Tests.Cli;

/// <summary>The command-line surface of the proxy bypass list: --noproxy itself, its refusal to
/// coexist with --no-proxy, the NO_PROXY environment fallback and its precedence against an explicit
/// flag and a saved request's own list, and the exit code a misconfiguration produces end to end.
/// The matching engine itself (suffix/CIDR/port rules) is ProxyBypass's own concern, covered by its
/// own tests; what these prove is that the flag and the environment reach it correctly.</summary>
public class ProxyBypassCliTests
{
    // Never the developer's real %AppData%\CertApiTester\state.json: certapi send always reads the
    // live state (for auto-token reuse), so every command-level test gets its own temp path.
    private static string TempState() => Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");

    private static TransportOptions Apply(params string[] tokens) =>
        TransportFlags.Parse(new Args(tokens), out _).ApplyTo(new TransportOptions());

    private static TransportOptions ApplyWithEnvironment(
        Func<string, string?> environment, TransportOptions baseline, params string[] tokens) =>
        TransportFlags.Parse(new Args(tokens), out _, environment).ApplyTo(baseline);

    [Fact]
    public void Noproxy_bypasses_the_listed_hosts_and_their_subdomains_but_not_an_unrelated_host()
    {
        var options = Apply("https://x", "--noproxy", "internal.corp,.corp");

        Assert.True(ProxyBypass.IsBypassed(options.NoProxy, new Uri("https://api.internal.corp/")));
        Assert.True(ProxyBypass.IsBypassed(options.NoProxy, new Uri("https://x.corp/")));
        Assert.False(ProxyBypass.IsBypassed(options.NoProxy, new Uri("https://example.com/")));
    }

    [Fact]
    public void Noproxy_together_with_no_proxy_throws_the_shared_usage_message()
    {
        var ex = Assert.Throws<CliUsageException>(() =>
            TransportFlags.Parse(new Args(new[] { "--no-proxy", "--noproxy", "internal.corp" }), out _));

        Assert.Equal(ProxyBypass.NoProxyWithProxyOffMessage, ex.Message);
    }

    [Fact]
    public void A_blank_noproxy_value_is_a_usage_error()
    {
        var ex = Assert.Throws<CliUsageException>(() =>
            TransportFlags.Parse(new Args(new[] { "--noproxy", "   " }), out _));

        Assert.Contains("--noproxy", ex.Message);
    }

    [Fact]
    public void A_malformed_noproxy_entry_is_a_usage_error_naming_the_offending_entry()
    {
        var ex = Assert.Throws<CliUsageException>(() =>
            TransportFlags.Parse(new Args(new[] { "--noproxy", "a*b.corp" }), out _));

        Assert.Contains("a*b.corp", ex.Message);
    }

    [Fact]
    public void With_no_noproxy_flag_an_injected_NO_PROXY_variable_is_honored()
    {
        Func<string, string?> env = name => name == "NO_PROXY" ? "internal.corp" : null;

        var options = ApplyWithEnvironment(env, new TransportOptions(), "https://x");

        Assert.True(ProxyBypass.IsBypassed(options.NoProxy, new Uri("https://internal.corp/")));
    }

    [Fact]
    public void An_explicit_noproxy_flag_wins_over_the_injected_NO_PROXY_variable()
    {
        Func<string, string?> env = name => name == "NO_PROXY" ? "from-env.corp" : null;

        var options = ApplyWithEnvironment(env, new TransportOptions(), "https://x", "--noproxy", "from-flag.corp");

        Assert.True(ProxyBypass.IsBypassed(options.NoProxy, new Uri("https://from-flag.corp/")));
        Assert.False(ProxyBypass.IsBypassed(options.NoProxy, new Uri("https://from-env.corp/")));
    }

    [Fact]
    public void No_proxy_flag_with_an_injected_NO_PROXY_variable_parses_clean_with_an_empty_bypass_list()
    {
        Func<string, string?> env = name => name == "NO_PROXY" ? "internal.corp" : null;

        var overrides = TransportFlags.Parse(new Args(new[] { "https://x", "--no-proxy" }), out _, env);

        Assert.Equal(ProxyMode.None, overrides.Proxy);
        var options = overrides.ApplyTo(new TransportOptions());
        Assert.Empty(options.NoProxy);
    }

    [Fact]
    public void A_saved_requests_own_bypass_list_wins_over_an_injected_NO_PROXY_variable()
    {
        Func<string, string?> env = name => name == "NO_PROXY" ? "from-env.corp" : null;
        var saved = new TransportOptions { NoProxy = ProxyBypass.ParseLenient("from-saved.corp") };

        var options = ApplyWithEnvironment(env, saved, "https://x");

        Assert.True(ProxyBypass.IsBypassed(options.NoProxy, new Uri("https://from-saved.corp/")));
        Assert.False(ProxyBypass.IsBypassed(options.NoProxy, new Uri("https://from-env.corp/")));
    }

    [Fact]
    public void An_explicit_noproxy_flag_wins_over_a_saved_requests_own_bypass_list()
    {
        var saved = new TransportOptions { NoProxy = ProxyBypass.ParseLenient("from-saved.corp") };

        var options = TransportFlags.Parse(new Args(new[] { "https://x", "--noproxy", "from-flag.corp" }), out _)
            .ApplyTo(saved);

        Assert.True(ProxyBypass.IsBypassed(options.NoProxy, new Uri("https://from-flag.corp/")));
        Assert.False(ProxyBypass.IsBypassed(options.NoProxy, new Uri("https://from-saved.corp/")));
    }

    [Fact]
    public void A_malformed_injected_NO_PROXY_variable_is_warned_about_and_skipped_not_fatal()
    {
        // Ambient, not typed for this run: refusing every command on the machine over a variable
        // somebody else set helped nobody. The entry is named once and ignored.
        Func<string, string?> env = name => name == "NO_PROXY" ? "a*b.corp,good.corp" : null;
        var warnings = new List<string>();
        TransportFlags.Warn = warnings.Add;
        try
        {
            var t = TransportFlags.Parse(new Args(new[] { "https://x" }), out _, env).ApplyTo(new TransportOptions());
            Assert.Single(warnings);
            Assert.Contains("NO_PROXY", warnings[0]);
            Assert.Contains("a*b.corp", warnings[0]);
            Assert.True(ProxyBypass.IsBypassed(t.NoProxy, new Uri("https://good.corp/")));
        }
        finally { TransportFlags.Warn = null; }
    }

    [Fact]
    public void Noproxy_with_no_proxy_exits_with_the_usage_code_and_the_shared_message_end_to_end()
    {
        var stderr = new StringWriter();
        int code = CliApp.Run(
            new[] { "send", "https://example.invalid/", "--no-proxy", "--noproxy", "internal.corp" },
            new StringWriter(), stderr, new MemoryStream(), new CliServices { LiveStatePath = TempState() });

        Assert.Equal(ExitCodes.Usage, code);
        Assert.Contains(ProxyBypass.NoProxyWithProxyOffMessage, stderr.ToString());
    }

    /// <summary>certapi send against a real loopback server, over a real mTLS handshake, with a
    /// deliberately unreachable proxy: the bypass rule must send the request direct without ever
    /// dialling that proxy, and --debug must name the rule responsible rather than just saying "no",
    /// which is indistinguishable from never having had a proxy at all.</summary>
    [Fact]
    public async Task Debug_output_names_the_bypass_rule_that_sent_a_proxyable_request_direct()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("ProxyBypassCliClient", ca, false, true);
        await using var server = await LoopbackMtlsServer.StartAsync(serverCert, clientCert.Thumbprint!);

        var services = new CliServices
        {
            LiveStatePath = TempState(),
            ListCertificates = _ => new[]
            {
                new CertificateInfo
                {
                    Subject = "CN=ProxyBypassCliClient", Issuer = "CN=CA", Thumbprint = clientCert.Thumbprint!,
                    NotBefore = DateTime.Now.AddDays(-1), NotAfter = DateTime.Now.AddDays(30),
                    HasClientAuthEku = true, Certificate = clientCert
                }
            }
        };

        var stderr = new StringWriter();
        int code = CliApp.Run(
            new[]
            {
                "send", server.BaseUrl, "--cert", "ProxyBypassCliClient", "--insecure", "--debug",
                "--proxy", "http://127.0.0.1:9", "--noproxy", "127.0.0.1"
            },
            new StringWriter(), stderr, new MemoryStream(), services);

        Assert.Equal(ExitCodes.Ok, code);
        Assert.Contains("proxy no (bypassed by '127.0.0.1')", stderr.ToString());
    }
}
