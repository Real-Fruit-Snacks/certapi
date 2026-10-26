using ApiTester.Cli.Commands;
using ApiTester.Core;

namespace ApiTester.Tests.Cli;

/// <summary>The startup table: one row per site, what to open, where it goes, which certificate,
/// what is different — aligned, and with no secret in it.</summary>
public class ServeSummaryTests
{
    private static (GatewayRoutes routes, MtlsGateway gateway, System.Security.Cryptography.X509Certificates.X509Certificate2 own) Build()
    {
        var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        var dflt = SelfSignedCertificateFactory.CreateSignedCertificate("matt", ca, false, true);
        var own = SelfSignedCertificateFactory.CreateSignedCertificate("svc-hr", ca, false, true);
        var routes = new GatewayRoutes(new[]
        {
            new GatewayRoute("/api", new Uri("https://api.corp")) { Name = "api" },
            new GatewayRoute("/hr", new Uri("https://hr.corp:8443")) { Name = "hr", Certificate = own,
                Settings = new SiteSettings { WindowsAuth = WindowsAuthOptions.FromCredentials("CORP\\svc", "hunter2"),
                    RequestHeaders = new[] { new KeyValuePair<string, string>("X-Key", "s3cret") } } },
            new GatewayRoute("/", new Uri("https://portal.corp")),
        });
        return (routes, new MtlsGateway(routes, dflt, false, TimeSpan.FromSeconds(5)), own);
    }

    [Fact]
    public void Path_mode_rows_name_the_url_to_open_the_certificate_and_the_differences()
    {
        var (routes, gateway, _) = Build();
        using (gateway)
        {
            var rows = ServeSummary.Rows(routes, gateway, null, new UpstreamAllowlist(new[] { "*.lab" }), "http", 8819);
            Assert.Equal(new ServeSummary.Row("api", "http://127.0.0.1:8819/api/", "https://api.corp", "matt", ""), rows[0]);
            Assert.Equal("hr", rows[1].Site);
            Assert.Equal("svc-hr", rows[1].Cert);
            Assert.Equal("own cert, windows-auth CORP\\svc, headers X-Key", rows[1].Settings);
            Assert.Equal(new ServeSummary.Row("(root)", "http://127.0.0.1:8819/", "https://portal.corp", "matt", ""), rows[2]);
            Assert.Equal(new ServeSummary.Row("*", "http://127.0.0.1:8819/_/https://<host>/", "any of *.lab", "matt", ""), rows[3]);

            string table = ServeSummary.Render(rows);
            Assert.DoesNotContain("hunter2", table);
            Assert.DoesNotContain("s3cret", table);
            var lines = table.Split('\n');
            Assert.StartsWith("  SITE", lines[0]);
            // Columns line up: every row's upstream starts where the header's does.
            int col = lines[0].IndexOf("UPSTREAM", StringComparison.Ordinal);
            foreach (var line in lines.Skip(1)) Assert.Equal(' ', line[col - 1]);
            Assert.Contains("https://hr.corp:8443", lines[2][col..]);
            Assert.Contains("  -", lines[1]);   // no settings is a dash, not a blank
        }
    }

    [Fact]
    public void Vhost_rows_give_each_site_its_own_origin()
    {
        var (routes, gateway, _) = Build();
        using (gateway)
        {
            var v = new VirtualHosts(routes, "gw.localhost", "http", 8819);
            var rows = ServeSummary.Rows(routes, gateway, v, new UpstreamAllowlist(new[] { "*" }), "http", 8819);
            Assert.Equal("http://api.gw.localhost:8819/", rows[0].Open);
            Assert.Equal("http://gw.localhost:8819/", rows[2].Open);
            Assert.Equal("http://<host>.gw.localhost:8819/", rows[3].Open);
            Assert.Equal("open relay", rows[3].Settings);
        }
    }

    [Fact]
    public void The_footer_says_what_every_site_gets()
    {
        Assert.Equal("  every site: cert none · no token · loopback only", ServeSummary.Gateway(new ServeSummary.GatewayFacts()));
        string full = ServeSummary.Gateway(new ServeSummary.GatewayFacts
        {
            Insecure = true, CaFiles = "corp-root.pem", Proxy = "http://u:REDACTED@proxy:8080", Revocation = RevocationMode.Online,
            BrowserBundle = true, CookieJar = "memory", AutoToken = true, WindowsAuth = new WindowsAuthOptions(true),
            HeaderRules = true, Forwarded = true, MaxBody = 1024, Tls = true, Token = true, Listen = "0.0.0.0",
        });
        Assert.Equal("  every site: cert none · insecure · ca corp-root.pem · revocation online · proxy http://u:REDACTED@proxy:8080 · " +
                     "browser · cookie-jar memory · auto-token · windows-auth · header rules · forwarded · max-body 1024 · tls · " +
                     "token required · listen 0.0.0.0", full);
        Assert.Contains("cors, strip-csp", ServeSummary.Gateway(new ServeSummary.GatewayFacts { Browser = new[] { "cors", "strip-csp" } }));
    }
}
