using System.Net.Http;
using System.Text;

namespace ApiTester.Core;

/// <summary>What a page would ask for: the origin it runs on, the method and headers its script
/// sets, and whether it sends credentials (cookies, <c>fetch(…, {credentials: "include"})</c>).</summary>
public sealed record CorsRequest(
    string Origin,
    string Method,
    IReadOnlyList<KeyValuePair<string, string>> Headers,
    bool Credentials)
{
    /// <summary>Check Private Network Access too: a public page reaching a private or loopback
    /// address sends <c>Access-Control-Request-Private-Network: true</c> on its preflight.</summary>
    public bool PrivateNetwork { get; init; }
}

/// <summary>A browser's verdict on a cross-origin request, with the reasons in words.</summary>
public sealed record CorsVerdict
{
    public bool Allowed { get; init; }
    public string? PreflightReason { get; init; }
    public bool PreflightNeeded => PreflightReason is not null;
    public IReadOnlyList<string> Problems { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Passed { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
    /// <summary>Response headers a script may read with <c>response.headers.get</c>.</summary>
    public IReadOnlyList<string> Readable { get; init; } = Array.Empty<string>();
    /// <summary>Response headers that arrived but a script cannot see.</summary>
    public IReadOnlyList<string> Hidden { get; init; } = Array.Empty<string>();
}

/// <summary>The Fetch standard's CORS rules as plain functions over header lists: whether a
/// request needs a preflight, what the preflight carries, and whether a browser accepts the
/// preflight and the response. No sockets, so every rule is testable on its own.</summary>
public static class CorsRules
{
    private static readonly HashSet<string> SimpleMethods = new(StringComparer.Ordinal) { "GET", "HEAD", "POST" };

    /// <summary>Methods browsers forbid scripts from sending at all (Fetch §2.2.1).</summary>
    private static readonly HashSet<string> ForbiddenMethods = new(StringComparer.OrdinalIgnoreCase) { "CONNECT", "TRACE", "TRACK" };

    private static readonly HashSet<string> MethodOverrideHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "X-HTTP-Method", "X-HTTP-Method-Override", "X-Method-Override"
    };

    private static readonly HashSet<string> SimpleContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "application/x-www-form-urlencoded", "multipart/form-data", "text/plain"
    };

    /// <summary>Response headers every script may read without being told.</summary>
    public static readonly IReadOnlySet<string> SafelistedResponse = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Cache-Control", "Content-Language", "Content-Length", "Content-Type", "Expires", "Last-Modified", "Pragma"
    };

    /// <summary>True when a browser forbids scripts from using <paramref name="method"/> (CONNECT, TRACE, TRACK).</summary>
    public static bool ForbiddenMethod(string method) => ForbiddenMethods.Contains(method.Trim());

    /// <summary>Headers a page cannot set: the browser owns them.</summary>
    public static bool Forbidden(string name)
    {
        string n = name.ToLowerInvariant();
        return n is "accept-charset" or "accept-encoding" or "access-control-request-headers"
                   or "access-control-request-method" or "access-control-request-private-network"
                   or "connection" or "content-length" or "cookie" or "cookie2" or "date" or "dnt" or "expect" or "host"
                   or "keep-alive" or "origin" or "referer" or "set-cookie" or "te" or "trailer" or "transfer-encoding"
                   or "upgrade" or "via"
               || n.StartsWith("proxy-", StringComparison.Ordinal) || n.StartsWith("sec-", StringComparison.Ordinal);
    }

    /// <summary>True when a header name/value pair is forbidden by Fetch (including method-override
    /// headers carrying a forbidden method such as TRACE or CONNECT).</summary>
    public static bool Forbidden(string name, string value)
    {
        if (Forbidden(name)) return true;
        if (MethodOverrideHeaders.Contains(name))
            return value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Any(ForbiddenMethod);
        return false;
    }

    /// <summary>A request header a page may send without a preflight (the CORS-safelisted ones).</summary>
    public static bool SafelistedRequestHeader(string name, string value)
    {
        if (value.Length > 128) return false;
        static bool UnsafeByte(char c) => c < 0x20 && c != '\t' || c == 0x7F ||
            c is '"' or '(' or ')' or ':' or '<' or '>' or '?' or '@' or '[' or '\\' or ']' or '{' or '}';
        switch (name.ToLowerInvariant())
        {
            case "accept":
                return !value.Any(UnsafeByte);
            case "accept-language" or "content-language":
                return value.All(c => char.IsAsciiLetterOrDigit(c) || c is ' ' or '*' or ',' or '-' or '.' or ';' or '=');
            case "content-type":
                return !value.Any(UnsafeByte) && SimpleContentTypes.Contains(value.Split(';')[0].Trim());
            case "range":
            {
                var m = System.Text.RegularExpressions.Regex.Match(value.Trim(), @"^bytes=(\d+)-(\d*)$");
                if (!m.Success) return false;
                if (m.Groups[2].Length == 0) return true;
                return ulong.TryParse(m.Groups[1].Value, out var start) &&
                       ulong.TryParse(m.Groups[2].Value, out var end) &&
                       end >= start;
            }
            default:
                return false;
        }
    }

    /// <summary>True when the combined byte/char length of CORS-safelisted request-header values
    /// exceeds Fetch's 1024-byte cap, forcing a preflight.</summary>
    public static bool ExceedsSafelistedTotal(CorsRequest r) =>
        r.Headers.Where(h => !Forbidden(h.Key, h.Value) && SafelistedRequestHeader(h.Key, h.Value))
            .Sum(h => h.Value.Length) > 1024;

    /// <summary>The header names a page's request carries that are not safelisted — what a
    /// preflight's Access-Control-Request-Headers lists: lower case, sorted, de-duplicated.</summary>
    public static IReadOnlyList<string> UnsafeHeaderNames(CorsRequest r)
    {
        bool overTotal = ExceedsSafelistedTotal(r);
        return r.Headers
            .Where(h => !Forbidden(h.Key, h.Value) && (overTotal || !SafelistedRequestHeader(h.Key, h.Value)))
            .Select(h => h.Key.ToLowerInvariant()).Distinct().OrderBy(n => n, StringComparer.Ordinal).ToList();
    }

    /// <summary>Why a browser would send a preflight first, or null when it would not.</summary>
    public static string? PreflightReason(CorsRequest r)
    {
        var reasons = new List<string>();
        string method = r.Method.ToUpperInvariant();
        if (!SimpleMethods.Contains(method)) reasons.Add($"the method is {method}");
        bool overTotal = ExceedsSafelistedTotal(r);
        if (overTotal) reasons.Add("combined CORS-safelisted request-header values exceed 1024 bytes");
        foreach (var name in UnsafeHeaderNames(r))
        {
            var header = r.Headers.Last(h => h.Key.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (overTotal && SafelistedRequestHeader(header.Key, header.Value)) continue;
            reasons.Add(name == "content-type"
                ? $"Content-Type is {header.Value.Split(';')[0].Trim()} (only form, multipart and text/plain go without one)"
                : $"the request sets {header.Key}");
        }
        if (r.PrivateNetwork) reasons.Add("the page is public and the target is on a private network (Private Network Access)");
        return reasons.Count == 0 ? null : string.Join("; ", reasons);
    }

    /// <summary>The preflight the browser sends: OPTIONS, the Origin, the method and the header
    /// names — never the headers' values, cookies or an Authorization header.</summary>
    public static IReadOnlyList<KeyValuePair<string, string>> PreflightHeaders(CorsRequest r)
    {
        var headers = new List<KeyValuePair<string, string>>
        {
            new("Origin", r.Origin),
            new("Access-Control-Request-Method", r.Method.ToUpperInvariant())
        };
        var names = UnsafeHeaderNames(r);
        if (names.Count > 0) headers.Add(new("Access-Control-Request-Headers", string.Join(",", names)));
        if (r.PrivateNetwork) headers.Add(new("Access-Control-Request-Private-Network", "true"));
        return headers;
    }

    private static List<string> Values(IReadOnlyList<KeyValuePair<string, string>> headers, string name) =>
        headers.Where(h => h.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Select(h => h.Value.Trim()).ToList();

    private static List<string> Tokens(IReadOnlyList<KeyValuePair<string, string>> headers, string name) =>
        Values(headers, name).SelectMany(v => v.Split(',')).Select(t => t.Trim()).Where(t => t.Length > 0).ToList();

    /// <summary>The CORS check the Fetch standard runs on a preflight answer and on a response:
    /// Access-Control-Allow-Origin, and Access-Control-Allow-Credentials for a credentialed one.</summary>
    private static void CheckOrigin(CorsRequest r, IReadOnlyList<KeyValuePair<string, string>> headers, string what,
        List<string> problems, List<string> passed, List<string> notes)
    {
        var allow = Values(headers, "Access-Control-Allow-Origin");
        if (allow.Count == 0)
        {
            problems.Add($"{what} has no Access-Control-Allow-Origin header");
            return;
        }
        if (allow.Count > 1 || allow[0].Contains(','))
        {
            problems.Add($"{what} names several origins in Access-Control-Allow-Origin ({string.Join(", ", allow)}); a browser accepts exactly one, or *");
            return;
        }
        string value = allow[0];
        if (value == "*")
        {
            if (r.Credentials)
                problems.Add($"{what} answers Access-Control-Allow-Origin: *, which a browser refuses for a request with credentials; it must name {r.Origin}");
            else passed.Add($"{what}: Access-Control-Allow-Origin: * (any origin, without credentials)");
        }
        else if (!value.Equals(r.Origin, StringComparison.Ordinal))
        {
            string hint = value.Equals(r.Origin, StringComparison.OrdinalIgnoreCase)
                ? " (the comparison is exact, letter case included)"
                : value.TrimEnd('/').Equals(r.Origin, StringComparison.Ordinal)
                    ? " (drop the trailing slash — an Origin never has a path)"
                    : "";
            problems.Add($"{what} allows the origin {value}, not {r.Origin}{hint}");
            return;
        }
        else
        {
            passed.Add($"{what}: Access-Control-Allow-Origin: {value}");
            if (!Tokens(headers, "Vary").Any(v => v.Equals("Origin", StringComparison.OrdinalIgnoreCase) || v == "*"))
                notes.Add($"{what} echoes the origin without Vary: Origin, so a shared cache may hand this answer to a page on another origin");
        }

        if (r.Credentials)
        {
            var credentials = Values(headers, "Access-Control-Allow-Credentials");
            if (credentials.Count == 1 && credentials[0] == "true")
                passed.Add($"{what}: Access-Control-Allow-Credentials: true");
            else
                problems.Add($"{what} lacks Access-Control-Allow-Credentials: true, which a request with credentials needs" +
                             (credentials.Count > 0 ? $" (it says '{string.Join(", ", credentials)}')" : ""));
        }
    }

    /// <summary>Judge a preflight answer.</summary>
    public static (List<string> Problems, List<string> Passed, List<string> Notes) CheckPreflight(
        CorsRequest r, int status, IReadOnlyList<KeyValuePair<string, string>> headers)
    {
        var problems = new List<string>();
        var passed = new List<string>();
        var notes = new List<string>();
        if (status is < 200 or > 299)
        {
            problems.Add($"the preflight was answered {status}; a browser needs a 2xx" +
                         (status is 401 or 403 ? " (a preflight carries no cookies or Authorization header, so a server that wants either for OPTIONS refuses every preflight)" :
                          status == 405 ? ". The server does not answer OPTIONS for this path at all" : ""));
        }
        CheckOrigin(r, headers, "the preflight", problems, passed, notes);

        string method = r.Method.ToUpperInvariant();
        var methods = Tokens(headers, "Access-Control-Allow-Methods");
        if (SimpleMethods.Contains(method)) { }
        else if (methods.Contains(method, StringComparer.Ordinal)) passed.Add($"the preflight allows the method {method}");
        else if (methods.Contains("*") && !r.Credentials) passed.Add($"the preflight allows every method (*)");
        else
            problems.Add($"the preflight does not allow the method {method}" +
                         (methods.Count > 0 ? $" (Access-Control-Allow-Methods: {string.Join(", ", methods)})" : " (no Access-Control-Allow-Methods)") +
                         (methods.Contains("*") && r.Credentials ? "; * does not count for a request with credentials" : ""));

        var allowedHeaders = Tokens(headers, "Access-Control-Allow-Headers");
        bool wildcard = allowedHeaders.Contains("*") && !r.Credentials;
        foreach (var name in UnsafeHeaderNames(r))
        {
            if (allowedHeaders.Contains(name, StringComparer.OrdinalIgnoreCase))
                passed.Add($"the preflight allows the header {name}");
            else if (wildcard && name != "authorization")
                passed.Add($"the preflight allows the header {name} (*)");
            else
                problems.Add($"the preflight does not allow the header {name}" +
                             (allowedHeaders.Count > 0 ? $" (Access-Control-Allow-Headers: {string.Join(", ", allowedHeaders)})" : " (no Access-Control-Allow-Headers)") +
                             (name == "authorization" && allowedHeaders.Contains("*") ? "; * never covers Authorization" : "") +
                             (allowedHeaders.Contains("*") && r.Credentials && name != "authorization" ? "; * does not count for a request with credentials" : ""));
        }

        if (r.PrivateNetwork)
        {
            var pna = Values(headers, "Access-Control-Allow-Private-Network");
            if (pna.Count == 1 && pna[0] == "true") passed.Add("the preflight allows Private Network Access (Access-Control-Allow-Private-Network: true)");
            else problems.Add("the preflight lacks Access-Control-Allow-Private-Network: true, which a Private Network Access preflight needs");
        }

        var maxAge = Values(headers, "Access-Control-Max-Age");
        notes.Add(maxAge.Count == 0
            ? "no Access-Control-Max-Age: a browser caches this preflight for 5 seconds"
            : int.TryParse(maxAge[0], out var seconds)
                ? seconds <= 0
                    ? $"Access-Control-Max-Age: {seconds} disables preflight caching (browsers will preflight every request)"
                    : $"a browser caches this preflight for {Math.Min(seconds, 7200)} s (Access-Control-Max-Age: {seconds}; Safari caps at 10 min, Chromium at 2 hours, Firefox at 24 hours)"
                : $"Access-Control-Max-Age '{maxAge[0]}' is not a number, so it is ignored");

        if (Values(headers, "Access-Control-Allow-Origin").Count > 0)
        {
            var vary = Tokens(headers, "Vary");
            if (!vary.Any(v => v == "*"))
            {
                var missingVary = new List<string>();
                if (!vary.Any(v => v.Equals("Access-Control-Request-Method", StringComparison.OrdinalIgnoreCase)))
                    missingVary.Add("Access-Control-Request-Method");
                if (UnsafeHeaderNames(r).Count > 0 &&
                    !vary.Any(v => v.Equals("Access-Control-Request-Headers", StringComparison.OrdinalIgnoreCase)))
                    missingVary.Add("Access-Control-Request-Headers");
                if (missingVary.Count > 0)
                    notes.Add($"preflight Vary omits {string.Join(", ", missingVary)}: a shared cache/CDN that caches OPTIONS may serve this preflight answer for another method or header set");
            }
        }
        return (problems, passed, notes);
    }

    /// <summary>True when two absolute URLs/origins share a registrable site (scheme + host/registrable domain),
    /// used to judge whether a browser treats a cookie exchange as cross-site.</summary>
    internal static bool IsSameSite(string origin, string? targetUrl)
    {
        if (targetUrl is null || origin == "null") return false;
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var a) || !Uri.TryCreate(targetUrl, UriKind.Absolute, out var b)) return false;
        if (!a.Scheme.Equals(b.Scheme, StringComparison.OrdinalIgnoreCase)) return false;
        string ha = a.Host.TrimEnd('.').ToLowerInvariant();
        string hb = b.Host.TrimEnd('.').ToLowerInvariant();
        if (ha == hb) return true;
        if (System.Net.IPAddress.TryParse(ha, out _) || System.Net.IPAddress.TryParse(hb, out _)) return false;
        static string SiteKey(string h)
        {
            var parts = h.Split('.');
            return parts.Length >= 2 ? parts[^2] + "." + parts[^1] : h;
        }
        return SiteKey(ha) == SiteKey(hb);
    }

    /// <summary>Judge the actual response, and say which of its headers a script can read.</summary>
    public static (List<string> Problems, List<string> Passed, List<string> Notes, List<string> Readable, List<string> Hidden) CheckResponse(
        CorsRequest r, IReadOnlyList<KeyValuePair<string, string>> headers, string? targetUrl = null)
    {
        var problems = new List<string>();
        var passed = new List<string>();
        var notes = new List<string>();
        CheckOrigin(r, headers, "the response", problems, passed, notes);

        var exposed = Tokens(headers, "Access-Control-Expose-Headers");
        bool all = exposed.Contains("*") && !r.Credentials;
        var readable = new List<string>();
        var hidden = new List<string>();
        foreach (var name in headers.Select(h => h.Key).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (name.StartsWith("Access-Control-", StringComparison.OrdinalIgnoreCase) || name.Equals("Vary", StringComparison.OrdinalIgnoreCase)) continue;
            if (name.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase) || name.Equals("Set-Cookie2", StringComparison.OrdinalIgnoreCase))
            {
                hidden.Add(name);
                continue;
            }
            if (SafelistedResponse.Contains(name) || all || exposed.Contains(name, StringComparer.OrdinalIgnoreCase)) readable.Add(name);
            else hidden.Add(name);
        }
        if (exposed.Contains("*") && r.Credentials)
            notes.Add("Access-Control-Expose-Headers: * means the literal header name '*' for a request with credentials, not every header");
        var setCookie = Values(headers, "Set-Cookie");
        if (setCookie.Count > 0 && !r.Credentials)
            notes.Add("the response sets a cookie, which a browser ignores unless the request is sent with credentials");
        else if (setCookie.Count > 0 && r.Credentials)
        {
            bool sameSiteTarget = IsSameSite(r.Origin, targetUrl);
            foreach (var rawCookie in setCookie)
            {
                var parts = rawCookie.Split(';').Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
                string cookieName = parts.Count > 0 ? parts[0].Split('=')[0].Trim() : "(cookie)";
                string? sameSiteAttr = parts.Skip(1)
                    .FirstOrDefault(p => p.StartsWith("SameSite=", StringComparison.OrdinalIgnoreCase))?[9..].Trim();
                bool hasSecure = parts.Skip(1).Any(p => p.Equals("Secure", StringComparison.OrdinalIgnoreCase));
                bool hasPartitioned = parts.Skip(1).Any(p => p.Equals("Partitioned", StringComparison.OrdinalIgnoreCase));

                if (string.Equals(sameSiteAttr, "None", StringComparison.OrdinalIgnoreCase) && !hasSecure)
                    notes.Add($"Set-Cookie '{cookieName}' has SameSite=None without Secure: modern browsers reject this cookie");
                else if (!sameSiteTarget && !string.Equals(sameSiteAttr, "None", StringComparison.OrdinalIgnoreCase))
                    notes.Add($"Set-Cookie '{cookieName}' has {(sameSiteAttr is null ? "no SameSite attribute (defaults to Lax)" : $"SameSite={sameSiteAttr}")}: cross-site credentialed fetches require SameSite=None; Secure");
                else if (!sameSiteTarget && string.Equals(sameSiteAttr, "None", StringComparison.OrdinalIgnoreCase) && hasSecure && !hasPartitioned)
                    notes.Add($"Set-Cookie '{cookieName}' is SameSite=None; Secure without Partitioned (CHIPS): browsers blocking unpartitioned third-party cookies may still reject it");
            }
        }

        // Cross-Origin Resource Policy (CORP), COOP/COEP, and Timing-Allow-Origin.
        var corp = Values(headers, "Cross-Origin-Resource-Policy");
        if (corp.Count > 0)
        {
            string val = corp[0].ToLowerInvariant();
            if (val == "same-origin")
                notes.Add("Cross-Origin-Resource-Policy: same-origin blocks cross-origin no-cors subresource loads and COEP-isolated pages even when CORS allows the origin");
            else if (val == "same-site" && !IsSameSite(r.Origin, targetUrl))
                notes.Add("Cross-Origin-Resource-Policy: same-site blocks cross-site subresource loads from " + r.Origin);
            else if (val == "cross-origin")
                passed.Add("the response: Cross-Origin-Resource-Policy: cross-origin (embeddable under COEP)");
        }
        var coep = Values(headers, "Cross-Origin-Embedder-Policy");
        if (coep.Count > 0)
            notes.Add($"Cross-Origin-Embedder-Policy: {coep[0]} (cross-origin subresources loaded by this document require CORS or CORP: cross-origin)");
        var coop = Values(headers, "Cross-Origin-Opener-Policy");
        if (coop.Count > 0)
            notes.Add($"Cross-Origin-Opener-Policy: {coop[0]}");

        var tao = Tokens(headers, "Timing-Allow-Origin");
        if (tao.Count > 0)
        {
            bool allowedTiming = tao.Contains("*") || tao.Any(t => t.Equals(r.Origin, StringComparison.Ordinal));
            notes.Add(allowedTiming
                ? $"Timing-Allow-Origin allows {r.Origin} to read detailed PerformanceResourceTiming metrics"
                : $"Timing-Allow-Origin ({string.Join(", ", tao)}) does not include {r.Origin}; PerformanceResourceTiming details are zeroed");
        }
        return (problems, passed, notes, readable, hidden);
    }
}

/// <summary>One request on the way: the preflight it needed, what it got, and where it led.</summary>
public sealed record CorsHop(string Url, string Origin, string Method, ApiResponse? Preflight, ApiResponse? Response, string? RedirectTo);

/// <summary>The outcome of checking a URL the way a browser would: the preflight (when one is
/// needed), the request, every redirect it followed, and the verdict.</summary>
public sealed record CorsProbeResult
{
    public required CorsVerdict Verdict { get; init; }
    /// <summary>The first preflight, when one was sent.</summary>
    public ApiResponse? Preflight { get; init; }
    /// <summary>The last response.</summary>
    public ApiResponse? Response { get; init; }
    /// <summary>Why the request itself was not sent (an unsafe method without --actual, or a failed preflight).</summary>
    public string? NotSent { get; init; }
    public IReadOnlyList<CorsHop> Hops { get; init; } = Array.Empty<CorsHop>();
}

/// <summary>Runs the exchange as a browser does: a preflight when one is needed, the request with
/// the Origin header, and each redirect in turn — every hop CORS-checked, a new preflight where the
/// new address needs one, and the Origin turned to <c>null</c> once a redirect crosses origins.</summary>
public static class CorsProbe
{
    private static readonly HashSet<string> Safe = new(StringComparer.Ordinal) { "GET", "HEAD", "OPTIONS" };

    public static async Task<CorsProbeResult> RunAsync(
        string url, CorsRequest request, string? body, string? contentType, bool? sendActual,
        Func<ApiRequest, Task<ApiResponse>> send, TimeSpan timeout, int maxRedirects = 20)
    {
        var problems = new List<string>();
        var passed = new List<string>();
        var notes = new List<string>();
        var hops = new List<CorsHop>();
        foreach (var h in request.Headers.Where(h => CorsRules.Forbidden(h.Key, h.Value)))
            notes.Add(h.Key.Equals("Cookie", StringComparison.OrdinalIgnoreCase)
                ? "a page cannot set Cookie; the browser sends its own cookies when the request has credentials (--credentials)"
                : $"a page cannot set {h.Key}{(CorsRules.Forbidden(h.Key) ? "" : $" to '{h.Value}'")}; the browser rejects or owns it, so it is left out of the check");

        if (CorsRules.ForbiddenMethod(request.Method))
            return new CorsProbeResult
            {
                NotSent = "forbidden browser method",
                Verdict = new CorsVerdict
                {
                    Allowed = false,
                    Problems = new[] { $"{request.Method.ToUpperInvariant()} is a forbidden method in browsers (CONNECT, TRACE, TRACK): fetch() throws a TypeError before sending any preflight or request" },
                    Notes = notes
                }
            };

        if (Uri.TryCreate(request.Origin, UriKind.Absolute, out var pageOrigin) && SameOrigin(pageOrigin, new Uri(url)))
            return new CorsProbeResult
            {
                Verdict = new CorsVerdict
                {
                    Allowed = true,
                    Notes = new[] { $"the page and {new Uri(url).GetLeftPart(UriPartial.Authority)} share an origin, so a browser applies no CORS rules to this request (nothing was sent)" }
                }
            };

        string? firstReason = CorsRules.PreflightReason(request);
        ApiResponse? firstPreflight = null, response = null;
        var readable = new List<string>();
        var hidden = new List<string>();
        string? notSent = null;
        var current = new Uri(url);
        var hopRequest = request;
        string? hopBody = body;
        bool tainted = false;

        for (int hop = 0; ; hop++)
        {
            string prefix = hop == 0 ? "" : $"after redirect {hop}, ";
            ApiResponse? preflight = null;
            if (CorsRules.PreflightReason(hopRequest) is not null)
            {
                preflight = await send(new ApiRequest
                {
                    Method = HttpMethod.Options,
                    Url = current.ToString(),
                    Headers = CorsRules.PreflightHeaders(hopRequest).ToList(),
                    Timeout = timeout
                });
                firstPreflight ??= preflight;
                if (preflight.Error is { } error)
                    problems.Add(prefix + "the preflight got no answer: " + error.Message);
                else if (preflight.StatusCode is >= 300 and < 400)
                    problems.Add(prefix + $"the preflight was answered with a redirect ({preflight.StatusCode}); a browser never follows a redirect on a preflight");
                else
                {
                    var (p, ok, n) = CorsRules.CheckPreflight(hopRequest, preflight.StatusCode ?? 0, preflight.Headers);
                    problems.AddRange(p.Select(x => prefix + x));
                    passed.AddRange(ok.Select(x => prefix + x));
                    if (hop == 0) notes.AddRange(n);
                }
            }

            string method = hopRequest.Method.ToUpperInvariant();
            if (problems.Count > 0) notSent = "a browser stops at the failed preflight";
            else if (hop == 0 && sendActual == false) notSent = "not asked for";
            else if (hop == 0 && sendActual != true && !Safe.Contains(method)) notSent = $"{method} may change something on the server, so only its preflight was checked (--actual, or Send unsafe methods in the app, sends it)";
            if (notSent is not null)
            {
                hops.Add(new CorsHop(current.ToString(), hopRequest.Origin, method, preflight, null, null));
                break;
            }

            var headers = hopRequest.Headers
                .Where(h => !CorsRules.Forbidden(h.Key, h.Value) || h.Key.Equals("Cookie", StringComparison.OrdinalIgnoreCase) && hopRequest.Credentials)
                .Where(h => !h.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
                .ToList();
            headers.Add(new("Origin", hopRequest.Origin));
            string? type = contentType ?? hopRequest.Headers.LastOrDefault(h => h.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)).Value;
            response = await send(new ApiRequest
            {
                Method = new HttpMethod(method),
                Url = current.ToString(),
                Headers = headers,
                Body = hopBody,
                ContentType = hopBody is null ? null : type ?? "application/json",
                Timeout = timeout
            });
            if (response.Error is { } sendError)
            {
                problems.Add(prefix + "the request got no answer: " + sendError.Message);
                hops.Add(new CorsHop(current.ToString(), hopRequest.Origin, method, preflight, response, null));
                break;
            }

            // A redirect is CORS-checked like any response: a browser refuses to follow one that
            // does not allow the origin.
            var (rp, rok, rn, r, hdn) = CorsRules.CheckResponse(hopRequest, response.Headers, current.ToString());
            problems.AddRange(rp.Select(x => prefix + x));
            passed.AddRange(rok.Select(x => prefix + x));
            notes.AddRange(rn.Select(x => prefix + x));
            readable = r;
            hidden = hdn;

            string? location = response.StatusCode is 301 or 302 or 303 or 307 or 308
                ? response.Headers.FirstOrDefault(x => x.Key.Equals("Location", StringComparison.OrdinalIgnoreCase)).Value
                : null;
            if (location is null || problems.Count > 0)
            {
                hops.Add(new CorsHop(current.ToString(), hopRequest.Origin, method, preflight, response, null));
                break;
            }
            if (!Uri.TryCreate(current, location, out var next) || next.Scheme is not ("http" or "https"))
            {
                problems.Add(prefix + $"the response redirects to '{location}', which a browser cannot follow");
                hops.Add(new CorsHop(current.ToString(), hopRequest.Origin, method, preflight, response, location));
                break;
            }
            hops.Add(new CorsHop(current.ToString(), hopRequest.Origin, method, preflight, response, next.ToString()));
            if (maxRedirects == 0)
            {
                notes.Add($"the response redirects to {next}, which was not followed (--no-redirect); a browser would follow it and check CORS there");
                break;
            }
            if (hop + 1 > maxRedirects)
            {
                problems.Add($"more than {maxRedirects} redirects; a browser gives up");
                break;
            }
            if (next.UserInfo.Length > 0)
            {
                problems.Add(prefix + "the redirect's URL carries a user name or password, which a browser refuses on a cross-origin request");
                break;
            }
            // The Fetch standard's tainted origin: once a cross-origin request is redirected to
            // another origin, the Origin it carries from then on is "null".
            if (!tainted && !SameOrigin(current, next))
            {
                tainted = true;
                notes.Add($"the redirect to {next.GetLeftPart(UriPartial.Authority)} crosses origins, so from there on the request carries Origin: null, and that server must allow null (or *)");
            }
            bool crossOrigin = !SameOrigin(current, next);
            string nextMethod = method;
            if ((response.StatusCode is 301 or 302 && method == "POST") || (response.StatusCode == 303 && method != "HEAD"))
            {
                nextMethod = "GET";
                hopBody = null;
            }
            hopRequest = hopRequest with
            {
                Origin = tainted ? "null" : hopRequest.Origin,
                Method = nextMethod,
                // A browser drops a body's headers when the method becomes GET, and the
                // Authorization header when the redirect leaves the origin.
                Headers = hopRequest.Headers
                    .Where(x => nextMethod == method || !x.Key.StartsWith("Content-", StringComparison.OrdinalIgnoreCase))
                    .Where(x => !crossOrigin || !x.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase))
                    .ToList()
            };
            current = next;
        }

        if (notSent is not null && problems.Count == 0)
            notes.Add($"the request itself was not sent ({notSent}), so its response's CORS headers were not checked");

        return new CorsProbeResult
        {
            Preflight = firstPreflight,
            Response = response,
            NotSent = notSent,
            Hops = hops,
            Verdict = new CorsVerdict
            {
                Allowed = problems.Count == 0,
                PreflightReason = firstReason,
                Problems = problems,
                Passed = passed,
                Notes = notes,
                Readable = readable,
                Hidden = hidden
            }
        };
    }

    internal static bool SameOrigin(Uri a, Uri b) =>
        a.Scheme.Equals(b.Scheme, StringComparison.OrdinalIgnoreCase) &&
        a.Host.Equals(b.Host, StringComparison.OrdinalIgnoreCase) && a.Port == b.Port;

    /// <summary>The verdict as lines for a terminal.</summary>
    public static string Describe(CorsProbeResult result, CorsRequest request, string url)
    {
        var sb = new StringBuilder();
        var v = result.Verdict;
        var first = result.Hops.FirstOrDefault();
        if (v.PreflightNeeded)
        {
            sb.Append("preflight  OPTIONS ").Append(url);
            sb.Append(result.Preflight is { Error: null } p ? $" → {p.StatusCode}" : " → no answer").AppendLine();
            sb.Append("           needed because ").AppendLine(v.PreflightReason);
        }
        else sb.AppendLine("preflight  none needed (a simple request)");
        if (result.Hops.Count == 0 || first?.Response is null)
        {
            sb.Append("request    ").Append(request.Method.ToUpperInvariant()).Append(' ').Append(url);
            sb.AppendLine(result.NotSent is { } why ? $" (not sent: {why})" : " → no answer");
        }
        else
            foreach (var hop in result.Hops)
            {
                sb.Append(hop == first ? "request    " : "redirect   ").Append(hop.Method).Append(' ').Append(hop.Url);
                if (hop.Origin != request.Origin) sb.Append("  (Origin: ").Append(hop.Origin).Append(')');
                sb.AppendLine(hop.Response is { Error: null } r ? $" → {r.StatusCode}" : " → no answer");
            }
        foreach (var ok in v.Passed) sb.Append("  ok       ").AppendLine(ok);
        foreach (var problem in v.Problems) sb.Append("  BLOCKED  ").AppendLine(problem);
        foreach (var note in v.Notes) sb.Append("  note     ").AppendLine(note);
        if (result.Response is { Error: null })
        {
            sb.Append("readable   ").AppendLine(v.Readable.Count == 0 ? "(none)" : string.Join(", ", v.Readable));
            if (v.Hidden.Count > 0) sb.Append("hidden     ").Append(string.Join(", ", v.Hidden)).AppendLine("  (not in Access-Control-Expose-Headers)");
        }
        sb.Append("verdict    ").Append(Verdict(result, request));
        return sb.ToString();
    }

    public static string Verdict(CorsProbeResult result, CorsRequest request) =>
        !result.Verdict.Allowed
            ? $"blocked: a browser on {request.Origin} would refuse this{(request.Credentials ? " (with credentials)" : "")}"
            : result.Response is null
                ? $"the preflight allows it: a page on {request.Origin} may send this request{(request.Credentials ? " with credentials" : "")}; whether it may read the answer depends on the response, which sending the request checks (--actual)"
                : $"allowed: a page on {request.Origin} can make this request{(request.Credentials ? " with credentials" : "")} and read the response";
}

/// <summary>How bad a CORS finding is: High lets any site read credentialed responses.</summary>
public enum CorsSeverity { Info, Low, Medium, High }

public sealed record CorsFinding(CorsSeverity Severity, string Title, string Detail);

/// <summary>One crafted origin tried by the audit, and what the server answered.</summary>
public sealed record CorsAuditProbe(string Kind, string Origin, int? Status, string? AllowOrigin, bool AllowCredentials, bool Reflected, string? Error);

public sealed record CorsAuditResult(IReadOnlyList<CorsAuditProbe> Probes, IReadOnlyList<CorsFinding> Findings, string BaseOrigin)
{
    public CorsSeverity? Worst => Findings.Count == 0 ? null : Findings.Max(f => f.Severity);
}

/// <summary>`cors --audit`: sends the origins that catch the common CORS mistakes — any origin
/// reflected, <c>null</c> trusted, an origin check that is a substring or an unescaped regular
/// expression, every subdomain trusted, plain http trusted — and grades what comes back. Only GET
/// and OPTIONS are sent.</summary>
public static class CorsAudit
{
    public const string Marker = "certapi-cors-audit";

    /// <summary>The probes for a trusted origin (scheme://host[:port]).</summary>
    public static IReadOnlyList<(string Kind, string Origin)> ProbesFor(Uri trusted, bool includeDevOrigins = false)
    {
        string scheme = trusted.Scheme;
        string host = trusted.Host;
        string port = trusted.IsDefaultPort ? "" : ":" + trusted.Port;
        int altPort = trusted.Port == 8443 ? 8444 : 8443;
        var list = new List<(string, string)>
        {
            ("arbitrary origin", $"https://{Marker}.example"),
            ("null origin", "null"),
            ("prefix look-alike", $"{scheme}://{Marker}{host}{port}"),
            ("suffix look-alike", $"{scheme}://{host}.{Marker}.example{port}"),
            ("subdomain", $"{scheme}://{Marker}.{host}{port}"),
            ("port look-alike", $"{scheme}://{host}:{altPort}"),
            ("userinfo look-alike", $"{scheme}://{host}@{Marker}.example"),
            ("underscore look-alike", $"{scheme}://{host}_.{Marker}.example"),
            ("backtick look-alike", $"{scheme}://{host}%60.{Marker}.example")
        };
        int dot = host.IndexOf('.');
        if (dot > 0 && !System.Net.IPAddress.TryParse(host, out _))
            list.Add(("unescaped dot", $"{scheme}://{host[..dot]}x{host[(dot + 1)..]}{port}"));
        if (scheme == "https") list.Add(("plain http", $"http://{host}{port}"));
        if (includeDevOrigins && !AddressSpace.IsLocalOrigin($"{scheme}://{host}{port}"))
        {
            list.Add(("localhost origin", "http://localhost"));
            list.Add(("localhost dev port", "http://localhost:3000"));
        }
        list.Add(("trusted origin", $"{scheme}://{host}{port}"));
        return list;
    }

    public static async Task<CorsAuditResult> RunAsync(string url, Uri trusted, IReadOnlyList<KeyValuePair<string, string>> headers,
        Func<ApiRequest, Task<ApiResponse>> send, TimeSpan timeout,
        bool includeDevOrigins = false, Func<ApiRequest, Task<ApiResponse>>? sendWithoutCert = null)
    {
        var probes = new List<CorsAuditProbe>();
        bool missingVaryOnReflected = false;
        foreach (var (kind, origin) in ProbesFor(trusted, includeDevOrigins))
        {
            var request = new ApiRequest
            {
                Method = HttpMethod.Get,
                Url = url,
                Headers = headers.Where(h => !h.Key.Equals("Origin", StringComparison.OrdinalIgnoreCase)).Append(new("Origin", origin)).ToList(),
                Timeout = timeout
            };
            var response = await send(request);
            if (response.Error is { } error)
            {
                probes.Add(new CorsAuditProbe(kind, origin, null, null, false, false, error.Message));
                continue;
            }
            string? allow = response.Headers.FirstOrDefault(h => h.Key.Equals("Access-Control-Allow-Origin", StringComparison.OrdinalIgnoreCase)).Value?.Trim();
            bool credentials = response.Headers.Any(h => h.Key.Equals("Access-Control-Allow-Credentials", StringComparison.OrdinalIgnoreCase) && h.Value.Trim() == "true");
            if (allow is not null && allow != "*" &&
                !response.Headers.Where(h => h.Key.Equals("Vary", StringComparison.OrdinalIgnoreCase))
                    .SelectMany(h => h.Value.Split(',')).Select(v => v.Trim())
                    .Any(v => v.Equals("Origin", StringComparison.OrdinalIgnoreCase) || v == "*"))
                missingVaryOnReflected = true;
            probes.Add(new CorsAuditProbe(kind, origin, response.StatusCode, allow, credentials, allow == origin, null));
        }

        // A preflight from a stranger, asking for a method and a header nobody would list: a server
        // that grants both echoes whatever it is asked.
        var preflight = await send(new ApiRequest
        {
            Method = HttpMethod.Options,
            Url = url,
            Headers = new List<KeyValuePair<string, string>>
            {
                new("Origin", $"https://{Marker}.example"),
                new("Access-Control-Request-Method", "PATCH"),
                new("Access-Control-Request-Headers", "x-" + Marker)
            },
            Timeout = timeout
        });

        var findings = new List<CorsFinding>();
        CorsAuditProbe? Find(string kind) => probes.FirstOrDefault(p => p.Kind == kind);
        bool ArbitraryReflected() => Find("arbitrary origin") is { Reflected: true };
        void Grade(string kind, string title, string why, CorsSeverity withCredentials, CorsSeverity without, bool skipWhenArbitrary = false)
        {
            if (skipWhenArbitrary && ArbitraryReflected()) return;
            if (Find(kind) is not { Reflected: true } p) return;
            findings.Add(p.AllowCredentials
                ? new CorsFinding(withCredentials, title + ", with credentials", $"{why} The origin {p.Origin} was allowed with Access-Control-Allow-Credentials: true, so a page there can read responses sent with the user's cookies or client certificate.")
                : new CorsFinding(without, title, $"{why} The origin {p.Origin} was allowed, without credentials."));
        }
        Grade("arbitrary origin", "Any origin is reflected", "The server echoes whatever Origin it is sent.", CorsSeverity.High, CorsSeverity.Medium);
        Grade("null origin", "The null origin is trusted", "Sandboxed iframes, data: URLs and some redirects send Origin: null, and any site can produce one.", CorsSeverity.High, CorsSeverity.Medium);
        Grade("prefix look-alike", "The origin check matches the end of the name", "A domain anyone can register that ends with the trusted name passes.", CorsSeverity.High, CorsSeverity.Medium);
        Grade("suffix look-alike", "The origin check matches the start of the name", "A domain anyone can register that starts with the trusted name passes.", CorsSeverity.High, CorsSeverity.Medium);
        Grade("unescaped dot", "The origin check is a regular expression with an unescaped dot", "A name with any character in place of a dot passes.", CorsSeverity.High, CorsSeverity.Medium);
        Grade("userinfo look-alike", "The origin check is bypassed by a userinfo look-alike (@)", "A crafted origin containing the trusted host before '@' passes an unanchored check.", CorsSeverity.High, CorsSeverity.Medium, skipWhenArbitrary: true);
        Grade("underscore look-alike", "The origin check is bypassed by an underscore look-alike", "A crafted origin with an underscore suffix passes naive prefix matching.", CorsSeverity.High, CorsSeverity.Medium, skipWhenArbitrary: true);
        Grade("backtick look-alike", "The origin check is bypassed by a backtick look-alike", "A crafted origin with an encoded backtick passes naive parser checks.", CorsSeverity.High, CorsSeverity.Medium, skipWhenArbitrary: true);
        Grade("port look-alike", "Non-standard ports on the trusted host are allowed", "The origin check compares hostname without port, so a service on another port passes.", CorsSeverity.Medium, CorsSeverity.Low, skipWhenArbitrary: true);
        Grade("localhost origin", "Localhost development origin is trusted in production", "http://localhost is allowed on a non-local endpoint; a local service or DNS rebinding page can read responses.", CorsSeverity.Medium, CorsSeverity.Low, skipWhenArbitrary: true);
        Grade("localhost dev port", "Localhost dev-server origin (:3000) is trusted in production", "http://localhost:3000 is allowed on a non-local endpoint; remove development origins from production policies.", CorsSeverity.Medium, CorsSeverity.Low, skipWhenArbitrary: true);
        Grade("plain http", "Plain http is trusted", "Anyone on the network path can inject a page into an http:// origin.", CorsSeverity.Medium, CorsSeverity.Low);
        Grade("subdomain", "Every subdomain is trusted", "One forgotten or taken-over subdomain, or one XSS on any of them, is enough.", CorsSeverity.Low, CorsSeverity.Info);

        if (missingVaryOnReflected)
            findings.Add(new CorsFinding(CorsSeverity.Low, "Dynamic Access-Control-Allow-Origin without Vary: Origin",
                "The server returns a specific Access-Control-Allow-Origin without Vary: Origin, risking shared-cache / CDN poisoning across origins."));

        if (probes.Any(p => p.AllowOrigin == "*"))
            findings.Add(probes.Any(p => p.AllowOrigin == "*" && p.AllowCredentials)
                ? new CorsFinding(CorsSeverity.Low, "Access-Control-Allow-Origin: * with credentials", "Browsers refuse this combination, so credentialed requests fail; for public data, * alone is enough.")
                : new CorsFinding(CorsSeverity.Info, "Any origin may read responses without credentials (*)", "Fine for public data; nothing sent with cookies or a client certificate is readable this way."));
        if (preflight.Error is null && preflight.StatusCode is >= 200 and < 300)
        {
            bool method = preflight.Headers.Any(h => h.Key.Equals("Access-Control-Allow-Methods", StringComparison.OrdinalIgnoreCase) && h.Value.Contains("PATCH", StringComparison.OrdinalIgnoreCase));
            bool header = preflight.Headers.Any(h => h.Key.Equals("Access-Control-Allow-Headers", StringComparison.OrdinalIgnoreCase) && h.Value.Contains("x-" + Marker, StringComparison.OrdinalIgnoreCase));
            bool origin = preflight.Headers.Any(h => h.Key.Equals("Access-Control-Allow-Origin", StringComparison.OrdinalIgnoreCase) && h.Value.Trim() == $"https://{Marker}.example");
            bool preflightCreds = preflight.Headers.Any(h => h.Key.Equals("Access-Control-Allow-Credentials", StringComparison.OrdinalIgnoreCase) && h.Value.Trim() == "true");
            if (origin && method && header)
                findings.Add(new CorsFinding(CorsSeverity.Info, "Preflights grant whatever they ask for",
                    "A preflight from an unknown origin asking for PATCH and an invented header got both; the preflight is not limiting anything."));
            if (origin && preflightCreds && !ArbitraryReflected())
                findings.Add(new CorsFinding(CorsSeverity.High, "Preflights reflect arbitrary origins with credentials",
                    "OPTIONS preflight echoes an untrusted origin with Access-Control-Allow-Credentials: true even though GET did not."));
        }

        if (sendWithoutCert is not null)
        {
            string trustedOrigin = $"{trusted.Scheme}://{trusted.Authority}";
            var noCertPreflight = await sendWithoutCert(new ApiRequest
            {
                Method = HttpMethod.Options,
                Url = url,
                Headers = new List<KeyValuePair<string, string>>
                {
                    new("Origin", trustedOrigin),
                    new("Access-Control-Request-Method", "PUT")
                },
                Timeout = timeout
            });
            if (noCertPreflight.Error is not null || noCertPreflight.StatusCode is < 200 or >= 300)
                findings.Add(new CorsFinding(CorsSeverity.Medium, "Preflight fails without a client certificate",
                    $"An OPTIONS preflight from {trustedOrigin} without a client certificate {(noCertPreflight.Error is not null ? "failed at TLS/transport (" + noCertPreflight.Error.Message + ")" : $"returned {noCertPreflight.StatusCode}")}; Fetch sends preflights without credentials, so browsers may fail before CORS runs."));
        }

        return new CorsAuditResult(probes, findings.OrderByDescending(f => f.Severity).ToList(), $"{trusted.Scheme}://{trusted.Authority}");
    }

    public static string Describe(CorsAuditResult result, string url)
    {
        var sb = new StringBuilder();
        sb.Append("audit      ").Append(url).Append("  (trusted origin ").Append(result.BaseOrigin).AppendLine(")");
        int width = result.Probes.Count == 0 ? 0 : result.Probes.Max(x => x.Origin.Length) + 1;
        foreach (var p in result.Probes)
        {
            string answer = p.Error is not null ? "no answer: " + p.Error
                : p.AllowOrigin is null ? "no Access-Control-Allow-Origin"
                : $"Access-Control-Allow-Origin: {p.AllowOrigin}{(p.AllowCredentials ? " + credentials" : "")}";
            sb.Append("  ").Append(p.Reflected ? "allowed  " : p.AllowOrigin == "*" ? "any (*)  " : "refused  ")
              .Append(p.Kind.PadRight(18)).Append(p.Origin.PadRight(width)).Append(' ').AppendLine(answer);
        }
        if (result.Findings.Count == 0) sb.AppendLine("findings   none: only the origins it should allow were allowed");
        else
            foreach (var f in result.Findings)
                sb.Append(f.Severity.ToString().ToUpperInvariant().PadRight(7)).Append("    ").Append(f.Title).Append(". ").AppendLine(f.Detail);
        if (result.Probes.FirstOrDefault(p => p.Kind == "trusted origin") is { Reflected: false, AllowOrigin: not "*" })
            sb.AppendLine("note       the trusted origin itself was not allowed; give the origin the API is meant to serve with --origin");
        return sb.ToString().TrimEnd();
    }
}

/// <summary>Private Network Access's address spaces: whether an address or an origin's host is
/// loopback or private rather than public.</summary>
public static class AddressSpace
{
    public static bool IsPrivate(System.Net.IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (System.Net.IPAddress.IsLoopback(address)) return true;
        var b = address.GetAddressBytes();
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            return b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168) ||
                   (b[0] == 169 && b[1] == 254) || (b[0] == 100 && b[1] is >= 64 and <= 127) || b[0] == 0;
        return (b[0] & 0xFE) == 0xFC || (b[0] == 0xFE && (b[1] & 0xC0) == 0x80) || address.Equals(System.Net.IPAddress.IPv6None);
    }

    /// <summary>A page's origin is local when its host is loopback, a private address literal, or
    /// a name that only resolves inside a network (localhost, *.localhost, *.local, *.internal).</summary>
    public static bool IsLocalOrigin(string origin)
    {
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var u)) return false;
        string host = u.Host.Trim('[', ']');
        if (System.Net.IPAddress.TryParse(host, out var ip)) return IsPrivate(ip);
        return host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
               host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase) ||
               host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ||
               host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>The CORS lines of the Diagnostics view: for a request that carried an Origin header,
/// whether a page there could read the answer, without credentials and with them.</summary>
public static class CorsReport
{
    public static string? For(IEnumerable<KeyValuePair<string, string>> requestHeaders, ApiResponse response)
    {
        string? origin = requestHeaders.LastOrDefault(h => h.Key.Equals("Origin", StringComparison.OrdinalIgnoreCase)).Value?.Trim();
        if (string.IsNullOrEmpty(origin) || response.Error is not null) return null;
        var plain = new CorsRequest(origin, "GET", Array.Empty<KeyValuePair<string, string>>(), Credentials: false);
        var (without, _, notes, readable, hidden) = CorsRules.CheckResponse(plain, response.Headers);
        var (with, _, _, _, _) = CorsRules.CheckResponse(plain with { Credentials = true }, response.Headers);
        var sb = new StringBuilder();
        sb.AppendLine($"CORS  (Origin: {origin})");
        sb.AppendLine(without.Count == 0 ? "  ✓ a page there can read this response" : $"  ✗ a page there cannot read it: {without[0]}");
        if (without.Count == 0)
            sb.AppendLine(with.Count == 0 ? "  ✓ also with credentials (cookies, the client certificate)" : $"  ✗ not with credentials: {with[0]}");
        if (without.Count == 0)
        {
            sb.AppendLine($"  readable: {(readable.Count == 0 ? "(none)" : string.Join(", ", readable))}");
            if (hidden.Count > 0) sb.AppendLine($"  hidden:   {string.Join(", ", hidden)}");
        }
        foreach (var n in notes) sb.AppendLine("  note: " + n);
        sb.Append("  (the response only; `certapi cors` also checks the preflight)");
        return sb.ToString();
    }
}

/// <summary>One cross-origin request from a capture, and what a browser made of it.</summary>
public sealed record CorsHarRow(string Method, string Url, string Origin, bool Credentials, int Status,
    string? PreflightReason, bool PreflightRecorded, bool Allowed, IReadOnlyList<string> Problems, IReadOnlyList<string> Notes);

/// <summary>`cors --from-har`: every cross-origin request in a browser capture, judged from the
/// headers the capture recorded — the preflight when the capture has it, and the response.</summary>
public static class CorsHar
{
    private static IReadOnlyList<KeyValuePair<string, string>> Pairs(IEnumerable<HarNameValue> headers) =>
        headers.Where(h => !h.Name.StartsWith(':')).Select(h => new KeyValuePair<string, string>(h.Name, h.Value)).ToList();

    private static string? Header(IEnumerable<HarNameValue> headers, string name) =>
        headers.LastOrDefault(h => h.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value;

    public static IReadOnlyList<CorsHarRow> Check(Har har, string? originOverride, bool credentials)
    {
        var entries = har.Log.Entries;
        bool IsPreflight(HarEntry e) => e.Request.Method.Equals("OPTIONS", StringComparison.OrdinalIgnoreCase) &&
                                        Header(e.Request.Headers, "Access-Control-Request-Method") is not null;
        var rows = new List<CorsHarRow>();
        foreach (var entry in entries)
        {
            if (IsPreflight(entry)) continue;
            if (!Uri.TryCreate(entry.Request.Url, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https")) continue;
            string? origin = originOverride ?? Header(entry.Request.Headers, "Origin");
            if (origin is null) continue;
            if (Uri.TryCreate(origin, UriKind.Absolute, out var originUri) && CorsProbe.SameOrigin(originUri, url)) continue;

            var requestHeaders = Pairs(entry.Request.Headers)
                .Where(h => !h.Key.Equals("Origin", StringComparison.OrdinalIgnoreCase)).ToList();
            bool sentCredentials = credentials || Header(entry.Request.Headers, "Cookie") is not null;
            var request = new CorsRequest(origin, entry.Request.Method.ToUpperInvariant(), requestHeaders, sentCredentials);
            var problems = new List<string>();
            var notes = new List<string>();
            string? reason = CorsRules.PreflightReason(request);
            bool preflightRecorded = false;
            if (reason is not null)
            {
                var preflight = entries.LastOrDefault(e => IsPreflight(e) && e.Request.Url == entry.Request.Url &&
                                                          e.StartedDateTime <= entry.StartedDateTime) ??
                                entries.FirstOrDefault(e => IsPreflight(e) && e.Request.Url == entry.Request.Url);
                if (preflight is not null && preflight.Response.Status != 0)
                {
                    preflightRecorded = true;
                    problems.AddRange(CorsRules.CheckPreflight(request, preflight.Response.Status, Pairs(preflight.Response.Headers)).Problems);
                }
                else notes.Add("needs a preflight, which the capture does not hold (export it with the preflight requests shown, or use --live)");
            }
            if (problems.Count == 0)
            {
                if (entry.Response.Status == 0)
                    problems.Add("the browser recorded no response: it blocked or failed the request (look for the CORS error in the console)");
                else
                    problems.AddRange(CorsRules.CheckResponse(request, Pairs(entry.Response.Headers)).Problems);
            }
            rows.Add(new CorsHarRow(entry.Request.Method.ToUpperInvariant(), entry.Request.Url, origin, sentCredentials,
                entry.Response.Status, reason, preflightRecorded, problems.Count == 0, problems, notes));
        }
        return rows;
    }
}

/// <summary>What a server's CORS policy has to grant for the requests checked.</summary>
public sealed record CorsNeeds(
    IReadOnlyList<string> Origins,
    IReadOnlyList<string> Methods,
    IReadOnlyList<string> Headers,
    IReadOnlyList<string> Expose,
    bool Credentials,
    bool PrivateNetwork)
{
    /// <summary>The needs of a set of checked requests: their origins, the methods and headers
    /// that made a preflight necessary, the response headers a script could not see.</summary>
    public static CorsNeeds From(IEnumerable<(CorsRequest Request, CorsProbeResult? Result)> checks)
    {
        var list = checks.ToList();
        return new CorsNeeds(
            list.Select(c => c.Request.Origin).Where(o => o != "null").Distinct(StringComparer.Ordinal).ToList(),
            list.Select(c => c.Request.Method.ToUpperInvariant()).Distinct(StringComparer.Ordinal).OrderBy(m => m, StringComparer.Ordinal).ToList(),
            list.SelectMany(c => CorsRules.UnsafeHeaderNames(c.Request)).Distinct(StringComparer.Ordinal).OrderBy(h => h, StringComparer.Ordinal).ToList(),
            list.SelectMany(c => c.Result?.Verdict.Hidden ?? Array.Empty<string>())
                .Where(h => !h.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase) && !h.Equals("Date", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            list.Any(c => c.Request.Credentials),
            list.Any(c => c.Request.PrivateNetwork));
    }
}

/// <summary>`cors --suggest`: the server configuration that grants exactly what was checked, for
/// the frameworks people actually run — an allow-list, never a reflected origin.</summary>
public static class CorsSuggest
{
    public static readonly IReadOnlyList<string> Frameworks = new[]
    {
        "aspnet", "nginx", "express", "spring", "iis", "apigateway",
        "apim", "yarp", "cloudflare", "caddy", "traefik", "apache", "fastapi", "go"
    };

    public static string For(CorsNeeds n, string? framework = null, int maxAge = 600)
    {
        var sb = new StringBuilder();
        var origins = n.Origins.Count > 0 ? n.Origins : new[] { "https://app.example.com" };
        var methods = n.Methods.Where(m => m is not ("GET" or "HEAD" or "POST")).ToList();
        var allMethods = n.Methods.Count > 0 ? n.Methods : new[] { "GET" };
        string Q(IEnumerable<string> xs) => string.Join(", ", xs.Select(x => $"\"{x}\""));
        bool Want(string f) => framework is null || framework.Equals(f, StringComparison.OrdinalIgnoreCase);

        sb.AppendLine($"needs      origin{(origins.Count > 1 ? "s" : "")} {string.Join(", ", origins)}" +
                      $"{(methods.Count > 0 ? $"; methods {string.Join(", ", methods)}" : "")}" +
                      $"{(n.Headers.Count > 0 ? $"; headers {string.Join(", ", n.Headers)}" : "")}" +
                      $"{(n.Expose.Count > 0 ? $"; expose {string.Join(", ", n.Expose)}" : "")}" +
                      $"{(n.Credentials ? "; credentials" : "")}{(n.PrivateNetwork ? "; private network" : "")}");

        if (Want("aspnet"))
        {
            sb.AppendLine().AppendLine("ASP.NET Core (Program.cs):");
            sb.AppendLine("  builder.Services.AddCors(o => o.AddPolicy(\"app\", p => p");
            sb.AppendLine($"      .WithOrigins({Q(origins)})");
            sb.AppendLine($"      .WithMethods({Q(allMethods)})");
            if (n.Headers.Count > 0) sb.AppendLine($"      .WithHeaders({Q(n.Headers)})");
            if (n.Expose.Count > 0) sb.AppendLine($"      .WithExposedHeaders({Q(n.Expose)})");
            if (n.Credentials) sb.AppendLine("      .AllowCredentials()");
            sb.AppendLine($"      .SetPreflightMaxAge(TimeSpan.FromSeconds({maxAge}))));");
            sb.AppendLine("  // after UseRouting, before UseAuthentication and UseAuthorization:");
            sb.AppendLine("  app.UseCors(\"app\");");
            if (n.PrivateNetwork) sb.AppendLine("  // Private Network Access has no built-in option: add Access-Control-Allow-Private-Network: true to OPTIONS answers in a small middleware.");
        }
        if (Want("nginx"))
        {
            sb.AppendLine().AppendLine("nginx (in the location; with several origins, map $http_origin to an allowed value first):");
            string origin = origins.Count == 1 ? $"\"{origins[0]}\"" : "$cors_origin";
            if (origins.Count > 1)
            {
                sb.AppendLine("  # in http {}:  map $http_origin $cors_origin { default \"\";");
                foreach (var o in origins) sb.AppendLine($"  #   \"{o}\" $http_origin;");
                sb.AppendLine("  # }");
            }
            sb.AppendLine("  if ($request_method = OPTIONS) {");
            sb.AppendLine($"      add_header Access-Control-Allow-Origin {origin} always;");
            if (n.Credentials) sb.AppendLine("      add_header Access-Control-Allow-Credentials \"true\" always;");
            sb.AppendLine($"      add_header Access-Control-Allow-Methods \"{string.Join(", ", allMethods)}\" always;");
            if (n.Headers.Count > 0) sb.AppendLine($"      add_header Access-Control-Allow-Headers \"{string.Join(", ", n.Headers)}\" always;");
            if (n.PrivateNetwork) sb.AppendLine("      add_header Access-Control-Allow-Private-Network \"true\" always;");
            sb.AppendLine($"      add_header Access-Control-Max-Age {maxAge} always;");
            sb.AppendLine("      add_header Vary Origin always;");
            sb.AppendLine("      return 204;");
            sb.AppendLine("  }");
            sb.AppendLine($"  add_header Access-Control-Allow-Origin {origin} always;");
            if (n.Credentials) sb.AppendLine("  add_header Access-Control-Allow-Credentials \"true\" always;");
            if (n.Expose.Count > 0) sb.AppendLine($"  add_header Access-Control-Expose-Headers \"{string.Join(", ", n.Expose)}\" always;");
            sb.AppendLine("  add_header Vary Origin always;");
        }
        if (Want("express"))
        {
            sb.AppendLine().AppendLine("Express (npm install cors):");
            sb.AppendLine("  app.use(require('cors')({");
            sb.AppendLine($"    origin: [{string.Join(", ", origins.Select(o => $"'{o}'"))}],");
            sb.AppendLine($"    methods: [{string.Join(", ", allMethods.Select(m => $"'{m}'"))}],");
            if (n.Headers.Count > 0) sb.AppendLine($"    allowedHeaders: [{string.Join(", ", n.Headers.Select(h => $"'{h}'"))}],");
            if (n.Expose.Count > 0) sb.AppendLine($"    exposedHeaders: [{string.Join(", ", n.Expose.Select(h => $"'{h}'"))}],");
            if (n.Credentials) sb.AppendLine("    credentials: true,");
            if (n.PrivateNetwork) sb.AppendLine("    // Private Network Access: answer OPTIONS with Access-Control-Allow-Private-Network: true yourself");
            sb.AppendLine($"    maxAge: {maxAge}");
            sb.AppendLine("  }));");
        }
        if (Want("spring"))
        {
            sb.AppendLine().AppendLine("Spring (a WebMvcConfigurer):");
            sb.AppendLine("  @Override public void addCorsMappings(CorsRegistry registry) {");
            sb.AppendLine("      registry.addMapping(\"/**\")");
            sb.AppendLine($"          .allowedOrigins({Q(origins)})");
            sb.AppendLine($"          .allowedMethods({Q(allMethods)})");
            if (n.Headers.Count > 0) sb.AppendLine($"          .allowedHeaders({Q(n.Headers)})");
            if (n.Expose.Count > 0) sb.AppendLine($"          .exposedHeaders({Q(n.Expose)})");
            if (n.Credentials) sb.AppendLine("          .allowCredentials(true)");
            if (n.PrivateNetwork) sb.AppendLine("          .allowPrivateNetwork(true)");
            sb.AppendLine($"          .maxAge({maxAge});");
            sb.AppendLine("  }");
            sb.AppendLine("  // with Spring Security, also http.cors(Customizer.withDefaults()) so preflights skip authentication");
        }
        if (Want("iis"))
        {
            sb.AppendLine().AppendLine("IIS (web.config, with the IIS CORS Module installed):");
            sb.AppendLine("  <system.webServer>");
            sb.AppendLine("    <cors enabled=\"true\" failUnlistedOrigins=\"true\">");
            foreach (var o in origins)
            {
                sb.AppendLine($"      <add origin=\"{o}\"{(n.Credentials ? " allowCredentials=\"true\"" : "")} maxAge=\"{maxAge}\">");
                if (n.Headers.Count > 0)
                {
                    sb.AppendLine("        <allowHeaders allowAllRequestedHeaders=\"false\">");
                    foreach (var h in n.Headers) sb.AppendLine($"          <add header=\"{h}\" />");
                    sb.AppendLine("        </allowHeaders>");
                }
                sb.AppendLine("        <allowMethods>");
                foreach (var m in allMethods) sb.AppendLine($"          <add method=\"{m}\" />");
                sb.AppendLine("        </allowMethods>");
                if (n.Expose.Count > 0)
                {
                    sb.AppendLine("        <exposeHeaders>");
                    foreach (var h in n.Expose) sb.AppendLine($"          <add header=\"{h}\" />");
                    sb.AppendLine("        </exposeHeaders>");
                }
                sb.AppendLine("      </add>");
            }
            sb.AppendLine("    </cors>");
            sb.AppendLine("  </system.webServer>");
        }
        if (Want("apigateway"))
        {
            sb.AppendLine().AppendLine("AWS API Gateway (HTTP API: aws apigatewayv2 update-api --api-id <id> --cors-configuration file://cors.json):");
            sb.AppendLine("  {");
            sb.AppendLine($"    \"AllowOrigins\": [{Q(origins)}],");
            sb.AppendLine($"    \"AllowMethods\": [{Q(allMethods)}],");
            if (n.Headers.Count > 0) sb.AppendLine($"    \"AllowHeaders\": [{Q(n.Headers)}],");
            if (n.Expose.Count > 0) sb.AppendLine($"    \"ExposeHeaders\": [{Q(n.Expose)}],");
            sb.AppendLine($"    \"AllowCredentials\": {(n.Credentials ? "true" : "false")},");
            sb.AppendLine($"    \"MaxAge\": {maxAge}");
            sb.AppendLine("  }");
        }
        if (Want("apim"))
        {
            sb.AppendLine().AppendLine("Azure API Management (<inbound> policy XML):");
            sb.AppendLine($"  <cors allow-credentials=\"{(n.Credentials ? "true" : "false")}\">");
            sb.AppendLine("    <allowed-origins>");
            foreach (var o in origins) sb.AppendLine($"      <origin>{o}</origin>");
            sb.AppendLine("    </allowed-origins>");
            sb.AppendLine($"    <allowed-methods preflight-result-max-age=\"{maxAge}\">");
            foreach (var m in allMethods) sb.AppendLine($"      <method>{m}</method>");
            sb.AppendLine("    </allowed-methods>");
            if (n.Headers.Count > 0)
            {
                sb.AppendLine("    <allowed-headers>");
                foreach (var h in n.Headers) sb.AppendLine($"      <header>{h}</header>");
                sb.AppendLine("    </allowed-headers>");
            }
            if (n.Expose.Count > 0)
            {
                sb.AppendLine("    <expose-headers>");
                foreach (var h in n.Expose) sb.AppendLine($"      <header>{h}</header>");
                sb.AppendLine("    </expose-headers>");
            }
            sb.AppendLine("  </cors>");
        }
        if (Want("yarp"))
        {
            sb.AppendLine().AppendLine("ASP.NET Core YARP (Program.cs + appsettings.json route CorsPolicy):");
            sb.AppendLine("  builder.Services.AddCors(o => o.AddPolicy(\"yarp-cors\", p => p");
            sb.AppendLine($"      .WithOrigins({Q(origins)})");
            sb.AppendLine($"      .WithMethods({Q(allMethods)})");
            if (n.Headers.Count > 0) sb.AppendLine($"      .WithHeaders({Q(n.Headers)})");
            if (n.Expose.Count > 0) sb.AppendLine($"      .WithExposedHeaders({Q(n.Expose)})");
            if (n.Credentials) sb.AppendLine("      .AllowCredentials()");
            sb.AppendLine($"      .SetPreflightMaxAge(TimeSpan.FromSeconds({maxAge}))));");
            sb.AppendLine("  app.UseCors();");
            sb.AppendLine("  app.MapReverseProxy();");
            sb.AppendLine("  // In appsettings.json under ReverseProxy.Routes.<route>: \"CorsPolicy\": \"yarp-cors\"");
        }
        if (Want("cloudflare"))
        {
            sb.AppendLine().AppendLine("Cloudflare Worker (JS/TS):");
            sb.AppendLine($"  const ALLOWED = new Set([{string.Join(", ", origins.Select(o => $"'{o}'"))}]);");
            sb.AppendLine("  const origin = request.headers.get('Origin') ?? '';");
            sb.AppendLine("  if (ALLOWED.has(origin)) {");
            sb.AppendLine("    headers.set('Access-Control-Allow-Origin', origin);");
            sb.AppendLine("    headers.append('Vary', 'Origin');");
            if (n.Credentials) sb.AppendLine("    headers.set('Access-Control-Allow-Credentials', 'true');");
            sb.AppendLine($"    if (request.method === 'OPTIONS') {{");
            sb.AppendLine($"      headers.set('Access-Control-Allow-Methods', '{string.Join(", ", allMethods)}');");
            if (n.Headers.Count > 0) sb.AppendLine($"      headers.set('Access-Control-Allow-Headers', '{string.Join(", ", n.Headers)}');");
            if (n.PrivateNetwork) sb.AppendLine("      headers.set('Access-Control-Allow-Private-Network', 'true');");
            sb.AppendLine($"      headers.set('Access-Control-Max-Age', '{maxAge}');");
            sb.AppendLine("      return new Response(null, { status: 204, headers });");
            sb.AppendLine("    }");
            if (n.Expose.Count > 0) sb.AppendLine($"    headers.set('Access-Control-Expose-Headers', '{string.Join(", ", n.Expose)}');");
            sb.AppendLine("  }");
        }
        if (Want("caddy"))
        {
            sb.AppendLine().AppendLine("Caddy (Caddyfile):");
            sb.AppendLine($"  @cors header_regexp origin Origin ^({string.Join("|", origins.Select(System.Text.RegularExpressions.Regex.Escape))})$");
            sb.AppendLine("  handle @cors {");
            sb.AppendLine("      header Access-Control-Allow-Origin {header.Origin}");
            if (n.Credentials) sb.AppendLine("      header Access-Control-Allow-Credentials true");
            if (n.Expose.Count > 0) sb.AppendLine($"      header Access-Control-Expose-Headers \"{string.Join(", ", n.Expose)}\"");
            sb.AppendLine("      header Vary Origin");
            sb.AppendLine("      @preflight method OPTIONS");
            sb.AppendLine("      handle @preflight {");
            sb.AppendLine($"          header Access-Control-Allow-Methods \"{string.Join(", ", allMethods)}\"");
            if (n.Headers.Count > 0) sb.AppendLine($"          header Access-Control-Allow-Headers \"{string.Join(", ", n.Headers)}\"");
            if (n.PrivateNetwork) sb.AppendLine("          header Access-Control-Allow-Private-Network true");
            sb.AppendLine($"          header Access-Control-Max-Age {maxAge}");
            sb.AppendLine("          respond 204");
            sb.AppendLine("      }");
            sb.AppendLine("  }");
        }
        if (Want("traefik"))
        {
            sb.AppendLine().AppendLine("Traefik (dynamic config YAML middleware):");
            sb.AppendLine("  http:");
            sb.AppendLine("    middlewares:");
            sb.AppendLine("      cors-policy:");
            sb.AppendLine("        headers:");
            sb.AppendLine($"          accessControlAllowOriginList: [{Q(origins)}]");
            sb.AppendLine($"          accessControlAllowMethods: [{Q(allMethods)}]");
            if (n.Headers.Count > 0) sb.AppendLine($"          accessControlAllowHeaders: [{Q(n.Headers)}]");
            if (n.Expose.Count > 0) sb.AppendLine($"          accessControlExposeHeaders: [{Q(n.Expose)}]");
            sb.AppendLine($"          accessControlAllowCredentials: {(n.Credentials ? "true" : "false")}");
            sb.AppendLine($"          accessControlMaxAge: {maxAge}");
            sb.AppendLine("          addVaryHeader: true");
        }
        if (Want("apache"))
        {
            sb.AppendLine().AppendLine("Apache httpd (mod_headers in VirtualHost or .htaccess):");
            sb.AppendLine($"  SetEnvIf Origin \"^({string.Join("|", origins.Select(System.Text.RegularExpressions.Regex.Escape))})$\" CORS_ORIGIN=$0");
            sb.AppendLine("  Header always set Access-Control-Allow-Origin \"%{CORS_ORIGIN}e\" env=CORS_ORIGIN");
            if (n.Credentials) sb.AppendLine("  Header always set Access-Control-Allow-Credentials \"true\" env=CORS_ORIGIN");
            sb.AppendLine($"  Header always set Access-Control-Allow-Methods \"{string.Join(", ", allMethods)}\" env=CORS_ORIGIN");
            if (n.Headers.Count > 0) sb.AppendLine($"  Header always set Access-Control-Allow-Headers \"{string.Join(", ", n.Headers)}\" env=CORS_ORIGIN");
            if (n.Expose.Count > 0) sb.AppendLine($"  Header always set Access-Control-Expose-Headers \"{string.Join(", ", n.Expose)}\" env=CORS_ORIGIN");
            if (n.PrivateNetwork) sb.AppendLine("  Header always set Access-Control-Allow-Private-Network \"true\" env=CORS_ORIGIN");
            sb.AppendLine($"  Header always set Access-Control-Max-Age \"{maxAge}\" env=CORS_ORIGIN");
            sb.AppendLine("  Header always merge Vary \"Origin\"");
        }
        if (Want("fastapi"))
        {
            sb.AppendLine().AppendLine("FastAPI (Python):");
            sb.AppendLine("  from fastapi.middleware.cors import CORSMiddleware");
            sb.AppendLine("  app.add_middleware(");
            sb.AppendLine("      CORSMiddleware,");
            sb.AppendLine($"      allow_origins=[{Q(origins)}],");
            sb.AppendLine($"      allow_methods=[{Q(allMethods)}],");
            if (n.Headers.Count > 0) sb.AppendLine($"      allow_headers=[{Q(n.Headers)}],");
            if (n.Expose.Count > 0) sb.AppendLine($"      expose_headers=[{Q(n.Expose)}],");
            sb.AppendLine($"      allow_credentials={(n.Credentials ? "True" : "False")},");
            sb.AppendLine($"      max_age={maxAge},");
            sb.AppendLine("  )");
        }
        if (Want("go"))
        {
            sb.AppendLine().AppendLine("Go (github.com/rs/cors):");
            sb.AppendLine("  handler := cors.New(cors.Options{");
            sb.AppendLine($"      AllowedOrigins:   []string{{{Q(origins)}}},");
            sb.AppendLine($"      AllowedMethods:   []string{{{Q(allMethods)}}},");
            if (n.Headers.Count > 0) sb.AppendLine($"      AllowedHeaders:   []string{{{Q(n.Headers)}}},");
            if (n.Expose.Count > 0) sb.AppendLine($"      ExposedHeaders:   []string{{{Q(n.Expose)}}},");
            sb.AppendLine($"      AllowCredentials: {(n.Credentials ? "true" : "false")},");
            if (n.PrivateNetwork) sb.AppendLine("      AllowPrivateNetwork: true,");
            sb.AppendLine($"      MaxAge:           {maxAge},");
            sb.AppendLine("  }).Handler(mux)");
        }
        sb.AppendLine();
        sb.Append("note       A server that demands the client certificate during the TLS handshake can refuse the preflight before " +
                  "its CORS policy runs: the Fetch standard sends a preflight without credentials, and browsers differ on whether " +
                  "they present a certificate for it. Ask for the certificate as optional at the TLS layer (nginx ssl_verify_client " +
                  "optional, IIS Accept) and require it for everything but OPTIONS; `certapi cors --preflight-without-cert` tests that.");
        return sb.ToString();
    }
}

/// <summary>A WebSocket handshake tried with one Origin: accepted (101) or not.</summary>
public sealed record WebSocketOriginProbe(string Kind, string Origin, bool Accepted, int? Status, string? Error);

/// <summary>WebSockets and Origin: CORS does not apply to a WebSocket, and a browser sends the
/// page's cookies and client certificate on the handshake whatever the page — so the server's own
/// Origin check is the only thing standing between another site and an authenticated socket
/// (cross-site WebSocket hijacking). These try handshakes from chosen origins.</summary>
public static class WebSocketOrigins
{
    /// <summary><paramref name="connect"/> opens a handshake with the given Origin and returns
    /// whether it was accepted, the status, and the error when it was not.</summary>
    public static async Task<IReadOnlyList<WebSocketOriginProbe>> ProbeAsync(
        IEnumerable<(string Kind, string Origin)> origins, Func<string, Task<(bool Accepted, int? Status, string? Error)>> connect)
    {
        var probes = new List<WebSocketOriginProbe>();
        foreach (var (kind, origin) in origins)
        {
            var (accepted, status, error) = await connect(origin);
            probes.Add(new WebSocketOriginProbe(kind, origin, accepted, status, error));
        }
        return probes;
    }

    public static IReadOnlyList<CorsFinding> Grade(IReadOnlyList<WebSocketOriginProbe> probes)
    {
        var findings = new List<CorsFinding>();
        bool Accepted(string kind) => probes.Any(p => p.Kind == kind && p.Accepted);
        const string Why = "A browser sends the user's cookies and client certificate on a WebSocket handshake from any page, and CORS does not apply, so ";
        if (Accepted("arbitrary origin"))
            findings.Add(new CorsFinding(CorsSeverity.High, "Any origin may open the socket",
                Why + "a handshake from any site is accepted: any page the user visits can open an authenticated socket (cross-site WebSocket hijacking)."));
        if (Accepted("null origin"))
            findings.Add(new CorsFinding(CorsSeverity.High, "The null origin may open the socket", Why + "a sandboxed iframe on any site can open an authenticated socket."));
        foreach (var (kind, title) in new[] { ("prefix look-alike", "the end of the name"), ("suffix look-alike", "the start of the name"), ("unescaped dot", "a regular expression with an unescaped dot") })
            if (Accepted(kind) && !Accepted("arbitrary origin"))
                findings.Add(new CorsFinding(CorsSeverity.High, $"The Origin check matches {title}",
                    $"{Why}a look-alike domain anyone can register ({probes.First(p => p.Kind == kind).Origin}) is accepted."));
        if (Accepted("plain http") && !Accepted("arbitrary origin"))
            findings.Add(new CorsFinding(CorsSeverity.Medium, "Plain http may open the socket", "Anyone on the network path can inject a page into an http:// origin."));
        if (Accepted("subdomain") && !Accepted("arbitrary origin"))
            findings.Add(new CorsFinding(CorsSeverity.Low, "Every subdomain may open the socket", "One forgotten or taken-over subdomain, or one XSS on any of them, is enough."));
        return findings.OrderByDescending(f => f.Severity).ToList();
    }

    public static string Describe(IReadOnlyList<WebSocketOriginProbe> probes, IReadOnlyList<CorsFinding>? findings, string url)
    {
        var sb = new StringBuilder();
        sb.Append("websocket  ").AppendLine(url);
        int width = probes.Count == 0 ? 0 : probes.Max(p => p.Origin.Length) + 1;
        foreach (var p in probes)
            sb.Append("  ").Append(p.Accepted ? "accepted " : "refused  ").Append(p.Kind.PadRight(18)).Append(p.Origin.PadRight(width)).Append(' ')
              .AppendLine(p.Accepted ? "101" : p.Status is { } s ? $"{s}" : p.Error ?? "no answer");
        if (findings is not null)
        {
            if (findings.Count == 0) sb.AppendLine("findings   none: only the origins it should accept were accepted");
            foreach (var f in findings)
                sb.Append(f.Severity.ToString().ToUpperInvariant().PadRight(7)).Append("    ").Append(f.Title).Append(". ").AppendLine(f.Detail);
            if (probes.All(p => p.Accepted))
                sb.AppendLine("note       every origin was accepted; that is only harmless when the socket needs no cookie, token or certificate");
        }
        sb.Append("note       WebSockets ignore CORS: a browser opens the socket from any page and sends its cookies and client certificate; only the server's Origin check decides");
        return sb.ToString();
    }
}

/// <summary>One policy difference between two endpoints for a given origin and method.</summary>
public sealed record CorsDiffEntry(string Origin, string Method, string Field, string? Left, string? Right);

/// <summary>Compares the CORS behavior of two endpoints (for example staging vs production, or
/// before vs after a deployment) across verdicts, preflight/response headers, and exposed headers.</summary>
public static class CorsDiff
{
    private static readonly string[] ComparedHeaders =
    {
        "Access-Control-Allow-Origin",
        "Access-Control-Allow-Credentials",
        "Access-Control-Allow-Methods",
        "Access-Control-Allow-Headers",
        "Access-Control-Expose-Headers",
        "Access-Control-Max-Age",
        "Access-Control-Allow-Private-Network",
        "Vary",
        "Cross-Origin-Resource-Policy"
    };

    private static string? Header(ApiResponse? r, string name)
    {
        if (r is null || r.Error is not null) return null;
        var vals = r.Headers.Where(h => h.Key.Equals(name, StringComparison.OrdinalIgnoreCase))
            .Select(h => h.Value.Trim()).ToList();
        return vals.Count == 0 ? null : string.Join(", ", vals);
    }

    public static IReadOnlyList<CorsDiffEntry> Compare(CorsRequest request, CorsProbeResult left, CorsProbeResult right)
    {
        var diffs = new List<CorsDiffEntry>();
        void Add(string field, string? a, string? b)
        {
            if (!string.Equals(a, b, StringComparison.Ordinal))
                diffs.Add(new CorsDiffEntry(request.Origin, request.Method, field, a, b));
        }

        Add("verdict", left.Verdict.Allowed ? "allowed" : "blocked", right.Verdict.Allowed ? "allowed" : "blocked");
        if (left.Preflight is not null || right.Preflight is not null)
        {
            Add("preflight.status", left.Preflight?.StatusCode?.ToString() ?? left.Preflight?.Error?.Message,
                right.Preflight?.StatusCode?.ToString() ?? right.Preflight?.Error?.Message);
            foreach (var h in ComparedHeaders)
                Add("preflight." + h, Header(left.Preflight, h), Header(right.Preflight, h));
        }
        if (left.Response is not null || right.Response is not null)
        {
            Add("response.status", left.Response?.StatusCode?.ToString() ?? left.Response?.Error?.Message,
                right.Response?.StatusCode?.ToString() ?? right.Response?.Error?.Message);
            foreach (var h in ComparedHeaders)
                Add("response." + h, Header(left.Response, h), Header(right.Response, h));
            Add("readable", string.Join(", ", left.Verdict.Readable.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)),
                string.Join(", ", right.Verdict.Readable.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)));
        }
        return diffs;
    }

    public static string Describe(string leftUrl, string rightUrl, IReadOnlyList<CorsDiffEntry> diffs)
    {
        var sb = new StringBuilder();
        sb.Append("left       ").AppendLine(leftUrl);
        sb.Append("right      ").AppendLine(rightUrl);
        if (diffs.Count == 0)
        {
            sb.Append("diff       none: both endpoints behave identically for every checked origin and method");
            return sb.ToString();
        }
        foreach (var g in diffs.GroupBy(d => (d.Origin, d.Method)))
        {
            sb.AppendLine().Append("  ").Append(g.Key.Method).Append("  from ").AppendLine(g.Key.Origin);
            int width = g.Max(d => d.Field.Length) + 2;
            foreach (var d in g)
                sb.Append("    ").Append(d.Field.PadRight(width))
                  .Append(d.Left ?? "(absent)").Append("  →  ").AppendLine(d.Right ?? "(absent)");
        }
        sb.AppendLine().Append($"diff       {diffs.Count} difference(s) found");
        return sb.ToString();
    }
}
