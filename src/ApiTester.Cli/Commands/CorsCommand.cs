using System.Text.Json;
using ApiTester.Core;

namespace ApiTester.Cli.Commands;

/// <summary>`certapi cors <url> --origin <o>`: would a browser on that origin be allowed to make
/// this request and read the answer?</summary>
public static class CorsCommand
{
    public const string Help = """
        Usage: certapi cors <url> --origin <origin> [options]
               certapi cors <url> --audit [--origin <trusted origin>] [options]
               certapi cors <url> --diff <other-url> --origin <origin> [options]
               certapi cors --from-har <file> [--origin <origin>] [options]
               certapi cors --from-openapi <spec.json> --origin <origin> [options]

        Checks a URL the way a browser checks a cross-origin request from a page on <origin>, with
        your client certificate: it works out whether the browser would send a preflight first and
        why, sends that preflight (OPTIONS, with no cookies or Authorization, as a browser does),
        then the request itself with the Origin header, and judges both against the CORS rules —
        Access-Control-Allow-Origin, -Credentials, -Methods, -Headers, -Max-Age, -Expose-Headers,
        cross-site Set-Cookie attributes, CORP/COEP/COOP, and Timing-Allow-Origin.
        Each rule that passed or failed is listed in words, then the verdict, and which response
        headers a script would be able to read.

          --origin <origin>       The page's origin, e.g. https://app.example.com (required).
                                  Repeatable, or comma-separated: several origins are checked
                                  in turn and shown as a table
          -X, --method <m>        The method the page's script uses (default GET). Repeatable
                                  or comma-separated, like --origin
          -H, --header "k: v"     A header the script sets (repeatable). Headers a page cannot set
                                  (Cookie, Host, Origin, Sec-*, …) are left out, as a browser would
          -d, --data <body>       A body; its Content-Type is application/json unless
                                  --content-type or -H "Content-Type: …" says otherwise
          --content-type <ct>     The body's content type
          --credentials           The page sends credentials (cookies, fetch credentials:
                                  "include"), which tightens every rule
          --actual                Also send the request itself when the method is not GET/HEAD
                                  (by default an unsafe method is only preflighted, so the check
                                  changes nothing on the server)
          --preflight-only        Never send the request itself
          --private-network       Check Private Network Access as well: the preflight carries
                                  Access-Control-Request-Private-Network: true and the answer
                                  must allow it (for a public page calling a private address)
          --audit                 Probe for misconfigurations instead: an arbitrary origin, null,
                                  look-alikes (prefix, suffix, port, userinfo, underscore, backtick,
                                  unescaped dot), plain http, localhost dev origins (for public
                                  targets), missing Vary: Origin, and preflight reflection, each
                                  graded by what it can read. Only GET and OPTIONS are sent.
                                  Exit 1 on a high or medium finding
          --preflight-without-cert  Send the preflight without the client certificate, as the
                                  Fetch standard does: a server that demands one in the TLS
                                  handshake then refuses the preflight before CORS is reached
                                  (with --audit, also flags if an unauthenticated OPTIONS fails)
          --suggest [<framework>] Also print the server configuration that grants what was
                                  checked: aspnet, nginx, express, spring, iis, apigateway, apim,
                                  yarp, cloudflare, caddy, traefik, apache, fastapi or go
                                  (all of them when none is named; included in --json output too)
          --max-age <seconds>     Preflight Access-Control-Max-Age used in --suggest (default 600)
          --diff <other-url>      Compare the CORS policy of <url> against <other-url> for every
                                  --origin and -X combination (e.g. staging vs production).
                                  Exit 0 when identical, 1 when they differ
          --from-har <file>       Check every cross-origin request in a browser capture instead
                                  of a URL: the recorded preflight and response of each request
                                  that carried an Origin, judged as the browser judged them
                                  (--origin judges them all as if from that page)
          --from-openapi <file>   Sweep every operation in an OpenAPI 3.x / Swagger 2.0 JSON spec
                                  against --origin (GET/HEAD in full, unsafe methods preflighted
                                  unless --actual). Use --base-url to override the spec's server
          --base-url <url>        With --from-openapi, override the spec's base server URL
          --live                  With --from-har, also re-check each distinct request now
                                  (GET and HEAD in full, anything else by its preflight)
          --json                  The result as JSON
          -q, --quiet             Only the verdict line (with --from-har, only blocked requests)

        TLS / certificates:
        """ + "\n" + CliCert.HelpLines + "\n" + """
          --insecure              Ignore server certificate errors

        """ + "\n" + TransportFlags.Help + "\n" + """

        A ws:// or wss:// URL is a WebSocket: CORS does not apply to it, and a browser sends cookies
        and the client certificate on the handshake from any page, so the server's own Origin check
        is what matters. --origin tries a handshake from each origin (accepted or refused), and
        --audit tries the audit's origins and grades cross-site WebSocket hijacking.

        Redirects are followed one hop at a time, as a browser follows them: each hop is checked,
        a new preflight is sent where the new address needs one, and after a redirect to another
        origin the request carries Origin: null. --max-redirs and --no-redirect apply.

        Global: --debug (verbose diagnostics) and --log-file <path> work here too.

        Examples:
          certapi cors https://api.example.com/orders --origin https://app.example.com --cert "CN=svc"
          certapi cors https://api.example.com/orders --origin http://localhost:3000 -X PUT -H "X-Api-Key: k" --credentials
          certapi cors https://api.example.com/orders --origin https://app.example.com -X DELETE --actual --json
          certapi cors https://api.example.com/orders --origin https://app.example.com,http://localhost:3000 -X GET,PUT
          certapi cors https://api.example.com/me --audit --origin https://app.example.com --cert "CN=svc"
          certapi cors https://api.example.com/orders --origin https://app.example.com -X PUT --suggest apim --max-age 3600
          certapi cors https://staging.example.com/orders --diff https://api.example.com/orders --origin https://app.example.com -X GET,PUT
          certapi cors --from-har portal.har --live --cert "CN=svc"
          certapi cors --from-openapi openapi.json --base-url https://staging.example.com --origin https://app.example.com
          certapi cors wss://api.example.com/hub --audit --origin https://app.example.com --cert "CN=svc"

        Exit 0 when a browser would allow it (every combination, for a table), 1 when it would block it.
        """;

    public static int Run(Args args, TextWriter stdout, TextWriter stderr, CliServices services)
    {
        var originSpecs = args.Values("--origin");
        var methodSpecs = args.Values("-X", "--method");
        var headerSpecs = args.Values("-H", "--header");
        string? data = args.Value("-d", "--data");
        string? contentType = args.Value("--content-type");
        bool credentials = args.Flag("--credentials");
        bool actual = args.Flag("--actual");
        bool preflightOnly = args.Flag("--preflight-only");
        bool audit = args.Flag("--audit");
        bool privateNetwork = args.Flag("--private-network");
        string? fromHar = args.Value("--from-har");
        string? fromOpenApi = args.Value("--from-openapi");
        string? baseUrlOverride = args.Value("--base-url");
        string? diffUrl = args.Value("--diff");
        bool live = args.Flag("--live");
        bool suggest = args.OptionalValue(out var suggestFor, "--suggest");
        string? maxAgeRaw = args.Value("--max-age");
        int maxAge = 600;
        if (maxAgeRaw is not null && (!int.TryParse(maxAgeRaw, out maxAge) || maxAge < 0))
            throw new CliUsageException($"--max-age expects a whole number of seconds (0 or more), got '{maxAgeRaw}'.");
        bool preflightWithoutCert = args.Flag("--preflight-without-cert");
        bool json = args.Flag("--json");
        bool quiet = args.Flag("-q", "--quiet");
        string store = args.Value("--store") ?? services.Profile?.Store ?? "CurrentUser";
        bool insecure = args.Switch("--insecure", services.Profile?.Insecure);
        var overrides = TransportFlags.Parse(args, out _, environment: null, services.Profile);
        var cert = CliCert.Resolve(args, store, services, stderr);

        if (suggestFor is not null && !CorsSuggest.Frameworks.Contains(suggestFor, StringComparer.OrdinalIgnoreCase))
            throw new CliUsageException($"--suggest takes one of {string.Join(", ", CorsSuggest.Frameworks)} (or nothing, for all of them), got '{suggestFor}'.");
        if (live && fromHar is null) throw new CliUsageException("--live re-checks the requests of a capture, so it needs --from-har <file>.");
        if (baseUrlOverride is not null && fromOpenApi is null)
            throw new CliUsageException("--base-url only applies with --from-openapi <file>.");
        if (fromHar is not null && fromOpenApi is not null)
            throw new CliUsageException("--from-har and --from-openapi cannot be used together.");
        if (diffUrl is not null && (fromHar is not null || fromOpenApi is not null || audit))
            throw new CliUsageException("--diff compares two URLs, so it cannot be combined with --audit, --from-har or --from-openapi.");
        var positionals = args.Positionals();
        if (fromHar is not null)
        {
            if (positionals.Count != 0) throw new CliUsageException("--from-har takes the requests from the capture: give no URL.");
            if (audit || methodSpecs.Count > 0 || data is not null || actual || preflightOnly)
                throw new CliUsageException("--from-har checks the requests the capture holds; --audit, -X, -d, --actual and --preflight-only do not apply.");
        }
        else if (fromOpenApi is not null)
        {
            if (positionals.Count != 0) throw new CliUsageException("--from-openapi takes the endpoints from the spec: give no URL (use --base-url to override the server).");
            if (audit || methodSpecs.Count > 0 || data is not null)
                throw new CliUsageException("--from-openapi sweeps the spec's own operations; --audit, -X and -d do not apply.");
        }
        else if (positionals.Count != 1) throw new CliUsageException(Help);
        string url = fromHar is null && fromOpenApi is null ? positionals[0] : "";
        Uri target = new("http://localhost/");
        if (fromHar is null && fromOpenApi is null && (!Uri.TryCreate(url, UriKind.Absolute, out target!) || target.Scheme is not ("http" or "https" or "ws" or "wss")))
            throw new CliUsageException($"cors needs an http://, https://, ws:// or wss:// URL, got '{url}'.");
        if (diffUrl is not null && (!Uri.TryCreate(diffUrl, UriKind.Absolute, out var diffTarget) || diffTarget.Scheme is not ("http" or "https")))
            throw new CliUsageException($"--diff expects an http:// or https:// URL, got '{diffUrl}'.");
        bool webSocket = target.Scheme is "ws" or "wss";
        if (webSocket && (methodSpecs.Count > 0 || data is not null || credentials || actual || preflightOnly || privateNetwork || suggest || preflightWithoutCert || diffUrl is not null))
            throw new CliUsageException("A WebSocket handshake has no CORS: -X, -d, --credentials, --actual, --preflight-only, --private-network, --suggest, --diff and --preflight-without-cert do not apply to a ws:// or wss:// URL.");
        var origins = originSpecs.SelectMany(o => o.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Select(NormalizeOrigin).Distinct(StringComparer.Ordinal).ToList();
        var methods = methodSpecs.SelectMany(m => m.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Select(m => m.ToUpperInvariant()).Distinct(StringComparer.Ordinal).ToList();
        if (methods.Count == 0) methods.Add("GET");
        if (actual && preflightOnly) throw new CliUsageException("--actual and --preflight-only contradict each other; give one.");
        foreach (var m in methods)
            if (m.Any(c => char.IsWhiteSpace(c) || !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.')))
                throw new CliUsageException($"-X expects a method name, got '{m}'.");
        if (audit)
        {
            if (origins.Count > 1) throw new CliUsageException("--audit takes one --origin: the origin the API is meant to trust.");
            if (methodSpecs.Count > 0 || data is not null || actual || preflightOnly || privateNetwork || credentials)
                throw new CliUsageException("--audit sends its own GET and OPTIONS probes, so -X, -d, --credentials, --actual, --preflight-only and --private-network do not apply.");
        }
        else if (origins.Count == 0 && fromHar is null)
            throw new CliUsageException("cors needs --origin: the origin of the page making the request, e.g. https://app.example.com (or --audit to probe for misconfigurations).");
        if (data is not null && data.StartsWith('@'))
        {
            string path = data[1..];
            if (!File.Exists(path)) throw new CliDataException($"-d: file not found: {path}");
            data = File.ReadAllText(path);
        }

        var headers = new List<KeyValuePair<string, string>>();
        foreach (var raw in headerSpecs)
        {
            int colon = raw.IndexOf(':');
            if (colon <= 0) throw new CliUsageException($"Header must be \"Name: value\", got '{raw}'.");
            headers.Add(new(raw[..colon].Trim(), raw[(colon + 1)..].Trim()));
        }
        if (contentType is not null)
        {
            headers.RemoveAll(h => h.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase));
            headers.Add(new("Content-Type", contentType));
        }
        else if (data is not null && !headers.Any(h => h.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)))
            headers.Add(new("Content-Type", "application/json"));

        var parsed = overrides.ApplyTo(new TransportOptions { IgnoreServerCertificateErrors = insecure });
        int maxRedirects = parsed.FollowRedirects ? parsed.MaxRedirects : 0;
        // Redirects are followed here, one hop at a time, the way a browser follows them — never by
        // the HTTP stack, which would hide each hop's CORS answer.
        var transport = parsed with { FollowRedirects = false };
        if (fromHar is null && fromOpenApi is null && !webSocket && ApiClient.ValidateTransport(transport, url) is { } problem) throw new CliUsageException(problem);
        var state = CliWorkspace.Load(null, services.LiveStatePath, TextWriter.Null);
        var predicates = new TrustPredicates(state);
        // --preflight-without-cert: a preflight goes without the client certificate, as the Fetch
        // standard sends it — the test for a server that demands one during the handshake.
        Task<ApiResponse> Send(ApiRequest r)
        {
            bool isPreflight = r.Method == HttpMethod.Options && r.Headers.Any(h => h.Key.Equals("Access-Control-Request-Method", StringComparison.OrdinalIgnoreCase));
            return services.Client.SendAsync(r, preflightWithoutCert && isPreflight ? null : cert, transport,
                predicates.For(new Uri(r.Url).Host), cancellationToken: services.Cancel);
        }
        Task<ApiResponse> SendNoCert(ApiRequest r) =>
            services.Client.SendAsync(r, null, transport, predicates.For(new Uri(r.Url).Host), cancellationToken: services.Cancel);

        if (webSocket)
            return RunWebSocket(url, target, origins, audit, headers, cert, transport, predicates.For(target.Host), json, quiet, stdout, services);
        if (fromHar is not null)
            return RunHar(fromHar, origins, credentials, privateNetwork, live, suggest, suggestFor, maxAge, json, quiet, Send, stdout, stderr);
        if (fromOpenApi is not null)
            return RunOpenApi(fromOpenApi, baseUrlOverride, origins, headers, credentials, privateNetwork, actual, preflightOnly, maxRedirects, suggest, suggestFor, maxAge, json, quiet, Send, stdout);

        if (audit)
        {
            var trusted = origins.Count == 1 ? new Uri(origins[0]) : new Uri(target.GetLeftPart(UriPartial.Authority));
            bool includeDev = !TargetIsPrivate(target);
            Func<ApiRequest, Task<ApiResponse>>? auditNoCert = preflightWithoutCert ? SendNoCert : null;
            var result = CorsAudit.RunAsync(url, trusted, headers, Send, TimeSpan.FromSeconds(100), includeDev, auditNoCert).GetAwaiter().GetResult();
            string? suggestion = suggest
                ? CorsSuggest.For(new CorsNeeds(new[] { result.BaseOrigin }, new[] { "GET" }, Array.Empty<string>(), Array.Empty<string>(), true, false), suggestFor, maxAge)
                : null;
            if (json) stdout.WriteLine(AuditJson(result, url, suggestion));
            else if (quiet) stdout.WriteLine(result.Worst is { } w ? $"{w.ToString().ToUpperInvariant()}: {result.Findings[0].Title}" : "no findings");
            else stdout.WriteLine(CorsAudit.Describe(result, url));
            if (suggestion is not null && !json)
                stdout.WriteLine("\n" + suggestion);
            return result.Worst is CorsSeverity.High or CorsSeverity.Medium ? ExitCodes.Failure : ExitCodes.Ok;
        }

        if (diffUrl is not null)
            return RunDiff(url, diffUrl, origins, methods, headers, data, contentType, credentials, privateNetwork, actual, preflightOnly, maxRedirects, json, quiet, Send, stdout);

        // A public page reaching a private or loopback address is what Private Network Access is
        // about; the check is on when asked for, and the case is pointed out when it is not.
        bool targetPrivate = TargetIsPrivate(target);
        var results = new List<(CorsRequest Request, CorsProbeResult Result)>();
        foreach (var origin in origins)
            foreach (var method in methods)
            {
                var request = new CorsRequest(origin, method, headers, credentials) { PrivateNetwork = privateNetwork };
                var result = CorsProbe.RunAsync(url, request, data, contentType, preflightOnly ? false : actual ? true : null,
                    Send, TimeSpan.FromSeconds(100), maxRedirects).GetAwaiter().GetResult();
                if (!privateNetwork && targetPrivate && !AddressSpace.IsLocalOrigin(origin))
                    result = result with
                    {
                        Verdict = result.Verdict with
                        {
                            Notes = result.Verdict.Notes.Append(
                                $"{target.Host} is a private or loopback address and {origin} is not: current Chrome asks the user for permission to reach the local network (earlier versions sent a Private Network Access preflight, which --private-network checks)").ToList()
                        }
                    };
                results.Add((request, result));
            }

        string? suggestText = suggest
            ? CorsSuggest.For(CorsNeeds.From(results.Select(r => (r.Request, (CorsProbeResult?)r.Result))), suggestFor, maxAge)
            : null;

        if (results.Count == 1)
        {
            var (request, result) = results[0];
            if (json) stdout.WriteLine(Json(result, request, url, suggestText));
            else if (quiet) stdout.WriteLine(CorsProbe.Verdict(result, request));
            else stdout.WriteLine(CorsProbe.Describe(result, request, url));
            if (suggestText is not null && !json) stdout.WriteLine("\n" + suggestText);
            return result.Verdict.Allowed ? ExitCodes.Ok : ExitCodes.Failure;
        }

        if (json)
        {
            if (suggestText is not null)
                stdout.WriteLine(JsonSerializer.Serialize(new
                {
                    results = results.Select(r => JsonSerializer.Deserialize<JsonElement>(Json(r.Result, r.Request, url))),
                    suggestion = suggestText
                }, new JsonSerializerOptions { WriteIndented = true }));
            else
                stdout.WriteLine("[" + string.Join(",\n", results.Select(r => Json(r.Result, r.Request, url))) + "]");
        }
        else
        {
            int width = Math.Max(6, origins.Max(o => o.Length));
            stdout.WriteLine($"{"origin".PadRight(width)}  {"method",-7} {"preflight",-9} {"status",-6} verdict");
            foreach (var (request, result) in results)
            {
                var v = result.Verdict;
                string preflight = !v.PreflightNeeded ? "-" : result.Preflight is { Error: null } p ? p.StatusCode.ToString()! : "failed";
                string status = result.Response is { Error: null } r ? r.StatusCode.ToString()! : result.NotSent is not null ? "unsent" : "-";
                string verdict = v.Allowed ? (result.Response is null ? "preflight ok" : "allowed") : "BLOCKED: " + v.Problems[0];
                stdout.WriteLine($"{request.Origin.PadRight(width)}  {request.Method,-7} {preflight,-9} {status,-6} {verdict}");
            }
            if (!quiet && results.Any(r => r.Result.NotSent is not null && r.Result.Verdict.Allowed))
                stdout.WriteLine("(preflight ok: the request itself was not sent, so the response was not checked; --actual sends it)");
            if (suggestText is not null)
                stdout.WriteLine("\n" + suggestText +
                    "\nnote       every --origin given is in the allow-list above; take out any you checked in order to keep it out");
        }
        return results.All(r => r.Result.Verdict.Allowed) ? ExitCodes.Ok : ExitCodes.Failure;
    }

    private static int RunWebSocket(string url, Uri target, List<string> origins, bool audit, List<KeyValuePair<string, string>> headers,
        System.Security.Cryptography.X509Certificates.X509Certificate2? cert, TransportOptions transport,
        Func<System.Security.Cryptography.X509Certificates.X509Certificate2?, bool> trust, bool json, bool quiet, TextWriter stdout, CliServices services)
    {
        async Task<(bool, int?, string?)> Connect(string origin)
        {
            await using var session = new WebSocketSession();
            var sent = headers.Where(h => !h.Key.Equals("Origin", StringComparison.OrdinalIgnoreCase)).Append(new("Origin", origin)).ToList();
            try
            {
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(services.Cancel);
                limit.CancelAfter(TimeSpan.FromSeconds(30));
                await session.ConnectAsync(url, cert, sent, transport.IgnoreServerCertificateErrors, transport, trust, limit.Token);
                await session.CloseAsync(CancellationToken.None);
                return (true, 101, null);
            }
            catch (Exception ex) when (ex is System.Net.WebSockets.WebSocketException or HttpRequestException or OperationCanceledException or InvalidOperationException)
            {
                return (false, session.HandshakeStatus, ex.Message);
            }
        }

        var trusted = origins.Count == 1 ? new Uri(origins[0]) : new Uri((target.Scheme == "wss" ? "https://" : "http://") + target.Authority);
        var list = audit ? CorsAudit.ProbesFor(trusted) : origins.Select(o => ("origin", o)).ToList();
        var probes = WebSocketOrigins.ProbeAsync(list, Connect).GetAwaiter().GetResult();
        var findings = audit ? WebSocketOrigins.Grade(probes) : null;
        if (json)
            stdout.WriteLine(JsonSerializer.Serialize(new
            {
                url,
                worst = findings is null || findings.Count == 0 ? null : findings.Max(f => f.Severity).ToString().ToLowerInvariant(),
                findings = findings?.Select(f => new { severity = f.Severity.ToString().ToLowerInvariant(), title = f.Title, detail = f.Detail }),
                probes = probes.Select(p => new { kind = p.Kind, origin = p.Origin, accepted = p.Accepted, status = p.Status, error = p.Error })
            }, new JsonSerializerOptions { WriteIndented = true }));
        else if (quiet)
            stdout.WriteLine(findings is not null
                ? findings.Count == 0 ? "no findings" : $"{findings[0].Severity.ToString().ToUpperInvariant()}: {findings[0].Title}"
                : string.Join("\n", probes.Select(p => $"{p.Origin}: {(p.Accepted ? "accepted" : "refused")}")));
        else stdout.WriteLine(WebSocketOrigins.Describe(probes, findings, url));
        if (audit) return findings!.Any(f => f.Severity is CorsSeverity.High or CorsSeverity.Medium) ? ExitCodes.Failure : ExitCodes.Ok;
        return probes.All(p => p.Accepted) ? ExitCodes.Ok : ExitCodes.Failure;
    }

    private static int RunHar(string file, List<string> origins, bool credentials, bool privateNetwork, bool live, bool suggest, string? suggestFor, int maxAge,
        bool json, bool quiet, Func<ApiRequest, Task<ApiResponse>> send, TextWriter stdout, TextWriter stderr)
    {
        if (origins.Count > 1) throw new CliUsageException("--from-har takes at most one --origin (to judge every request as if from that page).");
        if (!File.Exists(file)) throw new CliDataException($"--from-har: file not found: {file}");
        Har har;
        try { har = HarReader.Parse(File.ReadAllText(file)); }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidDataException) { throw new CliDataException($"--from-har: {file} is not a HAR file: {ex.Message}"); }
        var rows = CorsHar.Check(har, origins.FirstOrDefault(), credentials);
        var liveResults = new List<(CorsRequest Request, CorsProbeResult Result, string Url)>();
        if (live)
        {
            // Each distinct request once, as `cors` would check it: only GET and HEAD are sent in
            // full; anything else is preflighted only.
            foreach (var group in rows.GroupBy(r => (r.Method, r.Url, r.Origin)))
            {
                var row = group.First();
                var entry = har.Log.Entries.First(e => e.Request.Url == row.Url && e.Request.Method.Equals(row.Method, StringComparison.OrdinalIgnoreCase));
                var requestHeaders = entry.Request.Headers.Where(h => !h.Name.StartsWith(':') && !h.Name.Equals("Origin", StringComparison.OrdinalIgnoreCase))
                    .Select(h => new KeyValuePair<string, string>(h.Name, h.Value)).ToList();
                var request = new CorsRequest(row.Origin, row.Method, requestHeaders, row.Credentials) { PrivateNetwork = privateNetwork };
                var result = CorsProbe.RunAsync(row.Url, request, null, null, null, send, TimeSpan.FromSeconds(100)).GetAwaiter().GetResult();
                liveResults.Add((request, result, row.Url));
            }
        }
        string? suggestion = suggest
            ? CorsSuggest.For(CorsNeeds.From(rows.Select(r => (
                new CorsRequest(r.Origin, r.Method, har.Log.Entries.First(e => e.Request.Url == r.Url).Request.Headers
                    .Where(h => !h.Name.StartsWith(':')).Select(h => new KeyValuePair<string, string>(h.Name, h.Value)).ToList(), r.Credentials),
                (CorsProbeResult?)null))), suggestFor, maxAge)
            : null;

        if (json)
        {
            stdout.WriteLine(JsonSerializer.Serialize(new
            {
                file,
                recorded = rows.Select(r => new
                {
                    method = r.Method, url = MarkdownSecrets.RedactUrl(r.Url), origin = r.Origin, credentials = r.Credentials, status = r.Status,
                    preflightReason = r.PreflightReason, preflightRecorded = r.PreflightRecorded, allowed = r.Allowed, problems = r.Problems, notes = r.Notes
                }),
                live = live ? liveResults.Select(l => new { method = l.Request.Method, url = MarkdownSecrets.RedactUrl(l.Url), origin = l.Request.Origin, allowed = l.Result.Verdict.Allowed, problems = l.Result.Verdict.Problems }) : null,
                suggestion
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        else
        {
            if (rows.Count == 0) stdout.WriteLine($"no cross-origin requests in {file} (requests with an Origin header that differs from their own origin{(origins.Count == 0 ? "; --origin judges every request as if from that page" : "")})");
            foreach (var r in rows)
            {
                if (quiet && r.Allowed) continue;
                stdout.WriteLine($"{(r.Allowed ? "ok     " : "BLOCKED")}  {r.Status,3}  {r.Method,-7} {MarkdownSecrets.RedactUrl(r.Url)}  (from {r.Origin}{(r.Credentials ? ", credentials" : "")}{(r.PreflightReason is null ? "" : r.PreflightRecorded ? ", preflighted" : ", preflight not captured")})");
                foreach (var p in r.Problems) stdout.WriteLine("           " + p);
                if (!quiet) foreach (var n in r.Notes) stdout.WriteLine("           note: " + n);
            }
            if (rows.Count > 0)
                stdout.WriteLine($"{rows.Count(r => r.Allowed)} of {rows.Count} cross-origin request(s) allowed, as recorded");
            if (live)
            {
                stdout.WriteLine();
                stdout.WriteLine("live re-check:");
                foreach (var l in liveResults)
                {
                    stdout.WriteLine($"{(l.Result.Verdict.Allowed ? "ok     " : "BLOCKED")}  {l.Request.Method,-7} {MarkdownSecrets.RedactUrl(l.Url)}  (from {l.Request.Origin})");
                    foreach (var p in l.Result.Verdict.Problems) stdout.WriteLine("           " + p);
                }
            }
            if (suggestion is not null)
                stdout.WriteLine("\n" + suggestion);
        }
        bool ok = rows.All(r => r.Allowed) && liveResults.All(l => l.Result.Verdict.Allowed);
        return ok ? ExitCodes.Ok : ExitCodes.Failure;
    }

    private static int RunOpenApi(string file, string? baseUrlOverride, List<string> origins, List<KeyValuePair<string, string>> headers,
        bool credentials, bool privateNetwork, bool actual, bool preflightOnly, int maxRedirects,
        bool suggest, string? suggestFor, int maxAge, bool json, bool quiet,
        Func<ApiRequest, Task<ApiResponse>> send, TextWriter stdout)
    {
        if (!File.Exists(file)) throw new CliDataException($"--from-openapi: file not found: {file}");
        ParsedCollection spec;
        try { spec = OpenApiImporter.Parse(File.ReadAllText(file)); }
        catch (Exception ex) when (ex is JsonException or FormatException)
        {
            throw new CliDataException($"--from-openapi: could not parse {file}: {ex.Message}");
        }

        string? baseUrl = baseUrlOverride ?? spec.BaseUrl;
        if (string.IsNullOrWhiteSpace(baseUrl) || !Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri) || baseUri.Scheme is not ("http" or "https"))
            throw new CliUsageException("--from-openapi: the spec has no absolute http(s):// server URL; pass --base-url <url>.");

        var ops = spec.Folders.SelectMany(f => f.Requests).Concat(spec.Requests)
            .Select(r => (Method: r.Method.ToUpperInvariant(), Path: System.Text.RegularExpressions.Regex.Replace(r.Url, @"\{[^}]+\}", "1")))
            .Distinct()
            .ToList();
        if (ops.Count == 0) throw new CliDataException($"--from-openapi: {file} defines no operations under \"paths\".");

        string root = baseUrl.TrimEnd('/');
        var checks = new List<(string Url, CorsRequest Request, CorsProbeResult Result)>();
        foreach (var op in ops)
        {
            string fullUrl = op.Path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || op.Path.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                ? op.Path
                : root + (op.Path.StartsWith('/') ? op.Path : "/" + op.Path);
            foreach (var origin in origins)
            {
                var opHeaders = headers.ToList();
                if (op.Method is "POST" or "PUT" or "PATCH" &&
                    !opHeaders.Any(h => h.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)))
                    opHeaders.Add(new("Content-Type", "application/json"));
                var req = new CorsRequest(origin, op.Method, opHeaders, credentials) { PrivateNetwork = privateNetwork };
                var res = CorsProbe.RunAsync(fullUrl, req, null, null, preflightOnly ? false : actual ? true : null,
                    send, TimeSpan.FromSeconds(100), maxRedirects).GetAwaiter().GetResult();
                checks.Add((fullUrl, req, res));
            }
        }

        string? suggestion = suggest
            ? CorsSuggest.For(CorsNeeds.From(checks.Select(c => (c.Request, (CorsProbeResult?)c.Result))), suggestFor, maxAge)
            : null;

        if (json)
        {
            stdout.WriteLine(JsonSerializer.Serialize(new
            {
                file,
                baseUrl = root,
                checks = checks.Select(c => new
                {
                    url = MarkdownSecrets.RedactUrl(c.Url),
                    origin = c.Request.Origin,
                    method = c.Request.Method,
                    allowed = c.Result.Verdict.Allowed,
                    preflightNeeded = c.Result.Verdict.PreflightNeeded,
                    preflightStatus = c.Result.Preflight?.StatusCode,
                    status = c.Result.Response?.StatusCode,
                    notSent = c.Result.NotSent,
                    problems = c.Result.Verdict.Problems
                }),
                suggestion
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        else
        {
            int oWidth = Math.Max(6, origins.Max(o => o.Length));
            int uWidth = Math.Max(8, checks.Max(c => MarkdownSecrets.RedactUrl(c.Url).Length));
            if (!quiet) stdout.WriteLine($"{"method",-7} {"url".PadRight(uWidth)}  {"origin".PadRight(oWidth)}  verdict");
            foreach (var (u, req, res) in checks)
            {
                if (quiet && res.Verdict.Allowed) continue;
                string verdict = res.Verdict.Allowed
                    ? (res.Response is null ? "preflight ok" : "allowed")
                    : "BLOCKED: " + res.Verdict.Problems[0];
                stdout.WriteLine($"{req.Method,-7} {MarkdownSecrets.RedactUrl(u).PadRight(uWidth)}  {req.Origin.PadRight(oWidth)}  {verdict}");
            }
            stdout.WriteLine($"{checks.Count(c => c.Result.Verdict.Allowed)} of {checks.Count} operation check(s) allowed");
            if (suggestion is not null) stdout.WriteLine("\n" + suggestion);
        }
        return checks.All(c => c.Result.Verdict.Allowed) ? ExitCodes.Ok : ExitCodes.Failure;
    }

    private static int RunDiff(string leftUrl, string rightUrl, List<string> origins, List<string> methods,
        List<KeyValuePair<string, string>> headers, string? data, string? contentType,
        bool credentials, bool privateNetwork, bool actual, bool preflightOnly, int maxRedirects,
        bool json, bool quiet, Func<ApiRequest, Task<ApiResponse>> send, TextWriter stdout)
    {
        var allDiffs = new List<CorsDiffEntry>();
        foreach (var origin in origins)
            foreach (var method in methods)
            {
                var req = new CorsRequest(origin, method, headers, credentials) { PrivateNetwork = privateNetwork };
                bool? sendActual = preflightOnly ? false : actual ? true : null;
                var left = CorsProbe.RunAsync(leftUrl, req, data, contentType, sendActual, send, TimeSpan.FromSeconds(100), maxRedirects).GetAwaiter().GetResult();
                var right = CorsProbe.RunAsync(rightUrl, req, data, contentType, sendActual, send, TimeSpan.FromSeconds(100), maxRedirects).GetAwaiter().GetResult();
                allDiffs.AddRange(CorsDiff.Compare(req, left, right));
            }

        if (json)
        {
            stdout.WriteLine(JsonSerializer.Serialize(new
            {
                leftUrl = MarkdownSecrets.RedactUrl(leftUrl),
                rightUrl = MarkdownSecrets.RedactUrl(rightUrl),
                identical = allDiffs.Count == 0,
                differences = allDiffs.Select(d => new { origin = d.Origin, method = d.Method, field = d.Field, left = d.Left, right = d.Right })
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        else if (quiet)
        {
            stdout.WriteLine(allDiffs.Count == 0 ? "identical" : $"{allDiffs.Count} difference(s)");
        }
        else
        {
            stdout.WriteLine(CorsDiff.Describe(MarkdownSecrets.RedactUrl(leftUrl), MarkdownSecrets.RedactUrl(rightUrl), allDiffs));
        }
        return allDiffs.Count == 0 ? ExitCodes.Ok : ExitCodes.Failure;
    }

    internal static bool TargetIsPrivate(Uri target)
    {
        string host = target.Host.Trim('[', ']');
        if (System.Net.IPAddress.TryParse(host, out var ip)) return AddressSpace.IsPrivate(ip);
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
        try
        {
            var addresses = System.Net.Dns.GetHostAddresses(host);
            return addresses.Length > 0 && addresses.All(AddressSpace.IsPrivate);
        }
        catch (System.Net.Sockets.SocketException) { return false; }
    }

    internal static string AuditJson(CorsAuditResult result, string url, string? suggestion = null) =>
        JsonSerializer.Serialize(new
        {
            url,
            trustedOrigin = result.BaseOrigin,
            worst = result.Worst?.ToString().ToLowerInvariant(),
            findings = result.Findings.Select(f => new { severity = f.Severity.ToString().ToLowerInvariant(), title = f.Title, detail = f.Detail }),
            probes = result.Probes.Select(p => new
            {
                kind = p.Kind, origin = p.Origin, status = p.Status, allowOrigin = p.AllowOrigin,
                allowCredentials = p.AllowCredentials, allowed = p.Reflected, error = p.Error
            }),
            suggestion
        }, new JsonSerializerOptions { WriteIndented = true });

    /// <summary>An origin is scheme://host[:port] with nothing after it; a trailing slash or a path
    /// typed by habit is dropped, because a browser never sends one.</summary>
    internal static string NormalizeOrigin(string raw)
    {
        if (raw == "null") return raw;
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https") || u.Host.Length == 0)
            throw new CliUsageException($"--origin expects scheme://host[:port] (or null), got '{raw}'.");
        return u.GetLeftPart(UriPartial.Authority);
    }

    internal static string Json(CorsProbeResult result, CorsRequest request, string url, string? suggestion = null)
    {
        var v = result.Verdict;
        static object? Exchange(ApiResponse? r) => r is null ? null : new
        {
            status = r.Error is null ? r.StatusCode : (int?)null,
            error = r.Error?.Message,
            headers = r.Headers.Where(h => h.Key.StartsWith("Access-Control-", StringComparison.OrdinalIgnoreCase) || h.Key.Equals("Vary", StringComparison.OrdinalIgnoreCase))
                .Select(h => new { name = h.Key, value = h.Value })
        };
        return JsonSerializer.Serialize(new
        {
            url,
            origin = request.Origin,
            method = request.Method,
            credentials = request.Credentials,
            allowed = v.Allowed,
            preflightNeeded = v.PreflightNeeded,
            preflightReason = v.PreflightReason,
            preflight = Exchange(result.Preflight),
            response = Exchange(result.Response),
            notSent = result.NotSent,
            hops = result.Hops.Select(h => new { url = h.Url, origin = h.Origin, method = h.Method,
                preflightStatus = h.Preflight?.StatusCode, status = h.Response?.StatusCode, redirectTo = h.RedirectTo }),
            passed = v.Passed,
            problems = v.Problems,
            notes = v.Notes,
            readableHeaders = v.Readable,
            hiddenHeaders = v.Hidden,
            suggestion
        }, new JsonSerializerOptions { WriteIndented = true });
    }
}
