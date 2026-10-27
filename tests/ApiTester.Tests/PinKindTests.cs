using System.IO;
using System.Net.Http;
using System.Security.Cryptography.X509Certificates;
using ApiTester.Cli;
using ApiTester.Cli.Commands;
using ApiTester.Core;

namespace ApiTester.Tests;

/// <summary>Issuer and public-key pins beside the leaf pin: what survives a renewal, and what an
/// intercepting proxy's CA can be pinned by.</summary>
public class PinKindTests
{
    private static (X509Certificate2 ca, X509Certificate2 leaf, X509Chain chain) ChainOf()
    {
        var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("Pin CA");
        var leaf = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(ca);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.DisableCertificateDownloads = true;
        Assert.True(chain.Build(leaf));
        Assert.Equal(2, chain.ChainElements.Count);
        return (ca, leaf, chain);
    }

    [Fact]
    public void An_issuer_pin_matches_through_the_presented_chain_and_never_the_leaf_alone()
    {
        var (ca, leaf, chain) = ChainOf();
        using (ca) using (leaf) using (chain)
        {
            var state = new AppState();
            TrustService.Trust(state, "api.corp", ca.Thumbprint!, ca.Subject, PinKind.Issuer);

            Assert.True(TrustService.IsTrusted(state, "api.corp", leaf, chain));
            Assert.False(TrustService.IsTrusted(state, "api.corp", leaf, null));        // no chain, no issuer to match
            Assert.False(TrustService.IsTrusted(state, "other.corp", leaf, chain));
            Assert.False(TrustService.IsTrusted(state, "api.corp", ca.Thumbprint!));     // not a leaf pin

            // A renewed leaf from the same CA is still trusted; a leaf pin would have broken.
            using var renewed = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
            using var renewedChain = new X509Chain();
            renewedChain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            renewedChain.ChainPolicy.CustomTrustStore.Add(ca);
            renewedChain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            renewedChain.Build(renewed);
            Assert.True(TrustService.IsTrusted(state, "api.corp", renewed, renewedChain));
        }
    }

    [Fact]
    public void A_public_key_pin_matches_the_leaf_by_spki_and_survives_a_rekeyless_renewal()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var leaf = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        var state = new AppState();
        string spki = TrustService.SpkiSha256(leaf);
        Assert.Equal(44, spki.Length);
        TrustService.Trust(state, "api.corp", spki, leaf.Subject, PinKind.Spki);

        Assert.True(TrustService.IsTrusted(state, "api.corp", leaf, null));
        using var other = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        Assert.False(TrustService.IsTrusted(state, "api.corp", other, null));   // a new key
    }

    [Fact]
    public void The_chain_reaches_the_memoised_predicate_through_the_trust_context()
    {
        var (ca, leaf, chain) = ChainOf();
        using (ca) using (leaf) using (chain)
        {
            var state = new AppState();
            TrustService.Trust(state, "api.corp", ca.Thumbprint!, null, PinKind.Issuer);
            var predicate = new TrustPredicates(state).For("api.corp");
            Assert.False(predicate(leaf));
            Assert.True(TrustContext.Run(chain, System.Net.Security.SslPolicyErrors.None, () => predicate(leaf)));
            Assert.Null(TrustContext.CurrentChain);   // restored afterwards
        }
    }

    [Fact]
    public async Task A_send_is_accepted_on_a_public_key_pin_and_the_response_reports_the_spki()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("Client", ca, false, true);
        await using var server = await LoopbackMtlsServer.StartAsync(serverCert, clientCert.Thumbprint!);

        var state = new AppState();
        TrustService.Trust(state, "127.0.0.1", TrustService.SpkiSha256(serverCert), null, PinKind.Spki);
        var resp = await new ApiClient().SendAsync(
            new ApiRequest { Method = HttpMethod.Get, Url = server.BaseUrl }, clientCert,
            transport: new TransportOptions { Proxy = ProxyMode.None },
            trustServerCertificate: new TrustPredicates(state).For("127.0.0.1"));
        Assert.True(resp.IsSuccess, resp.Error?.Message);
        Assert.Equal(TrustService.SpkiSha256(serverCert), resp.Connection!.ServerCertificateSpkiSha256);
    }

    [Fact]
    public void Trust_add_stores_each_kind_and_refuses_malformed_pins()
    {
        string workspace = Path.Combine(Path.GetTempPath(), "certapi-pins-" + Guid.NewGuid().ToString("N") + ".json");
        new AppState().SaveTo(workspace);
        try
        {
            string thumb = "0123456789ABCDEF0123456789ABCDEF01234567";
            Assert.Equal(ExitCodes.Ok, CliApp.Run(new[] { "trust", "add", "api.corp", "--ca", thumb.ToLowerInvariant(), "--workspace", workspace }, TextWriter.Null, TextWriter.Null));
            string spki = Convert.ToBase64String(new byte[32]);
            Assert.Equal(ExitCodes.Ok, CliApp.Run(new[] { "trust", "add", "api.corp", "--spki", spki, "--workspace", workspace }, TextWriter.Null, TextWriter.Null));
            Assert.Equal(ExitCodes.Ok, CliApp.Run(new[] { "trust", "add", "api.corp", "--thumbprint", thumb, "--workspace", workspace }, TextWriter.Null, TextWriter.Null));

            var state = AppState.LoadFrom(workspace);
            Assert.Equal(3, state.TrustedServerCerts.Count);
            Assert.Contains(state.TrustedServerCerts, p => p.EffectiveKind == PinKind.Issuer && p.Thumbprint == thumb);
            Assert.Contains(state.TrustedServerCerts, p => p.EffectiveKind == PinKind.Spki && p.Thumbprint == spki);
            Assert.Contains(state.TrustedServerCerts, p => p.EffectiveKind == PinKind.Leaf && p.Kind is null);

            var list = new StringWriter();
            CliApp.Run(new[] { "trust", "list", "--workspace", workspace }, list, TextWriter.Null);
            Assert.Contains("issuer", list.ToString());
            Assert.Contains("public key", list.ToString());

            Assert.Equal(ExitCodes.Usage, CliApp.Run(new[] { "trust", "add", "api.corp", "--spki", "nope", "--workspace", workspace }, TextWriter.Null, TextWriter.Null));
            Assert.Equal(ExitCodes.Usage, CliApp.Run(new[] { "trust", "add", "api.corp", "--ca", "ABC", "--workspace", workspace }, TextWriter.Null, TextWriter.Null));
            Assert.Equal(ExitCodes.Usage, CliApp.Run(new[] { "trust", "add", "api.corp", "--ca", "--spki", "--workspace", workspace }, TextWriter.Null, TextWriter.Null));
        }
        finally { File.Delete(workspace); }
    }

    [Fact]
    public void An_issuer_pin_does_not_waive_a_certificate_issued_for_another_name()
    {
        var (ca, leaf, chain) = ChainOf();
        using (ca) using (leaf) using (chain)
        {
            var state = new AppState();
            TrustService.Trust(state, "api.corp", ca.Thumbprint!, ca.Subject, PinKind.Issuer);
            Assert.False(TrustService.IsTrusted(state, "api.corp", leaf, chain,
                System.Net.Security.SslPolicyErrors.RemoteCertificateNameMismatch | System.Net.Security.SslPolicyErrors.RemoteCertificateChainErrors));
            Assert.True(TrustService.IsTrusted(state, "api.corp", leaf, chain, System.Net.Security.SslPolicyErrors.RemoteCertificateChainErrors));

            // A leaf pin names this very certificate, so it still stands in for the name.
            TrustService.Trust(state, "api.corp", leaf.Thumbprint!, leaf.Subject, PinKind.Leaf);
            Assert.True(TrustService.IsTrusted(state, "api.corp", leaf, chain, System.Net.Security.SslPolicyErrors.RemoteCertificateNameMismatch));
        }
    }

    [Fact]
    public void A_connection_to_another_host_is_judged_by_that_hosts_pins()
    {
        var (ca, leaf, chain) = ChainOf();
        using (ca) using (leaf) using (chain)
        {
            var state = new AppState();
            TrustService.Trust(state, "api.corp", ca.Thumbprint!, null, PinKind.Issuer);
            var predicate = new TrustPredicates(state).For("api.corp");
            Assert.True(TrustContext.Run(chain, System.Net.Security.SslPolicyErrors.None, "api.corp", () => predicate(leaf)));
            Assert.False(TrustContext.Run(chain, System.Net.Security.SslPolicyErrors.None, "redirected.example", () => predicate(leaf)));
        }
    }

    [Fact]
    public async Task An_issuer_pin_refuses_a_leaf_that_names_the_ca_but_was_signed_by_another_key()
    {
        using var rootKey = System.Security.Cryptography.RSA.Create(2048);
        var rootReq = new System.Security.Cryptography.X509Certificates.CertificateRequest("CN=Corp Offline Root", rootKey,
            System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1);
        rootReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        rootReq.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        rootReq.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(rootReq.PublicKey, false));
        using var root = rootReq.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-2), DateTimeOffset.UtcNow.AddYears(2));

        using var caKey = System.Security.Cryptography.RSA.Create(2048);
        var caReq = new System.Security.Cryptography.X509Certificates.CertificateRequest("CN=Corp Issuing CA", caKey,
            System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1);
        caReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        caReq.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        caReq.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(caReq.PublicKey, false));
        caReq.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(root, true, false));
        using var ca = caReq.Create(root, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1), new byte[] { 9, 9 });

        // The forgery: the leaf names the pinned CA as its issuer, but someone else's key signed it.
        using var evilKey = System.Security.Cryptography.RSA.Create(2048);
        using var leafKey = System.Security.Cryptography.RSA.Create(2048);
        var leafReq = new System.Security.Cryptography.X509Certificates.CertificateRequest("CN=localhost", leafKey,
            System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1);
        leafReq.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(ca, true, false));
        leafReq.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new System.Security.Cryptography.OidCollection { new("1.3.6.1.5.5.7.3.1") }, false));
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        leafReq.CertificateExtensions.Add(san.Build());
        using var forged0 = leafReq.Create(new X500DistinguishedName("CN=Corp Issuing CA"),
            X509SignatureGenerator.CreateForRSA(evilKey, System.Security.Cryptography.RSASignaturePadding.Pkcs1),
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30), new byte[] { 1, 2, 3, 4 });
        using var withKey = forged0.CopyWithPrivateKey(leafKey);
        using var forged = X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pfx), null);
        using var caPublic = X509CertificateLoader.LoadCertificate(ca.RawData);

        var context = System.Net.Security.SslStreamCertificateContext.Create(forged, new X509Certificate2Collection { caPublic }, offline: true);
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        var serve = Task.Run(async () =>
        {
            for (int i = 0; i < 2; i++)
            {
                using var c = await listener.AcceptTcpClientAsync();
                try
                {
                    using var ssl = new System.Net.Security.SslStream(c.GetStream());
                    await ssl.AuthenticateAsServerAsync(new System.Net.Security.SslServerAuthenticationOptions { ServerCertificateContext = context });
                    var buf = new byte[4096];
                    await ssl.ReadAsync(buf);
                    await ssl.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 4\r\nConnection: close\r\n\r\nevil"u8.ToArray());
                }
                catch { /* the client refused the handshake */ }
            }
        });
        try
        {
            var state = new AppState();
            TrustService.Trust(state, "localhost", ca.Thumbprint!, ca.Subject, PinKind.Issuer);
            var resp = await new ApiClient().SendAsync(new ApiRequest { Method = HttpMethod.Get, Url = $"https://localhost:{port}/" },
                null, new TransportOptions(), new TrustPredicates(state).For("localhost"));
            Assert.NotEqual(200, resp.StatusCode);
        }
        finally { listener.Stop(); }
    }
}
