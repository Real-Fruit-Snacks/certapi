using System.IO;
using System.Security.Cryptography.X509Certificates;
using ApiTester.Core;

namespace ApiTester.Tests;

public class CertificateFileLoaderTests
{
    private static X509Certificate2 MakeClientCert()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        return SelfSignedCertificateFactory.CreateSignedCertificate("FileClient", ca, false, true);
    }

    private static string Temp(string ext) => Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ext);

    [Fact]
    public void Loads_a_pfx_with_a_password()
    {
        using var cert = MakeClientCert();
        var pfx = Temp(".pfx");
        File.WriteAllBytes(pfx, cert.Export(X509ContentType.Pkcs12, "s3cret"));
        try
        {
            using var loaded = CertificateFileLoader.Load(pfx, "s3cret");
            Assert.True(loaded.HasPrivateKey);
            Assert.Equal(cert.Thumbprint, loaded.Thumbprint);
        }
        finally { File.Delete(pfx); }
    }

    [Fact]
    public void Loads_a_passwordless_pfx()
    {
        using var cert = MakeClientCert();
        var pfx = Temp(".pfx");
        File.WriteAllBytes(pfx, cert.Export(X509ContentType.Pkcs12));
        try
        {
            using var loaded = CertificateFileLoader.Load(pfx);
            Assert.True(loaded.HasPrivateKey);
            Assert.Equal(cert.Thumbprint, loaded.Thumbprint);
        }
        finally { File.Delete(pfx); }
    }

    [Fact]
    public void Loads_a_pem_with_cert_and_key_in_one_file()
    {
        using var cert = MakeClientCert();
        var pem = Temp(".pem");
        using (var rsa = cert.GetRSAPrivateKey()!)
            File.WriteAllText(pem, cert.ExportCertificatePem() + "\n" + rsa.ExportPkcs8PrivateKeyPem());
        try
        {
            using var loaded = CertificateFileLoader.Load(pem);
            Assert.True(loaded.HasPrivateKey);
            Assert.Equal(cert.Thumbprint, loaded.Thumbprint);
        }
        finally { File.Delete(pem); }
    }

    [Fact]
    public void Loads_a_pem_cert_with_a_separate_key_file()
    {
        using var cert = MakeClientCert();
        var crt = Temp(".crt");
        var key = Temp(".key");
        File.WriteAllText(crt, cert.ExportCertificatePem());
        using (var rsa = cert.GetRSAPrivateKey()!)
            File.WriteAllText(key, rsa.ExportPkcs8PrivateKeyPem());
        try
        {
            using var loaded = CertificateFileLoader.Load(crt, password: null, keyPath: key);
            Assert.True(loaded.HasPrivateKey);
            Assert.Equal(cert.Thumbprint, loaded.Thumbprint);
        }
        finally { File.Delete(crt); File.Delete(key); }
    }

    [Fact]
    public void Missing_file_throws_a_clear_error()
    {
        var ex = Assert.Throws<CertificateFileException>(() => CertificateFileLoader.Load("C:\\no\\such.pfx"));
        Assert.Contains("not found", ex.Message);
    }

    [Fact]
    public void Wrong_pfx_password_throws_a_clear_error()
    {
        using var cert = MakeClientCert();
        var pfx = Temp(".pfx");
        File.WriteAllBytes(pfx, cert.Export(X509ContentType.Pkcs12, "right"));
        try
        {
            Assert.Throws<CertificateFileException>(() => CertificateFileLoader.Load(pfx, "wrong"));
        }
        finally { File.Delete(pfx); }
    }

    [Fact]
    public void Pem_without_a_private_key_throws_a_clear_error()
    {
        using var cert = MakeClientCert();
        var crt = Temp(".crt");
        File.WriteAllText(crt, cert.ExportCertificatePem());   // cert only, no key
        try
        {
            var ex = Assert.Throws<CertificateFileException>(() => CertificateFileLoader.Load(crt));
            Assert.Contains("private key", ex.Message);
        }
        finally { File.Delete(crt); }
    }
}

public class PemIntermediateTests
{
    [Fact]
    public void Intermediates_in_a_pem_bundle_are_found_whichever_order_they_come_in()
    {
        using var root = SelfSignedCertificateFactory.CreateCertificateAuthority("Root");
        using var intermediate = SelfSignedCertificateFactory.CreateSignedCertificate("Intermediate", root, false, false);
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var req = new System.Security.Cryptography.X509Certificates.CertificateRequest(
            "CN=leaf", rsa, System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1);
        using var leaf = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));

        string pem = System.IO.Path.GetTempFileName() + ".pem";
        try
        {
            // Root first, then the intermediate, leaf last with its key: the awkward order.
            System.IO.File.WriteAllText(pem, root.ExportCertificatePem() + "\n" + intermediate.ExportCertificatePem() + "\n" +
                                             leaf.ExportCertificatePem() + "\n" + rsa.ExportPkcs8PrivateKeyPem());
            var extras = CertificateFileLoader.LoadIntermediates(pem, leaf.Thumbprint!);
            Assert.Equal(2, extras.Count);
            Assert.DoesNotContain(extras, c => c.Subject == "CN=leaf");

            // A leaf-only PEM has nothing to report; a .pfx is not a PEM at all.
            System.IO.File.WriteAllText(pem, leaf.ExportCertificatePem() + "\n" + rsa.ExportPkcs8PrivateKeyPem());
            Assert.Empty(CertificateFileLoader.LoadIntermediates(pem, leaf.Thumbprint!));

            var note = new System.IO.StringWriter();
            System.IO.File.WriteAllText(pem, leaf.ExportCertificatePem() + "\n" + intermediate.ExportCertificatePem() + "\n" + rsa.ExportPkcs8PrivateKeyPem());
            ApiTester.Cli.CliCert.NoteMissingIntermediates(pem, leaf.Thumbprint!, note);
            // The test CA is not in any store on any machine, so the note always fires here.
            Assert.Contains("CN=Intermediate", note.ToString());
            Assert.Contains("certutil", note.ToString());
        }
        finally { System.IO.File.Delete(pem); }
    }
}
