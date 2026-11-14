using System.Net.Http;
using ApiTester.Core;

namespace ApiTester.Tests;

/// <summary>Sends through a proxy share a pooled handler, as direct sends do: a second request to
/// the same site reuses the CONNECT tunnel and its handshake. What the handshake reports, and why a
/// refused one failed, still reaches every send.</summary>
public class ProxyPoolingTests
{
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(30);

    private static TransportOptions Via(LoopbackConnectProxy proxy, bool insecure = true) => new()
    {
        Proxy = ProxyMode.Explicit,
        ProxyUrl = proxy.Url,
        IgnoreServerCertificateErrors = insecure
    };

    private static Task<ApiResponse> Send(ApiClient client, MockServer mock, TransportOptions transport, string path = "") =>
        client.SendAsync(new ApiRequest { Method = HttpMethod.Get, Url = mock.BaseUrl + path, Timeout = SendTimeout },
            null, transport);

    [Fact]
    public async Task Requests_through_a_proxy_share_one_tunnel_and_each_reports_the_server_certificate()
    {
        using var serverCert = SelfSignedCertificateFactory.CreateSelfSignedServerCertificate(
            "localhost", new[] { "localhost" }, new[] { System.Net.IPAddress.Loopback });
        await using var mock = MockServer.Start(0, MockTlsMode.Https, serverCert);
        await using var proxy = await LoopbackConnectProxy.StartAsync();
        var client = new ApiClient();
        var transport = Via(proxy);

        var first = await Send(client, mock, transport, "one");
        var second = await Send(client, mock, transport, "two");
        var third = await Send(client, mock, transport, "three");

        Assert.True(first.IsSuccess, first.Error?.Message);
        Assert.True(second.IsSuccess, second.Error?.Message);
        Assert.True(third.IsSuccess, third.Error?.Message);
        Assert.Equal(1, proxy.ConnectCount);
        foreach (var r in new[] { first, second, third })
        {
            Assert.True(r.Connection!.ViaProxy);
            Assert.Equal(serverCert.Thumbprint, r.Connection.ServerCertificateThumbprint);
        }
    }

    [Fact]
    public async Task A_refused_certificate_is_reported_on_every_send_and_never_reused_by_an_insecure_one()
    {
        using var serverCert = SelfSignedCertificateFactory.CreateSelfSignedServerCertificate(
            "localhost", new[] { "localhost" }, new[] { System.Net.IPAddress.Loopback });
        await using var mock = MockServer.Start(0, MockTlsMode.Https, serverCert);
        await using var proxy = await LoopbackConnectProxy.StartAsync();
        var client = new ApiClient();

        // An untrusted self-signed certificate, checked: refused, and refused again on the next
        // send through the same pooled handler.
        var strict = Via(proxy, insecure: false);
        var refused1 = await Send(client, mock, strict);
        var refused2 = await Send(client, mock, strict);
        Assert.Equal(ApiErrorKind.ServerCertificateUntrusted, refused1.Error?.Kind);
        Assert.Equal(ApiErrorKind.ServerCertificateUntrusted, refused2.Error?.Kind);

        // --insecure is part of the sharing key: it gets its own handler and succeeds.
        var insecure = await Send(client, mock, Via(proxy, insecure: true));
        Assert.True(insecure.IsSuccess, insecure.Error?.Message);

        // And the strict one still refuses afterwards: nothing the insecure send accepted leaked.
        var refused3 = await Send(client, mock, strict);
        Assert.Equal(ApiErrorKind.ServerCertificateUntrusted, refused3.Error?.Kind);
    }

    [Fact]
    public async Task A_pin_that_does_not_match_is_named_as_the_reason_through_a_proxy()
    {
        using var serverCert = SelfSignedCertificateFactory.CreateSelfSignedServerCertificate(
            "localhost", new[] { "localhost" }, new[] { System.Net.IPAddress.Loopback });
        await using var mock = MockServer.Start(0, MockTlsMode.Https, serverCert);
        await using var proxy = await LoopbackConnectProxy.StartAsync();
        var transport = Via(proxy) with { PinnedPublicKeys = new[] { new string('A', 43) + "=" } };

        var r = await Send(new ApiClient(), mock, transport);

        Assert.Equal(ApiErrorKind.ServerCertificateUntrusted, r.Error?.Kind);
        Assert.Equal(ApiClient.PinRefusedMessage, r.Error?.Message);
    }

    [Fact]
    public async Task A_proxy_failure_after_a_refusal_is_reported_as_the_proxy_not_the_certificate()
    {
        using var serverCert = SelfSignedCertificateFactory.CreateSelfSignedServerCertificate(
            "localhost", new[] { "localhost" }, new[] { System.Net.IPAddress.Loopback });
        var mock = MockServer.Start(0, MockTlsMode.Https, serverCert);
        var proxy = await LoopbackConnectProxy.StartAsync();
        var client = new ApiClient();
        var strict = Via(proxy, insecure: false);
        var refused = await Send(client, mock, strict);
        Assert.Equal(ApiErrorKind.ServerCertificateUntrusted, refused.Error?.Kind);

        // The proxy goes away: the next send's failure is not a TLS failure, so the refusal kept
        // from before must not be what it is reported as.
        await proxy.DisposeAsync();
        await mock.DisposeAsync();
        var gone = await Send(client, mock, strict);
        Assert.NotNull(gone.Error);
        Assert.NotEqual(ApiErrorKind.ServerCertificateUntrusted, gone.Error!.Kind);
    }
}
