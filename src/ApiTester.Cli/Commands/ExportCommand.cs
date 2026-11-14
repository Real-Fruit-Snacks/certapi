using System.Text.Json;
using ApiTester.Core;

namespace ApiTester.Cli.Commands;

public static class ExportCommand
{
    public const string Help = """
        Usage: certapi export openapi [<folder>] -o <file> [--workspace <file>]
               certapi export openapi --from-har <file.har> -o <file> [--host <h>] [--no-template-ids]
               certapi export workspace -o <file> [--workspace <file>] [--include-secrets]
               certapi export markdown -o <folder> [--workspace <file>] [--into <name>]
                                       [--index] [--include-secrets]

        openapi: writes collections (optionally one root folder) as an OpenAPI 3.0
        document — auth is exported as a security scheme only, never the secrets.
        --from-har turns a captured HTTP Archive (HAR) session into an OpenAPI document instead of
        reading saved collections: repeated calls to the same endpoint collapse into one operation,
        identifier-looking path segments become {id}, responses of 400 and above are skipped, and
        redacted header values are never written.
          --host <h>          keep only entries for this host (case-insensitive)
          --no-template-ids   keep identifier-looking path segments literal instead of {id}
        -o (long form --output) names the file to write in every form above.
        workspace: writes the whole workspace as a portable JSON file (window geometry stripped).
        Secrets — captured tokens and cookies, saved auth values, secret environment
        variables, and stored response bodies (history and known-good) — are stripped by default,
        since an exported workspace is a file people email to each other.
          --include-secrets   Keep secrets in the export instead of stripping them; they are
                               written encrypted for the current Windows user, same as a saved
                               workspace, so another user or machine still cannot read them.
        markdown: writes the workspace as a folder of linked markdown notes — one note per saved
        request, plus environments and chains, with YAML frontmatter and [[wikilinks]]. An Obsidian
        vault is just a folder of markdown, so -o can point straight at one; the same output works
        in Logseq, Foam, a docs repository or a plain wiki. Re-exporting overwrites the same notes
        in place, so a note you edited by hand is overwritten too — that is why the generated tree
        lives in its own subfolder.
          --into <name>       Subfolder to write into (default "certapi")
          --index             Also write index.md, a table of every request
          --include-secrets   Keep credential values. Off by default, and the default matters here
                               more than anywhere else: vaults sync (Obsidian Sync, iCloud,
                               OneDrive, git), so a note is likely to leave the machine

        Global: --debug (verbose diagnostics) and --log-file <path> work here too.

        Examples:
          certapi export openapi -o api.json
          certapi export openapi --from-har session.har -o api.json
          certapi export workspace -o workspace.json
          certapi export workspace -o workspace.json --include-secrets
          certapi export markdown -o C:\Users\me\Vault --index
        """;

    public static int Run(Args args, TextWriter stdout, TextWriter stderr, CliServices services)
    {
        string? fromHar = args.Value("--from-har");
        string? host = args.Value("--host");
        bool noTemplateIds = args.Flag("--no-template-ids");
        bool includeSecrets = args.Flag("--include-secrets");
        string? workspace = args.Value("--workspace");
        string? into = args.Value("--into");
        bool index = args.Flag("--index");
        string? outFile = args.Value("-o", "--output");
        var positionals = args.Positionals();
        if (positionals.Count is < 1 or > 2 || outFile is null) throw new CliUsageException(Help);
        string kind = positionals[0].ToLowerInvariant();

        if (includeSecrets && (fromHar is not null || kind is not ("workspace" or "markdown")))
            throw new CliUsageException(
                "--include-secrets only applies to 'export workspace' and 'export markdown'.");
        if ((into is not null || index) && kind != "markdown")
            throw new CliUsageException("--into and --index only apply to 'export markdown'.");

        if (fromHar is not null)
        {
            if (kind != "openapi")
                throw new CliUsageException("--from-har only applies to 'export openapi'.");
            if (positionals.Count != 1)
                throw new CliUsageException("--from-har exports the archive itself, so it takes no folder name.");
            return ExportOpenApiFromHar(fromHar, outFile, host, templateIds: !noTemplateIds, stderr);
        }
        if (host is not null || noTemplateIds)
            throw new CliUsageException("--host and --no-template-ids only apply together with --from-har.");

        var state = CliWorkspace.Load(workspace, services.LiveStatePath, stderr);

        switch (kind)
        {
            case "openapi":
            {
                CollectionNode wrapper;
                if (positionals.Count == 2)
                {
                    wrapper = state.Collections.FirstOrDefault(n =>
                            n.IsFolder && n.Name.Equals(positionals[1], StringComparison.OrdinalIgnoreCase))
                        ?? throw new CliDataException($"No folder named '{positionals[1]}' at the collections root.");
                }
                else
                {
                    wrapper = new CollectionNode { Name = "API collection", IsFolder = true };
                    foreach (var n in state.Collections) wrapper.Children.Add(n);
                }
                var pc = wrapper.ToParsed();
                if (pc.Requests.Count == 0 && pc.Folders.Count == 0)
                    throw new CliDataException("Nothing to export — no saved requests found.");
                File.WriteAllText(outFile, OpenApiExporter.ToJson(pc));
                stderr.WriteLine($"Exported OpenAPI to {outFile}.");
                return ExitCodes.Ok;
            }
            case "workspace":
            {
                if (positionals.Count != 1) throw new CliUsageException(Help);
                var clone = state.DeepClone();
                clone.WindowWidth = clone.WindowHeight = clone.WindowLeft = clone.WindowTop = null;
                clone.WindowMaximized = false;

                if (includeSecrets)
                {
                    var result = clone.SaveTo(outFile);
                    stderr.WriteLine($"Workspace exported to {outFile} — it includes auth values and history, so treat it as private. " +
                        "The secrets in it are encrypted for your Windows user, so another user or another machine will not be able to read them.");
                    CliWorkspace.ReportSaveResult(result, outFile, stderr);
                }
                else
                {
                    var summary = StateSecrets.Strip(clone);
                    var result = clone.SaveTo(outFile);
                    stderr.WriteLine(summary.Any
                        ? $"Workspace exported to {outFile} — stripped {summary.Describe()}. Pass --include-secrets to keep them."
                        : $"Workspace exported to {outFile} — it contained no secrets to strip.");
                    CliWorkspace.ReportSaveResult(result, outFile, stderr);
                }
                return ExitCodes.Ok;
            }
            case "markdown":
            {
                if (positionals.Count != 1) throw new CliUsageException(Help);
                var files = MarkdownExport.Build(state, new MarkdownExportOptions
                {
                    Into = into ?? "certapi",
                    Index = index,
                    IncludeSecrets = includeSecrets,
                });
                if (files.Count == 0)
                    throw new CliDataException("Nothing to export — no saved requests, environments or chains.");

                foreach (var file in files)
                {
                    // The builder returns forward-slash relative paths; the filesystem decides the
                    // separator, and the folder has to exist before the file can.
                    string path = Path.Combine(outFile, file.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    File.WriteAllText(path, file.Content);
                }

                stderr.WriteLine($"Exported {files.Count} note(s) to {outFile}"
                    + (includeSecrets
                        ? " — WITH secrets. Vaults sync, so treat this folder as private."
                        : " — credential values are redacted; pass --include-secrets to keep them."));
                return ExitCodes.Ok;
            }
            default: throw new CliUsageException(Help);
        }
    }

    private static int ExportOpenApiFromHar(string harFile, string outFile, string? host, bool templateIds, TextWriter stderr)
    {
        if (!File.Exists(harFile)) throw new CliDataException($"No such file: {harFile}");

        Har har;
        try
        {
            har = HarReader.Parse(File.ReadAllText(harFile));
        }
        catch (HarFormatException ex)
        {
            throw new CliDataException(ex.Message);
        }

        var pc = HarToCollection.Group(har, new HarGroupOptions { Host = host, TemplateIds = templateIds });
        int count = CountRequests(pc);
        if (count == 0)
        {
            string reason = host is not null
                ? $"the --host '{host}' filter matched nothing, and/or every matching entry had a response status of 400 or above (those are skipped)."
                : "every entry had a response status of 400 or above (those are skipped).";
            throw new CliDataException($"Nothing to export from {Path.GetFileName(harFile)} — {reason}");
        }

        File.WriteAllText(outFile, OpenApiExporter.ToJson(pc));
        stderr.WriteLine($"Exported {count} operation(s) from {Path.GetFileName(harFile)} to {outFile}.");
        return ExitCodes.Ok;
    }

    private static int CountRequests(ParsedCollection pc) =>
        pc.Requests.Count + pc.Folders.Sum(CountRequests);
}
