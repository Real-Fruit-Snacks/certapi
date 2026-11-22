using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ApiTester.Cli.Mcp;
using ApiTester.Core;
using ApiTester.Grpc;

namespace ApiTester.Cli.Commands;

public static class McpCommand
{
    public const string Help = """
        Usage: certapi mcp [options]

        Runs a Model Context Protocol server on stdio so an AI agent can make mutual-TLS calls
        with a pinned Windows-store client certificate. Tools: send_request, run_saved, run_chain,
        list_saved, list_environments, list_certificates, grpc_list, grpc_call, ws_exchange,
        sse_read, cors_check, self_test, doctor, inspect_cert, inspect_token, diff_known_good. Saved
        requests, environments, and chains are also published as read-only MCP resources.
        Configure your MCP host to launch this command.

          --cert <thumb|subject>  Certificate the tools use (pinned; the agent can't change it). A saved
                                  request that names its own certificate presents that one instead
          --store <location>      CurrentUser (default); LocalMachine searches both stores
          --cert-file <path>      Pin a certificate from a file (.pfx/.p12 or .pem/.crt) instead
          -E <file[:password]>    curl's spelling of --cert-file (and --cert-password)
          --cert-password <pw>    Password for a .pfx/.p12 certificate file
          --key-file <path>       Private-key file for a PEM certificate whose key is separate
          --cert-issuer <substr>  Narrow --cert to certificates issued by a matching CA (or use alone)
          --cert-serial <hex>     Pick the certificate with this serial number (or use alone)
          --cert-pin <pin>        Smart-card / HSM PIN for the key, supplied ahead of time so a
                                  headless job is not stopped by the prompt. Prefer CERTAPI_CERT_PIN,
                                  which keeps it out of the shell history. Windows (CNG) only
          --allow <host>          Allowed upstream host (repeatable); a URL must match or be a
                                  subdomain of one. A bare host name or IP address only: an empty
                                  value, a scheme, port, path, wildcard or comma is refused. Omit
                                  to allow any host (the start-up line says so; set it for anything
                                  but local experiments). Enforced per request — a chain step whose
                                  URL comes from an earlier capture is still checked.
          --allow-any             Allow every host, on purpose: the start-up warning becomes a note.
                                  Cannot be combined with --allow
          --insecure              Ignore upstream server-certificate errors (internal CAs)
          --timeout <seconds>     send_request and gRPC call deadline (default 100; a saved
                                  request keeps its own saved timeout)
          --workspace <file>      Load saved requests / environments / chains from a workspace
                                  file. The workspace is read once at launch; captures and tokens
                                  live in memory for the session and are never written back.
          --no-auto-token         Don't capture/reuse bearer tokens across the session's calls, and
                                  don't attach the workspace's stored tokens or browser cookies
          --max-body <size>       Most of a response body a tool returns (default 1m; e.g. 64k,
                                  8m). The rest is not read; the result says "truncated": true
          --protoset <file>       Compiled descriptor set for grpc_list/grpc_call against a
                                  server without reflection (produce it with protoc's
                                  descriptor_set_out and include_imports options). Pinned at
                                  launch; the agent cannot name files.
          --print-config <host>   Print the configuration block that launches this server with the
                                  other options given, for claude (Claude Desktop's
                                  claude_desktop_config.json, also Claude Code's .mcp.json),
                                  vscode (.vscode/mcp.json) or cursor (.cursor/mcp.json), and
                                  exit. Nothing is started. Secrets (--cert-password, --cert-pin,
                                  --proxy-user) are refused: use their environment variables

        The transport options apply to every call the tools make, with the same rules as `send`.
        """ + "\n\n" + TransportFlags.Help + "\n\n" + """
        Redirects are never followed (the redirect options above are accepted and change nothing): a 3xx comes back as itself, so every hop an agent takes is
        an explicit call checked against the allowlist. A host pinned with `certapi trust add`
        is reachable without --insecure, exactly as it is for send/run.

        Tokens returned by one tool call (e.g. a login via send_request) are captured in
        memory for this session and attached to later calls to the same host. Values captured
        by run_saved / run_chain resolve {{variables}} in later calls the same way. The vars
        argument of run_saved / run_chain fills values into a saved request but cannot change
        its scheme, host or port. Response bodies and headers go to the agent as received.

        Configuration: a certapi.config.json found by walking up from the working directory is
        NOT read by mcp — an MCP host chooses that directory. Use --config, CERTAPI_CONFIG or the
        per-user file; the start-up line names the file in use.

        Global: --debug (verbose diagnostics) and --log-file <path> work here too.

        Examples:
          certapi mcp --cert "CN=Agent Client" --allow api.example.com
          certapi mcp --cert "CN=Agent Client" --allow api.example.com --print-config claude
          certapi mcp --cert 4A8823… --allow api.example.com --allow auth.example.com --insecure
          certapi mcp --cert "CN=Agent Client" --allow api.example.com --workspace .\suite.json
          certapi mcp --cert "CN=Agent Client" --allow grpc.internal --protoset .\contracts.protoset

        Speaks JSON-RPC 2.0 over stdin/stdout: protocol revisions 2024-11-05 through 2025-11-25
        after an initialize handshake, and 2026-07-28 per request (server/discover). Diagnostics
        go to stderr. Stop with Ctrl+C or by closing stdin. Exit 0 clean shutdown, 2 usage, 3 data
        error.
        """;

    public static int Run(Args args, TextReader input, TextWriter stdout, TextWriter stderr, CliServices services)
    {
        string store = args.Value("--store") ?? services.Profile?.Store ?? "CurrentUser";
        var allowHosts = args.Values("--allow");
        bool insecure = args.Switch("--insecure", services.Profile?.Insecure);
        string? timeoutRaw = args.Value("--timeout");
        string? workspace = args.Value("--workspace");
        bool noAutoToken = args.Flag("--no-auto-token");
        string? maxBodyRaw = args.Value("--max-body");
        string? protosetPath = args.Value("--protoset");
        bool allowAny = args.Flag("--allow-any");
        string? printConfig = args.Value("--print-config");
        var overrides = TransportFlags.Parse(args, out _, environment: null, services.Profile);
        // Resolve the certificate before Positionals() rejects its options (store or a file).
        var cert = CliCert.Resolve(args, store, services, stderr);
        bool localMachine = store.Equals("LocalMachine", StringComparison.OrdinalIgnoreCase);
        if (args.Positionals().Count > 0) throw new CliUsageException(Help);

        int timeout = 100;
        if (timeoutRaw is not null && (!int.TryParse(timeoutRaw, out timeout) || timeout <= 0))
            throw new CliUsageException($"--timeout expects a positive number of seconds, got '{timeoutRaw}'.");

        GrpcDescriptorSet? protoset = null;
        if (protosetPath is not null)
        {
            try { protoset = GrpcDescriptorSet.Load(protosetPath); }
            catch (GrpcDescriptorSetException ex) { throw new CliDataException(ex.Message); }
        }

        long maxBody = DefaultMaxBody;
        if (maxBodyRaw is not null && (ServeCommand.ParseSize(maxBodyRaw) is not { } parsed || parsed <= 0))
            throw new CliUsageException($"--max-body expects a size such as 64k or 8m, got '{maxBodyRaw}'.");
        else if (maxBodyRaw is not null) maxBody = ServeCommand.ParseSize(maxBodyRaw)!.Value;

        foreach (var entry in allowHosts)
            if (HostAllowlist.EntryProblem(entry) is { } problem)
                throw new CliUsageException($"--allow '{entry}': {problem}");
        if (allowAny && allowHosts.Count > 0)
            throw new CliUsageException("--allow-any and --allow contradict each other: give one or the other.");
        var allow = new HostAllowlist(allowHosts);

        if (printConfig is not null)
        {
            stdout.Write(McpHostConfig.Render(printConfig, args.Original, Environment.ProcessPath));
            stderr.WriteLine($"note: merge this into {McpHostConfig.FileFor(printConfig)}; nothing was started.");
            if (allow.Count == 0 && !allowAny)
                stderr.WriteLine("warning: this configuration allows any host; add --allow <host> (or --allow-any to mean it).");
            return ExitCodes.Ok;
        }

        // Decided from the parsed list, so the warning cannot be skipped by a value that parses to nothing.
        stderr.WriteLine($"certapi mcp ready · cert: {cert?.Subject ?? "none"} · " +
            (allow.Count > 0 ? "allow: " + string.Join(", ", allow.Entries)
             : allowAny ? "allow: any host (--allow-any)"
             : "allow: ANY HOST (no --allow given; pass --allow <host>, or --allow-any to mean it)"));
        if (services.ProfileSource is { } source && services.Profile is { } profile)
            stderr.WriteLine($"certapi mcp config · {source.Path} ({source.Rule}) · " + DescribeProfile(profile));
        if (insecure) stderr.WriteLine("certapi mcp warning · server-certificate checks are OFF (--insecure)");

        var notifier = new McpNotifier();
        var (tools, resources) = Build(cert, allow, insecure, timeout, localMachine, workspace,
            noAutoToken, services, stderr, overrides, protoset, notifier, maxBody);
        var server = new McpServer(tools, Version(), resources, notifier);
        server.Run(input, stdout, stderr, services.Cancel);
        return ExitCodes.Ok;
    }

    /// <summary>The settings a configuration profile changes for this server, named so the
    /// operator can see them in the host's log.</summary>
    internal static string DescribeProfile(ConfigProfile p)
    {
        var parts = new List<string>();
        if (p.Insecure == true) parts.Add("insecure");
        if (p.Proxy is not null) parts.Add("proxy " + p.Proxy);
        if (p.NoProxy == true) parts.Add("no proxy");
        if (p.CaFiles.Count > 0) parts.Add($"{p.CaFiles.Count} extra root certificate file(s)");
        if (p.Cert is not null || p.CertFile is not null) parts.Add("certificate");
        if (p.Store is not null) parts.Add("store " + p.Store);
        if (p.Revocation is not null) parts.Add("revocation " + p.Revocation);
        if (p.Timeout is not null) parts.Add("timeout");
        return parts.Count == 0 ? "no settings that affect mcp" : string.Join(", ", parts);
    }

    private static string Version()
    {
        var v = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
        int plus = v.IndexOf('+');
        return plus > 0 ? v[..plus] : v;
    }

    /// <summary>The original test seam: tools only, default transport, no protoset.</summary>
    internal static IReadOnlyList<ToolDef> BuildTools(
        X509Certificate2? cert, HostAllowlist allow, bool insecure, int timeout,
        bool includeLocalMachine, string? workspace, bool noAutoToken, CliServices services,
        TextWriter? stderr = null) =>
        Build(cert, allow, insecure, timeout, includeLocalMachine, workspace, noAutoToken,
              services, stderr, new TransportOverrides(), null, null).Tools;

    /// <summary>The body a tool result carries at most, unless --max-body says otherwise: enough
    /// for any API answer, small enough that one large download cannot exhaust memory or an
    /// agent's context.</summary>
    internal const long DefaultMaxBody = 1024 * 1024;

    internal static (IReadOnlyList<ToolDef> Tools, IReadOnlyList<ResourceDef> Resources) Build(
        X509Certificate2? cert, HostAllowlist allow, bool insecure, int timeout,
        bool includeLocalMachine, string? workspace, bool noAutoToken, CliServices services,
        TextWriter? stderr, TransportOverrides overrides, GrpcDescriptorSet? protoset,
        McpNotifier? notifier, long maxBody = DefaultMaxBody)
    {
        // The session's one workspace view, read once at launch: definitions, trust pins, and the
        // home for everything the session captures — tokens, cookies, {{variables}} — which is
        // what lets a login by one tool call serve the calls after it. Never written back to disk.
        var state = CliWorkspace.Load(workspace, services.LiveStatePath, stderr);
        var predicates = new TrustPredicates(state);

        // Redirects stay off however the saved request or the launch flags lean: every hop an
        // agent takes must be an explicit call the allowlist judged, and a 3xx as data is exactly
        // that. --insecure is the launch-wide override it has always been.
        TransportOptions McpTransport(TransportOptions baseline)
        {
            var options = overrides.ApplyTo(baseline) with { FollowRedirects = false, MaxResponseBytes = maxBody };
            if (insecure) options = options with { IgnoreServerCertificateErrors = true };
            return options;
        }

        // Names the host only: the resolved URL can carry a secret variable in its path or query.
        string? Gate(string url) => allow.IsAllowed(url) ? null : $"host '{RequestGuards.DescribeHost(url)}' is not allowed";

        // The request now being served can be cancelled (notifications/cancelled, end of input);
        // outside the server loop, Ctrl+C is all there is.
        CancellationToken CallCt() => McpServer.CurrentCallCancellation ?? services.Cancel;

        RequestRunContext MakeContext(List<string> notes, Func<Dictionary<string, string>>? baseline = null) => new()
        {
            BaselineVariables = baseline,
            RefuseHostOverride = true,
            State = state,
            Client = services.Client,
            Predicates = predicates,
            FindCertificate = thumbprint => services.FindCertificate(thumbprint),
            DefaultCertificate = cert,
            AllowUrl = Gate,
            NoAutoToken = noAutoToken,
            StrictVars = true,
            Record = false,
            TransportOverride = McpTransport,
            Note = line => { notes.Add(line); notifier?.Info(line); },
            Debug = line => { services.Log.Debug(line); notifier?.Debug(line); }
        };

        ToolResult SendUrl(string method, string url, IEnumerable<KeyValuePair<string, string>> headers,
            string? body, string? contentType)
        {
            if (Gate(url) is { } refused)
                return new ToolResult(JsonSerializer.Serialize(new { error = refused }), true);

            var headerList = headers.ToList();
            if (RequestGuards.HostHeaderMismatch(url, headerList) is { } hostProblem) return Err(hostProblem);
            foreach (var h in headerList)
                if (HeaderValues.Problem(h.Key, h.Value) is { } valueProblem) return Err(valueProblem);
            // A Content-Type given among the headers names the body's type, as on `certapi send`.
            if (contentType is null && headerList.LastOrDefault(h => h.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)) is { Key: not null } typedType)
            {
                contentType = typedType.Value;
                headerList.RemoveAll(h => h.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase));
            }
            var notes = new List<string>();
            if (!noAutoToken)
            {
                var used = TokenService.AutoAttach(state, url, headerList, out var expired);
                if (used is not null) notes.Add($"using captured token for {TokenService.HostOf(url)}");
                else if (expired is not null) notes.Add($"captured token for {TokenService.HostOf(url)} has expired");
            }

            var transport = McpTransport(new TransportOptions());
            if (ApiClient.ValidateTransport(transport, url) is { } transportProblem)
                return Err(transportProblem);

            var request = new ApiRequest
            {
                Method = new HttpMethod(method.ToUpperInvariant()),
                Url = url,
                Headers = headerList,
                Body = body,
                ContentType = body is not null ? (contentType ?? "application/json") : null,
                Timeout = TimeSpan.FromSeconds(timeout)
            };
            var response = services.Client.SendAsync(request, cert,
                    transport: transport,
                    trustServerCertificate: predicates.For(TokenService.HostOf(url)),
                    cancellationToken: CallCt())
                .GetAwaiter().GetResult();

            if (!noAutoToken && response.Error is null &&
                TokenService.Capture(state, response.FinalUrl(url), response.Body, response.ContentType, response.Headers) is { } captured)
                notes.Add($"captured bearer token for {TokenService.HostOf(url)} ({captured.Source})");
            foreach (var n in notes) notifier?.Info(n);

            return new ToolResult(SendCommand.BuildEnvelope(response, includeBody: true, notes), IsError: response.Error is not null);
        }

        // ---- send_request ----
        var sendRequest = new ToolDef("send_request",
            "Send an HTTP request to an allowed host with the pinned client certificate. Returns status, headers, and body. Redirects are not followed; a 3xx comes back as itself.",
            JsonNode.Parse("""
                {"type":"object","additionalProperties":false,"required":["url"],"properties":{
                  "method":{"type":"string","description":"HTTP method (default GET)"},
                  "url":{"type":"string","description":"Absolute http(s):// URL on an allowed host"},
                  "headers":{"type":"object","additionalProperties":{"type":"string"}},
                  "body":{"type":"string"},
                  "contentType":{"type":"string"}}}
                """)!,
            a =>
            {
                string? url = Str(a, "url");
                if (string.IsNullOrWhiteSpace(url)) return Err("url is required");
                string? methodArg = Str(a, "method");
                if (methodArg is not null && string.IsNullOrWhiteSpace(methodArg))
                    return Err(MethodOption.Describe("method", methodArg));
                var headers = ObjPairs(a, "headers");
                return SendUrl(methodArg ?? "GET", url!, headers, Str(a, "body"), Str(a, "contentType"));
            })
        { Annotations = new ToolAnnotations(ReadOnlyHint: false, DestructiveHint: true, IdempotentHint: false, OpenWorldHint: true) };

        // ---- list_certificates ----
        var listCerts = new ToolDef("list_certificates",
            "List client certificates in the Windows store (subject, thumbprint, expiry).",
            JsonNode.Parse("""{"type":"object","additionalProperties":false,"properties":{"filter":{"type":"string"}}}""")!,
            a =>
            {
                string? filter = Str(a, "filter");
                var certs = services.ListCertificates(includeLocalMachine)
                    .Where(c => filter is null ||
                                c.Subject.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                                c.Issuer.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                                c.Thumbprint.Contains(filter, StringComparison.OrdinalIgnoreCase))
                    .Select(c => new
                    {
                        subject = c.Subject, issuer = c.Issuer, thumbprint = c.Thumbprint,
                        notAfter = c.NotAfter, expired = c.IsExpired(), clientAuthEku = c.HasClientAuthEku
                    });
                return new ToolResult(JsonSerializer.Serialize(new { certificates = certs }), false);
            })
        {
            Annotations = new ToolAnnotations(ReadOnlyHint: true, IdempotentHint: true, OpenWorldHint: false),
            OutputSchema = JsonNode.Parse("""
                {"type":"object","properties":{"certificates":{"type":"array","items":{"type":"object","properties":{
                  "subject":{"type":"string"},"issuer":{"type":"string"},"thumbprint":{"type":"string"},
                  "notAfter":{"type":"string"},"expired":{"type":"boolean"},"clientAuthEku":{"type":"boolean"}}}}}}
                """)!
        };

        // ---- list_saved ----
        var listSaved = new ToolDef("list_saved",
            "List saved requests from your collections as Collection/Folder/Request paths.",
            JsonNode.Parse("""{"type":"object","additionalProperties":false,"properties":{}}""")!,
            _ =>
            {
                var items = SavedLeaves(state)
                    .Select(l => new { path = l.Path, method = l.Node.Request!.Method, url = MarkdownSecrets.RedactUrl(l.Node.Request!.EffectiveUrl()) });
                return new ToolResult(JsonSerializer.Serialize(new { items }), false);
            })
        {
            Annotations = new ToolAnnotations(ReadOnlyHint: true, IdempotentHint: true, OpenWorldHint: false),
            OutputSchema = JsonNode.Parse("""
                {"type":"object","properties":{"items":{"type":"array","items":{"type":"object","properties":{
                  "path":{"type":"string"},"method":{"type":"string"},"url":{"type":"string"}}}}}}
                """)!
        };

        // ---- list_environments ----
        var listEnvironments = new ToolDef("list_environments",
            "List the workspace's environments by name, for run_saved / run_chain's env argument. Variable values are never returned.",
            JsonNode.Parse("""{"type":"object","additionalProperties":false,"properties":{}}""")!,
            _ =>
            {
                var items = state.Environments.Select(e => new
                {
                    name = e.Name,
                    active = e.Id == state.ActiveEnvironmentId,
                    variables = e.Variables.Count
                });
                return new ToolResult(JsonSerializer.Serialize(new { environments = items }), false);
            })
        {
            Annotations = new ToolAnnotations(ReadOnlyHint: true, IdempotentHint: true, OpenWorldHint: false),
            OutputSchema = JsonNode.Parse("""
                {"type":"object","properties":{"environments":{"type":"array","items":{"type":"object","properties":{
                  "name":{"type":"string"},"active":{"type":"boolean"},"variables":{"type":"integer"}}}}}}
                """)!
        };

        // ---- run_saved ----
        var runSaved = new ToolDef("run_saved",
            "Run a saved request by its Collection/Folder/Request path, exactly as `certapi run` would: its saved transport settings, auth, assertions, and capture rules all apply. Captured values resolve {{variables}} in later calls this session.",
            JsonNode.Parse("""
                {"type":"object","additionalProperties":false,"required":["path"],"properties":{
                  "path":{"type":"string"},
                  "env":{"type":"string"},
                  "vars":{"type":"object","additionalProperties":{"type":"string"}}}}
                """)!,
            a =>
            {
                string? path = Str(a, "path");
                if (string.IsNullOrWhiteSpace(path)) return Err("path is required");
                List<(string Path, CollectionNode Node)> leaves;
                try { leaves = CliWorkspace.ResolveTargets(state, path, all: false); }
                catch (CliDataException ex) { return Err(ex.Message); }
                if (leaves.Count != 1) return Err($"'{path}' resolves to {leaves.Count} requests; name a single request");

                var notes = new List<string>();
                string? env = Str(a, "env");
                var overrides = ObjKeys(a, "vars");
                if (overrides.FirstOrDefault(kv => HeaderValues.HasLineBreak(kv)) is { } broken)
                    return Err($"the value of var '{broken[..broken.IndexOf('=')]}' contains a line break or NUL character");
                var vars = new RunVariables(() => CliWorkspace.BuildVars(state, env, overrides));
                // The agent may fill values in, but not choose where a saved request — with its saved
                // secrets — is sent: that stays what the workspace alone resolves it to.
                Func<Dictionary<string, string>>? baseline = overrides.Count == 0
                    ? null : () => CliWorkspace.BuildVars(state, env, Array.Empty<string>());
                var outcome = RequestRunner
                    .RunAsync(leaves[0].Path, leaves[0].Node, MakeContext(notes, baseline), vars, CallCt())
                    .GetAwaiter().GetResult();
                return OutcomeResult(outcome, notes);
            })
        { Annotations = new ToolAnnotations(ReadOnlyHint: false, DestructiveHint: true, IdempotentHint: false, OpenWorldHint: true) };

        // ---- run_chain ----
        var runChain = new ToolDef("run_chain",
            "Run a saved chain by name: its requests in order as one unit, each step seeing what earlier steps captured. A failing step stops the chain unless it is marked to carry on.",
            JsonNode.Parse("""
                {"type":"object","additionalProperties":false,"required":["name"],"properties":{
                  "name":{"type":"string"},
                  "env":{"type":"string"},
                  "vars":{"type":"object","additionalProperties":{"type":"string"}}}}
                """)!,
            a =>
            {
                string? name = Str(a, "name");
                if (string.IsNullOrWhiteSpace(name)) return Err("name is required");
                RequestChain chain;
                IReadOnlyList<ResolvedChainStep> steps;
                try
                {
                    chain = ChainRunner.Find(state, name!);
                    steps = ChainRunner.Resolve(state, chain);
                }
                catch (ChainRunException ex) { return Err(ex.Message); }

                string? envName = Str(a, "env") ?? ChainRunner.PrepareCaptureEnvironment(state, chain);
                var notes = new List<string>();
                var overrides = ObjKeys(a, "vars");
                if (overrides.FirstOrDefault(kv => HeaderValues.HasLineBreak(kv)) is { } broken)
                    return Err($"the value of var '{broken[..broken.IndexOf('=')]}' contains a line break or NUL character");
                var vars = new RunVariables(() => CliWorkspace.BuildVars(state, envName, overrides));
                Func<Dictionary<string, string>>? baseline = overrides.Count == 0
                    ? null : () => CliWorkspace.BuildVars(state, envName, Array.Empty<string>());
                var result = ChainRunner
                    .RunAsync(steps, MakeContext(notes, baseline), vars, progress: null, CallCt())
                    .GetAwaiter().GetResult();

                var stepArr = new JsonArray();
                foreach (var o in result.Steps)
                {
                    var step = new JsonObject
                    {
                        ["label"] = o.Label,
                        ["passed"] = o.Passed,
                        ["status"] = o.Response.Error is null ? o.Response.StatusCode : null,
                        ["error"] = o.Response.Error?.Message
                    };
                    if (o.Captures.Count > 0)
                        step["captures"] = new JsonArray(o.Captures.Select(c => (JsonNode)new JsonObject
                        {
                            ["variable"] = c.Variable, ["ok"] = c.Ok, ["error"] = c.Error
                        }).ToArray());
                    stepArr.Add(step);
                }
                var payload = new JsonObject
                {
                    ["chain"] = chain.Name,
                    ["passed"] = result.Steps.All(o => o.Passed) && result.SkippedLabels.Count == 0,
                    ["steps"] = stepArr,
                    ["skipped"] = new JsonArray(result.SkippedLabels.Select(s => (JsonNode)s).ToArray()),
                    ["notes"] = new JsonArray(notes.Select(n => (JsonNode)n).ToArray())
                };
                return new ToolResult(payload.ToJsonString(), IsError: false);
            })
        { Annotations = new ToolAnnotations(ReadOnlyHint: false, DestructiveHint: true, IdempotentHint: false, OpenWorldHint: true) };

        // ---- grpc_list ----
        var grpcList = new ToolDef("grpc_list",
            "List the services and methods a gRPC server advertises via reflection — or, when the operator pinned a descriptor set at launch, exactly what that set declares (no address needed then).",
            JsonNode.Parse("""{"type":"object","additionalProperties":false,"properties":{"address":{"type":"string","description":"https:// or http:// gRPC endpoint on an allowed host"}}}""")!,
            a =>
            {
                string? address = Str(a, "address");
                try
                {
                    IReadOnlyList<GrpcServiceInfo> discovered;
                    if (protoset is not null && address is null)
                        discovered = protoset.Services;
                    else
                    {
                        if (string.IsNullOrWhiteSpace(address))
                            return Err("address is required (no descriptor set was pinned at launch)");
                        if (Gate(address!) is { } refused) return Err(refused);
                        var caller = new GrpcCaller(new Uri(address!), cert, McpTransport(new TransportOptions()),
                            predicates.For(new Uri(address!).Host), protoset);
                        var callCt = CallCt();
                        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(callCt);
                        deadline.CancelAfter(TimeSpan.FromSeconds(timeout));
                        try { discovered = caller.DiscoverAsync(deadline.Token).GetAwaiter().GetResult(); }
                        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !callCt.IsCancellationRequested)
                        { return Err($"the server did not answer within {timeout} s (--timeout)"); }
                        finally { caller.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
                    }
                    var payload = new
                    {
                        services = discovered.Select(s => new
                        {
                            name = s.Name,
                            methods = s.Methods.Select(m => new
                            {
                                name = m.Name,
                                kind = GrpcCommand.KindName(m.ClientStreaming, m.ServerStreaming),
                                inputType = m.InputType,
                                outputType = m.OutputType
                            })
                        })
                    };
                    return new ToolResult(JsonSerializer.Serialize(payload), false);
                }
                catch (GrpcReflectionUnavailableException ex) { return Err(ex.Message); }
                catch (GrpcStatusException ex) { return Err(ex.Message); }
            })
        {
            Annotations = new ToolAnnotations(ReadOnlyHint: true, IdempotentHint: true, OpenWorldHint: true),
            OutputSchema = JsonNode.Parse("""
                {"type":"object","properties":{"services":{"type":"array","items":{"type":"object","properties":{
                  "name":{"type":"string"},"methods":{"type":"array","items":{"type":"object","properties":{
                  "name":{"type":"string"},"kind":{"type":"string"},"inputType":{"type":"string"},"outputType":{"type":"string"}}}}}}}}}
                """)!
        };

        // ---- grpc_call ----
        var grpcCall = new ToolDef("grpc_call",
            "Invoke a gRPC method — unary, server-streaming, client-streaming, or bidirectional, chosen from the method's own definition — with the pinned certificate. `data` is one JSON message, or an array of them for a streaming request.",
            JsonNode.Parse("""
                {"type":"object","additionalProperties":false,"required":["address","method"],"properties":{
                  "address":{"type":"string","description":"https:// or http:// gRPC endpoint on an allowed host"},
                  "method":{"type":"string","description":"Service/Method; a short service name resolves when unambiguous"},
                  "data":{"description":"The request message as JSON, or an array of messages for a streaming request (default {})"},
                  "metadata":{"type":"object","additionalProperties":{"type":"string"}},
                  "maxMessages":{"type":"integer","description":"Stop a streaming response after this many messages (default 100)"}}}
                """)!,
            a => GrpcCallTool(a, cert, protoset, predicates, Gate, McpTransport, CallCt(), timeout))
        { Annotations = new ToolAnnotations(ReadOnlyHint: false, DestructiveHint: true, IdempotentHint: false, OpenWorldHint: true) };

        // ---- ws_exchange / sse_read ----
        // A stream is bounded twice — by a count and by a time — so a tool call always returns,
        // and its total text by --max-body, so a chatty socket cannot flood the agent's context.
        // The allowlist judges the stream's http(s) form: ws:// is not a scheme it would pass.
        string? StreamGate(string url) => Gate(McpStreamTools.HttpForm(url));
        var wsExchange = new ToolDef("ws_exchange",
            "Open a WebSocket to an allowed host with the pinned client certificate, send the given text messages, and return what the server sends back until `maxMessages` arrive, `seconds` pass, or it closes.",
            JsonNode.Parse("""
                {"type":"object","additionalProperties":false,"required":["url"],"properties":{
                  "url":{"type":"string","description":"ws:// or wss:// URL on an allowed host"},
                  "messages":{"type":"array","items":{"type":"string"},"description":"Text messages to send, in order, once connected"},
                  "headers":{"type":"object","additionalProperties":{"type":"string"}},
                  "maxMessages":{"type":"integer","description":"Stop after this many messages arrive (default 20; 0 sends only)"},
                  "seconds":{"type":"number","description":"Stop listening after this long (default 10, at most the --timeout)"}}}
                """)!,
            a => McpStreamTools.WebSocket(a, cert, StreamGate, McpTransport(new TransportOptions()), predicates, state,
                                       noAutoToken, timeout, maxBody, CallCt()))
        { Annotations = new ToolAnnotations(ReadOnlyHint: false, DestructiveHint: true, IdempotentHint: false, OpenWorldHint: true) };

        var sseRead = new ToolDef("sse_read",
            "Subscribe to a Server-Sent Events stream on an allowed host with the pinned client certificate and return the events that arrive until `maxEvents` arrive, `seconds` pass, or the stream ends.",
            JsonNode.Parse("""
                {"type":"object","additionalProperties":false,"required":["url"],"properties":{
                  "url":{"type":"string","description":"http(s):// event-stream URL on an allowed host"},
                  "headers":{"type":"object","additionalProperties":{"type":"string"}},
                  "lastEventId":{"type":"string","description":"Resume after this event id (sent as Last-Event-ID)"},
                  "maxEvents":{"type":"integer","description":"Stop after this many events (default 20)"},
                  "seconds":{"type":"number","description":"Stop listening after this long (default 10, at most the --timeout)"}}}
                """)!,
            a => McpStreamTools.EventStream(a, cert, StreamGate, McpTransport(new TransportOptions()), predicates, state,
                                         noAutoToken, timeout, maxBody, CallCt()))
        { Annotations = new ToolAnnotations(ReadOnlyHint: false, DestructiveHint: false, IdempotentHint: false, OpenWorldHint: true) };

        // ---- cors_check ----
        var corsCheck = new ToolDef("cors_check",
            "Check whether a browser page on `origin` (or `origins`) may make this request to an allowed host and read the response: sends the preflight a browser would (when one is needed) and the request itself with an Origin header, with the pinned certificate, and judges both against the CORS rules (including cross-site Set-Cookie, CORP/COEP/COOP, and Timing-Allow-Origin). Supports http(s):// and ws(s):// (WebSocket Origin / CSWSH checks), multi-origin × multi-method matrices, preflightOnly, preflightWithoutCert, diffUrl policy comparison, server config suggestions (`suggest`), and security auditing (`audit`).",
            JsonNode.Parse("""
                {"type":"object","additionalProperties":false,"required":["url"],"properties":{
                  "url":{"type":"string","description":"Absolute http(s):// or ws(s):// URL on an allowed host"},
                  "origin":{"type":"string","description":"The page's origin, e.g. https://app.example.com (optional when origins is given or audit is true)"},
                  "origins":{"type":"array","items":{"type":"string"},"description":"Multiple origins to check as a matrix"},
                  "method":{"type":"string","description":"The method the page's script uses (default GET)"},
                  "methods":{"type":"array","items":{"type":"string"},"description":"Multiple methods to check as a matrix"},
                  "headers":{"type":"object","additionalProperties":{"type":"string"},"description":"Headers the script sets"},
                  "body":{"type":"string"},
                  "contentType":{"type":"string","description":"The body's type (default application/json when there is a body)"},
                  "credentials":{"type":"boolean","description":"The page sends cookies / credentials: 'include' (default false)"},
                  "sendActual":{"type":"boolean","description":"Also send the request itself for an unsafe method (default false)"},
                  "preflightOnly":{"type":"boolean","description":"Never send the request itself (default false)"},
                  "preflightWithoutCert":{"type":"boolean","description":"Send preflights without the client certificate, as Fetch does (default false)"},
                  "privateNetwork":{"type":"boolean","description":"Also check Private Network Access, for a public page calling a private address (default false)"},
                  "audit":{"type":"boolean","description":"Probe for misconfigurations instead (arbitrary origin, null, look-alikes, plain http, dev origins, missing Vary); only GET and OPTIONS are sent"},
                  "suggest":{"type":"string","description":"Include server CORS config fix for one framework (aspnet, nginx, express, spring, iis, apigateway, apim, yarp, cloudflare, caddy, traefik, apache, fastapi, go) or 'all'"},
                  "maxAge":{"type":"integer","description":"Preflight max-age seconds for suggest (default 600)"},
                  "diffUrl":{"type":"string","description":"Second allowed http(s):// URL to compare CORS policies against"}}}
                """)!,
            a =>
            {
                string? url = Str(a, "url");
                if (string.IsNullOrWhiteSpace(url)) return Err("url is required");
                if (!Uri.TryCreate(url, UriKind.Absolute, out var target) || target.Scheme is not ("http" or "https" or "ws" or "wss"))
                    return Err("url must be an absolute http://, https://, ws:// or wss:// URL");
                bool webSocket = target.Scheme is "ws" or "wss";
                if ((webSocket ? StreamGate(url!) : Gate(url!)) is { } refused) return Err(refused);

                var origins = new List<string>();
                if (Str(a, "origin") is { } singleOrigin && !string.IsNullOrWhiteSpace(singleOrigin))
                {
                    try { origins.Add(CorsCommand.NormalizeOrigin(singleOrigin)); }
                    catch (CliUsageException ex) { return Err(ex.Message); }
                }
                if (a.TryGetProperty("origins", out var originsEl) && originsEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in originsEl.EnumerateArray())
                    {
                        if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString())) continue;
                        try
                        {
                            var norm = CorsCommand.NormalizeOrigin(item.GetString()!);
                            if (!origins.Contains(norm, StringComparer.Ordinal)) origins.Add(norm);
                        }
                        catch (CliUsageException ex) { return Err(ex.Message); }
                    }
                }
                bool audit = a.TryGetProperty("audit", out var au) && au.ValueKind == JsonValueKind.True;
                if (!audit && origins.Count == 0) return Err("origin (or origins) is required unless audit is true");

                var methods = new List<string>();
                if (Str(a, "method") is { } singleMethod && !string.IsNullOrWhiteSpace(singleMethod))
                    methods.Add(singleMethod.ToUpperInvariant());
                if (a.TryGetProperty("methods", out var methodsEl) && methodsEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in methodsEl.EnumerateArray())
                        if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                        {
                            var m = item.GetString()!.Trim().ToUpperInvariant();
                            if (!methods.Contains(m, StringComparer.Ordinal)) methods.Add(m);
                        }
                }
                if (methods.Count == 0) methods.Add("GET");
                foreach (var m in methods)
                    if (m.Length == 0 || m.Any(char.IsWhiteSpace)) return Err(MethodOption.Describe("method", m));

                var headerList = ObjPairs(a, "headers").ToList();
                if (!webSocket && RequestGuards.HostHeaderMismatch(url!, headerList) is { } hostProblem) return Err(hostProblem);
                foreach (var h in headerList)
                    if (HeaderValues.Problem(h.Key, h.Value) is { } valueProblem) return Err(valueProblem);
                string? body = Str(a, "body");
                string? type = Str(a, "contentType");
                if (type is not null)
                {
                    headerList.RemoveAll(h => h.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase));
                    headerList.Add(new("Content-Type", type));
                }
                else if (body is not null && !headerList.Any(h => h.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)))
                    headerList.Add(new("Content-Type", "application/json"));

                bool credentials = a.TryGetProperty("credentials", out var cr) && cr.ValueKind == JsonValueKind.True;
                bool sendActual = a.TryGetProperty("sendActual", out var sa) && sa.ValueKind == JsonValueKind.True;
                bool preflightOnly = a.TryGetProperty("preflightOnly", out var po) && po.ValueKind == JsonValueKind.True;
                if (sendActual && preflightOnly) return Err("sendActual and preflightOnly contradict each other");
                bool preflightWithoutCert = a.TryGetProperty("preflightWithoutCert", out var pwc) && pwc.ValueKind == JsonValueKind.True;
                bool privateNetwork = a.TryGetProperty("privateNetwork", out var pn) && pn.ValueKind == JsonValueKind.True;
                string? suggestArg = Str(a, "suggest");
                string? suggestFor = null;
                bool wantSuggest = false;
                if (!string.IsNullOrWhiteSpace(suggestArg))
                {
                    wantSuggest = true;
                    if (!suggestArg.Equals("all", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!CorsSuggest.Frameworks.Contains(suggestArg, StringComparer.OrdinalIgnoreCase))
                            return Err($"suggest must be 'all' or one of {string.Join(", ", CorsSuggest.Frameworks)}");
                        suggestFor = suggestArg;
                    }
                }
                int maxAge = a.TryGetProperty("maxAge", out var ma) && ma.TryGetInt32(out var maVal) && maVal >= 0 ? maVal : 600;
                string? diffUrl = Str(a, "diffUrl");
                if (diffUrl is not null)
                {
                    if (webSocket || audit) return Err("diffUrl only applies to standard http(s) CORS checks");
                    if (!Uri.TryCreate(diffUrl, UriKind.Absolute, out var diffTarget) || diffTarget.Scheme is not ("http" or "https"))
                        return Err("diffUrl must be an absolute http:// or https:// URL");
                    if (Gate(diffUrl) is { } diffRefused) return Err(diffRefused);
                }

                var transport = McpTransport(new TransportOptions()) with { FollowRedirects = false };

                if (webSocket)
                {
                    async Task<(bool, int?, string?)> ConnectWs(string o)
                    {
                        await using var session = new WebSocketSession();
                        var sent = headerList.Where(h => !h.Key.Equals("Origin", StringComparison.OrdinalIgnoreCase))
                            .Append(new KeyValuePair<string, string>("Origin", o)).ToList();
                        try
                        {
                            using var limit = CancellationTokenSource.CreateLinkedTokenSource(CallCt());
                            limit.CancelAfter(TimeSpan.FromSeconds(timeout));
                            await session.ConnectAsync(url!, cert, sent, transport.IgnoreServerCertificateErrors, transport, predicates.For(target.Host), limit.Token);
                            await session.CloseAsync(CancellationToken.None);
                            return (true, 101, null);
                        }
                        catch (Exception ex) when (ex is System.Net.WebSockets.WebSocketException or HttpRequestException or OperationCanceledException or InvalidOperationException)
                        {
                            return (false, session.HandshakeStatus, ex.Message);
                        }
                    }
                    var trustedWs = origins.FirstOrDefault(o => o != "null") is { } wsO
                        ? new Uri(wsO)
                        : new Uri((target.Scheme == "wss" ? "https://" : "http://") + target.Authority);
                    var probeList = audit ? CorsAudit.ProbesFor(trustedWs) : origins.Select(o => ("origin", o)).ToList();
                    var wsProbes = WebSocketOrigins.ProbeAsync(probeList, ConnectWs).GetAwaiter().GetResult();
                    var wsFindings = audit ? WebSocketOrigins.Grade(wsProbes) : null;
                    return new ToolResult(JsonSerializer.Serialize(new
                    {
                        url = MarkdownSecrets.RedactUrl(url!),
                        worst = wsFindings is null || wsFindings.Count == 0 ? null : wsFindings.Max(f => f.Severity).ToString().ToLowerInvariant(),
                        findings = wsFindings?.Select(f => new { severity = f.Severity.ToString().ToLowerInvariant(), title = f.Title, detail = f.Detail }),
                        probes = wsProbes.Select(p => new { kind = p.Kind, origin = p.Origin, accepted = p.Accepted, status = p.Status, error = p.Error })
                    }, new JsonSerializerOptions { WriteIndented = true }), false);
                }

                if (ApiClient.ValidateTransport(transport, url!) is { } transportProblem) return Err(transportProblem);
                // Every hop is checked against the allowlist too: a redirect must not walk the
                // certificate to a host the agent was never allowed to reach.
                Task<ApiResponse> CorsSend(ApiRequest r)
                {
                    if (Gate(r.Url) is { } hopRefused)
                        return Task.FromResult(new ApiResponse { Error = new ApiError(ApiErrorKind.Unknown, hopRefused) });
                    bool isPreflight = r.Method == HttpMethod.Options && r.Headers.Any(h => h.Key.Equals("Access-Control-Request-Method", StringComparison.OrdinalIgnoreCase));
                    return services.Client.SendAsync(r, preflightWithoutCert && isPreflight ? null : cert, transport,
                        predicates.For(TokenService.HostOf(r.Url)), cancellationToken: CallCt());
                }
                Task<ApiResponse> CorsSendNoCert(ApiRequest r) => Gate(r.Url) is { } hopRefused
                    ? Task.FromResult(new ApiResponse { Error = new ApiError(ApiErrorKind.Unknown, hopRefused) })
                    : services.Client.SendAsync(r, null, transport, predicates.For(TokenService.HostOf(r.Url)), cancellationToken: CallCt());

                if (audit)
                {
                    string baseOrigin = origins.FirstOrDefault(o => o != "null") ?? target.GetLeftPart(UriPartial.Authority);
                    bool includeDev = !CorsCommand.TargetIsPrivate(target);
                    Func<ApiRequest, Task<ApiResponse>>? auditNoCert = preflightWithoutCert ? CorsSendNoCert : null;
                    var auditResult = CorsAudit.RunAsync(url!, new Uri(baseOrigin), headerList, CorsSend,
                        TimeSpan.FromSeconds(timeout), includeDev, auditNoCert).GetAwaiter().GetResult();
                    string? suggestion = wantSuggest
                        ? CorsSuggest.For(new CorsNeeds(new[] { auditResult.BaseOrigin }, new[] { "GET" }, Array.Empty<string>(), Array.Empty<string>(), true, false), suggestFor, maxAge)
                        : null;
                    return new ToolResult(CorsCommand.AuditJson(auditResult, MarkdownSecrets.RedactUrl(url!), suggestion), false);
                }

                bool? actualMode = preflightOnly ? false : sendActual ? true : null;
                if (diffUrl is not null)
                {
                    var allDiffs = new List<CorsDiffEntry>();
                    foreach (var o in origins)
                        foreach (var m in methods)
                        {
                            var req = new CorsRequest(o, m, headerList, credentials) { PrivateNetwork = privateNetwork };
                            var left = CorsProbe.RunAsync(url!, req, body, type, actualMode, CorsSend, TimeSpan.FromSeconds(timeout)).GetAwaiter().GetResult();
                            var right = CorsProbe.RunAsync(diffUrl, req, body, type, actualMode, CorsSend, TimeSpan.FromSeconds(timeout)).GetAwaiter().GetResult();
                            allDiffs.AddRange(CorsDiff.Compare(req, left, right));
                        }
                    return new ToolResult(JsonSerializer.Serialize(new
                    {
                        leftUrl = MarkdownSecrets.RedactUrl(url!),
                        rightUrl = MarkdownSecrets.RedactUrl(diffUrl),
                        identical = allDiffs.Count == 0,
                        differences = allDiffs.Select(d => new { origin = d.Origin, method = d.Method, field = d.Field, left = d.Left, right = d.Right })
                    }, new JsonSerializerOptions { WriteIndented = true }), false);
                }

                var matrix = new List<(CorsRequest Req, CorsProbeResult Res)>();
                foreach (var o in origins)
                    foreach (var m in methods)
                    {
                        var corsRequest = new CorsRequest(o, m, headerList, credentials) { PrivateNetwork = privateNetwork };
                        var result = CorsProbe.RunAsync(url!, corsRequest, body, type, actualMode,
                            CorsSend, TimeSpan.FromSeconds(timeout)).GetAwaiter().GetResult();
                        matrix.Add((corsRequest, result));
                    }
                string? suggestText = wantSuggest
                    ? CorsSuggest.For(CorsNeeds.From(matrix.Select(x => (x.Req, (CorsProbeResult?)x.Res))), suggestFor, maxAge)
                    : null;
                if (matrix.Count == 1)
                    return new ToolResult(CorsCommand.Json(matrix[0].Res, matrix[0].Req, MarkdownSecrets.RedactUrl(url!), suggestText), false);
                return new ToolResult(JsonSerializer.Serialize(new
                {
                    results = matrix.Select(x => JsonSerializer.Deserialize<JsonElement>(CorsCommand.Json(x.Res, x.Req, MarkdownSecrets.RedactUrl(url!)))),
                    suggestion = suggestText
                }, new JsonSerializerOptions { WriteIndented = true }), false);
            })
        { Annotations = new ToolAnnotations(ReadOnlyHint: false, DestructiveHint: true, IdempotentHint: false, OpenWorldHint: true) };

        // ---- self_test ----
        var selfTest = new ToolDef("self_test",
            "Prove the mutual-TLS path end to end against a built-in loopback server.",
            JsonNode.Parse("""{"type":"object","additionalProperties":false,"properties":{}}""")!,
            _ =>
            {
                var result = new SelfTestRunner().RunAsync().GetAwaiter().GetResult();
                return new ToolResult(JsonSerializer.Serialize(new { passed = result.Passed, detail = result.Detail }), false);
            })
        {
            Annotations = new ToolAnnotations(ReadOnlyHint: true, IdempotentHint: true, OpenWorldHint: false),
            OutputSchema = JsonNode.Parse("""
                {"type":"object","properties":{"passed":{"type":"boolean"},"detail":{"type":"string"}}}
                """)!
        };

        // ---- doctor ----
        var doctorTool = new ToolDef("doctor",
            "Diagnose an mTLS/TLS connection stage by stage (URL, DNS, proxy, TCP, CONNECT tunnel, TLS handshake + server-accepted CAs, HTTP) for an allowed URL.",
            JsonNode.Parse("""
                {"type":"object","additionalProperties":false,"required":["url"],"properties":{
                  "url":{"type":"string","description":"https:// or http:// URL on an allowed host"},
                  "checkRevocation":{"type":"boolean","description":"Run online OCSP/CRL revocation check (default false)"}}}
                """)!,
            a =>
            {
                string? url = Str(a, "url");
                if (string.IsNullOrWhiteSpace(url)) return Err("url is required");
                if (Gate(url!) is { } refused) return Err(refused);
                var opts = McpTransport(new TransportOptions { IgnoreServerCertificateErrors = insecure });
                if (a.TryGetProperty("checkRevocation", out var revEl) && revEl.ValueKind == JsonValueKind.True && opts.Revocation == RevocationMode.None)
                    opts = opts with { Revocation = RevocationMode.Online };
                var storeCerts = new List<X509Certificate2>();
                try
                {
                    foreach (var ci in services.ListCertificates(includeLocalMachine))
                        if (services.FindCertificate(ci.Thumbprint) is { } found) storeCerts.Add(found);
                }
                catch { }
                var report = ConnectionDoctor.RunAsync(url!, cert, storeCerts, opts, probe: null, CallCt()).GetAwaiter().GetResult();
                var payload = new
                {
                    url = MarkdownSecrets.RedactUrl(report.Url),
                    ok = report.Ok,
                    failedStage = report.FirstFailure?.Name,
                    stages = report.Stages.Select(s => new
                    {
                        name = s.Name, ok = s.Ok, summary = s.Summary, detail = s.Detail,
                        advice = s.Advice, elapsedMs = (int)s.Elapsed.TotalMilliseconds
                    })
                };
                return new ToolResult(JsonSerializer.Serialize(payload), !report.Ok);
            })
        { Annotations = new ToolAnnotations(ReadOnlyHint: true, IdempotentHint: true, OpenWorldHint: true) };

        // ---- inspect_cert ----
        var inspectCert = new ToolDef("inspect_cert",
            "Inspect full X.509 certificate details (SANs, EKUs, key algorithm/size, CNG/hardware provider, expiry, chain) for a store certificate by thumbprint or subject.",
            JsonNode.Parse("""
                {"type":"object","additionalProperties":false,"required":["query"],"properties":{
                  "query":{"type":"string","description":"Certificate thumbprint or subject substring"},
                  "checkRevocation":{"type":"boolean"}}}
                """)!,
            a =>
            {
                string? query = Str(a, "query");
                if (string.IsNullOrWhiteSpace(query)) return Err("query is required");
                try
                {
                    var picked = CertPicker.Resolve(services.ListCertificates(includeLocalMachine), query!, TextWriter.Null);
                    bool checkRev = a.TryGetProperty("checkRevocation", out var revEl) && revEl.ValueKind == JsonValueKind.True;
                    var details = CertificateInspector.Inspect(picked.Certificate, checkRev);
                    return new ToolResult(JsonSerializer.Serialize(details), false);
                }
                catch (CliDataException ex) { return Err(ex.Message); }
            })
        { Annotations = new ToolAnnotations(ReadOnlyHint: true, IdempotentHint: true, OpenWorldHint: false) };

        // ---- inspect_token ----
        var inspectToken = new ToolDef("inspect_token",
            "Decode a JWT offline (header, claims, scopes, roles, issuer, audience, expiry) without verifying its signature or contacting any external service. Pass a JWT string or a stored workspace origin.",
            JsonNode.Parse("""
                {"type":"object","additionalProperties":false,"required":["token"],"properties":{
                  "token":{"type":"string","description":"Raw JWT string (optional Bearer prefix) or workspace origin URL"}}}
                """)!,
            a =>
            {
                string? raw = Str(a, "token");
                if (string.IsNullOrWhiteSpace(raw)) return Err("token is required");
                string toParse = raw!;
                if (!JwtInspector.LooksLikeJwt(toParse))
                {
                    string? wanted = TokenService.OriginOf(toParse);
                    var found = state.SessionTokens.FirstOrDefault(t =>
                        (wanted is not null && t.Origin.Equals(wanted, StringComparison.OrdinalIgnoreCase)) ||
                        t.Origin.Contains(toParse, StringComparison.OrdinalIgnoreCase));
                    if (found is null) return Err($"Not a JWT and no captured token matches '{toParse}'.");
                    toParse = found.Token;
                }
                if (!JwtInspector.TryParse(toParse, out var info, out var err))
                    return Err(err ?? "Could not parse JWT.");
                return new ToolResult(JsonSerializer.Serialize(info), false);
            })
        { Annotations = new ToolAnnotations(ReadOnlyHint: true, IdempotentHint: true, OpenWorldHint: false) };

        // ---- diff_known_good ----
        var diffKnownGood = new ToolDef("diff_known_good",
            "Run a saved request and compare its live response against its stored known-good baseline, returning structured status/header/JSON diffs.",
            JsonNode.Parse("""
                {"type":"object","additionalProperties":false,"required":["path"],"properties":{
                  "path":{"type":"string","description":"Saved request path (e.g. 'api/Get user')"},
                  "env":{"type":"string","description":"Optional environment name"},
                  "ignorePaths":{"type":"array","items":{"type":"string"},"description":"Optional JSON paths to ignore"}}}
                """)!,
            a =>
            {
                string? path = Str(a, "path");
                if (string.IsNullOrWhiteSpace(path)) return Err("path is required");
                try
                {
                    var targets = CliWorkspace.ResolveTargets(state, path, all: false);
                    if (targets.Count != 1) return Err($"'{path}' matched {targets.Count} requests; name one saved request.");
                    var (label, node) = targets[0];
                    if (node.KnownGood is null) return Err($"'{label}' has no stored known-good baseline.");
                    var envName = Str(a, "env");
                    var vars = new RunVariables(() => CliWorkspace.BuildVars(state, envName, Array.Empty<string>()));
                    string resolvedUrl = node.Request!.EffectiveUrl(s => VariableResolver.Resolve(s, vars.Current).Result);
                    if (Gate(resolvedUrl) is { } refused) return Err(refused);
                    var ctx = new RequestRunContext
                    {
                        State = state, Client = services.Client, Predicates = predicates,
                        FindCertificate = services.FindCertificate, DefaultCertificate = cert, AllowUrl = Gate,
                        NoAutoToken = noAutoToken, StrictVars = false,
                        Record = false, ShowRedirects = false, TransportOverride = McpTransport
                    };
                    var outcome = RequestRunner.RunAsync(label, node, ctx, vars, CallCt()).GetAwaiter().GetResult();
                    if (outcome.Response.Error is { } reqErr) return Err(reqErr.Message);
                    var ignorePaths = a.TryGetProperty("ignorePaths", out var ip) && ip.ValueKind == JsonValueKind.Array
                        ? ip.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList()
                        : new List<string>();
                    var diffOpts = new DiffOptions { IgnorePaths = ignorePaths, CompareHeaderValues = true };
                    var diff = ResponseDiff.Compare(node.KnownGood, ResponseSnapshot.From(outcome.Response), diffOpts);
                    return new ToolResult(JsonSerializer.Serialize(new
                    {
                        path = label, identical = diff.Identical, summary = DiffText.Format(diff)
                    }), false);
                }
                catch (CliDataException ex) { return Err(ex.Message); }
            })
        { Annotations = new ToolAnnotations(ReadOnlyHint: true, IdempotentHint: true, OpenWorldHint: true) };

        var tools = new[]
        {
            sendRequest, runSaved, runChain, listSaved, listEnvironments, listCerts,
            grpcList, grpcCall, wsExchange, sseRead, corsCheck, selfTest,
            doctorTool, inspectCert, inspectToken, diffKnownGood
        };
        return (tools, BuildResources(state));
    }

    /// <summary>The saved-request outcome as the tool's payload: the same envelope send_request
    /// returns, plus what `certapi run` would have reported — pass/fail, each assertion, each
    /// capture.</summary>
    private static ToolResult OutcomeResult(RequestOutcome outcome, IReadOnlyList<string> notes)
    {
        var envelope = JsonNode.Parse(
            SendCommand.BuildEnvelope(outcome.Response, includeBody: true, notes))!.AsObject();
        envelope["passed"] = outcome.Passed;
        if (outcome.Request.Assertions.Any(x => x.Enabled))
        {
            var results = outcome.Assertions ?? AssertionEvaluator.Evaluate(outcome.Request.Assertions, outcome.Response);
            envelope["assertions"] = new JsonArray(results.Select(r => (JsonNode)new JsonObject
            {
                ["description"] = r.Description, ["passed"] = r.Passed, ["actual"] = r.Actual
            }).ToArray());
        }
        if (outcome.Captures.Count > 0)
            envelope["captures"] = new JsonArray(outcome.Captures.Select(c => (JsonNode)new JsonObject
            {
                ["variable"] = c.Variable, ["ok"] = c.Ok, ["error"] = c.Error
            }).ToArray());
        return new ToolResult(envelope.ToJsonString(), IsError: outcome.Response.Error is not null);
    }

    private static ToolResult GrpcCallTool(
        JsonElement a, X509Certificate2? cert, GrpcDescriptorSet? protoset, TrustPredicates predicates,
        Func<string, string?> gate, Func<TransportOptions, TransportOptions> transport, CancellationToken callCt, int timeout)
    {
        string? address = Str(a, "address");
        string? methodSpec = Str(a, "method");
        if (string.IsNullOrWhiteSpace(address)) return Err("address is required");
        if (string.IsNullOrWhiteSpace(methodSpec)) return Err("method is required (Service/Method)");
        if (gate(address!) is { } refused) return Err(refused);

        int slash = methodSpec!.LastIndexOf('/');
        if (slash <= 0 || slash == methodSpec.Length - 1)
            return Err($"method expects Service/Method, got '{methodSpec}'");
        string servicePart = methodSpec[..slash], methodPart = methodSpec[(slash + 1)..];

        var messages = new List<string>();
        if (a.ValueKind == JsonValueKind.Object && a.TryGetProperty("data", out var data))
        {
            if (data.ValueKind == JsonValueKind.String) messages.Add(data.GetString()!);
            else if (data.ValueKind == JsonValueKind.Array)
                foreach (var item in data.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String) messages.Add(item.GetString()!);
                    else messages.Add(item.GetRawText());
                }
            else if (data.ValueKind == JsonValueKind.Object) messages.Add(data.GetRawText());
            else return Err("data must be a JSON message (string or object) or an array of them");
        }

        int maxMessages = 100;
        if (a.ValueKind == JsonValueKind.Object && a.TryGetProperty("maxMessages", out var mm))
        {
            if (mm.ValueKind != JsonValueKind.Number || !mm.TryGetInt32(out maxMessages) || maxMessages < 1)
                return Err("maxMessages expects a positive integer");
        }
        var metadata = ObjPairs(a, "metadata");
        foreach (var m in metadata)
        {
            if (m.Key.Equals("host", StringComparison.OrdinalIgnoreCase))
                return Err("metadata may not set 'host': the allow-list judges the address");
            if (HeaderValues.Problem(m.Key, m.Value) is { } valueProblem) return Err(valueProblem);
        }

        // The MCP server answers one call at a time: a gRPC server that accepts and never answers
        // would freeze every later call too, so each call has the --timeout deadline.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(callCt);
        deadline.CancelAfter(TimeSpan.FromSeconds(timeout));
        bool DeadlinePassed() => deadline.IsCancellationRequested && !callCt.IsCancellationRequested;
        // A stream cut off by the deadline still returns what it delivered, marked as cut off.
        ToolResult DeadlineWithPartial(IReadOnlyList<string> received) => new(
            GrpcStreamPayload(received, 4, "DEADLINE_EXCEEDED", $"stopped after {timeout} s (--timeout); the messages received until then are listed"),
            IsError: true);
        try
        {
            var caller = new GrpcCaller(new Uri(address!), cert, transport(new TransportOptions()),
                predicates.For(new Uri(address!).Host), protoset);
            try
            {
                var discovered = caller.DiscoverAsync(deadline.Token).GetAwaiter().GetResult();
                string sourceLabel = protoset is not null ? "the descriptor set declares" : "the server advertises";
                string service = GrpcCommand.ResolveServiceName(discovered, servicePart, sourceLabel);
                var svc = discovered.First(s => s.Name == service);
                var methodInfo = svc.Methods.FirstOrDefault(m => m.Name == methodPart);
                if (methodInfo is null)
                    return Err($"Method '{methodPart}' was not found on {service}. Methods: " +
                               (svc.Methods.Count == 0 ? "(none)" : string.Join(", ", svc.Methods.Select(m => m.Name))) + ".");

                if (!methodInfo.ClientStreaming && messages.Count > 1)
                    return Err($"{service}/{methodPart} is {GrpcCommand.KindName(false, methodInfo.ServerStreaming)} — it takes a single request message, but data carried {messages.Count}.");

                var ct = deadline.Token;
                if (!methodInfo.ClientStreaming && !methodInfo.ServerStreaming)
                {
                    var result = caller.InvokeAsync(service, methodPart, messages.Count == 1 ? messages[0] : "{}", metadata, ct)
                        .GetAwaiter().GetResult();
                    return GrpcResult(result, null);
                }
                if (!methodInfo.ClientStreaming)
                {
                    var received = new List<string>();
                    try
                    {
                        var stream = caller.InvokeStreamingAsync(service, methodPart, messages.Count == 1 ? messages[0] : "{}", metadata, ct);
                        var e = stream.GetAsyncEnumerator(ct);
                        try
                        {
                            while (received.Count < maxMessages && e.MoveNextAsync().AsTask().GetAwaiter().GetResult())
                                received.Add(e.Current);
                        }
                        finally { e.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
                    }
                    catch (GrpcStatusException ex)
                    {
                        return new ToolResult(GrpcStreamPayload(received, ex.StatusCode, ex.StatusName, ex.StatusDetail), IsError: true);
                    }
                    catch (OperationCanceledException) when (DeadlinePassed() && received.Count > 0)
                    {
                        return DeadlineWithPartial(received);
                    }
                    return new ToolResult(GrpcStreamPayload(received, 0, "OK", ""), IsError: false);
                }
                if (!methodInfo.ServerStreaming)
                {
                    var result = caller.InvokeClientStreamingAsync(service, methodPart, ToAsync(messages), metadata, ct)
                        .GetAwaiter().GetResult();
                    return GrpcResult(result, null);
                }
                var responses = new List<string>();
                try
                {
                    var duplex = caller.InvokeDuplexAsync(service, methodPart, ToAsync(messages), metadata,
                            onResponse: s => { lock (responses) responses.Add(s); return responses.Count < maxMessages; }, ct)
                        .GetAwaiter().GetResult();
                    return GrpcResult(duplex, responses);
                }
                catch (OperationCanceledException) when (DeadlinePassed() && responses.Count > 0)
                {
                    lock (responses) return DeadlineWithPartial(responses.ToList());
                }
            }
            finally { caller.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        }
        catch (OperationCanceledException) when (DeadlinePassed())
        { return Err($"the gRPC call did not finish within {timeout} s (--timeout)"); }
        catch (CliDataException ex) { return Err(ex.Message); }
        catch (GrpcReflectionUnavailableException ex) { return Err(ex.Message); }
        catch (GrpcMethodNotFoundException ex) { return Err(ex.Message); }
        catch (GrpcUnsupportedMethodException ex) { return Err(ex.Message); }
        catch (GrpcJsonException ex) { return Err(ex.Message); }
        catch (GrpcStatusException ex) { return Err(ex.Message); }
    }

    private static ToolResult GrpcResult(GrpcCallResult result, IReadOnlyList<string>? streamed)
    {
        var payload = new JsonObject
        {
            ["statusCode"] = result.StatusCode,
            ["statusName"] = result.StatusName,
            ["elapsedMs"] = (int)result.Elapsed.TotalMilliseconds
        };
        if (result.StatusDetail.Length > 0) payload["statusDetail"] = result.StatusDetail;
        if (streamed is not null)
            payload["messages"] = new JsonArray(streamed.Select(s => ParseOrString(s)).ToArray());
        else if (result.ResponseJson.Length > 0)
            payload["message"] = ParseOrString(result.ResponseJson);
        if (result.Trailers.Count > 0)
        {
            var trailers = new JsonObject();
            foreach (var t in result.Trailers) trailers[t.Key] = t.Value;
            payload["trailers"] = trailers;
        }
        return new ToolResult(payload.ToJsonString(), IsError: result.StatusCode != 0);
    }

    private static string GrpcStreamPayload(IReadOnlyList<string> received, int statusCode, string statusName, string statusDetail)
    {
        var payload = new JsonObject
        {
            ["statusCode"] = statusCode,
            ["statusName"] = statusName,
            ["messages"] = new JsonArray(received.Select(s => ParseOrString(s)).ToArray())
        };
        if (statusDetail.Length > 0) payload["statusDetail"] = statusDetail;
        return payload.ToJsonString();
    }

    private static JsonNode ParseOrString(string json)
    {
        try { return JsonNode.Parse(json) ?? (JsonNode)json; }
        catch (JsonException) { return json; }
    }

    private static async IAsyncEnumerable<string> ToAsync(IReadOnlyList<string> messages)
    {
        foreach (var m in messages) yield return m;
        await Task.CompletedTask;
    }

    /// <summary>"{{token}}" names a variable; it is not itself a secret.</summary>
    private static bool IsOnlyVariable(string value)
    {
        string v = value.Trim();
        if (v.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) v = v[7..].Trim();
        return v.StartsWith("{{", StringComparison.Ordinal) && v.EndsWith("}}", StringComparison.Ordinal) &&
               v.IndexOf("}}", StringComparison.Ordinal) == v.Length - 2;
    }

    private static List<(string Path, CollectionNode Node)> SavedLeaves(AppState state)
    {
        try { return CliWorkspace.ResolveTargets(state, null, all: true); }
        catch (CliDataException) { return new(); }   // empty collections
    }

    /// <summary>The workspace's read-only surfaces, addressable as MCP resources. Secrets never
    /// appear: a request's auth secret reads as a redaction marker, and a secret variable's value
    /// is withheld — the same stance `certapi export workspace` takes by default.</summary>
    internal static IReadOnlyList<ResourceDef> BuildResources(AppState state)
    {
        var resources = new List<ResourceDef>();

        foreach (var (path, node) in SavedLeaves(state))
        {
            var m = node.Request!;
            string uri = "certapi://requests/" + string.Join("/", path.Split('/').Select(Uri.EscapeDataString));
            resources.Add(new ResourceDef(uri, path, $"Saved request: {m.Method} {MarkdownSecrets.RedactUrl(m.EffectiveUrl())}",
                "application/json", () =>
                {
                    var request = new JsonObject
                    {
                        ["path"] = path,
                        ["method"] = m.Method,
                        // A credential typed into a header, a query (?api_key=) or a body field is
                        // withheld like the auth secret: the agent reading this is the transcript.
                        ["url"] = MarkdownSecrets.RedactUrl(m.EffectiveUrl()),
                        ["headers"] = new JsonArray(m.Headers
                            .Select(h => (JsonNode)new JsonObject
                            {
                                ["name"] = h.Name,
                                ["value"] = SecretHeaders.IsSecret(h.Name) && !string.IsNullOrEmpty(h.Value) && !IsOnlyVariable(h.Value) ? "(redacted)" : h.Value,
                                ["enabled"] = h.Enabled
                            }).ToArray()),
                        ["contentType"] = m.ContentType,
                        ["body"] = m.Body is null ? null : MarkdownSecrets.RedactBody(m.Body, m.ContentType),
                        ["authType"] = m.AuthType,
                        ["authSecret"] = string.IsNullOrEmpty(m.AuthSecret) ? null : "(redacted)",
                        ["timeoutSeconds"] = m.TimeoutSeconds,
                        ["assertions"] = new JsonArray(m.Assertions
                            .Select(x => JsonNode.Parse(JsonSerializer.Serialize(x))!).ToArray()),
                        ["captures"] = new JsonArray(m.Captures
                            .Select(x => JsonNode.Parse(JsonSerializer.Serialize(x))!).ToArray())
                    };
                    return request.ToJsonString();
                }));
        }

        foreach (var env in state.Environments)
        {
            string name = env.Name;
            string uri = "certapi://environments/" + Uri.EscapeDataString(name);
            resources.Add(new ResourceDef(uri, $"environment: {name}",
                $"Environment '{name}' — variable names; secret values withheld",
                "application/json", () =>
                {
                    var payload = new JsonObject
                    {
                        ["name"] = name,
                        ["active"] = env.Id == state.ActiveEnvironmentId,
                        ["variables"] = new JsonArray(env.Variables.Select(v => (JsonNode)new JsonObject
                        {
                            ["name"] = v.Key,
                            ["secret"] = v.Secret,
                            ["value"] = v.Secret ? "(secret — value withheld)" : v.Value
                        }).ToArray())
                    };
                    return payload.ToJsonString();
                }));
        }

        if (state.Chains.Count > 0)
            resources.Add(new ResourceDef("certapi://chains", "chains",
                "The workspace's saved chains, runnable with run_chain",
                "application/json", () =>
                {
                    var payload = new JsonObject
                    {
                        ["chains"] = new JsonArray(state.Chains.Select(c => (JsonNode)new JsonObject
                        {
                            ["name"] = c.Name,
                            ["environment"] = c.EnvironmentName,
                            ["steps"] = c.Steps.Count
                        }).ToArray())
                    };
                    return payload.ToJsonString();
                }));

        return resources;
    }

    private static ToolResult Err(string message) =>
        new(JsonSerializer.Serialize(new { error = message }), true);

    private static string? Str(JsonElement a, string name) =>
        a.ValueKind == JsonValueKind.Object && a.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    private static List<KeyValuePair<string, string>> ObjPairs(JsonElement a, string name)
    {
        var list = new List<KeyValuePair<string, string>>();
        if (a.ValueKind == JsonValueKind.Object && a.TryGetProperty(name, out var o) && o.ValueKind == JsonValueKind.Object)
            foreach (var p in o.EnumerateObject())
                if (p.Value.ValueKind == JsonValueKind.String) list.Add(new(p.Name, p.Value.GetString()!));
        return list;
    }

    private static List<string> ObjKeys(JsonElement a, string name)
    {
        var list = new List<string>();
        if (a.ValueKind == JsonValueKind.Object && a.TryGetProperty(name, out var o) && o.ValueKind == JsonValueKind.Object)
            foreach (var p in o.EnumerateObject())
                if (p.Value.ValueKind == JsonValueKind.String) list.Add($"{p.Name}={p.Value.GetString()}");
        return list;
    }
}
