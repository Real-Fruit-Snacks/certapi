using System.IO;
using ApiTester.Cli;
using ApiTester.Core;

namespace ApiTester.Tests.Cli;

/// <summary><c>certapi run --md</c> and <c>--md-vault</c>. The note's shape is proved in
/// <see cref="RunReportMarkdownTests"/> against the pure renderer; here the run is real, so what is
/// under test is that the command feeds the renderer the right results and files them correctly.</summary>
public class RunMarkdownCliTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("certapi-run-md-").FullName;

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    private CliServices Services() => new()
    {
        WorkingDirectory = _dir,
        UserConfigPath = null,
        // A CERTAPI_CONFIG set on the machine running the suite must not reach these tests.
        Environment = name => name == ConfigLoader.EnvironmentVariable ? null : System.Environment.GetEnvironmentVariable(name),
        LiveStatePath = Path.Combine(_dir, "state.json"),
        FileExists = path => path.StartsWith(_dir, StringComparison.OrdinalIgnoreCase) && File.Exists(path),
        IsGuiRunning = () => false,
    };

    private (int Code, string Out, string Err) Run(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        int code = CliApp.Run(args, stdout, stderr, services: Services());
        return (code, stdout.ToString(), stderr.ToString());
    }

    /// <summary>A workspace with two saved requests against the mock: one that will pass its
    /// assertion and one that will not.</summary>
    private string WriteWorkspace(string baseUrl)
    {
        var state = new AppState();
        var folder = new CollectionNode { Name = "Orders", IsFolder = true };

        var good = new RequestModel { Method = "GET", BaseUrl = baseUrl, Path = "/api/x" };
        good.Assertions.Add(new AssertionRule
        { Enabled = true, Target = AssertTarget.Status, Op = AssertOp.Equals, Value = "200" });
        folder.Children.Add(new CollectionNode { Name = "Get orders", IsFolder = false, Request = good });

        var bad = new RequestModel { Method = "GET", BaseUrl = baseUrl, Path = "/api/x" };
        bad.Assertions.Add(new AssertionRule
        { Enabled = true, Target = AssertTarget.Status, Op = AssertOp.Equals, Value = "418" });
        folder.Children.Add(new CollectionNode { Name = "Expects a teapot", IsFolder = false, Request = bad });

        state.Collections.Add(folder);
        string path = Path.Combine(_dir, "workspace.json");
        state.SaveTo(path);
        return path;
    }

    [Fact]
    public async Task The_report_records_what_passed_and_what_did_not()
    {
        await using var mock = MockServer.Start(0, MockTlsMode.Http);
        string workspace = WriteWorkspace(mock.BaseUrl);
        string file = Path.Combine(_dir, "report.md");

        var (code, _, error) = Run("run", "--all", "--workspace", workspace, "--md", file);

        Assert.Equal(1, code);                       // one request failed, and that is the run's verdict
        Assert.Contains("wrote the run report to", error);

        string note = File.ReadAllText(file);
        Assert.Contains("total: 2", note);
        Assert.Contains("passed: 1", note);
        Assert.Contains("failed: 1", note);
        Assert.Contains("outcome: fail", note);
        Assert.Contains("PASS", note);
        Assert.Contains("**FAIL**", note);
    }

    [Fact]
    public async Task A_failed_assertion_reaches_the_note_with_what_arrived()
    {
        await using var mock = MockServer.Start(0, MockTlsMode.Http);
        string workspace = WriteWorkspace(mock.BaseUrl);
        string file = Path.Combine(_dir, "report.md");

        Run("run", "--all", "--workspace", workspace, "--md", file);

        string note = File.ReadAllText(file);
        Assert.Contains("## Failures", note);
        Assert.Contains("Expects a teapot", note);
        Assert.Contains("| 200 |", note);            // the status that actually arrived
    }

    [Fact]
    public async Task A_vault_report_is_filed_under_runs()
    {
        await using var mock = MockServer.Start(0, MockTlsMode.Http);
        string workspace = WriteWorkspace(mock.BaseUrl);
        string vault = Path.Combine(_dir, "vault");

        Run("run", "--all", "--workspace", workspace, "--md-vault", vault);

        var written = Directory.GetFiles(Path.Combine(vault, "certapi", "runs"));
        Assert.Single(written);
        Assert.StartsWith("run-", Path.GetFileName(written[0]));
    }

    [Fact]
    public async Task A_chain_report_is_numbered_and_named_for_the_chain()
    {
        await using var mock = MockServer.Start(0, MockTlsMode.Http);

        var state = AppState.LoadFrom(WriteWorkspace(mock.BaseUrl));
        var steps = state.Collections[0].Children;
        state.Chains.Add(new RequestChain
        {
            Name = "Two steps",
            Steps = { new ChainStep { RequestId = steps[0].Id } }
        });
        string workspace = Path.Combine(_dir, "chained.json");
        state.SaveTo(workspace);

        string vault = Path.Combine(_dir, "vault");
        Run("run", "--chain", "Two steps", "--workspace", workspace, "--md-vault", vault);

        var written = Directory.GetFiles(Path.Combine(vault, "certapi", "runs"));
        string note = File.ReadAllText(Assert.Single(written));

        Assert.StartsWith("Two steps-", Path.GetFileName(written[0]));
        Assert.Contains("tags: [certapi/chain-run]", note);
        Assert.Contains("chain: \"Two steps\"", note);
        Assert.Contains("| 1 | [[Get orders]] |", note);   // links back into the catalogue
    }

    [Fact]
    public async Task A_report_that_cannot_be_written_warns_without_changing_the_verdict()
    {
        // A CI job must not turn green or red because of a folder permission.
        await using var mock = MockServer.Start(0, MockTlsMode.Http);
        string workspace = WriteWorkspace(mock.BaseUrl);

        var (code, _, error) = Run("run", "--all", "--workspace", workspace,
                                   "--md", Path.Combine(_dir, "bad\0name.md"));

        Assert.Equal(1, code);                       // still the run's own verdict
        Assert.Contains("could not write the run report", error);
    }

    [Fact]
    public void Naming_two_destinations_is_a_usage_error()
    {
        var (code, _, error) = Run("run", "--all", "--md", "a.md", "--md-vault", "b");

        Assert.Equal(2, code);
        Assert.Contains("pick one", error);
    }

    [Fact]
    public void The_secrets_flag_needs_a_report_to_apply_to()
    {
        var (code, _, error) = Run("run", "--all", "--md-include-secrets");

        Assert.Equal(2, code);
        Assert.Contains("only applies with --md", error);
    }
}
