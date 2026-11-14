using System.Security.Cryptography.X509Certificates;
using ApiTester.Core;

namespace ApiTester.Cli.Commands;

public static class MockCommand
{
    public const string Help = """
        Usage: certapi mock [options]

        Run a local test server you can fire requests at — the standing counterpart to selftest.
        It echoes each request back as JSON (method, path, query, headers, body, and, under mTLS,
        the client certificate you presented) and serves a few fixed routes:

          /                 echoes the request (any method, any path)
          /status/<code>    responds with that HTTP status (e.g. /status/404)
          /sse              a short text/event-stream (try it with certapi sse)
          /token            an OAuth 2.0 token response (try it with certapi token)
          /windows-auth     a 401 NTLM challenge, then success (try it with certapi send --windows-auth)
          /cookie-auth      sets a session cookie, then reports authenticated once you send it back
          (Upgrade)         a WebSocket echo on any path (try it with certapi ws)

        Options:
          --port <n>        Port to listen on (default 8770; 0 picks a free port)
          --http            Plain HTTP (default) — hit it with anything, no certificates
          --tls             HTTPS with a generated self-signed server certificate
          --mtls            HTTPS that also requires a client certificate (any cert is accepted)
          --cert-dir <dir>  Where to write generated certificates (default ./certapi-mock-certs)
          -q, --quiet       Don't log each request
          --tls-mode <m>    Serve a deliberately broken server certificate, so a client's own
                            error paths can be exercised from a terminal: valid (default),
                            expired, wrong-host, or self-signed. Needs --tls or --mtls
          --routes <file>   Serve the routes declared in a JSON scenario file instead of the
                            built-in ones: each route says what it matches (method, path glob or
                            regular expression, required query and headers) and what it answers
                            (status, headers, inline body or bodyFile). Matched top to bottom,
                            first match winning. With --har as well, a request the routes miss
                            falls through to the recording
          --har <file>      Replay a captured HTTP Archive (HAR) instead of the built-in routes:
                            each request is answered with the recorded response for that method
                            and path (query included when it disambiguates), in recorded order
          --no-match-status <code>
                            Status for a request that matches nothing in the archive (default 404)
          --cors [<origins>]
                            Behave like a server with a CORS policy: answer preflights (204, or
                            403 for an origin not allowed) and put Access-Control-* headers on
                            every response to a page, credentials allowed and every header
                            exposed. With no value any origin is allowed; a comma-separated list
                            (repeatable) allows only those, and any other origin gets no CORS
                            headers, which a browser blocks. Without --cors the built-in routes
                            answer Access-Control-Allow-Origin: * as before

        With --tls / --mtls the server certificate (and, for --mtls, a ready-to-use client .pfx) are
        written to the cert dir so you can trust/present them. Runs until Ctrl+C.

        With --har, replay mode turns a captured session into an offline fake backend: point your
        app, a test suite, or a teammate's client at the mock and it answers with the recorded
        responses instead of live traffic. The built-in echo routes are not served while replaying.

        Examples:
          certapi mock
          curl http://127.0.0.1:8770/anything

          certapi mock --mtls --port 9443
          certapi send https://localhost:9443/orders --cert-file .\certapi-mock-certs\mock-client.pfx --insecure

          certapi mock --har session.har --port 8770
        """;

    public static int Run(Args args, TextWriter stdout, TextWriter stderr, CliServices services)
    {
        string? portRaw = args.Value("--port");
        int port = 8770;
        if (portRaw is not null && (!int.TryParse(portRaw, out port) || port is < 0 or > 65535))
            throw new CliUsageException($"--port expects 0-65535, got '{portRaw}'.");

        bool http = args.Flag("--http");   // explicit no-op selector for the default, useful for scripts to state
        bool tls = args.Flag("--tls");
        bool mtls = args.Flag("--mtls");
        if ((http ? 1 : 0) + (tls ? 1 : 0) + (mtls ? 1 : 0) > 1)
            throw new CliUsageException("--http, --tls, and --mtls are mutually exclusive.");
        var mode = mtls ? MockTlsMode.Mtls : tls ? MockTlsMode.Https : MockTlsMode.Http;
        string certDir = args.Value("--cert-dir") ?? Path.Combine(Directory.GetCurrentDirectory(), "certapi-mock-certs");
        bool quiet = args.Flag("-q", "--quiet");

        string? harFile = args.Value("--har");
        string? noMatchRaw = args.Value("--no-match-status");
        if (noMatchRaw is not null && harFile is null)
            throw new CliUsageException("--no-match-status only applies together with --har.");
        int noMatchStatus = 404;
        if (noMatchRaw is not null && (!int.TryParse(noMatchRaw, out noMatchStatus) || noMatchStatus is < 100 or > 599))
            throw new CliUsageException($"--no-match-status expects an HTTP status 100-599, got '{noMatchRaw}'.");

        string? routesFile = args.Value("--routes");
        bool corsOn = false;
        var corsOrigins = new List<string>();
        while (args.OptionalValue(out var oneCors, "--cors"))
        {
            corsOn = true;
            if (oneCors is not null)
                corsOrigins.AddRange(oneCors.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(o => o == "*" ? o : CorsCommand.NormalizeOrigin(o)));
        }
        MockCors? cors = corsOn ? new MockCors(corsOrigins.Count == 0 ? null : corsOrigins) : null;
        string? tlsDefectRaw = args.Value("--tls-mode");
        var tlsDefect = tlsDefectRaw?.ToLowerInvariant() switch
        {
            null or "valid" => MockTlsDefect.None,
            "expired" => MockTlsDefect.Expired,
            "wrong-host" => MockTlsDefect.WrongHost,
            "self-signed" => MockTlsDefect.SelfSigned,
            _ => throw new CliUsageException(
                $"--tls-mode expects valid, expired, wrong-host, or self-signed, got '{tlsDefectRaw}'.")
        };
        if (tlsDefect != MockTlsDefect.None && mode == MockTlsMode.Http)
            throw new CliUsageException("--tls-mode needs --tls or --mtls: there is no certificate to spoil over plain HTTP.");

        if (args.Positionals().Count > 0) throw new CliUsageException(Help);

        MockScenario? scenario = null;
        if (routesFile is not null)
        {
            if (!File.Exists(routesFile)) throw new CliDataException($"No such file: {routesFile}");
            try
            {
                // Body files are resolved against the scenario's own folder, so a scenario and its
                // bodies move together rather than depending on the working directory.
                scenario = MockScenario.Parse(
                    File.ReadAllText(routesFile), Path.GetDirectoryName(Path.GetFullPath(routesFile)));
            }
            catch (FormatException ex) { throw new CliDataException($"Could not parse '{routesFile}': {ex.Message}"); }
            foreach (var warning in scenario.Warnings) stderr.WriteLine("warning: " + warning);
        }

        HarReplaySource? replay = null;
        if (harFile is not null)
        {
            if (!File.Exists(harFile)) throw new CliDataException($"No such file: {harFile}");
            Har har;
            try
            {
                har = HarReader.Parse(File.ReadAllText(harFile));
            }
            catch (HarFormatException ex)
            {
                throw new CliDataException(ex.Message);
            }
            replay = new HarReplaySource(har, new HarReplayOptions { NoMatchStatus = noMatchStatus });
        }

        X509Certificate2? serverCert = null;
        if (mode != MockTlsMode.Http)
            serverCert = MockCertificates.Generate(mode, certDir, tlsDefect).ServerCertificate;

        Action<MockRequestLog>? onRequest = quiet ? null : Log;
        MockServer server;
        try
        {
            server = MockServer.Start(port, mode, serverCert, onRequest, replay, scenario, cors);
        }
        catch (System.Net.Sockets.SocketException ex)
        {
            stderr.WriteLine($"error: could not listen on port {port} ({ex.Message}). Try a different --port.");
            serverCert?.Dispose();
            return ExitCodes.Failure;
        }

        stderr.WriteLine($"certapi mock listening on {server.BaseUrl}  ({mode})");
        if (cors is not null)
            stderr.WriteLine(cors.Origins is null
                ? "cors: preflights answered, any origin allowed (with credentials)"
                : $"cors: preflights answered for {string.Join(", ", cors.Origins)}; any other origin gets no CORS headers");
        if (tlsDefect != MockTlsDefect.None)
            stderr.WriteLine($"serving a deliberately {tlsDefectRaw} certificate — clients are SUPPOSED to refuse this one");
        if (scenario is not null)
            stderr.WriteLine($"serving {scenario.Routes.Count} declared route(s) from {routesFile}" +
                (replay is not null ? "; anything they miss falls through to the recording" : ""));
        if (replay is not null)
        {
            stderr.WriteLine($"replaying {replay.Count} recorded responses from {harFile}");
            stderr.WriteLine($"built-in routes are not served in replay mode; a request matching nothing answers {noMatchStatus}");
        }
        else
        {
            stderr.WriteLine("routes: /  /status/<code>  /sse  /token  /windows-auth  /cookie-auth  (WebSocket on any path)");
        }
        if (mode != MockTlsMode.Http)
        {
            stderr.WriteLine($"certificates in {certDir}");
            stderr.WriteLine(mode == MockTlsMode.Mtls
                ? "present mock-client.pfx as your client cert; use --insecure (or trust mock-ca.cer) for the server cert."
                : "use --insecure (or trust mock-ca.cer) for the self-signed server certificate.");
        }
        stderr.WriteLine("press Ctrl+C to stop.");

        services.Cancel.WaitHandle.WaitOne();   // block until Program.cs cancels on Ctrl+C

        stderr.WriteLine("stopping…");
        server.DisposeAsync().AsTask().GetAwaiter().GetResult();
        serverCert?.Dispose();
        return ExitCodes.Ok;

        void Log(MockRequestLog r)
        {
            string who = r.ClientCertSubject is { } s ? $"  ({s})" : "";
            string matched = r.Replay switch { "exact" => "  exact-match", "path" => "  path-match", "miss" => "  miss", _ => "" };
            lock (stderr) stderr.WriteLine($"  {DateTime.Now:HH:mm:ss}  {r.Method,-6} {r.Path} → {r.Status}{who}{matched}");
        }
    }
}
