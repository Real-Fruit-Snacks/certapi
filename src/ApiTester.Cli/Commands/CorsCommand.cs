using System.Text.Json;
using ApiTester.Core;

namespace ApiTester.Cli.Commands;

/// <summary>`certapi cors <url> --origin <o>`: would a browser on that origin be allowed to make
/// this request and read the answer?</summary>
public static class CorsCommand
{
    public const string Help = """
        Usage: certapi cors <url> --origin <origin> [options]

        Checks a URL the way a browser checks a cross-origin request from a page on <origin>, with
        your client certificate: it works out whether the browser would send a preflight first and
        why, sends that preflight (OPTIONS, with no cookies or Authorization, as a browser does),
        then the request itself with the Origin header, and judges both against the CORS rules —
        Access-Control-Allow-Origin, -Credentials, -Methods, -Headers, -Max-Age, -Expose-Headers.
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
                                  look-alikes of the --origin you trust (or of the URL's own
                                  origin) and plain http, each graded by what it can read. Only
                                  GET and OPTIONS are sent. Exit 1 on a high or medium finding
          --json                  The result as JSON
          -q, --quiet             Only the verdict line

        TLS / certificates:
        """ + "\n" + CliCert.HelpLines + "\n" + """
          --insecure              Ignore server certificate errors

        """ + "\n" + TransportFlags.Help + "\n" + """

        Redirects are followed one hop at a time, as a browser follows them: each hop is checked,
        a new preflight is sent where the new address needs one, and after a redirect to another
        origin the request carries Origin: null. --max-redirs and --no-redirect apply.

        Examples:
          certapi cors https://api.example.com/orders --origin https://app.example.com --cert "CN=svc"
          certapi cors https://api.example.com/orders --origin http://localhost:3000 -X PUT -H "X-Api-Key: k" --credentials
          certapi cors https://api.example.com/orders --origin https://app.example.com -X DELETE --actual --json
          certapi cors https://api.example.com/orders --origin https://app.example.com,http://localhost:3000 -X GET,PUT
          certapi cors https://api.example.com/me --audit --origin https://app.example.com --cert "CN=svc"

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
        bool json = args.Flag("--json");
        bool quiet = args.Flag("-q", "--quiet");
        string store = args.Value("--store") ?? services.Profile?.Store ?? "CurrentUser";
        bool insecure = args.Switch("--insecure", services.Profile?.Insecure);
        var overrides = TransportFlags.Parse(args, out _, environment: null, services.Profile);
        var cert = CliCert.Resolve(args, store, services, stderr);

        var positionals = args.Positionals();
        if (positionals.Count != 1) throw new CliUsageException(Help);
        string url = positionals[0];
        if (!Uri.TryCreate(url, UriKind.Absolute, out var target) || target.Scheme is not ("http" or "https"))
            throw new CliUsageException($"cors needs an http:// or https:// URL, got '{url}'.");
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
        else if (origins.Count == 0)
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
        if (ApiClient.ValidateTransport(transport, url) is { } problem) throw new CliUsageException(problem);
        var state = CliWorkspace.Load(null, services.LiveStatePath, TextWriter.Null);
        var predicates = new TrustPredicates(state);
        Task<ApiResponse> Send(ApiRequest r) =>
            services.Client.SendAsync(r, cert, transport, predicates.For(new Uri(r.Url).Host), cancellationToken: services.Cancel);

        if (audit)
        {
            var trusted = origins.Count == 1 ? new Uri(origins[0]) : new Uri(target.GetLeftPart(UriPartial.Authority));
            var result = CorsAudit.RunAsync(url, trusted, headers, Send, TimeSpan.FromSeconds(100)).GetAwaiter().GetResult();
            if (json) stdout.WriteLine(AuditJson(result, url));
            else if (quiet) stdout.WriteLine(result.Worst is { } w ? $"{w.ToString().ToUpperInvariant()}: {result.Findings[0].Title}" : "no findings");
            else stdout.WriteLine(CorsAudit.Describe(result, url));
            return result.Worst is CorsSeverity.High or CorsSeverity.Medium ? ExitCodes.Failure : ExitCodes.Ok;
        }

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

        if (results.Count == 1)
        {
            var (request, result) = results[0];
            if (json) stdout.WriteLine(Json(result, request, url));
            else if (quiet) stdout.WriteLine(CorsProbe.Verdict(result, request));
            else stdout.WriteLine(CorsProbe.Describe(result, request, url));
            return result.Verdict.Allowed ? ExitCodes.Ok : ExitCodes.Failure;
        }

        if (json) stdout.WriteLine("[" + string.Join(",\n", results.Select(r => Json(r.Result, r.Request, url))) + "]");
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
        }
        return results.All(r => r.Result.Verdict.Allowed) ? ExitCodes.Ok : ExitCodes.Failure;
    }

    private static bool TargetIsPrivate(Uri target)
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

    internal static string AuditJson(CorsAuditResult result, string url) =>
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
            })
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

    internal static string Json(CorsProbeResult result, CorsRequest request, string url)
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
            hiddenHeaders = v.Hidden
        }, new JsonSerializerOptions { WriteIndented = true });
    }
}
