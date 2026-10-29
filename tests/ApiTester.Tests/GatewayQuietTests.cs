using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using ApiTester.Core;

namespace ApiTester.Tests;

/// <summary>What the gateway asks of the shared parts of Windows: the system proxy once per site
/// rather than per request, no certificate downloads a connection does not need, no lookup of the
/// user's name per request, and a connect that cannot hang for long.</summary>
public class GatewayQuietTests
{
    /// <summary>A system proxy that counts how often it is asked.</summary>
    private sealed class CountingProxy : IWebProxy
    {
        public int Asked;
        public Uri? Answer = new("http://proxy.corp:8080/");
        public bool Throw;
        public int DelayMs;
        public ICredentials? Credentials { get; set; }
        public Uri? GetProxy(Uri destination)
        {
            Interlocked.Increment(ref Asked);
            if (DelayMs > 0) Thread.Sleep(DelayMs);
            if (Throw) throw new InvalidOperationException("the PAC script could not be downloaded");
            return Answer;
        }
        public bool IsBypassed(Uri host) => false;
    }

    [Fact]
    public void The_system_proxy_is_asked_once_per_site_until_the_answer_is_five_minutes_old()
    {
        long now = 0;
        var inner = new CountingProxy();
        var cached = new ProxyConfiguration.CachedSystemProxy(inner, _ => false, () => now);

        for (int i = 0; i < 100; i++)
            Assert.Equal(inner.Answer, cached.GetProxy(new Uri($"https://app.corp/page/{i}")));
        Assert.Equal(1, inner.Asked);
        Assert.False(cached.IsBypassed(new Uri("https://app.corp/other")));
        Assert.Equal(1, inner.Asked);

        cached.GetProxy(new Uri("https://login.corp/"));
        Assert.Equal(2, inner.Asked);   // another site is its own question

        now += (long)ProxyConfiguration.CachedSystemProxy.DirectLifetime.TotalMilliseconds + 1;
        cached.GetProxy(new Uri("https://app.corp/"));
        Assert.Equal(2, inner.Asked);   // a proxy answer outlives a "no proxy" one

        now += (long)ProxyConfiguration.CachedSystemProxy.Lifetime.TotalMilliseconds;
        cached.GetProxy(new Uri("https://app.corp/"));
        Assert.Equal(3, inner.Asked);   // an old answer is asked again
    }

    [Fact]
    public void No_proxy_and_a_failed_lookup_are_asked_again_after_thirty_seconds()
    {
        // With automatic configuration a lookup that fails also answers "no proxy": a PAC address
        // that was unreachable for a moment must not send a site direct for five minutes.
        long now = 0;
        var inner = new CountingProxy { Throw = true };
        var cached = new ProxyConfiguration.CachedSystemProxy(inner, _ => false, () => now);
        for (int i = 0; i < 20; i++)
        {
            Assert.Null(cached.GetProxy(new Uri("https://app.corp/")));
            Assert.True(cached.IsBypassed(new Uri("https://app.corp/")));
        }
        Assert.Equal(1, inner.Asked);

        now += (long)ProxyConfiguration.CachedSystemProxy.DirectLifetime.TotalMilliseconds + 1;
        inner.Throw = false;
        Assert.Equal(inner.Answer, cached.GetProxy(new Uri("https://app.corp/")));
        Assert.Equal(2, inner.Asked);
    }

    [Fact]
    public async Task Requests_arriving_together_share_one_question()
    {
        var inner = new CountingProxy { DelayMs = 100 };
        var cached = new ProxyConfiguration.CachedSystemProxy(inner, _ => false, () => 0);
        await Task.WhenAll(Enumerable.Range(0, 30).Select(i => Task.Run(() => cached.GetProxy(new Uri($"https://app.corp/{i}")))));
        Assert.Equal(1, inner.Asked);
    }

    [Fact]
    public void A_site_whose_answer_is_a_failover_list_or_unknown_is_left_to_the_system_proxy()
    {
        int probes = 0;
        var cached = new ProxyConfiguration.CachedSystemProxy(new CountingProxy(), u =>
        {
            probes++;
            if (u.Host == "broken.corp") throw new InvalidOperationException("no answer");
            return u.Host == "app.corp";
        }, () => 0);
        Assert.False(cached.Takes(new Uri("https://app.corp/")));
        Assert.True(cached.Takes(new Uri("https://login.corp/")));
        Assert.False(cached.Takes(new Uri("https://broken.corp/")));
        Assert.False(cached.Takes(new Uri("https://app.corp/second")));
        Assert.Equal(3, probes);   // asked once per site
    }

    [Fact]
    public void Proxy_credentials_are_the_signed_in_user_behind_an_object_of_their_own()
    {
        // The DefaultCredentials object itself makes .NET look the user's name up in lsass for
        // every request through a proxy; the same credentials behind another object do not.
        var cached = new ProxyConfiguration.CachedSystemProxy(new CountingProxy(), _ => false, () => 0);
        Assert.NotSame(CredentialCache.DefaultCredentials, cached.Credentials);
        var site = new Uri("http://proxy.corp:8080/");
        Assert.Same(CredentialCache.DefaultCredentials.GetCredential(site, "Negotiate"), cached.Credentials!.GetCredential(site, "Negotiate"));
        Assert.Null(cached.Credentials.GetCredential(site, "Basic"));

        using var gateway = MtlsGateway.CreateHandler(null, false, new TransportOptions { Proxy = ProxyMode.Explicit, ProxyUrl = "http://proxy.corp:8080" }, null);
        Assert.NotSame(CredentialCache.DefaultCredentials, gateway.DefaultProxyCredentials);
        Assert.NotSame(CredentialCache.DefaultCredentials, gateway.Proxy!.Credentials);
        Assert.Same(CredentialCache.DefaultCredentials.GetCredential(site, "NTLM"), gateway.Proxy.Credentials!.GetCredential(site, "NTLM"));
    }

    [Fact]
    public void Under_insecure_the_gateway_downloads_nothing_for_the_chain_and_other_commands_are_unchanged()
    {
        // send, trust add --from-url and the app's diagnostics show the chain as Windows builds it.
        var send = new SslClientAuthenticationOptions();
        RevocationCheck.Apply(send, new TransportOptions { IgnoreServerCertificateErrors = true });
        Assert.Null(send.CertificateChainPolicy);

        using var insecure = MtlsGateway.CreateHandler(null, ignoreServerCertificateErrors: true,
            new TransportOptions { Revocation = RevocationMode.Online }, trustServerCertificate: null);
        var policy = insecure.SslOptions.CertificateChainPolicy!;
        Assert.True(policy.DisableCertificateDownloads);
        Assert.Equal(X509RevocationMode.NoCheck, policy.RevocationMode);   // its result would be ignored too

        using var checking = MtlsGateway.CreateHandler(null, false, new TransportOptions(), null);
        Assert.Null(checking.SslOptions.CertificateChainPolicy);   // the system's own chain building, downloads and all
    }

    [Fact]
    public void A_gateway_handler_bounds_its_connections_and_leaves_the_handshake_its_own_time()
    {
        using var handler = MtlsGateway.CreateHandler(null, ignoreServerCertificateErrors: true, transport: null,
            trustServerCertificate: null, maxConnectionsPerServer: 16);
        Assert.Equal(16, handler.MaxConnectionsPerServer);
        // Without --connect-timeout only the TCP connect is bounded (in the connect step): a
        // handshake can wait on a smart card's PIN prompt.
        Assert.Equal(Timeout.InfiniteTimeSpan, handler.ConnectTimeout);

        using var explicitTimeout = MtlsGateway.CreateHandler(null, false, new TransportOptions { ConnectTimeout = TimeSpan.FromSeconds(3) }, null);
        Assert.Equal(TimeSpan.FromSeconds(3), explicitTimeout.ConnectTimeout);
    }
}
