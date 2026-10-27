using System.Text;

namespace ApiTester.Core;

/// <summary>One file the export would write: a path relative to the output folder, and its
/// content. Nothing is written by the builder — see <see cref="MarkdownExport"/>.</summary>
public sealed record MarkdownFile(string RelativePath, string Content);

public sealed class MarkdownExportOptions
{
    /// <summary>Subfolder inside the vault that the generated tree lives in. Its own island, so a
    /// re-export cannot overwrite notes the user wrote by hand elsewhere.</summary>
    public string Into { get; init; } = "certapi";

    /// <summary>Write an index note listing every request. Off by default: in a vault the graph
    /// view and search already do this, and an index is one more file to keep current.</summary>
    public bool Index { get; init; }

    /// <summary>Keep credential values instead of redacting them. Off by default — see the class
    /// note on <see cref="MarkdownExport"/>, which explains why this default is not negotiable.</summary>
    public bool IncludeSecrets { get; init; }
}

/// <summary>Turns a saved workspace into a folder of markdown notes: a browsable, linked reference
/// for the APIs someone actually calls.
///
/// <para><b>Why markdown rather than an Obsidian integration.</b> An Obsidian vault *is* a folder
/// of markdown files — no API, no plugin, no service. So writing good markdown into a folder is
/// the whole integration, and the same output serves Logseq, Foam, a git-backed docs repository or
/// a plain wiki. Obsidian's conventions are honoured because they cost nothing and make the result
/// native rather than generic: YAML frontmatter its properties view reads, and
/// <c>[[wikilinks]]</c> that turn the export into a graph instead of a pile of files.</para>
///
/// <para><b>Why secrets are redacted by default, firmly.</b> Vaults sync — Obsidian Sync, iCloud,
/// OneDrive, git. A note written into one is more likely to leave the machine than any other
/// artifact this product writes. So every credential is redacted unless
/// <see cref="MarkdownExportOptions.IncludeSecrets"/> is set, and the tests prove it per output
/// path rather than trusting the rule.</para>
///
/// <para><b>Pure.</b> <see cref="Build"/> returns the files it would write. Every layout, escaping
/// and redaction decision is therefore testable as data, and the command does nothing but write
/// what it is handed.</para></summary>
public static class MarkdownExport
{
    private const string Redacted = MarkdownSecrets.Redacted;

    public static IReadOnlyList<MarkdownFile> Build(AppState state, MarkdownExportOptions options)
    {
        var files = new List<MarkdownFile>();
        string root = Sanitize(options.Into, fallback: "certapi");

        // Requests are collected first: the index and the chain notes both need to know where each
        // one landed, and a link to a note that was never written is a broken link.
        var requests = new List<ExportedRequest>();
        foreach (var node in state.Collections) Walk(node, new List<string>(), requests);

        // A name is unique only within its folder, and even there only if the user kept it so. The
        // note title is what wikilinks resolve on, so a duplicate would silently point at the wrong
        // note — disambiguate rather than overwrite.
        DisambiguateTitles(requests);

        foreach (var request in requests)
            files.Add(new MarkdownFile(
                Combine(root, Combine(request.Folders), request.FileName),
                RenderRequest(request, state, options)));

        foreach (var environment in state.Environments)
            files.Add(new MarkdownFile(
                Combine(root, "environments", NoteFile(environment.Name, "environment")),
                RenderEnvironment(environment, options)));

        foreach (var chain in state.Chains)
            files.Add(new MarkdownFile(
                Combine(root, "chains", NoteFile(chain.Name, "chain")),
                RenderChain(chain, requests, options)));

        if (options.Index)
            files.Add(new MarkdownFile(Combine(root, "index.md"), RenderIndex(requests, state, options.IncludeSecrets)));

        return files;
    }

    // ------------------------------------------------------------------ walking

    private sealed class ExportedRequest
    {
        public required CollectionNode Node { get; init; }
        public required RequestModel Request { get; init; }
        public required List<string> Folders { get; init; }
        public string Title { get; set; } = "";
        public string FileName => NoteFile(Title, "request");
    }

    private static void Walk(CollectionNode node, List<string> folders, List<ExportedRequest> into)
    {
        if (node.IsFolder)
        {
            var deeper = new List<string>(folders) { Sanitize(node.Name, "folder") };
            foreach (var child in node.Children) Walk(child, deeper, into);
            return;
        }
        if (node.Request is null) return;      // a leaf with no request is not exportable
        into.Add(new ExportedRequest
        {
            Node = node,
            Request = node.Request,
            Folders = new List<string>(folders),
            Title = string.IsNullOrWhiteSpace(node.Name) ? "Untitled request" : node.Name.Trim(),
        });
    }

    /// <summary>Two requests may legitimately share a name in different folders — and a careless
    /// workspace may share one in the same folder. Wikilinks resolve on the title, so identical
    /// titles would collide; the second and later get their folder appended, and failing that a
    /// number.</summary>
    private static void DisambiguateTitles(List<ExportedRequest> requests)
    {
        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var request in requests)
        {
            string title = request.Title;
            if (!seen.ContainsKey(title)) { seen[title] = 1; continue; }

            string candidate = request.Folders.Count > 0
                ? $"{title} ({request.Folders[^1]})"
                : title;
            while (seen.ContainsKey(candidate))
                candidate = $"{title} ({++seen[title]})";

            seen[candidate] = 1;
            request.Title = candidate;
        }
    }

    // ------------------------------------------------------------------ notes

    private static string RenderRequest(ExportedRequest exported, AppState state, MarkdownExportOptions options)
    {
        var request = exported.Request;
        var node = exported.Node;
        // A credential in a query string looks like part of the address rather than like a
        // secret, so it survives the review a header would not — and this note is bound for a
        // folder that syncs. Same rule as the investigation notes, from the same place.
        string url = MarkdownSecrets.RedactUrl(request.ExportUrl(), options.IncludeSecrets);
        string host = Uri.TryCreate(url, UriKind.Absolute, out var parsed) ? parsed.Host : "";

        var front = new List<(string Key, string Value)>
        {
            ("method", request.Method),
        };
        if (host.Length > 0) front.Add(("host", host));
        if (url.Length > 0) front.Add(("url", Quote(url)));
        if (!string.IsNullOrWhiteSpace(request.AuthType) && request.AuthType != "Auto")
            front.Add(("auth", request.AuthType));
        if (node.LastStatusCode is { } status) front.Add(("lastStatus", status.ToString()));
        if (node.LastCheckedUtc is { } checkedAt)
            front.Add(("lastChecked", checkedAt.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")));

        var tags = new List<string> { "certapi/request" };
        foreach (var folder in exported.Folders) tags.Add($"certapi/collection/{Tag(folder)}");

        var sb = new StringBuilder();
        WriteFrontMatter(sb, tags, front);

        sb.AppendLine($"# {Escape(exported.Title)}").AppendLine();
        if (url.Length > 0) sb.AppendLine($"`{request.Method} {url}`").AppendLine();

        var links = new List<string>();
        if (exported.Folders.Count > 0) links.Add($"Collection: [[{Link(exported.Folders[^1])}]]");
        var usedBy = state.Chains
            .Where(c => c.Steps.Any(s => s.RequestId == node.Id))
            .Select(c => $"[[{Link(c.Name)}]]").ToList();
        if (usedBy.Count > 0) links.Add("Used by: " + string.Join(", ", usedBy));
        if (links.Count > 0) sb.AppendLine(string.Join(" · ", links)).AppendLine();

        var headers = request.Headers.Where(h => h.Enabled && !string.IsNullOrWhiteSpace(h.Name)).ToList();
        if (headers.Count > 0)
        {
            sb.AppendLine("## Headers").AppendLine();
            sb.AppendLine("| Name | Value |").AppendLine("|---|---|");
            foreach (var header in headers)
                sb.AppendLine($"| {Cell(header.Name)} | {Cell(HeaderValue(header.Name, header.Value, options))} |");
            sb.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(request.AuthUser) || !string.IsNullOrWhiteSpace(request.AuthSecret))
        {
            sb.AppendLine("## Auth").AppendLine();
            sb.AppendLine($"- Type: {Escape(request.AuthType)}");
            if (!string.IsNullOrWhiteSpace(request.AuthUser)) sb.AppendLine($"- User: {Escape(request.AuthUser!)}");
            if (!string.IsNullOrWhiteSpace(request.AuthSecret))
                sb.AppendLine("- Secret: " + (options.IncludeSecrets ? Escape(request.AuthSecret!) : Redacted));
            sb.AppendLine();
        }

        if (request.IsMultipart)
        {
            var parts = request.FormParts.Where(p => p.Enabled && !string.IsNullOrWhiteSpace(p.Name)).ToList();
            if (parts.Count > 0)
            {
                sb.AppendLine("## Body (multipart/form-data)").AppendLine();
                foreach (var part in parts)
                    sb.AppendLine($"- `{Escape(part.Name)}` — {(part.IsFile ? "file" : "text")}");
                sb.AppendLine();
            }
        }
        else if (!string.IsNullOrWhiteSpace(request.Body))
        {
            sb.AppendLine("## Body").AppendLine();
            sb.AppendLine(Fence(MarkdownSecrets.RedactBody(request.Body!, request.ContentType, options.IncludeSecrets), request.ContentType));
            sb.AppendLine();
        }

        var assertions = request.Assertions.Where(a => a.Enabled).ToList();
        if (assertions.Count > 0)
        {
            sb.AppendLine("## Assertions").AppendLine();
            foreach (var assertion in assertions) sb.AppendLine($"- {Escape(Describe(assertion))}");
            sb.AppendLine();
        }

        var captures = request.Captures.Where(c => c.Enabled && !string.IsNullOrWhiteSpace(c.Variable)).ToList();
        if (captures.Count > 0)
        {
            sb.AppendLine("## Captures").AppendLine();
            foreach (var capture in captures)
                sb.AppendLine($"- `{{{{{Escape(capture.Variable)}}}}}` ← {capture.Source} `{Escape(capture.Path)}`");
            sb.AppendLine();
        }

        if (node.LastCheckedUtc is { } when)
        {
            sb.AppendLine("## Known good").AppendLine();
            string verdict = node.LastStatusCode is { } code ? $"**{code}**" : "no response";
            sb.AppendLine($"Last checked {when.ToUniversalTime():yyyy-MM-dd} — {verdict}.");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string RenderEnvironment(ApiEnvironment environment, MarkdownExportOptions options)
    {
        var sb = new StringBuilder();
        WriteFrontMatter(sb, new[] { "certapi/environment" }, Array.Empty<(string, string)>());

        sb.AppendLine($"# {Escape(environment.Name)}").AppendLine();

        var variables = environment.Variables.Where(v => !string.IsNullOrWhiteSpace(v.Key)).ToList();
        if (variables.Count == 0) { sb.AppendLine("_No variables._"); return sb.ToString(); }

        sb.AppendLine("| Variable | Value |").AppendLine("|---|---|");
        foreach (var variable in variables)
        {
            // A variable the user marked secret is redacted on its own say-so, which is the whole
            // point of the flag; the escape hatch still opens it, as it does everywhere else.
            string value = variable.Secret && !options.IncludeSecrets ? Redacted : Cell(variable.Value ?? "");
            sb.AppendLine($"| {Cell(variable.Key)} | {value} |");
        }
        return sb.ToString();
    }

    private static string RenderChain(RequestChain chain, List<ExportedRequest> requests,
                                      MarkdownExportOptions options)
    {
        var sb = new StringBuilder();
        var front = new List<(string, string)> { ("steps", chain.Steps.Count.ToString()) };
        if (!string.IsNullOrWhiteSpace(chain.EnvironmentName))
            front.Add(("environment", Quote(chain.EnvironmentName!)));
        WriteFrontMatter(sb, new[] { "certapi/chain" }, front);

        sb.AppendLine($"# {Escape(chain.Name)}").AppendLine();
        if (!string.IsNullOrWhiteSpace(chain.EnvironmentName))
            sb.AppendLine($"Environment: [[{Link(chain.EnvironmentName!)}]]").AppendLine();

        if (chain.Steps.Count == 0) { sb.AppendLine("_No steps._"); return sb.ToString(); }

        sb.AppendLine("## Steps").AppendLine();
        for (int i = 0; i < chain.Steps.Count; i++)
        {
            var step = chain.Steps[i];
            var target = requests.FirstOrDefault(r => r.Node.Id == step.RequestId);
            // A step whose request was deleted is named as missing rather than linked into a void:
            // a wikilink to a note that does not exist looks like an export bug, not a data one.
            string label = target is null
                ? "_(missing request)_"
                : $"[[{Link(target.Title)}]]";
            sb.AppendLine($"{i + 1}. {label}"
                        + (step.StopOnFailure ? "" : " — continues on failure"));
        }
        return sb.ToString();
    }

    private static string RenderIndex(List<ExportedRequest> requests, AppState state, bool includeSecrets)
    {
        var sb = new StringBuilder();
        WriteFrontMatter(sb, new[] { "certapi/index" },
            new[] { ("requests", requests.Count.ToString()),
                    ("environments", state.Environments.Count.ToString()),
                    ("chains", state.Chains.Count.ToString()) });

        sb.AppendLine("# API catalogue").AppendLine();
        if (requests.Count == 0) { sb.AppendLine("_No saved requests._"); return sb.ToString(); }

        sb.AppendLine("| Request | Method | URL | Collection |").AppendLine("|---|---|---|---|");
        foreach (var request in requests.OrderBy(r => string.Join("/", r.Folders), StringComparer.OrdinalIgnoreCase)
                                        .ThenBy(r => r.Title, StringComparer.OrdinalIgnoreCase))
        {
            string collection = request.Folders.Count > 0 ? Cell(request.Folders[^1]) : "";
            sb.AppendLine($"| [[{Link(request.Title)}]] | {Cell(request.Request.Method)} "
                        + $"| {Cell(MarkdownSecrets.RedactUrl(request.Request.ExportUrl(), includeSecrets))} | {collection} |");
        }
        return sb.ToString();
    }

    // ------------------------------------------------------------------ formatting

    private static void WriteFrontMatter(StringBuilder sb, IReadOnlyCollection<string> tags,
                                         IReadOnlyCollection<(string Key, string Value)> fields)
    {
        sb.AppendLine("---");
        sb.AppendLine($"tags: [{string.Join(", ", tags)}]");
        foreach (var (key, value) in fields) sb.AppendLine($"{key}: {value}");
        sb.AppendLine("---").AppendLine();
    }

    /// <summary>An Authorization or Cookie header's value never reaches the note unless asked for.
    /// The header NAME stays either way, because "this request sends a bearer token" is exactly the
    /// kind of thing the catalogue exists to record.</summary>
    private static string HeaderValue(string name, string? value, MarkdownExportOptions options)
    {
        if (options.IncludeSecrets) return value ?? "";
        return MarkdownSecrets.IsSecretHeader(name) ? Redacted : value ?? "";
    }

    internal static string Describe(AssertionRule rule)
    {
        string subject = rule.Target switch
        {
            AssertTarget.Status => "status",
            AssertTarget.Time => "time",
            AssertTarget.Header => $"header {rule.Path}",
            AssertTarget.Body => $"body.{rule.Path}",
            AssertTarget.BodyText => "body text",
            _ => rule.Target.ToString().ToLowerInvariant()
        };
        string op = rule.Op switch
        {
            AssertOp.Equals => "==",
            AssertOp.NotEquals => "!=",
            AssertOp.Contains => "contains",
            AssertOp.Matches => "matches",
            AssertOp.Exists => "exists",
            AssertOp.NotExists => "does not exist",
            AssertOp.LessThan => "<",
            AssertOp.GreaterThan => ">",
            _ => rule.Op.ToString()
        };
        return rule.Op is AssertOp.Exists or AssertOp.NotExists
            ? $"{subject} {op}"
            : $"{subject} {op} {rule.Value}";
    }

    /// <summary>A fenced code block whose fence is long enough to survive backticks in the content.
    /// A body containing ``` would otherwise end the block early and spill the rest into the note
    /// as prose — which is how a redacted-looking document quietly stops being one.</summary>
    internal static string Fence(string content, string? contentType)
    {
        string type = contentType?.ToLowerInvariant() ?? "";
        string language =
            type.Contains("json") ? "json" :
            type.Contains("xml") ? "xml" :
            type.Contains("html") ? "html" :
            type.Contains("yaml") ? "yaml" : "";

        int longest = 0, run = 0;
        foreach (char c in content)
        {
            run = c == '`' ? run + 1 : 0;
            longest = Math.Max(longest, run);
        }
        string fence = new('`', Math.Max(3, longest + 1));
        return $"{fence}{language}\n{content}\n{fence}";
    }

    /// <summary>A filename that is safe on Windows and stable across exports, so re-exporting
    /// overwrites the same note rather than accumulating "Orders 2.md".</summary>
    internal static string NoteFile(string name, string fallback) => Sanitize(name, fallback) + ".md";

    /// <summary>Strip what a filename cannot carry, collapse the result, and never return empty.
    /// Windows reserved device names are suffixed rather than rejected: a request genuinely called
    /// "CON" is not an error, but a file called CON.md cannot be created.</summary>
    internal static string Sanitize(string? name, string fallback)
    {
        if (string.IsNullOrWhiteSpace(name)) return fallback;

        var sb = new StringBuilder(name.Length);
        foreach (char c in name.Trim())
            sb.Append(c is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*' || c < ' ' ? '-' : c);

        string cleaned = sb.ToString().Trim().Trim('.');
        while (cleaned.Contains("--")) cleaned = cleaned.Replace("--", "-");
        if (cleaned.Length == 0) return fallback;

        string[] reserved = { "CON", "PRN", "AUX", "NUL",
                              "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
                              "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" };
        if (reserved.Contains(cleaned, StringComparer.OrdinalIgnoreCase)) cleaned += "-note";

        return cleaned.Length > 120 ? cleaned[..120].TrimEnd() : cleaned;
    }

    /// <summary>The text inside <c>[[…]]</c>. Wikilinks cannot contain <c>[</c>, <c>]</c>, <c>|</c>
    /// or <c>#</c> — those have meaning to the link syntax itself — and the target is a note whose
    /// filename went through <see cref="Sanitize"/>, so this must agree with it.</summary>
    internal static string Link(string name) => Sanitize(name, "note");

    /// <summary>Escape markdown that would otherwise style a name: a request called
    /// <c>*beta*</c> must read as <c>*beta*</c>, not as italics.</summary>
    internal static string Escape(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            if (c is '\\' or '`' or '*' or '_' or '[' or ']' or '<' or '>' or '#') sb.Append('\\');
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>A table cell: escaped, and with pipes and newlines neutralised — either one ends
    /// the cell early and silently shifts every column after it.</summary>
    internal static string Cell(string text) =>
        Escape(text).Replace("|", "\\|").Replace("\r", "").Replace("\n", " ");

    private static string Tag(string text)
    {
        // Obsidian tags cannot contain spaces; a space would end the tag and leave the rest as prose.
        var sb = new StringBuilder(text.Length);
        foreach (char c in text) sb.Append(char.IsWhiteSpace(c) ? '-' : c);
        return sb.ToString();
    }

    private static string Quote(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    private static string Combine(params string[] parts) =>
        string.Join("/", parts.Where(p => !string.IsNullOrEmpty(p)));

    private static string Combine(IEnumerable<string> parts) =>
        string.Join("/", parts.Where(p => !string.IsNullOrEmpty(p)));
}
