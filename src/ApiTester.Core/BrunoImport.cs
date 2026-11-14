using System.Text;
using System.Text.Json;

namespace ApiTester.Core;

/// <summary>What a Bruno collection parsed to: the folder tree, its environments, and a warning
/// for everything that could not be carried across.</summary>
public sealed record BrunoImportResult(
    CollectionNode Root, IReadOnlyList<ApiEnvironment> Environments, IReadOnlyList<string> Warnings);

/// <summary>Reads a Bruno collection — the folder of <c>.bru</c> files Bruno keeps on disk, or the
/// JSON its Export Collection writes — into this product's collections tree.
/// <para>Pure text-in, tree-out, like <see cref="PostmanImport"/>: the caller reads the files.
/// Bruno's <c>{{variables}}</c> share this product's syntax and pass through unchanged. Its
/// <c>assert</c> block becomes assertions and <c>vars:post-response</c> becomes captures where they
/// have an equivalent here; scripts and tests are programs and are named in a warning.</para></summary>
public static class BrunoImport
{
    private static readonly HashSet<string> Methods = new(StringComparer.OrdinalIgnoreCase)
        { "get", "post", "put", "delete", "patch", "options", "head", "trace", "connect" };

    // ------------------------------------------------------------------ the .bru format

    /// <summary>One block of a .bru file: <c>name { … }</c> holding either <c>key: value</c> pairs
    /// (a leading <c>~</c> marks a disabled one) or, for bodies, scripts and docs, raw text.</summary>
    internal sealed record BruBlock(string Name, List<(string Key, string Value, bool Enabled)> Pairs, string? Text);

    /// <summary>Blocks whose content is text, not pairs.</summary>
    private static bool IsTextBlock(string name) =>
        name.StartsWith("body", StringComparison.Ordinal) && !name.StartsWith("body:form-urlencoded", StringComparison.Ordinal)
            && !name.StartsWith("body:multipart-form", StringComparison.Ordinal)
        || name.StartsWith("script", StringComparison.Ordinal) || name is "tests" or "docs";

    internal static List<BruBlock> ParseBru(string text)
    {
        var blocks = new List<BruBlock>();
        var lines = text.Replace("\r\n", "\n").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].TrimEnd();
            if (line.Length == 0 || char.IsWhiteSpace(line[0])) continue;
            bool list = line.EndsWith('[');
            if (!line.EndsWith('{') && !list) continue;
            string name = line[..^1].Trim();
            char close = list ? ']' : '}';
            var body = new List<string>();
            for (i++; i < lines.Length && lines[i].TrimEnd() != close.ToString(); i++) body.Add(lines[i]);

            if (list)
            {
                blocks.Add(new BruBlock(name, body.Select(l => l.Trim()).Where(l => l.Length > 0)
                    .Select(l => (l.TrimStart('~').TrimEnd(','), "", !l.StartsWith('~'))).ToList(), null));
            }
            else if (IsTextBlock(name))
            {
                blocks.Add(new BruBlock(name, new(), Dedent(body, 2)));
            }
            else
            {
                var pairs = new List<(string, string, bool)>();
                for (int j = 0; j < body.Count; j++)
                {
                    string raw = body[j].Trim();
                    if (raw.Length == 0) continue;
                    bool enabled = !raw.StartsWith('~');
                    if (!enabled) raw = raw[1..];
                    int colon = raw.IndexOf(':');
                    if (colon <= 0) continue;
                    string key = raw[..colon].Trim();
                    string value = raw[(colon + 1)..].Trim();
                    if (value == "'''")
                    {
                        // A multi-line value, newer Bruno: indented lines up to a closing '''.
                        var multi = new List<string>();
                        for (j++; j < body.Count && body[j].Trim() != "'''"; j++) multi.Add(body[j]);
                        value = Dedent(multi, 4);
                    }
                    pairs.Add((key, value, enabled));
                }
                blocks.Add(new BruBlock(name, pairs, null));
            }
        }
        return blocks;
    }

    private static string Dedent(List<string> lines, int by)
    {
        while (lines.Count > 0 && lines[^1].Trim().Length == 0) lines.RemoveAt(lines.Count - 1);
        return string.Join("\n", lines.Select(l =>
        {
            int n = 0;
            while (n < by && n < l.Length && l[n] == ' ') n++;
            return l[n..];
        }));
    }

    /// <summary>A collection from its files: every path relative to the collection folder, with
    /// '/' separators. <c>bruno.json</c> names it, <c>collection.bru</c> and each
    /// <c>folder.bru</c> carry auth a request may inherit, and <c>environments/*.bru</c> are its
    /// environments.</summary>
    public static BrunoImportResult ParseFolder(IReadOnlyList<(string Path, string Text)> files, string fallbackName)
    {
        var warnings = new List<string>();
        string name = fallbackName;
        if (files.FirstOrDefault(f => f.Path.Equals("bruno.json", StringComparison.OrdinalIgnoreCase)) is { Text: { } config })
        {
            try
            {
                using var doc = JsonDocument.Parse(config);
                if (doc.RootElement.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String) name = n.GetString()!;
            }
            catch (JsonException) { warnings.Add("bruno.json is not valid JSON; the folder name is used instead."); }
        }
        var root = new CollectionNode { Name = name, IsFolder = true };
        var environments = new List<ApiEnvironment>();

        // Inherited auth, by folder path ("" is the collection itself).
        var folderAuth = new Dictionary<string, List<BruBlock>>(StringComparer.OrdinalIgnoreCase);
        var folderNames = new Dictionary<string, (string Name, int Seq)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, text) in files)
        {
            string file = System.IO.Path.GetFileName(path);
            string dir = DirOf(path);
            if (file.Equals("collection.bru", StringComparison.OrdinalIgnoreCase) && dir.Length == 0)
                folderAuth[""] = ParseBru(text);
            else if (file.Equals("folder.bru", StringComparison.OrdinalIgnoreCase))
            {
                var blocks = ParseBru(text);
                folderAuth[dir] = blocks;
                var meta = Block(blocks, "meta");
                folderNames[dir] = (Pair(meta, "name") ?? System.IO.Path.GetFileName(dir),
                                    int.TryParse(Pair(meta, "seq"), out int s) ? s : int.MaxValue);
            }
        }

        var folders = new Dictionary<string, CollectionNode>(StringComparer.OrdinalIgnoreCase) { [""] = root };
        var order = new Dictionary<CollectionNode, int>();
        CollectionNode FolderFor(string dir)
        {
            if (folders.TryGetValue(dir, out var existing)) return existing;
            var parent = FolderFor(DirOf(dir));
            var (folderName, seq) = folderNames.TryGetValue(dir, out var named) ? named : (System.IO.Path.GetFileName(dir), int.MaxValue);
            var node = new CollectionNode { Name = folderName, IsFolder = true };
            parent.Children.Add(node);
            order[node] = seq;
            return folders[dir] = node;
        }

        foreach (var (path, text) in files.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase))
        {
            if (!path.EndsWith(".bru", StringComparison.OrdinalIgnoreCase)) continue;
            string file = System.IO.Path.GetFileName(path);
            string dir = DirOf(path);
            if (dir.Equals("environments", StringComparison.OrdinalIgnoreCase))
            {
                if (EnvironmentFrom(System.IO.Path.GetFileNameWithoutExtension(file), ParseBru(text), warnings) is { } env) environments.Add(env);
                continue;
            }
            if (file.Equals("collection.bru", StringComparison.OrdinalIgnoreCase) || file.Equals("folder.bru", StringComparison.OrdinalIgnoreCase))
                continue;

            var blocks = ParseBru(text);
            var meta = Block(blocks, "meta");
            string requestName = Pair(meta, "name") ?? System.IO.Path.GetFileNameWithoutExtension(file);
            if (Pair(meta, "type") is { } type && type is not ("http" or "graphql"))
            {
                warnings.Add($"'{requestName}': a Bruno '{type}' request has no equivalent here and was not imported.");
                continue;
            }
            var model = FromBru(requestName, blocks, dir, folderAuth, warnings);
            if (model is null) continue;
            var node = new CollectionNode { Name = requestName, IsFolder = false, Request = model };
            FolderFor(dir).Children.Add(node);
            order[node] = int.TryParse(Pair(meta, "seq"), out int seq) ? seq : int.MaxValue;
        }

        SortBySeq(root, order);
        return new BrunoImportResult(root, environments, warnings);
    }

    /// <summary>One <c>.bru</c> request file on its own, in a folder named after it.</summary>
    public static BrunoImportResult ParseFile(string text, string fallbackName) =>
        ParseFolder(new[] { (fallbackName + ".bru", text) }, fallbackName);

    private static void SortBySeq(CollectionNode folder, Dictionary<CollectionNode, int> order)
    {
        var sorted = folder.Children
            .Select((c, i) => (c, i))
            .OrderBy(x => order.TryGetValue(x.c, out int s) ? s : int.MaxValue).ThenBy(x => x.i)
            .Select(x => x.c).ToList();
        folder.Children.Clear();
        foreach (var c in sorted)
        {
            folder.Children.Add(c);
            if (c.IsFolder) SortBySeq(c, order);
        }
    }

    private static string DirOf(string path)
    {
        int slash = path.Replace('\\', '/').LastIndexOf('/');
        return slash < 0 ? "" : path.Replace('\\', '/')[..slash];
    }

    private static BruBlock? Block(List<BruBlock> blocks, string name) =>
        blocks.FirstOrDefault(b => b.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static string? Pair(BruBlock? block, string key) =>
        block?.Pairs.Where(p => p.Enabled && p.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Select(p => p.Value).FirstOrDefault();

    private static RequestModel? FromBru(string name, List<BruBlock> blocks, string dir,
        Dictionary<string, List<BruBlock>> folderAuth, List<string> warnings)
    {
        var methodBlock = blocks.FirstOrDefault(b => Methods.Contains(b.Name));
        if (methodBlock is null)
        {
            warnings.Add($"'{name}': no method block (get {{ … }}, post {{ … }}); not imported.");
            return null;
        }
        var model = new RequestModel { Method = methodBlock.Name.ToUpperInvariant() };
        string url = Pair(methodBlock, "url") ?? "";
        int q = url.IndexOf('?');
        model.Path = q < 0 ? url : url[..q];

        var query = Block(blocks, "params:query") ?? Block(blocks, "query");
        if (query is not null)
            foreach (var (k, v, on) in query.Pairs) model.QueryParams.Add(new ParamRow { Key = k, Value = v, Enabled = on });
        else if (q >= 0)
            foreach (var kv in RequestUrl.SplitForEditing(url).Params) model.QueryParams.Add(new ParamRow { Key = kv.Key, Value = kv.Value });
        if (Block(blocks, "params:path") is { Pairs.Count: > 0 } pathParams)
            foreach (var (k, v, on) in pathParams.Pairs)
                if (on) model.Path = model.Path.Replace(":" + k, v, StringComparison.Ordinal);

        if (Block(blocks, "headers") is { } headers)
            foreach (var (k, v, on) in headers.Pairs) model.Headers.Add(new HeaderRow { Name = k, Value = v, Enabled = on });

        ApplyBody(name, Pair(methodBlock, "body") ?? "none", mode => Block(blocks, "body:" + mode), model, warnings);

        // Auth: the request's own, or — "inherit" — the nearest folder's, then the collection's.
        // No auth line at all leaves this product's default (Auto); Bruno itself writes "none".
        string authMode = Pair(methodBlock, "auth") ?? "";
        var authBlocks = blocks;
        if (authMode == "inherit")
        {
            authMode = "none";
            for (string d = dir; ; d = DirOf(d))
            {
                if (folderAuth.TryGetValue(d, out var inherited) && Pair(Block(inherited, "auth"), "mode") is { } m && m != "inherit")
                {
                    authMode = m;
                    authBlocks = inherited;
                    break;
                }
                if (d.Length == 0) break;
            }
        }
        if (authMode.Length > 0)
            ApplyAuth(name, authMode, key => Block(authBlocks, "auth:" + key) is { } b ? (field => Pair(b, field)) : null, model, warnings);

        if (Block(blocks, "assert") is { } asserts)
            foreach (var (k, v, on) in asserts.Pairs)
                if (Assertion(k, v) is { } rule) { rule.Enabled = on; model.Assertions.Add(rule); }
                else warnings.Add($"'{name}': assertion '{k}: {v}' has no equivalent here and was not imported.");

        if (Block(blocks, "vars:post-response") is { } post)
            foreach (var (k, v, on) in post.Pairs)
                if (Capture(k, v) is { } capture) { capture.Enabled = on; model.Captures.Add(capture); }
                else warnings.Add($"'{name}': post-response variable '{k}' is an expression, not a path, and was not imported.");

        foreach (var b in blocks.Where(b => b.Name.StartsWith("script", StringComparison.Ordinal) || b.Name == "tests"))
            if (!string.IsNullOrWhiteSpace(b.Text))
                warnings.Add($"'{name}': its {b.Name} block is JavaScript and was not imported.");
        if (Block(blocks, "vars:pre-request") is { Pairs.Count: > 0 })
            warnings.Add($"'{name}': pre-request variables are not imported; put them in an environment.");
        return model;
    }

    // ------------------------------------------------------------------ shared mapping

    /// <summary>A body by Bruno's mode name; <paramref name="block"/> finds the body block for a mode.</summary>
    private static void ApplyBody(string name, string mode, Func<string, BruBlock?> block, RequestModel model, List<string> warnings)
    {
        switch (mode)
        {
            case "none": return;
            case "json": SetText(block("json")?.Text, "application/json"); return;
            case "text": SetText(block("text")?.Text, "text/plain"); return;
            case "xml": SetText(block("xml")?.Text, "application/xml"); return;
            case "sparql": SetText(block("sparql")?.Text, "application/sparql-query"); return;
            case "graphql":
            {
                string queryText = block("graphql")?.Text ?? "";
                string? vars = block("graphql:vars")?.Text;
                var payload = new Dictionary<string, object?> { ["query"] = queryText };
                if (!string.IsNullOrWhiteSpace(vars))
                {
                    try { payload["variables"] = JsonDocument.Parse(vars).RootElement.Clone(); }
                    catch (JsonException) { warnings.Add($"'{name}': GraphQL variables are not valid JSON and were left out."); }
                }
                SetText(JsonSerializer.Serialize(payload), "application/json");
                return;
            }
            case "formUrlEncoded" or "form-urlencoded":
            {
                var pairs = block("form-urlencoded")?.Pairs ?? new();
                model.Body = string.Join("&", pairs.Where(p => p.Enabled).Select(p =>
                    VariableResolver.EscapeOutsideTokens(p.Key, Uri.EscapeDataString) + "=" +
                    VariableResolver.EscapeOutsideTokens(p.Value, Uri.EscapeDataString)));
                model.ContentType = "application/x-www-form-urlencoded";
                return;
            }
            case "multipartForm" or "multipart-form":
            {
                model.IsMultipart = true;
                foreach (var (k, v, on) in block("multipart-form")?.Pairs ?? new())
                {
                    bool isFile = v.StartsWith("@file(", StringComparison.Ordinal);
                    if (isFile) warnings.Add($"'{name}': file part '{k}' imported disabled — set its path before sending.");
                    model.FormParts.Add(new FormPart
                    {
                        Name = k, IsFile = isFile, Enabled = on && !isFile,
                        Value = isFile ? v[6..].TrimEnd(')') : v
                    });
                }
                return;
            }
            default:
                warnings.Add($"'{name}': body mode '{mode}' is not supported — the request imported without a body.");
                return;
        }

        void SetText(string? text, string type)
        {
            if (text is null) return;
            model.Body = text;
            model.ContentType = type;
        }
    }

    /// <summary>Auth by Bruno's mode name; <paramref name="section"/> gives the fields of a mode's
    /// block (null when the file has none).</summary>
    private static void ApplyAuth(string name, string mode, Func<string, Func<string, string?>?> section, RequestModel model, List<string> warnings)
    {
        string F(string key, string field) => section(key)?.Invoke(field) ?? "";
        switch (mode)
        {
            case "none":
                model.AuthType = "None";
                break;
            case "bearer":
                model.AuthType = "Bearer";
                model.AuthSecret = F("bearer", "token");
                break;
            case "basic":
                model.AuthType = "Basic";
                model.AuthUser = F("basic", "username");
                model.AuthSecret = F("basic", "password");
                break;
            case "digest":
                model.AuthType = "Digest";
                model.AuthUser = F("digest", "username");
                model.AuthSecret = F("digest", "password");
                break;
            case "ntlm":
            {
                model.AuthType = "Windows";
                string domain = F("ntlm", "domain");
                string user = F("ntlm", "username");
                model.AuthUser = domain.Length > 0 ? domain + "\\" + user : user;
                model.AuthSecret = F("ntlm", "password");
                break;
            }
            case "awsv4":
            {
                model.AuthType = "AwsSigV4";
                model.AuthUser = F("awsv4", "accessKeyId");
                model.AuthSecret = F("awsv4", "secretAccessKey");
                string region = F("awsv4", "region"), service = F("awsv4", "service");
                model.AuthScope = region.Length == 0 && service.Length == 0 ? null : region + "/" + service;
                if (F("awsv4", "sessionToken") is { Length: > 0 } token)
                    model.Headers.Add(new HeaderRow { Name = "X-Amz-Security-Token", Value = token });
                if (F("awsv4", "profileName").Length > 0)
                    warnings.Add($"'{name}': an AWS profile name is not read; give the keys themselves.");
                break;
            }
            case "apikey":
            {
                string key = F("apikey", "key"), value = F("apikey", "value");
                if (F("apikey", "placement").Equals("queryparams", StringComparison.OrdinalIgnoreCase))
                    model.QueryParams.Add(new ParamRow { Key = key, Value = value });
                else
                    model.Headers.Add(new HeaderRow { Name = key, Value = value });
                break;
            }
            default:
                model.AuthType = "None";
                warnings.Add($"'{name}': auth mode '{mode}' is not supported — the request imported without auth.");
                break;
        }
    }

    /// <summary>Bruno's <c>res.status: eq 200</c> and friends, where this product has the same check.</summary>
    internal static AssertionRule? Assertion(string subject, string expression)
    {
        string expr = expression.Trim();
        int space = expr.IndexOf(' ');
        string op = space < 0 ? expr : expr[..space];
        string value = space < 0 ? "" : expr[(space + 1)..].Trim().Trim('"');
        AssertOp? mapped = op switch
        {
            "eq" => AssertOp.Equals, "neq" => AssertOp.NotEquals,
            "gt" => AssertOp.GreaterThan, "lt" => AssertOp.LessThan,
            "contains" => AssertOp.Contains, "matches" => AssertOp.Matches,
            "isDefined" => AssertOp.Exists, "isUndefined" => AssertOp.NotExists,
            _ => null
        };
        if (mapped is null) return null;
        var rule = new AssertionRule { Op = mapped.Value, Value = value };
        string s = subject.Trim();
        if (s == "res.status") rule.Target = AssertTarget.Status;
        else if (s == "res.responseTime") rule.Target = AssertTarget.Time;
        else if (s.StartsWith("res.headers.", StringComparison.Ordinal) || s.StartsWith("res.headers[", StringComparison.Ordinal))
        {
            rule.Target = AssertTarget.Header;
            rule.Path = s["res.headers".Length..].TrimStart('.').Trim('[', ']', '"', '\'');
        }
        else if (s == "res.body") { rule.Target = rule.Op is AssertOp.Contains or AssertOp.Matches ? AssertTarget.BodyText : AssertTarget.Body; rule.Path = "$"; }
        else if (s.StartsWith("res.body.", StringComparison.Ordinal) || s.StartsWith("res.body[", StringComparison.Ordinal))
        {
            rule.Target = AssertTarget.Body;
            rule.Path = s["res.body".Length..].TrimStart('.');
        }
        else return null;
        if (rule.Target is AssertTarget.Status or AssertTarget.Time && rule.Op is AssertOp.Exists or AssertOp.NotExists) return null;
        return rule;
    }

    /// <summary><c>token: res.body.access_token</c> is a capture; anything that computes is not.</summary>
    internal static CaptureRule? Capture(string variable, string expression)
    {
        string e = expression.Trim();
        if (e.StartsWith("res.body.", StringComparison.Ordinal) && e[9..].All(c => char.IsLetterOrDigit(c) || c is '_' or '.' or '[' or ']' or '-'))
            return new CaptureRule { Variable = variable, Source = CaptureSource.Body, Path = e[9..] };
        if (e.StartsWith("res.headers.", StringComparison.Ordinal) && e[12..].All(c => char.IsLetterOrDigit(c) || c is '-' or '_'))
            return new CaptureRule { Variable = variable, Source = CaptureSource.Header, Path = e[12..] };
        if (e.StartsWith("res(\"", StringComparison.Ordinal) && e.EndsWith("\")", StringComparison.Ordinal))
            return new CaptureRule { Variable = variable, Source = CaptureSource.Body, Path = e[5..^2] };
        return null;
    }

    private static ApiEnvironment? EnvironmentFrom(string name, List<BruBlock> blocks, List<string> warnings)
    {
        var env = new ApiEnvironment { Name = name };
        if (Block(blocks, "vars") is { } vars)
            foreach (var (k, v, on) in vars.Pairs)
                if (on) env.Variables.Add(new Variable { Key = k, Value = v });
        if (Block(blocks, "vars:secret") is { } secrets && secrets.Pairs.Count > 0)
        {
            foreach (var (k, _, _) in secrets.Pairs) env.Variables.Add(new Variable { Key = k, Value = "", Secret = true });
            warnings.Add($"environment '{name}': Bruno keeps secret values out of its files, so {secrets.Pairs.Count} secret variable(s) came across empty — fill them in.");
        }
        return env.Variables.Count > 0 ? env : null;
    }

    // ------------------------------------------------------------------ the JSON export

    /// <summary>Bruno's Export Collection JSON (<c>{"name", "version", "items": […]}</c>).</summary>
    public static BrunoImportResult ParseJson(string json)
    {
        JsonElement root;
        try { root = JsonDocument.Parse(json).RootElement; }
        catch (JsonException ex) { throw new FormatException("Not JSON: " + ex.Message); }
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            throw new FormatException("Not a Bruno collection export: expected an object with an 'items' array.");

        var warnings = new List<string>();
        var rootNode = new CollectionNode { Name = Str(root, "name") ?? "Bruno import", IsFolder = true };
        var collectionAuth = root.TryGetProperty("root", out var r) && r.TryGetProperty("request", out var rr) && rr.TryGetProperty("auth", out var ra) ? ra : default;
        AddItems(items, rootNode, collectionAuth, warnings);

        var environments = new List<ApiEnvironment>();
        if (root.TryGetProperty("environments", out var envs) && envs.ValueKind == JsonValueKind.Array)
            foreach (var e in envs.EnumerateArray())
            {
                var env = new ApiEnvironment { Name = Str(e, "name") ?? "Bruno" };
                if (e.TryGetProperty("variables", out var vars) && vars.ValueKind == JsonValueKind.Array)
                    foreach (var v in vars.EnumerateArray())
                        if (Bool(v, "enabled") != false)
                            env.Variables.Add(new Variable { Key = Str(v, "name") ?? "", Value = Str(v, "value") ?? "", Secret = Bool(v, "secret") == true });
                if (env.Variables.Count > 0) environments.Add(env);
            }
        return new BrunoImportResult(rootNode, environments, warnings);
    }

    private static void AddItems(JsonElement items, CollectionNode into, JsonElement inheritedAuth, List<string> warnings)
    {
        var ordered = items.EnumerateArray().Where(i => i.ValueKind == JsonValueKind.Object)
            .Select((item, index) => (item, index))
            .OrderBy(x => x.item.TryGetProperty("seq", out var s) && s.TryGetInt32(out int n) ? n : int.MaxValue).ThenBy(x => x.index);
        foreach (var (item, _) in ordered)
        {
            string type = Str(item, "type") ?? "";
            string name = Str(item, "name") ?? "(unnamed)";
            if (type == "folder")
            {
                var folder = new CollectionNode { Name = name, IsFolder = true };
                var folderAuth = item.TryGetProperty("root", out var fr) && fr.TryGetProperty("request", out var frr) && frr.TryGetProperty("auth", out var fa)
                    && Str(fa, "mode") is { } fm && fm != "inherit" ? fa : inheritedAuth;
                if (item.TryGetProperty("items", out var children) && children.ValueKind == JsonValueKind.Array)
                    AddItems(children, folder, folderAuth, warnings);
                into.Children.Add(folder);
                continue;
            }
            if (type is not ("http-request" or "graphql-request" or "http" or "graphql"))
            {
                warnings.Add($"'{name}': a Bruno '{type}' item has no equivalent here and was not imported.");
                continue;
            }
            if (!item.TryGetProperty("request", out var req) || req.ValueKind != JsonValueKind.Object) continue;
            into.Children.Add(new CollectionNode { Name = name, IsFolder = false, Request = FromJson(name, req, inheritedAuth, warnings) });
        }
    }

    private static RequestModel FromJson(string name, JsonElement req, JsonElement inheritedAuth, List<string> warnings)
    {
        var model = new RequestModel { Method = (Str(req, "method") ?? "GET").ToUpperInvariant() };
        string url = Str(req, "url") ?? "";
        int q = url.IndexOf('?');
        model.Path = q < 0 ? url : url[..q];
        bool sawQuery = false;
        foreach (var p in Array(req, "params"))
        {
            string key = Str(p, "name") ?? "", value = Str(p, "value") ?? "";
            if (Str(p, "type") == "path") { model.Path = model.Path.Replace(":" + key, value, StringComparison.Ordinal); continue; }
            model.QueryParams.Add(new ParamRow { Key = key, Value = value, Enabled = Bool(p, "enabled") != false });
            sawQuery = true;
        }
        if (!sawQuery && q >= 0)
            foreach (var kv in RequestUrl.SplitForEditing(url).Params) model.QueryParams.Add(new ParamRow { Key = kv.Key, Value = kv.Value });
        foreach (var h in Array(req, "headers"))
            model.Headers.Add(new HeaderRow { Name = Str(h, "name") ?? "", Value = Str(h, "value") ?? "", Enabled = Bool(h, "enabled") != false });

        if (req.TryGetProperty("body", out var body) && body.ValueKind == JsonValueKind.Object)
        {
            string mode = Str(body, "mode") ?? "none";
            ApplyBody(name, mode, m => m switch
            {
                "graphql" => body.TryGetProperty("graphql", out var g) ? new BruBlock(m, new(), Str(g, "query")) : null,
                "graphql:vars" => body.TryGetProperty("graphql", out var g) ? new BruBlock(m, new(), Str(g, "variables")) : null,
                "form-urlencoded" => new BruBlock(m, Pairs(body, "formUrlEncoded"), null),
                "multipart-form" => new BruBlock(m, Array(body, "multipartForm").Select(p =>
                    (Str(p, "name") ?? "", Str(p, "type") == "file" ? "@file(" + FileValue(p) + ")" : Str(p, "value") ?? "", Bool(p, "enabled") != false)).ToList(), null),
                _ => Str(body, m) is { } text ? new BruBlock(m, new(), text) : null
            }, model, warnings);
        }

        var auth = req.TryGetProperty("auth", out var a) && a.ValueKind == JsonValueKind.Object ? a : default;
        if (auth.ValueKind == JsonValueKind.Object && Str(auth, "mode") == "inherit") auth = inheritedAuth;
        if (auth.ValueKind == JsonValueKind.Object)
        {
            var source = auth;
            ApplyAuth(name, Str(source, "mode") ?? "none",
                key => source.TryGetProperty(key, out var s) && s.ValueKind == JsonValueKind.Object ? (field => Str(s, field)) : null,
                model, warnings);
        }

        foreach (var x in Array(req, "assertions"))
            if (Assertion(Str(x, "name") ?? "", Str(x, "value") ?? "") is { } rule) { rule.Enabled = Bool(x, "enabled") != false; model.Assertions.Add(rule); }
            else warnings.Add($"'{name}': assertion '{Str(x, "name")}: {Str(x, "value")}' has no equivalent here and was not imported.");
        if (req.TryGetProperty("vars", out var vars) && vars.ValueKind == JsonValueKind.Object)
            foreach (var v in Array(vars, "res"))
                if (Capture(Str(v, "name") ?? "", Str(v, "value") ?? "") is { } c) { c.Enabled = Bool(v, "enabled") != false; model.Captures.Add(c); }
                else warnings.Add($"'{name}': post-response variable '{Str(v, "name")}' is an expression, not a path, and was not imported.");
        if (req.TryGetProperty("script", out var script) && script.ValueKind == JsonValueKind.Object &&
            script.EnumerateObject().Any(p => p.Value.ValueKind == JsonValueKind.String && p.Value.GetString()!.Trim().Length > 0))
            warnings.Add($"'{name}': its scripts are JavaScript and were not imported.");
        if (Str(req, "tests") is { Length: > 0 } tests && tests.Trim().Length > 0)
            warnings.Add($"'{name}': its tests are JavaScript and were not imported.");
        return model;
    }

    private static string FileValue(JsonElement p) =>
        p.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.Array
            ? string.Join(",", v.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()))
            : Str(p, "value") ?? "";

    private static List<(string, string, bool)> Pairs(JsonElement o, string name) =>
        Array(o, name).Select(p => (Str(p, "name") ?? "", Str(p, "value") ?? "", Bool(p, "enabled") != false)).ToList();

    private static IEnumerable<JsonElement> Array(JsonElement o, string name) =>
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array
            ? a.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object) : Enumerable.Empty<JsonElement>();

    private static string? Str(JsonElement o, string name) =>
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool? Bool(JsonElement o, string name) =>
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? v.GetBoolean() : null;
}
