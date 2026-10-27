using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using ApiTester.Core;

namespace ApiTester.Tests.Sweep;

public class GatewayCertificateRenewalTests
{
    [Fact]
    public void Renewing_a_certificate_near_expiry_reports_the_one_it_replaced_and_remembers_it()
    {
        string dir = Path.Combine(Path.GetTempPath(), "gwcert-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using var key = RSA.Create(2048);
            var req = new CertificateRequest("CN=127.0.0.1", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var san = new SubjectAlternativeNameBuilder();
            san.AddDnsName("localhost");
            san.AddDnsName("old.gw.localhost");
            req.CertificateExtensions.Add(san.Build());
            using var expiring = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-360), DateTimeOffset.UtcNow.AddDays(3));
            File.WriteAllBytes(Path.Combine(dir, "gateway.pfx"), expiring.Export(X509ContentType.Pfx));

            using var renewed = GatewayCertificate.LoadOrCreate(dir, null, out var replaced);
            Assert.Equal(expiring.Thumbprint, replaced);
            Assert.NotEqual(expiring.Thumbprint, renewed.Thumbprint);
            Assert.True(renewed.NotAfter > DateTime.Now.AddDays(300));
            Assert.Contains(expiring.Thumbprint, GatewayCertificate.PreviousThumbprints(dir));
            // The names the old one carried are kept.
            var names = renewed.Extensions.OfType<X509SubjectAlternativeNameExtension>().Single().EnumerateDnsNames();
            Assert.Contains("old.gw.localhost", names);

            using var again = GatewayCertificate.LoadOrCreate(dir, null, out var none);
            Assert.Null(none);
            Assert.Equal(renewed.Thumbprint, again.Thumbprint);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
