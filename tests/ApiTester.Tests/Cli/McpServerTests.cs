using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using ApiTester.Cli.Mcp;

namespace ApiTester.Tests.Cli;

public class McpServerTests
{
    private static McpServer Server()
    {
        var echo = new ToolDef(
            "echo", "Echoes its text argument.",
            JsonNode.Parse("""{"type":"object","properties":{"text":{"type":"string"}}}""")!,
            args => new ToolResult($"{{\"echoed\":\"{args.GetProperty("text").GetString()}\"}}", IsError: false));
        var boom = new ToolDef(
            "boom", "Always fails.",
            JsonNode.Parse("""{"type":"object"}""")!,
            _ => new ToolResult("{\"error\":\"nope\"}", IsError: true));
        return new McpServer(new[] { echo, boom }, "9.9.9");
    }

    private static JsonElement Result(string line)
    {
        using var doc = JsonDocument.Parse(line);
        return doc.RootElement.GetProperty("result").Clone();
    }

    [Fact]
    public void Initialize_reports_capabilities_and_server_info()
    {
        var line = Server().HandleLine("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05"}}""");
        var r = Result(line!);
        Assert.Equal("2024-11-05", r.GetProperty("protocolVersion").GetString());
        Assert.True(r.GetProperty("capabilities").TryGetProperty("tools", out _));
        Assert.Equal("certapi", r.GetProperty("serverInfo").GetProperty("name").GetString());
        Assert.Equal("9.9.9", r.GetProperty("serverInfo").GetProperty("version").GetString());
    }

    [Fact]
    public void Tools_list_returns_the_injected_tools()
    {
        var line = Server().HandleLine("""{"jsonrpc":"2.0","id":2,"method":"tools/list"}""");
        var tools = Result(line!).GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("name").GetString()).ToList();
        Assert.Contains("echo", tools);
        Assert.Contains("boom", tools);
    }

    [Fact]
    public void Tools_call_runs_the_handler_and_wraps_the_result()
    {
        var line = Server().HandleLine("""{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"echo","arguments":{"text":"hi"}}}""");
        var r = Result(line!);
        Assert.False(r.GetProperty("isError").GetBoolean());
        var text = r.GetProperty("content")[0].GetProperty("text").GetString()!;
        Assert.Contains("\"echoed\":\"hi\"", text);
    }

    [Fact]
    public void Tools_call_marks_handler_errors_and_refuses_unknown_tools()
    {
        var boom = Result(Server().HandleLine("""{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"boom","arguments":{}}}""")!);
        Assert.True(boom.GetProperty("isError").GetBoolean());

        // An unknown tool is a protocol error (-32602), as every revision since 2025-03-26 says.
        using var unknown = JsonDocument.Parse(Server().HandleLine("""{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"ghost","arguments":{}}}""")!);
        Assert.Equal(-32602, unknown.RootElement.GetProperty("error").GetProperty("code").GetInt32());
        Assert.Contains("ghost", unknown.RootElement.GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public void Notifications_get_no_response()
    {
        Assert.Null(Server().HandleLine("""{"jsonrpc":"2.0","method":"notifications/initialized"}"""));
    }

    [Fact]
    public void Unknown_method_and_parse_errors_return_json_rpc_errors()
    {
        using var d1 = JsonDocument.Parse(Server().HandleLine("""{"jsonrpc":"2.0","id":6,"method":"no_such"}""")!);
        Assert.Equal(-32601, d1.RootElement.GetProperty("error").GetProperty("code").GetInt32());

        using var d2 = JsonDocument.Parse(Server().HandleLine("{ this is not json ")!);
        Assert.Equal(-32700, d2.RootElement.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public void Run_processes_a_stream_until_eof()
    {
        var input = new StringReader(
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\"}\n" +
            "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}\n" +
            "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"echo\",\"arguments\":{\"text\":\"x\"}}}\n");
        var output = new StringWriter();
        Server().Run(input, output, TextWriter.Null, default);
        var lines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);   // ping + tools/call; the notification produced no line
    }

    // ---------------------------------------------------------------- protocol revisions

    [Fact]
    public void Initialize_echoes_a_supported_protocol_version_and_clamps_an_unknown_one()
    {
        // A version this server implements is echoed back...
        var known = Result(Server().HandleLine(
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-03-26"}}""")!);
        Assert.Equal("2025-03-26", known.GetProperty("protocolVersion").GetString());

        // ...an arbitrary client version is not: echoing it would claim support for a revision
        // this code has never seen, so the newest supported one is offered instead.
        var unknown = Result(Server().HandleLine(
            """{"jsonrpc":"2.0","id":2,"method":"initialize","params":{"protocolVersion":"2031-01-01"}}""")!);
        Assert.Equal(McpServer.SupportedProtocolVersions[0], unknown.GetProperty("protocolVersion").GetString());
    }

    // ---------------------------------------------------------------- annotations & schemas

    private static McpServer AnnotatedServer(McpNotifier? notifier = null)
    {
        var annotated = new ToolDef(
            "lookup", "Reads without touching anything.",
            JsonNode.Parse("""{"type":"object","properties":{}}""")!,
            _ => new ToolResult("{\"items\":[]}", IsError: false))
        {
            Annotations = new ToolAnnotations(ReadOnlyHint: true, IdempotentHint: true, OpenWorldHint: false),
            OutputSchema = JsonNode.Parse("""{"type":"object","properties":{"items":{"type":"array"}}}""")!
        };
        var resource = new ResourceDef("certapi://things/one", "one", "The first thing",
            "application/json", () => "{\"name\":\"one\"}");
        return new McpServer(new[] { annotated }, "9.9.9", new[] { resource }, notifier);
    }

    [Fact]
    public void Tools_list_carries_annotations_and_output_schema()
    {
        var tools = Result(AnnotatedServer().HandleLine("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""")!)
            .GetProperty("tools");
        var tool = tools[0];

        Assert.True(tool.GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean());
        Assert.True(tool.GetProperty("annotations").GetProperty("idempotentHint").GetBoolean());
        Assert.False(tool.GetProperty("annotations").GetProperty("openWorldHint").GetBoolean());
        Assert.Equal("object", tool.GetProperty("outputSchema").GetProperty("type").GetString());
    }

    [Fact]
    public void Tools_call_returns_structured_content_matching_the_text()
    {
        var r = Result(Server().HandleLine(
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"echo","arguments":{"text":"hi"}}}""")!);

        Assert.Equal("hi", r.GetProperty("structuredContent").GetProperty("echoed").GetString());
    }

    // ---------------------------------------------------------------- resources

    [Fact]
    public void Initialize_advertises_resources_and_logging_only_when_wired()
    {
        var bare = Result(Server().HandleLine("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}""")!);
        Assert.False(bare.GetProperty("capabilities").TryGetProperty("resources", out _));
        Assert.False(bare.GetProperty("capabilities").TryGetProperty("logging", out _));

        var wired = Result(AnnotatedServer(new McpNotifier()).HandleLine(
            """{"jsonrpc":"2.0","id":2,"method":"initialize","params":{}}""")!);
        Assert.True(wired.GetProperty("capabilities").TryGetProperty("resources", out _));
        Assert.True(wired.GetProperty("capabilities").TryGetProperty("logging", out _));
    }

    [Fact]
    public void Resources_list_and_read_round_trip()
    {
        var server = AnnotatedServer();
        var list = Result(server.HandleLine("""{"jsonrpc":"2.0","id":1,"method":"resources/list"}""")!)
            .GetProperty("resources");
        Assert.Equal("certapi://things/one", list[0].GetProperty("uri").GetString());
        Assert.Equal("application/json", list[0].GetProperty("mimeType").GetString());

        var read = Result(server.HandleLine(
            """{"jsonrpc":"2.0","id":2,"method":"resources/read","params":{"uri":"certapi://things/one"}}""")!);
        var content = read.GetProperty("contents")[0];
        Assert.Equal("certapi://things/one", content.GetProperty("uri").GetString());
        Assert.Contains("\"name\":\"one\"", content.GetProperty("text").GetString());
    }

    [Fact]
    public void Resources_read_of_an_unknown_uri_is_resource_not_found()
    {
        using var doc = JsonDocument.Parse(AnnotatedServer().HandleLine(
            """{"jsonrpc":"2.0","id":1,"method":"resources/read","params":{"uri":"certapi://things/none"}}""")!);
        Assert.Equal(-32002, doc.RootElement.GetProperty("error").GetProperty("code").GetInt32());
    }

    // ---------------------------------------------------------------- logging

    [Fact]
    public void Logging_set_level_accepts_a_known_level_and_refuses_garbage()
    {
        var server = AnnotatedServer(new McpNotifier());
        using var ok = JsonDocument.Parse(server.HandleLine(
            """{"jsonrpc":"2.0","id":1,"method":"logging/setLevel","params":{"level":"debug"}}""")!);
        Assert.True(ok.RootElement.TryGetProperty("result", out _));

        using var bad = JsonDocument.Parse(server.HandleLine(
            """{"jsonrpc":"2.0","id":2,"method":"logging/setLevel","params":{"level":"chatty"}}""")!);
        Assert.Equal(-32602, bad.RootElement.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public void Notifier_drops_lines_below_the_minimum_and_formats_the_rest()
    {
        var notifier = new McpNotifier();          // default minimum: info
        notifier.Debug("too quiet");
        notifier.Info("heard");

        var lines = notifier.Drain();

        var line = Assert.Single(lines);
        using var doc = JsonDocument.Parse(line);
        Assert.Equal("notifications/message", doc.RootElement.GetProperty("method").GetString());
        Assert.Equal("info", doc.RootElement.GetProperty("params").GetProperty("level").GetString());
        Assert.Equal("heard", doc.RootElement.GetProperty("params").GetProperty("data").GetString());
        Assert.Empty(notifier.Drain());            // drained means drained

        // Lowering the minimum admits debug lines pushed afterwards.
        Assert.True(notifier.TrySetMinimum("debug"));
        notifier.Debug("now audible");
        Assert.Single(notifier.Drain());
    }

    [Fact]
    public void Run_emits_notifications_before_the_response_that_caused_them()
    {
        var notifier = new McpNotifier();
        var noisy = new ToolDef("noisy", "Logs while it works.",
            JsonNode.Parse("""{"type":"object"}""")!,
            _ => { notifier.Info("working"); return new ToolResult("{\"done\":true}", false); });
        var server = new McpServer(new[] { noisy }, "9.9.9", null, notifier);

        var output = new StringWriter();
        server.Run(new StringReader(
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"noisy\",\"arguments\":{}}}\n"),
            output, TextWriter.Null, default);

        var lines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.Contains("notifications/message", lines[0]);
        Assert.Contains("\"id\":1", lines[1]);
    }
}
