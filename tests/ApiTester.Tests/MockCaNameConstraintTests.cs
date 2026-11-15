using System.IO;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using ApiTester.Core;

namespace ApiTester.Tests;

/// <summary>The mock's certificate authority is kept and meant to be trusted, so it may only
/// certify localhost and the loopback addresses: a certificate it signs for any other name fails
/// chain validation on a machine that trusts it.</summary>
public class MockCaNameConstraintTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("certapi-mockca-").FullName;

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    private static X509ChainStatusFlags ChainStatus(X509Certificate2 leaf, X509Certificate2 root, string? usage = null)
    {
        using var chain = new X509Chain();
        if (usage is not null) chain.ChainPolicy.ApplicationPolicy.Add(new System.Security.Cryptography.Oid(usage));
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(root);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.Build(leaf);
        return chain.ChainStatus.Aggregate(X509ChainStatusFlags.NoError, (all, s) => all | s.Status);
    }

    [Fact]
    public void The_authority_carries_a_critical_name_constraint()
    {
        using var ca = MockCertificates.CreateAuthority();
        var ext = ca.Extensions["2.5.29.30"];
        Assert.NotNull(ext);
        Assert.True(ext!.Critical);
        Assert.True(MockCertificates.LimitedToLoopback(ca));
        using var plain = SelfSignedCertificateFactory.CreateCertificateAuthority("certapi mock CA");
        Assert.False(MockCertificates.LimitedToLoopback(plain));
    }

    [Fact]
    public void A_certificate_for_another_site_does_not_chain()
    {
        using var ca = MockCertificates.CreateAuthority();
        using var root = X509CertificateLoader.LoadCertificate(ca.Export(X509ContentType.Cert));
        using var evil = SelfSignedCertificateFactory.CreateSignedCertificate(
            "bank.example", ca, serverAuth: true, clientAuth: false, dnsNames: ["bank.example"]);
        using var evilIp = SelfSignedCertificateFactory.CreateSignedCertificate(
            "localhost", ca, serverAuth: true, clientAuth: false, dnsNames: ["localhost"], ipAddresses: [IPAddress.Parse("10.0.0.1")]);
        Assert.True(ChainStatus(evil, root).HasFlag(X509ChainStatusFlags.HasNotPermittedNameConstraint));
        Assert.True(ChainStatus(evilIp, root).HasFlag(X509ChainStatusFlags.HasNotPermittedNameConstraint));
    }

    /// <summary>A leaf the test makes itself, for what the factory has no switch for.</summary>
    private static X509Certificate2 Leaf(X509Certificate2 ca, string subject, Action<CertificateRequest> shape)
    {
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var req = new CertificateRequest(subject, rsa, System.Security.Cryptography.HashAlgorithmName.SHA256,
            System.Security.Cryptography.RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        shape(req);
        using var signed = req.Create(ca, DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddDays(30), [1, 2, 3, 4]);
        return signed.CopyWithPrivateKey(rsa);
    }

    [Fact]
    public void Code_signing_e_mail_and_other_subjects_do_not_chain()
    {
        using var ca = MockCertificates.CreateAuthority();
        using var root = X509CertificateLoader.LoadCertificate(ca.Export(X509ContentType.Cert));
        using var signing = Leaf(ca, "CN=localhost", r => r.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension(new System.Security.Cryptography.OidCollection { new("1.3.6.1.5.5.7.3.3") }, false)));
        Assert.True(ChainStatus(signing, root, "1.3.6.1.5.5.7.3.3").HasFlag(X509ChainStatusFlags.NotValidForUsage));
        using var mail = Leaf(ca, "CN=localhost", r =>
        {
            var san = new SubjectAlternativeNameBuilder();
            san.AddEmailAddress("ceo@bank.example");
            r.CertificateExtensions.Add(san.Build());
        });
        Assert.True(ChainStatus(mail, root).HasFlag(X509ChainStatusFlags.HasNotPermittedNameConstraint));
        using var someone = SelfSignedCertificateFactory.CreateSignedCertificate(
            "Alice Example", ca, serverAuth: false, clientAuth: true);
        Assert.True(ChainStatus(someone, root).HasFlag(X509ChainStatusFlags.HasNotPermittedNameConstraint));
    }

    [Fact]
    public void The_mocks_own_certificates_still_chain()
    {
        using var server = MockCertificates.Generate(MockTlsMode.Mtls, _dir).ServerCertificate;
        using var root = X509CertificateLoader.LoadCertificateFromFile(Path.Combine(_dir, "mock-ca.cer"));
        Assert.Equal(X509ChainStatusFlags.NoError, ChainStatus(server, root));
        using var client = X509CertificateLoader.LoadPkcs12FromFile(Path.Combine(_dir, "mock-client.pfx"), (string?)null);
        Assert.Equal(X509ChainStatusFlags.NoError, ChainStatus(client, root));
        // The wrong-host defect is still a name mismatch for TLS to report, not a chain failure.
        using var wrong = MockCertificates.Generate(MockTlsMode.Https, _dir, MockTlsDefect.WrongHost).ServerCertificate;
        using var rootNow = X509CertificateLoader.LoadCertificateFromFile(Path.Combine(_dir, "mock-ca.cer"));   // new where keys are not kept
        Assert.Equal(X509ChainStatusFlags.NoError, ChainStatus(wrong, rootNow));
    }

    [Fact]
    public void A_kept_authority_without_the_constraint_is_replaced()
    {
        if (!SecretProtection.Default.IsAvailable) return;   // nothing is kept where keys cannot be encrypted
        using (var old = SelfSignedCertificateFactory.CreateCertificateAuthority("certapi mock CA"))
        {
            string sealedPfx = SecretProtection.Default.Protect(Convert.ToBase64String(old.Export(X509ContentType.Pfx)))!;
            File.WriteAllText(Path.Combine(_dir, MockCertificates.CaKeyFile), sealedPfx);
            File.WriteAllBytes(Path.Combine(_dir, "mock-ca.cer"), old.Export(X509ContentType.Cert));
        }
        var g = MockCertificates.Generate(MockTlsMode.Https, _dir);
        g.ServerCertificate.Dispose();
        Assert.False(g.Reused);
        using var now = X509CertificateLoader.LoadCertificateFromFile(Path.Combine(_dir, "mock-ca.cer"));
        Assert.True(MockCertificates.LimitedToLoopback(now));
    }

    [Fact]
    public async Task Two_starts_at_once_in_one_folder_agree_on_the_authority()
    {
        for (int round = 0; round < 3; round++)
        {
            foreach (var f in Directory.GetFiles(_dir)) File.Delete(f);
            using var go = new ManualResetEventSlim();
            var starts = Enumerable.Range(0, 2).Select(_ => Task.Run(() =>
            {
                go.Wait();
                return MockCertificates.Generate(MockTlsMode.Https, _dir).ServerCertificate;
            })).ToArray();
            go.Set();
            var servers = await Task.WhenAll(starts);
            try
            {
                if (!SecretProtection.Default.IsAvailable) return;   // without kept keys every start is its own
                using var ca = X509CertificateLoader.LoadCertificateFromFile(Path.Combine(_dir, "mock-ca.cer"));
                Assert.All(servers, s => Assert.True(MockCertificates.IssuedBy(s, ca), $"round {round}: a mock serves a certificate mock-ca.cer did not sign"));
            }
            finally { foreach (var s in servers) s.Dispose(); }
        }
    }
}
