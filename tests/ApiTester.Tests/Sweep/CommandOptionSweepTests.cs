using System.IO;
using ApiTester.Cli;
using ApiTester.Cli.Commands;

namespace ApiTester.Tests.Sweep;

public class CommandOptionSweepTests
{
    private static (int Code, string Err) Run(params string[] args)
    {
        var err = new StringWriter();
        int code = CliApp.Run(args, TextWriter.Null, err);
        return (code, err.ToString());
    }

    [Fact]
    public void Trust_add_refuses_two_pins_at_once()
    {
        string ws = Path.Combine(Path.GetTempPath(), $"ws-{Guid.NewGuid():N}.json");
        new ApiTester.Core.AppState().SaveTo(ws);
        try
        {
            var (code, err) = Run("trust", "add", "api.corp", "--ca", new string('A', 40), "--thumbprint", new string('B', 40), "--workspace", ws);
            Assert.Equal(ExitCodes.Usage, code);
            Assert.Contains("Give one pin", err);
            (code, err) = Run("trust", "add", "api.corp", "--thumbprint", new string('B', 40), "--ca", "--workspace", ws);
            Assert.Equal(ExitCodes.Usage, code);
            Assert.Contains("--from-url", err);
        }
        finally { File.Delete(ws); }
    }

    [Fact]
    public void Token_client_auth_must_be_body_or_basic()
    {
        var (code, err) = Run("--no-config", "token", "--token-url", "https://idp.example/token", "--client-id", "c", "--client-secret", "s", "--client-auth", "basci");
        Assert.Equal(ExitCodes.Usage, code);
        Assert.Contains("body or basic", err);
    }

    [Theory]
    [InlineData("10k", 10 * 1024L)]
    [InlineData("2g", 2L * 1024 * 1024 * 1024)]
    [InlineData("9999999999g", null)]
    public void Serve_max_body_never_wraps_around(string raw, long? expected) =>
        Assert.Equal(expected, ServeCommand.ParseSize(raw));
}
