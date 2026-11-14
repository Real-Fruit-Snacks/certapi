using System.Net.Http;
using System.Text;

namespace ApiTester.Core;

/// <summary>What a page would ask for: the origin it runs on, the method and headers its script
/// sets, and whether it sends credentials (cookies, <c>fetch(…, {credentials: "include"})</c>).</summary>
public sealed record CorsRequest(
    string Origin,
    string Method,
    IReadOnlyList<KeyValuePair<string, string>> Headers,
    bool Credentials);

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

    private static readonly HashSet<string> SimpleContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "application/x-www-form-urlencoded", "multipart/form-data", "text/plain"
    };

    /// <summary>Response headers every script may read without being told.</summary>
    public static readonly IReadOnlySet<string> SafelistedResponse = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Cache-Control", "Content-Language", "Content-Length", "Content-Type", "Expires", "Last-Modified", "Pragma"
    };

    /// <summary>Headers a page cannot set: the browser owns them.</summary>
    public static bool Forbidden(string name)
    {
        string n = name.ToLowerInvariant();
        return n is "accept-charset" or "accept-encoding" or "access-control-request-headers" or "access-control-request-method"
                   or "connection" or "content-length" or "cookie" or "cookie2" or "date" or "dnt" or "expect" or "host"
                   or "keep-alive" or "origin" or "referer" or "set-cookie" or "te" or "trailer" or "transfer-encoding"
                   or "upgrade" or "via"
               || n.StartsWith("proxy-", StringComparison.Ordinal) || n.StartsWith("sec-", StringComparison.Ordinal);
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
                return System.Text.RegularExpressions.Regex.IsMatch(value.Trim(), @"^bytes=\d+-\d*$");
            default:
                return false;
        }
    }

    /// <summary>The header names a page's request carries that are not safelisted — what a
    /// preflight's Access-Control-Request-Headers lists: lower case, sorted, de-duplicated.</summary>
    public static IReadOnlyList<string> UnsafeHeaderNames(CorsRequest r) =>
        r.Headers.Where(h => !Forbidden(h.Key) && !SafelistedRequestHeader(h.Key, h.Value))
            .Select(h => h.Key.ToLowerInvariant()).Distinct().OrderBy(n => n, StringComparer.Ordinal).ToList();

    /// <summary>Why a browser would send a preflight first, or null when it would not.</summary>
    public static string? PreflightReason(CorsRequest r)
    {
        var reasons = new List<string>();
        string method = r.Method.ToUpperInvariant();
        if (!SimpleMethods.Contains(method)) reasons.Add($"the method is {method}");
        foreach (var name in UnsafeHeaderNames(r))
        {
            var header = r.Headers.Last(h => h.Key.Equals(name, StringComparison.OrdinalIgnoreCase));
            reasons.Add(name == "content-type"
                ? $"Content-Type is {header.Value.Split(';')[0].Trim()} (only form, multipart and text/plain go without one)"
                : $"the request sets {header.Key}");
        }
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
            problems.Add($"{what} allows the origin {value}, not {r.Origin}" +
                         (value.Equals(r.Origin, StringComparison.OrdinalIgnoreCase) ? " (the comparison is exact, letter case included)" : ""));
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

        var maxAge = Values(headers, "Access-Control-Max-Age");
        notes.Add(maxAge.Count == 0
            ? "no Access-Control-Max-Age: a browser caches this preflight for 5 seconds"
            : int.TryParse(maxAge[0], out var seconds)
                ? $"a browser caches this preflight for {Math.Min(seconds, 7200)} s (Access-Control-Max-Age: {seconds}; Chromium caps it at 2 hours, Firefox at 24 hours)"
                : $"Access-Control-Max-Age '{maxAge[0]}' is not a number, so it is ignored");
        return (problems, passed, notes);
    }

    /// <summary>Judge the actual response, and say which of its headers a script can read.</summary>
    public static (List<string> Problems, List<string> Passed, List<string> Notes, List<string> Readable, List<string> Hidden) CheckResponse(
        CorsRequest r, IReadOnlyList<KeyValuePair<string, string>> headers)
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
        return (problems, passed, notes, readable, hidden);
    }
}

/// <summary>The outcome of checking a URL the way a browser would: the preflight (when one is
/// needed), the request, and the verdict.</summary>
public sealed record CorsProbeResult
{
    public required CorsVerdict Verdict { get; init; }
    public ApiResponse? Preflight { get; init; }
    public ApiResponse? Response { get; init; }
    /// <summary>Why the request itself was not sent (an unsafe method without --actual, or a failed preflight).</summary>
    public string? NotSent { get; init; }
}

/// <summary>Runs the exchange: a preflight like a browser's, then — when the preflight passes and
/// the method is safe, or the caller asked — the request itself with the Origin header.</summary>
public static class CorsProbe
{
    private static readonly HashSet<string> Safe = new(StringComparer.Ordinal) { "GET", "HEAD", "OPTIONS" };

    public static async Task<CorsProbeResult> RunAsync(
        string url, CorsRequest request, string? body, string? contentType, bool? sendActual,
        Func<ApiRequest, Task<ApiResponse>> send, TimeSpan timeout)
    {
        var problems = new List<string>();
        var passed = new List<string>();
        var notes = new List<string>();
        foreach (var h in request.Headers.Where(h => CorsRules.Forbidden(h.Key)))
            notes.Add(h.Key.Equals("Cookie", StringComparison.OrdinalIgnoreCase)
                ? "a page cannot set Cookie; the browser sends its own cookies when the request has credentials (--credentials)"
                : $"a page cannot set {h.Key}; the browser sets it, so it is left out of the check");

        string? reason = CorsRules.PreflightReason(request);
        ApiResponse? preflight = null;
        if (reason is not null)
        {
            preflight = await send(new ApiRequest
            {
                Method = HttpMethod.Options,
                Url = url,
                Headers = CorsRules.PreflightHeaders(request).ToList(),
                Timeout = timeout
            });
            if (preflight.Error is { } error)
                problems.Add("the preflight got no answer: " + error.Message);
            else if (preflight.StatusCode is >= 300 and < 400)
                problems.Add($"the preflight was answered with a redirect ({preflight.StatusCode}); a browser never follows a redirect on a preflight");
            else
            {
                var (p, ok, n) = CorsRules.CheckPreflight(request, preflight.StatusCode ?? 0, preflight.Headers);
                problems.AddRange(p);
                passed.AddRange(ok);
                notes.AddRange(n);
            }
        }

        string method = request.Method.ToUpperInvariant();
        string? notSent = null;
        if (problems.Count > 0) notSent = "a browser stops at the failed preflight";
        else if (sendActual == false) notSent = "not asked for";
        else if (sendActual != true && !Safe.Contains(method)) notSent = $"{method} may change something on the server; --actual sends it";

        ApiResponse? response = null;
        var readable = new List<string>();
        var hidden = new List<string>();
        if (notSent is null)
        {
            var headers = request.Headers.Where(h => !CorsRules.Forbidden(h.Key) || h.Key.Equals("Cookie", StringComparison.OrdinalIgnoreCase) && request.Credentials)
                .Where(h => !h.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
                .ToList();
            headers.Add(new("Origin", request.Origin));
            string? type = contentType ?? request.Headers.LastOrDefault(h => h.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)).Value;
            response = await send(new ApiRequest
            {
                Method = new HttpMethod(method),
                Url = url,
                Headers = headers,
                Body = body,
                ContentType = body is null ? null : type ?? "application/json",
                Timeout = timeout
            });
            if (response.Error is { } error)
                problems.Add("the request got no answer: " + error.Message);
            else
            {
                var (p, ok, n, r, h) = CorsRules.CheckResponse(request, response.Headers);
                problems.AddRange(p);
                passed.AddRange(ok);
                notes.AddRange(n);
                if (response.StatusCode is >= 300 and < 400 &&
                    response.Headers.FirstOrDefault(x => x.Key.Equals("Location", StringComparison.OrdinalIgnoreCase)).Value is { } location)
                    notes.Add($"the response redirects to {location}; a browser follows it and checks CORS again there (and its Origin becomes 'null' if the redirect crosses to another origin)");
                readable = r;
                hidden = h;
            }
        }
        else if (problems.Count == 0)
            notes.Add($"the request itself was not sent ({notSent}), so its response's CORS headers were not checked");

        return new CorsProbeResult
        {
            Preflight = preflight,
            Response = response,
            NotSent = notSent,
            Verdict = new CorsVerdict
            {
                Allowed = problems.Count == 0,
                PreflightReason = reason,
                Problems = problems,
                Passed = passed,
                Notes = notes,
                Readable = readable,
                Hidden = hidden
            }
        };
    }

    /// <summary>The verdict as lines for a terminal.</summary>
    public static string Describe(CorsProbeResult result, CorsRequest request, string url)
    {
        var sb = new StringBuilder();
        var v = result.Verdict;
        if (v.PreflightNeeded)
        {
            sb.Append("preflight  OPTIONS ").Append(url);
            sb.Append(result.Preflight is { Error: null } p ? $" → {p.StatusCode}" : " → no answer").AppendLine();
            sb.Append("           needed because ").AppendLine(v.PreflightReason);
        }
        else sb.AppendLine("preflight  none needed (a simple request)");
        sb.Append("request    ").Append(request.Method.ToUpperInvariant()).Append(' ').Append(url);
        sb.AppendLine(result.Response is { Error: null } r ? $" → {r.StatusCode}" : result.NotSent is { } why ? $" (not sent: {why})" : " → no answer");
        foreach (var ok in v.Passed) sb.Append("  ok       ").AppendLine(ok);
        foreach (var problem in v.Problems) sb.Append("  BLOCKED  ").AppendLine(problem);
        foreach (var note in v.Notes) sb.Append("  note     ").AppendLine(note);
        if (result.Response is { Error: null })
        {
            sb.Append("readable   ").AppendLine(v.Readable.Count == 0 ? "(none)" : string.Join(", ", v.Readable));
            if (v.Hidden.Count > 0) sb.Append("hidden     ").Append(string.Join(", ", v.Hidden)).AppendLine("  (not in Access-Control-Expose-Headers)");
        }
        sb.Append("verdict    ").Append(!v.Allowed
            ? $"blocked: a browser on {request.Origin} would refuse this{(request.Credentials ? " (with credentials)" : "")}"
            : result.Response is null
                ? $"the preflight allows it: a page on {request.Origin} may send this request{(request.Credentials ? " with credentials" : "")}; whether it may read the answer depends on the response, which --actual checks"
                : $"allowed: a page on {request.Origin} can make this request{(request.Credentials ? " with credentials" : "")} and read the response");
        return sb.ToString();
    }
}
