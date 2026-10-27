using System.Text;

namespace ApiTester.Core;

/// <summary>A request reduced to what a code snippet needs.</summary>
public sealed record CodeRequest(
    string Method,
    string Url,
    IReadOnlyList<KeyValuePair<string, string>> Headers,
    string? Body,
    bool Insecure = false,
    string? CertThumbprint = null,
    string? ContentType = null);

/// <summary>Generates a copy-pasteable request snippet in several languages, so a request built in
/// the app can be handed to a script or a teammate. Every value is quoted for its language the way
/// that language reads it — a body with a quote, a backslash, a <c>$</c> or a line break arrives
/// exactly as it was written — and the body's type travels with it.</summary>
public static class CodeGenerator
{
    public static string Generate(string language, CodeRequest r) => language.ToLowerInvariant() switch
    {
        "curl" => Curl(r),
        "powershell" or "ps" => PowerShell(r),
        "python" or "py" => Python(r),
        "csharp" or "c#" or "cs" => CSharp(r),
        _ => throw new ArgumentException($"Unknown language '{language}'. Use curl, powershell, python, or csharp.")
    };

    private static bool IsContentType(string name) => name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase);

    /// <summary>The body's type: a Content-Type header row wins (it is what the user typed), then
    /// the request's own content type. Null without a body.</summary>
    private static string? BodyType(CodeRequest r) =>
        string.IsNullOrEmpty(r.Body) ? null
        : r.Headers.LastOrDefault(h => IsContentType(h.Key)).Value is { Length: > 0 } typed ? typed
        : r.ContentType;

    private static IEnumerable<KeyValuePair<string, string>> OtherHeaders(CodeRequest r) =>
        r.Headers.Where(h => !IsContentType(h.Key));

    // ---------------------------------------------------------------- curl (bash)

    /// <summary>Single quotes: bash interprets nothing inside them, so the only character to deal
    /// with is the single quote itself.</summary>
    private static string Sh(string s) => "'" + s.Replace("'", "'\\''") + "'";

    public static string Curl(CodeRequest r)
    {
        var sb = new StringBuilder();
        sb.Append("curl -X ").Append(r.Method).Append(' ').Append(Sh(r.Url));
        foreach (var h in OtherHeaders(r))
            sb.Append(" \\\n  -H ").Append(Sh($"{h.Key}: {h.Value}"));
        if (BodyType(r) is { } type)
            sb.Append(" \\\n  -H ").Append(Sh($"Content-Type: {type}"));
        if (r.CertThumbprint is not null)
            sb.Append(" \\\n  --cert ").Append(Sh($@"CurrentUser\MY\{r.CertThumbprint}"))
              .Append("   # client cert from the Windows store (curl built with Schannel)");
        if (r.Insecure) sb.Append(" \\\n  -k");
        // --data-raw: --data would read a body that starts with @ as a file name.
        if (!string.IsNullOrEmpty(r.Body))
            sb.Append(" \\\n  --data-raw ").Append(Sh(r.Body));
        return sb.ToString();
    }

    // ---------------------------------------------------------------- PowerShell

    /// <summary>Single quotes: PowerShell expands nothing inside them; a quote is doubled.</summary>
    private static string Ps(string s) => "'" + s.Replace("'", "''") + "'";

    public static string PowerShell(CodeRequest r)
    {
        var sb = new StringBuilder();
        var headers = OtherHeaders(r).ToList();
        if (headers.Count > 0)
        {
            sb.Append("$headers = @{\n");
            foreach (var h in headers)
                sb.Append("  ").Append(Ps(h.Key)).Append(" = ").Append(Ps(h.Value)).Append('\n');
            sb.Append("}\n");
        }
        sb.Append("Invoke-RestMethod -Method ").Append(r.Method).Append(" -Uri ").Append(Ps(r.Url));
        if (headers.Count > 0) sb.Append(" -Headers $headers");
        if (!string.IsNullOrEmpty(r.Body))
            sb.Append(" -Body ").Append(Ps(r.Body));
        if (BodyType(r) is { } type) sb.Append(" -ContentType ").Append(Ps(type));
        if (r.Insecure) sb.Append(" -SkipCertificateCheck");
        if (r.CertThumbprint is not null)
            sb.Append(" `\n  -Certificate (Get-Item ").Append(Ps($@"Cert:\CurrentUser\My\{r.CertThumbprint}")).Append(')');
        return sb.ToString();
    }

    // ---------------------------------------------------------------- Python

    public static string Python(CodeRequest r)
    {
        var sb = new StringBuilder();
        sb.Append("import requests\n\n");
        var headers = OtherHeaders(r).ToList();
        if (BodyType(r) is { } type) headers.Add(new("Content-Type", type));
        if (headers.Count > 0)
        {
            sb.Append("headers = {\n");
            foreach (var h in headers)
                sb.Append("    ").Append(PyStr(h.Key)).Append(": ").Append(PyStr(h.Value)).Append(",\n");
            sb.Append("}\n");
        }
        sb.Append("resp = requests.request(").Append(PyStr(r.Method)).Append(", ").Append(PyStr(r.Url));
        if (headers.Count > 0) sb.Append(", headers=headers");
        if (!string.IsNullOrEmpty(r.Body))
            sb.Append(", data=").Append(PyStr(r.Body)).Append(".encode(\"utf-8\")");
        if (r.Insecure) sb.Append(", verify=False");
        if (r.CertThumbprint is not null) sb.Append(", cert=(\"client.pem\", \"client.key\")  # export your client certificate to PEM");
        sb.Append(")\nprint(resp.status_code)\nprint(resp.text)");
        return sb.ToString();
    }

    // ---------------------------------------------------------------- C#

    public static string CSharp(CodeRequest r)
    {
        var sb = new StringBuilder();
        sb.Append("using var client = new HttpClient();\n");
        sb.Append("var request = new HttpRequestMessage(new HttpMethod(").Append(CsStr(r.Method)).Append("), ").Append(CsStr(r.Url)).Append(");\n");
        // Content-* headers belong to the body; request.Headers refuses them.
        var contentHeaders = new List<KeyValuePair<string, string>>();
        foreach (var h in OtherHeaders(r))
        {
            if (h.Key.StartsWith("Content-", StringComparison.OrdinalIgnoreCase)) { contentHeaders.Add(h); continue; }
            sb.Append("request.Headers.TryAddWithoutValidation(").Append(CsStr(h.Key)).Append(", ").Append(CsStr(h.Value)).Append(");\n");
        }
        if (!string.IsNullOrEmpty(r.Body))
        {
            sb.Append("request.Content = new StringContent(").Append(CsStr(r.Body)).Append(", System.Text.Encoding.UTF8);\n");
            if (BodyType(r) is { } type)
                sb.Append("request.Content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(").Append(CsStr(type)).Append(");\n");
            foreach (var h in contentHeaders)
                sb.Append("request.Content.Headers.TryAddWithoutValidation(").Append(CsStr(h.Key)).Append(", ").Append(CsStr(h.Value)).Append(");\n");
        }
        if (r.CertThumbprint is not null)
            sb.Append("// Attach a client certificate (thumbprint ").Append(r.CertThumbprint).Append(") via SocketsHttpHandler.SslOptions.ClientCertificates.\n");
        sb.Append("var response = await client.SendAsync(request);\n");
        sb.Append("Console.WriteLine((int)response.StatusCode);\n");
        sb.Append("Console.WriteLine(await response.Content.ReadAsStringAsync());");
        return sb.ToString();
    }

    /// <summary>A double-quoted string literal with the escapes Python and C# share; a carriage
    /// return is kept as <c>\r</c> (a CRLF body — multipart, SOAP — must stay CRLF).</summary>
    private static string Escaped(string s)
    {
        var sb = new StringBuilder(s.Length + 2).Append('"');
        foreach (char c in s)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", System.Globalization.CultureInfo.InvariantCulture));
                    else sb.Append(c);
                    break;
            }
        }
        return sb.Append('"').ToString();
    }

    private static string PyStr(string s) => Escaped(s);

    private static string CsStr(string s) => Escaped(s);
}
