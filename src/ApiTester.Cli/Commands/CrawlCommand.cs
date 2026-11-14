using System.Collections.Concurrent;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ApiTester.Core;

namespace ApiTester.Cli.Commands;

/// <summary>`certapi crawl <url>`: walk a site's links with the client certificate, reporting each
/// page's status — and, with --mirror, keeping a copy.</summary>
public static class CrawlCommand
{
    public const string Help = """
        Usage: certapi crawl <url> [options]

        Walks a site from <url> by its links, with your client certificate, and reports every page
        and resource it reaches: the status, the time, the size, and which page linked to it.
        Broken links (4xx, 5xx, no answer) are listed at the end, and the exit code is 1 when there
        are any, so a crawl is a site check in CI. Pages are HTML; their links, images, scripts,
        stylesheets and the url(…)s in stylesheets are followed. Redirects are reported and their
        target is followed like a link.

          --depth <n>             How many links away from <url> to go (default 3)
          --max-pages <n>         Stop after this many URLs (default 500)
          --scope <s>             What may be fetched: path (default: under <url>'s folder),
                                  origin (the same scheme, host and port) or host (any port/scheme)
          --include <regex>       Only fetch URLs matching this (repeatable)
          --exclude <regex>       Never fetch URLs matching this (repeatable), e.g. logout
          --parallel <n>          Requests at once (default 4)
          --delay <ms>            Wait this long between requests on each worker
          --no-robots             Ignore robots.txt (read and obeyed by default)
          --mirror <dir>          Save every body under <dir>/<host>/<path> (a folder's page as
                                  index.html); links are left as written
          --json                  One JSON object per URL instead of the table

          -H, --header <h>        Send this header with every request (repeatable)
          --bearer <t> / -u, --basic <u:p> / -b, --cookie <c>

        TLS / certificates:
        """ + "\n" + CliCert.HelpLines + "\n" + """
          --insecure              Ignore server certificate errors

        """ + "\n" + TransportFlags.Help + "\n" + """

        Global: --debug (verbose diagnostics) and --log-file <path> work here too.

        Examples:
          certapi crawl https://portal.corp/ --cert "CN=me"
          certapi crawl https://docs.internal/guide/ --depth 5 --mirror .\docs-copy
          certapi crawl https://app.corp/ --exclude logout --json > crawl.jsonl

        Exit 0 when nothing is broken, 1 when something is, 2/3 usage/data errors.
        """;

    private sealed record Found(Uri Url, Uri? From, int Depth);

    private sealed record Visit(string Url, string? From, int? Status, long Bytes, double Ms, string? ContentType, string? Location, string? Error);

    public static int Run(Args args, TextWriter stdout, TextWriter stderr, CliServices services)
    {
        string? depthRaw = args.Value("--depth");
        string? maxRaw = args.Value("--max-pages");
        string scope = (args.Value("--scope") ?? "path").ToLowerInvariant();
        var includes = args.Values("--include").Select(r => Pattern(r, "--include")).ToList();
        var excludes = args.Values("--exclude").Select(r => Pattern(r, "--exclude")).ToList();
        string? parallelRaw = args.Value("--parallel");
        string? delayRaw = args.Value("--delay");
        bool noRobots = args.Flag("--no-robots");
        string? mirror = args.Value("--mirror");
        bool json = args.Flag("--json");
        var headerSpecs = args.Values("-H", "--header");
        string? bearer = args.Value("--bearer");
        string? basic = args.Value("--basic");
        var cookieSpecs = args.Values("--cookie");
        string store = args.Value("--store") ?? services.Profile?.Store ?? "CurrentUser";
        bool insecure = args.Switch("--insecure", services.Profile?.Insecure);
        var overrides = TransportFlags.Parse(args, out _, environment: null, services.Profile);
        var cert = CliCert.Resolve(args, store, services, stderr);
        var positionals = args.Positionals();
        if (positionals.Count != 1) throw new CliUsageException(Help);
        if (!Uri.TryCreate(positionals[0], UriKind.Absolute, out var start) || start.Scheme is not ("http" or "https"))
            throw new CliUsageException($"crawl needs an http(s) URL, got '{positionals[0]}'.");

        int depth = Int(depthRaw, 3, "--depth", 0), maxPages = Int(maxRaw, 500, "--max-pages", 1);
        int parallel = Int(parallelRaw, 4, "--parallel", 1), delay = Int(delayRaw, 0, "--delay", 0);
        if (parallel > 32) throw new CliUsageException("--parallel is at most 32: a crawl should not become a load test.");
        if (scope is not ("path" or "origin" or "host")) throw new CliUsageException($"--scope is path, origin or host, got '{scope}'.");

        var headers = new List<KeyValuePair<string, string>>();
        foreach (var raw in headerSpecs)
        {
            int colon = raw.IndexOf(':');
            if (colon <= 0) throw new CliUsageException($"Header must be \"Name: value\", got '{raw}'.");
            headers.Add(new(raw[..colon].Trim(), raw[(colon + 1)..].Trim()));
        }
        if (bearer is not null) headers.Add(new("Authorization", "Bearer " + bearer));
        else if (basic is not null) headers.Add(new("Authorization", "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(basic))));
        var cookies = cookieSpecs.Select(c => c.Contains('=') ? c : NetscapeCookies.HeaderFor(c, start.ToString())).Where(c => c.Length > 0).ToList();
        if (cookies.Count > 0) headers.Add(new("Cookie", string.Join("; ", cookies)));
        headers.Add(new("User-Agent", "certapi-crawl"));

        // Every hop is reported and queued, never followed silently: a redirect off the site would
        // otherwise carry the client certificate somewhere the scope says not to go.
        var transport = overrides.ApplyTo(new TransportOptions { IgnoreServerCertificateErrors = insecure }) with { FollowRedirects = false };
        if (ApiClient.ValidateTransport(transport, start.ToString()) is { } problem) throw new CliUsageException(problem);
        AppState state;
        try { state = CliWorkspace.Load(null, services.LiveStatePath, TextWriter.Null); } catch (CliDataException) { state = new AppState(); }
        string scopePath = start.AbsolutePath.EndsWith('/') ? start.AbsolutePath : start.AbsolutePath[..(start.AbsolutePath.LastIndexOf('/') + 1)];

        bool InScope(Uri u)
        {
            bool ok = scope switch
            {
                "host" => u.Host.Equals(start.Host, StringComparison.OrdinalIgnoreCase),
                "origin" => u.Scheme == start.Scheme && u.Host.Equals(start.Host, StringComparison.OrdinalIgnoreCase) && u.Port == start.Port,
                _ => u.Scheme == start.Scheme && u.Host.Equals(start.Host, StringComparison.OrdinalIgnoreCase) && u.Port == start.Port &&
                     u.AbsolutePath.StartsWith(scopePath, StringComparison.Ordinal)
            };
            string s = u.ToString();
            return ok && (includes.Count == 0 || includes.Any(r => r.IsMatch(s))) && !excludes.Any(r => r.IsMatch(s));
        }

        var robots = new ConcurrentDictionary<string, RobotsRules>(StringComparer.OrdinalIgnoreCase);
        async Task<RobotsRules> RobotsFor(Uri u)
        {
            string origin = u.GetLeftPart(UriPartial.Authority);
            if (robots.TryGetValue(origin, out var known)) return known;
            var response = await Send(new Uri(origin + "/robots.txt"));
            var rules = response.Error is null && response.StatusCode == 200 ? RobotsRules.Parse(Encoding.UTF8.GetString(response.Body)) : RobotsRules.Parse("");
            return robots.GetOrAdd(origin, rules);
        }

        Task<ApiResponse> Send(Uri u) => services.Client.SendAsync(new ApiRequest
        {
            Method = HttpMethod.Get, Url = u.ToString(), Headers = headers, Timeout = TimeSpan.FromSeconds(30)
        }, cert, transport, c => c is not null && TrustService.IsTrustedHere(state, u.Host, c), cancellationToken: services.Cancel);

        var seen = new ConcurrentDictionary<string, bool>(StringComparer.Ordinal);
        var visits = new ConcurrentBag<Visit>();
        var level = new List<Found> { new(start, null, 0) };
        seen[start.ToString()] = true;
        int fetched = 0;
        var output = TextWriter.Synchronized(stdout);
        if (!json) stderr.WriteLine($"crawling {start} (depth {depth}, scope {scope}{(noRobots ? ", robots.txt ignored" : "")}) …");

        while (level.Count > 0 && fetched < maxPages && !services.Cancel.IsCancellationRequested)
        {
            var next = new ConcurrentBag<Found>();
            using var gate = new SemaphoreSlim(parallel);
            var tasks = level.Select(async item =>
            {
                await gate.WaitAsync(services.Cancel);
                try
                {
                    if (Interlocked.Increment(ref fetched) > maxPages) return;
                    if (!noRobots && !(await RobotsFor(item.Url)).Allows(item.Url))
                    {
                        Report(new Visit(item.Url.ToString(), item.From?.ToString(), null, 0, 0, null, null, "skipped: robots.txt"));
                        return;
                    }
                    var clock = System.Diagnostics.Stopwatch.StartNew();
                    var response = await Send(item.Url);
                    string? location = response.Headers.FirstOrDefault(h => h.Key.Equals("Location", StringComparison.OrdinalIgnoreCase)).Value;
                    var visit = new Visit(item.Url.ToString(), item.From?.ToString(), response.StatusCode, response.Body.LongLength,
                        Math.Round(clock.Elapsed.TotalMilliseconds), response.ContentType, location, response.Error?.Message);
                    Report(visit);
                    if (mirror is not null && response.Error is null && response.StatusCode is >= 200 and < 300)
                        Save(mirror, item.Url, response, stderr);

                    var links = new List<Uri>();
                    if (location is not null && Uri.TryCreate(item.Url, location, out var target)) links.Add(target);
                    bool html = response.ContentType?.Contains("html", StringComparison.OrdinalIgnoreCase) == true;
                    bool css = response.ContentType?.Contains("css", StringComparison.OrdinalIgnoreCase) == true;
                    if (response.Error is null && response.StatusCode is >= 200 and < 300 && (html || css) && item.Depth < depth)
                        links.AddRange(LinkExtractor.Extract(Encoding.UTF8.GetString(response.Body), item.Url, css));
                    foreach (var link in links)
                        if (InScope(link) && seen.TryAdd(link.ToString(), true))
                            next.Add(new Found(link, item.Url, item.Depth + 1));
                    if (delay > 0) await Task.Delay(delay, services.Cancel);
                }
                catch (OperationCanceledException) { }
                finally { gate.Release(); }
            }).ToList();
            try { Task.WaitAll(tasks.ToArray()); } catch (AggregateException) { }
            level = next.OrderBy(f => f.Url.ToString(), StringComparer.Ordinal).ToList();
        }

        void Report(Visit v)
        {
            visits.Add(v);
            if (json) output.WriteLine(JsonSerializer.Serialize(v));
            else output.WriteLine($"{(v.Status?.ToString() ?? (v.Error?.StartsWith("skipped") == true ? "skip" : "ERR")),-4} {v.Ms,6:0} ms {OutputText.Size(v.Bytes),9}  {v.Url}" +
                                  (v.Location is not null ? $"  -> {v.Location}" : ""));
        }

        var broken = visits.Where(v => v.Error is { } e && !e.StartsWith("skipped") || v.Status >= 400).OrderBy(v => v.Url).ToList();
        if (!json)
        {
            stderr.WriteLine();
            stderr.WriteLine($"{visits.Count} URL(s): {visits.Count(v => v.Status is >= 200 and < 300)} ok, " +
                             $"{visits.Count(v => v.Status is >= 300 and < 400)} redirected, {broken.Count} broken" +
                             (fetched > maxPages ? $" — stopped at --max-pages {maxPages}" : ""));
            foreach (var b in broken)
                stderr.WriteLine($"  {(b.Status?.ToString() ?? "ERR")}  {b.Url}" + (b.From is not null ? $"  (linked from {b.From})" : "") +
                                 (b.Error is not null ? $"  {b.Error}" : ""));
        }
        return broken.Count == 0 ? ExitCodes.Ok : ExitCodes.Failure;
    }

    private static void Save(string root, Uri url, ApiResponse response, TextWriter stderr)
    {
        try
        {
            string path = MirrorPath.For(root, url, response.ContentType);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, response.Body);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            stderr.WriteLine($"warning: could not save {url}: {ex.Message}");
        }
    }

    private static Regex Pattern(string raw, string option)
    {
        try { return new Regex(raw, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)); }
        catch (ArgumentException ex) { throw new CliUsageException($"{option} '{raw}' is not a valid pattern: {ex.Message}"); }
    }

    private static int Int(string? raw, int fallback, string option, int min)
    {
        if (raw is null) return fallback;
        if (!int.TryParse(raw, out int n) || n < min) throw new CliUsageException($"{option} expects a whole number of at least {min}, got '{raw}'.");
        return n;
    }
}
