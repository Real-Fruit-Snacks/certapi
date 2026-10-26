using System.IO;
using System.Net;
using System.Net.Sockets;
using ApiTester.Cli;
using ApiTester.Core;

namespace ApiTester.Tests.Cli;

/// <summary>`serve --check`: everything resolved and validated, the table printed, nothing bound.</summary>
public class ServeCheckTests
{
    private static CliServices Services(System.Security.Cryptography.X509Certificates.X509Certificate2 cert) => new()
    {
        IsGuiRunning = () => false,
        LiveStatePath = Path.Combine(Path.GetTempPath(), $"certapi-check-{Guid.NewGuid():N}.json"),
        ListCertificates = _ => new[]
        {
            new CertificateInfo { Subject = "CN=matt", Issuer = "CN=CA", Thumbprint = cert.Thumbprint!,
                NotBefore = DateTime.Now.AddDays(-1), NotAfter = DateTime.Now.AddDays(30), HasClientAuthEku = true, Certificate = cert }
        }
    };

    [Fact]
    public void A_usable_configuration_prints_the_table_and_binds_nothing()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var cert = SelfSignedCertificateFactory.CreateSignedCertificate("matt", ca, false, true);
        // Hold the port: --check must not need it, and must not fail because something else has it.
        var holder = new TcpListener(IPAddress.Loopback, 0);
        holder.Start();
        int port = ((IPEndPoint)holder.LocalEndpoint).Port;
        try
        {
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            int code = CliApp.Run(new[]
            {
                "serve", "--check", "--port", port.ToString(), "--cert", "matt", "--no-proxy",
                "--upstream", "api=https://api.corp", "--upstream", "/auth=https://login.corp", "--allow", "*.lab", "--vhost", "--token", "t",
            }, stdout, stderr, services: Services(cert));
            string output = stdout.ToString();
            Assert.True(code == ExitCodes.Ok, stderr.ToString());
            Assert.Contains("serve configuration is valid: 2 sites and dynamic hosts (*.lab)", output);
            Assert.Contains($"http://api.gw.localhost:{port}/", output);
            Assert.Contains($"http://auth.gw.localhost:{port}/", output);
            Assert.Contains("every site: cert matt", output);
            Assert.Contains("Nothing was bound or contacted", output);
        }
        finally { holder.Stop(); }
    }

    [Fact]
    public void A_problem_is_the_same_error_the_real_start_would_give()
    {
        string file = Path.GetTempFileName();
        File.WriteAllText(file, """[{ "name": "hr", "upstream": "https://hr.corp", "certFile": "missing.pem" }]""");
        try
        {
            var err = new StringWriter();
            Assert.Equal(ExitCodes.Data, CliApp.Run(new[] { "serve", "--check", "--port", "8819", "--routes", file }, new StringWriter(), err));
            Assert.Contains("--routes hr", err.ToString());

            Assert.Equal(ExitCodes.Usage, CliApp.Run(new[] { "serve", "--check", "--port", "8819", "--upstream", "a_b=https://x" }, new StringWriter(), new StringWriter()));
            Assert.Equal(ExitCodes.Usage, CliApp.Run(new[] { "serve", "--check", "--upstream", "api=https://x" }, new StringWriter(), new StringWriter()));
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void Check_with_tls_touches_no_binding_and_says_when_it_will_be_checked()
    {
        var stdout = new StringWriter();
        int code = CliApp.Run(new[] { "serve", "--check", "--port", "8819", "--tls", "--no-proxy", "https://portal.corp" }, stdout, new StringWriter(),
            services: new CliServices { IsGuiRunning = () => false, LiveStatePath = Path.Combine(Path.GetTempPath(), $"certapi-check-{Guid.NewGuid():N}.json") });
        Assert.Equal(ExitCodes.Ok, code);
        Assert.Contains("https://127.0.0.1:8819/", stdout.ToString());
        Assert.Contains("--tls: the certificate binding", stdout.ToString());
    }
}

public class ServeUpstreamPathNoteTests
{
    [Fact]
    public void A_site_url_with_a_path_says_the_path_is_not_kept()
    {
        var err = new StringWriter();
        int code = CliApp.Run(new[] { "serve", "--check", "--port", "8819", "--no-proxy", "--upstream", "api=https://api.corp/v1", "--upstream", "web=https://web.corp/" },
            new StringWriter(), err, services: new CliServices { IsGuiRunning = () => false, LiveStatePath = Path.Combine(Path.GetTempPath(), $"certapi-pn-{Guid.NewGuid():N}.json") });
        Assert.Equal(ExitCodes.Ok, code);
        Assert.Contains("request /api/v1/… to reach /v1/…", err.ToString());
        Assert.DoesNotContain("web=", err.ToString());   // a bare "/" is not a path worth a note
    }
}
