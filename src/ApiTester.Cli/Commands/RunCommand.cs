using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using ApiTester.Core;

namespace ApiTester.Cli.Commands;

public static class RunCommand
{
    public const string Help = """
        Usage: certapi run <Collection[/Folder][/Request]> [options]
               certapi run --all [options]
               certapi run --chain <name> [options]
               certapi run <file.har> [--cert <thumb|subject> | --cert-file <path>] [options]

        Runs saved requests. A folder or collection path runs everything beneath it as a
        suite; a request path runs that one request. A request passes when its assertions all
        pass (Status / Time / Header / Body / Body-text checks set on it in the app); a request
        with no assertions passes on any 2xx response. Failed assertions are listed on stderr.

        A <file.har> positional (captured in a browser, or by certapi send/run --har) replays
        its entries in file order as an ordered suite instead — WITH the client certificate you
        name attached, so a session captured in Chrome DevTools replays authenticated. A HAR
        entry carries no assertions, so it passes on any 2xx response. A HAR run never writes to
        saved state (it isn't a saved collection), matching --workspace semantics.

        Options:
          --all                   Run every saved request in the workspace
          --workspace <file>      Load collections from a workspace file (default: live GUI state)
          --env <name>            Environment for {{variables}}; --var k=v overrides (repeatable)
          --data <file>           Data-driven run: repeat the request(s) once per row of a CSV or
                                  JSON file, the row's columns overriding {{variables}}
          --record / --no-record  Write known-good results back (default: on for live state,
                                  off for workspace files; skipped while the GUI is running)
          --strict-vars           Unresolved {{tokens}} fail the request
          --no-auto-token         Don't attach captured session tokens or cookies during this run
          --cookie-jar <file>     Like --cookies, but the jar is loaded from and saved to this file,
                                  so a run's login carries to the next run (or to certapi send)
          --cookies               Keep a cookie jar for the run, so a login's Set-Cookie is sent
                                  on later requests (cookie-based sessions)
          --json                  JSON results instead of the table
          --har <file>            Capture the whole suite (every request, and every redirect hop)
                                  as one HAR file, written once when the run finishes
          --har-include-secrets   Don't redact Authorization/Proxy-Authorization/Cookie/
                                  Set-Cookie values in the captured HAR (redacted by default)
          --md <file>             Also write the run as a markdown note: a pass/fail table, each
                                  failed assertion with what arrived instead, per-request timing,
                                  and totals in frontmatter so a vault can chart a suite's health
                                  over time. A chain's steps link back to their request notes
          --md-vault <folder>     Write that report into a vault instead, as
                                  certapi/runs/<name>-<timestamp>.md — a new note per run, so the
                                  history a trend needs is never overwritten
          --md-include-secrets    Keep credential-looking query values in the report (redacted by
                                  default, because a vault syncs)

        Chains:
          --chain <name>          Run a saved chain: its requests, in the order the chain names them,
                                  as one unit — so a token captured by one step is usable by the next
                                  through its {{variable}}. A chain names its own requests, so it takes
                                  no positional and does not combine with --all or --diff-har; --data
                                  is refused too, because whether a row would repeat the whole chain
                                  or each step within it is not defined.
                                  A failing step stops the chain unless that step is marked to carry
                                  on; the steps that never ran are listed as SKIP and count as neither
                                  passed nor failed. Any failed step exits 1.
                                  Captures write into the environment the chain names (created if it
                                  does not exist yet); an explicit --env wins over it, because a flag
                                  you typed is a more specific instruction than a stored default.

        Diff:
          --diff-har <file.har>   Replay a HAR and compare each response against the one it
                                  recorded — regression-testing an API against a captured
                                  session. Names the archive itself, so it takes no positional
                                  and does not combine with --all. An entry passes only when its
                                  diff is identical (the status is part of the diff), and any
                                  difference exits 1: there is no --diff-fail here, because
                                  diffing the capture is the whole point of the flag.
          --diff-ignore <path>    JSON path to ignore, e.g. data.timestamp (repeatable; a trailing
                                  * on a segment matches by prefix)
          --diff-ignore-header <n>  Header to ignore on top of the volatile defaults (repeatable)

        TLS / certificates (for a <file.har> replay only — a saved request carries its own):
          --cert <thumb|subject>  Client certificate from the Windows store
          --store <location>      CurrentUser (default); LocalMachine searches both stores
          --cert-file <path>      Client certificate from a file (.pfx/.p12 or .pem/.crt) instead
          --cert-password <pw>    Password for a .pfx/.p12 certificate file
          --key-file <path>       Private-key file for a PEM cert whose key is separate
          --cert-issuer <substr>  Narrow --cert to certificates issued by a matching CA (or use alone)
          --cert-serial <hex>     Pick the certificate with this serial number (or use alone)
          --cert-pin <pin>        Smart-card / HSM PIN for the key, supplied ahead of time so a
                                  headless job is not stopped by the prompt. Prefer CERTAPI_CERT_PIN,
                                  which keeps it out of the shell history. Windows (CNG) only


        """ + TransportFlags.Help + """


        A saved request keeps its own transport settings; a flag here overrides only what it
        names, for every request in the run.

        Requests whose Auth is "Auto" attach the captured token for their host; a token
        captured by one request (e.g. a login) is reused by the rest of the suite.

        Global: --debug (verbose diagnostics) and --log-file <path> work here too.

        Examples:
          # Run one request, a folder, or everything
          certapi run "petstore/Get pet by id"
          certapi run petstore/smoke
          certapi run --all

          # A login-first suite: the login response's token carries through the suite
          certapi run "api/login then browse" --env Staging

          # The same thing as a saved chain: step 1 logs in, step 2 uses the token it captured
          certapi run --chain "login then browse"

          # Data-driven: run one request once per row of users.csv (columns become {{variables}})
          certapi run "api/Get user" --data .\users.csv

          # CI: machine-readable results, no writes at all, fail the job on any failure
          certapi run --all --workspace .\suite.json --no-record --no-auto-token --json

          # Investigate a flaky suite with full diagnostics
          certapi run api --debug --log-file suite-debug.log

          # Capture the whole suite as a HAR file for later replay or review
          certapi run --all --har suite.har

          # Replay a browser session's HAR through mutual TLS
          certapi run session.har --cert "CN=Me"

          # Regression-test against that capture: any difference from what it recorded fails
          certapi run --diff-har session.har --cert "CN=Me" --diff-ignore data.generatedAt

        Exit codes: 0 all passed · 1 any failure · 2 usage · 3 data error.
        """;

    public static int Run(Args args, TextWriter stdout, TextWriter stderr, CliServices services)
    {
        bool all = args.Flag("--all");
        string? workspace = args.Value("--workspace") ?? services.Profile?.Workspace;
        string? envName = args.Value("--env");
        var varOverrides = args.Values("--var");
        bool recordFlag = args.Flag("--record");
        bool noRecord = args.Flag("--no-record");
        bool strictVars = args.Flag("--strict-vars");
        bool json = args.Flag("--json");
        bool noAutoToken = args.Flag("--no-auto-token");
        string? dataFile = args.Value("--data");
        string? chainName = args.Value("--chain");
        bool useCookies = args.Flag("--cookies");
        string? cookieJarPath = args.Value("--cookie-jar");
        if (cookieJarPath is not null) useCookies = true;
        var transportOverrides = TransportFlags.Parse(args, out bool showRedirects, environment: null, services.Profile);
        string store = args.Value("--store") ?? services.Profile?.Store ?? "CurrentUser";
        string? harPath = args.Value("--har");
        string? mdFile = args.Value("--md");
        string? mdVault = args.Value("--md-vault");
        bool mdIncludeSecrets = args.Flag("--md-include-secrets");
        if (mdFile is not null && mdVault is not null)
            throw new CliUsageException("--md and --md-vault both name where the report goes; pick one.");
        if (mdIncludeSecrets && mdFile is null && mdVault is null)
            throw new CliUsageException("--md-include-secrets only applies with --md or --md-vault.");
        bool harIncludeSecrets = args.Flag("--har-include-secrets");
        string? diffHar = args.Value("--diff-har");
        var diffIgnorePaths = args.Values("--diff-ignore");
        var diffIgnoreHeaders = args.Values("--diff-ignore-header");
        // Resolved unconditionally so its options are consumed before Positionals() rejects
        // anything option-shaped left over; only the <file.har> replay path below uses it — a
        // normal collection run has no --cert of its own (a saved request carries its own).
        var cert = CliCert.Resolve(args, store, services, stderr);

        var positionals = args.Positionals();
        // --diff-har names the archive itself, so it replaces the positional rather than qualifying
        // one: there is no reading of "replay this capture" that also runs a saved collection.
        if (diffHar is not null && (positionals.Count > 0 || all))
            throw new CliUsageException(
                "--diff-har names the archive to replay, so it cannot be combined with --all or a " +
                "collection/<file.har> positional.");
        if (diffHar is null && (diffIgnorePaths.Count > 0 || diffIgnoreHeaders.Count > 0))
            throw new CliUsageException(
                $"{(diffIgnorePaths.Count > 0 ? "--diff-ignore" : "--diff-ignore-header")} needs --diff-har — " +
                "there is nothing to compare against without it.");
        // A chain names its own requests, in its own order, so there is nothing for a positional or
        // --all to add — and --diff-har names an archive to replay instead of a saved anything.
        if (chainName is not null && (positionals.Count > 0 || all || diffHar is not null))
            throw new CliUsageException(
                "--chain names the chain to run, so it cannot be combined with --all, --diff-har, or a " +
                "collection/<file.har> positional.");
        // Whether a data row would repeat the whole chain or each step within it is not defined, and
        // guessing would invent behaviour a CI job would then depend on.
        if (chainName is not null && dataFile is not null)
            throw new CliUsageException(
                "--chain cannot be combined with --data: a data-driven chain is not defined — whether a " +
                "row repeats the whole chain or each step would be a guess.");
        if (diffHar is null && chainName is null && (positionals.Count > 1 || (positionals.Count == 0 && !all)))
            throw new CliUsageException(Help);
        // --har's directory must exist before the first request, not merely before the write — a
        // typo shouldn't surface after requests already went out over the wire.
        if (harPath is not null)
        {
            var harDir = Path.GetDirectoryName(Path.GetFullPath(harPath));
            if (!Directory.Exists(harDir)) throw new CliUsageException($"--har directory does not exist: {harDir}");
        }

        var state = CliWorkspace.Load(workspace, services.LiveStatePath, stderr);
        // One instance for the whole run: ApiClient's handler cache keys the per-host trust
        // predicate by delegate identity, so a caller that built a fresh lambda per request could
        // never pool a connection with itself. This makes repeated requests to the same host reuse
        // the same delegate instance instead.
        var predicates = new TrustPredicates(state);

        // --diff-har: the same replay, but every response is compared against the one the archive
        // recorded. Named headers are added to the volatile defaults rather than replacing them —
        // a user naming one noisy header has said nothing about wanting Date compared.
        if (diffHar is not null)
        {
            if (!File.Exists(diffHar)) throw new CliDataException($"--diff-har file not found: {diffHar}");
            var diffOptions = new DiffOptions
            {
                IgnorePaths = diffIgnorePaths,
                IgnoreHeaders = diffIgnoreHeaders.Count == 0
                    ? DiffOptions.DefaultIgnoredHeaders
                    : DiffOptions.DefaultIgnoredHeaders.Concat(diffIgnoreHeaders).ToList(),
                CompareHeaderValues = true
            };
            return RunHar(diffHar, cert, state, transportOverrides, showRedirects, json, stdout, stderr, services,
                          predicates, diffOptions);
        }

        // A <file.har> positional (versus a live collection/folder name) replays its entries as an
        // ordered suite, with the selected client certificate attached — never falls through to the
        // saved-collection path below.
        if (positionals.Count == 1 && positionals[0].EndsWith(".har", StringComparison.OrdinalIgnoreCase) &&
            File.Exists(positionals[0]))
            return RunHar(positionals[0], cert, state, transportOverrides, showRedirects, json, stdout, stderr, services, predicates);

        // A chain resolves every step up front — a chain naming a request that no longer exists must
        // fail before it makes a network call, not half-way through with a login already sent.
        RequestChain? chain = null;
        IReadOnlyList<ResolvedChainStep> chainSteps = Array.Empty<ResolvedChainStep>();
        if (chainName is not null)
        {
            try
            {
                chain = ChainRunner.Find(state, chainName);
                chainSteps = ChainRunner.Resolve(state, chain);
            }
            // The runner is UI-free, so it raises its own resolution failure; naming it a data error
            // is this front end's judgement, and is what makes it exit 3.
            catch (ChainRunException ex) { throw new CliDataException(ex.Message); }
        }

        var targets = chain is null
            ? CliWorkspace.ResolveTargets(state, positionals.FirstOrDefault(), all)
            : new List<(string Path, CollectionNode Node)>();

        // Data-driven runs: one iteration per dataset row, its columns overriding the variables.
        IReadOnlyList<IReadOnlyDictionary<string, string>?> rows;
        if (dataFile is null) rows = new IReadOnlyDictionary<string, string>?[] { null };
        else
        {
            try { rows = DataSet.Load(dataFile).Cast<IReadOnlyDictionary<string, string>?>().ToList(); }
            catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException)
            { throw new CliDataException($"Could not read data file '{dataFile}': {ex.Message}"); }
            if (rows.Count == 0) throw new CliDataException($"The data file '{dataFile}' has no rows.");
        }

        Dictionary<string, string> BuildIterVars(IReadOnlyDictionary<string, string>? row)
        {
            var v = CliWorkspace.BuildVars(state, envName, varOverrides);
            if (row is not null) foreach (var kv in row) v[kv.Key] = kv.Value;
            return v;
        }

        // If --env names an existing environment, make it the capture target so a token captured
        // by one request in this run is reusable by later requests via {{var}}.
        if (envName is not null &&
            state.Environments.FirstOrDefault(e => e.Name.Equals(envName, StringComparison.OrdinalIgnoreCase)) is { } namedEnv)
            state.ActiveEnvironmentId = namedEnv.Id;
        // A chain names the environment its captures write into, created on first use, so a token
        // captured by step 1 has somewhere step 2 can read it from. A typed --env is the more specific
        // instruction, so it wins; with neither, the chain gets no opinion it was not given.
        if (envName is null && chain is not null) envName = ChainRunner.PrepareCaptureEnvironment(state, chain);

        bool record = !noRecord && (workspace is null || recordFlag);
        if (record && workspace is null && services.IsGuiRunning())
        {
            record = false;
            stderr.WriteLine("note: the GUI is running — results were not recorded (it would overwrite them on close).");
        }

        // One cookie jar for the whole run, so a login's Set-Cookie carries to later requests.
        var jar = useCookies ? new System.Net.CookieContainer() : null;
        if (cookieJarPath is not null && jar is not null)
        {
            try { CookieJarFile.Load(cookieJarPath, jar); }
            catch (InvalidDataException ex) { throw new CliDataException(ex.Message); }
        }
        bool capturedAny = false;
        bool tokensCaptured = false;
        // A CI job records exactly what it exercised: every request performed appends a HarEntry
        // (including every redirect hop), written once at the end of the run.
        var harEntries = harPath is not null ? new List<HarEntry>() : null;
        var results = new List<(string Path, RequestModel Model, string Url, ApiResponse Response)>();
        // Kept alongside the tuples above so the markdown report can name what each step
        // captured; the tuple shape is load-bearing for the existing text and JSON output.
        var outcomes = new List<RequestOutcome>();
        // Chain steps that never ran because an earlier one failed. Reported rather than dropped: an
        // output that just stops leaves the reader guessing whether the rest passed.
        var skippedSteps = new List<string>();
        var clock = Stopwatch.StartNew();
        var runContext = new RequestRunContext
        {
            State = state,
            Client = services.Client,
            Predicates = predicates,
            FindCertificate = thumbprint => services.FindCertificate(thumbprint),
            NoAutoToken = noAutoToken,
            StrictVars = strictVars,
            Record = record,
            ShowRedirects = showRedirects,
            HarIncludeSecrets = harIncludeSecrets,
            Cookies = jar,
            TransportOverride = options => transportOverrides.ApplyTo(options),
            StandingHeaders = services.Profile?.Headers ?? Array.Empty<KeyValuePair<string, string>>(),
            HarEntries = harEntries,
            Note = line => stderr.WriteLine(line),
            Debug = line => services.Log.Debug(line)
        };

        void Collect(RequestOutcome outcome)
        {
            results.Add((outcome.Label, outcome.Request, outcome.Url, outcome.Response));
            outcomes.Add(outcome);
            capturedAny |= outcome.CapturedValues;
            tokensCaptured |= outcome.CapturedTokens;
        }

        if (chain is not null)
        {
            var vars = new RunVariables(() => BuildIterVars(null));
            // No progress sink: this front end renders after the run, and System.Progress<T> would
            // hand each report to the thread pool, which is exactly wrong for a list read back
            // synchronously three lines later.
            var chainResult = ChainRunner
                .RunAsync(chainSteps, runContext, vars, progress: null, services.Cancel)
                .GetAwaiter().GetResult();
            foreach (var outcome in chainResult.Steps) Collect(outcome);
            skippedSteps.AddRange(chainResult.SkippedLabels);
        }
        else
        {
            int rowIndex = 0;
            foreach (var row in rows)
            {
                rowIndex++;
                var vars = new RunVariables(() => BuildIterVars(row));
                string label = dataFile is null ? "" : $"[row {rowIndex}] ";
                foreach (var (path, node) in targets)
                    Collect(RequestRunner.RunAsync(label + path, node, runContext, vars, services.Cancel)
                                         .GetAwaiter().GetResult());
            }
        }
        clock.Stop();

        bool guiBlocksLiveWrite = workspace is null && services.IsGuiRunning();
        if ((record || capturedAny || tokensCaptured) && !guiBlocksLiveWrite)
        {
            string path = workspace ?? services.LiveStatePath;
            try { CliWorkspace.ReportSaveResult(state.SaveTo(path), path, stderr); }
            catch (Exception ex) { stderr.WriteLine($"warning: could not save results: {ex.Message}"); }
        }
        else if ((capturedAny || tokensCaptured) && guiBlocksLiveWrite)
        {
            stderr.WriteLine("note: the GUI is running — captured values were not saved (it would overwrite them on close).");
        }

        if (cookieJarPath is not null && jar is not null)
        {
            try { CookieJarFile.Save(cookieJarPath, jar); }
            catch (IOException ex) { stderr.WriteLine($"warning: could not write --cookie-jar {cookieJarPath}: {ex.Message}"); }
        }
        if (harPath is not null && harEntries is not null)
        {
            File.WriteAllText(harPath, HarWriter.Write(harEntries, HarCreatorVersion()));
            stderr.WriteLine($"wrote HAR to {harPath} ({harEntries.Count} entr{(harEntries.Count == 1 ? "y" : "ies")})");
        }

        int passed = results.Count(r => Passed(r.Model, r.Response));
        int failed = results.Count - passed;

        if (json)
        {
            stdout.WriteLine(JsonSerializer.Serialize(new
            {
                results = results.Select(r => new
                {
                    path = r.Path,
                    method = r.Model.Method,
                    // The URL actually sent (resolver already applied), not recomputed from the model —
                    // recomputing here would report "%7B%7Btok%7D%7D" for a request that actually sent
                    // the resolved value.
                    url = r.Url,
                    status = r.Response.StatusCode,
                    elapsedMs = Math.Round(r.Response.Elapsed.TotalMilliseconds),
                    sizeBytes = r.Response.Body.LongLength,
                    passed = Passed(r.Model, r.Response),
                    assertions = r.Model.Assertions.Any(a => a.Enabled)
                        ? AssertionEvaluator.Evaluate(r.Model.Assertions, r.Response)
                            .Select(a => new { a.Description, a.Passed, actual = a.Actual })
                        : null,
                    error = r.Response.Error?.Message
                }),
                // A count of zero would be noise in every ordinary run's envelope, so the key appears
                // only when a chain actually stopped short.
                summary = skippedSteps.Count == 0
                    ? new { total = results.Count, passed, failed, elapsedMs = clock.ElapsedMilliseconds }
                    : (object)new { total = results.Count, passed, failed, elapsedMs = clock.ElapsedMilliseconds,
                                    skipped = skippedSteps.Count }
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        else
        {
            foreach (var (path, model, _, r) in results)
            {
                string verdict = Passed(model, r) ? "PASS" : "FAIL";
                string status = r.Error is not null ? "ERR" : r.StatusCode?.ToString() ?? "—";
                int assertCount = model.Assertions.Count(a => a.Enabled);
                string detail = r.Error is not null ? $"  ({r.Error.Message})"
                    : assertCount > 0 ? $"  ({assertCount} assertion{(assertCount == 1 ? "" : "s")})" : "";
                stdout.WriteLine(
                    $"{verdict}  {status,4}  {r.Elapsed.TotalMilliseconds,6:F0} ms  {OutputText.Size(r.Body.LongLength),9}  {path}{detail}");
            }
            // Same columns as PASS/FAIL, with an em dash where there is no measurement to report.
            foreach (var label in skippedSteps)
                stdout.WriteLine($"SKIP  {"—",4}  {"—",6} ms  {"—",9}  {label}  (an earlier step failed)");
            stdout.WriteLine($"----\n{results.Count} request{(results.Count == 1 ? "" : "s")} · {passed} passed · {failed} failed" +
                             (skippedSteps.Count > 0 ? $" · {skippedSteps.Count} skipped" : "") +
                             $" · {clock.Elapsed.TotalSeconds:F1} s");
        }

        if (mdFile is not null || mdVault is not null)
            WriteReport(outcomes, skippedSteps, chainName, clock.Elapsed,
                        mdFile, mdVault, mdIncludeSecrets, stderr);

        return failed == 0 ? ExitCodes.Ok : ExitCodes.Failure;
    }

    /// <summary>Write the run as a markdown note. As with `doctor --md`, a failure to write is
    /// reported but never changes the run's own verdict: the results are what the caller asked for,
    /// and a CI job must not turn green or red because of a folder permission.</summary>
    private static void WriteReport(List<RequestOutcome> outcomes, List<string> skipped, string? chainName,
                                    TimeSpan elapsed, string? file, string? vault, bool includeSecrets,
                                    TextWriter stderr)
    {
        var when = DateTimeOffset.UtcNow;
        var report = new RunReport(chainName,
            outcomes.Select(o => new RunReportRow(
                o.Label, o.Request.Method, o.Url, o.Response.StatusCode, o.Response.Elapsed,
                o.Response.Body.LongLength, o.Passed,
                o.Request.Assertions.Any(a => a.Enabled)
                    ? AssertionEvaluator.Evaluate(o.Request.Assertions, o.Response)
                    : Array.Empty<AssertionResult>(),
                o.Response.Error?.Message,
                o.Captures)).ToList(),
            skipped, elapsed);

        string path = file ?? Path.Combine(vault!,
            RunReportMarkdown.VaultPath(report, when).Replace('/', Path.DirectorySeparatorChar));
        try
        {
            if (Path.GetDirectoryName(path) is { Length: > 0 } folder) Directory.CreateDirectory(folder);
            File.WriteAllText(path, RunReportMarkdown.Render(report, when, includeSecrets));
            stderr.WriteLine($"wrote the run report to {path}");
        }
        catch (Exception ex)
        {
            stderr.WriteLine($"warning: could not write the run report to {path}: {ex.Message}");
        }
    }

    /// <summary>A request passes when its enabled assertions all pass; with no assertions it falls
    /// back to the historical "a 2xx response is a pass" behaviour.</summary>
    private static bool Passed(RequestModel m, ApiResponse r) => AssertionEvaluator.RequestPassed(m, r);

    /// <summary>Replay a captured HAR's entries, in file order, as an ordered suite — WITH the
    /// selected client certificate attached, which is the entire point: a session captured in a
    /// browser replays authenticated. A HAR entry carries no assertions, so it passes on any 2xx
    /// response. Never writes to saved state (it isn't a saved collection), matching --workspace
    /// semantics.
    /// <para>Under --diff-har (<paramref name="diffOptions"/> non-null, which is the flag: there is
    /// nothing to configure when diffing is off) the pass rule changes — an entry passes only when
    /// its response is identical to the one recorded, status included. That is what makes a captured
    /// session a regression test rather than a liveness check.</para></summary>
    private static int RunHar(
        string path, System.Security.Cryptography.X509Certificates.X509Certificate2? cert, AppState state,
        TransportOverrides transportOverrides, bool showRedirects, bool json,
        TextWriter stdout, TextWriter stderr, CliServices services, TrustPredicates predicates,
        DiffOptions? diffOptions = null)
    {
        Har har;
        try { har = HarReader.Parse(File.ReadAllText(path)); }
        catch (HarFormatException ex) { throw new CliDataException(ex.Message); }
        if (har.Log.Entries.Count == 0) throw new CliDataException("The HAR has no entries.");

        var transport = transportOverrides.ApplyTo(new TransportOptions());
        if (ApiClient.ValidateTransport(transport, har.Log.Entries[0].Request.Url) is { } transportProblem)
            throw new CliUsageException(transportProblem);

        var results = new List<(string Label, string Method, string Url, ApiResponse Response, DiffResult? Diff)>();
        var clock = Stopwatch.StartNew();
        int index = 0;
        foreach (var entry in har.Log.Entries)
        {
            index++;
            var pr = HarReader.ToParsedRequest(entry);
            string label = $"entry {index}: {pr.Method} {pr.Url}";
            var request = new ApiRequest
            {
                Method = new HttpMethod(pr.Method),
                Url = pr.Url,
                Headers = services.Profile?.WithStandingHeaders(pr.Headers) ?? pr.Headers,
                Body = pr.Body,
                ContentType = pr.ContentType
            };
            string host = TokenService.HostOf(pr.Url);
            var response = services.Client.SendAsync(request, cert,
                transport: transport,
                trustServerCertificate: predicates.For(host),
                cancellationToken: services.Cancel).GetAwaiter().GetResult();
            // A transport failure produced no response to compare; it fails on its own terms below.
            DiffResult? diff = diffOptions is not null && response.Error is null
                ? ResponseDiff.Compare(DiffBaseline.FromEntry(entry), ResponseSnapshot.From(response), diffOptions)
                : null;
            results.Add((label, pr.Method, pr.Url, response, diff));
            if (diff is { Identical: false })
                foreach (var line in DiffText.Format(diff).Split('\n'))
                    stderr.WriteLine($"{label}: {line}");
            if (showRedirects && response.Redirects.Count > 0)
                foreach (var line in OutputText.RedirectLines(response.Redirects).Split('\n'))
                    stderr.WriteLine($"{label}:{line}");
        }
        clock.Stop();

        // Under --diff-har the diff *is* the verdict: the status is one of the things it compares, so
        // "it answered 200" no longer carries any information the diff hasn't already weighed.
        bool EntryPassed((string Label, string Method, string Url, ApiResponse Response, DiffResult? Diff) r) =>
            diffOptions is not null ? r.Diff is { Identical: true } : r.Response.IsSuccess;

        int passed = results.Count(EntryPassed);
        int failed = results.Count - passed;

        if (json)
        {
            stdout.WriteLine(JsonSerializer.Serialize(new
            {
                results = results.Select(r => new
                {
                    path = r.Label,
                    method = r.Method,
                    url = r.Url,
                    status = r.Response.StatusCode,
                    elapsedMs = Math.Round(r.Response.Elapsed.TotalMilliseconds),
                    sizeBytes = r.Response.Body.LongLength,
                    passed = EntryPassed(r),
                    diff = r.Diff is null ? null : new
                    {
                        identical = r.Diff.Identical,
                        statusBefore = r.Diff.StatusBefore,
                        statusAfter = r.Diff.StatusAfter,
                        headers = r.Diff.Headers.Select(h => new { name = h.Name, before = h.Before, after = h.After }).ToList(),
                        body = r.Diff.Body.Select(b => new
                        {
                            path = b.Path,
                            kind = b.Kind.ToString(),
                            before = b.Before,
                            after = b.After
                        }).ToList()
                    },
                    error = r.Response.Error?.Message
                }),
                summary = new { total = results.Count, passed, failed, elapsedMs = clock.ElapsedMilliseconds }
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        else
        {
            foreach (var result in results)
            {
                var (label, _, _, r, entryDiff) = result;
                string verdict = EntryPassed(result) ? "PASS" : "FAIL";
                string status = r.Error is not null ? "ERR" : r.StatusCode?.ToString() ?? "—";
                int diffCount = entryDiff is null ? 0
                    : entryDiff.Headers.Count + entryDiff.Body.Count
                      + (entryDiff.StatusBefore != entryDiff.StatusAfter ? 1 : 0);
                string detail = r.Error is not null ? $"  ({r.Error.Message})"
                    : entryDiff is { Identical: false }
                        ? $"  ({diffCount} difference{(diffCount == 1 ? "" : "s")})"
                        : "";
                stdout.WriteLine(
                    $"{verdict}  {status,4}  {r.Elapsed.TotalMilliseconds,6:F0} ms  {OutputText.Size(r.Body.LongLength),9}  {label}{detail}");
            }
            stdout.WriteLine($"----\n{results.Count} request{(results.Count == 1 ? "" : "s")} · {passed} passed · {failed} failed · {clock.Elapsed.TotalSeconds:F1} s");
        }

        return failed == 0 ? ExitCodes.Ok : ExitCodes.Failure;
    }

    /// <summary>The creator version written into a captured HAR document — the same idiom as
    /// <c>certapi --version</c>: the assembly's informational version with any <c>+build</c>
    /// metadata stripped.</summary>
    private static string HarCreatorVersion()
    {
        var version = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
        int plus = version.IndexOf('+');
        return plus > 0 ? version[..plus] : version;
    }
}
