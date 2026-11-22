using System.Diagnostics;
using System.Security.Cryptography.X509Certificates;

namespace ApiTester.Core;

/// <summary>What to measure. <paramref name="Count"/> and <paramref name="Duration"/> are
/// alternatives: with a duration set the count is ignored and the workers run until the deadline.
/// <paramref name="WarmUp"/> requests are sent first and thrown away, so the figures describe a
/// warmed-up endpoint rather than its first-connection cost. <paramref name="TargetRps"/> paces
/// requests to a constant arrival rate across all workers when positive.</summary>
public sealed record BenchOptions(
    int Count,
    int Concurrency,
    TimeSpan? Duration = null,
    TimeSpan? WarmUp = null,
    double? TargetRps = null,
    bool CollectSamples = false);

/// <summary>One measured request exported when <see cref="BenchOptions.CollectSamples"/> is set.</summary>
public sealed record BenchSample(double ElapsedMs, int? StatusCode, string? Error, bool Succeeded);

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
    IReadOnlyDictionary<string, int> ErrorCounts,
    IReadOnlyList<BenchSample>? Samples = null)
{
    /// <summary>Responses that came back with <c>Connection: close</c>: each one ended its
    /// connection, so the request after it paid a new connect (and TLS handshake) that pooling would
    /// otherwise have saved. Zero is the normal case.</summary>
    public int ClosedByServer { get; init; }
}

/// <summary>A latency/error/throughput SLO threshold checked against a <see cref="BenchResult"/>
/// (e.g. <c>p99 &lt; 250ms</c>, <c>error-rate &lt;= 1%</c>, <c>rps &gt;= 50</c>).</summary>
public sealed record BenchThreshold(string Raw, string Metric, string Op, double Limit)
{
    public static BenchThreshold Parse(string spec)
    {
        if (string.IsNullOrWhiteSpace(spec))
            throw new FormatException("Threshold cannot be empty. Examples: 'p99 < 250ms', 'error-rate <= 1%', 'rps >= 50'.");
        var parts = spec.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3)
            throw new FormatException($"Threshold must be '<metric> <op> <value>', got '{spec}'.");
        string metric = parts[0].ToLowerInvariant();
        string op = parts[1];
        string rawVal = parts[2].Trim();

        if (metric is not ("min" or "p50" or "p90" or "p99" or "max" or "rps" or "error-rate"))
            throw new FormatException($"Unknown threshold metric '{parts[0]}'. Use min, p50, p90, p99, max, rps, or error-rate.");
        if (op is not ("<" or "<=" or ">" or ">="))
            throw new FormatException($"Unknown threshold operator '{op}'. Use <, <=, >, or >=.");

        double multiplier = 1.0;
        if (metric == "error-rate" && rawVal.EndsWith('%'))
        {
            rawVal = rawVal[..^1];
            multiplier = 0.01;
        }
        else if (metric is "min" or "p50" or "p90" or "p99" or "max")
        {
            if (rawVal.EndsWith("ms", StringComparison.OrdinalIgnoreCase)) rawVal = rawVal[..^2];
            else if (rawVal.EndsWith("s", StringComparison.OrdinalIgnoreCase)) { rawVal = rawVal[..^1]; multiplier = 1000.0; }
        }

        if (!double.TryParse(rawVal, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double v) || v < 0)
            throw new FormatException($"Invalid threshold value '{parts[2]}' in '{spec}'.");

        return new BenchThreshold(spec.Trim(), metric, op, v * multiplier);
    }

    public (bool Passed, double Actual) Evaluate(BenchResult r)
    {
        double actual = Metric switch
        {
            "min" => r.MinMs,
            "p50" => r.P50Ms,
            "p90" => r.P90Ms,
            "p99" => r.P99Ms,
            "max" => r.MaxMs,
            "rps" => r.RequestsPerSecond,
            "error-rate" => r.Sent == 0 ? 0.0 : (double)r.Failed / r.Sent,
            _ => 0
        };
        bool ok = Op switch
        {
            "<" => actual < Limit,
            "<=" => actual <= Limit,
            ">" => actual > Limit,
            ">=" => actual >= Limit,
            _ => false
        };
        return (ok, actual);
    }
}

/// <summary>Builds a compact ASCII latency histogram from measured samples.</summary>
public static class BenchHistogram
{
    public static string Format(IReadOnlyList<BenchSample> samples, int buckets = 10, int barWidth = 30)
    {
        if (samples.Count == 0) return "";
        double min = double.MaxValue, max = double.MinValue;
        foreach (var s in samples)
        {
            if (s.ElapsedMs < min) min = s.ElapsedMs;
            if (s.ElapsedMs > max) max = s.ElapsedMs;
        }
        buckets = Math.Clamp(buckets, 1, 20);
        var counts = new int[buckets];
        double span = max - min;
        if (span <= 0)
        {
            counts[0] = samples.Count;
        }
        else
        {
            foreach (var s in samples)
            {
                int idx = (int)Math.Floor((s.ElapsedMs - min) / span * buckets);
                if (idx >= buckets) idx = buckets - 1;
                counts[idx]++;
            }
        }
        int peak = counts.Max();
        var sb = new System.Text.StringBuilder();
        double step = span <= 0 ? 0 : span / buckets;
        for (int i = 0; i < buckets; i++)
        {
            double lo = min + i * step;
            double hi = i == buckets - 1 ? max : min + (i + 1) * step;
            int width = peak == 0 ? 0 : (int)Math.Round((double)counts[i] / peak * barWidth);
            sb.AppendFormat(System.Globalization.CultureInfo.InvariantCulture,
                "  {0,7:F1} - {1,7:F1} ms | {2,-30} ({3})\n",
                lo, hi, new string('#', width), counts[i]);
        }
        return sb.ToString().TrimEnd();
    }
}

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
    /// discard. Requests through a proxy are pooled the same way: the CONNECT tunnel and its handshake
    /// are made once and reused.</para>
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
        var benchRequest = request with { DiscardBody = true };

        async Task<Sample?> SendOnceAsync()
        {
            long started = Stopwatch.GetTimestamp();
            try
            {
                var response = await client.SendAsync(benchRequest, certificate, transport,
                    trustServerCertificate, cookies: null, cancellationToken: ct);
                // A send this bench itself aborted measured the interruption, not the endpoint.
                if (ct.IsCancellationRequested && response.Error is { Kind: ApiErrorKind.None }) return null;
                // A 3xx that was not followed is the endpoint answering, not failing.
                bool ok = response.Error is null && response.StatusCode is >= 200 and < 400;
                bool closed = false;
                foreach (var h in response.Headers)
                    if (h.Key.Equals("Connection", StringComparison.OrdinalIgnoreCase) &&
                        h.Value.Contains("close", StringComparison.OrdinalIgnoreCase)) { closed = true; break; }
                return new Sample(response.Elapsed.TotalMilliseconds, response.StatusCode,
                                  response.Error?.Kind.ToString(), ok, closed);
            }
            catch (OperationCanceledException) { return null; }
            catch (Exception ex)
            {
                return new Sample(Stopwatch.GetElapsedTime(started).TotalMilliseconds, null, ex.GetType().Name, false, false);
            }
        }

        if (options.WarmUp is { } warmUp && warmUp > TimeSpan.Zero)
        {
            var warmClock = Stopwatch.StartNew();
            var warming = new Task[concurrency];
            for (int w = 0; w < concurrency; w++)
                warming[w] = Task.Run(async () =>
                {
                    while (!ct.IsCancellationRequested && warmClock.Elapsed < warmUp)
                        await SendOnceAsync();
                }, CancellationToken.None);
            await Task.WhenAll(warming);
        }

        var samples = new List<Sample>[concurrency];
        int claimed = 0, completed = 0;
        double? targetRps = options.TargetRps is { } rps && rps > 0 ? rps : null;
        long pacedSlot = 0;
        var clock = Stopwatch.StartNew();
        var workers = new Task[concurrency];
        int initialCap = duration is null ? Math.Max(4, (total / concurrency) + 4) : 256;
        for (int w = 0; w < concurrency; w++)
        {
            int index = w;
            samples[index] = new List<Sample>(initialCap);
            workers[index] = Task.Run(async () =>
            {
                var mine = samples[index];
                while (!ct.IsCancellationRequested)
                {
                    if (duration is { } deadline) { if (clock.Elapsed >= deadline) return; }
                    else if (Interlocked.Increment(ref claimed) > total) return;

                    if (targetRps is { } rate)
                    {
                        long slot = Interlocked.Increment(ref pacedSlot) - 1;
                        var scheduled = TimeSpan.FromSeconds(slot / rate);
                        var wait = scheduled - clock.Elapsed;
                        if (wait > TimeSpan.Zero)
                        {
                            if (duration is { } d2 && scheduled >= d2) return;
                            try { await Task.Delay(wait, ct); }
                            catch (OperationCanceledException) { return; }
                        }
                    }

                    var sample = await SendOnceAsync();
                    if (sample is null) return;
                    mine.Add(sample.Value);
                    int done = Interlocked.Increment(ref completed);
                    Report(progress, done, total, clock.Elapsed);
                }
            }, CancellationToken.None);
        }
        await Task.WhenAll(workers);
        clock.Stop();

        return Summarize(samples, clock.Elapsed, options.CollectSamples);
    }

    private readonly record struct Sample(double Ms, int? Status, string? ErrorKind, bool Succeeded, bool Closed);

    private static BenchResult Summarize(List<Sample>[] perWorker, TimeSpan elapsed, bool collectSamples)
    {
        int sent = 0;
        foreach (var list in perWorker) sent += list.Count;
        if (sent == 0) return Empty() with { Elapsed = elapsed, Samples = collectSamples ? Array.Empty<BenchSample>() : null };

        var sorted = new double[sent];
        var exported = collectSamples ? new List<BenchSample>(sent) : null;
        int pos = 0, succeeded = 0, closedCount = 0;
        var statusRaw = new Dictionary<int, int>();
        var errorRaw = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var list in perWorker)
        {
            foreach (var s in list)
            {
                sorted[pos++] = s.Ms;
                exported?.Add(new BenchSample(s.Ms, s.Status, s.ErrorKind, s.Succeeded));
                if (s.Succeeded) succeeded++;
                if (s.Closed) closedCount++;
                if (s.Status is { } st)
                    statusRaw[st] = statusRaw.TryGetValue(st, out int c) ? c + 1 : 1;
                if (s.ErrorKind is { } err)
                    errorRaw[err] = errorRaw.TryGetValue(err, out int c) ? c + 1 : 1;
            }
        }
        Array.Sort(sorted);

        var statusCounts = statusRaw.Count <= 1
            ? statusRaw
            : statusRaw.OrderBy(kv => kv.Key).ToDictionary(kv => kv.Key, kv => kv.Value);
        var errorCounts = errorRaw.Count <= 1
            ? errorRaw
            : errorRaw.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal)
                      .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);

        return new BenchResult(
            Sent: sent,
            Succeeded: succeeded,
            Failed: sent - succeeded,
            Elapsed: elapsed,
            RequestsPerSecond: elapsed.TotalSeconds > 0 ? sent / elapsed.TotalSeconds : 0,
            MinMs: sorted[0],
            P50Ms: Percentile(sorted, 50),
            P90Ms: Percentile(sorted, 90),
            P99Ms: Percentile(sorted, 99),
            MaxMs: sorted[^1],
            StatusCounts: statusCounts,
            ErrorCounts: errorCounts,
            Samples: exported)
        {
            ClosedByServer = closedCount
        };
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
