using ApiTester.Cli;
using ApiTester.Cli.Commands;

namespace ApiTester.Tests.Cli;

public class HelpTextTests
{
    public static TheoryData<string, string> Helps => new()
    {
        { "send", SendCommand.Help },
        { "curl", CurlCommand.Help },
        { "run", RunCommand.Help },
        { "certs", CertsCommand.Help },
        { "selftest", SelfTestCommand.Help },
        { "import", ImportCommand.Help },
        { "export", ExportCommand.Help },
        { "serve", ServeCommand.Help },
        { "mcp", McpCommand.Help },
        { "trust", TrustCommand.Help },
        { "bench", BenchCommand.Help },
        { "grpc", GrpcCommand.Help },
    };

    [Theory]
    [MemberData(nameof(Helps))]
    public void Every_command_help_has_examples_and_the_global_flags(string name, string help)
    {
        Assert.Contains("Examples:", help);
        Assert.Contains($"certapi {name}", help);
        Assert.Contains("--debug", help);
    }

    [Fact]
    public void The_overview_has_a_quick_start_and_global_options()
    {
        Assert.Contains("Examples:", CliApp.Usage);
        Assert.Contains("--debug", CliApp.Usage);
        Assert.Contains("--log-file", CliApp.Usage);
    }

    [Theory]
    [InlineData("send")]
    [InlineData("run")]
    [InlineData("mcp")]
    public void Token_aware_commands_document_no_auto_token(string name)
    {
        var help = name switch { "send" => SendCommand.Help, "run" => RunCommand.Help, _ => McpCommand.Help };
        Assert.Contains("--no-auto-token", help);
    }
}
