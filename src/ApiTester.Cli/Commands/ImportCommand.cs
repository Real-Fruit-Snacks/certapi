using ApiTester.Core;

namespace ApiTester.Cli.Commands;

public static class ImportCommand
{
    public const string Help = """
        Usage: certapi import curl "<curl command>" [--into <folder>] [--workspace <file>]
               certapi import openapi <file>        [--into <folder>] [--workspace <file>]
               certapi import har <file>             [--into <folder>] [--workspace <file>]
               certapi import postman <file>         [--into <folder>] [--workspace <file>]
               certapi import insomnia <file>        [--into <folder>] [--workspace <file>]
               certapi import wsdl <file>            [--into <folder>] [--workspace <file>]
               certapi import bruno <folder|file>    [--into <folder>] [--workspace <file>]

        Adds requests to your collections — the live GUI state by default, or a workspace
        file. --into names a root-level folder (created if needed).

        postman: reads a Postman Collection (v2.0/v2.1 JSON export): folders, methods, URLs,
        query rows, headers (disabled ones stay disabled), raw/urlencoded/formdata bodies, and
        bearer/basic/apikey auth, with request-level auth beating folder- and collection-level.
        {{variables}} share their syntax and import unchanged; collection-level variables become
        an environment named after the collection (Postman's "secret" type stays secret here,
        encrypted at rest). A file form part imports disabled, since its path came from another
        machine. Anything that cannot carry across is named in a warning, never dropped silently.

        insomnia: reads an Insomnia v4 export (Export Data -> Insomnia v4 (JSON)): folders,
        methods, URLs, query and header rows (disabled ones stay disabled), text and form bodies,
        and bearer/basic auth. Insomnia's template syntax is translated -- its {{ _.name }} becomes
        this product's {{name}} -- and its environments come across as environments. A tag
        template ({% ... %}) is a small program rather than a value and has no equivalent here, so
        it is left in the text and named in a warning.

        bruno: reads a Bruno collection: its folder of .bru files (folders, requests in their seq
        order, query/path params, headers, json/text/xml/graphql/form/multipart bodies, and
        bearer/basic/digest/ntlm/awsv4/apikey auth, with "inherit" resolved through folder.bru and
        collection.bru), a single .bru file, or the JSON Bruno's Export Collection writes. Its
        environments/*.bru come across as environments; secret variables, which Bruno keeps out of
        its files, arrive empty. An assert line with an equivalent here (res.status: eq 200,
        res.body.id: isDefined, …) becomes an assertion, and a post-response variable that reads a
        body path or header becomes a capture. Scripts and tests are JavaScript and are named in a
        warning, as is anything else that cannot carry across.

        wsdl: reads a WSDL 1.1 document (and the SOAP 1.2 binding variant), turning each operation
        into a POST at the port's address with the right content type, the SOAPAction header for
        1.1, and an envelope skeleton naming the operation and its message parts. Deliberately
        minimal: types are NOT expanded from the schema -- each part becomes a commented
        placeholder naming its element or type, so a request is about ninety percent written and
        the rest is filled in by hand. An imported schema or document is named in a warning, never
        fetched: this reads the one file you name and touches no network.

        Global: --debug (verbose diagnostics) and --log-file <path> work here too.

        Examples:
          certapi import curl "curl -X POST https://api.example.com/login -d '{}'"
          certapi import openapi .\petstore.json
          certapi import openapi .\petstore.json --workspace .\suite.json
          certapi import har .\session.har
        """;

    public static int Run(Args args, TextWriter stdout, TextWriter stderr, CliServices services)
    {
        string? into = args.Value("--into");
        string? workspace = args.Value("--workspace");
        var positionals = args.Positionals();
        if (positionals.Count != 2) throw new CliUsageException(Help);

        string targetPath = workspace ?? services.LiveStatePath;
        if (workspace is null && services.IsGuiRunning())
            throw new CliDataException(
                "The GUI is running and would overwrite this change when it closes — close the app, or import into a --workspace file.");

        // Importing may target a brand-new workspace file: start empty and create it on save.
        var state = workspace is not null && !File.Exists(workspace)
            ? new AppState()
            : CliWorkspace.Load(workspace, services.LiveStatePath, stderr);

        int added;
        string what;
        var add = Target(state, into);
        switch (positionals[0].ToLowerInvariant())
        {
            case "curl":
            {
                ParsedRequest parsed;
                try { parsed = CurlParser.Parse(positionals[1]); }
                catch (Exception ex) { throw new CliDataException($"Could not parse the curl command: {ex.Message}"); }
                var model = RequestModel.FromParsed(parsed);
                var name = string.IsNullOrWhiteSpace(parsed.Name) ? $"{model.Method} {model.Path}" : parsed.Name!;
                add(new CollectionNode { Name = name, IsFolder = false, Request = model });
                added = 1; what = name;
                break;
            }
            case "openapi":
            {
                if (!File.Exists(positionals[1])) throw new CliDataException($"File not found: {positionals[1]}");
                ParsedCollection pc;
                try { pc = OpenApiImporter.Parse(File.ReadAllText(positionals[1])); }
                catch (Exception ex) { throw new CliDataException($"Could not parse '{positionals[1]}': {ex.Message}"); }
                var node = CollectionNode.FromParsed(pc);
                add(node);
                added = CountRequests(node); what = node.Name;
                break;
            }
            case "har":
            {
                if (!File.Exists(positionals[1])) throw new CliDataException($"File not found: {positionals[1]}");
                Har har;
                try { har = HarReader.Parse(File.ReadAllText(positionals[1])); }
                catch (HarFormatException ex) { throw new CliDataException(ex.Message); }
                var pc = new ParsedCollection { Name = Path.GetFileNameWithoutExtension(positionals[1]) };
                foreach (var entry in har.Log.Entries) pc.Requests.Add(HarReader.ToParsedRequest(entry));
                var node = CollectionNode.FromParsed(pc);
                add(node);
                added = CountRequests(node); what = node.Name;
                break;
            }
            case "postman":
            {
                if (!File.Exists(positionals[1])) throw new CliDataException($"File not found: {positionals[1]}");
                PostmanImportResult result;
                try { result = PostmanImport.Parse(File.ReadAllText(positionals[1])); }
                catch (FormatException ex) { throw new CliDataException($"Could not parse '{positionals[1]}': {ex.Message}"); }
                add(result.Root);
                if (result.Variables is { } env)
                {
                    // Merged by name, not duplicated: re-importing the same collection updates
                    // the environment it created rather than growing a second copy.
                    var existing = state.Environments.FirstOrDefault(
                        e => e.Name.Equals(env.Name, StringComparison.OrdinalIgnoreCase));
                    if (existing is not null) state.Environments.Remove(existing);
                    state.Environments.Add(env);
                    stderr.WriteLine($"Imported environment '{env.Name}' ({env.Variables.Count} variable{(env.Variables.Count == 1 ? "" : "s")}).");
                }
                foreach (var warning in result.Warnings) stderr.WriteLine("warning: " + warning);
                added = CountRequests(result.Root); what = result.Root.Name;
                break;
            }
            case "insomnia":
            {
                if (!File.Exists(positionals[1])) throw new CliDataException($"File not found: {positionals[1]}");
                InsomniaImportResult result;
                try { result = InsomniaImport.Parse(File.ReadAllText(positionals[1])); }
                catch (FormatException ex) { throw new CliDataException($"Could not parse '{positionals[1]}': {ex.Message}"); }
                add(result.Root);
                foreach (var env in result.Environments)
                {
                    // Merged by name, exactly as the Postman import does, so re-importing updates
                    // the environment it created rather than growing a second copy.
                    var existing = state.Environments.FirstOrDefault(
                        e => e.Name.Equals(env.Name, StringComparison.OrdinalIgnoreCase));
                    if (existing is not null) state.Environments.Remove(existing);
                    state.Environments.Add(env);
                    stderr.WriteLine($"Imported environment '{env.Name}' ({env.Variables.Count} variable{(env.Variables.Count == 1 ? "" : "s")}).");
                }
                foreach (var warning in result.Warnings) stderr.WriteLine("warning: " + warning);
                added = CountRequests(result.Root); what = result.Root.Name;
                break;
            }
            case "bruno":
            {
                BrunoImportResult result;
                try { result = ReadBruno(positionals[1]); }
                catch (FormatException ex) { throw new CliDataException($"Could not parse '{positionals[1]}': {ex.Message}"); }
                add(result.Root);
                foreach (var env in result.Environments)
                {
                    var existing = state.Environments.FirstOrDefault(
                        e => e.Name.Equals(env.Name, StringComparison.OrdinalIgnoreCase));
                    if (existing is not null) state.Environments.Remove(existing);
                    state.Environments.Add(env);
                    stderr.WriteLine($"Imported environment '{env.Name}' ({env.Variables.Count} variable{(env.Variables.Count == 1 ? "" : "s")}).");
                }
                foreach (var warning in result.Warnings) stderr.WriteLine("warning: " + warning);
                added = CountRequests(result.Root); what = result.Root.Name;
                break;
            }
            case "wsdl":
            {
                if (!File.Exists(positionals[1])) throw new CliDataException($"File not found: {positionals[1]}");
                WsdlImportResult result;
                try { result = WsdlImport.Parse(File.ReadAllText(positionals[1])); }
                catch (FormatException ex) { throw new CliDataException($"Could not parse '{positionals[1]}': {ex.Message}"); }
                add(result.Root);
                foreach (var warning in result.Warnings) stderr.WriteLine("warning: " + warning);
                added = CountRequests(result.Root); what = result.Root.Name;
                break;
            }
            default: throw new CliUsageException(Help);
        }

        CliWorkspace.ReportSaveResult(state.SaveTo(targetPath), targetPath, stderr);
        stderr.WriteLine($"Imported {added} request{(added == 1 ? "" : "s")} ({what}) into {(workspace is null ? "the live workspace" : workspace)}.");
        return ExitCodes.Ok;
    }

    /// <summary>A Bruno collection folder, one .bru file, or Bruno's JSON export.</summary>
    private static BrunoImportResult ReadBruno(string path)
    {
        if (Directory.Exists(path))
        {
            string full = Path.GetFullPath(path);
            var files = new List<(string, string)>();
            foreach (var file in Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(full, file).Replace('\\', '/');
                // A collection folder is often a git checkout, sometimes with packages for scripts.
                if (relative.Split('/').Any(part => part is "node_modules" or ".git")) continue;
                if (relative.EndsWith(".bru", StringComparison.OrdinalIgnoreCase) || relative.Equals("bruno.json", StringComparison.OrdinalIgnoreCase))
                    files.Add((relative, File.ReadAllText(file)));
            }
            if (!files.Any(f => f.Item1.EndsWith(".bru", StringComparison.OrdinalIgnoreCase)))
                throw new CliDataException($"'{path}' has no .bru files: give a Bruno collection folder.");
            return BrunoImport.ParseFolder(files, Path.GetFileName(full.TrimEnd(Path.DirectorySeparatorChar)));
        }
        if (!File.Exists(path)) throw new CliDataException($"File not found: {path}");
        string text = File.ReadAllText(path);
        return path.EndsWith(".bru", StringComparison.OrdinalIgnoreCase)
            ? BrunoImport.ParseFile(text, Path.GetFileNameWithoutExtension(path))
            : BrunoImport.ParseJson(text);
    }

    /// <summary>Where new nodes go: the collections root, or a root folder found/created by name.</summary>
    private static Action<CollectionNode> Target(AppState state, string? into)
    {
        if (into is null) return n => state.Collections.Add(n);
        var folder = state.Collections.FirstOrDefault(
            n => n.IsFolder && n.Name.Equals(into, StringComparison.OrdinalIgnoreCase));
        if (folder is null)
        {
            folder = new CollectionNode { Name = into, IsFolder = true };
            state.Collections.Add(folder);
        }
        return n => folder.Children.Add(n);
    }

    private static int CountRequests(CollectionNode n) =>
        n.IsFolder ? n.Children.Sum(CountRequests) : 1;
}
