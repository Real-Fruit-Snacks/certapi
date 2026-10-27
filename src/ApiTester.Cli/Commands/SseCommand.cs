using ApiTester.Core;

namespace ApiTester.Cli.Commands;

public static class SseCommand
{
    public const string Help = """
        Usage: certapi sse <url> [options]

        Streams Server-Sent Events (text/event-stream) from <url>, printing each event as it
        arrives. Runs until the server ends the stream, --max-events is reached, or Ctrl+C.

        Options:
          -H, --header "k: v"     Add a request header (repeatable)
          --max-events <n>        Stop after n events
          --json                  Print one JSON object per event (ndjson): {event,data,id,retry}
          -q, --quiet             Don't print the connecting/ended notices on stderr

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
          --insecure              Ignore server certificate errors; a host pinned with
                                  `certapi trust add` needs no --insecure

        """ + "\n" + TransportFlags.StreamHelp + "\n" + """

        Session:
          --workspace <file>      Read trust pins and captured tokens from a workspace file
                                  instead of the live state
          --no-auto-token         Don't attach a captured bearer token to the request

        Global: --debug and --log-file <path> work here too.

        Examples:
          certapi sse https://api.example.com/events --cert "CN=My Client"
          certapi sse https://api.example.com/stream --max-events 5 --json

        Events go to stdout; notices go to stderr. Exit 0 when the stream ends, 1 on transport errors.
        """;

    public static int Run(Args args, TextWriter stdout, TextWriter stderr, CliServices services)
    {
        var headers = args.Values("-H", "--header");
        string store = args.Value("--store") ?? services.Profile?.Store ?? "CurrentUser";
        bool insecure = args.Switch("--insecure", services.Profile?.Insecure);
        bool json = args.Flag("--json");
        bool quiet = args.Flag("-q", "--quiet");
        string? workspace = args.Value("--workspace");
        bool noAutoToken = args.Flag("--no-auto-token");
        var transport = TransportFlags.ParseStreamSubset(args, insecure, environment: null, services.Profile);
        string? maxRaw = args.Value("--max-events");
        int? maxEvents = null;
        if (maxRaw is not null)
        {
            if (!int.TryParse(maxRaw, out var m) || m <= 0)
                throw new CliUsageException($"--max-events expects a positive number, got '{maxRaw}'.");
            maxEvents = m;
        }
        // Resolve the certificate before Positionals() so its options aren't seen as leftovers.
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

        if (!quiet) stderr.WriteLine($"connecting to {url} …");
        services.Log.Debug($"SSE GET {MarkdownSecrets.RedactUrl(url)} · cert {(cert is null ? "none" : cert.Subject)} · insecure {insecure}");

        return StreamAsync().GetAwaiter().GetResult();

        async Task<int> StreamAsync()
        {
            int count = 0;
            try
            {
                await foreach (var ev in SseClient.StreamAsync(url, cert, headerPairs, insecure,
                                   transport, predicates.For(new Uri(url).Host), services.Cancel))
                {
                    if (json)
                        stdout.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
                        {
                            @event = ev.Event, data = ev.Data, id = ev.Id, retry = ev.Retry
                        }));
                    else
                    {
                        if (ev.Event is not null) stdout.WriteLine($"event: {ev.Event}");
                        stdout.WriteLine(ev.Data);
                    }
                    stdout.Flush();
                    count++;
                    if (maxEvents is int max && count >= max) break;
                }
                if (!quiet) stderr.WriteLine($"stream ended ({count} event{(count == 1 ? "" : "s")}).");
                return ExitCodes.Ok;
            }
            catch (OperationCanceledException) when (services.Cancel.IsCancellationRequested)
            {
                if (!quiet) stderr.WriteLine($"cancelled ({count} event{(count == 1 ? "" : "s")} received).");
                return ExitCodes.Ok;
            }
            catch (System.Net.Http.HttpRequestException ex)
            {
                stderr.WriteLine("error: " + ex.Message);
                return ExitCodes.Failure;
            }
        }
    }
}
