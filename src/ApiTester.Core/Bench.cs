using System.Diagnostics;
using System.Security.Cryptography.X509Certificates;

namespace ApiTester.Core;

/// <summary>What to measure. <paramref name="Count"/> and <paramref name="Duration"/> are
/// alternatives: with a duration set the count is ignored and the workers run until the deadline.
/// <paramref name="WarmUp"/> requests are sent first and thrown away, so the figures describe a
/// warmed-up endpoint rather than its first-connection cost.</summary>
public sealed record BenchOptions(int Count, int Concurrency, TimeSpan? Duration = null, TimeSpan? WarmUp = null);

/// <summary>Progress while a bench runs, for a caller that wants to show a counter.
/// <paramref name="Total"/> is 0 when the run is bounded by a duration rather than a count — there
/// is no total to count towards.</summary>
public sealed record BenchProgress(int Completed, int Total, TimeSpan Elapsed);

/// <summary>What a bench measured. The latency figures are milliseconds; every count describes the
/// measured phase only, never the discarded warm-up.</summary>
public sealed record BenchResult(
    int Sent, int Succeeded, int Failed,
    TimeSpan Elapsed, double RequestsPerSecond,
    double MinMs, double P50Ms, double P90Ms, double P99Ms, double MaxMs,
    IReadOnlyDictionary<int, int> StatusCounts,
    IReadOnlyDictionary<string, int> ErrorCounts);

/// <summary>Latency and load measurement over the ordinary certificate-authenticated send path: the
/// same <see cref="ApiClient"/> a single request uses, so what is measured is what the product
/// actually does rather than a second implementation of it.</summary>
public static class Bench
{
    /// <summary>Send <paramref name="request"/> repeatedly and report the distribution of its
    /// latency. Never writes a file, never touches saved state, and never records a known-good
    /// result: a measurement is not an observation worth keeping.
    /// <para><b>What the latency includes.</b> One <see cref="ApiClient"/> instance is shared by every
    /// worker, and that client now pools connections: the first request to an origin pays the TCP
    /// connect and TLS handshake, and every later request that shares the same client certificate,
    /// trust policy, and other handler-defining settings reuses that connection and measures only the
    /// request and response. So the figures below are dominated by warm, pooled requests unless
    /// <see cref="BenchOptions.WarmUp"/> is left unset and the run is short enough that the first
    /// connection's cost still shows up in the distribution — which is exactly what --warmup exists to
    /// discard. The one path that still pays a full connection every time is a request routed through a
    /// proxy: the CONNECT tunnel is established by the handler itself, not by a callback attributable to
    /// one connection, so proxied requests are never pooled.</para>
    /// <para><b>Retries.</b> Whatever <paramref name="transport"/> says is honored as given. A caller
    /// that wants the failure rate a bench exists to measure should switch retries off before calling,
    /// because a retry converts a failure into a slow success.</para>
    /// <para><b>Degenerate input is not validated here</b> — the command line is where a user's
    /// mistake belongs. A <see cref="BenchOptions.Concurrency"/> below 1 is clamped to 1, and a
    /// non-positive <see cref="BenchOptions.Count"/> with no duration sends nothing and returns an
    /// empty result rather than spinning.</para></summary>
    /// <param name="trustServerCertificate">Optional per-site trust decision, as
    /// <see cref="ApiClient.SendAsync"/> takes: it lets a bench reach an endpoint whose certificate the
    /// user has pinned without the blanket ignore-all-errors bypass. Additive and defaulted, so a
    /// caller with nothing to pin passes nothing.</param>
    public static async Task<BenchResult> RunAsync(
        ApiRequest request,
        X509Certificate2? certificate,
        TransportOptions transport,
        BenchOptions options,
        IProgress<BenchProgress>? progress,
        CancellationToken ct,
        Func<X509Certificate2?, bool>? trustServerCertificate = null)
    {
        int concurrency = Math.Max(1, options.Concurrency);
        var duration = options.Duration is { } d && d > TimeSpan.Zero ? d : (TimeSpan?)null;
        int total = duration is null ? Math.Max(0, options.Count) : 0;
        // Nothing to measure: return the empty report rather than starting workers that would either
        // spin or wait forever on a counter that can never be reached.
        if (duration is null && total == 0) return Empty();

        // One client for the whole run — see the remarks above for what that does and does not buy.
        // Every worker task is awaited (Task.WhenAll below) before this method returns, so disposing
        // it here, once every send it could ever issue has finished, is safe.
        using var client = new ApiClient();

        async Task<Sample?> SendOnceAsync()
        {
            var started = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var response = await client.SendAsync(request, certificate, transport,
                    trustServerCertificate, cookies: null, cancellationToken: ct);
                // A send this bench itself aborted measured the interruption, not the endpoint.
                if (ct.IsCancellationRequested && response.Error is { Kind: ApiErrorKind.None }) return null;
                // A 3xx that was not followed is the endpoint answering, not failing.
                bool ok = response.Error is null && response.StatusCode is >= 200 and < 400;
                return new Sample(response.Elapsed.TotalMilliseconds, response.StatusCode,
                                  response.Error?.Kind.ToString(), ok);
            }
            catch (OperationCanceledException) { return null; }
            catch (Exception ex)
            {
                // A send that throws instead of returning an ApiResponse must not tear down the bench:
                // it is one failed request, named by the exception that caused it.
                // Timed to the throw, so it neither drags the latency figures toward zero nor
                // pretends to be instant.
                return new Sample(started.Elapsed.TotalMilliseconds, null, ex.GetType().Name, false);
            }
        }

        if (options.WarmUp is { } warmUp && warmUp > TimeSpan.Zero)
        {
            // Warm-up requests are extra rather than deducted from Count: -n 20 has to still mean
            // twenty measured requests, or the number the user typed is not the number they get.
            var warmClock = Stopwatch.StartNew();
            var warming = new Task[concurrency];
            for (int w = 0; w < concurrency; w++)
                warming[w] = Task.Run(async () =>
                {
                    while (!ct.IsCancellationRequested && warmClock.Elapsed < warmUp)
                        await SendOnceAsync();          // every result discarded, by design
                }, CancellationToken.None);
            await Task.WhenAll(warming);
        }

        // Per-worker lists, concatenated at the end: no lock on the hot path, and nothing to contend
        // for while the thing being measured is the request.
        var samples = new List<Sample>[concurrency];
        int claimed = 0, completed = 0;
        var clock = Stopwatch.StartNew();
        var workers = new Task[concurrency];
        for (int w = 0; w < concurrency; w++)
        {
            int index = w;
            samples[index] = new List<Sample>();
            workers[index] = Task.Run(async () =>
            {
                var mine = samples[index];
                while (!ct.IsCancellationRequested)
                {
                    if (duration is { } deadline) { if (clock.Elapsed >= deadline) return; }
                    // The shared counter is what makes "exactly Count requests" true however unevenly
                    // the workers happen to be scheduled.
                    else if (Interlocked.Increment(ref claimed) > total) return;

                    var sample = await SendOnceAsync();
                    if (sample is null) return;         // cancelled mid-send
                    mine.Add(sample.Value);
                    int done = Interlocked.Increment(ref completed);
                    Report(progress, done, total, clock.Elapsed);
                }
            }, CancellationToken.None);
        }
        await Task.WhenAll(workers);
        clock.Stop();

        return Summarize(samples.SelectMany(s => s).ToList(), clock.Elapsed);
    }

    /// <summary>One measured request: how long it took, what it answered, and whether that counts as
    /// the endpoint working. A struct because a bench of thousands allocates one of these per request
    /// and none of them outlive the run.</summary>
    private readonly record struct Sample(double Ms, int? Status, string? ErrorKind, bool Succeeded);

    private static BenchResult Summarize(List<Sample> samples, TimeSpan elapsed)
    {
        if (samples.Count == 0) return Empty() with { Elapsed = elapsed };

        var sorted = samples.Select(s => s.Ms).ToArray();
        Array.Sort(sorted);
        int sent = samples.Count;
        int succeeded = samples.Count(s => s.Succeeded);

        return new BenchResult(
            Sent: sent,
            Succeeded: succeeded,
            Failed: sent - succeeded,
            Elapsed: elapsed,
            // Never NaN and never infinity: a report that prints ∞ req/s has told the reader nothing.
            RequestsPerSecond: elapsed.TotalSeconds > 0 ? sent / elapsed.TotalSeconds : 0,
            MinMs: sorted[0],
            P50Ms: Percentile(sorted, 50),
            P90Ms: Percentile(sorted, 90),
            P99Ms: Percentile(sorted, 99),
            MaxMs: sorted[^1],
            StatusCounts: samples.Where(s => s.Status is not null)
                .GroupBy(s => s.Status!.Value).OrderBy(g => g.Key)
                .ToDictionary(g => g.Key, g => g.Count()),
            ErrorCounts: samples.Where(s => s.ErrorKind is not null)
                .GroupBy(s => s.ErrorKind!).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal));
    }

    /// <summary>The nearest-rank percentile of the full retained array. Exact rather than estimated:
    /// a bench counts in thousands, not millions, so there is nothing an approximate histogram would
    /// buy except a wrong answer.</summary>
    private static double Percentile(double[] sorted, double percentile) =>
        sorted.Length == 0
            ? 0
            : sorted[Math.Clamp((int)Math.Ceiling(percentile / 100.0 * sorted.Length) - 1, 0, sorted.Length - 1)];

    private static BenchResult Empty() => new(
        0, 0, 0, TimeSpan.Zero, 0, 0, 0, 0, 0, 0,
        new Dictionary<int, int>(), new Dictionary<string, int>(StringComparer.Ordinal));

    /// <summary>Report progress without ever letting a caller's handler fault a worker — the bench is
    /// the point, and a counter that throws must not cost the run its measurements.</summary>
    private static void Report(IProgress<BenchProgress>? progress, int completed, int total, TimeSpan elapsed)
    {
        if (progress is null) return;
        try { progress.Report(new BenchProgress(completed, total, elapsed)); }
        catch { /* a progress sink's failure is not the bench's problem */ }
    }
}
