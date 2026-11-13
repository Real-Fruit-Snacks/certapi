using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using ApiTester.Cli.Mcp;

namespace ApiTester.Tests.Cli;

/// <summary>The JSON-RPC and protocol-revision rules the server keeps: malformed input never ends
/// the session, both protocol eras are served, and calls can be cancelled while they run.</summary>
public class McpProtocolTests
{
    private const string Meta = "\"_meta\":{\"io.modelcontextprotocol/protocolVersion\":\"2026-07-28\",\"io.modelcontextprotocol/clientCapabilities\":{}}";

    private static McpServer Server(McpNotifier? notifier = null, Func<JsonElement, ToolResult>? slow = null)
    {
        var echo = new ToolDef("echo", "Echoes its text argument.",
            JsonNode.Parse("""{"type":"object","additionalProperties":false,"required":["text"],"properties":{"text":{"type":"string"},"n":{"type":"integer"},"tags":{"type":"object","additionalProperties":{"type":"string"}}}}""")!,
            a =>
            {
                notifier?.Info("echoing");
                notifier?.Debug("echo detail");
                return new ToolResult(JsonSerializer.Serialize(new { echoed = a.GetProperty("text").GetString() }), false);
            });
        var tools = new List<ToolDef> { echo };
        if (slow is not null)
            tools.Add(new ToolDef("slow", "Blocks until cancelled.", JsonNode.Parse("""{"type":"object"}""")!, slow));
        var resource = new ResourceDef("certapi://things/one", "one", "The first thing", "application/json", () => "{}");
        return new McpServer(tools, "9.9.9", new[] { resource }, notifier);
    }

    private static JsonElement Parse(string? line)
    {
        Assert.NotNull(line);
        using var doc = JsonDocument.Parse(line!);
        return doc.RootElement.Clone();
    }

    private static int Code(string? line) => Parse(line).GetProperty("error").GetProperty("code").GetInt32();

    private static List<string> RunLines(McpServer server, string input)
    {
        var output = new StringWriter();
        server.Run(new StringReader(input), output, TextWriter.Null, default);
        return output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();
    }

    // ------------------------------------------------------------ malformed input

    [Theory]
    [InlineData("\"hello\"")]
    [InlineData("42")]
    [InlineData("null")]
    [InlineData("true")]
    [InlineData("[]")]
    public void A_line_that_is_not_an_object_is_invalid_request_and_the_loop_survives(string line)
    {
        var lines = RunLines(Server(), line + "\n{\"jsonrpc\":\"2.0\",\"id\":9,\"method\":\"ping\"}\n");
        // Responses are matched by id, not order: a queued message may answer after the ping.
        Assert.Equal(2, lines.Count);
        Assert.Contains(lines, l => Parse(l).TryGetProperty("error", out var e) && e.GetProperty("code").GetInt32() == -32600);
        Assert.Contains(lines, l => Parse(l).TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number && id.GetInt32() == 9);
    }

    [Theory]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":"a\ud800"}""", -32600)]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"a\ud800"}}""", -32602)]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":"resources/read","params":{"uri":"a\ud800"}}""", -32002)]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":"logging/setLevel","params":{"level":"a\ud800"}}""", -32602)]
    public void An_unpaired_surrogate_gets_an_error_and_the_loop_survives(string line, int code)
    {
        var lines = RunLines(Server(new McpNotifier()), line + "\n{\"jsonrpc\":\"2.0\",\"id\":9,\"method\":\"ping\"}\n");
        Assert.Equal(2, lines.Count);
        Assert.Contains(lines, l => Parse(l).TryGetProperty("error", out var e) && e.GetProperty("code").GetInt32() == code);
        Assert.Contains(lines, l => Parse(l).GetProperty("id").ValueKind == JsonValueKind.Number && Parse(l).GetProperty("id").GetInt32() == 9);
    }

    [Fact]
    public void An_unpaired_surrogate_in_initialize_falls_back_to_the_newest_version()
    {
        var r = Parse(Server().HandleLine("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"a\ud800"}}"""));
        Assert.Equal(McpServer.SupportedProtocolVersions[0], r.GetProperty("result").GetProperty("protocolVersion").GetString());
    }

    [Fact]
    public void A_batch_is_answered_with_an_array_in_order()
    {
        var lines = RunLines(Server(),
            """[{"jsonrpc":"2.0","id":1,"method":"ping"},{"jsonrpc":"2.0","method":"notifications/initialized"},{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"echo","arguments":{"text":"b"}}}]""" + "\n");
        var batch = Parse(Assert.Single(lines));
        Assert.Equal(JsonValueKind.Array, batch.ValueKind);
        Assert.Equal(2, batch.GetArrayLength());
        Assert.Equal(1, batch[0].GetProperty("id").GetInt32());
        Assert.Equal(2, batch[1].GetProperty("id").GetInt32());
    }

    [Theory]
    [InlineData("""{"jsonrpc":"2.0","id":{"a":1},"method":"ping"}""")]
    [InlineData("""{"jsonrpc":"2.0","id":true,"method":"ping"}""")]
    [InlineData("""{"jsonrpc":"2.0","id":1.5,"method":"ping"}""")]
    [InlineData("""{"jsonrpc":"2.0","id":null,"method":"ping"}""")]
    [InlineData("""{"id":3,"method":"ping"}""")]
    [InlineData("""{"jsonrpc":"1.0","id":3,"method":"ping"}""")]
    [InlineData("""{"jsonrpc":"2.0","id":3}""")]
    public void Malformed_requests_are_invalid_request(string line) =>
        Assert.Equal(-32600, Code(Server().HandleLine(line)));

    [Fact]
    public void A_request_with_an_id_and_a_notification_method_is_method_not_found() =>
        Assert.Equal(-32601, Code(Server().HandleLine("""{"jsonrpc":"2.0","id":5,"method":"notifications/initialized"}""")));

    [Fact]
    public void A_byte_order_mark_before_the_first_line_is_ignored()
    {
        var lines = RunLines(Server(), "﻿{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\"}\n");
        Assert.True(Parse(Assert.Single(lines)).TryGetProperty("result", out _));
    }

    [Fact]
    public void Params_that_are_not_an_object_are_invalid_params() =>
        Assert.Equal(-32602, Code(Server().HandleLine("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":["echo"]}""")));

    // ------------------------------------------------------------ arguments

    [Theory]
    [InlineData("""{"text":5}""", "'text'")]
    [InlineData("""{}""", "'text' is required")]
    [InlineData("""{"text":"a","extra":1}""", "'extra' is not an argument")]
    [InlineData("""{"text":"a","n":1.5}""", "'n'")]
    [InlineData("""{"text":"a","tags":{"k":7}}""", "'tags.k'")]
    public void Wrong_arguments_are_a_tool_error_that_names_the_field(string args, string expected)
    {
        var r = Parse(Server().HandleLine($$$"""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"echo","arguments":{{{args}}}}}"""))
            .GetProperty("result");
        Assert.True(r.GetProperty("isError").GetBoolean());
        Assert.Contains(expected, r.GetProperty("structuredContent").GetProperty("error").GetString());
    }

    [Fact]
    public void Arguments_that_are_not_an_object_are_invalid_params() =>
        Assert.Equal(-32602, Code(Server().HandleLine("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"echo","arguments":"x"}}""")));

    // ------------------------------------------------------------ 2026-07-28

    [Fact]
    public void Server_discover_lists_versions_capabilities_identity_and_cache_hints()
    {
        var r = Parse(Server().HandleLine($$$"""{"jsonrpc":"2.0","id":1,"method":"server/discover","params":{{{{Meta}}}}}""")).GetProperty("result");
        Assert.Equal("complete", r.GetProperty("resultType").GetString());
        var versions = r.GetProperty("supportedVersions").EnumerateArray().Select(v => v.GetString()).ToList();
        Assert.Contains("2026-07-28", versions);
        Assert.Contains("2025-06-18", versions);
        Assert.True(r.GetProperty("capabilities").TryGetProperty("tools", out _));
        Assert.Equal("certapi", r.GetProperty("_meta").GetProperty("io.modelcontextprotocol/serverInfo").GetProperty("name").GetString());
        Assert.True(r.GetProperty("ttlMs").GetInt32() >= 0);
        Assert.Equal("private", r.GetProperty("cacheScope").GetString());
    }

    [Fact]
    public void An_unsupported_version_is_error_32022_naming_the_supported_ones()
    {
        var e = Parse(Server().HandleLine("""{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{"_meta":{"io.modelcontextprotocol/protocolVersion":"2099-01-01","io.modelcontextprotocol/clientCapabilities":{}}}}"""))
            .GetProperty("error");
        Assert.Equal(-32022, e.GetProperty("code").GetInt32());
        Assert.Equal("2099-01-01", e.GetProperty("data").GetProperty("requested").GetString());
        Assert.Contains(e.GetProperty("data").GetProperty("supported").EnumerateArray(), v => v.GetString() == "2026-07-28");
    }

    [Fact]
    public void A_modern_request_without_client_capabilities_is_invalid_params() =>
        Assert.Equal(-32602, Code(Server().HandleLine("""{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{"_meta":{"io.modelcontextprotocol/protocolVersion":"2026-07-28"}}}""")));

    [Fact]
    public void Modern_results_carry_result_type_and_list_results_carry_cache_hints()
    {
        var server = Server();
        var tools = Parse(server.HandleLine($$$"""{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{{{{Meta}}}}}""")).GetProperty("result");
        Assert.Equal("complete", tools.GetProperty("resultType").GetString());
        Assert.True(tools.TryGetProperty("ttlMs", out _));
        Assert.Equal("private", tools.GetProperty("cacheScope").GetString());

        var call = Parse(server.HandleLine($$$"""{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"echo","arguments":{"text":"x"},{{{Meta}}}}}""")).GetProperty("result");
        Assert.Equal("complete", call.GetProperty("resultType").GetString());

        foreach (var method in new[] { "resources/list", "resources/templates/list" })
        {
            var r = Parse(server.HandleLine($$$"""{"jsonrpc":"2.0","id":3,"method":"{{{method}}}","params":{{{{Meta}}}}}""")).GetProperty("result");
            Assert.Equal("complete", r.GetProperty("resultType").GetString());
            Assert.True(r.TryGetProperty("cacheScope", out _));
        }
        var read = Parse(server.HandleLine($$$"""{"jsonrpc":"2.0","id":4,"method":"resources/read","params":{"uri":"certapi://things/one",{{{Meta}}}}}""")).GetProperty("result");
        Assert.Equal(0, read.GetProperty("ttlMs").GetInt32());
    }

    [Fact]
    public void Handshake_era_results_keep_their_old_shape()
    {
        var tools = Parse(Server().HandleLine("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""")).GetProperty("result");
        Assert.False(tools.TryGetProperty("resultType", out _));
        Assert.False(tools.TryGetProperty("ttlMs", out _));
    }

    [Fact]
    public void A_modern_unknown_resource_is_invalid_params_with_the_uri() 
    {
        var e = Parse(Server().HandleLine($$$"""{"jsonrpc":"2.0","id":1,"method":"resources/read","params":{"uri":"certapi://none",{{{Meta}}}}}""")).GetProperty("error");
        Assert.Equal(-32602, e.GetProperty("code").GetInt32());
        Assert.Equal("certapi://none", e.GetProperty("data").GetProperty("uri").GetString());
    }

    [Theory]
    [InlineData("initialize")]
    [InlineData("ping")]
    [InlineData("logging/setLevel")]
    public void Handshake_only_methods_are_not_found_on_a_modern_request(string method) =>
        Assert.Equal(-32601, Code(Server(new McpNotifier()).HandleLine($$$"""{"jsonrpc":"2.0","id":1,"method":"{{{method}}}","params":{{{{Meta}}}}}""")));

    [Fact]
    public void Server_discover_without_the_version_is_invalid_params() =>
        Assert.Equal(-32602, Code(Server().HandleLine("""{"jsonrpc":"2.0","id":1,"method":"server/discover"}""")));

    [Fact]
    public void A_modern_request_gets_log_notifications_only_when_it_asks_and_only_at_its_level()
    {
        var notifier = new McpNotifier();
        string call(int id, string extraMeta) =>
            "{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"method\":\"tools/call\",\"params\":{\"name\":\"echo\",\"arguments\":{\"text\":\"x\"}," +
            "\"_meta\":{\"io.modelcontextprotocol/protocolVersion\":\"2026-07-28\",\"io.modelcontextprotocol/clientCapabilities\":{}" + extraMeta + "}}}";
        var lines = RunLines(Server(notifier),
            call(1, "") + "\n" +
            call(2, ",\"io.modelcontextprotocol/logLevel\":\"info\"") + "\n" +
            call(3, ",\"io.modelcontextprotocol/logLevel\":\"debug\"") + "\n");
        // 1: response only. 2: one note (info) + response. 3: two notes (info, debug) + response.
        Assert.Equal(6, lines.Count);
        Assert.Contains("\"id\":1", lines[0]);
        Assert.Contains("echoing", lines[1]);
        Assert.Contains("\"id\":2", lines[2]);
        Assert.Contains("echoing", lines[3]);
        Assert.Contains("echo detail", lines[4]);
        Assert.Contains("\"id\":3", lines[5]);
    }

    [Fact]
    public void An_unknown_log_level_on_a_modern_request_is_invalid_params() =>
        Assert.Equal(-32602, Code(Server(new McpNotifier()).HandleLine("""{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{"_meta":{"io.modelcontextprotocol/protocolVersion":"2026-07-28","io.modelcontextprotocol/clientCapabilities":{},"io.modelcontextprotocol/logLevel":"loud"}}}""")));

    // ------------------------------------------------------------ concurrency and cancellation

    /// <summary>A stdin that hands out lines as a test writes them and ends when told to.</summary>
    private sealed class LineFeed : TextReader
    {
        private readonly BlockingCollection<string?> _lines = new();
        public void Send(string line) => _lines.Add(line);
        public void End() => _lines.Add(null);
        public override string? ReadLine() => _lines.Take();
    }

    private sealed class LineSink : TextWriter
    {
        public readonly BlockingCollection<string> Lines = new();
        private readonly System.Text.StringBuilder _current = new();
        public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;
        public override void Write(char value)
        {
            if (value == '\n') { Lines.Add(_current.ToString()); _current.Clear(); }
            else _current.Append(value);
        }
    }

    [Fact]
    public void A_ping_is_answered_while_a_call_runs_and_a_cancelled_call_answers_nothing()
    {
        var started = new ManualResetEventSlim();
        bool sawCancellation = false;
        var server = Server(slow: _ =>
        {
            started.Set();
            var ct = McpServer.CurrentCallCancellation!.Value;
            sawCancellation = ct.WaitHandle.WaitOne(TimeSpan.FromSeconds(20));
            return new ToolResult("{\"late\":true}", false);
        });
        var input = new LineFeed();
        var output = new LineSink();
        var run = Task.Run(() => server.Run(input, output, TextWriter.Null, default));

        input.Send("""{"jsonrpc":"2.0","id":"call","method":"tools/call","params":{"name":"slow","arguments":{}}}""");
        Assert.True(started.Wait(TimeSpan.FromSeconds(10)));
        input.Send("""{"jsonrpc":"2.0","id":2,"method":"ping"}""");
        Assert.True(output.Lines.TryTake(out var first, TimeSpan.FromSeconds(5)));
        Assert.Contains("\"id\":2", first);                       // answered while the call runs

        input.Send("""{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":"call"}}""");
        input.Send("""{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"echo","arguments":{"text":"after"}}}""");
        Assert.True(output.Lines.TryTake(out var next, TimeSpan.FromSeconds(10)));
        Assert.Contains("\"id\":3", next);                        // the cancelled call sent nothing
        Assert.True(sawCancellation);

        input.End();
        Assert.True(run.Wait(TimeSpan.FromSeconds(10)));
        Assert.Empty(output.Lines);
    }

    [Fact]
    public void End_of_input_cancels_a_call_that_outlives_the_grace_period()
    {
        var started = new ManualResetEventSlim();
        bool sawCancellation = false;
        var server = new McpServer(new[]
        {
            new ToolDef("slow", "Blocks until cancelled.", JsonNode.Parse("""{"type":"object"}""")!, _ =>
            {
                started.Set();
                sawCancellation = McpServer.CurrentCallCancellation!.Value.WaitHandle.WaitOne(TimeSpan.FromSeconds(30));
                return new ToolResult("{}", false);
            })
        }, "9.9.9") { DrainGrace = TimeSpan.FromMilliseconds(200) };
        var input = new LineFeed();
        var output = new LineSink();
        var run = Task.Run(() => server.Run(input, output, TextWriter.Null, default));
        input.Send("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"slow","arguments":{}}}""");
        Assert.True(started.Wait(TimeSpan.FromSeconds(10)));

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        input.End();
        Assert.True(run.Wait(TimeSpan.FromSeconds(10)));
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5));
        Assert.True(sawCancellation);
        Assert.Empty(output.Lines);
    }

    [Fact]
    public void A_queued_call_still_answers_when_input_ends_right_after_it() 
    {
        var lines = RunLines(Server(), """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"echo","arguments":{"text":"piped"}}}""" + "\n");
        Assert.Contains("piped", Assert.Single(lines));
    }
}
