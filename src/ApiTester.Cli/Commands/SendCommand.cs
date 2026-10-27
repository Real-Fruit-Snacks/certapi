using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using ApiTester.Core;

namespace ApiTester.Cli.Commands;

public static class SendCommand
{
    public const string Help = """
        Usage: certapi send <url> [options]

        Request:
          -X, --method <m>        HTTP method (default GET)
          -I, --head              Send HEAD and print the status line and headers
          -H, --header "k: v"     Add a header (repeatable)
          -A, --user-agent <ua>   Set the User-Agent header
          --query name=value      Add a query parameter to the URL, percent-encoded (repeatable)
          -d, --data <body>       Request body; -d @file reads it from a file, as in curl
                                  ( --data-file <file> reads a file without interpreting @ )
          -F, --form name=value   multipart/form-data field; name=@path uploads a file
                                  (name=@path;type=<ct> sets its content type). Repeatable;
                                  implies POST. Mutually exclusive with -d.
          --graphql <query>       Send a GraphQL query (POST, application/json)
          --gql-variables <json>  A JSON object of GraphQL variables
          --content-type <ct>     Body content type (default application/json)
          --bearer <token>        Authorization: Bearer …
          -u, --basic <user:pass> Authorization: Basic …
          --windows-auth          Windows Integrated Auth (Negotiate/NTLM) with your signed-in
                                  account (aliases: --ntlm, --negotiate)
          --windows-user <u>      Explicit Windows creds instead of SSO (DOMAIN\user); pair with
          --windows-password <p>  the password
          --timeout <seconds>     Default 100

        TLS / certificates:
          --cert <thumb|subject>  Client certificate from the Windows store
          --store <location>      CurrentUser (default); LocalMachine searches both stores
          --cert-file <path>      Client certificate from a file (.pfx/.p12 or .pem/.crt) instead
          --cert-password <pw>    Password for a .pfx/.p12 certificate file
          --key-file <path>       Private-key file for a PEM cert whose key is in a separate file
          --cert-issuer <substr>  Narrow --cert to certificates issued by a matching CA (or use alone)
          --cert-serial <hex>     Pick the certificate with this serial number (or use alone)
          --cert-pin <pin>        Smart-card / HSM PIN for the key, supplied ahead of time so a
                                  headless job is not stopped by the prompt. Prefer CERTAPI_CERT_PIN,
                                  which keeps it out of the shell history. Windows (CNG) only
          -k, --insecure          Ignore server certificate errors


        """ + TransportFlags.Help + """


        Addresses:
          --all-ips               Send once per address the host resolves to and compare the
                                  results (one row each; not with --json/-o/--capture/--assert/
                                  --diff)

        Automatic tokens:
          A bearer token found in a response (access_token, id_token, token, accessToken, jwt,
          or an X-Auth-Token / X-Access-Token header) is captured and scoped to that host.
          Later sends to the same host attach it automatically — unless you pass explicit auth
          (--bearer / --basic / an Authorization header).
          --no-auto-token         Disable token capture/reuse and captured-cookie attach for this send
          --cookie-jar <file>     Load cookies from this file before the send and save every cookie
                                  the response (and each redirect hop) set back into it, so a
                                  scripted multi-step login carries across invocations. Plain JSON,
                                  like curl's -b/-c in one flag; created on first use

        Variables:
          --env <name>            Environment ({{var}} values) from your workspace
          --var k=v               Override/add a variable (repeatable)
          --workspace <file>      Load environments from a workspace file instead of the live state
          --strict-vars           Unresolved {{tokens}} become an error instead of a warning
          --capture var=path      Save a response value into an environment variable after the
                                  send (repeatable). path is a JSON body path (access_token,
                                  data.token) or header:Name for a response header.

        Testing:
          --assert "<expr>"       Check the response and exit 1 if it fails (repeatable). Syntax:
                                    status == 200 | status < 300 | time < 500
                                    header <name> contains <v> | body <jsonpath> exists
                                    body-text matches <regex>
                                  ops: == != contains matches exists !exists < >

        Diff:
          --diff <baseline>       Compare the response against a baseline and print what changed:
                                  a .har file, a .json response file, or 'known-good' (the stored
                                  response for the matching saved request)
          --diff-fail             Exit 1 when anything differs (the CI form)
          --diff-ignore <path>    JSON path to ignore, e.g. data.timestamp (repeatable; a trailing
                                  * on a segment matches by prefix)
          --diff-ignore-header <n>  Header to ignore on top of the volatile defaults (repeatable)

        Output:
          -o, --output <file>     Write the body to a file instead of stdout
          -i, --include           Print status line and headers before the body
          --pretty                Pretty-print the body (JSON/XML; hex for binary)
          --json                  Print a JSON result envelope instead of the raw body
          -f, --fail              Exit 1 when the HTTP status is 400 or higher
          -q, -s, --quiet         No metadata line on stderr
          --timing                Where the time went: DNS, connect, TLS, wait for the server,
                                  first byte, download, total (stderr; in the envelope with --json)
          --har <file>            Capture the request/response (and every redirect hop) as a
                                  HAR file, written once when the command finishes
          --har-include-secrets   Don't redact Authorization/Proxy-Authorization/Cookie/
                                  Set-Cookie values in the captured HAR (redacted by default)
          --wire                  Print the plaintext bytes of the exchange: the request as it was
                                  actually framed and the response as it actually arrived, hex and
                                  ASCII for anything that isn't text. Direct connections only —
                                  through a proxy or on HTTP/3 the TLS belongs to the handler, and
                                  it says so instead of printing nothing
          --wire-file <path>      Write that transcript to a file instead of stdout
          --wire-include-secrets  Keep credential header values in it (redacted by default)
          --frames                Read the same exchange as HTTP/2 frames instead of bytes: type,
                                  stream, flags, SETTINGS values, window updates and GOAWAY error
                                  codes — the layer that explains a stalled or refused h2 request.
                                  Implies --wire; needs --http2

        Global: --debug (verbose diagnostics) and --log-file <path> work here too.

        Examples:
          (Examples use PowerShell quoting; in cmd.exe write JSON bodies as "{\"user\":\"me\"}".)

          # Simple GET with a client certificate picked by subject
          certapi send https://api.example.com/users --cert "CN=My Client"

          # POST JSON, pretty-print the response
          certapi send https://api.example.com/users -X POST -d '{"name":"Ada"}' --pretty

          # Log in once, then call the API — the token is captured and reused automatically
          certapi send https://api.example.com/login -X POST -d '{"user":"me","pass":"..."}'
          # the follow-on call sends Authorization: Bearer … automatically
          certapi send https://api.example.com/orders

          # Headers, query strings, and a file body
          certapi send "https://api.example.com/search?q=abc" -H "Accept: application/json"
          certapi send https://api.example.com/upload -X PUT --data-file .\payload.json

          # Upload a file (and a text field) as multipart/form-data (implies POST)
          certapi send https://api.example.com/upload -F "notes=cover page" -F "file=@.\report.pdf"

          # A GraphQL query with variables
          certapi send https://api.example.com/graphql --graphql "query($id:ID!){ user(id:$id){ name } }" --gql-variables "{\"id\":1}"

          # Environments and capture rules
          certapi send "https://{{host}}/login" --env Staging --capture session=data.session_id

          # Save a binary body, keep stderr clean, fail the build on HTTP errors
          certapi send https://api.example.com/report.pdf -o report.pdf -q --fail

          # A one-line CI smoke test: assert the status and a JSON field, exit 1 if either fails
          certapi send https://api.example.com/health --assert "status == 200" --assert "body status == ok"

          # Through the corporate proxy, showing every redirect it takes on the way
          certapi send https://api.example.com/health --proxy http://proxy.corp:8080 --show-redirects

          # Troubleshoot a failing endpoint with full diagnostics in a file
          certapi send https://api.example.com/broken --debug --log-file broken.log

          # Capture the request/response (and every redirect hop) as a HAR file
          certapi send https://api.example.com/health --har session.har

          # Record a baseline once, then fail CI when the response stops matching it
          certapi send https://api.example.com/users --har baseline.har
          certapi send https://api.example.com/users --diff baseline.har --diff-fail --diff-ignore data.generatedAt

        The body goes to stdout; everything else goes to stderr. Exit 0 on a delivered
        response (any status unless --fail), 1 on transport errors, 2/3 on usage/data errors.
        """;

    public static int Run(Args args, TextWriter stdout, TextWriter stderr, Stream bodyOut, CliServices services)
    {
        // ---- bind options ----
        string? methodOpt = args.Value("-X", "--method");
        MethodOption.Require(methodOpt, "-X/--method");
        bool head = args.Flag("--head");
        if (head && methodOpt is not null && !methodOpt.Equals("HEAD", StringComparison.OrdinalIgnoreCase))
            throw new CliUsageException($"-I/--head sends a HEAD request, so it cannot be combined with -X {methodOpt}.");
        if (head) methodOpt = "HEAD";
        string? userAgent = args.Value("--user-agent");
        var querySpecs = args.Values("--query");
        var headers = args.Values("-H", "--header");
        string? data = args.Value("-d", "--data");
        string? dataFile = args.Value("--data-file");
        var formSpecs = args.Values("-F", "--form");
        string? graphql = args.Value("--graphql");
        string? gqlVars = args.Value("--gql-variables");
        // -F and --graphql imply a POST unless a method is given explicitly (curl's behaviour).
        string method = methodOpt ?? (formSpecs.Count > 0 || graphql is not null ? "POST" : "GET");
        string? contentType = args.Value("--content-type");
        string? bearer = args.Value("--bearer");
        string? basic = args.Value("--basic");
        string store = args.Value("--store") ?? services.Profile?.Store ?? "CurrentUser";
        // A switch rather than a plain flag: a profile's `insecure: true` applies when nothing is
        // typed, and --no-insecure undoes it for one run.
        bool insecure = args.Switch("--insecure", services.Profile?.Insecure);
        string? timeoutRaw = args.Value("--timeout") ?? services.Profile?.Timeout?.ToString();
        int timeout = 100;
        if (timeoutRaw is not null && (!int.TryParse(timeoutRaw, out timeout) || timeout <= 0))
            throw new CliUsageException($"--timeout expects a positive number of seconds, got '{timeoutRaw}'.");
        string? envName = args.Value("--env");
        var varOverrides = args.Values("--var");
        string? workspace = args.Value("--workspace") ?? services.Profile?.Workspace;
        bool strictVars = args.Flag("--strict-vars");
        string? outFile = args.Value("-o", "--output");
        bool include = args.Flag("--include") || head;
        bool pretty = args.Flag("--pretty");
        bool json = args.Flag("--json");
        bool fail = args.Flag("--fail");
        bool quiet = args.Flag("-q", "--quiet");
        var captureSpecs = args.Values("--capture");
        var assertSpecs = args.Values("--assert");
        bool noAutoToken = args.Flag("--no-auto-token");
        string? cookieJarPath = args.Value("--cookie-jar");
        bool windowsAuth = args.Flag("--windows-auth", "--ntlm", "--negotiate");
        string? winUser = args.Value("--windows-user");
        string? winPass = args.Value("--windows-password");
        var transportOverrides = TransportFlags.Parse(args, out bool showRedirects, environment: null, services.Profile);
        bool allIps = args.Flag("--all-ips");
        string? diffBaseline = args.Value("--diff");
        bool diffFail = args.Flag("--diff-fail");
        var diffIgnorePaths = args.Values("--diff-ignore");
        var diffIgnoreHeaders = args.Values("--diff-ignore-header");
        string? harPath = args.Value("--har");
        bool harIncludeSecrets = args.Flag("--har-include-secrets");
        bool wire = args.Flag("--wire");
        string? wireFile = args.Value("--wire-file");
        bool wireIncludeSecrets = args.Flag("--wire-include-secrets");
        bool frames = args.Flag("--frames");
        bool timing = args.Flag("--timing");
        if (wireFile is not null) wire = true;
        if (frames) wire = true;                 // the frame view is a reading of the same capture
        if (wireIncludeSecrets && !wire)
            throw new CliUsageException("--wire-include-secrets only applies together with --wire.");
        // Resolve the certificate here (Windows store or a file) so its options are consumed
        // before Positionals() rejects anything option-shaped that's left over.
        var cert = CliCert.Resolve(args, store, services, stderr);

        var positionals = args.Positionals();
        if (positionals.Count != 1) throw new CliUsageException(Help);
        string url = positionals[0];

        var captureRules = new List<CaptureRule>();
        foreach (var raw in captureSpecs)
        {
            int eq = raw.IndexOf('=');
            if (eq <= 0) throw new CliUsageException($"--capture expects var=path, got '{raw}'.");
            string variable = raw[..eq].Trim();
            if (variable.Length == 0) throw new CliUsageException($"--capture needs a variable name, got '{raw}'.");
            string path = raw[(eq + 1)..];
            bool header = path.StartsWith("header:", StringComparison.OrdinalIgnoreCase);
            captureRules.Add(new CaptureRule
            {
                Variable = variable,
                Source = header ? CaptureSource.Header : CaptureSource.Body,
                Path = header ? path["header:".Length..] : path
            });
        }

        // Ad-hoc response assertions (--assert). The specs were consumed above (before Positionals);
        // parse them here so a bad expression is a clean usage error before anything is sent.
        var assertRules = new List<AssertionRule>();
        foreach (var raw in assertSpecs)
        {
            try { assertRules.Add(AssertionParser.Parse(raw)); }
            catch (FormatException ex) { throw new CliUsageException($"--assert: {ex.Message}"); }
        }
        if (data is not null && dataFile is not null)
            throw new CliUsageException("-d/--data and --data-file are mutually exclusive.");
        if (formSpecs.Count > 0 && (data is not null || dataFile is not null))
            throw new CliUsageException("-F/--form (multipart) and -d/--data are mutually exclusive.");
        if (graphql is not null && (data is not null || dataFile is not null || formSpecs.Count > 0))
            throw new CliUsageException("--graphql cannot be combined with -d/--data or -F/--form.");
        if (bearer is not null && basic is not null)
            throw new CliUsageException("--bearer and --basic are mutually exclusive.");
        // --all-ips produces one response per address; every option below describes a single one,
        // and there is no honest way to pick which response it should apply to.
        if (allIps && timing)
            throw new CliUsageException("--all-ips already prints a time per address; --timing describes a single request.");
        if (allIps && (json || outFile is not null || captureRules.Count > 0 || assertRules.Count > 0 ||
                       diffBaseline is not null))
            throw new CliUsageException(
                "--all-ips sends once per address, so it cannot be combined with --json, -o/--output, " +
                "--capture, --assert, or --diff — each of those describes a single response.");
        // A flag that silently does nothing is worse than an error, so the diff modifiers name
        // themselves rather than quietly evaporating when there is no baseline to modify.
        string? orphanDiffFlag =
            diffBaseline is not null ? null
            : diffFail ? "--diff-fail"
            : diffIgnorePaths.Count > 0 ? "--diff-ignore"
            : diffIgnoreHeaders.Count > 0 ? "--diff-ignore-header"
            : null;
        if (orphanDiffFlag is not null)
            throw new CliUsageException(
                $"{orphanDiffFlag} needs --diff <baseline> — there is nothing to compare against without it.");
        // --har's directory must exist before the first request, not merely before the write — a
        // typo shouldn't surface after a request already went out over the wire.
        if (harPath is not null)
        {
            var harDir = Path.GetDirectoryName(Path.GetFullPath(harPath));
            if (!Directory.Exists(harDir)) throw new CliUsageException($"--har directory does not exist: {harDir}");
        }

        // Multipart form parts: "name=value" (text) or "name=@path" (file, optionally ";type=<ct>").
        var parts = new List<MultipartPart>();
        foreach (var raw in formSpecs)
        {
            int eq = raw.IndexOf('=');
            if (eq <= 0) throw new CliUsageException($"-F expects name=value or name=@file, got '{raw}'.");
            string name = raw[..eq];
            string rest = raw[(eq + 1)..];
            if (rest.StartsWith('@'))
            {
                string spec = rest[1..];
                string? partType = null;
                int semi = spec.IndexOf(";type=", StringComparison.OrdinalIgnoreCase);
                if (semi >= 0) { partType = spec[(semi + ";type=".Length)..]; spec = spec[..semi]; }
                if (!File.Exists(spec)) throw new CliDataException($"Form file not found: {spec}");
                parts.Add(new MultipartPart(name, null, spec, partType));
            }
            else parts.Add(new MultipartPart(name, rest, null));
        }
        // As in curl, -d @file reads the body from a file. A body that genuinely starts with '@'
        // goes in a file and is passed with --data-file, which never interprets its contents.
        if (data is { Length: > 1 } && data[0] == '@')
        {
            string from = data[1..];
            data = File.Exists(from) ? File.ReadAllText(from) : throw new CliDataException($"Body file not found: {from} (from -d @{from})");
        }
        string? body = data ?? (dataFile is not null
            ? File.Exists(dataFile) ? File.ReadAllText(dataFile) : throw new CliDataException($"Body file not found: {dataFile}")
            : null);

        // An explicit --workspace file that doesn't exist is an error — unless --capture is
        // creating it (a capture write starts from an empty workspace).
        if (workspace is not null && captureRules.Count == 0 && !File.Exists(workspace))
            throw new CliDataException($"Workspace file not found: {workspace}");

        // ---- variables ----
        // The state is always loaded now: even without --env, the live state may hold a
        // captured session token for this URL's origin. A --workspace that doesn't exist yet
        // is fine when --capture is present — it is created fresh on save.
        var state = LoadWorkspaceOrEmpty(workspace, services, stderr);
        var vars = CliWorkspace.BuildVars(state, envName, varOverrides);
        var unresolved = new List<string>();
        string R(string s)
        {
            var (resolved, missing) = VariableResolver.Resolve(s ?? "", vars);
            foreach (var m in missing) if (!unresolved.Contains(m)) unresolved.Add(m);
            return resolved;
        }

        url = R(url);
        if (querySpecs.Count > 0) url = RequestUrl.AppendQuery(url, ParseQuery(querySpecs).Select(kv => new KeyValuePair<string, string>(R(kv.Key), R(kv.Value))));
        var headerPairs = new List<KeyValuePair<string, string>>();
        // As in curl, a User-Agent typed with -H wins over -A.
        if (userAgent is not null && !headers.Any(h => h.TrimStart().StartsWith("User-Agent:", StringComparison.OrdinalIgnoreCase)))
            headerPairs.Add(new("User-Agent", R(userAgent)));
        foreach (var raw in headers)
        {
            int colon = raw.IndexOf(':');
            if (colon <= 0) throw new CliUsageException($"Header must be \"Name: value\", got '{raw}'.");
            headerPairs.Add(new(R(raw[..colon].Trim()), R(raw[(colon + 1)..].Trim())));
        }
        if (bearer is not null) headerPairs.Add(new("Authorization", "Bearer " + R(bearer)));
        if (basic is not null) headerPairs.Add(new("Authorization",
            "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(R(basic)))));
        // The profile's standing headers fill in behind whatever was typed: a name set here wins.
        if (services.Profile is { } sendProfile)
            headerPairs = new List<KeyValuePair<string, string>>(sendProfile.WithStandingHeaders(headerPairs));
        if (body is not null) body = R(body);
        if (graphql is not null)
        {
            try { body = GraphQL.BuildBody(R(graphql), string.IsNullOrWhiteSpace(gqlVars) ? null : R(gqlVars)); }
            catch (System.Text.Json.JsonException ex)
            { throw new CliDataException($"--gql-variables must be a JSON object: {ex.Message}"); }
        }

        if (unresolved.Count > 0)
        {
            var tokens = string.Join(", ", unresolved.Select(u => "{{" + u + "}}"));
            if (strictVars) throw new CliDataException($"Unresolved variables: {tokens}");
            stderr.WriteLine($"warning: unresolved variables: {tokens}");
        }

        // ---- transport ----
        // Built once the URL is final, because whether a pinned address can be honoured depends on
        // whether *this* URL goes through a proxy. An unusable combination is a usage error here,
        // before any network contact, rather than a setting silently dropped on the floor.
        var transport = transportOverrides.ApplyTo(new TransportOptions { IgnoreServerCertificateErrors = insecure });
        if (ApiClient.ValidateTransport(transport, url) is { } transportProblem)
            throw new CliUsageException(transportProblem);

        // ---- per-site server-certificate trust ----
        // A pinned thumbprint for this host lets the send through even without the blanket
        // --insecure bypass; --insecure (above) already trusts anything and is untouched.
        string host = TokenService.HostOf(url);
        Func<System.Security.Cryptography.X509Certificates.X509Certificate2?, bool> trustCert = c =>
            c is not null && TrustService.IsTrusted(state, host, c, TrustContext.CurrentChain);

        // ---- diff baseline ----
        // Resolved after the URL is fully variable-resolved but before anything goes over the wire:
        // a baseline that doesn't exist has to fail the command without making a network call.
        var diffOptions = diffBaseline is null ? null : new DiffOptions
        {
            IgnorePaths = diffIgnorePaths,
            // Added to the volatile defaults rather than replacing them: a user naming one noisy
            // header has said nothing about wanting Date and Set-Cookie compared from now on.
            IgnoreHeaders = diffIgnoreHeaders.Count == 0
                ? DiffOptions.DefaultIgnoredHeaders
                : DiffOptions.DefaultIgnoredHeaders.Concat(diffIgnoreHeaders).ToList(),
            CompareHeaderValues = true
        };
        var baseline = diffBaseline is null ? null : DiffBaseline.Resolve(diffBaseline, method, url, state);

        // ---- automatic session token ----
        if (!noAutoToken)
        {
            var used = TokenService.AutoAttach(state, url, headerPairs, out var expired);
            if (used is not null)
            {
                if (!quiet) stderr.WriteLine($"note: using captured token for {TokenService.HostOf(url)}");
                services.Log.Debug($"auto token attached for {used.Origin} ({used.Source})");
            }
            else if (expired is not null && !quiet)
            {
                stderr.WriteLine($"note: the captured token for {TokenService.HostOf(url)} has expired — sending without it");
            }
        }


        // ---- send ----
        var request = new ApiRequest
        {
            Method = new HttpMethod(method.ToUpperInvariant()),
            Url = url,
            Headers = headerPairs,
            Body = body,
            Parts = parts.Count > 0
                ? parts.Select(p => p with { Name = R(p.Name), Value = p.Value is null ? null : R(p.Value) }).ToList()
                : null,
            ContentType = body is not null ? (contentType ?? "application/json") : null,
            WindowsAuth = winUser is not null ? WindowsAuthOptions.FromCredentials(winUser, winPass)
                       : windowsAuth ? new WindowsAuthOptions(true)
                       : null,
            Timeout = TimeSpan.FromSeconds(timeout)
        };
        services.Log.Debug($"{request.Method} {MarkdownSecrets.RedactUrl(request.Url)}");
        foreach (var h in headerPairs)
            services.Log.Debug("header: " + (h.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase)
                ? $"{h.Key}: {TokenService.MaskAuthorization(h.Value)}" : $"{h.Key}: {h.Value}"));
        services.Log.Debug(cert is null ? "certificate: none" : $"certificate: {cert.Subject} ({cert.Thumbprint})");
        services.Log.Debug($"timeout: {timeout} s · insecure: {insecure} · store: {store}");
        services.Log.Debug($"transport: {DescribeTransport(transport)}");
        foreach (var pin in transport.Resolve)
            services.Log.Debug($"resolve: {pin.Host}:{pin.Port} -> {pin.Address}");
        // Attach any browser-captured session cookies for this origin (honors --no-auto-token
        // and the workspace's AutoCookies switch).
        var cookieJar = new System.Net.CookieContainer();
        if (!noAutoToken) CookieService.SeedContainer(state, url, cookieJar);
        if (cookieJarPath is not null)
        {
            try { services.Log.Debug($"cookie jar: loaded {CookieJarFile.Load(cookieJarPath, cookieJar)} cookie(s) from {cookieJarPath}"); }
            catch (InvalidDataException ex) { throw new CliDataException(ex.Message); }
        }

        string harVersion = HarCreatorVersion();

        // --all-ips is a comparison run, not a single send: it owns stdout from here on, and the
        // single-response output path below (capture, token capture, envelope) does not apply.
        if (allIps)
            return SendToEveryAddress(request, cert, transport, cookieJar, showRedirects, fail, stdout, stderr, services,
                trustCert, harPath, harIncludeSecrets, harVersion, quiet);

        // The byte view works only where this product drives its own TLS: the direct path. A
        // proxied send lets the handler own the tunnel's TLS, and HTTP/3 runs QUIC inside the
        // handler — neither has a plaintext stream to tee. Saying so beats producing nothing.
        WireLog? wireLog = null;
        if (wire)
        {
            string? why = ApiClient.ProxyWillBeUsed(request.Url, transport)
                ? "the request goes through a proxy, which owns the tunnel's TLS"
                : transport.Version == HttpVersionMode.Http3
                    ? "HTTP/3 runs QUIC inside the HTTP handler"
                    : null;
            if (why is not null)
                stderr.WriteLine($"note: --wire cannot show the bytes here because {why}. " +
                                 "The request is sent normally; use --trace for what the stack reports.");
            else wireLog = new WireLog();
        }

        // --frames reads the capture as HTTP/2. Nothing forces the connection to be HTTP/2, and
        // quietly switching the protocol would change the very thing the user is measuring — so
        // say what to add rather than deciding for them.
        if (frames && transport.Version is not HttpVersionMode.Http2 and not HttpVersionMode.Auto)
            stderr.WriteLine("note: --frames reads HTTP/2 framing, but this request is pinned to " +
                             $"{transport.Version.ToString().ToUpperInvariant()}. Add --http2 to get frames.");

        PhaseTimings? phases;
        ApiResponse response;
        using (var timer = timing ? new PhaseTimer() : null)
        {
            response = services.Client.SendAsync(request, cert,
                transport: transport,
                trustServerCertificate: trustCert,
                cookies: cookieJar, wireLog: wireLog, cancellationToken: services.Cancel).GetAwaiter().GetResult();
            phases = timer?.Result();
        }

        if (wireLog is not null) WriteWire(wireLog, wireFile, wireIncludeSecrets, frames, stdout, stderr);
        if (cookieJarPath is not null)
        {
            try { services.Log.Debug($"cookie jar: saved {CookieJarFile.Save(cookieJarPath, cookieJar)} cookie(s) to {cookieJarPath}"); }
            catch (IOException ex) { stderr.WriteLine($"warning: could not write --cookie-jar {cookieJarPath}: {ex.Message}"); }
        }
        services.Log.Debug("result: " + (response.Error is null
            ? $"{response.StatusCode} {response.ReasonPhrase}".Trim()
            : $"[{response.Error.Kind}] {response.Error.Message}")
            + $" · {response.Elapsed.TotalMilliseconds:F0} ms · {response.Body.LongLength} bytes");
        if (response.Connection is { } conn)
        {
            // BypassedByRule and Direct both mean "no proxy was used", but they must read differently:
            // a rule-driven bypass is a decision, where Direct is simply "there was never a proxy here".
            string proxyDebug = conn.ProxyDisposition switch
            {
                ProxyDisposition.Proxied => "yes",
                ProxyDisposition.BypassedByRule => $"no (bypassed by '{conn.ProxyBypassRule}')",
                _ => "no"
            };
            // Plain words, not the bare enum name: a support log has to answer "was revocation
            // actually verified?" without the reader needing to know what NotEnforced means.
            string revocationDebug = conn.RevocationStatus switch
            {
                RevocationStatus.NotChecked => "not checked",
                RevocationStatus.Checked => "checked, good",
                RevocationStatus.Revoked => "REVOKED",
                RevocationStatus.Unknown => "status unknown",
                RevocationStatus.NotEnforced => "NOT ENFORCED (--insecure)",
                _ => conn.RevocationStatus.ToString()
            };
            services.Log.Debug($"connection: tls {conn.TlsProtocol ?? "—"} · proxy {proxyDebug} · client cert sent {(conn.ClientCertificateSent ? "yes" : "no")} · revocation {revocationDebug}");
        }
        LogErrorChain(response.Error, services);
        List<HarEntry>? harEntries = harPath is not null
            ? HarWriter.FromExchangeWithRedirects(request, response, harIncludeSecrets).ToList()
            : null;

        // The hop chain goes to stderr even under --quiet: it was asked for explicitly, and the
        // body owns stdout.
        if (showRedirects && response.Redirects.Count > 0)
            stderr.WriteLine(OutputText.RedirectLines(response.Redirects));
        if (!quiet) stderr.WriteLine(OutputText.MetaLine(response));
        // Asked for by name, so printed even with --quiet; with --json it is in the envelope instead.
        if (timing && !json)
            stderr.WriteLine("timing: " + (phases?.Render() ?? "no request reached the network"));
        if (!quiet && response.Error is null && response.Connection?.ServerCertificateThumbprint is { } thumb &&
            TrustService.IsTrusted(state, host, thumb))
            stderr.WriteLine($"note: trusting pinned certificate for {host}");
        // A certificate was chosen, the handshake never sent it, and the server then refused: the
        // per-path-certificate shape, which is otherwise indistinguishable from a bad token.
        if (!quiet && response.Error is null && cert is not null && response.StatusCode is 401 or 403 &&
            response.Connection is { ViaProxy: false, ClientCertificateSent: false } &&
            request.Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            stderr.WriteLine("note: " + DoctorReport.PerPathCertificateAdvice);
        // Ruling 5: --insecure keeps overriding revocation exactly as it overrides every other chain
        // problem, but doing so silently would be the tool lying by omission — a user who passes
        // --insecure is not necessarily reading --debug, so this goes to stderr unconditionally
        // (under -q's usual gate) rather than only into the debug log above. This can only fire when
        // revocation checking was actually asked for: RevocationCheck.Decide reports NotChecked, not
        // NotEnforced, when --insecure is combined with the default --revocation none, so an existing
        // user who has never touched --revocation can never see this note.
        if (!quiet && response.Connection?.RevocationStatus == RevocationStatus.NotEnforced)
            stderr.WriteLine(
                "note: revocation checking was requested but not enforced, because --insecure accepts any certificate.");

        // ---- diff ----
        // Computed here so the JSON envelope below can carry it. A transport failure produced no
        // response to compare, and the error path already returns 1 on its own.
        DiffResult? diff = null;
        if (baseline is not null && response.Error is null)
        {
            diff = ResponseDiff.Compare(baseline, ResponseSnapshot.From(response), diffOptions);
            // Printed even under --quiet, the way --show-redirects is: it was asked for explicitly,
            // and the body still owns stdout.
            stderr.WriteLine($"diff vs {diffBaseline}:");
            stderr.WriteLine(DiffText.Format(diff));
        }

        bool stateDirty = false;
        if (captureRules.Count > 0 && response.Error is null)
        {
            var outcome = CaptureApplier.Apply(state, captureRules, response.Body, response.ContentType, response.Headers);
            var ok = outcome.Where(o => o.Ok).Select(o => o.Variable).ToList();
            if (ok.Count > 0) stderr.WriteLine("captured " + string.Join(", ", ok));
            foreach (var b in outcome.Where(o => !o.Ok)) stderr.WriteLine($"capture '{b.Variable}' failed: {b.Error}");
            stateDirty |= outcome.Count > 0;
        }
        if (!noAutoToken && response.Error is null &&
            TokenService.Capture(state, url, response.Body, response.ContentType, response.Headers) is { } captured)
        {
            if (!quiet)
            {
                string expiry = captured.ExpiresUtc is { } e
                    ? $", expires in {(int)Math.Max(1, (e - DateTime.UtcNow).TotalMinutes)} min" : "";
                stderr.WriteLine($"note: captured bearer token for {TokenService.HostOf(url)} ({captured.Source}{expiry})");
            }
            services.Log.Debug($"token captured for {captured.Origin} ({captured.Source}): {TokenService.Mask(captured.Token)}");
            stateDirty = true;
        }
        if (stateDirty) SaveState(state, workspace, services, stderr);

        // ---- output ----
        if (json)
        {
            if (outFile is not null && response.Error is null) File.WriteAllBytes(outFile, response.Body);
            stdout.WriteLine(BuildEnvelope(response, includeBody: outFile is null, diff: diff, timing: timing ? phases : null));
        }
        else if (response.Error is null)
        {
            if (include)
            {
                stdout.WriteLine($"{response.StatusCode} {response.ReasonPhrase}".Trim());
                foreach (var h in response.Headers) stdout.WriteLine($"{h.Key}: {h.Value}");
                stdout.WriteLine();
            }
            if (outFile is not null) File.WriteAllBytes(outFile, response.Body);
            else if (pretty) stdout.WriteLine(new ResponseFormatter().Format(response).Text);
            else { stdout.Flush(); bodyOut.Write(response.Body); bodyOut.Flush(); }
        }

        // The archive is written once here — before any of the returns below — so it captures
        // this send whether it succeeded, failed assertions, or came back with a transport error.
        if (harPath is not null && harEntries is not null)
        {
            File.WriteAllText(harPath, HarWriter.Write(harEntries, harVersion));
            if (!quiet) stderr.WriteLine($"wrote HAR to {harPath} ({harEntries.Count} entr{(harEntries.Count == 1 ? "y" : "ies")})");
        }

        if (response.Error is not null) return ExitCodes.Failure;

        // ---- assertions ----
        bool assertionsFailed = false;
        if (assertRules.Count > 0)
        {
            var results = AssertionEvaluator.Evaluate(assertRules, response);
            foreach (var r in results)
            {
                stderr.WriteLine(r.Passed
                    ? $"  PASS  {r.Description}"
                    : $"  FAIL  {r.Description}  (actual: {r.Actual ?? "<none>"})");
                if (!r.Passed) assertionsFailed = true;
            }
            stderr.WriteLine($"assertions: {results.Count(r => r.Passed)}/{results.Count} passed");
        }

        // The diff was already reported above; --diff-fail is the only thing that turns a difference
        // into an exit code, and it is folded in after the assertions so a send with both has
        // reported both before either decides the result.
        if (assertionsFailed || (diffFail && diff is { Identical: false })) return ExitCodes.Failure;
        if (fail && response.StatusCode is >= 400) return ExitCodes.Failure;
        return ExitCodes.Ok;
    }

    /// <summary>The transport configuration in one --debug line, so a support log says how the
    /// request was actually routed rather than only where it was pointed.</summary>
    private static string DescribeTransport(TransportOptions t) =>
        $"proxy {(t.Proxy switch { ProxyMode.None => "off", ProxyMode.Explicit => t.ProxyUrl is null ? "explicit" : MarkdownSecrets.RedactUrl(t.ProxyUrl), _ => "system" })}"
        + $" · redirects {(t.FollowRedirects ? $"follow (max {t.MaxRedirects})" : "off")}"
        + $" · decompress {(t.Decompress ? "on" : "off")}"
        + $" · http {(t.Version switch { HttpVersionMode.Http11 => "1.1", HttpVersionMode.Http2 => "2", _ => "auto" })}"
        + $" · revocation {(t.Revocation switch { RevocationMode.Offline => "offline", RevocationMode.Online => "online", _ => "off" })}{(t.RevocationStrict ? " (strict)" : "")}";

    /// <summary>The flattened InnerException chain on --debug. For a refused client certificate the
    /// AuthenticationException and the SChannel status code are several links down and never in the
    /// outermost message, so the chain is the whole diagnostic.</summary>
    private static void LogErrorChain(ApiError? error, CliServices services)
    {
        if (error is null) return;
        if (error.SocketErrorCode is { } socketError) services.Log.Debug($"socket error: {socketError}");
        foreach (var d in error.Detail ?? Array.Empty<ErrorDetail>())
            services.Log.Debug($"caused by: {d.ExceptionType}: {d.Message}"
                + (d.NativeErrorCode is { } native ? $" (native error {native})" : ""));
    }

    /// <summary>--all-ips: the same request once per address the host resolves to, so the nodes
    /// behind a load balancer can be compared. Built entirely from the --resolve primitive, so the
    /// TLS server name and the Host header still carry the original hostname on every send.</summary>
    private static int SendToEveryAddress(
        ApiRequest request, System.Security.Cryptography.X509Certificates.X509Certificate2? cert,
        TransportOptions transport, System.Net.CookieContainer cookies, bool showRedirects, bool fail,
        TextWriter stdout, TextWriter stderr, CliServices services,
        Func<System.Security.Cryptography.X509Certificates.X509Certificate2?, bool> trustServerCertificate,
        string? harPath, bool harIncludeSecrets, string harVersion, bool quiet)
    {
        if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var uri))
            throw new CliUsageException($"--all-ips needs an absolute URL, got '{request.Url}'.");

        // A host that is already an IP literal has nothing to pin: it is one address by definition.
        bool literal = System.Net.IPAddress.TryParse(uri.Host.Trim('[', ']'), out _);
        var addresses = literal ? new[] { uri.Host } : ResolveAll(uri.Host);
        services.Log.Debug($"all-ips: {uri.Host} -> {string.Join(", ", addresses)}");

        var rows = new List<(string Address, ApiResponse Response)>();
        // One entry-set per address — the honest record of what --all-ips actually performed.
        var harEntries = harPath is not null ? new List<HarEntry>() : null;
        foreach (var address in addresses)
        {
            var pinned = literal
                ? transport
                : transport with { Resolve = new[] { new ResolveOverride(uri.Host, uri.Port, address) } };
            var response = services.Client.SendAsync(request, cert, transport: pinned,
                trustServerCertificate: trustServerCertificate,
                cookies: cookies, cancellationToken: services.Cancel).GetAwaiter().GetResult();
            rows.Add((address, response));
            harEntries?.AddRange(HarWriter.FromExchangeWithRedirects(request, response, harIncludeSecrets));
            LogErrorChain(response.Error, services);
            if (showRedirects && response.Redirects.Count > 0)
                stderr.WriteLine($"{address}:\n{OutputText.RedirectLines(response.Redirects)}");
        }

        int width = rows.Max(r => r.Address.Length);
        foreach (var (address, r) in rows)
            stdout.WriteLine(
                $"{address.PadRight(width)}  {(r.Error is null ? r.StatusCode?.ToString() ?? "—" : "ERR"),4}  " +
                $"{r.Elapsed.TotalMilliseconds,6:F0} ms  {OutputText.Size(r.Body.LongLength),9}" +
                (r.Error is not null ? $"  ({r.Error.Message})" : ""));
        int responded = rows.Count(r => r.Response.Error is null);
        stdout.WriteLine($"----\n{rows.Count} address{(rows.Count == 1 ? "" : "es")} · " +
                         $"{responded} responded · {rows.Count - responded} failed");

        if (harPath is not null && harEntries is not null)
        {
            File.WriteAllText(harPath, HarWriter.Write(harEntries, harVersion));
            if (!quiet) stderr.WriteLine($"wrote HAR to {harPath} ({harEntries.Count} entr{(harEntries.Count == 1 ? "y" : "ies")})");
        }

        // A comparison run succeeds when any address answered; --fail keeps its own meaning on top.
        if (responded == 0) return ExitCodes.Failure;
        if (fail && rows.Any(r => r.Response.StatusCode is >= 400)) return ExitCodes.Failure;
        return ExitCodes.Ok;
    }

    /// <summary>The creator version written into a captured HAR document — the same idiom as
    /// <c>certapi --version</c>: the assembly's informational version with any <c>+build</c>
    /// metadata stripped.</summary>
    /// <summary>Write the byte transcript where it was asked for. To a file when named — a large
    /// exchange is easier to read in an editor — and otherwise to stdout with the response, since
    /// the transcript IS the output the user asked for.</summary>
    private static void WriteWire(WireLog log, string? file, bool includeSecrets, bool frames,
                                  TextWriter stdout, TextWriter stderr)
    {
        string rendered = frames ? log.RenderFrames(includeSecrets) : log.Render(includeSecrets);
        if (file is null) { stdout.Write(rendered); return; }
        try
        {
            File.WriteAllText(file, rendered);
            stderr.WriteLine($"wrote the wire transcript to {file}");
        }
        catch (Exception ex) { stderr.WriteLine("warning: could not write the wire transcript: " + ex.Message); }
    }

    private static string HarCreatorVersion()
    {
        var version = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
        int plus = version.IndexOf('+');
        return plus > 0 ? version[..plus] : version;
    }

    private static IReadOnlyList<string> ResolveAll(string host)
    {
        System.Net.IPAddress[] found;
        try { found = System.Net.Dns.GetHostAddresses(host); }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or ArgumentException)
        { throw new CliDataException($"Could not resolve {host}: {ex.Message}"); }
        if (found.Length == 0) throw new CliDataException($"Could not resolve {host}: no addresses.");
        return found.Select(a => a.ToString()).ToList();
    }

    /// <summary>Like <see cref="CliWorkspace.Load"/>, but a missing explicit workspace file yields an
    /// empty workspace instead of an error — --capture is allowed to create the file on save. A
    /// corrupt *live* state (no --workspace given) is tolerated the same way the GUI tolerates it:
    /// warn on stderr and start fresh, rather than failing every plain send. An explicit --workspace
    /// keeps its current behavior — a corrupt file the caller named is a data error.</summary>
    private static AppState LoadWorkspaceOrEmpty(string? workspace, CliServices services, TextWriter stderr)
    {
        if (workspace is not null && !File.Exists(workspace)) return new AppState();
        if (workspace is null)
        {
            try { return CliWorkspace.Load(null, services.LiveStatePath, stderr); }
            catch (CliDataException ex)
            {
                stderr.WriteLine($"warning: could not read the live state ({ex.Message}) — continuing without saved tokens/environments");
                return new AppState();
            }
        }
        return CliWorkspace.Load(workspace, services.LiveStatePath, stderr);
    }

    private static void SaveState(AppState state, string? workspace, CliServices services, TextWriter stderr)
    {
        if (workspace is null && services.IsGuiRunning())
        {
            stderr.WriteLine("note: the GUI is running — captured values were not saved (it would overwrite them on close).");
            return;
        }
        string path = workspace ?? services.LiveStatePath;
        try { CliWorkspace.ReportSaveResult(state.SaveTo(path), path, stderr); }
        catch (Exception ex) { stderr.WriteLine($"warning: could not save captured values: {ex.Message}"); }
    }

    /// <summary>--query name=value pairs; a pair without '=' is a name with an empty value.</summary>
    internal static List<KeyValuePair<string, string>> ParseQuery(IEnumerable<string> specs)
    {
        var pairs = new List<KeyValuePair<string, string>>();
        foreach (var raw in specs)
        {
            int eq = raw.IndexOf('=');
            if (eq == 0) throw new CliUsageException($"--query expects name=value, got '{raw}'.");
            pairs.Add(eq < 0 ? new(raw, "") : new(raw[..eq], raw[(eq + 1)..]));
        }
        return pairs;
    }

    internal static string BuildEnvelope(ApiResponse r, bool includeBody, IReadOnlyList<string>? notes = null,
                                         DiffResult? diff = null, PhaseTimings? timing = null)
    {
        bool binary = false;
        string? text = null;
        if (includeBody && r.Error is null)
        {
            // UTF8.GetString never throws — invalid bytes decode to U+FFFD, which marks the body binary.
            text = Encoding.UTF8.GetString(r.Body);
            binary = text.Contains('�');
        }
        var obj = new Dictionary<string, object?>
        {
            ["status"] = r.StatusCode,
            ["reasonPhrase"] = r.ReasonPhrase,
            ["elapsedMs"] = Math.Round(r.Elapsed.TotalMilliseconds),
            ["contentType"] = r.ContentType,
            ["sizeBytes"] = r.Body.LongLength,
            ["headers"] = r.Headers
                .GroupBy(h => h.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    g => g.Key,
                    g => g.Count() == 1 ? (object)g.First().Value : g.Select(h => h.Value).ToArray()),
            ["tlsProtocol"] = r.Connection?.TlsProtocol,
            ["clientCertPresented"] = r.Connection?.ClientCertificateSent ?? false,
            // Enum .ToString() here matches how error.kind is rendered above, so a consumer parses
            // one consistent style rather than two.
            ["revocationMode"] = r.Connection?.RevocationMode.ToString(),
            ["revocationStatus"] = r.Connection?.RevocationStatus.ToString(),
            // kind/message keep the shape existing consumers already parse; socketError and the
            // flattened InnerException chain are additive, and null when the failure had neither.
            ["error"] = r.Error is null ? null : new
            {
                kind = r.Error.Kind.ToString(),
                message = r.Error.Message,
                socketError = r.Error.SocketErrorCode?.ToString(),
                detail = r.Error.Detail?.Select(d => new
                {
                    exceptionType = d.ExceptionType,
                    message = d.Message,
                    nativeErrorCode = d.NativeErrorCode
                })
            }
        };
        // Only when something was actually followed: adding "redirects": [] to every envelope would
        // churn the output of every existing user for nothing.
        if (r.Redirects.Count > 0)
            obj["redirects"] = r.Redirects.Select(h => new
            {
                status = h.StatusCode,
                from = h.From,
                to = h.To,
                authorizationDropped = h.AuthorizationDropped,
                schemeDowngrade = h.SchemeDowngrade
            }).ToList();
        // Only when a retry actually happened, for the same reason redirects is conditional: an
        // "attempts": 1 on every envelope would churn every existing consumer's output for nothing.
        if (r.Attempts > 1) obj["attempts"] = r.Attempts;
        // Only with --timing: milliseconds per phase, null for a phase that did not happen.
        if (timing is not null)
        {
            static double? Ms(TimeSpan? t) => t is { } v ? Math.Round(v.TotalMilliseconds, 1) : null;
            obj["timing"] = new
            {
                dnsMs = Ms(timing.Dns), connectMs = Ms(timing.Connect), tlsMs = Ms(timing.Tls),
                waitMs = Ms(timing.Wait), firstByteMs = Ms(timing.FirstByte), downloadMs = Ms(timing.Download),
                totalMs = Ms(timing.Total), reused = timing.Reused, requests = timing.Requests
            };
        }
        if (diff is not null)
            obj["diff"] = new
            {
                identical = diff.Identical,
                statusBefore = diff.StatusBefore,
                statusAfter = diff.StatusAfter,
                headers = diff.Headers.Select(h => new { name = h.Name, before = h.Before, after = h.After }).ToList(),
                body = diff.Body.Select(b => new
                {
                    path = b.Path,
                    kind = b.Kind.ToString(),
                    before = b.Before,
                    after = b.After
                }).ToList()
            };
        if (notes is { Count: > 0 }) obj["notes"] = notes;
        if (includeBody && r.Error is null)
        {
            if (binary) obj["bodyBase64"] = Convert.ToBase64String(r.Body);
            else obj["body"] = text;
        }
        return JsonSerializer.Serialize(obj, new JsonSerializerOptions { WriteIndented = true });
    }
}
