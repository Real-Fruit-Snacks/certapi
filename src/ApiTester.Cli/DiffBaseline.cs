using System.Text;
using System.Text.Json;
using ApiTester.Core;

namespace ApiTester.Cli;

/// <summary>Turns a --diff argument into the snapshot to compare against. Kept out of SendCommand
/// because resolving "a .har, a .json, or the word known-good" is the part with real rules, and a
/// wrong baseline silently makes every later diff a lie.</summary>
public static class DiffBaseline
{
    public const string KnownGoodToken = "known-good";

    /// <summary>The baseline for a send. Throws <see cref="CliDataException"/> (exit 3) when the
    /// argument names something missing, unreadable, or not matching the request. Nothing else ever
    /// escapes: an IOException or a JsonException reaching the top would print a stack trace for
    /// what is only ever a bad file the user named.</summary>
    public static ResponseSnapshot Resolve(string argument, string method, string url, AppState state,
                                           IReadOnlyDictionary<string, string>? vars = null)
    {
        if (string.Equals(argument, KnownGoodToken, StringComparison.OrdinalIgnoreCase))
            return FromKnownGood(method, url, state, vars);
        if (argument.EndsWith(".har", StringComparison.OrdinalIgnoreCase))
            return FromHar(argument, method, url);
        if (argument.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            return FromJson(argument);
        throw new CliDataException(
            $"--diff baseline must be a .har file, a .json response file, or 'known-good', got '{argument}'.");
    }

    /// <summary>The recorded response for a HAR entry, for `run --diff-har`.</summary>
    public static ResponseSnapshot FromEntry(HarEntry entry) => ResponseSnapshot.From(entry);

    /// <summary>The stored known-good response of the saved request this send is repeating. Matching
    /// on method + URL rather than a name keeps `--diff known-good` usable from a plain
    /// `certapi send <url>` line, which is the only form that has no request to name.</summary>
    private static ResponseSnapshot FromKnownGood(string method, string url, AppState state,
                                                  IReadOnlyDictionary<string, string>? vars)
    {
        // A saved URL is usually a template ({{base}}/health); the send's URL is resolved, so the
        // saved one is resolved with the same variables before they are compared.
        Func<string, string>? resolve = vars is null ? null : s => VariableResolver.Resolve(s, vars).Result;
        var node = FindSaved(state.Collections, method, url, resolve)
            ?? throw new CliDataException(
                $"--diff known-good found no saved request matching {method} {url}. " +
                "Save the request and run it once so a known-good response is recorded.");

        return node.KnownGood
            ?? throw new CliDataException(
                $"--diff known-good: the saved request '{node.Name}' has no recorded known-good response yet " +
                "(run it once with 'certapi run' so one is stored).");
    }

    /// <summary>Depth-first, first match wins. Two saved requests can legitimately point at the same
    /// endpoint; picking the first is at least stable and repeatable, where refusing would make the
    /// flag unusable in exactly the workspaces that use it most.</summary>
    private static CollectionNode? FindSaved(IEnumerable<CollectionNode> nodes, string method, string url,
                                            Func<string, string>? resolve)
    {
        foreach (var node in nodes)
        {
            if (!node.IsFolder && node.Request is { } request &&
                string.Equals(request.Method, method, StringComparison.OrdinalIgnoreCase) &&
                (string.Equals(request.EffectiveUrl(), url, StringComparison.OrdinalIgnoreCase) ||
                 (resolve is not null && string.Equals(request.EffectiveUrl(resolve), url, StringComparison.OrdinalIgnoreCase))))
                return node;
            if (FindSaved(node.Children, method, url, resolve) is { } found) return found;
        }
        return null;
    }

    private static ResponseSnapshot FromHar(string path, string method, string url)
    {
        if (!File.Exists(path)) throw new CliDataException($"--diff baseline file not found: {path}");

        Har har;
        try { har = HarReader.Parse(ReadText(path)); }
        catch (HarFormatException ex) { throw new CliDataException(ex.Message); }

        var entries = har.Log.Entries;
        if (entries.Count == 0) throw new CliDataException($"--diff baseline '{path}' has no entries.");

        var match = entries.FirstOrDefault(e =>
            string.Equals(e.Request.Method, method, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(e.Request.Url, url, StringComparison.OrdinalIgnoreCase));
        // A one-request capture is the overwhelmingly common baseline, and refusing it because the
        // recorded URL differs by a trailing slash would be pedantic about the wrong thing.
        if (match is null && entries.Count == 1) match = entries[0];
        if (match is null)
            throw new CliDataException(
                $"--diff baseline '{path}' has no entry for {method} {url} " +
                $"(it has {entries.Count} entries; none match).");

        return ResponseSnapshot.From(match);
    }

    /// <summary>Two shapes, tried in order: the envelope `certapi send --json` writes, then a bare
    /// serialized snapshot. The envelope comes first because `certapi send x --json > baseline.json`
    /// followed by `certapi send x --diff baseline.json` is the obvious workflow, and a workflow the
    /// tool's own output cannot feed would be a trap.</summary>
    private static ResponseSnapshot FromJson(string path)
    {
        if (!File.Exists(path)) throw new CliDataException($"--diff baseline file not found: {path}");
        string text = ReadText(path);

        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("status", out _)) return FromEnvelope(root);
                if (root.TryGetProperty("StatusCode", out _) &&
                    JsonSerializer.Deserialize<ResponseSnapshot>(text) is { } snapshot)
                    return snapshot;
            }
        }
        catch (JsonException) { /* falls through to the one message either failure deserves */ }

        throw new CliDataException(
            $"--diff baseline '{path}' is not a response this tool can read " +
            "(expected a .har, a certapi --json envelope, or a saved response snapshot).");
    }

    /// <summary>Read the comparable facts out of a `certapi send --json` envelope. Every field is
    /// optional on the way in: an envelope written by an older version, or trimmed by a user, should
    /// still diff on what it does carry rather than fail on what it doesn't.</summary>
    private static ResponseSnapshot FromEnvelope(JsonElement root)
    {
        int status = root.TryGetProperty("status", out var statusElement) &&
                     statusElement.ValueKind == JsonValueKind.Number &&
                     statusElement.TryGetInt32(out var parsed)
            ? parsed
            : 0;

        string? contentType = root.TryGetProperty("contentType", out var typeElement) &&
                              typeElement.ValueKind == JsonValueKind.String
            ? typeElement.GetString()
            : null;

        // The envelope groups repeated headers into an array under one name; flatten it back to one
        // pair per value so a header sent twice compares as the two values it really was.
        var headers = new List<KeyValuePair<string, string>>();
        if (root.TryGetProperty("headers", out var headersElement) &&
            headersElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in headersElement.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String)
                    headers.Add(new(property.Name, property.Value.GetString() ?? ""));
                else if (property.Value.ValueKind == JsonValueKind.Array)
                    foreach (var item in property.Value.EnumerateArray())
                        if (item.ValueKind == JsonValueKind.String)
                            headers.Add(new(property.Name, item.GetString() ?? ""));
            }
        }

        byte[] body = Array.Empty<byte>();
        if (root.TryGetProperty("bodyBase64", out var base64) && base64.ValueKind == JsonValueKind.String)
        {
            // A mangled base64 body is not worth failing the whole command over: an empty baseline
            // body still diffs, and says so loudly.
            try { body = Convert.FromBase64String(base64.GetString() ?? ""); }
            catch (FormatException) { body = Array.Empty<byte>(); }
        }
        else if (root.TryGetProperty("body", out var bodyElement) && bodyElement.ValueKind == JsonValueKind.String)
        {
            body = Encoding.UTF8.GetBytes(bodyElement.GetString() ?? "");
        }

        return new ResponseSnapshot(status, headers, body, contentType);
    }

    private static string ReadText(string path)
    {
        try { return File.ReadAllText(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { throw new CliDataException($"--diff baseline '{path}' could not be read: {ex.Message}"); }
    }
}
