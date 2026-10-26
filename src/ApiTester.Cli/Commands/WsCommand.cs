using ApiTester.Core;

namespace ApiTester.Cli.Commands;

public static class WsCommand
{
    public const string Help = """
        Usage: certapi ws <url> [options]

        Opens a WebSocket connection to <url> (ws:// or wss://). Each --message and each line read
        from stdin is sent as a text message; every message received from the server is printed to
        stdout. The client then listens until the server closes, --expect messages arrive, or Ctrl+C.

        Options:
          -m, --message <text>    Send this text after connecting (repeatable)
          -H, --header "k: v"     Add a handshake request header (repeatable)
          --expect <n>            Stop after receiving n messages (good for scripts and pipes)
          -q, --quiet             Don't print the connect/send/close notices on stderr

        TLS / certificates:
          --cert <thumb|subject>  Client certificate from the Windows store
          --store <location>      CurrentUser (default); LocalMachine searches both stores
          --cert-file <path>      Client certificate from a file (.pfx/.p12 or .pem/.crt)
          --cert-password <pw>    Password for a .pfx/.p12 certificate file
          --key-file <path>       Private-key file for a PEM cert whose key is in a separate file
          --cert-issuer <substr>  Narrow --cert to certificates issued by a matching CA (or use alone)
          --cert-serial <hex>     Pick the certificate with this serial number (or use alone)
          --cert-pin <pin>        Smart-card / HSM PIN for the key, supplied ahead of time so a
                                  headless job is not stopped by the prompt. Prefer CERTAPI_CERT_PIN,
                                  which keeps it out of the shell history. Windows (CNG) only
          --insecure              Ignore server certificate errors (wss); a host pinned with
                                  `certapi trust add` needs no --insecure

        """ + "\n" + TransportFlags.StreamHelp + "\n" + """

        Session:
          --workspace <file>      Read trust pins and captured tokens from a workspace file
                                  instead of the live state
          --no-auto-token         Don't attach a captured bearer token to the handshake

        Global: --debug and --log-file <path> work here too.

        Examples:
          certapi ws wss://api.example.com/socket --cert "CN=My Client" -m '{"sub":"prices"}' --expect 3
          echo '{"ping":1}' | certapi ws wss://api.example.com/socket --expect 1

        Received messages go to stdout; notices go to stderr. Exit 0 on a clean close, 1 on errors.
        """;

    public static int Run(Args args, TextReader input, TextWriter stdout, TextWriter stderr, CliServices services)
    {
        var messages = args.Values("-m", "--message");
        var headers = args.Values("-H", "--header");
        string store = args.Value("--store") ?? services.Profile?.Store ?? "CurrentUser";
        bool insecure = args.FlagOrNull("--insecure") ?? services.Profile?.Insecure ?? false;
        bool quiet = args.Flag("-q", "--quiet");
        string? workspace = args.Value("--workspace");
        bool noAutoToken = args.Flag("--no-auto-token");
        var transport = TransportFlags.ParseStreamSubset(args, insecure, environment: null, services.Profile);
        string? expectRaw = args.Value("--expect");
        int? expect = null;
        if (expectRaw is not null)
        {
            if (!int.TryParse(expectRaw, out var e) || e < 0)
                throw new CliUsageException($"--expect expects a non-negative number, got '{expectRaw}'.");
            expect = e;
        }
        var cert = CliCert.Resolve(args, store, services, stderr);

        var positionals = args.Positionals();
        if (positionals.Count != 1) throw new CliUsageException(Help);
        string url = positionals[0];

        var headerPairs = new List<KeyValuePair<string, string>>();
        foreach (var raw in headers)
        {
            int colon = raw.IndexOf(':');
            if (colon <= 0) throw new CliUsageException($"Header must be \"Name: value\", got '{raw}'.");
            headerPairs.Add(new(raw[..colon].Trim(), raw[(colon + 1)..].Trim()));
        }

        // Pins and captured tokens come from the same state file every other command reads.
        var state = CliWorkspace.Load(workspace, services.LiveStatePath, stderr);
        var predicates = new TrustPredicates(state);
        if (!noAutoToken && TokenService.AutoAttach(state, url, headerPairs, out _) is not null && !quiet)
            stderr.WriteLine($"using captured token for {TokenService.HostOf(url)}");

        // Outgoing messages: explicit --message values first, then any lines piped on stdin.
        var toSend = new List<string>(messages);
        string? line;
        while ((line = input.ReadLine()) is not null) toSend.Add(line);

        return RunAsync().GetAwaiter().GetResult();

        async Task<int> RunAsync()
        {
            await using var session = new WebSocketSession();
            try
            {
                if (!quiet) stderr.WriteLine($"connecting to {url} …");
                await session.ConnectAsync(url, cert, headerPairs, insecure, transport,
                    predicates.For(new Uri(url).Host), services.Cancel);
                if (!quiet) stderr.WriteLine("connected.");
            }
            catch (Exception ex) when (ex is System.Net.WebSockets.WebSocketException
                                          or System.Net.Http.HttpRequestException
                                          or UriFormatException or InvalidOperationException)
            {
                stderr.WriteLine("error: " + ex.Message);
                return ExitCodes.Failure;
            }

            foreach (var m in toSend)
            {
                await session.SendTextAsync(m, services.Cancel);
                if (!quiet) stderr.WriteLine($"> {m}");
            }

            int want = expect ?? int.MaxValue;   // no --expect ⇒ listen until close or Ctrl+C
            int got = 0;
            try
            {
                // --expect 0 means "send only" — don't wait for any reply.
                if (want > 0)
                    await foreach (var msg in session.ReceiveAllAsync(services.Cancel))
                    {
                        if (msg.IsClose)
                        {
                            if (!quiet) stderr.WriteLine("server closed the connection.");
                            break;
                        }
                        stdout.WriteLine(msg.IsText ? msg.Text : $"[binary {msg.Bytes.Length} bytes]");
                        stdout.Flush();
                        if (++got >= want) break;
                    }
            }
            catch (OperationCanceledException) when (services.Cancel.IsCancellationRequested)
            {
                if (!quiet) stderr.WriteLine("cancelled.");
            }

            await session.CloseAsync(CancellationToken.None);
            if (!quiet) stderr.WriteLine($"closed ({got} message{(got == 1 ? "" : "s")} received).");
            return ExitCodes.Ok;
        }
    }
}
