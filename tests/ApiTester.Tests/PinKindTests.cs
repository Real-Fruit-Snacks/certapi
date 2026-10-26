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
            Assert.True(TrustContext.Run(chain, () => predicate(leaf)));
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
}
