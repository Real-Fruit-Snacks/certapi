using System.IO;
using System.Text;
using System.Text.Json;
using ApiTester.Cli;
using ApiTester.Core;

namespace ApiTester.Tests.Cli;

public class TimingCliTests
{
    private static (int Code, string Out, string Err) Run(params string[] args)
    {
        var so = new StringWriter();
        var se = new StringWriter();
        int code = CliApp.Run(args.Append("--no-config").ToArray(), so, se, new MemoryStream(), new CliServices
        {
            LiveStatePath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json"),
            UserConfigPath = null,
        });
        return (code, so.ToString(), se.ToString());
    }

    [Fact]
    public async Task Timing_prints_the_phases_of_a_new_connection()
    {
        await using var srv = MockServer.Start(0, MockTlsMode.Http);
        var r = Run("send", srv.BaseUrl.TrimEnd('/') + "/x", "--timing");
        Assert.Equal(0, r.Code);
        var line = r.Err.Split('\n').Single(l => l.StartsWith("timing: "));
        Assert.Contains("connect ", line);
        Assert.Contains("wait ", line);
        Assert.Contains("total ", line);
        Assert.DoesNotContain("tls", line);              // plain http: no handshake to report
    }

    [Fact]
    public async Task Timing_over_tls_reports_the_handshake()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("Timing CA");
        using var cert = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        await using var srv = MockServer.Start(0, MockTlsMode.Https, cert);
        var r = Run("send", srv.BaseUrl.TrimEnd('/') + "/x", "--timing", "-k");
        Assert.Equal(0, r.Code);
        Assert.Contains("tls ", r.Err.Split('\n').Single(l => l.StartsWith("timing: ")));
    }

    [Fact]
    public async Task Timing_is_printed_even_with_quiet()
    {
        await using var srv = MockServer.Start(0, MockTlsMode.Http);
        var r = Run("send", srv.BaseUrl.TrimEnd('/') + "/x", "--timing", "-q");
        Assert.StartsWith("timing: ", r.Err.Trim());
    }

    [Fact]
    public async Task With_json_the_timing_is_in_the_envelope()
    {
        await using var srv = MockServer.Start(0, MockTlsMode.Http);
        var r = Run("send", srv.BaseUrl.TrimEnd('/') + "/x", "--timing", "--json");
        Assert.Equal(0, r.Code);
        var t = JsonDocument.Parse(r.Out).RootElement.GetProperty("timing");
        Assert.True(t.GetProperty("totalMs").GetDouble() > 0);
        Assert.Equal(JsonValueKind.Null, t.GetProperty("tlsMs").ValueKind);
        Assert.False(t.GetProperty("reused").GetBoolean());
        Assert.DoesNotContain("timing: ", r.Err);
    }

    [Fact]
    public async Task Without_the_flag_the_envelope_is_unchanged()
    {
        await using var srv = MockServer.Start(0, MockTlsMode.Http);
        var r = Run("send", srv.BaseUrl.TrimEnd('/') + "/x", "--json");
        Assert.False(JsonDocument.Parse(r.Out).RootElement.TryGetProperty("timing", out _));
    }

    [Fact]
    public void Timing_with_all_ips_is_a_usage_error()
    {
        var r = Run("send", "http://127.0.0.1:9/", "--timing", "--all-ips");
        Assert.Equal(2, r.Code);
    }

    [Fact]
    public void Write_out_points_at_timing()
    {
        var r = Run("send", "http://127.0.0.1:9/", "-w", "%{time_total}");
        Assert.Equal(2, r.Code);
        Assert.Contains("--timing", r.Err);
    }

    [Fact]
    public void Render_names_each_phase_that_happened()
    {
        var t = new PhaseTimings
        {
            Dns = TimeSpan.FromMilliseconds(2.5), Connect = TimeSpan.FromMilliseconds(12), Total = TimeSpan.FromMilliseconds(40),
            Requests = 2,
        };
        Assert.Equal("dns 2.5 ms · connect 12 ms · total 40 ms · last of 2 requests", t.Render());
        Assert.StartsWith("connection reused", new PhaseTimings { Reused = true, Total = TimeSpan.FromMilliseconds(3) }.Render());
    }
}
