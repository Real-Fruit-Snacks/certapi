using ApiTester.Core;

namespace ApiTester.Tests;

/// <summary>`tls` waited a fixed 400 ms after every TLS 1.3 handshake for a certificate request
/// that might come after it. It now waits only when none came in the handshake, and only as long
/// as one could take to arrive.</summary>
public class TlsProbeWaitTests
{
    [Theory]
    [InlineData(0, 50)]
    [InlineData(1, 52)]
    [InlineData(40, 130)]
    [InlineData(500, 400)]
    public void The_wait_is_two_round_trips_and_a_margin_within_bounds(int connectMs, int expectedMs) =>
        Assert.Equal(expectedMs, TlsProbe.PostHandshakeWait(TimeSpan.FromMilliseconds(connectMs)).TotalMilliseconds);

    [Fact]
    public async Task A_nearby_server_that_asks_for_no_certificate_is_not_waited_on()
    {
        using var cert = SelfSignedCertificateFactory.CreateSelfSignedServerCertificate("localhost", new[] { "localhost" });
        await using var mock = MockServer.Start(0, MockTlsMode.Https, cert);
        var probe = () => TlsProbe.ProbeAsync("127.0.0.1", mock.Port, StartTls.None, null, null, [],
            new TransportOptions(), TimeSpan.FromSeconds(10));
        await probe();                       // first use pays the TLS stack's own start-up
        var r = await probe();
        Assert.True(r.Connected, r.Error);
        Assert.True(r.Elapsed < TimeSpan.FromMilliseconds(350), $"took {r.Elapsed.TotalMilliseconds:0} ms");
    }
}
