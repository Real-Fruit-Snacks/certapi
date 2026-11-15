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

    [Theory]
    [InlineData(0, 500)]
    [InlineData(40, 580)]
    [InlineData(900, 1500)]
    public void A_server_judging_a_certificate_is_given_half_a_second_more(int connectMs, int expectedMs) =>
        Assert.Equal(expectedMs, TlsProbe.JudgementWait(TimeSpan.FromMilliseconds(connectMs)).TotalMilliseconds);

    [Fact]
    public async Task A_nearby_server_that_asks_for_no_certificate_is_not_waited_on()
    {
        using var cert = SelfSignedCertificateFactory.CreateSelfSignedServerCertificate("localhost", new[] { "localhost" });
        await using var mock = MockServer.Start(0, MockTlsMode.Https, cert);
        var probe = () => TlsProbe.ProbeAsync("127.0.0.1", mock.Port, StartTls.None, null, null, [],
            new TransportOptions(), TimeSpan.FromSeconds(10));
        await probe();                       // first use pays the TLS stack's own start-up
        // The fastest of three: the old wait made every probe at least 400 ms, so one quick probe
        // proves it gone, while a loaded machine slowing one probe down does not fail the test.
        var best = TimeSpan.MaxValue;
        for (int i = 0; i < 3 && best >= TimeSpan.FromMilliseconds(350); i++)
        {
            var r = await probe();
            Assert.True(r.Connected, r.Error);
            if (r.Elapsed < best) best = r.Elapsed;
        }
        Assert.True(best < TimeSpan.FromMilliseconds(350), $"took {best.TotalMilliseconds:0} ms");
    }
}
