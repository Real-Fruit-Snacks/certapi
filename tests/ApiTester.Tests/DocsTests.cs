using System.IO;
using System.Text.RegularExpressions;

namespace ApiTester.Tests;

/// <summary>Guards the documentation's internal consistency.
///
/// <para>These exist because both kinds of rot had actually happened. Three links in the CLI
/// reference pointed at anchors that did not exist — `#import` and `#export` because the two
/// commands shared one heading called "import / export", and `#trust` because a real command had
/// no section at all. And two whole programs' worth of flags reached the wiki without ever being
/// added to the reference page, which is the page people search first.</para>
///
/// <para>A missing page or a dead anchor is invisible in review and obvious to a reader, which is
/// exactly the kind of thing a test should hold.</para></summary>
public class DocsTests
{
    private static string? Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "wiki"))) return dir.FullName;
            dir = dir.Parent;
        }
        return null;   // not a source checkout; there is nothing to check
    }

    private static IEnumerable<string> DocFiles(string root)
    {
        foreach (var file in Directory.GetFiles(Path.Combine(root, "wiki"), "*.md")) yield return file;
        string readme = Path.Combine(root, "README.md");
        if (File.Exists(readme)) yield return readme;
    }

    /// <summary>GitHub's heading-to-anchor rule: lower-case, then **drop** every character that is
    /// not a letter, digit, space or hyphen, then turn spaces into hyphens.
    ///
    /// <para>Dropping rather than collapsing is the part that matters, and getting it wrong the
    /// other way produced false results here first. A heading like
    /// <c>"It's slow" — reading the timings</c> loses its quotes, apostrophe and dash but keeps the
    /// spaces that surrounded the dash, so the real anchor has a *double* hyphen:
    /// <c>its-slow--reading-the-timings</c>. A rule that collapsed punctuation to one hyphen would
    /// call the correct link broken.</para></summary>
    private static string Anchor(string heading)
    {
        var kept = new string(heading.ToLowerInvariant()
            .Where(c => char.IsLetterOrDigit(c) || c == ' ' || c == '-').ToArray());
        return kept.Replace(' ', '-');
    }

    [Fact]
    public void Every_link_to_a_wiki_page_points_at_a_page_that_exists()
    {
        if (Root() is not { } root) return;

        var pages = Directory.GetFiles(Path.Combine(root, "wiki"), "*.md")
                             .Select(Path.GetFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var broken = new List<string>();

        foreach (var file in DocFiles(root))
        {
            string text = File.ReadAllText(file);
            foreach (Match match in Regex.Matches(text, @"\]\((\d\d-[A-Za-z0-9-]+\.md)(?:#[a-z0-9-]+)?\)"))
                if (!pages.Contains(match.Groups[1].Value))
                    broken.Add($"{Path.GetFileName(file)} → {match.Groups[1].Value}");
        }

        Assert.Empty(broken);
    }

    [Fact]
    public void Every_intra_page_anchor_points_at_a_heading_that_exists()
    {
        if (Root() is not { } root) return;

        var broken = new List<string>();
        foreach (var file in DocFiles(root))
        {
            string text = File.ReadAllText(file);
            var headings = Regex.Matches(text, @"^#{1,4} (.+)$", RegexOptions.Multiline)
                                .Select(m => Anchor(m.Groups[1].Value)).ToHashSet();

            foreach (Match match in Regex.Matches(text, @"\]\(#([a-z0-9-]+)\)"))
                if (!headings.Contains(match.Groups[1].Value))
                    broken.Add($"{Path.GetFileName(file)} → #{match.Groups[1].Value}");
        }

        Assert.Empty(broken);
    }

    [Fact]
    public void Every_anchor_into_another_page_points_at_a_heading_that_exists()
    {
        // A link into another page's section is the easiest kind to get wrong and the hardest to
        // notice: the page opens, so it looks like it worked, and it silently lands at the top.
        if (Root() is not { } root) return;

        // Keyed on the wiki pages only: a cross-page anchor always targets a numbered page, and
        // both the repository root and the wiki folder have a README.md, which would collide.
        var headings = Directory.GetFiles(Path.Combine(root, "wiki"), "*.md").ToDictionary(
            f => Path.GetFileName(f),
            f => Regex.Matches(File.ReadAllText(f), @"^#{1,4} (.+)$", RegexOptions.Multiline)
                      .Select(m => Anchor(m.Groups[1].Value)).ToHashSet(),
            StringComparer.OrdinalIgnoreCase);

        var broken = new List<string>();
        foreach (var file in DocFiles(root))
            foreach (Match match in Regex.Matches(File.ReadAllText(file),
                                                  @"\]\((\d\d-[A-Za-z0-9-]+\.md)#([a-z0-9-]+)\)"))
            {
                string page = match.Groups[1].Value, anchor = match.Groups[2].Value;
                if (!headings.TryGetValue(page, out var found) || !found.Contains(anchor))
                    broken.Add($"{Path.GetFileName(file)} → {page}#{anchor}");
            }

        Assert.Empty(broken);
    }

    [Fact]
    public void The_table_of_contents_lists_every_page()
    {
        // The contents page is the one thing that must never fall behind, because it is how a
        // reader discovers a page exists at all. Two pages had already slipped into being
        // parenthetical "also" notes in the old index; this makes that impossible to repeat.
        if (Root() is not { } root) return;
        string contents = Path.Combine(root, "wiki", "00-Table-of-Contents.md");
        if (!File.Exists(contents)) return;

        string text = File.ReadAllText(contents);
        var missing = Directory.GetFiles(Path.Combine(root, "wiki"), "*.md")
            .Select(Path.GetFileName)
            .Where(name => name is not null
                        && !name.StartsWith("00-", StringComparison.Ordinal)
                        && !name.Equals("README.md", StringComparison.OrdinalIgnoreCase)
                        && !text.Contains($"({name})", StringComparison.Ordinal)
                        && !text.Contains($"({name}#", StringComparison.Ordinal))
            .ToList();

        Assert.Empty(missing);
    }

    /// <summary>Which command each command file belongs to. Kept beside the test rather than
    /// derived, because the mapping is the one thing here a reader should be able to check by
    /// eye — and a file that is added without an entry fails the test below rather than being
    /// silently skipped.</summary>
    private static readonly Dictionary<string, string> CommandFiles = new(StringComparer.Ordinal)
    {
        ["SendCommand.cs"] = "send", ["RunCommand.cs"] = "run", ["FuzzCommand.cs"] = "fuzz",
        ["BenchCommand.cs"] = "bench", ["SseCommand.cs"] = "sse", ["WsCommand.cs"] = "ws",
        ["CertsCommand.cs"] = "certs", ["SelfTestCommand.cs"] = "selftest", ["MockCommand.cs"] = "mock",
        ["ImportCommand.cs"] = "import", ["ExportCommand.cs"] = "export", ["TrustCommand.cs"] = "trust",
        ["ServeCommand.cs"] = "serve", ["GrpcCommand.cs"] = "grpc", ["McpCommand.cs"] = "mcp",
        ["DoctorCommand.cs"] = "doctor", ["ProxyCommand.cs"] = "proxy",
        ["ConnectionsCommand.cs"] = "connections", ["TokenCommand.cs"] = "token",
        ["ConfigCommand.cs"] = "config", ["CompletionCommand.cs"] = "completion",
    };

    /// <summary>Every option literal the argument parser is asked for, per file.</summary>
    private static Dictionary<string, HashSet<string>> ParsedOptions(string root)
    {
        var result = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        string cli = Path.Combine(root, "src", "ApiTester.Cli");
        foreach (var file in Directory.GetFiles(cli, "*.cs", SearchOption.AllDirectories))
        {
            string text = File.ReadAllText(file);
            var found = new HashSet<string>(StringComparer.Ordinal);
            // Only the calls that actually consume an option; a literal elsewhere in the file is
            // not a flag, and treating it as one would make this test cry wolf.
            foreach (Match call in Regex.Matches(text, @"args\.(?:Flag|FlagOrNull|Value|Values)\(([^)]*)\)"))
                foreach (Match literal in Regex.Matches(call.Groups[1].Value, @"""(-{1,2}[A-Za-z][A-Za-z0-9.\-]*)"""))
                    found.Add(literal.Groups[1].Value);
            if (found.Count > 0) result[Path.GetFileName(file)] = found;
        }
        return result;
    }

    private static bool Mentions(string text, string option) =>
        Regex.IsMatch(text, @"(?<![A-Za-z0-9.\-])" + Regex.Escape(option) + @"(?![A-Za-z0-9.\-])");

    [Fact]
    public void Every_option_the_parser_accepts_is_documented_in_the_reference()
    {
        // The promise this page makes is "every command, every option, every default". A flag that
        // the parser accepts but the page never mentions is the exact way that promise rots, and it
        // is invisible without reading 149 options against 20 sections by hand.
        //
        // Ground truth is the PARSER, not the help text: help can lag behind what is accepted, and
        // an option the tool takes but nobody documented is precisely what this should catch.
        if (Root() is not { } root) return;
        string reference = Path.Combine(root, "wiki", "21-CLI-Reference.md");
        if (!Directory.Exists(Path.Combine(root, "src", "ApiTester.Cli")) || !File.Exists(reference)) return;

        var parsed = ParsedOptions(root);
        Assert.NotEmpty(parsed);   // the pattern still finds the parser calls at all

        string doc = File.ReadAllText(reference);

        // Options a command owns must appear in that command's own section, or in the shared
        // blocks every section refers to (certificate, transport, variables, global).
        var sections = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(doc, @"^## (\S+)\s*$", RegexOptions.Multiline))
        {
            int start = m.Index + m.Length;
            int next = doc.IndexOf("\n## ", start, StringComparison.Ordinal);
            sections[m.Groups[1].Value] = doc[start..(next > 0 ? next : doc.Length)];
        }
        int firstCommand = doc.IndexOf("\n## send", StringComparison.Ordinal);
        string shared = firstCommand > 0 ? doc[..firstCommand] : doc;

        var undocumented = new List<string>();
        foreach (var (file, options) in parsed)
        {
            if (!CommandFiles.TryGetValue(file, out var command))
            {
                // A shared block (transport, certificate flags). Anywhere on the page will do.
                foreach (var option in options)
                    if (!Mentions(doc, option)) undocumented.Add($"{file}: {option}");
                continue;
            }
            if (!sections.TryGetValue(command, out var body))
            {
                undocumented.Add($"{command}: has no section");
                continue;
            }
            foreach (var option in options)
                if (!Mentions(body, option) && !Mentions(shared, option))
                    undocumented.Add($"{command}: {option}");
        }

        Assert.Empty(undocumented);
    }

    [Fact]
    public void Every_place_that_builds_an_archive_entry_redacts_the_url_it_records()
    {
        // Three separate places build HAR entries — a send, the gateway's recorder, and the desktop
        // application's network export — and each one leaked the credential in its URL until it was
        // found individually. The shape of that bug is "a rule only some of the builders applied",
        // so a fourth builder must not be able to repeat it silently.
        if (Root() is not { } root) return;

        var offenders = new List<string>();
        foreach (var file in Directory.GetFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")) continue;
            string text = File.ReadAllText(file);
            if (!text.Contains("new HarRequest", StringComparison.Ordinal)) continue;

            // Whoever builds a HarRequest must route its Url through the shared rule.
            if (!text.Contains("RecordedUrl", StringComparison.Ordinal))
                offenders.Add(Path.GetFileName(file));
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void Every_command_file_is_accounted_for_by_the_option_check()
    {
        // Guards the guard: a new command file with no entry in CommandFiles would have its options
        // checked against the whole page rather than its own section, which is a weaker check that
        // would pass while the section was missing entirely.
        if (Root() is not { } root) return;
        string cli = Path.Combine(root, "src", "ApiTester.Cli", "Commands");
        if (!Directory.Exists(cli)) return;

        var unmapped = Directory.GetFiles(cli, "*Command.cs")
            .Select(Path.GetFileName)
            .Where(name => name is not null && !CommandFiles.ContainsKey(name))
            .ToList();

        Assert.Empty(unmapped);
    }

    [Fact]
    public void Every_command_the_cli_dispatches_has_a_section_in_the_reference()
    {
        // The reference page is where people look first, and it had fallen behind by two whole
        // programs. Reading the dispatch table rather than a hand-kept list is what keeps this
        // honest: a new command cannot ship undocumented.
        if (Root() is not { } root) return;
        string cliApp = Path.Combine(root, "src", "ApiTester.Cli", "CliApp.cs");
        string reference = Path.Combine(root, "wiki", "21-CLI-Reference.md");
        if (!File.Exists(cliApp) || !File.Exists(reference)) return;

        var commands = Regex.Matches(File.ReadAllText(cliApp), @"^\s+""([a-z]+)"" => Commands\.",
                                     RegexOptions.Multiline)
                            .Select(m => m.Groups[1].Value)
                            .Distinct(StringComparer.Ordinal)
                            .ToList();
        Assert.NotEmpty(commands);   // the pattern still matches the dispatch table

        string text = File.ReadAllText(reference);
        var headings = Regex.Matches(text, @"^## (.+)$", RegexOptions.Multiline)
                            .Select(m => m.Groups[1].Value.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var undocumented = commands.Where(c => !headings.Contains(c)).ToList();
        Assert.Empty(undocumented);
    }
}
