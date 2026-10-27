using ApiTester.Core;

namespace ApiTester.Cli;

public static class OutputText
{
    public static string Size(long bytes) =>
        bytes < 1024 ? $"{bytes} B" :
        bytes < 1024 * 1024 ? $"{bytes / 1024.0:F1} KB" :
        $"{bytes / (1024.0 * 1024.0):F1} MB";

    /// <summary>One stderr line: "200 OK · 118 B · 42 ms · 3 attempts · Tls13 · client cert presented".</summary>
    public static string MetaLine(ApiResponse r)
    {
        // A request that exhausted its retries and ended in a transport error still reports the count:
        // "it failed" and "it failed three times" are different facts, and the second is the one that
        // says retry was actually working.
        if (r.Error is not null)
            return $"error [{r.Error.Kind}]: {r.Error.Message}"
                 + (r.Attempts > 1 ? $" ({r.Attempts} attempts)" : "");
        var parts = new List<string>
        {
            $"{r.StatusCode} {r.ReasonPhrase}".Trim(),
            Size(r.Body.LongLength),
            $"{r.Elapsed.TotalMilliseconds:F0} ms"
        };
        // Only when a retry actually happened: "1 attempts" on every response would churn the output
        // of every user who never asked for one, for no information.
        if (r.Attempts > 1) parts.Add($"{r.Attempts} attempts");
        if (r.Connection?.TlsProtocol is { } tls) parts.Add(tls);
        if (r.Connection?.ClientCertificateSent == true) parts.Add("client cert presented");
        return string.Join(" · ", parts);
    }

    /// <summary>The redirect chain for --show-redirects, one line per hop, with the two facts that
    /// matter for a client certificate called out. Empty when nothing was followed.</summary>
    public static string RedirectLines(IReadOnlyList<RedirectHop> hops) => RedirectReport.Lines(hops);
}

/// <summary>An <see cref="IProgress{T}"/> that reports on the caller's thread, at once. A console has
/// no SynchronizationContext, so <see cref="Progress{T}"/> queues each report to the thread pool,
/// and the last "\r probing N/N…" could land after the command's closing line.</summary>
internal sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
