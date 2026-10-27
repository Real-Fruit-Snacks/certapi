using System.Net.Http;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace ApiTester.Core;

/// <summary>The run-wide choices and sinks one request needs: the flags that change how it is sent
/// and recorded, the two sinks shared by a whole run (the cookie jar and the HAR log), and the
/// front end's own seams — where certificates come from, where notes go. Bundled so the per-request
/// path can be shared without a twenty-argument signature.</summary>
public sealed class RequestRunContext
{
    public required AppState State { get; init; }
    public required ApiClient Client { get; init; }
    public required TrustPredicates Predicates { get; init; }
    /// <summary>Resolves a saved request's stored thumbprint to a certificate. Null answers nothing,
    /// which fails that request the way a missing certificate does.</summary>
    public Func<string, X509Certificate2?>? FindCertificate { get; init; }
    /// <summary>The certificate for a request that names no thumbprint of its own. Null — every
    /// existing caller — keeps today's behavior: no thumbprint, no certificate. The MCP server sets
    /// it to the certificate the operator pinned at launch, so a saved request that never chose a
    /// certificate still presents the session's identity.</summary>
    public X509Certificate2? DefaultCertificate { get; init; }
    /// <summary>Refuses a resolved URL before anything touches the network — the problem string to
    /// report, or null to proceed. Null — every existing caller — allows everything. The MCP server
    /// sets it to its host allowlist; checked per request, after variables resolve, so a chain step
    /// whose URL comes from an earlier step's capture is still gated.</summary>
    public Func<string, string?>? AllowUrl { get; init; }
    public bool NoAutoToken { get; init; }
    public bool StrictVars { get; init; }
    public bool Record { get; init; }
    public bool ShowRedirects { get; init; }
    public bool HarIncludeSecrets { get; init; }
    public System.Net.CookieContainer? Cookies { get; init; }
    /// <summary>Applied over each saved request's own transport settings — the command line's flags
    /// override only what they name. Null leaves the saved request's settings alone.</summary>
    public Func<TransportOptions, TransportOptions>? TransportOverride { get; init; }

    /// <summary>Headers the configuration profile adds to every request — applied after the saved
    /// request's own headers, and only for names the request does not set itself.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> StandingHeaders { get; init; } =
        Array.Empty<KeyValuePair<string, string>>();
    public List<HarEntry>? HarEntries { get; init; }
    /// <summary>Per-request notes, already formatted, delivered synchronously at the moment they
    /// happen so a caller writing them to a stream keeps them in the order they occurred.</summary>
    public Action<string>? Note { get; init; }
    /// <summary>Verbose diagnostics, same delivery contract as <see cref="Note"/>.</summary>
    public Action<string>? Debug { get; init; }
}

/// <summary>The variables a run resolves against, and the means of re-reading them from the state a
/// capture just wrote into. That reassignment is what makes a token captured by one request visible
/// to the next, and it is the mechanism the whole chain feature rests on.</summary>
public sealed class RunVariables
{
    private readonly Func<Dictionary<string, string>> _rebuild;
    public RunVariables(Func<Dictionary<string, string>> rebuild)
    {
        _rebuild = rebuild;
        Current = rebuild();
    }
    public Dictionary<string, string> Current { get; private set; }
    public void Rebuild() => Current = _rebuild();
}

/// <summary>What one request of a run did: what was sent, what came back, whether it passed, and
/// what it captured.</summary>
public sealed record RequestOutcome(
    string Label, CollectionNode Node, RequestModel Request, string Url, ApiResponse Response,
    bool Passed, IReadOnlyList<(string Variable, bool Ok, string? Error)> Captures, bool CapturedTokens)
{
    /// <summary>True when capture rules produced any outcome at all, successful or not — the signal
    /// a caller uses to decide the run changed saved state.</summary>
    public bool CapturedValues => Captures.Count > 0;
}

/// <summary>The per-request path shared by a collection run and a chain: send it, capture what it
/// yields, record the result, and report its assertions. Extracted out of <c>certapi run</c> so a
/// chain step cannot drift from a suite entry — the whole point of a chain is that it runs the same
/// way, in a stated order — and so the desktop application's chain runner funnels through the exact
/// same path rather than a second copy of it.</summary>
public static class RequestRunner
{
    /// <summary>One request of a run: send it, capture what it yields, record the result, and report its
    /// assertions. Shared by a collection run and a chain so a step cannot drift from a suite entry —
    /// the whole point of a chain is that it runs the same way, in a stated order.
    /// <para><paramref name="vars"/> rebuilds itself after a successful capture: that reassignment is
    /// what makes a token captured here visible to the next request, and it is the mechanism the whole
    /// chain feature rests on.</para></summary>
    public static async Task<RequestOutcome> RunAsync(
        string label, CollectionNode node, RequestRunContext ctx, RunVariables vars, CancellationToken ct)
    {
        var (response, url) = await ExecuteAsync(label, node.Request!, ctx, vars.Current, ct).ConfigureAwait(false);
        bool tokensCaptured = false;
        if (!ctx.NoAutoToken && response.Error is null &&
            TokenService.Capture(ctx.State, response.FinalUrl(url), response.Body, response.ContentType, response.Headers) is { } captured)
        {
            ctx.Note?.Invoke($"{label}: captured bearer token for {TokenService.HostOf(url)} ({captured.Source})");
            tokensCaptured = true;
        }
        if (ctx.Record) node.RecordResult(response.Error is null ? response.StatusCode : null, DateTime.UtcNow,
                                          KnownGoodSnapshot(response));
        if (node.Request!.Assertions.Any(a => a.Enabled))
            foreach (var ar in AssertionEvaluator.Evaluate(node.Request!.Assertions, response).Where(a => !a.Passed))
                ctx.Note?.Invoke($"{label}: assertion failed — {ar.Description} (got {ar.Actual ?? "∅"})");
        var captures = new List<(string Variable, bool Ok, string? Error)>();
        if (response.Error is null && node.Request!.Captures.Count > 0)
        {
            var outcome = CaptureApplier.Apply(ctx.State, node.Request!.Captures, response.Body, response.ContentType, response.Headers);
            if (outcome.Count > 0)
            {
                captures.AddRange(outcome);
                var okVars = outcome.Where(o => o.Ok).Select(o => o.Variable).ToList();
                if (okVars.Count > 0) ctx.Note?.Invoke($"{label}: captured " + string.Join(", ", okVars));
                foreach (var b in outcome.Where(o => !o.Ok)) ctx.Note?.Invoke($"{label}: capture '{b.Variable}' failed: {b.Error}");
                vars.Rebuild();
            }
        }
        return new RequestOutcome(label, node, node.Request!, url, response,
            AssertionEvaluator.RequestPassed(node.Request!, response), captures, tokensCaptured);
    }

    private static async Task<(ApiResponse Response, string Url)> ExecuteAsync(
        string path, RequestModel m, RequestRunContext ctx, Dictionary<string, string> vars, CancellationToken ct)
    {
        var unresolved = new List<string>();
        string R(string s)
        {
            var (resolved, missing) = VariableResolver.Resolve(s ?? "", vars);
            foreach (var x in missing) if (!unresolved.Contains(x)) unresolved.Add(x);
            return resolved;
        }

        var headers = new List<KeyValuePair<string, string>>();
        foreach (var h in m.Headers)
            if (h.Enabled && !string.IsNullOrWhiteSpace(h.Name))
                headers.Add(new(R(h.Name.Trim()), R(h.Value ?? "")));
        switch (m.AuthType)
        {
            case "Bearer" when !string.IsNullOrWhiteSpace(m.AuthSecret):
                headers.Add(new("Authorization", "Bearer " + R(m.AuthSecret!.Trim())));
                break;
            case "Basic":
                headers.Add(new("Authorization", "Basic " +
                    Convert.ToBase64String(Encoding.UTF8.GetBytes($"{R(m.AuthUser ?? "")}:{R(m.AuthSecret ?? "")}"))));
                break;
        }
        var winAuth = m.AuthType == "Windows"
            ? WindowsAuthOptions.FromCredentials(R(m.AuthUser ?? ""), R(m.AuthSecret ?? ""))
            : null;
        string url = m.EffectiveUrl(R);
        string host = TokenService.HostOf(url);
        string? body = string.IsNullOrEmpty(m.Body) ? null : R(m.Body!);

        // Gated before the auto-token attach below, not just before the send: a captured token
        // must never even be offered to a host the gate refuses.
        if (ctx.AllowUrl?.Invoke(url) is { } refused)
            return (new ApiResponse { Error = new ApiError(ApiErrorKind.Unknown, refused) }, url);

        if (!ctx.NoAutoToken && m.AuthType == "Auto" &&
            TokenService.AutoAttach(ctx.State, url, headers, out _) is { } used)
        {
            ctx.Note?.Invoke($"{path}: using captured token for {TokenService.HostOf(url)}");
            ctx.Debug?.Invoke($"{path}: auto token attached for {used.Origin} ({used.Source})");
        }

        if (unresolved.Count > 0)
        {
            var tokens = string.Join(", ", unresolved.Select(u => "{{" + u + "}}"));
            if (ctx.StrictVars)
                return (new ApiResponse { Error = new ApiError(ApiErrorKind.Unknown, $"unresolved variables: {tokens}") }, url);
            ctx.Note?.Invoke($"warning: unresolved variables: {tokens}");
        }

        X509Certificate2? cert = ctx.DefaultCertificate;
        if (!string.IsNullOrEmpty(m.CertThumbprint))
        {
            cert = ctx.FindCertificate?.Invoke(m.CertThumbprint!);
            if (cert is null)
                return (new ApiResponse { Error = new ApiError(ApiErrorKind.Unknown, $"certificate {m.CertThumbprint} not found in the store") }, url);
        }

        // The saved request's own transport settings are the baseline; a command-line flag overrides
        // only what it names. An unusable combination fails this request the way a missing
        // certificate does — one bad request must not abort the suite.
        var baseline = m.Transport.ToOptions(m.IgnoreServerCert);
        var transport = ctx.TransportOverride is { } apply ? apply(baseline) : baseline;
        if (ApiClient.ValidateTransport(transport, url) is { } transportProblem)
            return (new ApiResponse { Error = new ApiError(ApiErrorKind.Unknown, transportProblem) }, url);

        if (ctx.StandingHeaders.Count > 0)
        {
            var named = new HashSet<string>(headers.Select(h => h.Key), StringComparer.OrdinalIgnoreCase);
            foreach (var h in ctx.StandingHeaders)
                if (named.Add(h.Key)) headers.Add(h);
        }

        var request = new ApiRequest
        {
            Method = new HttpMethod(m.Method),
            Url = url,
            Headers = headers,
            Body = m.IsMultipart ? null : body,
            Parts = m.IsMultipart
                ? m.EnabledParts().Select(p => p with { Name = R(p.Name), Value = p.Value is null ? null : R(p.Value) }).ToList()
                : null,
            ContentType = !m.IsMultipart && body is not null && m.ContentType != "(none)" ? m.ContentType : null,
            WindowsAuth = winAuth,
            Timeout = TimeSpan.FromSeconds(m.TimeoutSeconds)
        };
        // Attach any browser-captured session cookies for this origin, on top of the optional
        // shared --cookies jar (honors --no-auto-token and the workspace's AutoCookies switch).
        var effectiveJar = ctx.Cookies ?? new System.Net.CookieContainer();
        if (!ctx.NoAutoToken) CookieService.SeedContainer(ctx.State, url, effectiveJar);
        // A pinned thumbprint for this host lets the request through even without the request's
        // own IgnoreServerCert bypass (which trusts anything and is untouched above).
        var response = await ctx.Client.SendAsync(request, cert,
            transport: transport,
            trustServerCertificate: ctx.Predicates.For(host),
            cookies: effectiveJar, cancellationToken: ct).ConfigureAwait(false);
        ctx.Debug?.Invoke($"{path}: " + (response.Error is null
            ? $"{response.StatusCode} · {response.Elapsed.TotalMilliseconds:F0} ms"
            : $"[{response.Error.Kind}] {response.Error.Message}"));
        if (response.Error is null && response.Connection?.ServerCertificateThumbprint is { } thumb &&
            TrustService.IsTrusted(ctx.State, host, thumb))
            ctx.Note?.Invoke($"{path}: trusting pinned certificate for {host}");
        ctx.HarEntries?.AddRange(HarWriter.FromExchangeWithRedirects(request, response, ctx.HarIncludeSecrets));
        // Each hop line already starts with two spaces, so the request path prefixes it the way the
        // other per-request notes on stderr do.
        if (ctx.ShowRedirects && response.Redirects.Count > 0)
            foreach (var line in RedirectReport.Lines(response.Redirects).Split('\n'))
                ctx.Note?.Invoke($"{path}:{line}");
        return (response, url);
    }

    /// <summary>The snapshot to keep as this request's known-good baseline, or null when the body is too
    /// large to belong in state.json — a settings file is not a blob store, and a baseline nobody can
    /// load is worse than no baseline. <see cref="CollectionNode.RecordResult"/> already refuses
    /// anything that isn't a 2xx, so that check is not repeated here.</summary>
    private static ResponseSnapshot? KnownGoodSnapshot(ApiResponse response) =>
        response.Error is not null || response.Body.LongLength > MaxKnownGoodBody
            ? null
            : ResponseSnapshot.From(response);

    private const long MaxKnownGoodBody = 1024 * 1024;   // 1 MiB
}
