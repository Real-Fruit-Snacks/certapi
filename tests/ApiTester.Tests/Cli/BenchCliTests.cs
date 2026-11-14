using System.IO;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using ApiTester.Cli;
using ApiTester.Core;

namespace ApiTester.Tests.Cli;

/// <summary>The command-line surface of the bench: the exit codes CI depends on, the figures the
/// report prints, the JSON envelope, and the two rules a user could otherwise be misled by — that
/// retries are off during a bench, and that a bench never writes state.</summary>
public class BenchCliTests
{
    // Never the developer's real %AppData%\CertApiTester\state.json: bench reads the live state for
    // environments and pinned certificates, so every test gets its own temp path.
    private static string TempState() => Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");

    private sealed class Loopback : IAsyncDisposable
    {
        public LoopbackMtlsServer Server = null!;
        public X509Certificate2 Client = null!;
        private X509Certificate2 _ca = null!, _server = null!;

        /// <summary>A counting loopback server: <c>failures: 0</c> always answers 200, a large count
        /// always answers <paramref name="failStatus"/>. Its request count is the ground truth behind
        /// the retry-suppression test.</summary>
        public static async Task<Loopback> StartAsync(int failures = 0, int failStatus = 503)
        {
            var l = new Loopback();
            l._ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
            l._server = SelfSignedCertificateFactory.CreateSignedCertificate(
                "localhost", l._ca, true, false, new[] { "localhost" });
            l.Client = SelfSignedCertificateFactory.CreateSignedCertificate("BenchCliClient", l._ca, false, true);
            l.Server = await LoopbackMtlsServer.StartFlakyAsync(l._server, l.Client.Thumbprint!, failures, failStatus);
            return l;
        }

        public async ValueTask DisposeAsync()
        {
            await Server.DisposeAsync();
            _ca.Dispose(); _server.Dispose(); Client.Dispose();
        }
    }

    private static CliServices Services(Loopback l, string livePath) => new()
    {
        LiveStatePath = livePath,
        FindCertificate = _ => l.Client,
        ListCertificates = _ => new[]
        {
            new CertificateInfo
            {
                Subject = "CN=BenchCliClient", Issuer = "CN=CA", Thumbprint = l.Client.Thumbprint!,
                NotBefore = DateTime.Now.AddDays(-1), NotAfter = DateTime.Now.AddDays(30),
                HasClientAuthEku = true, Certificate = l.Client
            }
        }
    };

    private static (int Code, string Out, string Err) Run(CliServices services, params string[] args)
    {
        var so = new StringWriter();
        var se = new StringWriter();
        int code = CliApp.Run(args, so, se, new MemoryStream(), services);
        return (code, so.ToString(), se.ToString());
    }

    [Fact]
    public async Task A_bench_prints_the_latency_table_and_the_connection_caveat()
    {
        await using var l = await Loopback.StartAsync();

        var r = Run(Services(l, TempState()),
            "bench", l.Server.BaseUrl, "--cert", "BenchCliClient", "--insecure", "-n", "8", "-c", "2");

        Assert.Equal(ExitCodes.Ok, r.Code);
        Assert.Contains("8 sent", r.Out);
        Assert.Contains("latency", r.Out);
        Assert.Contains("p50", r.Out);
        Assert.Contains("p90", r.Out);
        Assert.Contains("p99", r.Out);
        Assert.Contains("req/s", r.Out);
        Assert.Contains("200", r.Out);          // the status line
        // The one thing a user must not be misled about: what the number includes. This test
        // server answers 'Connection: close', so every request paid a new connection, and the
        // note says exactly that rather than claiming the pool was used.
        Assert.Contains("'Connection: close' on 8 of 8 responses", r.Out);
        Assert.DoesNotContain("pooled and reused", r.Out);
    }

    [Fact]
    public async Task Json_emits_an_envelope_with_every_latency_figure()
    {
        await using var l = await Loopback.StartAsync();

        var r = Run(Services(l, TempState()),
            "bench", l.Server.BaseUrl, "--cert", "BenchCliClient", "--insecure", "-n", "8", "-c", "2", "--json");

        Assert.Equal(ExitCodes.Ok, r.Code);
        using var doc = JsonDocument.Parse(r.Out);
        var root = doc.RootElement;
        Assert.Equal(8, root.GetProperty("sent").GetInt32());
        Assert.Equal(8, root.GetProperty("succeeded").GetInt32());
        Assert.Equal(0, root.GetProperty("failed").GetInt32());
        var latency = root.GetProperty("latencyMs");
        foreach (var key in new[] { "min", "p50", "p90", "p99", "max" })
            Assert.True(latency.GetProperty(key).GetDouble() > 0, $"{key} was not measured");
        Assert.Equal(8, root.GetProperty("statusCounts").GetProperty("200").GetInt32());
        Assert.Equal(8, root.GetProperty("closedByServer").GetInt32());
        Assert.Contains("every request opened a new connection",
            string.Join(" ", root.GetProperty("notes").EnumerateArray().Select(n => n.GetString())));
    }

    [Theory]
    // -n and --duration are alternatives: one bench is bounded one way or the other.
    [InlineData("-n", "10", "--duration", "5")]
    // More workers than requests means idle workers and a number that means nothing.
    [InlineData("-n", "4", "-c", "8")]
    [InlineData("-c", "0")]
    [InlineData("-n", "abc")]
    [InlineData("-n", "0")]
    [InlineData("--duration", "0")]
    [InlineData("--warmup", "-1")]
    [InlineData("--warmup", "x")]
    public void A_contradictory_or_malformed_bench_is_a_usage_error(params string[] flags)
    {
        // example.invalid never resolves, so a usage error has to be decided before anything is sent —
        // and the exit code is the whole contract a CI job depends on.
        var args = new[] { "bench", "https://example.invalid/" }.Concat(flags).ToArray();
        var so = new StringWriter(); var se = new StringWriter();

        int code = CliApp.Run(args, so, se, new MemoryStream(), new CliServices { LiveStatePath = TempState() });

        Assert.Equal(ExitCodes.Usage, code);
    }

    [Fact]
    public async Task A_bench_of_an_endpoint_that_always_fails_still_exits_zero()
    {
        // 503 on every request: the endpoint is answering, so it has been measured — and a bench
        // reports numbers rather than passing judgement, so CI benching an endpoint whose normal
        // answer is 401, 404, or 503 must not be handed a surprising exit 1.
        await using var l = await Loopback.StartAsync(failures: int.MaxValue, failStatus: 503);

        var r = Run(Services(l, TempState()),
            "bench", l.Server.BaseUrl, "--cert", "BenchCliClient", "--insecure", "-n", "8", "-c", "2");

        Assert.Equal(ExitCodes.Ok, r.Code);
        Assert.Contains("8 sent", r.Out);
        Assert.Contains("8 failed", r.Out);
        // The figures still mean something, and the report still carries them.
        Assert.Contains("latency", r.Out);
        Assert.Contains("p50", r.Out);
        Assert.Contains("503 × 8", r.Out);
    }

    [Fact]
    public async Task A_bench_of_an_unreachable_endpoint_exits_one()
    {
        // Port 1 has no listener, so every attempt fails at the transport level and nothing answers:
        // there is no measurement here, only the news that the endpoint could not be reached. This is
        // the one case exit 1 exists for.
        await using var l = await Loopback.StartAsync();

        var r = Run(Services(l, TempState()),
            "bench", "https://127.0.0.1:1/", "--cert", "BenchCliClient", "--insecure", "-n", "4", "-c", "2");

        Assert.Equal(ExitCodes.Failure, r.Code);
        Assert.Contains("errors", r.Out);
    }

    [Fact]
    public async Task The_defaults_run_without_tripping_the_concurrency_rule()
    {
        await using var l = await Loopback.StartAsync();

        // No -n and no -c: the point of this test is that the defaults (100 requests, 10 workers) are
        // mutually consistent — 10 workers over 100 requests must not read as "more workers than
        // requests". A hundred loopback requests is a second or two.
        var r = Run(Services(l, TempState()),
            "bench", l.Server.BaseUrl, "--cert", "BenchCliClient", "--insecure");

        Assert.Equal(ExitCodes.Ok, r.Code);
        Assert.Contains("100 sent", r.Out);
        Assert.Contains("concurrency 10", r.Out);
    }

    [Fact]
    public async Task A_saved_request_can_be_benched_by_path()
    {
        await using var l = await Loopback.StartAsync();
        var state = new AppState();
        var folder = new CollectionNode { Name = "petstore", IsFolder = true };
        folder.Children.Add(new CollectionNode
        {
            Name = "health",
            Request = new RequestModel
            {
                Method = "GET", Path = l.Server.BaseUrl,
                IgnoreServerCert = true, CertThumbprint = l.Client.Thumbprint
            }
        });
        folder.Children.Add(new CollectionNode
        {
            Name = "other",
            Request = new RequestModel { Method = "GET", Path = l.Server.BaseUrl, IgnoreServerCert = true }
        });
        state.Collections.Add(folder);
        var ws = Path.Combine(Path.GetTempPath(), $"certapi-bench-{Guid.NewGuid():N}.json");
        state.SaveTo(ws);
        try
        {
            var one = Run(Services(l, TempState()),
                "bench", "petstore/health", "--workspace", ws, "-n", "4", "-c", "2");
            Assert.Equal(ExitCodes.Ok, one.Code);
            Assert.Contains("4 sent", one.Out);
            Assert.Contains("petstore/health", one.Out);

            // A folder is two endpoints; a bench measures one, so naming the folder is a usage error
            // rather than a silent choice of whichever request came first.
            var folderRun = Run(Services(l, TempState()),
                "bench", "petstore", "--workspace", ws, "-n", "4", "-c", "2");
            Assert.Equal(ExitCodes.Usage, folderRun.Code);
        }
        finally { File.Delete(ws); }
    }

    [Fact]
    public async Task Retries_are_off_during_a_bench_unless_bench_retries_asks_for_them()
    {
        // A server that answers 503 forever: exactly the status --retry retries.
        await using var suppressed = await Loopback.StartAsync(failures: int.MaxValue, failStatus: 503);
        var quiet = Run(Services(suppressed, TempState()),
            "bench", suppressed.Server.BaseUrl, "--cert", "BenchCliClient", "--insecure",
            "-n", "8", "-c", "2", "--retry", "3", "--retry-delay", "1");

        // Eight requests asked for, eight requests sent — not 8 × 4. (The server's counter is written
        // by whichever handler finished last, so under concurrency it can trail the total by one; what
        // this rules out is the retried count.)
        Assert.InRange(suppressed.Server.RequestCount, 7, 8);
        Assert.Contains("8 sent", quiet.Out);
        Assert.Contains("8 failed", quiet.Out);
        // Every request failed, but every request was also answered: the endpoint was measured, so the
        // bench reports the failure rate and exits 0. See the exit-code tests above.
        Assert.Equal(ExitCodes.Ok, quiet.Code);

        await using var retried = await Loopback.StartAsync(failures: int.MaxValue, failStatus: 503);
        var loud = Run(Services(retried, TempState()),
            "bench", retried.Server.BaseUrl, "--cert", "BenchCliClient", "--insecure",
            "-n", "8", "-c", "2", "--retry", "3", "--retry-delay", "1", "--bench-retries");

        Assert.Contains("8 sent", loud.Out);
        Assert.True(retried.Server.RequestCount > 8,
            $"--bench-retries should have retried; the server saw {retried.Server.RequestCount} requests");
    }

    [Fact]
    public async Task A_bench_never_writes_state()
    {
        await using var l = await Loopback.StartAsync();
        string live = TempState();

        var r = Run(Services(l, live),
            "bench", l.Server.BaseUrl, "--cert", "BenchCliClient", "--insecure", "-n", "4", "-c", "2");

        Assert.Equal(ExitCodes.Ok, r.Code);
        Assert.False(File.Exists(live), "a bench is a measurement, not something to remember");
    }

    [Fact]
    public void Help_is_available_and_a_bench_without_a_target_is_a_usage_error()
    {
        var help = Run(new CliServices { LiveStatePath = TempState() }, "help", "bench");
        Assert.Equal(ExitCodes.Ok, help.Code);
        Assert.Contains("certapi bench", help.Out);
        Assert.Contains("--bench-retries", help.Out);

        var bare = Run(new CliServices { LiveStatePath = TempState() }, "bench");
        Assert.Equal(ExitCodes.Usage, bare.Code);
    }
}
