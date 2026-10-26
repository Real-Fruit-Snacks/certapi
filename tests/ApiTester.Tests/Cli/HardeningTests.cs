using ApiTester.Cli;
using ApiTester.Cli.Commands;
using ApiTester.Core;

namespace ApiTester.Tests.Cli;

/// <summary>The smaller correctness fixes from the transport/trust/CLI review: argument forms,
/// thumbprint and host spelling on `trust add`, the proxy-engine comparison, cookie paths under a
/// mount, and the certificate-file loader's less common inputs.</summary>
public class HardeningTests
{
    [Fact]
    public void Options_accept_the_equals_form_and_split_only_option_shaped_tokens()
    {
        var args = new Args(new[] { "--cert=CN=matt", "--timeout=30", "a=b", "--insecure" });
        Assert.Equal("CN=matt", args.Value("--cert"));
        Assert.Equal("30", args.Value("--timeout"));
        Assert.True(args.Flag("--insecure"));
        Assert.Equal(new[] { "a=b" }, args.Positionals());
    }

    [Fact]
    public void Double_dash_ends_option_parsing()
    {
        var args = new Args(new[] { "--insecure", "--", "--not-an-option", "-x" });
        Assert.True(args.Flag("--insecure"));
        Assert.Equal(new[] { "--not-an-option", "-x" }, args.Positionals());
    }

    [Fact]
    public void An_option_shaped_token_is_not_swallowed_as_a_value()
    {
        var args = new Args(new[] { "--cert", "--insecure" });
        var ex = Assert.Throws<CliUsageException>(() => args.Value("--cert"));
        Assert.Contains("needs a value", ex.Message);
        Assert.Contains("--insecure", ex.Message);
    }

    [Fact]
    public void A_negative_number_is_still_a_value()
    {
        var args = new Args(new[] { "--retry", "-1" });
        Assert.Equal("-1", args.Value("--retry"));
    }

    [Fact]
    public void A_repeated_single_value_option_says_so()
    {
        var args = new Args(new[] { "--cert", "a", "--cert", "b" });
        args.Value("--cert");
        var ex = Assert.Throws<CliUsageException>(() => args.Positionals());
        Assert.Contains("more than once", ex.Message);
    }

    [Theory]
    [InlineData("0123456789abcdef0123456789abcdef01234567", "0123456789ABCDEF0123456789ABCDEF01234567")]
    [InlineData("01:23:45:67:89:ab:cd:ef:01:23:45:67:89:ab:cd:ef:01:23:45:67", "0123456789ABCDEF0123456789ABCDEF01234567")]
    [InlineData("01 23 45 67 89 AB CD EF 01 23 45 67 89 AB CD EF 01 23 45 67", "0123456789ABCDEF0123456789ABCDEF01234567")]
    [InlineData("ABC123", null)]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", null)]
    [InlineData("0123456789abcdef0123456789abcdef0123456g", null)]
    public void Trust_thumbprints_are_normalised_or_refused(string raw, string? expected) =>
        Assert.Equal(expected, TrustCommand.NormalizeThumbprint(raw));

    [Theory]
    [InlineData("internal.corp", "internal.corp", null)]
    [InlineData("Internal.Corp", "internal.corp", null)]
    [InlineData("https://internal.corp/", "internal.corp", "the scheme")]
    [InlineData("https://internal.corp:8443/api", "internal.corp", "the scheme, port and path")]
    [InlineData("internal.corp:8443", "internal.corp", "port 8443")]
    public void Trust_hosts_are_reduced_to_the_bare_host(string raw, string expected, string? dropped)
    {
        Assert.Equal(expected, TrustCommand.NormalizeHost(raw, out var what));
        Assert.Equal(dropped, what);
    }

    [Theory]
    [InlineData("proxy.corp:8080", "http://proxy.corp:8080/", false)]
    [InlineData("PROXY.corp:8080;backup.corp:8080", "http://backup.corp:8080/", false)]
    [InlineData("https=proxy.corp:8080", "http://proxy.corp:8080", false)]
    [InlineData("proxy.corp:8080", "http://other.corp:8080/", true)]
    [InlineData("", "http://proxy.corp:8080/", true)]
    [InlineData(null, null, false)]
    [InlineData("DIRECT", null, false)]
    public void Proxy_engines_are_compared_as_host_and_port(string? winhttp, string? dotnet, bool disagrees) =>
        Assert.Equal(disagrees, new ProxyDecision("https://x", winhttp, null, dotnet).Disagrees);

    [Fact]
    public void Cookie_paths_are_rerooted_under_a_prefix_mount()
    {
        var route = new GatewayRoute("/auth", new Uri("https://login.internal"));
        var options = new BrowserOptions(false, null, RewriteCookies: true, false, false, new Uri("http://127.0.0.1:8819"));
        string Rewrite(string setCookie) =>
            BrowserRewriter.RewriteResponseHeaders(new[] { new KeyValuePair<string, string>("Set-Cookie", setCookie) },
                null, route, options, out _).Single().Value;

        Assert.Equal("sid=1; Path=/auth; HttpOnly", Rewrite("sid=1; Path=/; HttpOnly"));
        Assert.Equal("sid=1; Path=/auth/app", Rewrite("sid=1; Path=/app"));
        Assert.Equal("sid=1; HttpOnly; Path=/auth", Rewrite("sid=1; HttpOnly"));

        var root = new GatewayRoute("/", new Uri("https://login.internal"));
        Assert.Equal("sid=1; Path=/app",
            BrowserRewriter.RewriteResponseHeaders(new[] { new KeyValuePair<string, string>("Set-Cookie", "sid=1; Path=/app") },
                null, root, options, out _).Single().Value);
    }

    [Fact]
    public void A_pkcs12_file_with_a_key_file_is_refused_plainly()
    {
        string pfx = System.IO.Path.GetTempFileName() + ".pfx";
        string key = System.IO.Path.GetTempFileName();
        System.IO.File.WriteAllBytes(pfx, new byte[] { 0x30, 0x82, 0x01, 0x00, 0x02, 0x01, 0x03 });
        try
        {
            var ex = Assert.Throws<CertificateFileException>(() => CertificateFileLoader.Load(pfx, null, key));
            Assert.Contains("--key-file does not apply", ex.Message);
        }
        finally { System.IO.File.Delete(pfx); System.IO.File.Delete(key); }
    }

    [Fact]
    public void An_encrypted_pem_key_needs_the_password_and_then_loads()
    {
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var req = new System.Security.Cryptography.X509Certificates.CertificateRequest(
            "CN=enc", rsa, System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1);
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        var pbe = new System.Security.Cryptography.PbeParameters(
            System.Security.Cryptography.PbeEncryptionAlgorithm.Aes256Cbc, System.Security.Cryptography.HashAlgorithmName.SHA256, 10_000);
        string pem = System.IO.Path.GetTempFileName() + ".pem";
        System.IO.File.WriteAllText(pem, cert.ExportCertificatePem() + "\n" + rsa.ExportEncryptedPkcs8PrivateKeyPem("pw", pbe));
        try
        {
            var without = Assert.Throws<CertificateFileException>(() => CertificateFileLoader.Load(pem));
            Assert.Contains("encrypted", without.Message);
            using var loaded = CertificateFileLoader.Load(pem, "pw");
            Assert.True(loaded.HasPrivateKey);
            Assert.Equal("CN=enc", loaded.Subject);
        }
        finally { System.IO.File.Delete(pem); }
    }
}

public class Ipv6LiteralTests
{
    [Theory]
    [InlineData("::1", "::1")]
    [InlineData("[::1]", "::1")]
    [InlineData("https://[::1]:8443/x", "::1")]
    [InlineData("[FE80::1]", "fe80::1")]
    public void An_ipv6_host_is_pinned_without_brackets(string raw, string expected) =>
        Assert.Equal(expected, TrustCommand.NormalizeHost(raw, out _));

    [Fact]
    public void A_pin_matches_whichever_spelling_the_lookup_uses()
    {
        var state = new AppState();
        TrustService.Trust(state, "::1", "0123456789ABCDEF0123456789ABCDEF01234567", null);
        Assert.True(TrustService.IsTrusted(state, "[::1]", "0123456789ABCDEF0123456789ABCDEF01234567"));
        Assert.True(TrustService.IsTrusted(state, "::1", "0123456789ABCDEF0123456789ABCDEF01234567"));
        TrustService.Untrust(state, "[::1]");
        Assert.Empty(state.TrustedServerCerts);
    }

    [Fact]
    public void Resolve_accepts_a_bracketed_ipv6_host_and_an_ipv6_address()
    {
        var t = TransportFlags.Parse(new Args(new[] { "--resolve", "[::1]:443:::2", "--resolve", "api.corp:443:10.0.0.1", "--resolve", "api.corp:8443:fd00::5" }), out _);
        Assert.Equal(("::1", 443, "::2"), (t.Resolve[0].Host, t.Resolve[0].Port, t.Resolve[0].Address));
        Assert.Equal(("api.corp", 443, "10.0.0.1"), (t.Resolve[1].Host, t.Resolve[1].Port, t.Resolve[1].Address));
        Assert.Equal(("api.corp", 8443, "fd00::5"), (t.Resolve[2].Host, t.Resolve[2].Port, t.Resolve[2].Address));
        Assert.Throws<CliUsageException>(() => TransportFlags.Parse(new Args(new[] { "--resolve", "[::1]443:::2" }), out _));
    }
}

public class LenientNoProxyEnvironmentTests
{
    [Fact]
    public void A_bad_entry_in_the_environment_is_warned_about_and_skipped_while_the_flag_stays_strict()
    {
        var warnings = new List<string>();
        TransportFlags.Warn = warnings.Add;
        try
        {
            string? Env(string name) => name == "NO_PROXY" ? "internal.corp,10.*,<local>,.lab" : null;
            var t = TransportFlags.Parse(new Args(Array.Empty<string>()), out _, environment: Env).ApplyTo(new TransportOptions());
            // The readable entries apply …
            Assert.True(ProxyBypass.IsBypassed(t.NoProxy, new Uri("https://api.internal.corp/")));
            Assert.True(ProxyBypass.IsBypassed(t.NoProxy, new Uri("https://x.lab/")));
            // … and the unreadable ones are named once.
            Assert.Single(warnings);
            Assert.Contains("10.*", warnings[0]);
            Assert.Contains("--noproxy", warnings[0]);

            // --noproxy on the command line is still refused outright.
            Assert.Throws<CliUsageException>(() => TransportFlags.Parse(new Args(new[] { "--noproxy", "10.*" }), out _, environment: Env));
        }
        finally { TransportFlags.Warn = null; }
    }
}
