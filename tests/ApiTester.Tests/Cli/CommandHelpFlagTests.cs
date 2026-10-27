using ApiTester.Cli;
using System.IO;

namespace ApiTester.Tests.Cli;

/// <summary><c>certapi &lt;command&gt; --help</c> and <c>-h</c> print that command's help, exactly
/// as <c>certapi help &lt;command&gt;</c> does, for every command.</summary>
public class CommandHelpFlagTests
{
    public static TheoryData<string> Commands => new()
    {
        "send", "certs", "run", "token", "fuzz", "bench", "sse", "ws", "selftest", "mock", "import",
        "export", "trust", "serve", "mcp", "grpc", "doctor", "proxy", "connections", "config", "completion", "update",
    };

    private static (int Code, string Out, string Err) Run(params string[] args)
    {
        var so = new StringWriter();
        var se = new StringWriter();
        int code = CliApp.Run(args, TextReader.Null, so, se);
        return (code, so.ToString(), se.ToString());
    }

    [Theory]
    [MemberData(nameof(Commands))]
    public void Double_dash_help_prints_the_commands_help(string command)
    {
        var r = Run(command, "--help");
        Assert.Equal(0, r.Code);
        Assert.Equal(CliApp.HelpFor(command)!.Trim(), r.Out.Trim());
        Assert.Equal("", r.Err);
    }

    [Theory]
    [MemberData(nameof(Commands))]
    public void Dash_h_prints_the_same_help(string command)
    {
        var r = Run(command, "-h");
        Assert.Equal(0, r.Code);
        Assert.Equal(CliApp.HelpFor(command)!.Trim(), r.Out.Trim());
    }

    [Fact]
    public void Help_after_other_arguments_still_wins()
    {
        var r = Run("send", "https://example.invalid/x", "-X", "POST", "--help");
        Assert.Equal(0, r.Code);
        Assert.StartsWith("Usage: certapi send", r.Out.Trim());
    }

    [Fact]
    public void Help_works_before_a_broken_configuration_is_read()
    {
        var r = Run("send", "--config", Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json"), "--help");
        Assert.Equal(0, r.Code);
        Assert.StartsWith("Usage: certapi send", r.Out.Trim());
    }

    [Fact]
    public void Capital_H_is_a_header_not_help()
    {
        Assert.False(CliApp.AsksForHelp(new[] { "https://x", "-H", "Accept: */*" }));
    }

    [Fact]
    public void Anything_after_a_double_dash_is_literal()
    {
        Assert.False(CliApp.AsksForHelp(new[] { "--", "--help" }));
        Assert.True(CliApp.AsksForHelp(new[] { "--HELP" }));
    }

    [Fact]
    public void The_overview_points_at_the_flag()
    {
        Assert.Contains("certapi <command> --help", CliApp.Usage);
    }
}
