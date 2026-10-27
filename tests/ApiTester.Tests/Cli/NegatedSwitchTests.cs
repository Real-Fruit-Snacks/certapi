using System.IO;
using ApiTester.Cli;
using ApiTester.Core;

namespace ApiTester.Tests.Cli;

/// <summary>A switch a profile turns on can be turned off for one run with its --no- form.</summary>
public class NegatedSwitchTests
{
    [Theory]
    [InlineData(new string[0], null, false)]
    [InlineData(new string[0], true, true)]
    [InlineData(new[] { "--insecure" }, null, true)]
    [InlineData(new[] { "--insecure" }, false, true)]
    [InlineData(new[] { "--no-insecure" }, true, false)]
    [InlineData(new[] { "-k" }, null, true)]
    public void Typed_beats_profile_beats_off(string[] typed, bool? profile, bool expected)
    {
        var a = new Args(typed);
        Assert.Equal(expected, a.Switch("--insecure", profile));
        Assert.Empty(a.Positionals());
    }

    [Fact]
    public void Both_forms_at_once_is_a_usage_error()
    {
        var a = new Args(new[] { "--insecure", "--no-insecure" });
        Assert.Contains("contradict", Assert.Throws<CliUsageException>(() => a.Switch("--insecure", null)).Message);
    }

    [Fact]
    public void The_negative_form_is_unknown_where_the_switch_is_not_read()
    {
        var a = new Args(new[] { "--no-insecure" });
        Assert.Throws<CliUsageException>(() => a.Positionals());
    }

    private static string Config(string json)
    {
        var file = Path.Combine(Path.GetTempPath(), $"certapi-neg-{Guid.NewGuid():N}.json");
        File.WriteAllText(file, json);
        return file;
    }

    private static CliServices Services(System.Security.Cryptography.X509Certificates.X509Certificate2 cert) => new()
    {
        IsGuiRunning = () => false,
        UserConfigPath = null,
        LiveStatePath = Path.Combine(Path.GetTempPath(), $"certapi-neg-{Guid.NewGuid():N}.json"),
        ListCertificates = _ => new[]
        {
            new CertificateInfo { Subject = "CN=matt", Issuer = "CN=CA", Thumbprint = cert.Thumbprint!,
                NotBefore = DateTime.Now.AddDays(-1), NotAfter = DateTime.Now.AddDays(30), HasClientAuthEku = true, Certificate = cert }
        }
    };

    [Fact]
    public void A_profiles_serve_switches_can_each_be_turned_off()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var cert = SelfSignedCertificateFactory.CreateSignedCertificate("matt", ca, false, true);
        var file = Config("""
            { "profiles": { "corp": {
                "cert": "matt", "insecure": true,
                "serve": { "port": 8819, "browser": true, "autoToken": true, "forwarded": true, "windowsAuth": true,
                           "sites": [ { "name": "api", "upstream": "https://api.corp" } ] } } } }
            """);
        try
        {
            string Check(params string[] extra)
            {
                var so = new StringWriter();
                var se = new StringWriter();
                int code = CliApp.Run(new[] { "serve", "--check", "--config", file, "--profile", "corp", "--no-proxy" }.Concat(extra).ToArray(),
                                      so, se, services: Services(cert));
                Assert.True(code == ExitCodes.Ok, se.ToString());
                return so.ToString().Split('\n').Single(l => l.Contains("every site:"));
            }

            string on = Check();
            Assert.Contains("insecure", on);
            Assert.Contains("browser", on);
            Assert.Contains("auto-token", on);
            Assert.Contains("forwarded", on);
            Assert.Contains("windows-auth", on);

            string off = Check("--no-insecure", "--no-browser", "--no-auto-token", "--no-forwarded", "--no-windows-auth");
            Assert.DoesNotContain("insecure", off);
            Assert.DoesNotContain("browser", off);
            Assert.DoesNotContain("auto-token", off);
            Assert.DoesNotContain("forwarded", off);
            Assert.DoesNotContain("windows-auth", off);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void A_typed_switch_and_its_negation_on_serve_is_a_usage_error()
    {
        var se = new StringWriter();
        Assert.Equal(ExitCodes.Usage, CliApp.Run(new[] { "serve", "--check", "--port", "8819", "--cors", "--no-cors", "https://x.corp" },
            new StringWriter(), se, services: new CliServices { IsGuiRunning = () => false, UserConfigPath = null }));
        Assert.Contains("contradict", se.ToString());
    }
}
