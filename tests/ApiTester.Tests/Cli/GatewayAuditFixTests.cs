using System.IO;
using System.Text;
using ApiTester.Cli;
using ApiTester.Cli.Commands;
using ApiTester.Core;

namespace ApiTester.Tests.Cli;

/// <summary>The gateway's defences against a web page and against its own configuration: a
/// WebSocket presents its site's identity, no public origin is answered without being listed, a
/// cross-site request or a rebinding Host is refused, and recordings, header rules, tokens and
/// mock scenarios cannot carry what they must not.</summary>
public class GatewayAuditFixTests
{
    private static BrowserOptions Browser(bool cors = true, IReadOnlyList<string>? origins = null) =>
        new(cors, origins, false, false, true, new Uri("http://127.0.0.1:8819"));

    private static List<KeyValuePair<string, string>> H(params (string, string)[] pairs) =>
        pairs.Select(p => new KeyValuePair<string, string>(p.Item1, p.Item2)).ToList();

    // ------------------------------------------------------------ HIGH-01: WebSockets and sites

    [Fact]
    public void A_websocket_to_a_site_uses_that_sites_certificate_and_trust()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var siteCert = SelfSignedCertificateFactory.CreateSignedCertificate("site-hr", ca, false, true);
        using var gatewayCert = SelfSignedCertificateFactory.CreateSignedCertificate("gateway-default", ca, false, true);
        var hr = new GatewayRoute("/hr", new Uri("https://hr.corp")) { Certificate = siteCert, Settings = new SiteSettings { Insecure = false } };
        using var gateway = new MtlsGateway(new GatewayRoutes(new[] { hr }), gatewayCert, ignoreServerCertificateErrors: true, TimeSpan.FromSeconds(5));

        Assert.Equal("/hr", gateway.SiteFor(new Uri("wss://hr.corp/socket"))?.PathPrefix.TrimEnd('/'));
        using var handler = gateway.CreateHandlerFor(GatewayWebSocketRelay.ToWebSocketUri(new Uri("https://hr.corp/socket")));
        Assert.Contains(handler.SslOptions.ClientCertificates!.Cast<System.Security.Cryptography.X509Certificates.X509Certificate>(),
            c => c.Subject.Contains("site-hr"));
        Assert.False(gateway.Effective(gateway.SiteFor(new Uri("wss://hr.corp/socket"))).Insecure);
    }

    [Fact]
    public void Two_mounts_on_one_host_keep_their_own_certificates_for_websockets()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var a = SelfSignedCertificateFactory.CreateSignedCertificate("mount-a", ca, false, true);
        using var b = SelfSignedCertificateFactory.CreateSignedCertificate("mount-b", ca, false, true);
        var ra = new GatewayRoute("/a", new Uri("https://one.corp")) { Certificate = a };
        var rb = new GatewayRoute("/b", new Uri("https://one.corp")) { Certificate = b };
        using var gateway = new MtlsGateway(new GatewayRoutes(new[] { ra, rb }), null, false, TimeSpan.FromSeconds(5));

        var route = gateway.ResolveTarget("/b/socket", out var target, out _);
        using var handler = gateway.CreateHandlerFor(GatewayWebSocketRelay.ToWebSocketUri(target!), route);
        Assert.Contains(handler.SslOptions.ClientCertificates!.Cast<System.Security.Cryptography.X509Certificates.X509Certificate>(),
            c => c.Subject.Contains("mount-b"));
    }

    // ------------------------------------------------------------ LOW-09: dynamic target for a site's host

    [Fact]
    public void A_dynamic_target_for_a_sites_host_is_routed_as_that_site()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var siteCert = SelfSignedCertificateFactory.CreateSignedCertificate("site-hr", ca, false, true);
        var hr = new GatewayRoute("/hr", new Uri("https://hr.corp")) { Certificate = siteCert, Name = "hr" };
        using var gateway = new MtlsGateway(new GatewayRoutes(new[] { hr }), null, false, TimeSpan.FromSeconds(5));
        gateway.EnableDynamicUpstreams(new UpstreamAllowlist(new[] { "*.corp" }));

        var route = gateway.ResolveTarget("/_/https://hr.corp/people", out _, out _);
        Assert.Same(siteCert, route!.Certificate);
        var other = gateway.ResolveTarget("/_/https://wiki.corp/", out _, out _);
        Assert.Null(other!.Certificate);
    }

    [Fact]
    public void A_dynamic_target_for_a_host_fronted_with_two_identities_is_refused()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var a = SelfSignedCertificateFactory.CreateSignedCertificate("a", ca, false, true);
        using var b = SelfSignedCertificateFactory.CreateSignedCertificate("b", ca, false, true);
        using var gateway = new MtlsGateway(new GatewayRoutes(new[]
        {
            new GatewayRoute("/a", new Uri("https://one.corp")) { Certificate = a },
            new GatewayRoute("/b", new Uri("https://one.corp")) { Certificate = b }
        }), null, false, TimeSpan.FromSeconds(5));
        gateway.EnableDynamicUpstreams(new UpstreamAllowlist(new[] { "*.corp" }));
        Assert.Throws<GatewaySiteAmbiguousException>(() => gateway.ResolveTarget("/_/https://one.corp/x", out _, out _));
    }

    // ------------------------------------------------------------ HIGH-02 / MED-04: origins

    [Fact]
    public void Browser_bundle_does_not_answer_cors_for_a_public_origin()
    {
        var preflight = new GatewayRequest("OPTIONS", "/x",
            H(("Origin", "https://evil.example"), ("Access-Control-Request-Method", "GET"), ("Access-Control-Request-Private-Network", "true")), null, null);
        Assert.Equal(403, BrowserRewriter.TryPreflight(preflight, Browser())!.StatusCode);

        var headers = BrowserRewriter.RewriteResponseHeaders(H(("Content-Type", "text/plain")), "https://evil.example",
            new GatewayRoute("/", new Uri("https://api.corp")), Browser(), out _);
        Assert.DoesNotContain(headers, h => h.Key.StartsWith("Access-Control-", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("http://localhost:3000")]
    [InlineData("http://127.0.0.1:5173")]
    [InlineData("http://app.gw.localhost:8819")]
    [InlineData("http://[::1]:8080")]
    public void The_echo_default_answers_pages_on_this_machine(string origin) =>
        Assert.True(BrowserRewriter.IsAllowed(origin, Browser()));

    [Theory]
    [InlineData("https://evil.example")]
    [InlineData("null")]
    [InlineData("http://127.0.0.1.evil.example")]
    [InlineData("http://localhost.evil.example")]
    public void The_echo_default_refuses_everything_else(string origin) =>
        Assert.False(BrowserRewriter.IsAllowed(origin, Browser()));

    [Theory]
    [InlineData(true, null)]
    [InlineData(true, "http://localhost:3000")]
    [InlineData(false, null)]
    public void An_unaccepted_origin_is_refused_for_a_simple_request_and_an_upgrade(bool cors, string? list)
    {
        var options = Browser(cors, list is null ? null : new[] { list });
        Assert.NotNull(BrowserRewriter.RefuseCrossSite("POST", H(("Origin", "https://evil.example"), ("Content-Type", "application/x-www-form-urlencoded")), options));
        Assert.NotNull(BrowserRewriter.RefuseCrossSite("GET", H(("Origin", "https://evil.example"), ("Upgrade", "websocket")), options));
        Assert.Null(BrowserRewriter.RefuseCrossSite("POST", H(("Origin", "http://127.0.0.1:8819")), options));   // the gateway's own page
        Assert.Null(BrowserRewriter.RefuseCrossSite("POST", H(("Origin", "http://localhost:8819")), options));   // the same, by another name
    }

    [Fact]
    public void A_cross_site_request_without_an_origin_is_refused_unless_it_is_a_page_load()
    {
        var options = Browser(cors: false);
        Assert.NotNull(BrowserRewriter.RefuseCrossSite("GET", H(("Sec-Fetch-Site", "cross-site"), ("Sec-Fetch-Mode", "no-cors")), options));
        Assert.Null(BrowserRewriter.RefuseCrossSite("GET", H(("Sec-Fetch-Site", "cross-site"), ("Sec-Fetch-Mode", "navigate")), options));
        Assert.NotNull(BrowserRewriter.RefuseCrossSite("POST", H(("Sec-Fetch-Site", "cross-site"), ("Sec-Fetch-Mode", "navigate")), options));
        Assert.Null(BrowserRewriter.RefuseCrossSite("POST", H(("Content-Type", "application/json")), options));   // a program
    }

    // ------------------------------------------------------------ MED-03: Host

    [Theory]
    [InlineData("rebind.attacker.test:8819", "rebind.attacker.test")]
    [InlineData("192.168.1.5:8819", "192.168.1.5")]
    [InlineData("127.0.0.1.attacker.test", "127.0.0.1.attacker.test")]
    public void A_foreign_host_header_is_refused(string header, string expected) =>
        Assert.Equal(expected, ServeCommand.HostRefused(header));

    [Theory]
    [InlineData("127.0.0.1:8819")]
    [InlineData("localhost:8819")]
    [InlineData("api.gw.localhost:8819")]
    [InlineData("[::1]:8819")]
    [InlineData(null)]
    public void A_local_host_header_is_answered(string? header) =>
        Assert.Null(ServeCommand.HostRefused(header));

    // ------------------------------------------------------------ MED-05: recordings

    [Fact]
    public void A_recording_redacts_body_credentials_and_redirect_tokens()
    {
        var recorder = new GatewayRecorder();
        recorder.Record("POST", "https://login.corp/session?token=QUERYSECRET",
            H(("Authorization", "Bearer HEADERSECRET"), ("Cookie", "sid=COOKIESECRET")),
            Encoding.UTF8.GetBytes("user=alice&password=BODYPASSWORD"), "application/x-www-form-urlencoded",
            302, "Found",
            H(("Location", "https://app.corp/cb?code=AUTHCODE&token=LOCATIONTOKEN"), ("Set-Cookie", "sid=SETCOOKIESECRET"), ("Content-Type", "application/json")),
            Encoding.UTF8.GetBytes("""{"access_token":"RESPONSETOKEN","refresh_token":"REFRESHTOKEN","user":"alice"}"""),
            12);
        var path = Path.Combine(Path.GetTempPath(), $"certapi-rec-{Guid.NewGuid():N}.har");
        try
        {
            recorder.Save(path);
            string har = File.ReadAllText(path);
            foreach (var secret in new[] { "QUERYSECRET", "HEADERSECRET", "COOKIESECRET", "SETCOOKIESECRET", "BODYPASSWORD", "LOCATIONTOKEN", "AUTHCODE", "RESPONSETOKEN", "REFRESHTOKEN" })
                Assert.DoesNotContain(secret, har);
            Assert.Contains("alice", har);   // the rest of each body is kept
        }
        finally { File.Delete(path); }
    }

    // ------------------------------------------------------------ LOW-10: header rule values

    [Fact]
    public void A_header_rule_value_with_a_line_break_is_refused()
    {
        var rules = HeaderRules.TryCreate(H(("X-Team", "people\r\nX-Injected: 1")), Array.Empty<string>(),
            Array.Empty<KeyValuePair<string, string>>(), Array.Empty<string>(), out var problem);
        Assert.Null(rules);
        Assert.Contains("line break", problem);
    }

    [Fact]
    public void A_profile_header_with_a_line_break_is_refused() =>
        Assert.Throws<ConfigException>(() => ConfigLoader.Parse("""{"defaultProfile":"p","profiles":{"p":{"headers":{"X-A":"v\r\nX-B: w"}}}}""").Resolve(null));

    // ------------------------------------------------------------ MED-06 / MED-08: launch

    private static (int Exit, string Err) Serve(params string[] args)
    {
        var err = new StringWriter();
        int exit = CliApp.Run(new[] { "--no-config", "serve" }.Concat(args).ToArray(), new StringWriter(), err);
        return (exit, err.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void An_empty_token_is_a_usage_error(string token)
    {
        var (exit, err) = Serve("http://127.0.0.1:9", "--port", "8899", "--listen", "0.0.0.0", "--token", token, "--check");
        Assert.Equal(2, exit);
        Assert.Contains("--token needs a value", err);
    }

    [Fact]
    public void Windows_sign_in_with_an_open_relay_is_a_usage_error()
    {
        var (exit, err) = Serve("--port", "8899", "--allow", "*", "--windows-auth", "--check");
        Assert.Equal(2, exit);
        Assert.Contains("--windows-auth cannot be combined with --allow *", err);
    }

    [Fact]
    public void A_header_flag_with_a_line_break_is_a_usage_error() =>
        Assert.Equal(2, Serve("http://127.0.0.1:9", "--port", "8899", "--request-header", "X-Team: people\r\nX-Injected: 1", "--check").Exit);

    // ------------------------------------------------------------ LOW-12: mock

    [Theory]
    [InlineData("../private.txt")]
    [InlineData("/etc/hostname")]
    [InlineData("sub/../../x.txt")]
    public void A_scenario_body_file_outside_its_folder_is_refused(string bodyFile)
    {
        var dir = Directory.CreateTempSubdirectory("certapi-mock-").FullName;
        try
        {
            bool read = false;
            var scenario = MockScenario.Parse(
                "{\"routes\":[{\"match\":{\"method\":\"GET\",\"path\":\"/x\"},\"respond\":{\"status\":200,\"bodyFile\":\"" + bodyFile + "\"}}]}",
                dir, readBytes: _ => { read = true; return Encoding.UTF8.GetBytes("SECRET"); });
            Assert.False(read);
            Assert.Contains(scenario.Warnings, w => w.Contains("outside the scenario's folder"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void A_scenario_body_file_inside_its_folder_is_served()
    {
        var dir = Directory.CreateTempSubdirectory("certapi-mock-").FullName;
        try
        {
            bool read = false;
            MockScenario.Parse("""{"routes":[{"match":{"method":"GET","path":"/x"},"respond":{"status":200,"bodyFile":"bodies/logo.txt"}}]}""",
                dir, readBytes: _ => { read = true; return Encoding.UTF8.GetBytes("ok"); });
            Assert.True(read);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void The_netsh_path_is_absolute_on_windows()
    {
        if (OperatingSystem.IsWindows())
            Assert.True(Path.IsPathRooted(LoopbackTlsBinding.NetshPath));
        else
            Assert.Equal("netsh", LoopbackTlsBinding.NetshPath);
    }
}
