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

public class CommandOptionSweepTests2
{
    [Fact]
    public void An_mcp_call_without_an_id_is_not_answered()
    {
        var output = new StringWriter();
        var input = new StringReader("{\"jsonrpc\":\"2.0\",\"method\":\"tools/list\"}\n{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\"}\n");
        CliApp.Run(new[] { "--no-config", "mcp" }, input, output, TextWriter.Null);
        var lines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var only = Assert.Single(lines);
        Assert.Contains("\"id\":1", only);
    }

    [Fact]
    public void A_har_replay_refuses_the_options_it_does_not_honour()
    {
        string har = Path.Combine(Path.GetTempPath(), $"r-{Guid.NewGuid():N}.har");
        File.WriteAllText(har, "{\"log\":{\"version\":\"1.2\",\"creator\":{\"name\":\"t\",\"version\":\"1\"},\"entries\":[]}}");
        try
        {
            var err = new StringWriter();
            int code = CliApp.Run(new[] { "--no-config", "run", har, "--md", "r.md", "--har", "out.har" }, TextWriter.Null, err);
            Assert.Equal(ExitCodes.Usage, code);
            Assert.Contains("--har, --md", err.ToString());
        }
        finally { File.Delete(har); }
    }

    [Theory]
    [InlineData("--connect-timeout")]
    [InlineData("--sni")]
    [InlineData("--pac")]
    [InlineData("--ca-file")]
    public void Completion_knows_transport_options_take_a_value(string option) =>
        Assert.True(CompletionCommand.TakesValue(McpCommand.Help, option));
}

public class Round2McpMockTests
{
    [Fact]
    public void Mcp_resources_withhold_credentials_typed_into_headers_queries_and_bodies()
    {
        var state = new ApiTester.Core.AppState();
        var request = new ApiTester.Core.RequestModel { Method = "POST", BaseUrl = "https://api.example", Path = "/login?api_key=QSECRET&page=1", Body = "{\"password\":\"BSECRET\",\"user\":\"alice\"}", ContentType = "application/json" };
        request.Headers.Add(new ApiTester.Core.HeaderRow { Name = "X-Api-Key", Value = "HSECRET" });
        request.Headers.Add(new ApiTester.Core.HeaderRow { Name = "Authorization", Value = "Bearer {{token}}" });
        state.Collections.Add(new ApiTester.Core.CollectionNode { Name = "c", IsFolder = true, Children = { new ApiTester.Core.CollectionNode { Name = "login", Request = request } } });
        string all = string.Join("\n", McpCommand.BuildResources(state).Select(r => r.Read()));
        foreach (var secret in new[] { "QSECRET", "BSECRET", "HSECRET" }) Assert.DoesNotContain(secret, all);
        Assert.Contains("{{token}}", all);   // a variable reference is not a secret
        Assert.Contains("alice", all);
    }

    [Fact]
    public async Task A_mock_scenario_requirement_also_guards_websocket_upgrades()
    {
        var scenario = ApiTester.Core.MockScenario.Parse("""{"require":{"bearer":"expected"},"routes":[]}""");
        await using var srv = ApiTester.Core.MockServer.Start(0, ApiTester.Core.MockTlsMode.Http, scenario: scenario);
        using var ws = new System.Net.WebSockets.ClientWebSocket();
        await Assert.ThrowsAnyAsync<System.Net.WebSockets.WebSocketException>(() =>
            ws.ConnectAsync(new Uri("ws://127.0.0.1:" + srv.Port + "/ws"), default));
    }
}
