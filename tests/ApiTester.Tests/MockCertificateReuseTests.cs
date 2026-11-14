using System.IO;
using System.Security.Cryptography.X509Certificates;
using ApiTester.Cli;
using ApiTester.Core;

namespace ApiTester.Tests;

/// <summary>The mock keeps its certificates between starts: the same mock-ca.cer (trusted once),
/// the same server and client certificates, the private keys encrypted to the Windows user. Where
/// keys cannot be encrypted, nothing is kept.</summary>
public class MockCertificateReuseTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("certapi-mockreuse-").FullName;

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    private static bool CanKeep => SecretProtection.Default.IsAvailable;

    private (MockCertificates.Generated G, string Ca, string Server) Start(MockTlsMode mode = MockTlsMode.Mtls,
        MockTlsDefect defect = MockTlsDefect.None, bool fresh = false)
    {
        var g = MockCertificates.Generate(mode, _dir, defect, fresh);
        string thumb = g.ServerCertificate.Thumbprint;
        g.ServerCertificate.Dispose();
        return (g, Convert.ToBase64String(File.ReadAllBytes(Path.Combine(_dir, "mock-ca.cer"))), thumb);
    }

    [Fact]
    public void A_second_start_uses_the_same_certificates()
    {
        var first = Start();
        byte[] client = File.ReadAllBytes(Path.Combine(_dir, "mock-client.pfx"));
        var second = Start();
        Assert.False(first.G.Reused);
        if (!CanKeep)
        {
            Assert.False(second.G.Reused);
            Assert.False(File.Exists(Path.Combine(_dir, MockCertificates.CaKeyFile)));
            return;
        }
        Assert.True(second.G.Reused);
        Assert.Equal(first.Ca, second.Ca);
        Assert.Equal(first.Server, second.Server);
        Assert.Equal(client, File.ReadAllBytes(Path.Combine(_dir, "mock-client.pfx")));
    }

    [Fact]
    public void The_kept_server_certificate_can_still_serve()
    {
        Start(MockTlsMode.Https);
        using var again = MockCertificates.Generate(MockTlsMode.Https, _dir).ServerCertificate;
        Assert.True(again.HasPrivateKey);
        using var ca = X509CertificateLoader.LoadCertificateFromFile(Path.Combine(_dir, "mock-ca.cer"));
        Assert.True(MockCertificates.IssuedBy(again, ca));
    }

    [Fact]
    public void Fresh_makes_a_new_authority()
    {
        var first = Start();
        var second = Start(fresh: true);
        Assert.False(second.G.Reused);
        Assert.NotEqual(first.Ca, second.Ca);
        Assert.NotEqual(first.Server, second.Server);
    }

    [Fact]
    public void A_broken_certificate_is_not_kept_and_the_good_one_comes_back()
    {
        var good = Start(MockTlsMode.Https);
        var broken = Start(MockTlsMode.Https, MockTlsDefect.Expired);
        Assert.NotEqual(good.Server, broken.Server);
        var after = Start(MockTlsMode.Https);
        if (!CanKeep) return;
        Assert.Equal(good.Ca, broken.Ca);          // signed by the same, already trusted, authority
        Assert.Equal(good.Server, after.Server);
        using var cer = X509CertificateLoader.LoadCertificateFromFile(Path.Combine(_dir, "mock-server.cer"));
        Assert.Equal(good.Server, cer.Thumbprint);
    }

    [Fact]
    public void An_unreadable_key_means_new_certificates()
    {
        var first = Start();
        File.WriteAllText(Path.Combine(_dir, MockCertificates.CaKeyFile), "not a key");
        var second = Start();
        Assert.False(second.G.Reused);
        Assert.NotEqual(first.Ca, second.Ca);
    }

    [Fact]
    public void The_keys_are_never_written_in_the_clear()
    {
        Start();
        foreach (var name in new[] { MockCertificates.CaKeyFile, MockCertificates.ServerKeyFile })
        {
            string path = Path.Combine(_dir, name);
            if (!CanKeep) { Assert.False(File.Exists(path)); continue; }
            Assert.True(SecretProtection.LooksProtected(File.ReadAllText(path)));
        }
    }

    [Fact]
    public void The_server_certificate_names_both_loopback_addresses()
    {
        using var cert = MockCertificates.Generate(MockTlsMode.Https, _dir).ServerCertificate;
        var san = cert.Extensions.OfType<X509SubjectAlternativeNameExtension>().Single();
        Assert.Contains("localhost", san.EnumerateDnsNames());
        Assert.Contains(System.Net.IPAddress.Loopback, san.EnumerateIPAddresses());
        Assert.Contains(System.Net.IPAddress.IPv6Loopback, san.EnumerateIPAddresses());
    }

    [Fact]
    public async Task The_mock_answers_on_ipv6_loopback_too()
    {
        if (!System.Net.Sockets.Socket.OSSupportsIPv6) return;
        await using var mock = MockServer.Start(0, MockTlsMode.Http);
        using var http = new System.Net.Http.HttpClient();
        var body = await http.GetStringAsync($"http://[::1]:{mock.Port}/echo");
        Assert.Contains("certapi mock", body);
    }

    [Fact]
    public void New_certs_over_plain_http_is_a_usage_error()
    {
        var se = new StringWriter();
        int code = CliApp.Run(new[] { "mock", "--new-certs", "--port", "0", "--no-config" }, TextWriter.Null, se);
        Assert.Equal(2, code);
        Assert.Contains("--new-certs", se.ToString());
    }
}
