using System.IO;
using System.Text.Json;
using ApiTester.Cli;
using ApiTester.Cli.Commands;
using ApiTester.Cli.Mcp;
using ApiTester.Core;

namespace ApiTester.Tests.Cli;

/// <summary>ws_exchange and sse_read, --allow-any and --print-config.</summary>
public class McpStreamToolTests
{
    private static ToolDef Tool(IReadOnlyList<ToolDef> tools, string name) => tools.First(t => t.Name == name);

    private static JsonElement Args(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static IReadOnlyList<ToolDef> Tools(System.Security.Cryptography.X509Certificates.X509Certificate2? cert, params string[] allow) =>
        McpCommand.BuildTools(cert, new HostAllowlist(allow), insecure: true, timeout: 20, includeLocalMachine: false,
            workspace: null, noAutoToken: true,
            new CliServices { LiveStatePath = Path.Combine(Path.GetTempPath(), $"certapi-mcp-{Guid.NewGuid():N}.json") });

    [Fact]
    public async Task Ws_exchange_sends_and_returns_what_comes_back()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("Client", ca, false, true);
        await using var server = await LoopbackMtlsServer.StartWebSocketEchoAsync(serverCert, clientCert.Thumbprint!);
        var tools = Tools(clientCert, new Uri(server.WebSocketUrl).Host);

        var result = await Task.Run(() => Tool(tools, "ws_exchange").Handler(
            Args($$"""{"url":"{{server.WebSocketUrl}}","messages":["hello","world"],"maxMessages":2}""")));

        Assert.False(result.IsError, result.Json);
        using var doc = JsonDocument.Parse(result.Json);
        var texts = doc.RootElement.GetProperty("messages").EnumerateArray().Select(m => m.GetProperty("text").GetString()).ToList();
        Assert.Equal(new[] { "hello", "world" }, texts);
        Assert.Equal("count", doc.RootElement.GetProperty("ended").GetString());
        Assert.Equal(2, doc.RootElement.GetProperty("sent").GetInt32());
    }

    [Fact]
    public async Task Ws_exchange_stops_listening_after_the_time_given()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("Client", ca, false, true);
        await using var server = await LoopbackMtlsServer.StartWebSocketEchoAsync(serverCert, clientCert.Thumbprint!);
        var tools = Tools(clientCert, new Uri(server.WebSocketUrl).Host);

        var result = await Task.Run(() => Tool(tools, "ws_exchange").Handler(
            Args($$"""{"url":"{{server.WebSocketUrl}}","messages":["only one"],"maxMessages":5,"seconds":0.5}""")));

        using var doc = JsonDocument.Parse(result.Json);
        Assert.Equal("time", doc.RootElement.GetProperty("ended").GetString());
        Assert.Single(doc.RootElement.GetProperty("messages").EnumerateArray());
    }

    [Fact]
    public void The_allowlist_judges_a_websocket_by_its_host()
    {
        var tools = Tools(null, "internal.corp");
        var refused = Tool(tools, "ws_exchange").Handler(Args("""{"url":"wss://evil.example/socket"}"""));
        Assert.True(refused.IsError);
        Assert.Contains("not allowed", refused.Json);

        var wrongScheme = Tool(tools, "ws_exchange").Handler(Args("""{"url":"https://internal.corp/socket"}"""));
        Assert.True(wrongScheme.IsError);
        Assert.Contains("ws://", wrongScheme.Json);

        var sse = Tool(tools, "sse_read").Handler(Args("""{"url":"https://evil.example/events"}"""));
        Assert.True(sse.IsError);
        Assert.Contains("not allowed", sse.Json);
    }

    [Fact]
    public async Task Sse_read_returns_events_and_the_last_id()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("Client", ca, false, true);
        var events = new List<(string?, string)> { ("greeting", "hello"), (null, "two"), (null, "three") };
        await using var server = await LoopbackMtlsServer.StartSseAsync(serverCert, clientCert.Thumbprint!, events);
        var tools = Tools(clientCert, new Uri(server.BaseUrl).Host);

        var result = await Task.Run(() => Tool(tools, "sse_read").Handler(Args($$"""{"url":"{{server.BaseUrl}}","maxEvents":2}""")));

        Assert.False(result.IsError, result.Json);
        using var doc = JsonDocument.Parse(result.Json);
        var list = doc.RootElement.GetProperty("events").EnumerateArray().ToList();
        Assert.Equal(2, list.Count);
        Assert.Equal("greeting", list[0].GetProperty("event").GetString());
        Assert.Equal("hello", list[0].GetProperty("data").GetString());
        Assert.Equal("count", doc.RootElement.GetProperty("ended").GetString());
    }

    [Fact]
    public void Limits_out_of_range_are_refused()
    {
        var tools = Tools(null, "internal.corp");
        Assert.True(Tool(tools, "sse_read").Handler(Args("""{"url":"https://internal.corp/e","maxEvents":-1}""")).IsError);
        Assert.True(Tool(tools, "sse_read").Handler(Args("""{"url":"https://internal.corp/e","seconds":0}""")).IsError);
        Assert.True(Tool(tools, "ws_exchange").Handler(Args("""{"url":"wss://internal.corp/s","messages":[1]}""")).IsError);
    }

    [Theory]
    [InlineData("wss://api.corp/socket", "https://api.corp/socket")]
    [InlineData("ws://api.corp:8080/s", "http://api.corp:8080/s")]
    [InlineData("wss://api.corp:443/s", "https://api.corp/s")]
    [InlineData("https://api.corp/x", "https://api.corp/x")]
    public void A_websocket_address_is_judged_in_its_http_form(string ws, string http) =>
        Assert.Equal(http, McpStreamTools.HttpForm(ws));
}

public class McpLaunchOptionTests
{
    private static (int code, string stdout, string stderr) Run(params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        int code = CliApp.Run(args, new StringReader(""), o, e, null, new CliServices { LiveStatePath = Path.Combine(Path.GetTempPath(), $"certapi-none-{Guid.NewGuid():N}.json") });
        return (code, o.ToString(), e.ToString());
    }

    [Fact]
    public void Allow_any_and_allow_contradict()
    {
        var (code, _, err) = Run("mcp", "--allow-any", "--allow", "api.corp");
        Assert.Equal(2, code);
        Assert.Contains("--allow-any", err);
    }

    [Fact]
    public void Print_config_writes_each_hosts_shape_and_starts_nothing()
    {
        var (code, json, err) = Run("mcp", "--allow", "api.corp", "--timeout", "30", "--print-config", "claude");
        Assert.Equal(0, code);
        using var claude = JsonDocument.Parse(json);
        var server = claude.RootElement.GetProperty("mcpServers").GetProperty("certapi");
        var args = server.GetProperty("args").EnumerateArray().Select(a => a.GetString()).ToList();
        Assert.Equal(new[] { "mcp", "--allow", "api.corp", "--timeout", "30" }, args);
        Assert.False(string.IsNullOrEmpty(server.GetProperty("command").GetString()));
        Assert.DoesNotContain("ready", err);

        var (_, vscodeJson, _) = Run("mcp", "--allow", "api.corp", "--print-config=vscode");
        using var vscode = JsonDocument.Parse(vscodeJson);
        var vs = vscode.RootElement.GetProperty("servers").GetProperty("certapi");
        Assert.Equal("stdio", vs.GetProperty("type").GetString());
        Assert.DoesNotContain("--print-config", vs.GetProperty("args").ToString());
    }

    [Fact]
    public void Print_config_warns_about_an_open_allowlist_and_refuses_unknown_hosts()
    {
        var (_, _, err) = Run("mcp", "--print-config", "cursor");
        Assert.Contains("any host", err);
        var (code, _, unknown) = Run("mcp", "--print-config", "emacs");
        Assert.Equal(2, code);
        Assert.Contains("claude, vscode or cursor", unknown);
    }

    [Fact]
    public void Print_config_makes_paths_absolute_and_will_not_write_secrets()
    {
        string json = McpHostConfig.Render("claude", new[] { "--workspace", "suite.json", "--print-config", "claude" },
            "/opt/certapi/certapi", "/work");
        using var doc = JsonDocument.Parse(json);
        var args = doc.RootElement.GetProperty("mcpServers").GetProperty("certapi").GetProperty("args")
            .EnumerateArray().Select(a => a.GetString()).ToList();
        Assert.Equal(Path.GetFullPath("suite.json", "/work"), args[2]);
        Assert.Equal("/opt/certapi/certapi", doc.RootElement.GetProperty("mcpServers").GetProperty("certapi").GetProperty("command").GetString());

        var ex = Assert.Throws<CliUsageException>(() =>
            McpHostConfig.Render("claude", new[] { "--cert-pin", "1234" }, "/opt/certapi/certapi"));
        Assert.Contains("CERTAPI_CERT_PIN", ex.Message);
        Assert.Equal("certapi", JsonDocument.Parse(McpHostConfig.Render("cursor", Array.Empty<string>(), "/usr/bin/dotnet"))
            .RootElement.GetProperty("mcpServers").GetProperty("certapi").GetProperty("command").GetString());
    }
}
