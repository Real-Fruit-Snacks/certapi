using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography.X509Certificates;
using ApiTester.Cli;
using ApiTester.Cli.Commands;
using ApiTester.Core;
using static ApiTester.Tests.Cli.ServeFixture;

namespace ApiTester.Tests.Cli;

/// <summary>Sites: a name instead of a path, and settings of their own.</summary>
public class ServeSiteNameTests
{
    [Theory]
    [InlineData("api=https://api.corp", "/api", "api")]
    [InlineData("API=https://api.corp/ignored", "/api", "api")]
    [InlineData("hr-eu=https://hr.corp:8443", "/hr-eu", "hr-eu")]
    [InlineData("/api=https://api.corp", "/api", null)]
    [InlineData("/=https://portal.corp", "/", null)]
    public void An_upstream_is_named_or_mounted_at_a_path(string spec, string prefix, string? name)
    {
        var route = ServeCommand.ParseUpstream(spec);
        Assert.Equal(prefix, route.PathPrefix);
        Assert.Equal(name, route.Name);
    }

    [Theory]
    [InlineData("a_b=https://x.corp")]
    [InlineData("-api=https://x.corp")]
    [InlineData("a.b=https://x.corp")]
    public void A_name_that_is_not_a_host_label_says_how_to_write_a_path_instead(string spec)
    {
        var ex = Assert.Throws<CliUsageException>(() => ServeCommand.ParseUpstream(spec));
        Assert.Contains("letters, digits and inner hyphens", ex.Message);
        Assert.Contains("=…", ex.Message);
    }

    [Fact]
    public void Display_names_and_duplicate_names()
    {
        Assert.Equal("api", new GatewayRoute("/api", new Uri("https://x")) { Name = "api" }.DisplayName);
        Assert.Equal("auth/sso", new GatewayRoute("/auth/sso", new Uri("https://x")).DisplayName);
        Assert.Equal("(root)", new GatewayRoute("/", new Uri("https://x")).DisplayName);
        Assert.Contains("named 'hr'", Assert.Throws<ArgumentException>(() => new GatewayRoutes(new[]
        {
            new GatewayRoute("/hr", new Uri("https://a")) { Name = "hr" },
            new GatewayRoute("/people", new Uri("https://b")) { Name = "hr" },
        })).Message);
    }

    [Fact]
    public void A_named_site_answers_on_its_name_under_vhost_even_at_another_path()
    {
        var routes = new GatewayRoutes(new[] { new GatewayRoute("/people/hr", new Uri("https://hr.corp")) { Name = "hr" } });
        var v = new VirtualHosts(routes, "gw.localhost", "http", 8819);
        Assert.Equal("/people/hr", v.MountFor("hr.gw.localhost:8819"));
        Assert.Equal(new Uri("http://hr.gw.localhost:8819"), v.OriginFor(routes.All[0]));
    }
}

public class SiteSettingsParseTests
{
    [Fact]
    public void Every_site_setting_is_read_and_paths_resolve_against_the_file()
    {
        string dir = Path.Combine(Path.GetTempPath(), "sites");
        var specs = GatewayRoutesFile.Parse("""
            { "sites": [
              { "name": "hr", "upstream": "https://hr.corp:8443", "insecure": false,
                "caFile": ["hr-root.pem", "/abs/other.pem"], "revocation": "online",
                "windowsUser": "CORP\\svc", "windowsPassword": "${env:HR_PW}",
                "headers": { "X-Team": "people", "X-Key": "${env:HR_KEY}" }, "certFile": "hr.pem" },
              { "name": "lab", "upstream": "https://lab.corp", "windowsAuth": false }
            ] }
            """, "sites.json", dir, name => name switch { "HR_PW" => "pw1", "HR_KEY" => "k1", _ => null });

        var hr = specs[0];
        Assert.Equal("/hr", hr.Prefix);
        Assert.Equal("hr", hr.Name);
        Assert.False(hr.Insecure);
        Assert.Equal(new[] { Path.GetFullPath(Path.Combine(dir, "hr-root.pem")), "/abs/other.pem" }, hr.CaFiles);   // already rooted: kept as written
        Assert.Equal(Path.GetFullPath(Path.Combine(dir, "hr.pem")), hr.CertFile);
        Assert.Equal("online", hr.Revocation);
        Assert.Equal("pw1", hr.WindowsPassword);
        Assert.Equal(new[] { "people", "k1" }, hr.Headers.Select(h => h.Value));
        Assert.True(hr.HasSettings);
        Assert.False(specs[1].WindowsAuth);
    }

    [Theory]
    [InlineData("""[{ "name": "a", "upstream": "https://x", "revocation": "sometimes" }]""", "none, offline or online")]
    [InlineData("""[{ "name": "a", "upstream": "https://x", "windowsPassword": "p" }]""", "needs \"windowsUser\"")]
    [InlineData("""[{ "name": "a", "upstream": "https://x", "windowsAuth": false, "windowsUser": "u" }]""", "contradict")]
    [InlineData("""[{ "name": "a b", "upstream": "https://x" }]""", "letters, digits")]
    [InlineData("""[{ "name": "a", "upstream": "https://x", "insecure": "yes" }]""", "true or false")]
    [InlineData("""[{ "name": "a", "upstream": "https://x", "headers": ["X: y"] }]""", "object of name: value")]
    [InlineData("""[{ "name": "a", "upstream": "https://x", "headers": { "X": "${env:NOPE}" } }]""", "'NOPE'")]
    public void A_bad_site_setting_names_the_site_and_the_problem(string json, string expected)
    {
        var ex = Assert.Throws<InvalidDataException>(() => GatewayRoutesFile.Parse(json, environment: _ => null));
        Assert.Contains(expected, ex.Message);
    }

    [Fact]
    public void Settings_describe_themselves_without_secrets()
    {
        var s = new SiteSettings
        {
            Insecure = true, CaFiles = new[] { "/x/lab-root.pem" }, Revocation = RevocationMode.Online,
            WindowsAuth = WindowsAuthOptions.FromCredentials("CORP\\svc", "hunter2"),
            RequestHeaders = new[] { new KeyValuePair<string, string>("X-Key", "secret-value") },
        };
        var text = string.Join(" · ", s.Describe());
        Assert.Equal("insecure · ca lab-root.pem · revocation online · windows-auth CORP\\svc · headers X-Key", text);
        Assert.DoesNotContain("hunter2", text);
        Assert.DoesNotContain("secret-value", text);
        Assert.NotEqual(new SiteSettings { Insecure = true }.PoolKey, new SiteSettings { Insecure = false }.PoolKey);
    }

    [Fact]
    public void The_gateway_gives_each_site_its_own_settings_and_dynamic_targets_the_defaults()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var own = SelfSignedCertificateFactory.CreateSignedCertificate("Own", ca, false, true);
        using var dflt = SelfSignedCertificateFactory.CreateSignedCertificate("Default", ca, false, true);
        var strict = new GatewayRoute("/strict", new Uri("https://one.corp")) { Name = "strict", Certificate = own,
            Settings = new SiteSettings { Insecure = false, Revocation = RevocationMode.Online, WindowsAuthOff = true } };
        var sso = new GatewayRoute("/sso", new Uri("https://two.corp")) { Settings = new SiteSettings { WindowsAuth = new WindowsAuthOptions(true) } };
        using var gateway = new MtlsGateway(new GatewayRoutes(new[] { strict, sso }), dflt, true, TimeSpan.FromSeconds(5), new TransportOptions());
        gateway.UseWindowsAuth(WindowsAuthOptions.FromCredentials("CORP\\gw", "pw"));

        var a = gateway.Effective(gateway.Routes[0]);
        Assert.Same(own, a.Certificate);
        Assert.False(a.Insecure);
        Assert.Equal(RevocationMode.Online, a.Transport!.Revocation);
        Assert.Null(a.WindowsAuth);

        var b = gateway.Effective(gateway.Routes[1]);
        Assert.Same(dflt, b.Certificate);
        Assert.True(b.Insecure);
        Assert.True(b.WindowsAuth!.UseDefaultCredentials);

        var d = gateway.Effective(null);
        Assert.Same(dflt, d.Certificate);
        Assert.Equal("gw", d.WindowsAuth!.Username);
    }
}

public class ServeSiteSettingsTests
{
    [Fact]
    public async Task Trust_and_headers_are_per_site_and_sites_on_one_host_do_not_share_a_pool()
    {
        var (ca, server, client) = Certs();
        using (ca) using (server) using (client)
        {
            await using var upstream = await LoopbackMtlsServer.StartEchoAsync(server, client.Thumbprint!);
            string target = $"https://localhost:{new Uri(upstream.BaseUrl).Port}";
            string dir = Path.Combine(Path.GetTempPath(), "certapi-sites-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "ca.pem"), ca.ExportCertificatePem());
            string sites = Path.Combine(dir, "sites.json");
            File.WriteAllText(sites, $$"""
                [
                  { "name": "loose",   "upstream": "{{target}}", "headers": { "X-Team": "loose-team" } },
                  { "name": "strict",  "upstream": "{{target}}", "insecure": false },
                  { "name": "trusted", "upstream": "{{target}}", "insecure": false, "caFile": "ca.pem" }
                ]
                """);
            try
            {
                // ServeHost always passes --insecure: "loose" inherits it, the other two refuse it.
                await using var serve = ServeHost.Start(client, "--routes", sites, "--request-header", "X-Team: gateway");
                using var http = NewClient();

                var loose = await Poll(() => http.GetAsync($"{serve.Origin}/loose/x"));
                Assert.Equal(HttpStatusCode.OK, loose.StatusCode);
                Assert.Contains("X-Team: loose-team", await loose.Content.ReadAsStringAsync());

                var strict = await http.GetAsync($"{serve.Origin}/strict/x");
                Assert.Equal(HttpStatusCode.BadGateway, strict.StatusCode);

                var trusted = await http.GetAsync($"{serve.Origin}/trusted/x");
                Assert.Equal(HttpStatusCode.OK, trusted.StatusCode);
                Assert.Contains("X-Team: gateway", await trusted.Content.ReadAsStringAsync());

                string listed = await http.GetStringAsync($"{serve.Origin}/_certapi/routes");
                Assert.Contains("\"name\":\"trusted\"", listed.Replace(" ", ""));
                Assert.Contains("ca ca.pem", listed);
            }
            finally { Directory.Delete(dir, true); }
        }
    }

    [Fact]
    public void A_missing_site_ca_file_is_a_data_error_naming_the_site()
    {
        string file = Path.GetTempFileName();
        File.WriteAllText(file, """[{ "name": "lab", "upstream": "https://lab.corp", "caFile": "nope.pem" }]""");
        try
        {
            var err = new StringWriter();
            Assert.Equal(ExitCodes.Data, CliApp.Run(new[] { "serve", "--port", "1", "--routes", file }, TextWriter.Null, err));
            Assert.Contains("--routes lab", err.ToString());
        }
        finally { File.Delete(file); }
    }
}
