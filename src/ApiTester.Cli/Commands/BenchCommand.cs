using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using ApiTester.Core;

namespace ApiTester.Cli.Commands;

public static class BenchCommand
{
    public const string Help = """
        Usage: certapi bench <url> [options]
               certapi bench <Collection[/Folder]/Request> [options]

        Sends one request over and over and reports how long it took: how many succeeded, the
        rate, and the latency distribution (min/p50/p90/p99/max). Uses the same
        client-certificate send path as `certapi send`, so what it measures is what the rest of
        this tool actually does. A saved-request positional must name exactly one request — a
        bench measures one endpoint.

        Load:
          -n, --count <n>         Total requests to send (default 100)
          -c, --concurrency <n>   Parallel workers (default 10; never more than --count)
          --duration <seconds>    Run for a wall-clock period instead of a fixed count; -n is
                                  then unused, and the two cannot be combined
          --warmup <seconds>      Send for this long first and discard every result, so the
                                  figures describe a warmed-up endpoint. Warm-up requests are
                                  extra — they do not come out of --count.
          --bench-retries         Let the retry flags below apply during the bench (off by default)
          --pool                  Also report the connections this run actually used: how many
                                  were opened and how many requests each served. Turns the note
                                  below into a measurement — a server answering 'Connection:
                                  close' makes every request pay a fresh handshake

        Request:
          -X, --method <m>        HTTP method (default GET)
          -H, --header "k: v"     Add a header (repeatable)
          -d, --data <body>       Request body; -d @file reads it from a file (sent byte for byte
                                  when it is not UTF-8 text)
          --content-type <ct>     Body content type (default application/json)
          --bearer <token>        Authorization: Bearer …
          --timeout <seconds>     Per-request timeout (default 100)

        On a saved request these override or add to what it already carries; everything else
        about it (its own auth, headers, body, and transport settings) is used as saved. A
        multipart saved request cannot be benched.

        TLS / certificates:

        """ + CliCert.HelpLines + """

          --insecure              Ignore server certificate errors (a certificate pinned with
                                  `certapi trust` is honored without it)

        Variables:
          --env <name>            Environment ({{var}} values) from your workspace
          --var k=v               Override/add a variable (repeatable)
          --workspace <file>      Read environments and saved requests from a workspace file
                                  instead of the live state


        """ + TransportFlags.Help + """


        Retries are forced off during a bench even when --retry or the saved request asks for
        them: a retry turns a failure into a slow success and hides the failure rate the bench
        exists to measure. --bench-retries measures it anyway.

        Output:
          --json                  A JSON envelope instead of the summary table

        What the numbers include:
          Connections are pooled and reused, so only the first request to an origin pays the TCP
          connect and TLS handshake; later requests measure the request and response alone. Use
          --warmup to discard that first-connection cost so the figures describe a warmed-up
          endpoint instead of "how long the first request takes, from cold". A request routed
          through a proxy still opens its own connection every time, because the proxied path
          cannot be pooled — see --proxy above.

        A bench never writes anything: no known-good results, no captured tokens, no state
        file. The workspace is read for {{variables}}, saved requests, and pinned certificates
        only. Captured session tokens are not attached either — a bench sends exactly the
        request you named.

        Global: --debug (verbose diagnostics) and --log-file <path> work here too.

        Examples:
          # 500 requests, 20 at a time, against an mTLS endpoint
          certapi bench https://api.example.com/health --cert "CN=My Client" -n 500 -c 20

          # Run for 30 seconds instead, discarding the first 5 seconds as warm-up
          certapi bench https://api.example.com/health --cert "CN=My Client" --duration 30 --warmup 5

          # Bench an authenticated POST
          certapi bench https://api.example.com/orders --cert "CN=My Client" -X POST \
              -d '{"sku":"A1"}' --bearer $env:TOKEN -n 200 -c 10

          # Bench a saved request (its own auth, headers, body, and transport settings)
          certapi bench "petstore/Get pet by id" -n 200 -c 10

          # Machine-readable, for a CI job that tracks latency over time
          certapi bench https://api.example.com/health --cert "CN=My Client" -n 200 --json

        Exit codes: 0 whenever the bench measured something — it reports numbers rather than
        passing judgement, so an endpoint that answers 503 or 404 every time still exits 0 · 1 only
        when no request got a response at all (the endpoint was unreachable, so there is nothing to
        report but that) · 2 usage · 3 data error.
        """;

    /// <summary>The one thing a reader of these numbers must not be misled about, in one line, in
    /// both output forms. See <see cref="Bench.RunAsync"/> for what pooling does and does not cover.</summary>
    private const string ConnectionCaveat =
        "connections are pooled and reused, so only the first request to an origin pays the TCP " +
        "connect and TLS handshake.";

    public static int Run(Args args, TextWriter stdout, TextWriter stderr, CliServices services)
    {
        // ---- bind options ----
        // Every option is consumed before Positionals(), which rejects anything option-shaped left over.
        bool pool = args.Flag("--pool");
        string? countRaw = args.Value("-n", "--count");
        string? concurrencyRaw = args.Value("-c", "--concurrency");
        string? durationRaw = args.Value("--duration");
        string? warmupRaw = args.Value("--warmup");
        bool json = args.Flag("--json");
        bool benchRetries = args.Flag("--bench-retries");
        string store = args.Value("--store") ?? services.Profile?.Store ?? "CurrentUser";
        bool insecure = args.Switch("--insecure", services.Profile?.Insecure);
        string? envName = args.Value("--env");
        var varOverrides = args.Values("--var");
        string? workspace = args.Value("--workspace");
        string? methodOpt = args.Value("-X", "--method");
        MethodOption.Require(methodOpt, "-X/--method");
        var headers = args.Values("-H", "--header");
        string? data = args.Value("-d", "--data");
        // -d @file reads a file, as on send; a file that is not text goes out byte for byte.
        var dataSource = BodyFile.FromDataOption(data);
        string? contentType = args.Value("--content-type");
        string? bearer = args.Value("--bearer");
        string? timeoutRaw = args.Value("--timeout");
        var transportOverrides = TransportFlags.Parse(args, out _, environment: null, services.Profile);
        // Resolve the certificate here so its options are consumed before Positionals() sees them.
        var cert = CliCert.Resolve(args, store, services, stderr);

        var positionals = args.Positionals();
        if (positionals.Count != 1) throw new CliUsageException(Help);

        // ---- how much to send ----
        // Refused up front, before the workspace is even read: a contradictory command line is the
        // user's to fix, and nothing should go over the wire while it stands.
        if (countRaw is not null && durationRaw is not null)
            throw new CliUsageException(
                "-n/--count and --duration are alternatives: a bench runs either a fixed number of " +
                "requests or for a period, not both.");
        int count = ParsePositive(countRaw, 100, "-n/--count", "requests");
        int concurrency = ParsePositive(concurrencyRaw, 10, "-c/--concurrency", "workers");
        var duration = ParsePositiveSeconds(durationRaw, "--duration");
        var warmUp = ParsePositiveSeconds(warmupRaw, "--warmup");
        int timeout = ParsePositive(timeoutRaw, 100, "--timeout", "seconds");
        // Checked against the effective count (the default included), and only when there is a count:
        // with --duration there is nothing for the workers to outnumber.
        if (duration is null && concurrency > count)
            throw new CliUsageException(
                $"-c/--concurrency {concurrency} is more than the {count} request(s) -n/--count asks " +
                "for: the extra workers would sit idle and the rate would mean nothing.");

        // ---- workspace (read-only) ----
        // Loaded for variables, saved requests, and pinned certificates. Never saved back: a bench is
        // a measurement, not an observation worth keeping.
        var state = CliWorkspace.Load(workspace, services.LiveStatePath, stderr);
        var vars = CliWorkspace.BuildVars(state, envName, varOverrides);
        var unresolved = new List<string>();
        string R(string s)
        {
            var (resolved, missing) = VariableResolver.Resolve(s ?? "", vars);
            foreach (var m in missing) if (!unresolved.Contains(m)) unresolved.Add(m);
            return resolved;
        }

        // Resolved before the URL test below, so "https://{{host}}/health --env Staging" is recognised
        // as the URL it becomes rather than mistaken for a saved-request path.
        string target = R(positionals[0]);

        var headerPairs = new List<KeyValuePair<string, string>>();
        foreach (var raw in headers)
        {
            int colon = raw.IndexOf(':');
            if (colon <= 0) throw new CliUsageException($"Header must be \"Name: value\", got '{raw}'.");
            headerPairs.Add(new(R(raw[..colon].Trim()), R(raw[(colon + 1)..].Trim())));
        }
        if (bearer is not null) headerPairs.Add(new("Authorization", "Bearer " + R(bearer)));
        // A Content-Type typed with -H describes the body, as it does on send (and in curl).
        if (contentType is null && headerPairs.LastOrDefault(h => h.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)) is { Key: not null } typedType)
        {
            contentType = typedType.Value;
            headerPairs.RemoveAll(h => h.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase));
        }

        ApiRequest request;
        TransportOptions baseline;
        string? savedPath = null;

        if (Uri.TryCreate(target, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
        {
            string? body = dataSource?.Text is { } text ? R(text) : null;
            request = new ApiRequest
            {
                Method = new HttpMethod((methodOpt ?? "GET").ToUpperInvariant()),
                Url = target,
                Headers = headerPairs,
                Body = body,
                BodyBytes = dataSource?.Bytes,
                ContentType = body is not null ? (contentType ?? "application/json")
                            : dataSource?.Bytes is not null ? (contentType ?? "application/octet-stream") : null,
                Timeout = TimeSpan.FromSeconds(timeout)
            };
            baseline = new TransportOptions { IgnoreServerCertificateErrors = insecure };
        }
        else
        {
            var targets = CliWorkspace.ResolveTargets(state, target, all: false);
            // A folder resolves to everything beneath it; a bench measures one endpoint, so choosing
            // one of them silently would invent a decision the user never made.
            if (targets.Count != 1)
                throw new CliUsageException(
                    $"'{target}' names {targets.Count} saved requests; a bench measures one endpoint, " +
                    "so name a single request (e.g. \"petstore/Get pet by id\").");
            var (path, node) = targets[0];
            var model = node.Request!;
            savedPath = path;
            if (model.IsMultipart)
                throw new CliUsageException(
                    $"'{path}' is a multipart/form-data request, which certapi bench does not send " +
                    "(re-sending file parts thousands of times measures the disk, not the endpoint).");

            // The saved request's own headers and auth, as `run` builds them: enabled rows only.
            var savedHeaders = new List<KeyValuePair<string, string>>();
            foreach (var h in model.Headers)
                if (h.Enabled && !string.IsNullOrWhiteSpace(h.Name))
                    savedHeaders.Add(new(R(h.Name.Trim()), R(h.Value ?? "")));
            switch (model.AuthType)
            {
                case "Bearer" when !string.IsNullOrWhiteSpace(model.AuthSecret):
                    savedHeaders.Add(new("Authorization", "Bearer " + R(model.AuthSecret!.Trim())));
                    break;
                case "Basic":
                    savedHeaders.Add(new("Authorization", "Basic " + Convert.ToBase64String(
                        Encoding.UTF8.GetBytes($"{R(model.AuthUser ?? "")}:{R(model.AuthSecret ?? "")}"))));
                    break;
            }
            // Anything named on the command line is added on top, so a bench can carry an extra header
            // or a fresh token without editing the saved request.
            savedHeaders.AddRange(headerPairs);

            string? savedBody = dataSource is not null ? (dataSource.Text is { } text ? R(text) : null)
                : string.IsNullOrEmpty(model.Body) ? null : R(model.Body!);
            request = new ApiRequest
            {
                Method = new HttpMethod((methodOpt ?? model.Method).ToUpperInvariant()),
                Url = model.EffectiveUrl(R),
                Headers = savedHeaders,
                Body = savedBody,
                BodyBytes = dataSource?.Bytes,
                ContentType = dataSource?.Bytes is not null ? (contentType ?? "application/octet-stream")
                    : savedBody is null ? null
                    : contentType ?? (model.ContentType == "(none)" ? null : model.ContentType),
                WindowsAuth = model.AuthType == "Windows"
                    ? WindowsAuthOptions.FromCredentials(R(model.AuthUser ?? ""), R(model.AuthSecret ?? ""))
                    : null,
                Timeout = TimeSpan.FromSeconds(timeoutRaw is null ? model.TimeoutSeconds : timeout)
            };
            // A saved request keeps its own transport settings as the baseline — the same rule `run`
            // follows — so an endpoint that needs a proxy or a pinned HTTP version is still reachable.
            baseline = model.Transport.ToOptions(model.IgnoreServerCert || insecure);
            if (cert is null && !string.IsNullOrEmpty(model.CertThumbprint))
                cert = services.FindCertificate(model.CertThumbprint!)
                    ?? throw new CliDataException(
                        $"The certificate saved on '{path}' ({model.CertThumbprint}) is not in the store.");
        }

        if (unresolved.Count > 0)
            stderr.WriteLine("warning: unresolved variables: " +
                             string.Join(", ", unresolved.Select(u => "{{" + u + "}}")));

        // ---- transport ----
        var transport = transportOverrides.ApplyTo(baseline);
        // A retry hides the very failure rate a bench exists to measure, so it is off here even when the
        // saved request or a --retry flag asked for it. --bench-retries says "I know, measure it anyway".
        if (!benchRetries) transport = transport with { Retries = 0 };
        if (ApiClient.ValidateTransport(transport, request.Url) is { } transportProblem)
            throw new CliUsageException(transportProblem);

        // A thumbprint pinned for this host lets the bench through without the blanket --insecure
        // bypass — the same courtesy `certapi send` extends.
        string host = TokenService.HostOf(request.Url);
        var options = new BenchOptions(count, concurrency, duration, warmUp);

        // The note this command has always printed — "connections are pooled and reused" — is a
        // claim about what happened. --pool measures it rather than asserting it, which matters
        // because a server sending 'Connection: close' silently turns every request into a fresh
        // handshake, and that dominates the very latency this command exists to report.
        using var inspector = pool ? new ConnectionInspector() : null;

        services.Log.Debug($"bench {request.Method} {MarkdownSecrets.RedactUrl(request.Url)}"
            + (savedPath is null ? "" : $" ({savedPath})"));
        services.Log.Debug(duration is { } d
            ? $"plan: {d.TotalSeconds:0.##} s · concurrency {concurrency}"
            : $"plan: {count} requests · concurrency {concurrency}");
        services.Log.Debug($"retries: {(benchRetries ? $"on ({transport.Retries})" : "off (bench)")}"
            + (warmUp is { } w ? $" · warm-up {w.TotalSeconds:0.##} s" : ""));
        services.Log.Debug(cert is null ? "certificate: none" : $"certificate: {cert.Subject} ({cert.Thumbprint})");

        var result = Bench.RunAsync(request, cert, transport, options,
            Counter(json, stderr), services.Cancel,
            trustServerCertificate: c => c is not null && TrustService.IsTrusted(state, host, c, TrustContext.CurrentChain))
            .GetAwaiter().GetResult();
        if (!json) stderr.WriteLine();

        // Narrowed to the origin benched: the listener is process-wide, and this run had one target.
        var pooled = inspector is null
            ? Array.Empty<ConnectionRecord>()
            : Uri.TryCreate(request.Url, UriKind.Absolute, out var benched)
                ? inspector.Connections.Where(c => c.Origin == ConnectionInspector.OriginOf(benched)).ToArray()
                : inspector.Connections.ToArray();

        // --json promises ONE machine-readable document on stdout. Appending the human-readable
        // pool report after it — which is what this did when --pool and --json were combined —
        // makes the whole thing unparseable, so the same facts go INSIDE the envelope instead.
        if (json) stdout.WriteLine(BuildEnvelope(result, inspector is null ? null : pooled));
        else WriteReport(result, request, savedPath, options, stdout);

        if (inspector is not null && !json)
        {
            stdout.WriteLine();
            stdout.Write(ConnectionInspector.Render(pooled));
        }

        // A bench reports numbers rather than passing judgement: an endpoint that answers 503 every time
        // has still been measured, and its latencies still mean something, so a high failure rate exits 0.
        // Exit 1 only when nothing answered at all — every attempt failed at the transport level, so there
        // is no measurement here, only the news that the endpoint could not be reached.
        bool anythingAnswered = result.StatusCounts.Count > 0;
        return result.Sent > 0 && anythingAnswered ? ExitCodes.Ok : ExitCodes.Failure;
    }

    /// <summary>A progress counter on stderr, so a long bench is visibly alive. Suppressed under
    /// --json, where the envelope on stdout is the whole output and a half-written counter line in a
    /// CI log is noise. Throttled by time rather than by count — a bench can complete thousands of
    /// requests a second, and one line per ten of them would cost more than it reports. Serialized
    /// because workers report from several threads at once.</summary>
    private static IProgress<BenchProgress>? Counter(bool json, TextWriter stderr)
    {
        if (json) return null;
        var gate = new object();
        var lastWrite = System.Diagnostics.Stopwatch.StartNew();
        bool written = false;
        return new Progress<BenchProgress>(p =>
        {
            lock (gate)
            {
                bool last = p.Total > 0 && p.Completed == p.Total;
                if (!last && written && lastWrite.ElapsedMilliseconds < 200) return;
                lastWrite.Restart();
                written = true;
                stderr.Write(p.Total > 0
                    ? $"\r  sending {p.Completed}/{p.Total}…"
                    : $"\r  sending {p.Completed} ({p.Elapsed.TotalSeconds:0.#} s)…");
                stderr.Flush();
            }
        });
    }

    private static void WriteReport(
        BenchResult result, ApiRequest request, string? savedPath, BenchOptions options, TextWriter stdout)
    {
        stdout.WriteLine($"bench {request.Method} {request.Url}" + (savedPath is null ? "" : $"  ({savedPath})"));
        string plan = options.Duration is { } d
            ? $"{d.TotalSeconds:0.##} s · concurrency {options.Concurrency}"
            : $"{options.Count} requests · concurrency {options.Concurrency}";
        if (options.WarmUp is { } w) plan += $" · warm-up {w.TotalSeconds:0.##} s (discarded)";
        stdout.WriteLine($"  plan       {plan}");
        stdout.WriteLine($"  requests   {result.Sent} sent · {result.Succeeded} succeeded · {result.Failed} failed");
        stdout.WriteLine($"  elapsed    {result.Elapsed.TotalSeconds:0.00} s · {result.RequestsPerSecond:0.0} req/s");
        stdout.WriteLine(
            $"  latency    min {result.MinMs:0.0} ms · p50 {result.P50Ms:0.0} ms · p90 {result.P90Ms:0.0} ms · " +
            $"p99 {result.P99Ms:0.0} ms · max {result.MaxMs:0.0} ms");
        if (result.StatusCounts.Count > 0)
            stdout.WriteLine("  status     " + string.Join(" · ",
                result.StatusCounts.OrderBy(k => k.Key).Select(k => $"{k.Key} × {k.Value}")));
        if (result.ErrorCounts.Count > 0)
            stdout.WriteLine("  errors     " + string.Join(" · ",
                result.ErrorCounts.OrderByDescending(k => k.Value).ThenBy(k => k.Key, StringComparer.Ordinal)
                    .Select(k => $"{k.Key} × {k.Value}")));
        stdout.WriteLine("  note: " + ConnectionCaveat);
    }

    /// <param name="connections">The connections this run used, or null when --pool was not asked
    /// for. An empty array is meaningfully different from null: it means the flag was given and
    /// nothing new was opened, which is itself the answer.</param>
    private static string BuildEnvelope(BenchResult result, IReadOnlyList<ConnectionRecord>? connections)
    {
        var obj = new Dictionary<string, object?>
        {
            ["sent"] = result.Sent,
            ["succeeded"] = result.Succeeded,
            ["failed"] = result.Failed,
            ["elapsedMs"] = Math.Round(result.Elapsed.TotalMilliseconds),
            ["requestsPerSecond"] = Math.Round(result.RequestsPerSecond, 2),
            ["latencyMs"] = new Dictionary<string, double>
            {
                ["min"] = Math.Round(result.MinMs, 2),
                ["p50"] = Math.Round(result.P50Ms, 2),
                ["p90"] = Math.Round(result.P90Ms, 2),
                ["p99"] = Math.Round(result.P99Ms, 2),
                ["max"] = Math.Round(result.MaxMs, 2)
            },
            ["statusCounts"] = result.StatusCounts
                .OrderBy(k => k.Key)
                .ToDictionary(k => k.Key.ToString(CultureInfo.InvariantCulture), k => k.Value),
            ["errorCounts"] = result.ErrorCounts
                .OrderByDescending(k => k.Value).ThenBy(k => k.Key, StringComparer.Ordinal)
                .ToDictionary(k => k.Key, k => k.Value),
            // Carried in the machine-readable form too: a dashboard that plots these numbers has to be
            // able to label what they include.
            ["notes"] = new[] { ConnectionCaveat }
        };

        if (connections is not null)
        {
            obj["connections"] = connections.Select(c => new Dictionary<string, object?>
            {
                ["id"] = c.Id,
                ["origin"] = c.Origin,
                ["version"] = c.Version,
                ["peer"] = c.RemoteAddress,
                ["openedAtMs"] = Math.Round(c.EstablishedAt.TotalMilliseconds, 1),
                ["requests"] = c.Requests,
            }).ToList();
            obj["reusing"] = connections.Count > 0 && connections.Sum(c => c.Requests) > connections.Count;
        }

        return JsonSerializer.Serialize(obj, new JsonSerializerOptions { WriteIndented = true });
    }

    private static int ParsePositive(string? raw, int fallback, string flag, string unit)
    {
        if (raw is null) return fallback;
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) || n <= 0)
            throw new CliUsageException($"{flag} expects a positive number of {unit}, got '{raw}'.");
        return n;
    }

    /// <summary>Seconds as a positive number, or null when the flag was not given. Fractions are
    /// allowed — a half-second warm-up is a reasonable thing to ask for — but zero and negative are
    /// not: they are typos, and reading them as "no warm-up" would hide the typo.</summary>
    private static TimeSpan? ParsePositiveSeconds(string? raw, string flag)
    {
        if (raw is null) return null;
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) ||
            double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds <= 0)
            throw new CliUsageException($"{flag} expects a positive number of seconds, got '{raw}'.");
        return TimeSpan.FromSeconds(seconds);
    }
}
