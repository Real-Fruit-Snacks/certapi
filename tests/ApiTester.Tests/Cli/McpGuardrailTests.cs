using System.IO;
using System.Text.Json;
using ApiTester.Cli;
using ApiTester.Cli.Commands;
using ApiTester.Cli.Mcp;
using ApiTester.Core;

namespace ApiTester.Tests.Cli;

/// <summary>What the agent may and may not do with the tools: fill values into a saved request but
/// not move it, never read a secret out of a refusal, not name another host in a Host header, not
/// split a request with a line break, and not pull an unbounded body into memory.</summary>
public class McpGuardrailTests
{
    private static ToolDef Tool(IReadOnlyList<ToolDef> tools, string name) => tools.First(t => t.Name == name);

    /// <summary>The result as readable text: the server escapes quotes and the like in its JSON.</summary>
    private static string Text(ToolResult r) =>
        System.Text.Json.Nodes.JsonNode.Parse(r.Json)!.ToJsonString(new JsonSerializerOptions
        { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    private static JsonElement Args(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static string Workspace(Action<AppState> fill)
    {
        var state = new AppState();
        fill(state);
        var ws = Path.Combine(Path.GetTempPath(), $"certapi-mcp-guard-{Guid.NewGuid():N}.json");
        state.SaveTo(ws);
        return ws;
    }

    private static void Saved(AppState state, string id, string name, RequestModel request) =>
        state.Collections.Add(new CollectionNode { Id = id, Name = name, IsFolder = false, Request = request });

    private static void ProdEnvironment(AppState state, string baseUrl)
    {
        state.Environments.Add(new ApiEnvironment
        {
            Id = "e1", Name = "Prod",
            Variables =
            {
                new Variable { Key = "base", Value = baseUrl },
                new Variable { Key = "apikey", Value = "ENV-SECRET-KEY-123", Secret = true },
                new Variable { Key = "id", Value = "7" }
            }
        });
    }

    private static IReadOnlyList<ToolDef> Tools(string ws, params string[] allow) =>
        McpCommand.BuildTools(null, new HostAllowlist(allow), insecure: false, timeout: 5,
            includeLocalMachine: false, workspace: ws, noAutoToken: true, new CliServices { LiveStatePath = ws });

    [Fact]
    public void Vars_cannot_move_a_saved_request_to_another_host()
    {
        var ws = Workspace(s =>
        {
            ProdEnvironment(s, "http://127.0.0.1:9");
            Saved(s, "r1", "orders", new RequestModel { Method = "GET", Path = "{{base}}/orders", AuthType = "Bearer", AuthSecret = "BEARER-SECRET" });
            s.Chains.Add(new RequestChain { Name = "flow", EnvironmentName = "Prod", Steps = { new ChainStep { RequestId = "r1" } } });
        });
        try
        {
            var tools = Tools(ws);   // no allow-list: the worst case
            var saved = Tool(tools, "run_saved").Handler(Args("""{"path":"orders","env":"Prod","vars":{"base":"http://127.0.0.2:9"}}"""));
            Assert.True(saved.IsError);
            Assert.Contains("vars may not change where a saved request is sent", Text(saved));
            Assert.Contains("127.0.0.2", Text(saved));
            Assert.DoesNotContain("BEARER-SECRET", Text(saved));

            var chain = Tool(tools, "run_chain").Handler(Args("""{"name":"flow","env":"Prod","vars":{"base":"https://127.0.0.1:9"}}"""));
            Assert.Contains("vars may not change where a saved request is sent", Text(chain));   // a scheme change counts too
        }
        finally { File.Delete(ws); }
    }

    [Fact]
    public void Vars_may_still_fill_in_values_that_keep_the_host()
    {
        var ws = Workspace(s =>
        {
            ProdEnvironment(s, "http://127.0.0.1:9");
            Saved(s, "r1", "order", new RequestModel { Method = "GET", Path = "{{base}}/orders/{{id}}", AuthType = "None" });
        });
        try
        {
            var r = Tool(Tools(ws), "run_saved").Handler(Args("""{"path":"order","env":"Prod","vars":{"id":"42","base":"http://127.0.0.1:9"}}"""));
            Assert.DoesNotContain("vars may not change", Text(r));   // it went on to connect (and fail: nothing listens on :9)
        }
        finally { File.Delete(ws); }
    }

    [Fact]
    public void A_saved_request_whose_host_comes_only_from_vars_is_refused()
    {
        var ws = Workspace(s => Saved(s, "r1", "loose", new RequestModel { Method = "GET", Path = "{{host}}/x", AuthType = "None" }));
        try
        {
            var r = Tool(Tools(ws), "run_saved").Handler(Args("""{"path":"loose","vars":{"host":"http://127.0.0.2:9"}}"""));
            Assert.Contains("put the host in an environment", Text(r));
        }
        finally { File.Delete(ws); }
    }

    [Fact]
    public void A_refusal_names_the_host_and_never_the_resolved_url()
    {
        var ws = Workspace(s =>
        {
            ProdEnvironment(s, "http://blocked.invalid");
            var r = new RequestModel { Method = "GET", Path = "{{base}}/k/{{apikey}}", AuthType = "None" };
            r.QueryParams.Add(new ParamRow { Key = "api_key", Value = "{{apikey}}" });
            Saved(s, "r1", "keyed", r);
        });
        try
        {
            var result = Tool(Tools(ws, "allowed.invalid"), "run_saved").Handler(Args("""{"path":"keyed","env":"Prod"}"""));
            Assert.Contains("host 'blocked.invalid' is not allowed", Text(result));
            Assert.DoesNotContain("ENV-SECRET-KEY-123", Text(result));
        }
        finally { File.Delete(ws); }
    }

    [Fact]
    public void Send_request_refuses_a_host_header_that_names_another_host()
    {
        var tools = McpCommand.BuildTools(null, new HostAllowlist(Array.Empty<string>()), false, 5, false, null, true, new CliServices());
        var other = Tool(tools, "send_request").Handler(Args("""{"url":"http://127.0.0.1:9/x","headers":{"Host":"other.internal"}}"""));
        Assert.True(other.IsError);
        Assert.Contains("Host header must name the URL's own host", Text(other));

        var same = Tool(tools, "send_request").Handler(Args("""{"url":"http://127.0.0.1:9/x","headers":{"host":"127.0.0.1:9"}}"""));
        Assert.DoesNotContain("Host header", Text(same));
    }

    [Fact]
    public void A_saved_host_header_that_differs_from_the_url_is_refused()
    {
        var ws = Workspace(s =>
        {
            var r = new RequestModel { Method = "GET", Path = "http://127.0.0.1:9/x", AuthType = "None" };
            r.Headers.Add(new HeaderRow { Name = "Host", Value = "{{h}}" });
            Saved(s, "r1", "hosted", r);
        });
        try
        {
            var r = Tool(Tools(ws), "run_saved").Handler(Args("""{"path":"hosted","vars":{"h":"other.internal"}}"""));
            Assert.Contains("Host header must name the URL's own host", Text(r));
        }
        finally { File.Delete(ws); }
    }

    [Theory]
    [InlineData("v\r\nX-Injected: 1")]
    [InlineData("v\nX-Injected: 1")]
    [InlineData("v\0")]
    public void Send_request_refuses_a_header_value_with_a_line_break(string value)
    {
        var tools = McpCommand.BuildTools(null, new HostAllowlist(Array.Empty<string>()), false, 5, false, null, true, new CliServices());
        var r = Tool(tools, "send_request").Handler(Args(JsonSerializer.Serialize(new { url = "http://127.0.0.1:9/x", headers = new Dictionary<string, string> { ["X-A"] = value } })));
        Assert.True(r.IsError);
        Assert.Contains("line break", Text(r));
    }

    [Fact]
    public void Vars_with_a_line_break_are_refused()
    {
        var ws = Workspace(s =>
        {
            var r = new RequestModel { Method = "GET", Path = "http://127.0.0.1:9/x", AuthType = "None" };
            r.Headers.Add(new HeaderRow { Name = "X-Trace", Value = "{{trace}}" });
            Saved(s, "r1", "trace", r);
        });
        try
        {
            var r = Tool(Tools(ws), "run_saved").Handler(Args("""{"path":"trace","vars":{"trace":"abc\r\nX-Injected: yes"}}"""));
            Assert.True(r.IsError);
            Assert.Contains("line break", Text(r));
        }
        finally { File.Delete(ws); }
    }

    [Fact]
    public void Grpc_metadata_may_not_set_host()
    {
        var tools = McpCommand.BuildTools(null, new HostAllowlist(Array.Empty<string>()), false, 5, false, null, true, new CliServices());
        var r = Tool(tools, "grpc_call").Handler(Args("""{"address":"http://127.0.0.1:9","method":"a.B/C","metadata":{"host":"other.internal"}}"""));
        Assert.True(r.IsError);
        Assert.Contains("may not set 'host'", Text(r));
    }

    [Fact]
    public async Task A_large_body_is_cut_at_the_cap_and_marked_truncated()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var server = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        using var client = SelfSignedCertificateFactory.CreateSignedCertificate("McpClient", ca, false, true);
        string big = new('A', 3 * 1024 * 1024);
        await using var upstream = await LoopbackMtlsServer.StartAsync(server, client.Thumbprint!, big, "text/plain");
        var tools = McpCommand.BuildTools(client, new HostAllowlist(new[] { "127.0.0.1" }), insecure: true, timeout: 30,
            includeLocalMachine: false, workspace: null, noAutoToken: true, new CliServices());

        var r = Tool(tools, "send_request").Handler(Args($"{{\"url\":\"{upstream.BaseUrl}\"}}"));
        Assert.False(r.IsError);
        using var doc = JsonDocument.Parse(r.Json);
        Assert.True(doc.RootElement.GetProperty("truncated").GetBoolean());
        Assert.Equal(McpCommand.DefaultMaxBody, doc.RootElement.GetProperty("body").GetString()!.Length);
    }

    // ------------------------------------------------------------ launch

    private static (int Exit, string Err) Launch(CliServices? services, params string[] args)
    {
        var err = new StringWriter();
        int exit = CliApp.Run(args, new StringReader(""), new StringWriter(), err, null, services);
        return (exit, err.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("http://api.example")]
    [InlineData("api.example:443")]
    [InlineData("a.example,b.example")]
    public void A_bad_allow_value_stops_the_launch(string value)
    {
        var (exit, err) = Launch(null, "--no-config", "mcp", "--allow", value);
        Assert.Equal(2, exit);
        Assert.Contains("--allow", err);
    }

    [Fact]
    public void No_allow_list_says_any_host_in_the_banner()
    {
        var (exit, err) = Launch(null, "--no-config", "mcp");
        Assert.Equal(0, exit);
        Assert.Contains("ANY HOST", err);
    }

    [Fact]
    public void A_bad_max_body_stops_the_launch() =>
        Assert.Equal(2, Launch(null, "--no-config", "mcp", "--max-body", "lots").Exit);

    [Fact]
    public void Mcp_ignores_a_config_file_found_by_walking_up_but_other_commands_still_read_it()
    {
        var root = Directory.CreateTempSubdirectory("certapi-mcp-cfg-").FullName;
        try
        {
            var sub = Directory.CreateDirectory(Path.Combine(root, "project", "sub")).FullName;
            File.WriteAllText(Path.Combine(root, "project", ConfigLoader.FileName),
                """{"defaultProfile":"p","profiles":{"p":{"insecure":true}}}""");
            var services = new CliServices { WorkingDirectory = sub, UserConfigPath = null };

            var (exit, err) = Launch(services, "mcp");
            Assert.Equal(0, exit);
            Assert.DoesNotContain("insecure", err, StringComparison.OrdinalIgnoreCase);
            Assert.Null(services.Profile);

            // The same file, named explicitly, is used — and the banner says what it changed.
            var named = new CliServices { WorkingDirectory = sub, UserConfigPath = null };
            var (exit2, err2) = Launch(named, "--config", Path.Combine(root, "project", ConfigLoader.FileName), "mcp");
            Assert.Equal(0, exit2);
            Assert.Contains("certapi mcp config", err2);
            Assert.Contains("insecure", err2);
            Assert.Contains("server-certificate checks are OFF", err2);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
