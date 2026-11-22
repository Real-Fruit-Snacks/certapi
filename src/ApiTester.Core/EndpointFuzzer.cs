using System.Net.Http;

namespace ApiTester.Core;

public enum FuzzOutcome { Found, Redirect, Unauthorized, MethodNotAllowed, NotFound, ServerError, OtherStatus, Error }

/// <summary>Classifies a probe response into a discovery outcome.</summary>
public static class FuzzClassifier
{
    public static FuzzOutcome Classify(ApiResponse r)
    {
        if (r.Error is not null) return FuzzOutcome.Error;
        return r.StatusCode switch
        {
            >= 200 and < 300 => FuzzOutcome.Found,
            >= 300 and < 400 => FuzzOutcome.Redirect,
            401 or 403 => FuzzOutcome.Unauthorized,
            405 => FuzzOutcome.MethodNotAllowed,
            404 => FuzzOutcome.NotFound,
            >= 500 => FuzzOutcome.ServerError,
            _ => FuzzOutcome.OtherStatus
        };
    }

    /// <summary>True when the endpoint probably exists (anything but a 404 or a transport error).</summary>
    public static bool IsDiscovery(FuzzOutcome o) => o is not (FuzzOutcome.NotFound or FuzzOutcome.Error);
}

public sealed record FuzzResult(
    string Method, string Path, string Url, int? StatusCode, string? ReasonPhrase,
    long SizeBytes, TimeSpan Elapsed, FuzzOutcome Outcome, string? Error);

public sealed record FuzzProgress(int Completed, int Total, FuzzResult Last);

public sealed class FuzzPlan
{
    public string BaseUrl { get; set; } = "";
    public IReadOnlyList<EndpointEntry> Entries { get; set; } = Array.Empty<EndpointEntry>();
    public IReadOnlyList<string> Methods { get; set; } = new[] { "GET" };
    public IReadOnlyList<KeyValuePair<string, string>> Headers { get; set; } = Array.Empty<KeyValuePair<string, string>>();
    public int Concurrency { get; set; } = 8;
    public int DelayMs { get; set; }
}

public sealed record FuzzReport(IReadOnlyList<FuzzResult> Results)
{
    public int Total => Results.Count;
    public int Discovered => Results.Count(r => FuzzClassifier.IsDiscovery(r.Outcome));
    public bool AllErrored => Results.Count > 0 && Results.All(r => r.Outcome == FuzzOutcome.Error);
    public IReadOnlyDictionary<FuzzOutcome, int> CountsByOutcome =>
        Results.GroupBy(r => r.Outcome).ToDictionary(g => g.Key, g => g.Count());
}

/// <summary>Drives endpoint-discovery probes with bounded concurrency. Network-agnostic: the
/// caller supplies the <paramref name="send"/> delegate (which owns the client certificate,
/// timeout, and any auto-token handling).</summary>
public static class EndpointFuzzer
{
    public static async Task<FuzzReport> RunAsync(
        FuzzPlan plan,
        Func<ApiRequest, CancellationToken, Task<ApiResponse>> send,
        IProgress<FuzzProgress>? progress,
        CancellationToken ct)
    {
        // Expand entries × methods into an ordered probe list.
        var probes = new List<(string Method, string Path, string Url)>();
        foreach (var entry in plan.Entries)
        {
            var methods = entry.Method is not null ? new[] { entry.Method } : plan.Methods;
            foreach (var method in methods)
            {
                // Combine base + path verbatim so a query string on the entry (e.g. /search?q=1,
                // or a full-URL entry) is probed exactly as written — RequestUrl.Effective would
                // strip the query, which is wrong for discovery where the caller wrote the URL.
                string url = UrlHelper.Combine(plan.BaseUrl, entry.Path);
                probes.Add((method.ToUpperInvariant(), entry.Path, url));
            }
        }

        int concurrency = Math.Clamp(plan.Concurrency, 1, 50);
        int workerCount = Math.Min(concurrency, probes.Count);
        var results = new FuzzResult?[probes.Count];
        int nextIndex = -1;
        int completed = 0;

        var workers = new Task[workerCount];
        for (int w = 0; w < workerCount; w++)
        {
            workers[w] = Task.Run(async () =>
            {
                while (!ct.IsCancellationRequested)
                {
                    int index = Interlocked.Increment(ref nextIndex);
                    if (index >= probes.Count) return;

                    var (method, path, url) = probes[index];
                    try
                    {
                        if (plan.DelayMs > 0) await Task.Delay(plan.DelayMs, ct);
                        var request = new ApiRequest
                        {
                            Method = ParseMethod(method),
                            Url = url,
                            Headers = plan.Headers,
                            Timeout = TimeSpan.FromSeconds(100)
                        };
                        var response = await send(request, ct);
                        var result = new FuzzResult(method, path, url, response.StatusCode, response.ReasonPhrase,
                            response.Body?.LongLength ?? 0, response.Elapsed, FuzzClassifier.Classify(response), response.Error?.Message);
                        results[index] = result;
                        int done = Interlocked.Increment(ref completed);
                        progress?.Report(new FuzzProgress(done, probes.Count, result));
                    }
                    catch (OperationCanceledException) { return; }
                    catch (Exception ex)
                    {
                        // A send that throws (rather than returning ApiResponse.Error) must not fault the batch —
                        // record this probe as an Error outcome and keep going.
                        var result = new FuzzResult(method, path, url, null, null, 0, TimeSpan.Zero, FuzzOutcome.Error, ex.Message);
                        results[index] = result;
                        int done = Interlocked.Increment(ref completed);
                        progress?.Report(new FuzzProgress(done, probes.Count, result));
                    }
                }
            }, CancellationToken.None);
        }

        await Task.WhenAll(workers);
        ct.ThrowIfCancellationRequested();

        var finalResults = new List<FuzzResult>(probes.Count);
        foreach (var r in results)
            if (r is not null) finalResults.Add(r);
        return new FuzzReport(finalResults);
    }

    private static HttpMethod ParseMethod(string method) => method switch
    {
        "GET" => HttpMethod.Get,
        "HEAD" => HttpMethod.Head,
        "POST" => HttpMethod.Post,
        "PUT" => HttpMethod.Put,
        "DELETE" => HttpMethod.Delete,
        "PATCH" => HttpMethod.Patch,
        "OPTIONS" => HttpMethod.Options,
        _ => new HttpMethod(method)
    };
}
