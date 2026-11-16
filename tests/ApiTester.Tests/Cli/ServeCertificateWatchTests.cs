using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using ApiTester.Cli;
using ApiTester.Core;

namespace ApiTester.Tests.Cli;

/// <summary>A renewal is taken without a restart, and an expiry is said ahead of time — while the
/// gateway runs, not only when it starts.</summary>
public class ServeCertificateWatchTests : IDisposable
{
    private readonly X509Certificate2 _ca = LongLivedCa("Corp Issuing CA");

    /// <summary>A CA valid from three years ago, so it can sign the year-old certificates a renewal replaces.</summary>
    private static X509Certificate2 LongLivedCa(string name)
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest($"CN={name}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.DigitalSignature, true));
        return req.CreateSelfSigned(DateTimeOffset.Now.AddYears(-3), DateTimeOffset.Now.AddYears(5));
    }
    private readonly string _dir = Directory.CreateTempSubdirectory("certapi-watch-").FullName;

    public void Dispose()
    {
        _ca.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    /// <summary>A client certificate for CN=svc-orders from the test CA, valid from/to the dates given.</summary>
    private X509Certificate2 Cert(DateTimeOffset from, DateTimeOffset to, string cn = "svc-orders")
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest($"CN={cn}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.2") }, false));
        var serial = new byte[8];
        RandomNumberGenerator.Fill(serial);
        serial[0] &= 0x7F;
        using var signed = req.Create(_ca, from, to, serial);
        return signed.CopyWithPrivateKey(rsa);
    }

    private static CertificateInfo Info(X509Certificate2 c) => new()
    {
        Subject = c.Subject, Issuer = c.Issuer, Thumbprint = c.Thumbprint, NotBefore = c.NotBefore, NotAfter = c.NotAfter,
        HasClientAuthEku = true, Certificate = c
    };

    private static MtlsGateway Gateway(X509Certificate2 cert) =>
        new(new GatewayRoutes(new[] { new GatewayRoute("/", new Uri("https://orders.example")) }), cert, false, TimeSpan.FromSeconds(30));

    private static readonly CertificateOrigin BySubject = new() { Query = "svc-orders" };

    // ---- choosing among renewals (every command) ----

    [Fact]
    public void Several_certificates_of_one_subject_from_one_issuer_are_a_renewal_and_the_newest_is_used()
    {
        var now = DateTimeOffset.Now;
        using var old = Cert(now.AddDays(-360), now.AddDays(5));
        using var renewed = Cert(now.AddDays(-1), now.AddDays(365));
        var err = new StringWriter();
        var hit = CertPicker.Resolve(new[] { Info(old), Info(renewed) }, "svc-orders", err);
        Assert.Same(renewed, hit.Certificate);
        Assert.Contains("a renewal", err.ToString());
        Assert.Contains(renewed.Thumbprint, err.ToString());
    }

    [Fact]
    public void A_newer_certificate_that_is_not_valid_yet_is_not_the_one_used()
    {
        var now = DateTimeOffset.Now;
        using var current = Cert(now.AddDays(-30), now.AddDays(300));
        using var future = Cert(now.AddDays(10), now.AddDays(800));
        var hit = CertPicker.Resolve(new[] { Info(future), Info(current) }, "svc-orders", TextWriter.Null);
        Assert.Same(current, hit.Certificate);
    }

    [Fact]
    public void The_same_subject_from_two_issuers_is_still_a_choice_to_make()
    {
        var now = DateTimeOffset.Now;
        using var otherCa = LongLivedCa("Test CA");
        using var corp = Cert(now.AddDays(-1), now.AddDays(300));
        using var test = SelfSignedCertificateFactory.CreateSignedCertificate("svc-orders", otherCa, false, true);
        var ex = Assert.Throws<CliDataException>(() => CertPicker.Resolve(new[] { Info(corp), Info(test) }, "svc-orders", TextWriter.Null));
        Assert.Contains("be more specific", ex.Message);
    }

    // ---- the running gateway ----

    [Fact]
    public void A_renewal_that_appears_in_the_store_is_presented_without_a_restart()
    {
        var now = DateTimeOffset.Now;
        using var old = Cert(now.AddDays(-360), now.AddDays(5));
        using var renewed = Cert(now.AddDays(-1), now.AddDays(365));
        var store = new List<CertificateInfo> { Info(old) };
        var services = new CliServices { ListCertificates = _ => store.ToList(), UserConfigPath = null };
        using var gw = Gateway(old);
        var log = new List<string>();
        var watch = new ServeCertificateWatch(gw, new[] { (old, BySubject) }, services, log.Add, TimeSpan.FromMinutes(5));

        watch.Check();
        Assert.Same(old, gw.Current(old));

        store.Add(Info(renewed));
        watch.Check();
        Assert.Same(renewed, gw.Current(old));
        Assert.Contains(log, l => l.StartsWith("certificate renewed:") && l.Contains(renewed.Thumbprint) && l.Contains(old.Thumbprint));

        // Found again on the next look, it changes nothing and is not announced twice.
        int lines = log.Count;
        watch.Check();
        Assert.Equal(lines, log.Count);
    }

    [Fact]
    public void An_older_or_not_yet_valid_certificate_found_by_the_query_is_not_taken()
    {
        var now = DateTimeOffset.Now;
        using var current = Cert(now.AddDays(-10), now.AddDays(300));
        using var older = Cert(now.AddDays(-400), now.AddDays(100), "svc-orders old");
        using var future = Cert(now.AddDays(3), now.AddDays(900));
        var store = new List<CertificateInfo> { Info(older) };
        var services = new CliServices { ListCertificates = _ => store.ToList(), UserConfigPath = null };
        using var gw = Gateway(current);
        var watch = new ServeCertificateWatch(gw, new[] { (current, BySubject) }, services, _ => { }, TimeSpan.FromMinutes(5));
        watch.Check();
        Assert.Same(current, gw.Current(current));
        store.Clear();
        store.Add(Info(future));
        watch.Check();
        Assert.Same(current, gw.Current(current));
    }

    [Fact]
    public void A_certificate_named_by_thumbprint_is_not_looked_for_and_the_warning_says_why()
    {
        var now = DateTime.Now;
        using var soon = Cert(now.AddDays(-300), now.AddDays(6));
        int listed = 0;
        var services = new CliServices { ListCertificates = _ => { listed++; return Array.Empty<CertificateInfo>(); }, UserConfigPath = null };
        using var gw = Gateway(soon);
        var log = new List<string>();
        var watch = new ServeCertificateWatch(gw, new[] { (soon, new CertificateOrigin { Query = soon.Thumbprint }) }, services, log.Add,
            TimeSpan.FromMinutes(5), startupWarned: false);
        watch.Check();
        Assert.Equal(0, listed);
        Assert.Contains(log, l => l.Contains("expires in") && l.Contains("by thumbprint"));
    }

    [Fact]
    public void An_expiring_certificate_is_warned_about_daily_and_an_expired_one_hourly()
    {
        var start = DateTime.Now;
        var clock = start;
        using var cert = Cert(start.AddDays(-300), start.AddDays(3).AddHours(1));
        var services = new CliServices { ListCertificates = _ => new[] { Info(cert) }, UserConfigPath = null };
        using var gw = Gateway(cert);
        var log = new List<string>();
        var watch = new ServeCertificateWatch(gw, new[] { (cert, BySubject) }, services, log.Add, TimeSpan.FromMinutes(5),
            () => clock, startupWarned: false);
        int Warnings() => log.Count(l => l.StartsWith("warning: ") && l.Contains("svc-orders"));

        Assert.Equal(1, Warnings());
        Assert.Contains(log, l => l.Contains("expires in 3 days") && l.Contains("within 5 minutes"));
        clock = start.AddHours(5); watch.Check();
        Assert.Equal(1, Warnings());
        clock = start.AddHours(25); watch.Check();
        Assert.Equal(2, Warnings());

        clock = start.AddDays(4); watch.Check();          // expired now
        Assert.Equal(3, Warnings());
        Assert.Contains(log, l => l.Contains("is expired") && l.Contains("refuse the connection"));
        clock = start.AddDays(4).AddMinutes(30); watch.Check();
        Assert.Equal(3, Warnings());
        clock = start.AddDays(4).AddMinutes(65); watch.Check();
        Assert.Equal(4, Warnings());
    }

    [Fact]
    public void A_healthy_certificate_is_not_warned_about()
    {
        var now = DateTime.Now;
        using var cert = Cert(now.AddDays(-10), now.AddDays(300));
        var services = new CliServices { ListCertificates = _ => new[] { Info(cert) }, UserConfigPath = null };
        using var gw = Gateway(cert);
        var log = new List<string>();
        var watch = new ServeCertificateWatch(gw, new[] { (cert, BySubject) }, services, log.Add, TimeSpan.FromMinutes(5), startupWarned: false);
        watch.Check();
        Assert.Empty(log);
    }

    [Fact]
    public void A_certificate_file_replaced_with_its_renewal_is_presented_without_a_restart()
    {
        var now = DateTimeOffset.Now;
        using var old = Cert(now.AddDays(-360), now.AddDays(20));
        using var renewed = Cert(now.AddDays(-1), now.AddDays(365));
        string file = Path.Combine(_dir, "svc.pfx");
        File.WriteAllBytes(file, old.Export(X509ContentType.Pfx, "pw"));
        var services = new CliServices { UserConfigPath = null };
        using var gw = Gateway(old);
        var log = new List<string>();
        var watch = new ServeCertificateWatch(gw, new[] { (old, new CertificateOrigin { File = file, Password = "pw" }) }, services, log.Add,
            TimeSpan.FromMinutes(5));

        watch.Check();   // unchanged file: not even read
        Assert.Same(old, gw.Current(old));

        File.WriteAllBytes(file, renewed.Export(X509ContentType.Pfx, "pw"));
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddSeconds(5));
        watch.Check();
        Assert.Equal(renewed.Thumbprint, gw.Current(old)!.Thumbprint);
        Assert.Contains(log, l => l.StartsWith("certificate renewed:"));
        services.DisposeOwned();
    }

    [Fact]
    public void A_certificate_file_that_cannot_be_read_keeps_the_one_presented_and_says_so_once()
    {
        var now = DateTimeOffset.Now;
        using var old = Cert(now.AddDays(-30), now.AddDays(200));
        string file = Path.Combine(_dir, "svc.pfx");
        File.WriteAllBytes(file, old.Export(X509ContentType.Pfx, "pw"));
        var services = new CliServices { UserConfigPath = null };
        using var gw = Gateway(old);
        var log = new List<string>();
        var watch = new ServeCertificateWatch(gw, new[] { (old, new CertificateOrigin { File = file, Password = "pw" }) }, services, log.Add,
            TimeSpan.FromMinutes(5));
        File.WriteAllText(file, "half written");
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddSeconds(5));
        watch.Check();
        watch.Check();
        Assert.Same(old, gw.Current(old));
        Assert.Single(log, l => l.StartsWith("warning: could not look for a renewal"));
    }
}
