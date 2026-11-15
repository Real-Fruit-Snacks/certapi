using ApiTester.Core;

namespace ApiTester.Cli.Commands;

/// <summary>`certapi call`: one operation of an OpenAPI document, by its operationId, with its
/// parameters by name, sent like `send` and checked against what the document says comes back.</summary>
public static class CallCommand
{
    public const string Help = """
        Usage: certapi call <openapi.json> <operationId | "METHOD /path"> [--param name=value …] [options]

        Calls one operation from an OpenAPI 3 or Swagger 2 document (JSON) by its operationId. Each
        --param goes where the document puts it — the path, the query, a header or a cookie — so
        you never assemble the URL by hand, and a required one that is missing stops the call. The
        response is then checked against the document: its status must be one the operation lists,
        and its body must match the JSON schema documented for that status.

          --param <name=value>    A parameter, by its name in the document. Repeatable
          --server <url>          The base URL, instead of the document's first server
          --no-validate           Send only; do not check the response against the document
          --list                  List the document's operations and exit

        Everything `send` takes works here too: -d/--data or --data-file for the body, --cert and the
        other certificate options, --bearer and the other auth options, -H, --json, -o, --fail,
        -w, --assert, the transport options. See `certapi help send`.

        Global: --debug (verbose diagnostics) and --log-file <path> work here too.

        Examples:
          certapi call petstore.json getPetById --param petId=7 --cert "CN=svc"
          certapi call petstore.json addPet -d @pet.json --server https://staging.example.com/v1
          certapi call petstore.json "GET /pets" --param limit=5 --json

        Exit 0 when the call matches the document, 1 when it does not or the request fails.
        """;

    public static int Run(Args args, TextWriter stdout, TextWriter stderr, Stream bodyOut, CliServices services)
    {
        var paramSpecs = args.Values("--param");
        string? server = args.Value("--server");
        bool noValidate = args.Flag("--no-validate");
        bool list = args.Flag("--list");

        // The two positionals come first; everything after them belongs to send.
        var tokens = args.Original.ToList();
        int spec = tokens.FindIndex(t => !t.StartsWith('-'));
        if (spec < 0) throw new CliUsageException(Help);
        string specPath = tokens[spec];
        if (!File.Exists(specPath)) throw new CliDataException($"OpenAPI document not found: {specPath}");
        string json = File.ReadAllText(specPath);

        if (list)
        {
            foreach (var line in OpenApiImporter.ListOperations(json)) stdout.WriteLine(line);
            return ExitCodes.Ok;
        }
        int opIndex = tokens.FindIndex(spec + 1, t => !t.StartsWith('-'));
        if (opIndex < 0) throw new CliUsageException("call needs an operationId (or \"METHOD /path\") after the document. --list shows them.");
        string wanted = tokens[opIndex];

        OpenApiOperation? op;
        try { op = OpenApiImporter.FindOperation(json, wanted); }
        catch (Exception ex) when (ex is FormatException or System.Text.Json.JsonException) { throw new CliDataException($"Could not read '{specPath}': {ex.Message}"); }
        if (op is null)
            throw new CliUsageException($"'{wanted}' is not an operation in {specPath}. It has:\n  " +
                string.Join("\n  ", OpenApiImporter.ListOperations(json).Take(60)));

        // Each --param to where the document puts it.
        var given = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in paramSpecs)
        {
            int eq = raw.IndexOf('=');
            if (eq <= 0) throw new CliUsageException($"--param expects name=value, got '{raw}'.");
            given[raw[..eq]] = raw[(eq + 1)..];
        }
        foreach (var name in given.Keys)
            if (!op.Parameters.Any(p => p.Name == name))
                throw new CliUsageException($"'{name}' is not a parameter of {wanted}. It takes: " +
                    (op.Parameters.Count == 0 ? "none" : string.Join(", ", op.Parameters.Select(p => $"{p.Name} ({p.In}{(p.Required ? ", required" : "")})"))) + ".");
        var missing = op.Parameters.Where(p => p.Required && !given.ContainsKey(p.Name)).Select(p => $"{p.Name} ({p.In})").ToList();
        if (missing.Count > 0) throw new CliUsageException($"{wanted} needs --param for: {string.Join(", ", missing)}.");

        string path = op.PathTemplate;
        var forward = new List<string>();
        foreach (var p in op.Parameters.Where(p => given.ContainsKey(p.Name)))
        {
            string value = given[p.Name];
            switch (p.In)
            {
                case "path":
                    path = path.Replace("{" + p.Name + "}", Uri.EscapeDataString(value), StringComparison.Ordinal);
                    break;
                case "query":
                    forward.Add("--query"); forward.Add($"{p.Name}={value}");
                    break;
                case "header":
                    forward.Add("-H"); forward.Add($"{p.Name}: {value}");
                    break;
                case "cookie":
                    forward.Add("--cookie"); forward.Add($"{p.Name}={Uri.EscapeDataString(value)}");
                    break;
            }
        }

        string? baseUrl = server ?? op.BaseUrl;
        if (string.IsNullOrWhiteSpace(baseUrl) || !Uri.TryCreate(baseUrl, UriKind.Absolute, out _))
            throw new CliUsageException($"{specPath} names no absolute server URL{(baseUrl is null ? "" : $" ('{baseUrl}')")}: give --server <url>.");
        string url = baseUrl.TrimEnd('/') + "/" + path.TrimStart('/');

        // What remains of the command line goes to send, with the method and the body's type
        // the document gives.
        var rest = tokens.Where((t, i) => i != spec && i != opIndex).ToList();
        var sendArgs = new List<string> { url };
        if (!HasAny(rest, "-X", "--method")) { sendArgs.Add("-X"); sendArgs.Add(op.Method); }
        bool hasBody = HasAny(rest, "-d", "--data", "--data-file", "-F", "--form", "--data-urlencode", "--graphql");
        if (op.RequestBodyRequired && !hasBody)
            throw new CliUsageException($"{wanted} needs a request body: give -d/--data or --data-file.");
        if (hasBody && op.RequestContentType is { } type && !HasAny(rest, "--content-type") && !type.Contains("multipart") && !type.Contains("form-urlencoded"))
        { sendArgs.Add("--content-type"); sendArgs.Add(type); }
        sendArgs.AddRange(forward);
        sendArgs.AddRange(StripCallOptions(rest));

        services.Log.Debug($"call {wanted} → {op.Method} {url}");
        Func<ApiResponse, IEnumerable<AssertionRule>>? checks = noValidate ? null : response => Checks(op, response);
        return SendCommand.Run(new Args(sendArgs, "send"), stdout, stderr, bodyOut, services, checks);
    }

    /// <summary>The status must be documented, and the body must match its schema.</summary>
    internal static IEnumerable<AssertionRule> Checks(OpenApiOperation op, ApiResponse response)
    {
        if (response.StatusCode is not { } status) yield break;
        var documented = op.ResponseSchemas.Keys.Where(k => k != "default").ToList();
        var (key, schema) = op.SchemaForStatus(status);
        if (key is null && documented.Count > 0)
            yield return new AssertionRule
            {
                Target = AssertTarget.Status, Op = AssertOp.Matches,
                Value = "^(" + string.Join("|", documented.Select(d => d.Replace("X", "\\d").Replace("x", "\\d"))) + ")$"
            };
        if (schema is not null)
            yield return new AssertionRule { Target = AssertTarget.Body, Path = "$", Op = AssertOp.MatchesSchema, Value = schema };
    }

    private static bool HasAny(List<string> tokens, params string[] names) =>
        tokens.Any(t => names.Any(n => t == n || t.StartsWith(n + "=", StringComparison.Ordinal)));

    /// <summary>call's own options, which send would not know.</summary>
    private static IEnumerable<string> StripCallOptions(List<string> tokens)
    {
        var withValue = new HashSet<string>(StringComparer.Ordinal) { "--param", "--server" };
        var alone = new HashSet<string>(StringComparer.Ordinal) { "--no-validate", "--list" };
        for (int i = 0; i < tokens.Count; i++)
        {
            string t = tokens[i];
            string name = t.Contains('=') && t.StartsWith("--", StringComparison.Ordinal) ? t[..t.IndexOf('=')] : t;
            if (alone.Contains(name)) continue;
            if (withValue.Contains(name)) { if (name == t) i++; continue; }
            yield return t;
        }
    }
}
