using System.Text;
using ApiTester.Core;

namespace ApiTester.Cli.Commands;

/// <summary>`certapi curl`: a curl command line, run with curl's meaning. The arguments are read
/// the way curl reads them and handed to <see cref="SendCommand"/>, so everything send does —
/// the Windows certificate store, pinning, timing, the debug log — applies to a pasted command.
/// Where curl and send differ, curl's meaning wins here: redirects are not followed without -L,
/// -d is a form body, several -d values are joined with &amp;, --json takes data, and --cert
/// names a file (or, as curl's Windows build reads it, a store certificate).</summary>
public static class CurlCommand
{
    public const string Help = """
        Usage: certapi curl <curl arguments…>
               certapi curl "curl -sS -H 'Accept: application/json' https://api.example.com/v1/me"

        Runs a curl command as curl would read it, through certapi's send: paste the command as it
        is (with or without the leading curl), quoted as one argument or as separate arguments.
        bash quoting, line continuations (\ and cmd's ^), and "Copy as cURL" from a browser all
        work. Options typed after the quoted command are added to it. A backslash in a Windows path
        is kept (-E C:\certs\me.pfx); one before a space, a quote or $ escapes it, as in bash.

        Where curl and certapi send differ, curl's meaning is kept:
          redirects               followed only with -L, as in curl (send follows them by default)
          -d, --data, --data-urlencode
                                  a form body (application/x-www-form-urlencoded) unless a
                                  Content-Type header says otherwise; several are joined with &
          -d @file                the file's text with line breaks removed, as curl reads it;
                                  --data-binary @file sends it byte for byte
          --json <data>           the data as JSON, with Content-Type and Accept application/json
          --cert / -E <file[:password]>
                                  a certificate file (.pfx/.p12/.pem; --key for a separate key,
                                  --pass for its password). CurrentUser\MY\<thumbprint> or
                                  LocalMachine\MY\<thumbprint> picks one from the Windows store,
                                  as curl's Windows build does, and anything that is not a file
                                  is looked up in the store by thumbprint or subject
          -c, --cookie-jar <file> cookies are saved to the file (and, as with send, read from it)
          -v, --verbose           certapi's diagnostics on stderr (--debug)

        Everything else is send's own option of the same name (see certapi send --help), and
        certapi's options can be added to the command: --timing, --pretty, --assert, --debug,
        --cert-issuer, --store. A curl option certapi does not have is reported, not ignored.

        certapi's certificate options work here as on send:
        """ + "\n" + CliCert.HelpLines + "\n" + """
        Global: --debug (verbose diagnostics) and --log-file <path> work here too.

        Examples:
          certapi curl "curl -fsSL https://api.example.com/health"
          certapi curl curl -X POST https://api.example.com/login -d user=me -d pass=secret
          certapi curl -sS -E "CurrentUser\MY\3f1c…" https://mtls.example.com/whoami --timing

        Exit codes as send's: 0 on a response (1 with -f and a status of 400 or more), 1 when no
        response came back, 2 for a usage error.
        """;

    public static int Run(Args args, TextWriter stdout, TextWriter stderr, Stream bodyOut, CliServices services)
    {
        var tokens = args.Original.ToList();
        // A first argument holding a whole command line: split it the way a shell would. Options
        // typed after it (certapi curl "curl …" --timing) stay as they are.
        if (tokens.Count > 0 && tokens[0].Any(char.IsWhiteSpace))
            tokens = [.. CurlParser.Tokenize(tokens[0]), .. tokens.Skip(1)];
        if (tokens.Count > 0 && IsCurlProgram(tokens[0])) tokens.RemoveAt(0);
        if (tokens.Count == 0) throw new CliUsageException(Help);

        var sendArgs = Translate(tokens);
        // curl's -v is certapi's --debug, which is a global option: it turns the log on here
        // rather than reaching send as an option send does not read.
        bool verbose = sendArgs.Remove("--debug");
        var log = services.Log;
        CliLog? own = null;
        if (verbose && !log.DebugEnabled) services.Log = own = CliLog.Create(true, null, stderr);
        try
        {
            services.Log.Debug("curl as send: " + string.Join(" ", sendArgs.Select(Quote)));
            return SendCommand.Run(new Args(sendArgs), stdout, stderr, bodyOut, services);
        }
        finally
        {
            if (own is not null) { services.Log = log; own.Dispose(); }
        }
    }

    private static bool IsCurlProgram(string token)
    {
        string name = token.Replace('\\', '/');
        name = name[(name.LastIndexOf('/') + 1)..];
        return name.Equals("curl", StringComparison.OrdinalIgnoreCase) || name.Equals("curl.exe", StringComparison.OrdinalIgnoreCase);
    }

    private enum DataKind { Text, Binary, Raw, Json }

    /// <summary>curl's arguments as send's. Exposed for the tests, which check the translation
    /// itself as well as what it sends.</summary>
    internal static List<string> Translate(IReadOnlyList<string> curlTokens)
    {
        // Bundles (-sSL, -XPOST) first, by the same rule send's own reader uses.
        var expanded = new Args(curlTokens).ExpandedTokens;

        var output = new List<string>();
        var data = new List<(DataKind Kind, string Value)>();
        bool follow = false, get = false, json = false, contentTypeGiven = false, acceptGiven = false, verbose = false, urlencode = false;
        bool ended = false;
        for (int i = 0; i < expanded.Count; i++)
        {
            string t = expanded[i];
            string Next(string option) => i + 1 < expanded.Count ? expanded[++i]
                : throw new CliUsageException($"Option {option} needs a value.");
            if (ended || t == "-" || t.Length < 2 || t[0] != '-') { output.Add(t); continue; }
            if (t == "--") { ended = true; output.Add(t); continue; }

            string name = t, inline = "";
            bool hasInline = false;
            int eq = t.IndexOf('=');
            if (t.StartsWith("--", StringComparison.Ordinal) && eq > 2) { name = t[..eq]; inline = t[(eq + 1)..]; hasInline = true; }
            string Value() => hasInline ? inline : Next(name);
            switch (name.StartsWith("--", StringComparison.Ordinal) ? name.ToLowerInvariant() : name)
            {
                case "-L": case "--location": follow = true; break;
                case "-G": case "--get": get = true; output.Add(t); break;
                case "--content-type": contentTypeGiven = true; output.Add("--content-type=" + Value()); break;
                case "-d": case "--data": case "--data-ascii": data.Add((DataKind.Text, Value())); break;
                case "--data-binary": data.Add((DataKind.Binary, Value())); break;
                case "--data-raw": data.Add((DataKind.Raw, Value())); break;
                case "--json": data.Add((DataKind.Json, Value())); json = true; break;
                case "--data-urlencode": urlencode = true; output.Add("--data-urlencode=" + Value()); break;
                case "-H": case "--header":
                {
                    string header = Value();
                    if (header.StartsWith("content-type:", StringComparison.OrdinalIgnoreCase)) contentTypeGiven = true;
                    if (header.StartsWith("accept:", StringComparison.OrdinalIgnoreCase)) acceptGiven = true;
                    output.Add("-H"); output.Add(header);
                    break;
                }
                case "-E": case "--cert":
                {
                    var (query, file, password, store) = CliCert.FromCurlCert(Value());
                    if (file is not null && !File.Exists(file) && password is null && LooksLikeStoreQuery(file))
                    {
                        // Not a file: a thumbprint or a subject, looked up in the Windows store.
                        output.Add("--cert=" + file);
                    }
                    else if (file is not null)
                    {
                        output.Add("--cert-file=" + file);
                        if (password is not null) output.Add("--cert-password=" + password);
                    }
                    else
                    {
                        output.Add("--cert=" + query);
                        if (store is not null) output.Add("--store=" + store);
                    }
                    break;
                }
                case "--pass": output.Add("--cert-password=" + Value()); break;
                case "--cert-type": case "--key-type": Value(); break;   // certapi reads the format from the file
                case "-c": case "--cookie-jar": output.Add("--cookie-jar=" + Value()); break;
                case "-v": case "--verbose": verbose = true; break;
                default:
                    output.Add(t);
                    break;
            }
        }

        if (!follow) output.Add("--no-redirect");
        if (verbose) output.Add("--debug");

        if (data.Count > 0)
        {
            if (data.Count == 1 && data[0].Kind == DataKind.Binary && data[0].Value is ['@', .. var binaryPath] && binaryPath != "-")
            {
                output.Add("--data-file=" + binaryPath);
            }
            else
            {
                output.Add("--data-raw=" + string.Join(json ? "" : "&", data.Select(Resolve)));
            }
            if (!contentTypeGiven && !get)
            {
                output.Add("--content-type=" + (json ? "application/json" : "application/x-www-form-urlencoded"));
            }
            if (json && !acceptGiven && !get) { output.Add("-H"); output.Add("Accept: application/json"); }
        }
        else if (urlencode && !contentTypeGiven && !get)
        {
            output.Add("--content-type=application/x-www-form-urlencoded");
        }
        return output;
    }

    /// <summary>One -d style value as curl sends it: @file reads the file (-d and --json with
    /// line breaks removed, --data-binary as it is), --data-raw is always literal.</summary>
    private static string Resolve((DataKind Kind, string Value) part)
    {
        if (part.Kind == DataKind.Raw || part.Value.Length < 2 || part.Value[0] != '@') return part.Value;
        string path = part.Value[1..];
        string text;
        try { text = path == "-" ? Console.In.ReadToEnd() : File.ReadAllText(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new CliDataException($"Could not read {part.Value}: {ex.Message}");
        }
        return part.Kind == DataKind.Binary ? text : text.Replace("\r", "").Replace("\n", "");
    }

    /// <summary>What -E/--cert means when it is not a file: a 40-character thumbprint, or a
    /// subject (it has an = or is a plain name with no path separators).</summary>
    private static bool LooksLikeStoreQuery(string value) =>
        value.IndexOfAny(['\\', '/']) < 0 && !value.EndsWith(".pem", StringComparison.OrdinalIgnoreCase) &&
        !value.EndsWith(".pfx", StringComparison.OrdinalIgnoreCase) && !value.EndsWith(".p12", StringComparison.OrdinalIgnoreCase) &&
        !value.EndsWith(".crt", StringComparison.OrdinalIgnoreCase);

    private static string Quote(string s) => s.Length == 0 || s.Any(char.IsWhiteSpace) || s.Contains('"') ? "\"" + s.Replace("\"", "\\\"") + "\"" : s;
}
