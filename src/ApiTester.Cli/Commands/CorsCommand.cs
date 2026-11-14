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

          --origin <origin>       The page's origin, e.g. https://app.example.com (required)
          -X, --method <m>        The method the page's script uses (default GET)
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
          --json                  The result as JSON
          -q, --quiet             Only the verdict line

        TLS / certificates:
        """ + "\n" + CliCert.HelpLines + "\n" + """
          --insecure              Ignore server certificate errors

        """ + "\n" + TransportFlags.Help + "\n" + """

        Examples:
          certapi cors https://api.example.com/orders --origin https://app.example.com --cert "CN=svc"
          certapi cors https://api.example.com/orders --origin http://localhost:3000 -X PUT -H "X-Api-Key: k" --credentials
          certapi cors https://api.example.com/orders --origin https://app.example.com -X DELETE --actual --json

        Exit 0 when a browser would allow it, 1 when it would block it.
        """;

    public static int Run(Args args, TextWriter stdout, TextWriter stderr, CliServices services)
    {
        string? origin = args.Value("--origin");
        string method = args.Value("-X", "--method") ?? "GET";
        var headerSpecs = args.Values("-H", "--header");
        string? data = args.Value("-d", "--data");
        string? contentType = args.Value("--content-type");
        bool credentials = args.Flag("--credentials");
        bool actual = args.Flag("--actual");
        bool preflightOnly = args.Flag("--preflight-only");
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
        if (origin is null) throw new CliUsageException("cors needs --origin: the origin of the page making the request, e.g. https://app.example.com.");
        origin = NormalizeOrigin(origin);
        if (actual && preflightOnly) throw new CliUsageException("--actual and --preflight-only contradict each other; give one.");
        if (string.IsNullOrWhiteSpace(method) || method.Any(char.IsWhiteSpace)) throw new CliUsageException($"-X expects a method name, got '{method}'.");
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

        var request = new CorsRequest(origin, method.ToUpperInvariant(), headers, credentials);
        // Never followed here: a redirected preflight is itself a CORS failure, and a redirected
        // request is checked again by the browser at the new address, which is reported instead.
        var transport = overrides.ApplyTo(new TransportOptions { IgnoreServerCertificateErrors = insecure }) with { FollowRedirects = false };
        if (ApiClient.ValidateTransport(transport, url) is { } problem) throw new CliUsageException(problem);
        var state = CliWorkspace.Load(null, services.LiveStatePath, TextWriter.Null);
        var predicates = new TrustPredicates(state);

        var result = CorsProbe.RunAsync(url, request, data, contentType, preflightOnly ? false : actual ? true : null,
            r => services.Client.SendAsync(r, cert, transport, predicates.For(target.Host), cancellationToken: services.Cancel),
            TimeSpan.FromSeconds(100)).GetAwaiter().GetResult();

        if (json) stdout.WriteLine(Json(result, request, url));
        else if (quiet) stdout.WriteLine(CorsProbe.Describe(result, request, url).Split('\n')[^1]);
        else stdout.WriteLine(CorsProbe.Describe(result, request, url));
        return result.Verdict.Allowed ? ExitCodes.Ok : ExitCodes.Failure;
    }

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
            passed = v.Passed,
            problems = v.Problems,
            notes = v.Notes,
            readableHeaders = v.Readable,
            hiddenHeaders = v.Hidden
        }, new JsonSerializerOptions { WriteIndented = true });
    }
}
