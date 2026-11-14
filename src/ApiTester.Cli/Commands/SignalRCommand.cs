using System.Diagnostics;
using System.Net.Http;
using System.Text.Json.Nodes;
using ApiTester.Core;

namespace ApiTester.Cli.Commands;

public static class SignalRCommand
{
    public const string Help = """
        Usage: certapi signalr <hub-url> [options]

        Connects to an ASP.NET Core SignalR hub (or Azure SignalR Service) over WebSockets with the
        client certificate: negotiates, completes the JSON hub-protocol handshake, calls hub methods,
        and prints the events the hub sends. An Azure SignalR redirect (negotiate answering with a
        url and accessToken) is followed automatically.

        Calls:
          --invoke "Method [args]"  Call a hub method and wait for its result, which is printed as
                                    JSON (repeatable; run in order). Args are a JSON array, or one
                                    JSON value for a single argument: --invoke 'Send ["ann","hi"]'
          --send "Method [args]"    Call a hub method without waiting for a result (repeatable)
          --stream "Method [args]"  Call a streaming hub method; each item is printed as it arrives

        Listening:
          --on <event>              Print only these client events (repeatable; default: all)
          --count <n>               Stop after n events
          --seconds <n>             Stop after n seconds
          With --on, --count or --seconds, or with no calls at all, the command keeps listening until
          the limit, the hub closes, or Ctrl+C; otherwise it exits once every call has finished.

        Connection:
          --access-token <token>    Bearer token for negotiate and the WebSocket (what a browser
                                    client passes as accessTokenFactory)
          -H, --header "k: v"       Extra header on negotiate and the WebSocket handshake (repeatable)
          --skip-negotiation        Open the WebSocket straight away (a hub configured with
                                    SkipNegotiation, or a ws:// / wss:// hub URL)
          --json                    Print each hub record as raw JSON instead
          -q, --quiet               No connect/close notices on stderr

        TLS / certificates:
        """ + "\n" + CliCert.HelpLines + "\n" + """
          --insecure                Ignore server certificate errors

        """ + "\n" + TransportFlags.StreamHelp + "\n" + """

        Session:
          --workspace <file>        Read trust pins and captured tokens from a workspace file
          --no-auto-token           Don't attach a captured bearer token

        Global: --debug (verbose diagnostics) and --log-file <path> work here too.

        Examples:
          certapi signalr https://api.example.com/hubs/chat --cert "CN=My Client" --invoke 'SendMessage ["ann","hello"]' --on ReceiveMessage --count 1
          certapi signalr https://api.example.com/hubs/prices --on PriceChanged --seconds 30
          certapi signalr https://api.example.com/hubs/feed --stream 'Counter [5, 100]'

        Events go to stdout as "Event arg1 arg2" (each argument as JSON); notices go to stderr. Exit 0
        when every call succeeded and the connection closed cleanly, 1 otherwise.
        """;

    public static int Run(Args args, TextReader input, TextWriter stdout, TextWriter stderr, CliServices services)
    {
        var invokes = args.Values("--invoke");
        var sends = args.Values("--send");
        var streams = args.Values("--stream");
        var on = new HashSet<string>(args.Values("--on"), StringComparer.OrdinalIgnoreCase);
        var headers = args.Values("-H", "--header");
        string? accessToken = args.Value("--access-token");
        bool skipNegotiation = args.Flag("--skip-negotiation");
        bool json = args.Flag("--json");
        bool quiet = args.Flag("-q", "--quiet");
        string store = args.Value("--store") ?? services.Profile?.Store ?? "CurrentUser";
        bool insecure = args.Switch("--insecure", services.Profile?.Insecure);
        string? workspace = args.Value("--workspace");
        bool noAutoToken = args.Flag("--no-auto-token");
        int? count = PositiveInt(args.Value("--count"), "--count");
        int? seconds = PositiveInt(args.Value("--seconds"), "--seconds");
        var transport = TransportFlags.ParseStreamSubset(args, insecure, environment: null, services.Profile);
        var cert = CliCert.Resolve(args, store, services, stderr);

        var positionals = args.Positionals();
        if (positionals.Count != 1) throw new CliUsageException(Help);
        if (!Uri.TryCreate(positionals[0], UriKind.Absolute, out var hub) ||
            hub.Scheme is not ("http" or "https" or "ws" or "wss"))
            throw new CliUsageException($"signalr needs an http(s):// or ws(s):// hub URL, got '{positionals[0]}'.");
        if (hub.Scheme is "ws" or "wss") skipNegotiation = true;

        var calls = new List<(string Kind, string Target, JsonArray Args)>();
        void AddCalls(IEnumerable<string> specs, string kind)
        {
            foreach (var spec in specs)
            {
                try
                {
                    var (target, arguments) = SignalRProtocol.ParseCall(spec);
                    calls.Add((kind, target, arguments));
                }
                catch (FormatException ex) { throw new CliUsageException($"--{kind} '{spec}': {ex.Message}"); }
            }
        }
        AddCalls(invokes, "invoke");
        AddCalls(sends, "send");
        AddCalls(streams, "stream");

        var headerPairs = new List<KeyValuePair<string, string>>();
        foreach (var raw in headers)
        {
            int colon = raw.IndexOf(':');
            if (colon <= 0) throw new CliUsageException($"Header must be \"Name: value\", got '{raw}'.");
            headerPairs.Add(new(raw[..colon].Trim(), raw[(colon + 1)..].Trim()));
        }

        var state = CliWorkspace.Load(workspace, services.LiveStatePath, stderr);
        var predicates = new TrustPredicates(state);
        if (accessToken is null && !noAutoToken)
        {
            var probe = new List<KeyValuePair<string, string>>();
            if (TokenService.AutoAttach(state, hub.ToString(), probe, out _) is not null)
            {
                var auth = probe.FirstOrDefault(h => h.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase));
                if (auth.Value is { } v && v.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                {
                    accessToken = v["Bearer ".Length..];
                    if (!quiet) stderr.WriteLine($"using captured token for {hub.Host}");
                }
            }
        }

        bool listen = on.Count > 0 || count is not null || seconds is not null || calls.Count == 0;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(services.Cancel);
        if (seconds is { } s) cts.CancelAfter(TimeSpan.FromSeconds(s));

        return RunAsync().GetAwaiter().GetResult();

        async Task<int> RunAsync()
        {
            Uri wsUri;
            try
            {
                wsUri = skipNegotiation ? SignalRProtocol.WebSocketUri(hub, null) : await NegotiateAsync();
            }
            catch (CliDataException ex)
            {
                stderr.WriteLine("error: " + ex.Message);
                return ExitCodes.Failure;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
            {
                stderr.WriteLine("error: negotiate failed: " + ex.Message);
                return ExitCodes.Failure;
            }

            var wsHeaders = new List<KeyValuePair<string, string>>(headerPairs);
            if (accessToken is not null) wsHeaders.Add(new("Authorization", "Bearer " + accessToken));

            await using var session = new WebSocketSession();
            try
            {
                if (!quiet) stderr.WriteLine($"connecting to {Redact(wsUri)} …");
                await session.ConnectAsync(wsUri.ToString(), cert, wsHeaders, insecure, transport,
                    predicates.For(wsUri.Host), cts.Token);
            }
            catch (Exception ex) when (ex is System.Net.WebSockets.WebSocketException or HttpRequestException
                                          or InvalidOperationException or OperationCanceledException)
            {
                stderr.WriteLine("error: " + ex.Message);
                return ExitCodes.Failure;
            }

            var sendLock = new SemaphoreSlim(1, 1);
            async Task SendAsync(string record)
            {
                await sendLock.WaitAsync(CancellationToken.None);
                try { await session.SendTextAsync(record, CancellationToken.None); }
                finally { sendLock.Release(); }
            }

            await SendAsync(SignalRProtocol.Handshake);

            var pending = new Dictionary<string, (string Kind, string Target)>();
            int nextId = 0, events = 0;
            bool failed = false, handshaken = false, started = false;
            var watch = Stopwatch.StartNew();

            // Keep the connection alive: the server drops a client it has not heard from in 30 s.
            using var pingTimer = new PeriodicTimer(TimeSpan.FromSeconds(15));
            _ = Task.Run(async () =>
            {
                try
                {
                    while (await pingTimer.WaitForNextTickAsync(cts.Token))
                        await SendAsync(SignalRProtocol.PingRecord);
                }
                catch { /* stopped */ }
            });

            async Task StartCallsAsync()
            {
                foreach (var (kind, target, arguments) in calls)
                {
                    string? id = kind == "send" ? null : (++nextId).ToString(System.Globalization.CultureInfo.InvariantCulture);
                    if (id is not null) pending[id] = (kind, target);
                    await SendAsync(SignalRProtocol.InvocationRecord(target, arguments, id, stream: kind == "stream"));
                    if (!quiet) stderr.WriteLine($"> {target} {arguments.ToJsonString()}");
                }
            }

            bool Done() => handshaken && pending.Count == 0 && (!listen || (count is { } c && events >= c));

            try
            {
                await foreach (var message in session.ReceiveAllAsync(cts.Token))
                {
                    if (message.IsClose)
                    {
                        if (!quiet) stderr.WriteLine("the hub closed the connection.");
                        break;
                    }
                    if (!message.IsText) continue;
                    foreach (var record in SignalRProtocol.Split(message.Text))
                    {
                        if (!handshaken)
                        {
                            handshaken = true;
                            if (SignalRProtocol.HandshakeError(record) is { } refused)
                            {
                                stderr.WriteLine("error: the hub refused the handshake: " + refused);
                                return ExitCodes.Failure;
                            }
                            if (!quiet) stderr.WriteLine("connected.");
                            continue;
                        }
                        var m = SignalRProtocol.Parse(record);
                        if (m.Type == SignalRProtocol.Ping) continue;
                        if (json) { stdout.WriteLine(record); stdout.Flush(); }

                        switch (m.Type)
                        {
                            case SignalRProtocol.Invocation:
                                // A hub asking this client for a result would otherwise wait forever.
                                if (m.InvocationId is { } asked)
                                    await SendAsync(SignalRProtocol.CompletionErrorRecord(asked, "certapi has no client method handlers"));
                                if (on.Count > 0 && !on.Contains(m.Target ?? "")) break;
                                events++;
                                if (!json) { stdout.WriteLine(SignalRProtocol.Describe(m)); stdout.Flush(); }
                                break;
                            case SignalRProtocol.StreamItem:
                                if (!json) { stdout.WriteLine(m.Item?.ToJsonString() ?? "null"); stdout.Flush(); }
                                break;
                            case SignalRProtocol.Completion:
                                if (m.InvocationId is { } id && pending.Remove(id, out var call))
                                {
                                    if (m.Error is { } error)
                                    {
                                        failed = true;
                                        stderr.WriteLine($"error: {call.Target}: {error}");
                                    }
                                    else if (!json && call.Kind == "invoke")
                                    {
                                        stdout.WriteLine(m.Result?.ToJsonString() ?? "null");
                                        stdout.Flush();
                                    }
                                }
                                break;
                            case SignalRProtocol.Close:
                                if (m.Error is { } why)
                                {
                                    failed = true;
                                    stderr.WriteLine("error: the hub closed the connection: " + why);
                                }
                                else if (!quiet) stderr.WriteLine("the hub closed the connection.");
                                await session.CloseAsync(CancellationToken.None);
                                return Finish();
                        }
                    }
                    if (handshaken && !started)
                    {
                        started = true;
                        await StartCallsAsync();
                    }
                    if (Done()) break;
                }
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                if (!quiet && services.Cancel.IsCancellationRequested) stderr.WriteLine("cancelled.");
            }

            if (pending.Count > 0)
            {
                failed = true;
                foreach (var (_, call) in pending)
                    stderr.WriteLine($"error: {call.Target} did not complete");
            }
            try { await SendAsync(SignalRProtocol.CloseRecord); } catch { /* already gone */ }
            await session.CloseAsync(CancellationToken.None);
            if (session.Fault is { } fault && !cts.IsCancellationRequested)
            {
                stderr.WriteLine("error: the connection broke: " + fault);
                return ExitCodes.Failure;
            }
            return Finish();

            int Finish()
            {
                if (!quiet)
                    stderr.WriteLine($"closed after {watch.Elapsed.TotalSeconds:F1} s ({events} event{(events == 1 ? "" : "s")}).");
                return failed ? ExitCodes.Failure : ExitCodes.Ok;
            }
        }

        async Task<Uri> NegotiateAsync()
        {
            var current = hub;
            for (int hop = 0; hop < 5; hop++)
            {
                using var handler = StreamTransport.CreateHandler(cert, transport, predicates.For(current.Host), current);
                handler.AllowAutoRedirect = false;
                using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(100) };
                using var request = new HttpRequestMessage(HttpMethod.Post, SignalRProtocol.NegotiateUri(current))
                {
                    Content = new ByteArrayContent(Array.Empty<byte>())
                };
                foreach (var h in headerPairs) request.Headers.TryAddWithoutValidation(h.Key, h.Value);
                if (accessToken is not null) request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + accessToken);
                using var response = await client.SendAsync(request, cts.Token);
                string body = await response.Content.ReadAsStringAsync(cts.Token);
                services.Log.Debug($"negotiate {current} → {(int)response.StatusCode}");
                if (!response.IsSuccessStatusCode)
                    throw new CliDataException(
                        $"negotiate answered {(int)response.StatusCode} {response.ReasonPhrase}" +
                        ((int)response.StatusCode == 404 ? " — is this a SignalR hub URL? (a hub that skips negotiation needs --skip-negotiation)" : ""));
                var n = SignalRProtocol.ParseNegotiate(body);
                if (n.Error is { } error) throw new CliDataException("negotiate refused: " + error);
                if (n.RedirectUrl is { } redirect)
                {
                    current = new Uri(redirect);
                    if (n.AccessToken is not null) accessToken = n.AccessToken;
                    if (!quiet) stderr.WriteLine($"redirected to {current.GetLeftPart(UriPartial.Path)}");
                    continue;
                }
                if (n.Transports.Count > 0 && !n.Transports.Contains("WebSockets", StringComparer.OrdinalIgnoreCase))
                    throw new CliDataException(
                        $"the hub does not offer WebSockets (it offers {string.Join(", ", n.Transports)}); certapi signalr speaks WebSockets only.");
                return SignalRProtocol.WebSocketUri(current, n.ConnectionToken ?? n.ConnectionId);
            }
            throw new CliDataException("negotiate redirected more than 5 times.");
        }
    }

    private static string Redact(Uri uri) => uri.GetLeftPart(UriPartial.Path);

    private static int? PositiveInt(string? raw, string flag)
    {
        if (raw is null) return null;
        if (!int.TryParse(raw, out var n) || n < 1)
            throw new CliUsageException($"{flag} expects a positive number, got '{raw}'.");
        return n;
    }
}
