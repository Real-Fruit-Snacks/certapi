using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ApiTester.Core;

namespace ApiTester.Cli;

/// <summary>curl's <c>-w/--write-out</c>: a template printed to stdout after the transfer, with
/// <c>%{variable}</c> filled in from the response. The variables are curl's names for what this
/// tool knows; one it does not know is a usage error, never an empty string a script misreads.</summary>
public sealed class WriteOut
{
    private static readonly Regex Variable = new(@"%\{([a-z_]+)(?:\{([^}]+)\})?\}", RegexOptions.CultureInvariant);

    private static readonly HashSet<string> TimeNames = new(StringComparer.Ordinal)
        { "time_namelookup", "time_connect", "time_appconnect", "time_pretransfer", "time_starttransfer", "time_total" };

    public static readonly IReadOnlyList<string> Names =
    [
        "http_code", "response_code", "http_connect", "method", "scheme", "url", "url_effective", "redirect_url",
        "num_redirects", "num_retries", "content_type", "size_download", "size_header", "size_request",
        "speed_download", "time_namelookup", "time_connect", "time_appconnect", "time_pretransfer",
        "time_starttransfer", "time_total", "ssl_verify_result", "tls_version", "filename_effective",
        "errormsg", "exitcode", "header", "json"
    ];

    private readonly string _template;
    public bool NeedsTimes { get; }

    private WriteOut(string template)
    {
        _template = template;
        foreach (Match m in Variable.Matches(template))
        {
            string name = m.Groups[1].Value;
            if (!Names.Contains(name) || (name == "header") != m.Groups[2].Success)
                throw new CliUsageException($"-w/--write-out: unknown variable '{m.Value}'. Known: " +
                    string.Join(" ", Names.Select(n => n == "header" ? "%{header{Name}}" : "%{" + n + "}")) + ".");
            if (TimeNames.Contains(name) || name is "speed_download" or "json") NeedsTimes = true;
        }
    }

    /// <summary>The template as given, or read from <c>@file</c> (<c>@-</c> is not supported: stdin
    /// may be the request body).</summary>
    public static WriteOut Load(string spec)
    {
        if (spec.StartsWith('@'))
        {
            string path = spec[1..];
            if (!File.Exists(path)) throw new CliDataException($"-w/--write-out: file not found: {path}");
            return new WriteOut(File.ReadAllText(path));
        }
        return new WriteOut(spec);
    }

    public sealed record Facts(ApiResponse Response, ApiRequest Request, string Url, PhaseTimings? Phases, string? OutputFile, int ExitCode);

    public string Render(Facts facts)
    {
        var values = Values(facts);
        string filled = Variable.Replace(_template, m => m.Groups[1].Value switch
        {
            "header" => Header(facts.Response, m.Groups[2].Value),
            "json" => JsonSerializer.Serialize(values),
            // Times print the way curl prints them: seconds with six decimals.
            var name => values.TryGetValue(name, out var v)
                ? v is double d ? d.ToString("F6", CultureInfo.InvariantCulture) : Convert.ToString(v, CultureInfo.InvariantCulture) ?? ""
                : ""
        });
        return Unescape(filled);
    }

    private static string Header(ApiResponse response, string name) =>
        string.Join(", ", response.Headers.Where(h => h.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Select(h => h.Value));

    private static Dictionary<string, object?> Values(Facts f)
    {
        var r = f.Response;
        var p = f.Phases;
        double S(TimeSpan? t) => Math.Round((t ?? TimeSpan.Zero).TotalSeconds, 6);
        TimeSpan dns = p?.Dns ?? TimeSpan.Zero;
        TimeSpan connect = dns + (p?.Connect ?? TimeSpan.Zero);
        TimeSpan app = connect + (p?.Tls ?? TimeSpan.Zero);
        TimeSpan total = p?.Total ?? r.Elapsed;
        string finalUrl = r.FinalUrl(f.Url);
        bool isRedirect = r.StatusCode is >= 300 and < 400;
        string? location = isRedirect ? r.Headers.FirstOrDefault(h => h.Key.Equals("Location", StringComparison.OrdinalIgnoreCase)).Value : null;
        string? redirectUrl = location is not null && Uri.TryCreate(new Uri(finalUrl), location, out var abs) ? abs.ToString() : null;
        long headerBytes = r.Headers.Sum(h => (long)h.Key.Length + h.Value.Length + 4) + 2;
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["http_code"] = r.StatusCode ?? 0,
            ["response_code"] = r.StatusCode ?? 0,
            ["http_connect"] = 0,
            ["method"] = f.Request.Method.Method,
            ["scheme"] = Uri.TryCreate(finalUrl, UriKind.Absolute, out var u) ? u.Scheme.ToUpperInvariant() : "",
            ["url"] = f.Url,
            ["url_effective"] = finalUrl,
            ["redirect_url"] = redirectUrl ?? "",
            ["num_redirects"] = r.Redirects.Count,
            ["num_retries"] = Math.Max(0, r.Attempts - 1),
            ["content_type"] = r.ContentType ?? "",
            ["size_download"] = r.Body.LongLength,
            ["size_header"] = r.StatusCode is null ? 0 : headerBytes,
            ["size_request"] = Encoding.UTF8.GetByteCount(f.Request.Body ?? "") + (f.Request.BodyBytes?.LongLength ?? 0),
            ["speed_download"] = total > TimeSpan.Zero ? (long)Math.Round(r.Body.LongLength / total.TotalSeconds) : 0L,
            ["time_namelookup"] = S(dns),
            ["time_connect"] = S(connect),
            ["time_appconnect"] = r.Connection?.TlsProtocol is null && p?.Tls is null ? 0 : S(app),
            ["time_pretransfer"] = S(app),
            ["time_starttransfer"] = S(p?.FirstByte ?? r.Elapsed),
            ["time_total"] = S(total),
            ["ssl_verify_result"] = r.Error?.Kind == ApiErrorKind.ServerCertificateUntrusted ? 1 : 0,
            ["tls_version"] = r.Connection?.TlsProtocol ?? "",
            ["filename_effective"] = f.OutputFile ?? "",
            ["errormsg"] = r.Error?.Message ?? "",
            ["exitcode"] = f.ExitCode
        };
    }

    /// <summary>\n, \r and \t, as curl reads them in a write-out template.</summary>
    private static string Unescape(string s) =>
        s.Replace("\\n", "\n").Replace("\\r", "\r").Replace("\\t", "\t");
}
