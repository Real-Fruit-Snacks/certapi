using ApiTester.Core;

namespace ApiTester.Cli.Commands;

public static class ConnectionsCommand
{
    public const string Help = """
        Usage: certapi connections <url> [-n <count>] [--parallel <count>] [transport options]

        Answers "am I actually reusing connections?" — by making the requests and reporting which
        connection each one went out on.

        Reusing a pooled connection skips a TCP handshake and a TLS handshake, which on a remote
        endpoint is most of the time a small request takes. Whether it is happening is normally
        invisible: the responses look identical either way. This makes it visible.

        The report lists the connections opened to that URL's origin, with protocol version, peer
        address, when each opened and how many requests went over it — then says plainly whether
        reuse is working. More requests than connections means it is. (Connections this process
        made to other origins are counted in a closing line but not mixed into the answer.)

        Common causes when it is not: the server answers 'Connection: close', a proxy sits in the
        way, or each request is built with a fresh client.

        Options:
          -n, --count <count>     How many requests to send (default 4)
          --parallel <count>      Send this many at a time (default 1, one after another).
                                  Parallel requests need one connection each, so a pool that is
                                  working still shows several — the useful number is connections
                                  compared with requests, not connections alone
          --json                  Print the report as JSON instead of text

        Certificate and transport options work here exactly as for `certapi send`:

          --insecure              Ignore server certificate errors (a host pinned with
                                  `certapi trust add` needs no --insecure)
        """ + "\n" + CliCert.HelpLines + "\n" + TransportFlags.Help + "\n" + """
        Global: --debug (verbose diagnostics) and --log-file <path> work here too.

        Examples:
          certapi connections https://api.example.com/health
          certapi connections https://api.internal/orders --cert "CN=My Client" -n 10
          certapi connections https://api.example.com/health -n 8 --parallel 4

        Exit 0 when the requests were made (whatever they answered), 3 when none could be sent,
        2 usage.
        """;

    public static int Run(Args args, TextWriter stdout, TextWriter stderr, CliServices services)
    {
        bool json = args.Flag("--json");
        int count = ParseCount(args.Value("-n", "--count"), "-n", 4);
        int parallel = ParseCount(args.Value("--parallel"), "--parallel", 1);
        if (parallel > count) parallel = count;

        var transportOverrides = TransportFlags.Parse(args, out _, environment: null, services.Profile);
        bool insecure = args.Switch("--insecure", services.Profile?.Insecure);
        string store = args.Value("--store") ?? services.Profile?.Store ?? "CurrentUser";
        var cert = CliCert.Resolve(args, store, services, stderr);

        var positionals = args.Positionals();
        if (positionals.Count != 1) throw new CliUsageException(Help);
        string url = positionals[0];
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed) ||
            (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
            throw new CliUsageException($"'{url}' is not an absolute http(s) URL.");

        var transport = transportOverrides.ApplyTo(
            new TransportOptions { IgnoreServerCertificateErrors = insecure });

        // Started before the first request, so every connection these requests open is observed
        // from its own beginning — a connection that predates it is reported as such rather than
        // given an invented age.
        using var inspector = new ConnectionInspector();

        var request = new ApiRequest { Method = HttpMethod.Get, Url = url };
        int sent = 0, failed = 0;
        string? firstError = null;

        for (int batch = 0; batch < count; batch += parallel)
        {
            int size = Math.Min(parallel, count - batch);
            var inFlight = new List<Task<ApiResponse>>(size);
            for (int i = 0; i < size; i++)
                inFlight.Add(services.Client.SendAsync(request, cert,
                    transport: transport,
                    cancellationToken: services.Cancel));

            foreach (var task in inFlight)
            {
                ApiResponse response;
                try { response = task.GetAwaiter().GetResult(); }
                catch (Exception ex) { failed++; firstError ??= ex.Message; continue; }

                // A transport error means the request never completed, so it is not a send. An HTTP
                // error status is — a 500 still travelled over a connection, which is the subject
                // here — so only Error, never IsSuccess, decides this.
                if (response.Error is not null) { failed++; firstError ??= response.Error.Message; }
                else sent++;
            }
        }

        if (sent == 0)
        {
            stderr.WriteLine($"could not send any request to {url}"
                           + (firstError is null ? "" : $": {firstError}"));
            return 3;
        }

        // Narrowed to the origin under test: the listener sees the whole process, and this
        // command was asked about one URL.
        string origin = ConnectionInspector.OriginOf(parsed);
        var connections = inspector.Connections
            .Where(c => string.Equals(c.Origin, origin, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (json)
        {
            stdout.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
            {
                url,
                requested = count,
                sent,
                failed,
                connections = connections.Select(c => new
                {
                    id = c.Id,
                    origin = c.Origin,
                    version = c.Version,
                    peer = c.RemoteAddress,
                    openedAtMs = Math.Round(c.EstablishedAt.TotalMilliseconds, 1),
                    requests = c.Requests,
                }),
                reusing = connections.Length > 0 && connections.Sum(c => c.Requests) > connections.Length,
            }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }

        stdout.Write(inspector.Render(origin));
        if (failed > 0)
            stderr.WriteLine($"note: {failed} of {sent + failed} attempt(s) failed"
                           + (firstError is null ? "" : $" — first error: {firstError}")
                           + ". A failed request may still have opened a connection.");
        return 0;
    }

    private static int ParseCount(string? raw, string flag, int fallback)
    {
        if (raw is null) return fallback;
        if (!int.TryParse(raw, out int value) || value < 1)
            throw new CliUsageException($"{flag} needs a positive whole number.");
        return value;
    }
}
