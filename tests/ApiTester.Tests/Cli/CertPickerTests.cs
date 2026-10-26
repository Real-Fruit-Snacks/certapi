using ApiTester.Cli;
using ApiTester.Core;
using System;
using System.IO;
using System.Security.Cryptography.X509Certificates;

namespace ApiTester.Tests.Cli;

public class CertPickerTests
{
    private static CertificateInfo Info(string subject, X509Certificate2 cert, DateTime? notAfter = null) => new()
    {
        Subject = subject,
        Issuer = "CN=CA",
        Thumbprint = cert.Thumbprint!,
        NotBefore = DateTime.Now.AddDays(-1),
        NotAfter = notAfter ?? DateTime.Now.AddDays(30),
        HasClientAuthEku = true,
        Certificate = cert
    };

    private static X509Certificate2 MakeCert(string cn)
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        return SelfSignedCertificateFactory.CreateSignedCertificate(cn, ca, false, true);
    }

    [Fact]
    public void Resolves_by_thumbprint_ignoring_case_and_spaces()
    {
        var cert = MakeCert("Alice");
        var list = new[] { Info("CN=Alice", cert) };
        var spaced = string.Join(" ", cert.Thumbprint!.ToLowerInvariant().Chunk(2).Select(c => new string(c)));

        var hit = CertPicker.Resolve(list, spaced, TextWriter.Null);
        Assert.Equal("CN=Alice", hit.Subject);
    }

    [Fact]
    public void Resolves_by_subject_substring()
    {
        var list = new[] { Info("CN=Alice Prod", MakeCert("AliceProd")), Info("CN=Bob", MakeCert("Bob")) };
        Assert.Equal("CN=Bob", CertPicker.Resolve(list, "bob", TextWriter.Null).Subject);
    }

    [Fact]
    public void Zero_matches_is_a_data_error_pointing_at_certs()
    {
        var ex = Assert.Throws<CliDataException>(() =>
            CertPicker.Resolve(Array.Empty<CertificateInfo>(), "nobody", TextWriter.Null));
        Assert.Contains("certapi certs", ex.Message);
    }

    [Fact]
    public void Ambiguity_is_a_data_error_listing_candidates()
    {
        var list = new[] { Info("CN=Alice One", MakeCert("A1")), Info("CN=Alice Two", MakeCert("A2")) };
        var ex = Assert.Throws<CliDataException>(() => CertPicker.Resolve(list, "alice", TextWriter.Null));
        Assert.Contains("Alice One", ex.Message);
        Assert.Contains("Alice Two", ex.Message);
    }

    [Fact]
    public void Expired_match_warns_but_resolves()
    {
        var cert = MakeCert("Old");
        var list = new[] { Info("CN=Old", cert, DateTime.Now.AddDays(-1)) };
        var err = new StringWriter();
        var hit = CertPicker.Resolve(list, "old", err);
        Assert.Equal("CN=Old", hit.Subject);
        Assert.Contains("expired", err.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_certificate_expiring_soon_warns_in_advance_and_still_resolves()
    {
        // The outage this exists to prevent: the certificate still works today, so the command
        // must run — and must also say that it stops working in a week.
        //
        // The extra hour is load-bearing: days-until-expiry floors rather than rounds (by design —
        // "expires in 1 day" must not overstate 23 hours), so a NotAfter of exactly AddDays(7),
        // read a few milliseconds later, is 6.9999 days and reports 6. Sitting a whole hour past
        // the boundary keeps this assertion about the message, not about the clock.
        var cert = MakeCert("Soon");
        var list = new[] { Info("CN=Soon", cert, DateTime.Now.AddDays(7).AddHours(1)) };
        var err = new StringWriter();

        var hit = CertPicker.Resolve(list, "soon", err);

        Assert.Equal("CN=Soon", hit.Subject);
        Assert.Contains("expires in 7 days", err.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("is expired", err.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_healthy_certificate_resolves_silently()
    {
        var cert = MakeCert("Fine");
        var list = new[] { Info("CN=Fine", cert, DateTime.Now.AddDays(90)) };
        var err = new StringWriter();

        CertPicker.Resolve(list, "fine", err);

        Assert.Equal("", err.ToString());
    }
}

public class CertSelectorTests
{
    private static CertificateInfo Info(string subject, string issuer, X509Certificate2 cert) => new()
    {
        Subject = subject, Issuer = issuer, Thumbprint = cert.Thumbprint!,
        NotBefore = DateTime.Now.AddDays(-1), NotAfter = DateTime.Now.AddDays(30),
        HasClientAuthEku = true, Certificate = cert
    };

    private static (CertificateInfo corp, CertificateInfo test) Two()
    {
        using var corpCa = SelfSignedCertificateFactory.CreateCertificateAuthority("Corp Issuing CA");
        using var testCa = SelfSignedCertificateFactory.CreateCertificateAuthority("Test CA");
        return (Info("CN=matt", "CN=Corp Issuing CA", SelfSignedCertificateFactory.CreateSignedCertificate("matt", corpCa, false, true)),
                Info("CN=matt", "CN=Test CA", SelfSignedCertificateFactory.CreateSignedCertificate("matt", testCa, false, true)));
    }

    [Fact]
    public void The_issuer_filter_narrows_an_ambiguous_subject()
    {
        var (corp, test) = Two();
        var list = new[] { corp, test };
        Assert.Throws<CliDataException>(() => CertPicker.Resolve(list, "matt", TextWriter.Null));
        Assert.Same(corp, CertPicker.Resolve(list, "matt", "corp issuing", null, TextWriter.Null));
        Assert.Same(test, CertPicker.Resolve(list, null, "Test CA", null, TextWriter.Null));   // issuer alone
    }

    [Fact]
    public void The_serial_filter_is_exact_and_forgives_separators_and_leading_zeros()
    {
        var (corp, test) = Two();
        var list = new[] { corp, test };
        string serial = test.Certificate.SerialNumber;
        string spaced = string.Join(":", Enumerable.Range(0, serial.Length / 2).Select(i => serial.Substring(i * 2, 2))).ToLowerInvariant();
        Assert.Same(test, CertPicker.Resolve(list, null, null, "00" + spaced, TextWriter.Null));
        Assert.Same(test, CertPicker.Resolve(list, "matt", null, serial, TextWriter.Null));
        var ex = Assert.Throws<CliDataException>(() => CertPicker.Resolve(list, null, null, "DEADBEEF", TextWriter.Null));
        Assert.Contains("serial DEADBEEF", ex.Message);
    }

    [Fact]
    public void The_selectors_reach_the_picker_through_the_flags_and_refuse_a_file()
    {
        var (corp, test) = Two();
        var services = new CliServices { ListCertificates = _ => new[] { corp, test } };
        using var chosen = CliCert.Resolve(new Args(new[] { "--cert-issuer", "Corp" }), "CurrentUser", services, TextWriter.Null, _ => null);
        Assert.Equal(corp.Thumbprint, chosen!.Thumbprint);

        Assert.Throws<CliUsageException>(() =>
            CliCert.Resolve(new Args(new[] { "--cert-file", "x.pfx", "--cert-serial", "01" }), "CurrentUser", services, TextWriter.Null, _ => null));
    }
}
