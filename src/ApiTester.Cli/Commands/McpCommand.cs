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
        list_saved, list_environments, list_certificates, grpc_list, grpc_call, self_test. Saved
        requests, environments, and chains are also published as read-only MCP resources.
        Configure your MCP host to launch this command.

          --cert <thumb|subject>  Certificate all tools use (pinned; the agent can't change it)
          --store <location>      CurrentUser (default); LocalMachine searches both stores
          --cert-file <path>      Pin a certificate from a file (.pfx/.p12 or .pem/.crt) instead
          --cert-password <pw>    Password for a .pfx/.p12 certificate file
          --key-file <path>       Private-key file for a PEM certificate whose key is separate
          --cert-issuer <substr>  Narrow --cert to certificates issued by a matching CA (or use alone)
          --cert-serial <hex>     Pick the certificate with this serial number (or use alone)
          --cert-pin <pin>        Smart-card / HSM PIN for the key, supplied ahead of time so a
                                  headless job is not stopped by the prompt. Prefer CERTAPI_CERT_PIN,
                                  which keeps it out of the shell history. Windows (CNG) only
          --allow <host>          Allowed upstream host (repeatable); a URL must match or be a
                                  subdomain of one. Omit to allow any host (prints a warning).
                                  Enforced per request — a chain step whose URL comes from an
                                  earlier capture is still checked.
          --insecure              Ignore upstream server-certificate errors (internal CAs)
          --timeout <seconds>     send_request and gRPC call deadline (default 100; a saved
                                  request keeps its own saved timeout)
          --workspace <file>      Load saved requests / environments / chains from a workspace
                                  file. The workspace is read once at launch; captures and tokens
                                  live in memory for the session and are never written back.
          --no-auto-token         Don't capture/reuse bearer tokens across the session's calls
          --protoset <file>       Compiled descriptor set for grpc_list/grpc_call against a
                                  server without reflection (produce it with protoc's
                                  descriptor_set_out and include_imports options). Pinned at
                                  launch; the agent cannot name files.

        The transport options apply to every call the tools make, with the same rules as `send`.
        """ + "\n\n" + TransportFlags.Help + "\n\n" + """
        Redirects are never followed (the redirect options above are accepted and change nothing): a 3xx comes back as itself, so every hop an agent takes is
        an explicit call checked against the allowlist. A host pinned with `certapi trust add`
        is reachable without --insecure, exactly as it is for send/run.

        Tokens returned by one tool call (e.g. a login via send_request) are captured in
        memory for this session and attached to later calls to the same host. Values captured
        by run_saved / run_chain resolve {{variables}} in later calls the same way.

        Global: --debug (verbose diagnostics) and --log-file <path> work here too.

        Examples:
          certapi mcp --cert "CN=Agent Client" --allow api.example.com
          certapi mcp --cert 4A8823… --allow api.example.com --allow auth.example.com --insecure
          certapi mcp --cert "CN=Agent Client" --allow api.example.com --workspace .\suite.json
          certapi mcp --cert "CN=Agent Client" --allow grpc.internal --protoset .\contracts.protoset

        Speaks JSON-RPC 2.0 over stdin/stdout; diagnostics go to stderr. Stop with Ctrl+C or by
        closing stdin. Exit 0 clean shutdown, 2 usage, 3 data error.
        """;

    public static int Run(Args args, TextReader input, TextWriter stdout, TextWriter stderr, CliServices services)
    {
        string store = args.Value("--store") ?? services.Profile?.Store ?? "CurrentUser";
        var allowHosts = args.Values("--allow");
        bool insecure = args.Switch("--insecure", services.Profile?.Insecure);
        string? timeoutRaw = args.Value("--timeout");
        string? workspace = args.Value("--workspace");
        bool noAutoToken = args.Flag("--no-auto-token");
        string? protosetPath = args.Value("--protoset");
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

        var allow = new HostAllowlist(allowHosts);
        stderr.WriteLine($"certapi mcp ready · cert: {cert?.Subject ?? "none"} · " +
            (allowHosts.Count == 0 ? "allow: ANY HOST (no --allow given)" : "allow: " + string.Join(", ", allowHosts)));

        var notifier = new McpNotifier();
        var (tools, resources) = Build(cert, allow, insecure, timeout, localMachine, workspace,
            noAutoToken, services, stderr, overrides, protoset, notifier);
        var server = new McpServer(tools, Version(), resources, notifier);
        server.Run(input, stdout, stderr, services.Cancel);
        return ExitCodes.Ok;
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

    internal static (IReadOnlyList<ToolDef> Tools, IReadOnlyList<ResourceDef> Resources) Build(
        X509Certificate2? cert, HostAllowlist allow, bool insecure, int timeout,
        bool includeLocalMachine, string? workspace, bool noAutoToken, CliServices services,
        TextWriter? stderr, TransportOverrides overrides, GrpcDescriptorSet? protoset,
        McpNotifier? notifier)
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
            var options = overrides.ApplyTo(baseline) with { FollowRedirects = false };
            if (insecure) options = options with { IgnoreServerCertificateErrors = true };
            return options;
        }

        string? Gate(string url) => allow.IsAllowed(url) ? null : $"host for '{url}' is not allowed";

        RequestRunContext MakeContext(List<string> notes) => new()
        {
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
                    cancellationToken: services.Cancel)
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
                {"type":"object","required":["url"],"properties":{
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
            JsonNode.Parse("""{"type":"object","properties":{"filter":{"type":"string"}}}""")!,
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
            JsonNode.Parse("""{"type":"object","properties":{}}""")!,
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
            JsonNode.Parse("""{"type":"object","properties":{}}""")!,
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
                {"type":"object","required":["path"],"properties":{
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
                var vars = new RunVariables(() => CliWorkspace.BuildVars(state, Str(a, "env"), ObjKeys(a, "vars")));
                var outcome = RequestRunner
                    .RunAsync(leaves[0].Path, leaves[0].Node, MakeContext(notes), vars, services.Cancel)
                    .GetAwaiter().GetResult();
                return OutcomeResult(outcome, notes);
            })
        { Annotations = new ToolAnnotations(ReadOnlyHint: false, DestructiveHint: true, IdempotentHint: false, OpenWorldHint: true) };

        // ---- run_chain ----
        var runChain = new ToolDef("run_chain",
            "Run a saved chain by name: its requests in order as one unit, each step seeing what earlier steps captured. A failing step stops the chain unless it is marked to carry on.",
            JsonNode.Parse("""
                {"type":"object","required":["name"],"properties":{
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
                var vars = new RunVariables(() => CliWorkspace.BuildVars(state, envName, ObjKeys(a, "vars")));
                var result = ChainRunner
                    .RunAsync(steps, MakeContext(notes), vars, progress: null, services.Cancel)
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
            JsonNode.Parse("""{"type":"object","properties":{"address":{"type":"string","description":"https:// or http:// gRPC endpoint on an allowed host"}}}""")!,
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
                        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(services.Cancel);
                        deadline.CancelAfter(TimeSpan.FromSeconds(timeout));
                        try { discovered = caller.DiscoverAsync(deadline.Token).GetAwaiter().GetResult(); }
                        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !services.Cancel.IsCancellationRequested)
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
                {"type":"object","required":["address","method"],"properties":{
                  "address":{"type":"string","description":"https:// or http:// gRPC endpoint on an allowed host"},
                  "method":{"type":"string","description":"Service/Method; a short service name resolves when unambiguous"},
                  "data":{"description":"The request message as JSON, or an array of messages for a streaming request (default {})"},
                  "metadata":{"type":"object","additionalProperties":{"type":"string"}},
                  "maxMessages":{"type":"integer","description":"Stop a streaming response after this many messages (default 100)"}}}
                """)!,
            a => GrpcCallTool(a, cert, protoset, predicates, Gate, McpTransport, services, timeout))
        { Annotations = new ToolAnnotations(ReadOnlyHint: false, DestructiveHint: true, IdempotentHint: false, OpenWorldHint: true) };

        // ---- self_test ----
        var selfTest = new ToolDef("self_test",
            "Prove the mutual-TLS path end to end against a built-in loopback server.",
            JsonNode.Parse("""{"type":"object","properties":{}}""")!,
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

        var tools = new[]
        {
            sendRequest, runSaved, runChain, listSaved, listEnvironments, listCerts,
            grpcList, grpcCall, selfTest
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
            var results = AssertionEvaluator.Evaluate(outcome.Request.Assertions, outcome.Response);
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
        Func<string, string?> gate, Func<TransportOptions, TransportOptions> transport, CliServices services, int timeout)
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

        // The MCP server answers one call at a time: a gRPC server that accepts and never answers
        // would freeze every later call too, so each call has the --timeout deadline.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(services.Cancel);
        deadline.CancelAfter(TimeSpan.FromSeconds(timeout));
        bool DeadlinePassed() => deadline.IsCancellationRequested && !services.Cancel.IsCancellationRequested;
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
