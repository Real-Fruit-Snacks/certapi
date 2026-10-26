using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using ApiTester.Cli;
using ApiTester.Core;

namespace ApiTester.Tests.Cli;

/// <summary>--cert-pin / CERTAPI_CERT_PIN: the PIN reaches the key ahead of the prompt on Windows
/// and is explained away, never silently dropped, everywhere else.</summary>
public class CertPinTests
{
    private static X509Certificate2 SoftwareCert()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=pin", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    }

    [Fact]
    public void A_pin_on_a_software_key_or_another_os_is_a_note_not_a_failure()
    {
        using var cert = SoftwareCert();
        var result = SmartCardPin.Apply(cert, "1234", out var note);
        Assert.NotNull(result);
        Assert.True(result.HasPrivateKey);
        Assert.NotNull(note);          // either "only on Windows" or "not a CNG provider"
        Assert.DoesNotContain("1234", note);
    }

    [Fact]
    public void The_flag_and_the_environment_variable_both_reach_the_key_and_the_flag_wins()
    {
        using var cert = SoftwareCert();
        string pfx = Path.GetTempFileName() + ".pfx";
        File.WriteAllBytes(pfx, cert.Export(X509ContentType.Pkcs12, "pw"));
        try
        {
            var services = new CliServices();
            var stderr = new StringWriter();
            var args = new Args(new[] { "--cert-file", pfx, "--cert-password", "pw", "--cert-pin", "4321" });
            using var fromFlag = CliCert.Resolve(args, "CurrentUser", services, stderr, _ => "9999");
            Assert.NotNull(fromFlag);
            Assert.Empty(args.Positionals());
            Assert.DoesNotContain("4321", stderr.ToString());
            Assert.DoesNotContain("9999", stderr.ToString());
            Assert.Contains("--cert-pin", stderr.ToString());   // the note names the flag, never the PIN

            var quiet = new StringWriter();
            using var noPin = CliCert.Resolve(new Args(new[] { "--cert-file", pfx, "--cert-password", "pw" }), "CurrentUser", services, quiet, _ => null);
            Assert.NotNull(noPin);
            Assert.Empty(quiet.ToString());

            var env = new StringWriter();
            using var fromEnv = CliCert.Resolve(new Args(new[] { "--cert-file", pfx, "--cert-password", "pw" }), "CurrentUser", services, env,
                name => name == SmartCardPin.EnvironmentVariable ? "7777" : null);
            Assert.NotNull(fromEnv);
            Assert.Contains("--cert-pin", env.ToString());
            Assert.DoesNotContain("7777", env.ToString());
        }
        finally { File.Delete(pfx); }
    }
}
