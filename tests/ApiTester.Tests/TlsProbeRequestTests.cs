using System.IO;
using System.Security.Cryptography.X509Certificates;
using ApiTester.Cli;
using ApiTester.Core;

namespace ApiTester.Tests;

/// <summary>`tls` against a server that wants a client certificate. A Windows server asks with no
/// list of issuers, which `tls` used to read as no request at all when no --cert was given; and a
/// server judges the client's certificate only after the client's side of the handshake is done,
/// so a refusal shows as the connection being ended just after it.</summary>
public class TlsProbeRequestTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("certapi-tlsreq-").FullName;
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static Task<TlsProbeResult> Probe(int port, X509Certificate2? cert = null) =>
        TlsProbe.ProbeAsync("127.0.0.1", port, StartTls.None, "localhost", cert, [], new TransportOptions(), TimeSpan.FromSeconds(10));

    [Fact]
    public async Task A_server_that_requires_a_certificate_is_reported_as_asking_and_refusing_without_one()
    {
        using var serverCert = MockCertificates.Generate(MockTlsMode.Mtls, _dir).ServerCertificate;
        await using var mock = MockServer.Start(0, MockTlsMode.Mtls, serverCert);
        TlsProbeResult r = null!;
        for (int i = 0; i < 3 && r?.RefusedAfterHandshake is null; i++) r = await Probe(mock.Port);   // as below
        Assert.True(r.Connected, r.Error);
        Assert.True(r.ClientCertificateRequested);
        Assert.Null(r.ClientCertificateSent);
        Assert.NotNull(r.RefusedAfterHandshake);
    }

    [Fact]
    public async Task A_server_that_asks_for_nothing_is_reported_as_asking_for_nothing()
    {
        using var serverCert = MockCertificates.Generate(MockTlsMode.Https, _dir).ServerCertificate;
        await using var mock = MockServer.Start(0, MockTlsMode.Https, serverCert);
        var r = await Probe(mock.Port);
        Assert.True(r.Connected, r.Error);
        Assert.False(r.ClientCertificateRequested);
        Assert.Null(r.RefusedAfterHandshake);
    }

    [Fact]
    public async Task A_certificate_the_server_takes_is_sent_and_not_refused()
    {
        using var serverCert = MockCertificates.Generate(MockTlsMode.Mtls, _dir).ServerCertificate;
        await using var mock = MockServer.Start(0, MockTlsMode.Mtls, serverCert);
        using var client = X509CertificateLoader.LoadPkcs12FromFile(Path.Combine(_dir, "mock-client.pfx"), (string?)null);
        var r = await Probe(mock.Port, client);
        Assert.True(r.ClientCertificateRequested);
        Assert.Contains("certapi mock client", r.ClientCertificateSent);
        Assert.Null(r.RefusedAfterHandshake);
    }

    [Fact]
    public async Task A_certificate_the_server_refuses_is_reported_and_fails_the_command()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("Probe CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        using var wanted = SelfSignedCertificateFactory.CreateSignedCertificate("Wanted Client", ca, false, true);
        using var other = SelfSignedCertificateFactory.CreateSignedCertificate("Other Client", ca, false, true);
        await using var server = await LoopbackMtlsServer.StartAsync(serverCert, wanted.Thumbprint);
        int port = new Uri(server.BaseUrl).Port;

        // Up to three tries: the refusal comes when the server has judged the certificate, and a
        // loaded test run can delay that past the probe's wait once. A probe that never sees it
        // fails every try.
        TlsProbeResult r = null!;
        for (int i = 0; i < 3 && r?.RefusedAfterHandshake is null; i++) r = await Probe(port, other);
        Assert.True(r.ClientCertificateRequested);
        Assert.NotNull(r.RefusedAfterHandshake);

        string pfx = Path.Combine(_dir, "other.pfx");
        File.WriteAllBytes(pfx, other.Export(X509ContentType.Pfx, "pw"));
        int code = -1;
        string output = "";
        for (int i = 0; i < 3 && code != 1; i++)
        {
            var so = new StringWriter();
            code = CliApp.Run(new[] { "tls", $"127.0.0.1:{port}", "--cert-file", pfx, "--cert-password", "pw", "--no-config" }, so, TextWriter.Null);
            output = so.ToString();
        }
        Assert.Equal(1, code);
        Assert.Contains("did not accept the certificate sent", output);
    }
}
