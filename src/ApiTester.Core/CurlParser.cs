using System.Globalization;
using System.Text;

namespace ApiTester.Core;

/// <summary>Parses a <c>curl</c> command line into a <see cref="ParsedRequest"/>: method, URL,
/// headers, body (<c>-d</c> and friends, <c>--json</c>, <c>-F</c>, <c>-G</c>), user, insecure. Quoting
/// follows bash (what "Copy as cURL (bash)" writes, <c>$'…'</c> included); a command copied as
/// "Copy as cURL (cmd)" — <c>^</c>-escaped for Windows' cmd.exe — is read with cmd's rules. Options
/// that take a value this importer does not use (<c>--cert</c>, <c>-o</c>, <c>-m</c>, …) have that
/// value skipped, so it is never mistaken for the URL.</summary>
public static class CurlParser
{
    // Short options that take a value; every other short option is a flag.
    private const string ShortWithValue = "XHAebduFmoEKrTwxUYyzcQCtDP";

    // Long options that take a value this parser does not use, so it is skipped.
    private static readonly HashSet<string> LongWithValue = new(StringComparer.Ordinal)
    {
        "--cert", "--key", "--cacert", "--capath", "--cert-type", "--key-type", "--pass", "--ciphers",
        "--tls13-ciphers", "--curves", "--crlfile", "--pinnedpubkey", "--tls-max", "--engine",
        "--proxy-cert", "--proxy-key", "--proxy-cacert", "--proxy-capath", "--proxy-pass", "--proxy-cert-type",
        "--proxy-key-type", "--proxy-ciphers", "--proxy-pinnedpubkey", "--proxy-crlfile", "--proxy-header",
        "--output", "--output-dir", "--max-time", "--connect-timeout", "--write-out", "--config",
        "--retry", "--retry-delay", "--retry-max-time", "--proxy", "--proxy-user", "--preproxy", "--noproxy",
        "--socks4", "--socks4a", "--socks5", "--socks5-hostname", "--resolve", "--connect-to", "--interface",
        "--dns-servers", "--dns-interface", "--unix-socket", "--abstract-unix-socket", "--cookie-jar",
        "--upload-file", "--range", "--time-cond", "--limit-rate", "--max-redirs", "--max-filesize",
        "--speed-limit", "--speed-time", "--keepalive-time", "--expect100-timeout", "--happy-eyeballs-timeout-ms",
        "--local-port", "--trace", "--trace-ascii", "--stderr", "--dump-header", "--etag-save", "--etag-compare",
        "--quote", "--telnet-option", "--hostpubmd5", "--hostpubsha256", "--pubkey", "--krb", "--delegation",
        "--service-name", "--sasl-authzid", "--login-options", "--mail-from", "--mail-rcpt", "--mail-auth",
        "--ftp-port", "--ftp-account", "--ftp-method", "--ftp-ssl-ccc-mode", "--ftp-alternative-to-user",
        "--rate", "--parallel-max", "--variable", "--expand-url", "--url-query", "--aws-sigv4", "--ech",
        "--ip-tos", "--vlan-priority", "--proto", "--proto-redir", "--proto-default", "--form-string",
        "--random-file", "--egd-file", "--netrc-file", "--create-file-mode", "--alt-svc", "--hsts",
        "--doh-url", "--json-file", "--request-target", "--proxy-service-name", "--proxy-tls13-ciphers",
        "--socks5-gssapi-service", "--tlsuser", "--tlspassword", "--tlsauthtype", "--proxy-tlsuser",
        "--proxy-tlspassword", "--proxy-tlsauthtype", "--continue-at",
    };

    public static ParsedRequest Parse(string curl)
    {
        var req = new ParsedRequest();
        var tokens = Tokenize(curl ?? "");
        int i = 0;
        if (tokens.Count > 0 && IsCurlProgram(tokens[0])) i = 1;

        string? explicitMethod = null;
        bool head = false, get = false, hasBody = false, json = false;
        var data = new List<string>();
        bool endOfOptions = false;

        for (; i < tokens.Count; i++)
        {
            var token = tokens[i];
            string NextToken() => ++i < tokens.Count ? tokens[i] : "";

            if (token == "-") continue; // stdin: nothing to import
            if (endOfOptions || token.Length < 2 || token[0] != '-')
            {
                if (token.Length > 0) req.Url = token; // a bare argument is the URL
                continue;
            }
            if (token == "--") { endOfOptions = true; continue; }

            if (token.StartsWith("--", StringComparison.Ordinal))
            {
                string opt = token;
                string? inline = null;
                int eq = token.IndexOf('=');
                if (eq > 2) { opt = token[..eq]; inline = token[(eq + 1)..]; }
                string Value() => inline ?? NextToken();

                switch (opt)
                {
                    case "--request": explicitMethod = Value().ToUpperInvariant(); break;
                    case "--header": AddHeader(req, Value()); break;
                    case "--user-agent": req.Headers.Add(new("User-Agent", Value())); break;
                    case "--referer": req.Headers.Add(new("Referer", Value())); break;
                    case "--cookie": req.Headers.Add(new("Cookie", Value())); break;
                    case "--data": case "--data-ascii": case "--data-binary": case "--data-raw":
                        data.Add(Value()); hasBody = true; break;
                    case "--data-urlencode": data.Add(UrlEncodeData(Value())); hasBody = true; break;
                    case "--json": data.Add(Value()); hasBody = true; json = true; break;
                    case "--form": AddForm(req, Value(), literal: false); break;
                    case "--form-string": AddForm(req, Value(), literal: true); break;
                    case "--user": SetUser(req, Value()); break;
                    case "--oauth2-bearer": req.BearerToken = Value(); break;
                    case "--url": req.Url = Value(); break;
                    case "--insecure": req.InsecureSkipVerify = true; break;
                    case "--head": head = true; break;
                    case "--get": get = true; break;
                    default:
                        if (inline is null && LongWithValue.Contains(opt)) NextToken();
                        // other long flags (--compressed, --location, --silent, …) carry no value
                        break;
                }
                continue;
            }

            // A cluster of short options: -sSL, -kv, -XPUT, -HAccept:x, -uuser:pw.
            for (int k = 1; k < token.Length; k++)
            {
                char o = token[k];
                if (ShortWithValue.IndexOf(o) >= 0)
                {
                    string value = k + 1 < token.Length ? token[(k + 1)..] : NextToken();
                    switch (o)
                    {
                        case 'X': explicitMethod = value.ToUpperInvariant(); break;
                        case 'H': AddHeader(req, value); break;
                        case 'A': req.Headers.Add(new("User-Agent", value)); break;
                        case 'e': req.Headers.Add(new("Referer", value)); break;
                        case 'b': req.Headers.Add(new("Cookie", value)); break;
                        case 'd': data.Add(value); hasBody = true; break;
                        case 'F': AddForm(req, value, literal: false); break;
                        case 'u': SetUser(req, value); break;
                    }
                    break; // the rest of the cluster was the value
                }
                switch (o)
                {
                    case 'k': req.InsecureSkipVerify = true; break;
                    case 'I': head = true; break;
                    case 'G': get = true; break;
                }
            }
        }

        if (get && data.Count > 0)
        {
            // -G: the data goes on the query string, and the request is a GET.
            string query = string.Join("&", data);
            int hash = req.Url.IndexOf('#');
            string frag = hash >= 0 ? req.Url[hash..] : "";
            string url = hash >= 0 ? req.Url[..hash] : req.Url;
            req.Url = url + (url.Contains('?') ? "&" : "?") + query + frag;
            hasBody = false;
        }
        else if (data.Count > 0)
        {
            req.Body = string.Join(json ? "" : "&", data);
        }

        bool hasForm = req.FormParts.Count > 0;
        req.Method = explicitMethod ?? (head ? "HEAD" : get ? "GET" : hasBody || hasForm ? "POST" : "GET");
        ExtractContentTypeAndAuth(req);
        if (json)
        {
            req.ContentType ??= "application/json";
            if (!req.Headers.Any(h => h.Key.Equals("Accept", StringComparison.OrdinalIgnoreCase)))
                req.Headers.Add(new("Accept", "application/json"));
        }
        else if (hasBody && !get && req.ContentType is null)
        {
            req.ContentType = "application/x-www-form-urlencoded"; // what curl sends for -d
        }
        return req;
    }

    private static bool IsCurlProgram(string t)
    {
        string name = t.Replace('\\', '/');
        name = name[(name.LastIndexOf('/') + 1)..];
        return name.Equals("curl", StringComparison.OrdinalIgnoreCase) || name.Equals("curl.exe", StringComparison.OrdinalIgnoreCase);
    }

    private static void SetUser(ParsedRequest req, string up)
    {
        int c = up.IndexOf(':');
        if (c >= 0) { req.BasicUser = up[..c]; req.BasicPassword = up[(c + 1)..]; }
        else req.BasicUser = up;
    }

    /// <summary>curl's <c>--data-urlencode</c> forms: <c>content</c>, <c>=content</c>,
    /// <c>name=content</c> (content encoded); <c>@file</c> / <c>name@file</c> are kept as written.</summary>
    private static string UrlEncodeData(string v)
    {
        int eq = v.IndexOf('=');
        int at = v.IndexOf('@');
        if (at >= 0 && (eq < 0 || at < eq)) return v; // reads a file: nothing to encode here
        if (eq < 0) return Uri.EscapeDataString(v);
        return v[..eq] + (eq == 0 ? "" : "=") + Uri.EscapeDataString(v[(eq + 1)..]);
    }

    /// <summary><c>-F name=value</c>, <c>-F name=@path</c> (a file), <c>-F name=&lt;path</c> (the
    /// file's text as the value — kept as a file part, the nearest this request model has).</summary>
    private static void AddForm(ParsedRequest req, string raw, bool literal)
    {
        int eq = raw.IndexOf('=');
        if (eq <= 0) return;
        string name = raw[..eq], value = raw[(eq + 1)..];
        if (!literal && value.Length > 1 && value[0] is '@' or '<')
        {
            string path = value[1..];
            int semi = path.IndexOf(';'); // ;type=… ;filename=…
            if (semi >= 0) path = path[..semi];
            req.FormParts.Add(new ParsedFormPart(name, path, IsFile: true));
        }
        else
        {
            req.FormParts.Add(new ParsedFormPart(name, value, IsFile: false));
        }
    }

    private static void AddHeader(ParsedRequest req, string raw)
    {
        int c = raw.IndexOf(':');
        if (c < 0)
        {
            // "-H 'X-Empty;'" sends an empty header; "-H 'X-Drop:'" removes one — both just a name here
            string n = raw.TrimEnd(';').Trim();
            if (n.Length > 0) req.Headers.Add(new(n, ""));
            return;
        }
        req.Headers.Add(new(raw[..c].Trim(), raw[(c + 1)..].Trim()));
    }

    private static void ExtractContentTypeAndAuth(ParsedRequest req)
    {
        for (int i = req.Headers.Count - 1; i >= 0; i--)
        {
            var (name, value) = (req.Headers[i].Key, req.Headers[i].Value);
            if (name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
            {
                req.ContentType ??= value; // the last -H wins, as in curl
                req.Headers.RemoveAt(i);
            }
            else if (name.Equals("Authorization", StringComparison.OrdinalIgnoreCase) &&
                     value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                req.BearerToken ??= value["Bearer ".Length..].Trim();
                req.Headers.RemoveAt(i);
            }
        }
    }

    /// <summary>Split a command line into arguments: bash rules, or cmd.exe's when the text is a
    /// "Copy as cURL (cmd)" command (its quotes are written <c>^"</c>).</summary>
    internal static List<string> Tokenize(string input) =>
        input.Contains("^\"", StringComparison.Ordinal) ? TokenizeCmd(input) : TokenizeBash(input);

    private static List<string> TokenizeBash(string input)
    {
        var tokens = new List<string>();
        var sb = new StringBuilder();
        bool has = false;

        for (int i = 0; i < input.Length; i++)
        {
            char ch = input[i];
            if (ch == '\\' && i + 1 < input.Length)
            {
                char n = input[i + 1];
                if (n == '\n') { i++; continue; }                                         // line continuation
                if (n == '\r' && i + 2 < input.Length && input[i + 2] == '\n') { i += 2; continue; }
                sb.Append(n); i++; has = true;
            }
            else if (ch is ' ' or '\t' or '\r' or '\n')
            {
                if (has) { tokens.Add(sb.ToString()); sb.Clear(); has = false; }
            }
            else if (ch == '\'')
            {
                has = true;
                int end = input.IndexOf('\'', i + 1);
                if (end < 0) end = input.Length;
                sb.Append(input, i + 1, end - i - 1);
                i = end;
            }
            else if (ch == '$' && i + 1 < input.Length && input[i + 1] == '\'')
            {
                has = true;
                i = ReadAnsiC(input, i + 2, sb);
            }
            else if (ch == '"')
            {
                has = true;
                for (i++; i < input.Length && input[i] != '"'; i++)
                {
                    char q = input[i];
                    if (q == '\\' && i + 1 < input.Length)
                    {
                        char n = input[i + 1];
                        if (n is '$' or '`' or '"' or '\\') { sb.Append(n); i++; continue; }
                        if (n == '\n') { i++; continue; }
                    }
                    sb.Append(q);
                }
            }
            else if (ch == '^' && i + 1 < input.Length && input[i + 1] is '\n' or '\r')
            {
                // a cmd.exe-style continuation on an otherwise bash-looking line
                if (has) { tokens.Add(sb.ToString()); sb.Clear(); has = false; }
            }
            else { sb.Append(ch); has = true; }
        }
        if (has) tokens.Add(sb.ToString());
        return tokens;
    }

    /// <summary>Reads a bash <c>$'…'</c> string from <paramref name="i"/> (just past the opening
    /// quote); returns the index of the closing quote.</summary>
    private static int ReadAnsiC(string s, int i, StringBuilder sb)
    {
        var bytes = new List<byte>(); // \xHH and octal escapes are bytes: UTF-8 sequences decode together
        void FlushBytes()
        {
            if (bytes.Count == 0) return;
            sb.Append(Encoding.UTF8.GetString(bytes.ToArray()));
            bytes.Clear();
        }
        for (; i < s.Length; i++)
        {
            char ch = s[i];
            if (ch == '\'') { FlushBytes(); return i; }
            if (ch != '\\' || i + 1 >= s.Length) { FlushBytes(); sb.Append(ch); continue; }
            char e = s[++i];
            switch (e)
            {
                case 'n': FlushBytes(); sb.Append('\n'); break;
                case 't': FlushBytes(); sb.Append('\t'); break;
                case 'r': FlushBytes(); sb.Append('\r'); break;
                case 'a': FlushBytes(); sb.Append('\a'); break;
                case 'b': FlushBytes(); sb.Append('\b'); break;
                case 'f': FlushBytes(); sb.Append('\f'); break;
                case 'v': FlushBytes(); sb.Append('\v'); break;
                case 'e': case 'E': FlushBytes(); sb.Append('\u001b'); break;
                case '\\': FlushBytes(); sb.Append('\\'); break;
                case '\'': FlushBytes(); sb.Append('\''); break;
                case '"': FlushBytes(); sb.Append('"'); break;
                case '?': FlushBytes(); sb.Append('?'); break;
                case 'x':
                {
                    int n = 0, len = 0;
                    while (len < 2 && i + 1 < s.Length && Uri.IsHexDigit(s[i + 1])) { n = n * 16 + Convert.ToInt32(s[++i].ToString(), 16); len++; }
                    if (len == 0) { FlushBytes(); sb.Append("\\x"); } else bytes.Add((byte)n);
                    break;
                }
                case 'u': case 'U':
                {
                    int max = e == 'u' ? 4 : 8, n = 0, len = 0;
                    while (len < max && i + 1 < s.Length && Uri.IsHexDigit(s[i + 1])) { n = n * 16 + Convert.ToInt32(s[++i].ToString(), 16); len++; }
                    FlushBytes();
                    if (len == 0) sb.Append('\\').Append(e);
                    else if (n <= 0x10FFFF && (n < 0xD800 || n > 0xDFFF)) sb.Append(char.ConvertFromUtf32(n));
                    break;
                }
                default:
                    if (e is >= '0' and <= '7')
                    {
                        int n = e - '0', len = 1;
                        while (len < 3 && i + 1 < s.Length && s[i + 1] is >= '0' and <= '7') { n = n * 8 + (s[++i] - '0'); len++; }
                        bytes.Add((byte)(n & 0xFF));
                    }
                    else { FlushBytes(); sb.Append('\\').Append(e); }
                    break;
            }
        }
        FlushBytes();
        return s.Length;
    }

    /// <summary>cmd.exe: <c>^</c> escapes the next character (a <c>^</c> at a line end continues the
    /// line); then curl.exe splits its command line the Microsoft C runtime's way — <c>"</c> quotes,
    /// <c>\"</c> is a literal quote, other backslashes are literal.</summary>
    private static List<string> TokenizeCmd(string input)
    {
        var unescaped = new StringBuilder(input.Length);
        for (int i = 0; i < input.Length; i++)
        {
            char ch = input[i];
            if (ch != '^' || i + 1 >= input.Length) { unescaped.Append(ch); continue; }
            char n = input[++i];
            if (n == '\r' && i + 1 < input.Length && input[i + 1] == '\n') { i++; unescaped.Append(' '); }
            else if (n == '\n') unescaped.Append(' ');
            else unescaped.Append(n);
        }
        string s = unescaped.ToString();

        var tokens = new List<string>();
        var sb = new StringBuilder();
        bool has = false, quoted = false;
        for (int i = 0; i < s.Length; i++)
        {
            char ch = s[i];
            if (ch == '\\')
            {
                int run = 0;
                while (i < s.Length && s[i] == '\\') { run++; i++; }
                if (i < s.Length && s[i] == '"')
                {
                    sb.Append('\\', run / 2);
                    if (run % 2 == 1) sb.Append('"'); else { quoted = !quoted; }
                }
                else { sb.Append('\\', run); i--; }
                has = true;
            }
            else if (ch == '"') { quoted = !quoted; has = true; }
            else if (!quoted && ch is ' ' or '\t' or '\r' or '\n')
            {
                if (has) { tokens.Add(sb.ToString()); sb.Clear(); has = false; }
            }
            else { sb.Append(ch); has = true; }
        }
        if (has) tokens.Add(sb.ToString());
        return tokens;
    }
}
