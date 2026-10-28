using System.IO;
using ApiTester.Cli;
using ApiTester.Core;

namespace ApiTester.Tests.Cli;

/// <summary><c>certapi export markdown</c> — the workspace as a folder of linked notes. The layout
/// and escaping rules are proved in <see cref="MarkdownExportTests"/> against the pure builder;
/// what matters here is that the command writes what the builder produced, in the right places,
/// and refuses what it should.</summary>
public class ExportMarkdownCliTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("certapi-md-").FullName;

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    private CliServices Services() => new()
    {
        WorkingDirectory = _dir,
        UserConfigPath = null,
        // A CERTAPI_CONFIG set on the machine running the suite must not reach these tests.
        Environment = name => name == ConfigLoader.EnvironmentVariable ? null : System.Environment.GetEnvironmentVariable(name),
        LiveStatePath = Path.Combine(_dir, "state.json"),
        FileExists = path => path.StartsWith(_dir, StringComparison.OrdinalIgnoreCase) && File.Exists(path)
    };

    private (int Code, string Out, string Err) Run(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        int code = CliApp.Run(args, stdout, stderr, services: Services());
        return (code, stdout.ToString(), stderr.ToString());
    }

    /// <summary>A workspace file on disk with one folder, one request and one secret-bearing
    /// environment — enough to exercise the tree, the links and the redaction rule.</summary>
    private string WriteWorkspace()
    {
        var state = new AppState();
        var request = new RequestModel { Method = "GET", BaseUrl = "https://api.internal/orders", Path = "" };
        request.Headers.Add(new HeaderRow { Enabled = true, Name = "Authorization", Value = "Bearer sekrit-42" });
        var node = new CollectionNode { Name = "Get orders", IsFolder = false, Request = request };
        var folder = new CollectionNode { Name = "Orders", IsFolder = true };
        folder.Children.Add(node);
        state.Collections.Add(folder);

        var environment = new ApiEnvironment { Name = "Staging" };
        environment.Variables.Add(new Variable { Key = "token", Value = "sekrit-42", Secret = true });
        state.Environments.Add(environment);

        string path = Path.Combine(_dir, "workspace.json");
        state.SaveTo(path);
        return path;
    }

    [Fact]
    public void It_writes_the_tree_a_vault_can_browse()
    {
        string workspace = WriteWorkspace();
        string vault = Path.Combine(_dir, "vault");

        var (code, _, error) = Run("export", "markdown", "-o", vault, "--workspace", workspace, "--index");

        Assert.Equal(0, code);
        Assert.True(File.Exists(Path.Combine(vault, "certapi", "Orders", "Get orders.md")));
        Assert.True(File.Exists(Path.Combine(vault, "certapi", "environments", "Staging.md")));
        Assert.True(File.Exists(Path.Combine(vault, "certapi", "index.md")));
        Assert.Contains("note(s) to", error);
    }

    [Fact]
    public void A_token_does_not_reach_the_vault_without_the_explicit_flag()
    {
        // This program's standing rule, and the reason for it: a vault syncs, so a note is more
        // likely to leave the machine than any other artifact this product writes.
        string workspace = WriteWorkspace();
        string vault = Path.Combine(_dir, "vault");

        Run("export", "markdown", "-o", vault, "--workspace", workspace);

        foreach (var file in Directory.GetFiles(vault, "*.md", SearchOption.AllDirectories))
            Assert.DoesNotContain("sekrit-42", File.ReadAllText(file));

        string note = File.ReadAllText(Path.Combine(vault, "certapi", "Orders", "Get orders.md"));
        Assert.Contains("Authorization", note);        // that it is sent is still recorded
        Assert.Contains("redacted", note);
    }

    [Fact]
    public void With_include_secrets_the_values_are_written_and_the_warning_says_so()
    {
        string workspace = WriteWorkspace();
        string vault = Path.Combine(_dir, "vault");

        var (code, _, error) = Run("export", "markdown", "-o", vault, "--workspace", workspace, "--include-secrets");

        Assert.Equal(0, code);
        Assert.Contains("sekrit-42", File.ReadAllText(Path.Combine(vault, "certapi", "Orders", "Get orders.md")));
        Assert.Contains("Vaults sync", error);
    }

    [Fact]
    public void Re_exporting_overwrites_in_place_rather_than_accumulating_copies()
    {
        string workspace = WriteWorkspace();
        string vault = Path.Combine(_dir, "vault");

        Run("export", "markdown", "-o", vault, "--workspace", workspace);
        Run("export", "markdown", "-o", vault, "--workspace", workspace);

        Assert.Single(Directory.GetFiles(Path.Combine(vault, "certapi", "Orders")));
    }

    [Fact]
    public void The_subfolder_is_configurable()
    {
        string workspace = WriteWorkspace();
        string vault = Path.Combine(_dir, "vault");

        Run("export", "markdown", "-o", vault, "--workspace", workspace, "--into", "Reference");

        Assert.True(Directory.Exists(Path.Combine(vault, "Reference")));
        Assert.False(Directory.Exists(Path.Combine(vault, "certapi")));
    }

    [Fact]
    public void An_empty_workspace_is_a_data_error_not_an_empty_folder()
    {
        string path = Path.Combine(_dir, "empty.json");
        new AppState().SaveTo(path);

        var (code, _, error) = Run("export", "markdown", "-o", Path.Combine(_dir, "vault"), "--workspace", path);

        Assert.Equal(3, code);
        Assert.Contains("Nothing to export", error);
    }

    [Theory]
    [InlineData("--into", "x")]
    [InlineData("--index", null)]
    public void Markdown_only_options_are_refused_on_other_exports(string flag, string? value)
    {
        var args = new List<string> { "export", "workspace", "-o", Path.Combine(_dir, "w.json"), flag };
        if (value is not null) args.Add(value);

        var (code, _, error) = Run(args.ToArray());

        Assert.Equal(2, code);
        Assert.Contains("only apply to 'export markdown'", error);
    }

    [Fact]
    public void Include_secrets_is_accepted_for_markdown_and_still_refused_for_openapi()
    {
        var (code, _, error) = Run("export", "openapi", "-o", Path.Combine(_dir, "api.json"), "--include-secrets");

        Assert.Equal(2, code);
        Assert.Contains("export markdown", error);     // the message names where it IS allowed
    }
}
