using System.IO;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using ApiTester.Cli;
using ApiTester.Core;

namespace ApiTester.Tests;

/// <summary>--ca-file, --tls-min, --connect-timeout, --sni and proxy-URL credentials: the flags
/// that exist for a network with a TLS-intercepting proxy, a private root handed out as a PEM, or
/// an explicit proxy that only Basic-authenticates.</summary>
public class TransportTrustTests
{
    private static (X509Certificate2 ca, X509Certificate2 leaf) Pair(string host = "api.corp")
    {
        var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("Corp Interception Root");
        var leaf = SelfSignedCertificateFactory.CreateSignedCertificate(host, ca, true, false, new[] { host });
        return (ca, leaf);
    }

    private static string PemFile(params X509Certificate2[] certs)
    {
        string path = Path.Combine(Path.GetTempPath(), "certapi-ca-" + Guid.NewGuid().ToString("N") + ".pem");
        File.WriteAllText(path, string.Join("\n", certs.Select(c => c.ExportCertificatePem())));
        return path;
    }

    [Fact]
    public void A_pem_bundle_loads_every_certificate_and_fingerprints_by_content()
    {
        var (ca, leaf) = Pair();
        using (ca) using (leaf)
        {
            string one = PemFile(ca), both = PemFile(leaf, ca);
            try
            {
                var set = ExtraRootSet.Load(new[] { both });
                Assert.Equal(2, set.Count);
                // Order and file name do not matter; the certificates do.
                Assert.Equal(ExtraRootSet.Load(new[] { PemFile(ca, leaf) }).Fingerprint, set.Fingerprint);
                Assert.NotEqual(ExtraRootSet.Load(new[] { one }).Fingerprint, set.Fingerprint);
            }
            finally { File.Delete(one); File.Delete(both); }
        }
    }

    [Fact]
    public void A_missing_or_empty_ca_file_is_refused_not_trusted_as_nothing()
    {
        Assert.Throws<CertificateFileException>(() => ExtraRootSet.Load(new[] { Path.Combine(Path.GetTempPath(), "nope-" + Guid.NewGuid() + ".pem") }));
        string empty = Path.GetTempFileName();
        try { Assert.Throws<CertificateFileException>(() => ExtraRootSet.Load(new[] { empty })); }
        finally { File.Delete(empty); }
    }

    [Fact]
    public void A_chain_ending_in_a_named_root_loses_its_trust_errors_and_nothing_else()
    {
        var (ca, leaf) = Pair();
        using (ca) using (leaf)
        {
            string path = PemFile(ca);
            try
            {
                var roots = ExtraRootSet.Load(new[] { path });
                using var chain = new X509Chain();
                chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                chain.ChainPolicy.DisableCertificateDownloads = true;
                chain.Build(leaf);   // untrusted root on this machine, by construction

                var errors = SslPolicyErrors.RemoteCertificateChainErrors;
                var flags = X509ChainStatusFlags.UntrustedRoot | X509ChainStatusFlags.PartialChain;
                var (judgedErrors, judgedFlags) = ExtraRoots.Reconsider(errors, flags, leaf, chain, roots);
                Assert.Equal(SslPolicyErrors.None, judgedErrors);
                Assert.Equal(X509ChainStatusFlags.NoError, judgedFlags);

                // A name mismatch is not a trust problem the root can vouch for.
                var (stillWrong, _) = ExtraRoots.Reconsider(
                    errors | SslPolicyErrors.RemoteCertificateNameMismatch, flags, leaf, chain, roots);
                Assert.True(stillWrong.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch));
                Assert.False(stillWrong.HasFlag(SslPolicyErrors.RemoteCertificateChainErrors));

                // An expired chain keeps its flag and its error.
                var (expiredErrors, expiredFlags) = ExtraRoots.Reconsider(
                    errors, flags | X509ChainStatusFlags.NotTimeValid, leaf, chain, roots);
                Assert.Equal(errors, expiredErrors);
                Assert.True(expiredFlags.HasFlag(X509ChainStatusFlags.NotTimeValid));

                // Revocation-unknown survives for RevocationCheck.Decide to rule on.
                var (unknownErrors, unknownFlags) = ExtraRoots.Reconsider(
                    errors, flags | X509ChainStatusFlags.RevocationStatusUnknown, leaf, chain, roots);
                Assert.True(unknownErrors.HasFlag(SslPolicyErrors.RemoteCertificateChainErrors));
                Assert.Equal(X509ChainStatusFlags.RevocationStatusUnknown, unknownFlags);
            }
            finally { File.Delete(path); }
        }
    }

    [Fact]
    public void A_root_that_did_not_sign_the_leaf_changes_nothing()
    {
        var (ca, leaf) = Pair();
        var (otherCa, _) = Pair("other");
        using (ca) using (leaf) using (otherCa)
        {
            string path = PemFile(otherCa);
            try
            {
                var roots = ExtraRootSet.Load(new[] { path });
                var errors = SslPolicyErrors.RemoteCertificateChainErrors;
                var flags = X509ChainStatusFlags.UntrustedRoot;
                Assert.Equal((errors, flags), ExtraRoots.Reconsider(errors, flags, leaf, null, roots));
            }
            finally { File.Delete(path); }
        }
    }

    [Fact]
    public void Without_roots_or_without_chain_errors_reconsider_is_the_identity()
    {
        var (ca, leaf) = Pair();
        using (ca) using (leaf)
        {
            Assert.Equal((SslPolicyErrors.RemoteCertificateChainErrors, X509ChainStatusFlags.UntrustedRoot),
                ExtraRoots.Reconsider(SslPolicyErrors.RemoteCertificateChainErrors, X509ChainStatusFlags.UntrustedRoot, leaf, null, null));
            string path = PemFile(ca);
            try
            {
                var roots = ExtraRootSet.Load(new[] { path });
                Assert.Equal((SslPolicyErrors.None, X509ChainStatusFlags.NoError),
                    ExtraRoots.Reconsider(SslPolicyErrors.None, X509ChainStatusFlags.NoError, leaf, null, roots));
            }
            finally { File.Delete(path); }
        }
    }

    [Fact]
    public void Ca_file_tls_min_connect_timeout_and_sni_flow_into_transport_options()
    {
        var (ca, _) = Pair();
        using (ca)
        {
            string path = PemFile(ca);
            try
            {
                var args = new Args(new[] { "--ca-file", path, "--tls-min", "1.3", "--connect-timeout", "4", "--sni", "edge.corp" });
                var t = TransportFlags.Parse(args, out _).ApplyTo(new TransportOptions());
                Assert.Equal(1, t.ExtraRoots!.Count);
                Assert.Equal(TlsFloor.Tls13, t.MinTls);
                Assert.Equal(TimeSpan.FromSeconds(4), t.ConnectTimeout);
                Assert.Equal("edge.corp", t.Sni);
                Assert.Empty(args.Positionals());

                // The handler key tells them apart: a different root set is a different connection.
                var a = HandlerKey.For(null, t, null, null);
                var b = HandlerKey.For(null, t with { ExtraRoots = null }, null, null);
                var c = HandlerKey.For(null, t with { MinTls = TlsFloor.Tls12 }, null, null);
                Assert.NotEqual(a, b);
                Assert.NotEqual(a, c);
            }
            finally { File.Delete(path); }
        }
    }

    [Theory]
    [InlineData("1.1")]
    [InlineData("tls")]
    [InlineData("")]
    public void Tls_min_refuses_anything_but_1_2_or_1_3(string raw) =>
        Assert.Throws<CliUsageException>(() => TransportFlags.Parse(new Args(new[] { "--tls-min", raw }), out _));

    [Fact]
    public void Sni_is_refused_with_a_proxy_because_the_tunnel_negotiates_tls_itself() =>
        Assert.Throws<CliUsageException>(() =>
            TransportFlags.Parse(new Args(new[] { "--sni", "x.corp", "--proxy", "http://proxy:8080" }), out _));

    [Fact]
    public void Credentials_in_the_proxy_url_become_proxy_user_and_leave_the_url()
    {
        var t = TransportFlags.Parse(new Args(new[] { "--proxy", "http://svc:p%40ss:word@proxy.corp:8080" }), out _);
        Assert.Equal("http://proxy.corp:8080/", t.ProxyUrl);
        Assert.Equal("svc", t.ProxyUser);
        Assert.Equal("p@ss:word", t.ProxyPassword);
        Assert.Equal(ProxyMode.Explicit, t.Proxy);

        // --proxy-user still wins when both are given; the URL is stripped either way.
        var both = TransportFlags.Parse(new Args(new[] { "--proxy", "http://a:b@proxy.corp:8080", "--proxy-user", "c:d" }), out _);
        Assert.Equal("c", both.ProxyUser);
        Assert.Equal("http://proxy.corp:8080/", both.ProxyUrl);

        var plain = TransportFlags.SplitProxyUserInfo("socks5://127.0.0.1:1080");
        Assert.Equal(("socks5://127.0.0.1:1080", (string?)null, (string?)null), plain);
    }

    [Fact]
    public void The_profile_supplies_ca_files_tls_min_and_connect_timeout_when_the_flags_do_not()
    {
        var (ca, _) = Pair();
        using (ca)
        {
            string path = PemFile(ca);
            try
            {
                var profile = new ConfigProfile { CaFiles = new[] { path }, TlsMin = "1.2", ConnectTimeout = 7 };
                var t = TransportFlags.Parse(new Args(Array.Empty<string>()), out _, environment: null, profile).ApplyTo(new TransportOptions());
                Assert.Equal(1, t.ExtraRoots!.Count);
                Assert.Equal(TlsFloor.Tls12, t.MinTls);
                Assert.Equal(TimeSpan.FromSeconds(7), t.ConnectTimeout);

                var stream = TransportFlags.ParseStreamSubset(new Args(Array.Empty<string>()), false, null, profile);
                Assert.Equal(1, stream.ExtraRoots!.Count);
                Assert.Equal(TlsFloor.Tls12, stream.MinTls);
            }
            finally { File.Delete(path); }
        }
    }

    [Fact]
    public void Proxy_configuration_lifts_url_credentials_for_a_saved_request()
    {
        var handler = new System.Net.Http.SocketsHttpHandler();
        ProxyConfiguration.Apply(handler, new TransportOptions { Proxy = ProxyMode.Explicit, ProxyUrl = "http://u:p@proxy.corp:3128" });
        var proxy = Assert.IsType<System.Net.WebProxy>(handler.Proxy);
        Assert.Equal("http://proxy.corp:3128/", proxy.Address!.ToString());
        var cred = Assert.IsType<System.Net.NetworkCredential>(proxy.Credentials);
        Assert.Equal("u", cred.UserName);
        Assert.Equal("p", cred.Password);
    }

    [Fact]
    public void Config_show_never_prints_credentials()
    {
        var json = """{"profiles":{"corp":{"proxy":"http://svc:hunter2@proxy.corp:8080","headers":{"Authorization":"Bearer abc","X-Api-Key":"k123","X-Trace":"on"}}}}""";
        string dir = Path.Combine(Path.GetTempPath(), "certapi-cfg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, "certapi.config.json");
        File.WriteAllText(file, json);
        try
        {
            // The loader marks the env-sourced header as secret; `show` masks it and the
            // credential-shaped names, and strips the proxy URL's userinfo.
            var withEnv = ConfigLoader.Parse("""{"profiles":{"corp":{"headers":{"X-Env":"${env:T}","X-Plain":"v"}}}}""",
                null, name => name == "T" ? "fromenv" : null).Resolve("corp")!;
            Assert.Contains("X-Env", withEnv.SecretHeaderNames);
            Assert.DoesNotContain("X-Plain", withEnv.SecretHeaderNames);

            var stdout = new StringWriter();
            int exit = CliApp.Run(new[] { "config", "show", "--config", file, "--profile", "corp" }, stdout, TextWriter.Null);
            string shown = stdout.ToString();
            Assert.Equal(ExitCodes.Ok, exit);
            Assert.DoesNotContain("hunter2", shown);
            Assert.DoesNotContain("Bearer abc", shown);
            Assert.DoesNotContain("k123", shown);
            Assert.Contains("X-Trace: on", shown);
            Assert.Contains("proxy.corp:8080", shown);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void A_broken_profile_does_not_stop_a_different_one_from_loading()
    {
        var json = """{"profiles":{"prod":{"cert":"${env:NOPE_UNSET}"},"dev":{"cert":"CN=dev"}}}""";
        var config = ConfigLoader.Parse(json, null, _ => null);
        Assert.Equal(2, config.Profiles.Count);
        Assert.Equal("CN=dev", config.Resolve("dev")!.Cert);
        Assert.Throws<ConfigException>(() => config.Resolve("prod"));
    }
}

public class ProxyFailoverTests
{
    private static TransportOptions SystemWithBypass() => new()
    {
        Proxy = ProxyMode.System,
        NoProxy = ProxyBypass.TryParse("internal.corp,10.0.0.0/8", out var rules, out _) ? rules : throw new Exception()
    };

    [Fact]
    public void A_destination_the_bypass_list_does_not_match_leaves_the_system_proxy_untouched()
    {
        // The handler keeps consulting HttpClient.DefaultProxy as .NET built it — a PAC's
        // "PROXY a; PROXY b; DIRECT" failover list included — instead of a decorator that can only
        // ever answer with the first proxy.
        var handler = new System.Net.Http.SocketsHttpHandler();
        ProxyConfiguration.Apply(handler, SystemWithBypass(), new Uri("https://sso.vendor.com/login"));
        Assert.Null(handler.Proxy);
        Assert.True(handler.UseProxy);
    }

    [Fact]
    public void A_destination_the_bypass_list_matches_or_no_destination_keeps_the_decorator()
    {
        var matched = new System.Net.Http.SocketsHttpHandler();
        ProxyConfiguration.Apply(matched, SystemWithBypass(), new Uri("https://api.internal.corp/x"));
        Assert.IsType<ProxyConfiguration.BypassingProxy>(matched.Proxy);
        Assert.True(matched.Proxy!.IsBypassed(new Uri("https://api.internal.corp/x")));

        var unknown = new System.Net.Http.SocketsHttpHandler();
        ProxyConfiguration.Apply(unknown, SystemWithBypass(), destination: null);
        Assert.IsType<ProxyConfiguration.BypassingProxy>(unknown.Proxy);
    }

    [Fact]
    public void An_explicit_proxy_is_always_decorated_because_it_has_no_failover_to_lose()
    {
        var handler = new System.Net.Http.SocketsHttpHandler();
        var t = SystemWithBypass() with { Proxy = ProxyMode.Explicit, ProxyUrl = "http://proxy.corp:8080" };
        ProxyConfiguration.Apply(handler, t, new Uri("https://sso.vendor.com/"));
        Assert.IsType<ProxyConfiguration.BypassingProxy>(handler.Proxy);
        Assert.False(handler.Proxy!.IsBypassed(new Uri("https://sso.vendor.com/")));
        Assert.True(handler.Proxy.IsBypassed(new Uri("https://x.internal.corp/")));
    }
}

public class PacProxyTests
{
    [Fact]
    public void A_pac_answer_is_mapped_to_a_proxy_once_per_host_and_direct_is_honoured()
    {
        int calls = 0;
        var pac = new ProxyConfiguration.PacWebProxy("C:/corp.pac", (_, url) =>
        {
            calls++;
            return url.Contains("internal.corp") ? (null, null) : ("PROXY proxy.corp:8080; PROXY backup.corp:8080; DIRECT", null);
        });
        Assert.Equal(new Uri("http://proxy.corp:8080"), pac.GetProxy(new Uri("https://sso.vendor.com/a")));
        Assert.Equal(new Uri("http://proxy.corp:8080"), pac.GetProxy(new Uri("https://sso.vendor.com/b")));
        Assert.Equal(1, calls);                                                     // cached per host
        Assert.Null(pac.GetProxy(new Uri("https://api.internal.corp/x")));
        Assert.True(pac.IsBypassed(new Uri("https://api.internal.corp/x")));
        Assert.Equal(2, calls);
    }

    [Fact]
    public void A_pac_that_cannot_be_evaluated_fails_the_request_loudly_rather_than_going_direct()
    {
        var pac = new ProxyConfiguration.PacWebProxy("C:/corp.pac", (_, _) => (null, "script has an error"));
        var ex = Assert.Throws<InvalidOperationException>(() => pac.GetProxy(new Uri("https://x.example/")));
        Assert.Contains("script has an error", ex.Message);
    }

    [Fact]
    public void A_named_pac_replaces_the_system_decision_on_the_handler_and_in_the_key()
    {
        var t = new TransportOptions { PacUrl = "C:/corp.pac" };
        var handler = new System.Net.Http.SocketsHttpHandler();
        ProxyConfiguration.Apply(handler, t);
        Assert.IsType<ProxyConfiguration.PacWebProxy>(handler.Proxy);
        Assert.NotEqual(HandlerKey.For(null, t, null, null), HandlerKey.For(null, new TransportOptions(), null, null));

        var off = new System.Net.Http.SocketsHttpHandler();
        ProxyConfiguration.Apply(off, t with { Proxy = ProxyMode.None });
        Assert.False(off.UseProxy);
    }

    [Fact]
    public void The_flag_refuses_proxy_and_no_proxy_beside_it()
    {
        Assert.Throws<CliUsageException>(() => TransportFlags.Parse(new Args(new[] { "--pac", "c.pac", "--proxy", "http://p:1" }), out _));
        Assert.Throws<CliUsageException>(() => TransportFlags.Parse(new Args(new[] { "--pac", "c.pac", "--no-proxy" }), out _));
        if (!OperatingSystem.IsWindows())
            Assert.Throws<CliUsageException>(() => TransportFlags.Parse(new Args(new[] { "--pac", "c.pac" }), out _));
        else
            Assert.Equal("c.pac", TransportFlags.Parse(new Args(new[] { "--pac", "c.pac" }), out _).PacUrl);
    }
}

public class NoEnvProxyTests
{
    [Fact]
    public void The_flag_clears_every_proxy_variable_for_the_process_and_refuses_no_proxy()
    {
        var cleared = new List<string>();
        TransportFlags.clearEnvironment = cleared.Add;
        try
        {
            TransportFlags.Parse(new Args(new[] { "--no-env-proxy" }), out _, environment: _ => null);
            Assert.Equal(TransportFlags.EnvironmentProxyVariables, cleared);
            cleared.Clear();

            TransportFlags.ParseStreamSubset(new Args(new[] { "--no-env-proxy" }), false, environment: _ => null);
            Assert.Contains("HTTPS_PROXY", cleared);

            Assert.Throws<CliUsageException>(() =>
                TransportFlags.Parse(new Args(new[] { "--no-env-proxy", "--no-proxy" }), out _, environment: _ => null));

            cleared.Clear();
            TransportFlags.Parse(new Args(Array.Empty<string>()), out _, environment: _ => null);
            Assert.Empty(cleared);   // nothing touched unless asked
        }
        finally { TransportFlags.clearEnvironment = null; }
    }
}

public class OfflineTests
{
    [Fact]
    public void Offline_builds_the_chain_without_downloads_and_carries_the_revocation_mode_on_the_policy()
    {
        var ssl = new System.Net.Security.SslClientAuthenticationOptions();
        RevocationCheck.Apply(ssl, new TransportOptions { Offline = true, Revocation = RevocationMode.Offline });
        Assert.NotNull(ssl.CertificateChainPolicy);
        Assert.True(ssl.CertificateChainPolicy!.DisableCertificateDownloads);
        Assert.Equal(System.Security.Cryptography.X509Certificates.X509RevocationMode.Offline, ssl.CertificateChainPolicy.RevocationMode);

        var online = new System.Net.Security.SslClientAuthenticationOptions();
        RevocationCheck.Apply(online, new TransportOptions { Revocation = RevocationMode.Online });
        Assert.Null(online.CertificateChainPolicy);
        Assert.Equal(System.Security.Cryptography.X509Certificates.X509RevocationMode.Online, online.CertificateRevocationCheckMode);
    }

    [Fact]
    public void The_flag_refuses_online_revocation_and_is_part_of_the_handler_key()
    {
        var t = TransportFlags.Parse(new Args(new[] { "--offline", "--revocation", "offline" }), out _).ApplyTo(new TransportOptions());
        Assert.True(t.Offline);
        Assert.Equal(RevocationMode.Offline, t.Revocation);
        Assert.Throws<CliUsageException>(() => TransportFlags.Parse(new Args(new[] { "--offline", "--revocation", "online" }), out _));
        Assert.Throws<CliUsageException>(() => TransportFlags.ParseStreamSubset(new Args(new[] { "--offline", "--revocation", "online" }), false));
        Assert.NotEqual(HandlerKey.For(null, t, null, null), HandlerKey.For(null, t with { Offline = false }, null, null));
    }

    [Fact]
    public async Task Doctor_does_not_probe_the_internet_when_offline()
    {
        bool probed = false;
        var report = await ConnectionDoctor.RunAsync("https://127.0.0.1:1/", null,
            Array.Empty<System.Security.Cryptography.X509Certificates.X509Certificate2>(),
            new TransportOptions { Offline = true, Proxy = ProxyMode.None },
            probe: _ => { probed = true; return Task.FromResult<string?>("Microsoft Connect Test"); });
        Assert.False(probed);
        Assert.Contains("--offline", report.FirstFailure?.Advice ?? "");
    }
}

public class AddressFamilyTests
{
    [Fact]
    public async Task A_literal_is_returned_as_is_and_a_family_with_no_address_is_a_named_failure()
    {
        var v4 = await AddressSelection.ResolveAsync("127.0.0.1", AddressFamilyPreference.IPv6, default);
        Assert.Equal(System.Net.IPAddress.Loopback, v4.Single());   // a literal is dialled as written

        var any = await AddressSelection.ResolveAsync("localhost", AddressFamilyPreference.Any, default);
        Assert.NotEmpty(any);
        var only4 = await AddressSelection.ResolveAsync("localhost", AddressFamilyPreference.IPv4, default);
        Assert.All(only4, a => Assert.Equal(System.Net.Sockets.AddressFamily.InterNetwork, a.AddressFamily));
    }

    [Fact]
    public void The_flags_are_exclusive_refuse_a_proxy_and_reach_the_key()
    {
        Assert.Equal(AddressFamilyPreference.IPv4, TransportFlags.Parse(new Args(new[] { "-4" }), out _).ApplyTo(new TransportOptions()).AddressFamily);
        Assert.Equal(AddressFamilyPreference.IPv6, TransportFlags.Parse(new Args(new[] { "--ipv6" }), out _).ApplyTo(new TransportOptions()).AddressFamily);
        Assert.Throws<CliUsageException>(() => TransportFlags.Parse(new Args(new[] { "-4", "-6" }), out _));
        Assert.Throws<CliUsageException>(() => TransportFlags.Parse(new Args(new[] { "-4", "--proxy", "http://p:1" }), out _));
        var t = new TransportOptions { AddressFamily = AddressFamilyPreference.IPv4 };
        Assert.NotEqual(HandlerKey.For(null, t, null, null), HandlerKey.For(null, new TransportOptions(), null, null));
    }

    [Fact]
    public async Task A_direct_send_with_dash_4_reaches_a_loopback_server()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("Client", ca, false, true);
        await using var server = await LoopbackMtlsServer.StartAsync(serverCert, clientCert.Thumbprint!);
        var resp = await new ApiClient().SendAsync(
            new ApiRequest { Method = System.Net.Http.HttpMethod.Get, Url = server.BaseUrl.Replace("127.0.0.1", "localhost") }, clientCert,
            transport: new TransportOptions { Proxy = ProxyMode.None, IgnoreServerCertificateErrors = true, AddressFamily = AddressFamilyPreference.IPv4 });
        Assert.True(resp.IsSuccess, resp.Error?.Message);
    }
}
