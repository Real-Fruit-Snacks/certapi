using System.IO;
using ApiTester.Cli;
using ApiTester.Cli.Commands;

namespace ApiTester.Tests.Sweep;

public class CliParsingSweepTests
{
    [Fact]
    public void Nothing_after_a_bare_double_dash_is_an_option()
    {
        var a = new Args(new[] { "--", "-k", "--cert", "x" });
        Assert.False(a.Flag("--insecure"));
        Assert.Null(a.Value("--cert"));
        Assert.Equal(new[] { "-k", "--cert", "x" }, a.Positionals());
    }

    [Fact]
    public void A_value_written_with_equals_may_start_with_dashes()
    {
        var a = new Args(new[] { "--data=--literal", "--cors=--x" });
        Assert.Equal("--literal", a.Value("--data"));
        Assert.True(a.OptionalValue(out var cors, "--cors"));
        Assert.Equal("--x", cors);
        // Separated, it is still read as a missing value.
        Assert.Throws<CliUsageException>(() => new Args(new[] { "--data", "--literal" }).Value("--data"));
    }

    [Theory]
    [InlineData("mcp")]
    [InlineData("ws")]
    [InlineData("sse")]
    public void A_global_option_before_a_streaming_command_still_finds_the_command(string command)
    {
        var err = new StringWriter();
        CliApp.Run(new[] { "--no-config", command, "--help" }, new StringReader(""), new StringWriter(), err);
        Assert.DoesNotContain("Unknown command", err.ToString());
        var err2 = new StringWriter();
        var outw = new StringWriter();
        int code = CliApp.Run(new[] { "--no-config", command, "--help" }, new StringReader(""), outw, err2);
        Assert.Equal(0, code);
        Assert.Contains("Usage: certapi " + command, outw.ToString());
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("localhost", true)]
    [InlineData("0.0.0.0", false)]
    [InlineData("+", false)]
    [InlineData("10.0.0.5", false)]
    public void Serve_listen_addresses_that_stay_loopback_only(string? listen, bool loopback) =>
        Assert.Equal(loopback, ServeCommand.IsLoopbackListen(listen));
}
