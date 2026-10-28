using System.IO;
using ApiTester.Cli;
using ApiTester.Core;

namespace ApiTester.Tests.Cli;

/// <summary><c>certapi doctor --md</c> and <c>--md-vault</c>. The note's content is proved in
/// <see cref="DoctorMarkdownTests"/> against the pure renderer; what matters here is where the file
/// lands, what the flags refuse, and that a write failure never changes the diagnosis's own
/// verdict.</summary>
public class DoctorMarkdownCliTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("certapi-doctor-md-").FullName;
    private readonly List<string> _opened = new();

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    private CliServices Services() => new()
    {
        WorkingDirectory = _dir,
        UserConfigPath = null,
        // A CERTAPI_CONFIG set on the machine running the suite must not reach these tests.
        Environment = name => name == ConfigLoader.EnvironmentVariable ? null : System.Environment.GetEnvironmentVariable(name),
        LiveStatePath = Path.Combine(_dir, "state.json"),
        FileExists = path => path.StartsWith(_dir, StringComparison.OrdinalIgnoreCase) && File.Exists(path),
        // Injected so the suite never launches a markdown viewer on the machine running it.
        OpenFile = path => _opened.Add(path),
    };

    private (int Code, string Out, string Err) Run(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        int code = CliApp.Run(args, stdout, stderr, services: Services());
        return (code, stdout.ToString(), stderr.ToString());
    }

    [Fact]
    public async Task The_note_is_written_beside_the_terminal_output()
    {
        await using var mock = MockServer.Start(0, MockTlsMode.Http);
        string file = Path.Combine(_dir, "investigation.md");

        var (_, output, error) = Run("doctor", $"{mock.BaseUrl}/api/x", "--md", file);

        Assert.True(File.Exists(file));
        Assert.Contains("certapi doctor", output);          // the terminal still gets its report
        Assert.Contains("wrote the investigation note to", error);

        string note = File.ReadAllText(file);
        Assert.Contains("tags: [certapi/investigation]", note);
        Assert.Contains("| tcp | ok |", note);
    }

    [Fact]
    public async Task A_vault_note_is_filed_under_investigations_by_host_and_time()
    {
        await using var mock = MockServer.Start(0, MockTlsMode.Http);
        string vault = Path.Combine(_dir, "vault");

        Run("doctor", $"{mock.BaseUrl}/api/x", "--md-vault", vault);

        var written = Directory.GetFiles(Path.Combine(vault, "certapi", "investigations"));
        Assert.Single(written);
        Assert.StartsWith("127.0.0.1-", Path.GetFileName(written[0]));
    }

    [Fact]
    public async Task Two_runs_against_one_host_keep_both_notes()
    {
        // The point of an investigation vault: a diagnosis is history. Overwriting last week's
        // failure with this week's would destroy the record exactly as a pattern became visible.
        await using var mock = MockServer.Start(0, MockTlsMode.Http);
        string vault = Path.Combine(_dir, "vault");

        Run("doctor", $"{mock.BaseUrl}/api/x", "--md-vault", vault);
        await Task.Delay(1100);      // the name carries seconds, which is the resolution that separates them
        Run("doctor", $"{mock.BaseUrl}/api/x", "--md-vault", vault);

        Assert.Equal(2, Directory.GetFiles(Path.Combine(vault, "certapi", "investigations")).Length);
    }

    [Fact]
    public async Task Md_open_asks_the_shell_to_open_what_was_written()
    {
        await using var mock = MockServer.Start(0, MockTlsMode.Http);
        string file = Path.Combine(_dir, "investigation.md");

        Run("doctor", $"{mock.BaseUrl}/api/x", "--md", file, "--md-open");

        Assert.Equal(new[] { file }, _opened);
    }

    [Fact]
    public async Task A_note_that_cannot_be_written_warns_without_changing_the_verdict()
    {
        // The diagnosis already ran, and its result is what the user asked for. Losing that because
        // a folder was read-only would be the wrong trade.
        await using var mock = MockServer.Start(0, MockTlsMode.Http);

        var (code, _, error) = Run("doctor", $"{mock.BaseUrl}/api/x",
            "--md", Path.Combine(_dir, "nope\0bad.md"));

        Assert.Equal(0, code);                       // every stage still passed
        Assert.Contains("could not write the note", error);
    }

    [Fact]
    public void Naming_two_destinations_is_a_usage_error()
    {
        var (code, _, error) = Run("doctor", "https://example.invalid/",
            "--md", Path.Combine(_dir, "a.md"), "--md-vault", _dir);

        Assert.Equal(2, code);
        Assert.Contains("pick one", error);
    }

    [Theory]
    [InlineData("--md-open")]
    [InlineData("--include-secrets")]
    public void The_note_modifiers_need_a_note_to_modify(string flag)
    {
        var (code, _, error) = Run("doctor", "https://example.invalid/", flag);

        Assert.Equal(2, code);
        Assert.Contains("only apply with --md", error);
    }

    [Fact]
    public async Task An_api_key_in_the_url_does_not_reach_the_written_note()
    {
        // This program's standing rule, checked at the output path a user actually gets.
        await using var mock = MockServer.Start(0, MockTlsMode.Http);
        string file = Path.Combine(_dir, "investigation.md");

        Run("doctor", $"{mock.BaseUrl}/api/x?api_key=sekrit-42", "--md", file);

        string note = File.ReadAllText(file);
        Assert.DoesNotContain("sekrit-42", note);
        Assert.Contains("api_key=REDACTED", note);
    }
}
