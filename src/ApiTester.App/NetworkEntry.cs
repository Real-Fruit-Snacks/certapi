using System;
using System.Collections.Generic;

namespace ApiTester.App;

/// <summary>One row in the Network trace — a single HTTP call (a request you sent, or a resource
/// the Rendered view fetched). Metadata only; response bodies are not kept.</summary>
public sealed class NetworkEntry
{
    public DateTime Timestamp { get; init; } = DateTime.Now;
    public string Method { get; init; } = "GET";
    public string Url { get; init; } = "";
    public int? StatusCode { get; init; }
    public string? ReasonPhrase { get; init; }
    public string? ContentType { get; init; }
    public long Size { get; init; }
    public double ElapsedMs { get; init; }
    public string? ClientCertSubject { get; init; }
    public bool ClientCertPresented { get; init; }
    public string? Error { get; init; }
    public string Source { get; init; } = "Request";  // "Request" or "Rendered"
    public List<KeyValuePair<string, string>> RequestHeaders { get; init; } = new();
    public List<KeyValuePair<string, string>> ResponseHeaders { get; init; } = new();

    private string? _statusLabel;
    private string? _statusClass;
    private string? _sizeLabel;
    private string? _timeLabel;
    private string? _shortType;

    public bool UsedClientCert => !string.IsNullOrEmpty(ClientCertSubject);

    public string StatusLabel => _statusLabel ??= Error is not null ? "ERR" : StatusCode?.ToString() ?? "—";

    /// <summary>The status class used by the filter bar: "2xx".."5xx", "ERR", or "" when unknown.</summary>
    public string StatusClass => _statusClass ??=
        Error is not null ? "ERR" :
        StatusCode is { } c && c >= 100 && c <= 599 ? $"{c / 100}xx" : "";

    public string SizeLabel => _sizeLabel ??= Error is not null ? "" : FormatSize(Size);

    public static string FormatSize(long bytes) =>
        bytes < 1024 ? $"{bytes} B" :
        bytes < 1024 * 1024 ? $"{bytes / 1024.0:F1} KB" :
        $"{bytes / (1024.0 * 1024.0):F1} MB";

    /// <summary>Whether this entry passes the Network filter bar.</summary>
    /// <param name="text">Substring matched against the URL, method, status, and content type.</param>
    /// <param name="statusClass">"All", "2xx".."5xx", or "ERR".</param>
    /// <param name="certOnly">Show only calls made with a client certificate.</param>
    public bool Matches(string? text, string statusClass, bool certOnly)
    {
        if (certOnly && !UsedClientCert) return false;
        if (statusClass != "All" &&
            !string.Equals(StatusClass, statusClass, StringComparison.OrdinalIgnoreCase)) return false;
        text = text?.Trim();
        if (string.IsNullOrEmpty(text)) return true;
        return Url.Contains(text, StringComparison.OrdinalIgnoreCase)
            || Method.Contains(text, StringComparison.OrdinalIgnoreCase)
            || StatusLabel.Contains(text, StringComparison.OrdinalIgnoreCase)
            || (ContentType?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    /// <summary>A curl command that reproduces this call (method, URL, and request headers).</summary>
    public string ToCurl()
    {
        var sb = new System.Text.StringBuilder("curl");
        if (!Method.Equals("GET", StringComparison.OrdinalIgnoreCase))
            sb.Append(" -X ").Append(Method.ToUpperInvariant());
        sb.Append(" \"").Append(Url.Replace("\"", "\\\"")).Append('"');
        foreach (var h in RequestHeaders)
            sb.Append(" -H \"").Append(h.Key).Append(": ").Append(h.Value.Replace("\"", "\\\"")).Append('"');
        return sb.ToString();
    }

    public string TimeLabel => _timeLabel ??= Error is not null ? "" : $"{ElapsedMs:F0} ms";

    /// <summary>Short content-type for the table (the subtype, e.g. "json", "html", "css").</summary>
    public string ShortType
    {
        get
        {
            if (_shortType is not null) return _shortType;
            var ct = ContentType;
            if (string.IsNullOrEmpty(ct)) return _shortType = Error is not null ? "—" : "";
            int semi = ct.IndexOf(';');
            if (semi >= 0) ct = ct[..semi];
            int slash = ct.IndexOf('/');
            return _shortType = (slash >= 0 ? ct[(slash + 1)..] : ct).Trim();
        }
    }
}
