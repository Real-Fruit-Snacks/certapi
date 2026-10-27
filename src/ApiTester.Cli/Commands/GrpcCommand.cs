using System.Diagnostics;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Nodes;
using ApiTester.Core;
using ApiTester.Grpc;

namespace ApiTester.Cli.Commands;

/// <summary>The CLI surface over <see cref="GrpcCaller"/>: <c>certapi grpc list</c> discovers the
/// services and methods a server advertises via reflection, and <c>certapi grpc call</c> invokes
/// one of them — unary, server-streaming, client-streaming, or bidirectional, whichever kind the
/// method's own definition declares — using the same Windows-store certificate handling as every
/// other command. Either subcommand can take its services and methods from a compiled descriptor
/// set (--protoset) instead of a live server, for a server that does not implement reflection at
/// all. Every gRPC/Protobuf type stays inside ApiTester.Grpc — this class only ever sees the plain
/// records ApiTester.Grpc exposes.</summary>
public static class GrpcCommand
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public const string Help = """
        Usage: certapi grpc list <address> [options]
               certapi grpc list --protoset <file> [options]
               certapi grpc call <address> <Service/Method> [options]

        Calls a gRPC service (HTTP/2) that requires a client certificate, using the same Windows-
        store certificate handling as the rest of certapi. list shows the services and methods a
        server advertises via server reflection (or a compiled descriptor set — see --protoset
        below); call invokes one of them — unary, server-streaming, client-streaming, or
        bidirectional. Which of the four it is comes from the method's own definition, not from a
        flag: certapi discovers the kind and dispatches accordingly.

        Descriptors come from server reflection (grpc.reflection.v1alpha.ServerReflection) by
        default. A server that does not implement reflection — often deliberately, in production —
        can still be listed and called by supplying a compiled descriptor set with --protoset.
        certapi serve does not proxy gRPC — HttpListener is HTTP/1.1-only — so certapi grpc reaches
        the service directly with your certificate rather than going through the gateway.

        Request:
          -d, --data <json>       The request message as JSON (default {}, an empty message).
                                    Repeatable: for a client-streaming or bidirectional method, each
                                    -d value is sent as one message, in order; several -d values
                                    against a unary or server-streaming method (which take a single
                                    request message) is a usage error. -d @file reads one
                                    message from a file.
          --data-file <path>      Read the request JSON from a file instead of -d; supplies one
                                    message, the same as a single -d
          -H, --header "k: v"     Request metadata (repeatable)
          --max-messages <n>      Stop a server-streaming or bidirectional call after n messages
          --timeout <seconds>     Default 100

        For a client-streaming or bidirectional method, each line read from standard input is also
        sent as one message (one JSON object per line) — after every -d value and any --data-file
        message, in that order. Standard input is read only for those two kinds; a unary or
        server-streaming call never consults it, so piping something into one never hangs the call
        or changes its request. Supplying no messages at all (no -d, no --data-file, empty or
        absent stdin) is legal for a client-streaming or bidirectional method — it sends an empty
        stream, not an error.

        Descriptors (for a server that does not implement reflection):
          --protoset <file>       Read the service definitions from a compiled descriptor set — a
                                    FileDescriptorSet — instead of asking the server. Produce one
                                    with:
                                      protoc --descriptor_set_out=<file> --include_imports <proto>
                                    (--include_imports is not optional: without it the set is
                                    missing the types it imports.) A descriptor set wins over
                                    server reflection when both are available. grpc list --protoset
                                    needs no address and opens no connection at all; an address
                                    given there is ignored.

        TLS / certificates:
        """ + "\n" + CliCert.HelpLines + "\n" + """
          --insecure               Ignore server certificate errors

        Transport (a gRPC channel is HTTP/2 by definition, so HTTP-version pinning, redirects,
        decompression, and retries do not apply and have no flags here):
          --proxy <url>            Route through this proxy (e.g. http://proxy.corp:8080)
          --no-proxy               Ignore the system/PAC proxy
          --proxy-user <u:pass>    Proxy credentials
          --noproxy <list>         Hosts that bypass the proxy, comma-separated, NO_PROXY-style
                                   (internal.corp, .corp, *.corp, 10.0.0.0/8, *; :port pins a port);
                                   defaults to the NO_PROXY environment variable
          --revocation <mode>      Check whether the server's certificate has been revoked by its
                                   issuer: none (default), offline (cached certificate revocation
                                   lists (CRLs) only), or online (may fetch a fresh list or query an
                                   Online Certificate Status Protocol (OCSP) responder). A status
                                   that cannot be determined is reported and is not fatal unless
                                   --revocation-strict is also given.
          --revocation-strict      Make an undeterminable revocation status fatal (needs
                                   --revocation offline or online). --insecure still overrides
                                   both, and says so.

        Automatic tokens:
          A bearer token captured by an earlier certapi send to the same host is attached
          automatically as metadata; certapi grpc uses a captured token but never captures a new
          one.
          --no-auto-token          Do not attach a captured bearer token for this call
          --workspace <file>       Load pins and tokens from a workspace file instead of the live
                                    state

        Output:
          --json                   Print a JSON envelope instead of the plain rendering
          -q, --quiet              No metadata line on stderr

        Global: --debug (verbose diagnostics) and --log-file <path> work here too.

        Well-known Protobuf types (google.protobuf.Timestamp, Duration, Struct, Any, the wrapper
        types) render and are supplied in their canonical JSON forms — a Timestamp renders as an
        RFC 3339 string ("2023-11-14T22:13:20Z"), not {"seconds":"5","nanos":0}. An unresolved
        Any degrades visibly ({"@type":...,"value":"<base64>"}) rather than failing the call.

        Examples:
          (Examples use PowerShell quoting; in cmd.exe write JSON bodies as "{\"user\":\"me\"}".)

          certapi grpc list https://api.example.com:5001 --cert "CN=My Client"
          certapi grpc call https://api.example.com:5001 my.pkg.Greeter/SayHello -d '{"name":"Ada"}'
          certapi grpc call https://api.example.com:5001 Greeter/SayHello --json
          certapi grpc call https://api.example.com:5001 my.pkg.Feed/Watch --max-messages 5
          certapi grpc list --protoset api.protoset
          certapi grpc call https://api.example.com:5001 my.pkg.Greeter/SayHello --protoset api.protoset
          certapi grpc call https://api.example.com:5001 my.pkg.Uploader/Collect -d '{"item":"a"}' -d '{"item":"b"}' -d '{"item":"c"}'
          Get-Content items.jsonl | certapi grpc call https://api.example.com:5001 my.pkg.Chat/Talk

        list prints services to stdout, one per line, indented with their methods (stream marks a
        streaming request or response); call prints the response — or, for a server-streaming or a
        bidirectional method, one compact JSON object per line as each message arrives — to stdout.
        Everything else goes to stderr. Exit 0 on success (including a stream stopped early by
        --max-messages), 1 when the gRPC status is not OK, 2 on a bad command line (including
        several -d/--data values against a method that takes a single request message), 3 on a data
        problem (reflection unavailable, an unknown service/method/field, or a descriptor set that
        is missing, unreadable, not a compiled FileDescriptorSet, or missing part of its dependency
        closure).
        """;

    public static int Run(Args args, TextReader input, TextWriter stdout, TextWriter stderr, CliServices services)
    {
        // ---- bind options (every one, even those a given subcommand ignores — Positionals()
        // rejects anything option-shaped left over) ----
        var dataValues = args.Values("-d", "--data");
        string? dataFile = args.Value("--data-file");
        var headers = args.Values("-H", "--header");
        string? maxMessagesRaw = args.Value("--max-messages");
        string store = args.Value("--store") ?? services.Profile?.Store ?? "CurrentUser";
        bool insecure = args.Switch("--insecure", services.Profile?.Insecure);
        // The profile fills in what is not typed, exactly as on send/ws/sse: a corporate proxy set
        // once in the configuration must not be skipped by one command.
        string? proxyFlag = args.Value("--proxy");
        bool noProxyFlag = args.Flag("--no-proxy");
        string? proxyUrl = proxyFlag ?? (noProxyFlag ? null : services.Profile?.Proxy);
        bool noProxy = noProxyFlag || (proxyFlag is null && services.Profile?.NoProxy == true);
        string? proxyUser = args.Value("--proxy-user") ?? (proxyUrl is not null ? services.Profile?.ProxyUser : null);
        string? noProxySpec = args.ListValue("--noproxy") ?? (noProxyFlag ? null : services.Profile?.NoProxyList);
        string? revocationRaw = args.Value("--revocation") ?? services.Profile?.Revocation;
        bool revocationStrict = args.Switch("--revocation-strict", services.Profile?.RevocationStrict);
        string? timeoutRaw = args.Value("--timeout");
        string? workspace = args.Value("--workspace");
        bool noAutoToken = args.Flag("--no-auto-token");
        bool json = args.Flag("--json");
        bool quiet = args.Flag("-q", "--quiet");
        string? protoset = args.Value("--protoset");
        var cert = CliCert.Resolve(args, store, services, stderr);

        if (dataValues.Count > 0 && dataFile is not null)
            throw new CliUsageException("-d/--data and --data-file are mutually exclusive.");

        if (proxyFlag is not null && noProxyFlag)
            throw new CliUsageException("--proxy and --no-proxy are mutually exclusive.");
        string? proxyAuthUser = null, proxyAuthPassword = null;
        if (proxyUser is not null)
        {
            if (proxyUrl is null)
                throw new CliUsageException("--proxy-user needs --proxy (there is no proxy to authenticate to).");
            int userColon = proxyUser.IndexOf(':');
            if (userColon < 0)
                throw new CliUsageException($"--proxy-user expects user:password, got '{proxyUser}'.");
            proxyAuthUser = proxyUser[..userColon];
            proxyAuthPassword = proxyUser[(userColon + 1)..];
        }

        var (noProxyFromFlag, noProxyFromEnvironment) = TransportFlags.ResolveNoProxy(noProxySpec, proxyOff: noProxy);

        // Reuses TransportFlags' own strict parse (none/offline/online, case-insensitive, or a usage
        // error naming the offending value) rather than a second copy — two copies of this parse is
        // exactly the drift this release is about.
        RevocationMode revocation = revocationRaw is null ? RevocationMode.None : TransportFlags.ParseRevocation(revocationRaw);

        int? maxMessages = null;
        if (maxMessagesRaw is not null)
        {
            if (!int.TryParse(maxMessagesRaw, out var m) || m < 1)
                throw new CliUsageException($"--max-messages expects a positive number, got '{maxMessagesRaw}'.");
            maxMessages = m;
        }

        int timeout = 100;
        if (timeoutRaw is not null && (!int.TryParse(timeoutRaw, out timeout) || timeout <= 0))
            throw new CliUsageException($"--timeout expects a positive number of seconds, got '{timeoutRaw}'.");

        var positionals = args.Positionals();
        if (positionals.Count == 0) throw new CliUsageException(Help);
        string sub = positionals[0].ToLowerInvariant();

        string addressRaw = "";
        string? serviceMethodRaw = null;
        switch (sub)
        {
            case "list" when positionals.Count == 2:
                addressRaw = positionals[1];
                break;
            // Ruling 3: listing what a descriptor set declares needs no server at all, so the
            // address is optional in this one combination — and when one is supplied anyway it is
            // simply ignored, because there is nothing to ask it.
            case "list" when positionals.Count == 1 && protoset is not null:
                break;
            case "call" when positionals.Count == 3:
                addressRaw = positionals[1];
                serviceMethodRaw = positionals[2];
                break;
            default:
                throw new CliUsageException(Help);
        }

        // A descriptor-set listing must not open a socket at all (ruling 3), so this returns before
        // the address is even parsed and before any channel exists — an address supplied alongside
        // --protoset (see the case above) is accepted and ignored rather than dialed. This is also
        // why an unreachable or malformed address is not an error in this one combination: it is
        // never looked at.
        if (sub == "list" && protoset is not null)
        {
            services.Log.Debug($"grpc list from descriptor set {protoset}");
            var loadStopwatch = Stopwatch.StartNew();
            var set = LoadDescriptorSet(protoset);
            loadStopwatch.Stop();
            return RunList(set.Services, loadStopwatch.Elapsed, stdout, stderr, json, quiet);
        }

        // D4: an address whose scheme is not http/https is a usage error — checked here so the
        // user gets this message rather than an ArgumentException out of GrpcCaller's constructor.
        if (!Uri.TryCreate(addressRaw, UriKind.Absolute, out var address) ||
            (address.Scheme != Uri.UriSchemeHttp && address.Scheme != Uri.UriSchemeHttps))
        {
            string scheme = Uri.TryCreate(addressRaw, UriKind.Absolute, out var parsed)
                ? parsed.Scheme
                : "(not an absolute URI)";
            throw new CliUsageException(
                $"gRPC needs an absolute http or https address; got scheme '{scheme}' from '{addressRaw}' " +
                "(only http and https are accepted).");
        }

        var metadata = new List<KeyValuePair<string, string>>();
        foreach (var raw in headers)
        {
            int colon = raw.IndexOf(':');
            if (colon <= 0) throw new CliUsageException($"Header must be \"Name: value\", got '{raw}'.");
            metadata.Add(new(raw[..colon].Trim(), raw[(colon + 1)..].Trim()));
        }

        // Read once, here, before the channel is built — a missing --data-file is a data error the
        // user must see before any connection is even attempted. What this content means (the whole
        // request, or one message among several) depends on the discovered method's kind, resolved
        // later in RunCallAsync once methodInfo is known.
        string? dataFileContent = dataFile is null ? null : GrpcText(BodyFile.Read(dataFile, "--data-file"), dataFile);
        // -d @file reads one message from a file, as -d does on send.
        for (int i = 0; i < dataValues.Count; i++)
            if (dataValues[i] is { Length: > 1 } v && v[0] == '@')
                dataValues[i] = GrpcText(BodyFile.Read(v[1..], "-d " + v), v[1..]);

        // ---- workspace, per-site trust, and the automatic session token (mirrors SendCommand) ----
        var state = LoadWorkspaceOrEmpty(workspace, services, stderr);
        string host = TokenService.HostOf(addressRaw);
        Func<X509Certificate2?, bool> trustCert = c =>
            c is not null && TrustService.IsTrustedHere(state, host, c);

        if (!noAutoToken)
        {
            var used = TokenService.AutoAttach(state, addressRaw, metadata, out var expired);
            if (used is not null)
            {
                if (!quiet) stderr.WriteLine($"note: using captured token for {host}");
                services.Log.Debug($"auto token attached for {used.Origin} ({used.Source})");
            }
            else if (expired is not null && !quiet)
            {
                stderr.WriteLine($"note: the captured token for {host} has expired — sending without it");
            }
        }

        var transport = new TransportOptions
        {
            IgnoreServerCertificateErrors = insecure,
            Proxy = proxyUrl is not null ? ProxyMode.Explicit : noProxy ? ProxyMode.None : ProxyMode.System,
            ProxyUrl = proxyUrl,
            ProxyUser = proxyAuthUser,
            ProxyPassword = proxyAuthPassword,
            // Precedence: --noproxy wins when named, otherwise NO_PROXY from the environment — grpc
            // has no saved request to consult, so there is no third source here.
            NoProxy = noProxyFromFlag.Count > 0 ? noProxyFromFlag : noProxyFromEnvironment,
            Revocation = revocation,
            RevocationStrict = revocationStrict
        };

        services.Log.Debug($"grpc {sub} {address}");
        if (protoset is not null) services.Log.Debug($"descriptors: from {protoset} (reflection not consulted)");
        services.Log.Debug(cert is null ? "certificate: none" : $"certificate: {cert.Subject} ({cert.Thumbprint})");
        services.Log.Debug($"timeout: {timeout} s · insecure: {insecure} · store: {store}");
        foreach (var h in metadata)
            services.Log.Debug("metadata: " + (h.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase)
                ? $"{h.Key}: {TokenService.MaskAuthorization(h.Value)}" : $"{h.Key}: {h.Value}"));

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(services.Cancel);
        cts.CancelAfter(TimeSpan.FromSeconds(timeout));

        return RunAsync().GetAwaiter().GetResult();

        async Task<int> RunAsync()
        {
            // Loaded before the channel is built so a bad descriptor set is exit 3 with a message
            // about the file, rather than a connection failure against a server that was never the
            // problem. From here on, list and call are identical whichever source the descriptors
            // came from: caller.DiscoverAsync already returns the descriptor set's services without
            // a reflection round trip when one was supplied (ruling 2), so RunList/RunCallAsync below
            // never need to know whether --protoset was given at all — that is why this slice is small.
            var descriptorSet = protoset is null ? null : LoadDescriptorSet(protoset);
            await using var caller = new GrpcCaller(address, cert, transport, trustCert, descriptorSet);

            IReadOnlyList<GrpcServiceInfo> discovered;
            var discoverStopwatch = Stopwatch.StartNew();
            try
            {
                discovered = await caller.DiscoverAsync(cts.Token);
            }
            catch (GrpcReflectionUnavailableException ex)
            {
                // The library's message already names both the problem and the --protoset fix —
                // pass it through as-is rather than rewording it.
                throw new CliDataException(ex.Message);
            }
            catch (GrpcStatusException ex)
            {
                if (!quiet) stderr.WriteLine($"{ex.StatusName} ({ex.StatusCode}): {ex.StatusDetail}");
                return ExitCodes.Failure;
            }
            discoverStopwatch.Stop();
            services.Log.Debug(
                $"discovery: {discovered.Count} service(s) in {discoverStopwatch.Elapsed.TotalMilliseconds:F0} ms");

            if (sub == "list")
                return RunList(discovered, discoverStopwatch.Elapsed, stdout, stderr, json, quiet);

            // Ruling 2 wording: with --protoset, no server advertised anything — the descriptor set
            // declared it — so "not found" messages must say so rather than implying a server was
            // asked and answered.
            string sourceLabel = protoset is null ? "the server advertises" : "the descriptor set declares";

            try
            {
                return await RunCallAsync(caller, discovered, serviceMethodRaw!, sourceLabel, dataValues, dataFileContent,
                    input, metadata, maxMessages, stdout, stderr, json, quiet, services, cts.Token);
            }
            catch (GrpcMethodNotFoundException ex) { throw new CliDataException(ex.Message); }
            catch (GrpcJsonException ex) { throw new CliDataException(ex.Message); }
            catch (GrpcUnsupportedMethodException ex) { throw new CliUsageException(ex.Message); }
            catch (ArgumentException ex) { throw new CliUsageException(ex.Message); }
            // Belt-and-braces: the CLI normally resolves the service against the discovered list
            // first (ResolveServiceName, above RunCallAsync's dispatch), so in practice a descriptor
            // set that lacks the requested service is already caught there. This catch exists for a
            // descriptor-set lookup that still fails inside GrpcCaller itself (e.g. a method resolved
            // against the wrong service internally) — exit 3 either way, never a stack trace.
            catch (GrpcDescriptorSetException ex) { throw new CliDataException(ex.Message); }
        }
    }

    /// <summary>The single translation point from ApiTester.Grpc's descriptor-set errors to the
    /// CLI's exit 3: a missing file, an unreadable one, one that is not a compiled
    /// FileDescriptorSet (including the classic mistake of passing the .proto source), and one
    /// missing part of its dependency closure all arrive here already carrying a message written
    /// for the user, so this adds nothing and hides nothing.</summary>
    private static GrpcDescriptorSet LoadDescriptorSet(string path)
    {
        try { return GrpcDescriptorSet.Load(path); }
        catch (GrpcDescriptorSetException ex) { throw new CliDataException(ex.Message); }
    }

    /// <summary>Prints the discovered services/methods (plain or --json) to stdout, and the
    /// summary line to stderr unless -q.</summary>
    private static int RunList(
        IReadOnlyList<GrpcServiceInfo> discovered, TimeSpan elapsed,
        TextWriter stdout, TextWriter stderr, bool json, bool quiet)
    {
        if (json)
        {
            var payload = discovered.Select(s => new
            {
                service = s.Name,
                methods = s.Methods.Select(m => new
                {
                    name = m.Name,
                    clientStreaming = m.ClientStreaming,
                    serverStreaming = m.ServerStreaming,
                    inputType = m.InputType,
                    outputType = m.OutputType
                }).ToList()
            }).ToList();
            stdout.WriteLine(JsonSerializer.Serialize(payload, Indented));
        }
        else
        {
            foreach (var s in discovered)
            {
                stdout.WriteLine(s.Name);
                foreach (var m in s.Methods)
                {
                    string input = m.ClientStreaming ? $"stream {m.InputType}" : m.InputType;
                    string output = m.ServerStreaming ? $"stream {m.OutputType}" : m.OutputType;
                    stdout.WriteLine($"  {m.Name}({input}) returns ({output})");
                }
            }
        }

        if (!quiet)
        {
            int methodCount = discovered.Sum(s => s.Methods.Count);
            stderr.WriteLine(
                $"{discovered.Count} service{(discovered.Count == 1 ? "" : "s")} · " +
                $"{methodCount} method{(methodCount == 1 ? "" : "s")} · {elapsed.TotalMilliseconds:F0} ms");
        }
        return ExitCodes.Ok;
    }

    /// <summary>Splits Service/Method on the last '/', resolves the service name generously
    /// against what reflection discovered, finds the method, and dispatches on its discovered kind —
    /// unary, server-streaming, client-streaming, or bidirectional. The kind comes from the
    /// service's own definition; there is no flag to choose it. Several -d/--data values against a
    /// method whose input is a single message (unary or server-streaming) is a usage error naming
    /// the method and its actual kind.</summary>
    private static async Task<int> RunCallAsync(
        GrpcCaller caller, IReadOnlyList<GrpcServiceInfo> discovered, string serviceMethodRaw, string sourceLabel,
        IReadOnlyList<string> dataValues, string? dataFileContent, TextReader input,
        IReadOnlyList<KeyValuePair<string, string>> metadata, int? maxMessages,
        TextWriter stdout, TextWriter stderr, bool json, bool quiet, CliServices services, CancellationToken ct)
    {
        int slash = serviceMethodRaw.LastIndexOf('/');
        if (slash <= 0 || slash == serviceMethodRaw.Length - 1)
            throw new CliUsageException(
                $"'{serviceMethodRaw}' doesn't look like Service/Method — expected e.g. my.pkg.Greeter/SayHello.\n{Help}");

        string given = serviceMethodRaw[..slash];
        string methodName = serviceMethodRaw[(slash + 1)..];
        string service = ResolveServiceName(discovered, given, sourceLabel);
        var serviceInfo = discovered.First(s => s.Name == service);
        var methodInfo = serviceInfo.Methods.FirstOrDefault(m => m.Name == methodName);
        if (methodInfo is null)
        {
            string known = serviceInfo.Methods.Count == 0
                ? "(none)"
                : string.Join(", ", serviceInfo.Methods.Select(m => m.Name));
            throw new CliDataException(
                $"Method '{methodName}' was not found on service '{service}'. Methods that do exist: {known}.");
        }

        services.Log.Debug($"resolved to {service}/{methodName}");

        if (!methodInfo.ClientStreaming)
        {
            // Unary or server-streaming: the input is a single request message, exactly as today.
            if (dataValues.Count > 1)
            {
                string kind = KindName(methodInfo.ClientStreaming, methodInfo.ServerStreaming);
                throw new CliUsageException(
                    $"'{service}/{methodName}' is {kind} and takes a single request message, but " +
                    $"{dataValues.Count} -d/--data values were supplied. Only a client-streaming or " +
                    "bidirectional method takes several messages.");
            }
            string body = dataValues.Count == 1 ? dataValues[0] : dataFileContent ?? "{}";

            return methodInfo.ServerStreaming
                ? await RunStreamingAsync(caller, service, methodName, body, metadata, maxMessages, stdout, stderr, json, quiet, services, ct)
                : await RunUnaryAsync(caller, service, methodName, body, metadata, stdout, stderr, json, quiet, services, ct);
        }

        // Client-streaming or bidirectional: the input is a stream of messages. The messages sent
        // are, in order: every -d/--data value; then --data-file's contents as one message, if
        // given; then each non-blank line read from stdin — the same eager convention `certapi ws`
        // uses. Stdin is consulted ONLY here: a unary or server-streaming call never reads it, so
        // piping something into one of those can never hang the call or change its request.
        var messages = new List<string>(dataValues);
        if (dataFileContent is not null) messages.Add(dataFileContent);
        // Only piped input: at a terminal there is nothing to read to the end of, and the call
        // would wait for Ctrl+Z/Ctrl+D before doing anything (Ctrl+C cannot reach a blocked read).
        if (!ReferenceEquals(input, Console.In) || Console.IsInputRedirected)
        {
            string? line;
            while ((line = input.ReadLine()) is not null)
            {
                if (!string.IsNullOrWhiteSpace(line)) messages.Add(line);
            }
        }
        else if (messages.Count == 0)
            throw new CliUsageException($"{service}/{methodName} takes a stream of messages: give them with -d (repeatable), --data-file, or pipe one per line on stdin.");

        return methodInfo.ServerStreaming
            ? await RunDuplexAsync(caller, service, methodName, messages, metadata, maxMessages, stdout, stderr, json, quiet, services, ct)
            : await RunClientStreamingAsync(caller, service, methodName, messages, metadata, stdout, stderr, json, quiet, services, ct);
    }

    /// <summary>What each of the four method kinds is called — used both by the "several -d/--data
    /// values against a single-message method" usage error and by debug logging.</summary>
    internal static string KindName(bool clientStreaming, bool serverStreaming) =>
        (clientStreaming, serverStreaming) switch
        {
            (false, false) => "unary",
            (false, true) => "server-streaming",
            (true, false) => "client-streaming",
            (true, true) => "bidirectional",
        };

    /// <summary>Resolves a possibly-short service name against what was discovered — reflection, or
    /// a descriptor set when --protoset was given: an exact match wins; otherwise exactly one
    /// discovered name ending with "." + given wins (so "Echo/Unary" finds "certapi.test.Echo");
    /// several matches or none is a data error naming the candidates or the services actually
    /// available, worded via <paramref name="sourceLabel"/> so the message stays honest about where
    /// that list came from ("the server advertises" vs. "the descriptor set declares").</summary>
    internal static string ResolveServiceName(IReadOnlyList<GrpcServiceInfo> discovered, string given, string sourceLabel)
    {
        var exact = discovered.FirstOrDefault(s => s.Name == given);
        if (exact is not null) return exact.Name;

        var suffixMatches = discovered.Where(s => s.Name.EndsWith("." + given, StringComparison.Ordinal)).ToList();
        if (suffixMatches.Count == 1) return suffixMatches[0].Name;
        if (suffixMatches.Count > 1)
            throw new CliDataException(
                $"'{given}' matches more than one service: {string.Join(", ", suffixMatches.Select(s => s.Name))}.");

        string knownServices = discovered.Count == 0 ? "(none)" : string.Join(", ", discovered.Select(s => s.Name));
        throw new CliDataException($"Service '{given}' was not found. Services {sourceLabel}: {knownServices}.");
    }

    private static async Task<int> RunUnaryAsync(
        GrpcCaller caller, string service, string method, string body,
        IReadOnlyList<KeyValuePair<string, string>> metadata,
        TextWriter stdout, TextWriter stderr, bool json, bool quiet, CliServices services, CancellationToken ct)
    {
        var result = await caller.InvokeAsync(service, method, body, metadata, ct);
        services.Log.Debug($"elapsed: {result.Elapsed.TotalMilliseconds:F0} ms");
        return RenderCallResult(result, stdout, stderr, json, quiet, $"OK · {result.Elapsed.TotalMilliseconds:F0} ms");
    }

    /// <summary>Invokes a client-streaming method — every message in <paramref name="messages"/>,
    /// in order (an empty list is a legal empty stream) — and renders the single reply exactly like
    /// <see cref="RunUnaryAsync"/>'s: non-JSON prints the indented response, --json prints the
    /// envelope with "message", and a non-OK status is exit 1. Only the non-quiet stderr summary
    /// differs, naming how many messages were sent.</summary>
    private static async Task<int> RunClientStreamingAsync(
        GrpcCaller caller, string service, string method, IReadOnlyList<string> messages,
        IReadOnlyList<KeyValuePair<string, string>> metadata,
        TextWriter stdout, TextWriter stderr, bool json, bool quiet, CliServices services, CancellationToken ct)
    {
        var result = await caller.InvokeClientStreamingAsync(service, method, ToAsyncEnumerable(messages), metadata, ct);
        services.Log.Debug($"elapsed: {result.Elapsed.TotalMilliseconds:F0} ms · sent {messages.Count} message(s)");
        string plural = messages.Count == 1 ? "" : "s";
        return RenderCallResult(
            result, stdout, stderr, json, quiet,
            $"OK · sent {messages.Count} message{plural} · {result.Elapsed.TotalMilliseconds:F0} ms");
    }

    /// <summary>Renders one <see cref="GrpcCallResult"/> the way a single-reply call does (unary or
    /// client-streaming): non-JSON prints the indented response to stdout; --json prints the shared
    /// envelope with "message"; a non-OK status prints the envelope (when --json) plus
    /// "{StatusName} ({StatusCode}): {StatusDetail}" on stderr and returns exit 1; success prints
    /// <paramref name="successSummary"/> on stderr (unless -q) and returns exit 0.</summary>
    private static int RenderCallResult(
        GrpcCallResult result, TextWriter stdout, TextWriter stderr, bool json, bool quiet, string successSummary)
    {
        if (result.StatusCode != 0)
        {
            if (json)
                stdout.WriteLine(BuildCallEnvelope(
                    result.StatusCode, result.StatusName, result.StatusDetail, result.Elapsed.TotalMilliseconds,
                    result.Trailers, message: null, messages: null));
            if (!quiet) stderr.WriteLine($"{result.StatusName} ({result.StatusCode}): {result.StatusDetail}");
            return ExitCodes.Failure;
        }

        if (json)
            stdout.WriteLine(BuildCallEnvelope(
                0, "OK", "", result.Elapsed.TotalMilliseconds, result.Trailers,
                message: JsonNode.Parse(result.ResponseJson), messages: null));
        else
            stdout.WriteLine(result.ResponseJson);

        if (!quiet) stderr.WriteLine(successSummary);
        return ExitCodes.Ok;
    }

    /// <summary>Wraps an already-collected list of messages as the <c>IAsyncEnumerable&lt;string&gt;</c>
    /// GrpcCaller's client-streaming and bidirectional entry points need — the CLI reads -d/--data and
    /// stdin eagerly (mirroring `certapi ws`), so there is never a genuinely asynchronous source of
    /// messages here.</summary>
    private static async IAsyncEnumerable<string> ToAsyncEnumerable(IReadOnlyList<string> messages)
    {
        await Task.Yield();
        foreach (var message in messages) yield return message;
    }

    /// <summary>Non-JSON: one compact JSON object per line, flushed as each arrives, so a slow
    /// stream is visible as it happens. JSON: the lines are collected instead and rendered as one
    /// "messages" array in the envelope at the end, per the --json contract. Either way, stopping
    /// at --max-messages is success (exit 0); a non-OK status ending the stream is exit 1, with
    /// whatever already reached stdout left in place.</summary>
    private static async Task<int> RunStreamingAsync(
        GrpcCaller caller, string service, string method, string body,
        IReadOnlyList<KeyValuePair<string, string>> metadata, int? maxMessages,
        TextWriter stdout, TextWriter stderr, bool json, bool quiet, CliServices services, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        List<JsonNode?>? collected = json ? new List<JsonNode?>() : null;
        int count = 0;
        bool truncated = false;

        try
        {
            await foreach (var message in caller.InvokeStreamingAsync(service, method, body, metadata, ct))
            {
                count++;
                if (json) collected!.Add(JsonNode.Parse(message));
                else { stdout.WriteLine(message); stdout.Flush(); }

                if (maxMessages is int max && count >= max) { truncated = true; break; }
            }
        }
        catch (GrpcStatusException ex)
        {
            stopwatch.Stop();
            services.Log.Debug($"elapsed: {stopwatch.Elapsed.TotalMilliseconds:F0} ms · {count} message(s)");
            if (json)
                stdout.WriteLine(BuildCallEnvelope(
                    ex.StatusCode, ex.StatusName, ex.StatusDetail, stopwatch.Elapsed.TotalMilliseconds,
                    ex.Trailers, message: null, messages: collected));
            if (!quiet) stderr.WriteLine($"{ex.StatusName} ({ex.StatusCode}): {ex.StatusDetail}");
            return ExitCodes.Failure;
        }

        stopwatch.Stop();
        services.Log.Debug($"elapsed: {stopwatch.Elapsed.TotalMilliseconds:F0} ms · {count} message(s)");

        if (json)
            stdout.WriteLine(BuildCallEnvelope(
                0, "OK", "", stopwatch.Elapsed.TotalMilliseconds, Array.Empty<KeyValuePair<string, string>>(),
                message: null, messages: collected));

        if (!quiet)
        {
            string plural = count == 1 ? "" : "s";
            stderr.WriteLine(truncated
                ? $"stopped after {count} message{plural} (--max-messages) · {stopwatch.Elapsed.TotalMilliseconds:F0} ms"
                : $"OK · {count} message{plural} · {stopwatch.Elapsed.TotalMilliseconds:F0} ms");
        }
        return ExitCodes.Ok;
    }

    /// <summary>Invokes a bidirectional-streaming method: <paramref name="messages"/> is sent while
    /// responses print as they arrive, concurrently — the send and receive sides never wait on each
    /// other (that concurrency is GrpcCaller.InvokeDuplexAsync's job, not this method's). The sink
    /// mirrors <see cref="RunStreamingAsync"/>'s loop body: non-JSON writes each compact line to
    /// stdout and flushes immediately; --json collects each message instead, rendered as the
    /// envelope's "messages" array at the end. Reaching --max-messages stops the call early, which
    /// is success (exit 0), exactly as it is for a server-streaming call; a non-OK status is exit 1,
    /// with whatever already reached stdout left in place and, with --json, the envelope carrying
    /// both the status and the partial "messages" array.</summary>
    private static async Task<int> RunDuplexAsync(
        GrpcCaller caller, string service, string method, IReadOnlyList<string> messages,
        IReadOnlyList<KeyValuePair<string, string>> metadata, int? maxMessages,
        TextWriter stdout, TextWriter stderr, bool json, bool quiet, CliServices services, CancellationToken ct)
    {
        List<JsonNode?>? collected = json ? new List<JsonNode?>() : null;
        int count = 0;
        bool truncated = false;

        bool OnResponse(string message)
        {
            count++;
            if (json) collected!.Add(JsonNode.Parse(message));
            else { stdout.WriteLine(message); stdout.Flush(); }

            if (maxMessages is int max && count >= max) { truncated = true; return false; }
            return true;
        }

        var result = await caller.InvokeDuplexAsync(
            service, method, ToAsyncEnumerable(messages), metadata, OnResponse, ct);
        services.Log.Debug($"elapsed: {result.Elapsed.TotalMilliseconds:F0} ms · {count} message(s)");

        if (result.StatusCode != 0)
        {
            if (json)
                stdout.WriteLine(BuildCallEnvelope(
                    result.StatusCode, result.StatusName, result.StatusDetail, result.Elapsed.TotalMilliseconds,
                    result.Trailers, message: null, messages: collected));
            if (!quiet) stderr.WriteLine($"{result.StatusName} ({result.StatusCode}): {result.StatusDetail}");
            return ExitCodes.Failure;
        }

        if (json)
            stdout.WriteLine(BuildCallEnvelope(
                0, "OK", "", result.Elapsed.TotalMilliseconds, result.Trailers,
                message: null, messages: collected));

        if (!quiet)
        {
            string plural = count == 1 ? "" : "s";
            stderr.WriteLine(truncated
                ? $"stopped after {count} message{plural} (--max-messages) · {result.Elapsed.TotalMilliseconds:F0} ms"
                : $"OK · {count} message{plural} · {result.Elapsed.TotalMilliseconds:F0} ms");
        }
        return ExitCodes.Ok;
    }

    /// <summary>The shared --json envelope shape for a call, unary or streaming, success or
    /// failure: {status, statusName, detail, elapsedMs, trailers} always, plus "message" (a single
    /// nested object) or "messages" (an array) only when there is one to show.</summary>
    private static string BuildCallEnvelope(
        int status, string statusName, string detail, double elapsedMs,
        IReadOnlyList<KeyValuePair<string, string>> trailers, JsonNode? message, List<JsonNode?>? messages)
    {
        var obj = new Dictionary<string, object?>
        {
            ["status"] = status,
            ["statusName"] = statusName,
            ["detail"] = detail,
            ["elapsedMs"] = Math.Round(elapsedMs),
            ["trailers"] = TrailersToObject(trailers)
        };
        if (message is not null) obj["message"] = message;
        if (messages is not null) obj["messages"] = messages;
        return JsonSerializer.Serialize(obj, Indented);
    }

    private static Dictionary<string, string> TrailersToObject(IReadOnlyList<KeyValuePair<string, string>> trailers)
    {
        var obj = new Dictionary<string, string>();
        foreach (var t in trailers) obj[t.Key] = t.Value;
        return obj;
    }

    /// <summary>Deliberately duplicates SendCommand's private helper of the same shape rather than
    /// sharing it: SendCommand's copy is private, and widening its visibility would be an unrelated
    /// change to a file this slice does not own. A missing explicit --workspace file is a data
    /// error (there is nothing for grpc to create — unlike send's --capture, grpc never writes to
    /// the state); a corrupt *live* state (no --workspace given) warns on stderr and continues with
    /// an empty state, the same tolerance the GUI itself uses.</summary>
    private static AppState LoadWorkspaceOrEmpty(string? workspace, CliServices services, TextWriter stderr)
    {
        if (workspace is null)
        {
            try { return CliWorkspace.Load(null, services.LiveStatePath, stderr); }
            catch (CliDataException ex)
            {
                string? kept = AppState.PreserveUnreadable(services.LiveStatePath);
                stderr.WriteLine($"warning: could not read the live state ({ex.Message}) — continuing without saved tokens/environments" +
                                 (kept is null ? "" : $"; the file was kept as {kept}"));
                return new AppState();
            }
        }
        return CliWorkspace.Load(workspace, services.LiveStatePath, stderr);
    }

    /// <summary>A gRPC message is JSON, so a body file must be text.</summary>
    private static string GrpcText(BodyFile file, string path) =>
        file.Text ?? throw new CliDataException($"{path} is not UTF-8 text; a gRPC message is given as JSON.");
}
