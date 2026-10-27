using System.Text.Json;
using System.Text.Json.Nodes;

namespace ApiTester.Cli.Mcp;

/// <summary>The JSON text a tool returns and whether it represents an error.</summary>
public sealed record ToolResult(string Json, bool IsError);

/// <summary>The behavioral hints the 2025-03-26 protocol revision added to a tool listing. Every
/// hint is optional and advisory — a host uses them for permission decisions, so a tool that
/// reaches the network must never claim <c>ReadOnlyHint</c>, however read-only its intent.</summary>
public sealed record ToolAnnotations(
    bool? ReadOnlyHint = null, bool? DestructiveHint = null,
    bool? IdempotentHint = null, bool? OpenWorldHint = null);

/// <summary>One MCP tool: its name, description, JSON-Schema for arguments, and handler.
/// The handler receives the call's <c>arguments</c> object (JsonElement; Undefined when absent).</summary>
public sealed record ToolDef(string Name, string Description, JsonNode InputSchema, Func<JsonElement, ToolResult> Handler)
{
    /// <summary>Behavioral hints for the host's permission model; omitted from the listing when null.</summary>
    public ToolAnnotations? Annotations { get; init; }
    /// <summary>JSON-Schema for the tool's result, when its shape is stable enough to promise.</summary>
    public JsonNode? OutputSchema { get; init; }
}

/// <summary>One MCP resource: an addressable, read-only piece of the session's workspace.
/// <paramref name="Read"/> runs on every resources/read, so the text reflects what the session
/// state says at that moment (captures included), not a snapshot from listing time.</summary>
public sealed record ResourceDef(string Uri, string Name, string Description, string MimeType, Func<string> Read);

/// <summary>The server-to-client log channel. Tools push lines here as they work; the server
/// drains it as `notifications/message` lines before each response, so a host sees the notes in
/// the order they happened. Severity follows the syslog ladder the protocol borrows; a line below
/// the client-set minimum is dropped at drain time, not at push time, so a later `logging/setLevel`
/// cannot resurrect notes from before it.</summary>
public sealed class McpNotifier
{
    private static readonly string[] Ladder =
        { "debug", "info", "notice", "warning", "error", "critical", "alert", "emergency" };

    private readonly Queue<(string Level, string Message)> _pending = new();
    private int _minimum = Array.IndexOf(Ladder, "info");

    /// <summary>True when <paramref name="level"/> is a level the protocol names.</summary>
    public bool TrySetMinimum(string level)
    {
        int index = Array.IndexOf(Ladder, level.ToLowerInvariant());
        if (index < 0) return false;
        _minimum = index;
        return true;
    }

    public void Info(string message) => Push("info", message);
    public void Debug(string message) => Push("debug", message);
    public void Warning(string message) => Push("warning", message);

    private void Push(string level, string message)
    {
        if (Array.IndexOf(Ladder, level) >= _minimum) _pending.Enqueue((level, message));
    }

    /// <summary>Everything pushed since the last drain, as ready-to-write notification lines.</summary>
    public IReadOnlyList<string> Drain()
    {
        if (_pending.Count == 0) return Array.Empty<string>();
        var lines = new List<string>(_pending.Count);
        while (_pending.Count > 0)
        {
            var (level, message) = _pending.Dequeue();
            lines.Add(new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["method"] = "notifications/message",
                ["params"] = new JsonObject { ["level"] = level, ["logger"] = "certapi", ["data"] = message }
            }.ToJsonString());
        }
        return lines;
    }
}

/// <summary>A minimal Model Context Protocol server over stdio: JSON-RPC 2.0, one compact JSON
/// object per line. Handles initialize / tools/list / tools/call / resources/list / resources/read /
/// logging/setLevel / ping / notifications.</summary>
public sealed class McpServer
{
    /// <summary>The protocol revisions this server actually implements, newest first. Initialize
    /// echoes the client's version only when it is one of these; anything else is answered with the
    /// newest, per the specification's negotiation rule — echoing an arbitrary client version would
    /// claim support for revisions this code has never seen.</summary>
    internal static readonly string[] SupportedProtocolVersions = { "2025-06-18", "2025-03-26", "2024-11-05" };

    private readonly Dictionary<string, ToolDef> _tools;
    private readonly IReadOnlyList<ToolDef> _order;
    private readonly Dictionary<string, ResourceDef> _resources;
    private readonly IReadOnlyList<ResourceDef> _resourceOrder;
    private readonly McpNotifier? _notifier;
    private readonly string _version;

    public McpServer(IReadOnlyList<ToolDef> tools, string version,
                     IReadOnlyList<ResourceDef>? resources = null, McpNotifier? notifier = null)
    {
        _order = tools;
        _tools = tools.ToDictionary(t => t.Name, StringComparer.Ordinal);
        _resourceOrder = resources ?? Array.Empty<ResourceDef>();
        _resources = _resourceOrder.ToDictionary(r => r.Uri, StringComparer.Ordinal);
        _notifier = notifier;
        _version = version;
    }

    public void Run(TextReader input, TextWriter output, TextWriter log, CancellationToken ct)
    {
        output.NewLine = "\n";
        string? line;
        while (!ct.IsCancellationRequested && (line = input.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var response = HandleLine(line);
            // Notes drain before the response so the host reads them in the order they happened;
            // a notification carries no id, so ordering is the only sequencing it gets.
            if (_notifier is not null)
                foreach (var note in _notifier.Drain()) output.WriteLine(note);
            if (response is not null) output.WriteLine(response);
            output.Flush();
        }
    }

    /// <summary>Process one request line; returns the response line, or null for a notification.</summary>
    public string? HandleLine(string line)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(line); }
        catch (JsonException) { return Error(null, -32700, "Parse error"); }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("method", out var methodEl) ||
                methodEl.ValueKind != JsonValueKind.String)
                return Error(IdOf(root), -32600, "Invalid request");

            string method = methodEl.GetString()!;
            JsonElement? id = IdOf(root);
            bool isNotification = id is null;

            // Notifications never get a response — and a call sent as a notification (no id) is
            // not run: JSON-RPC gives it no way to report its result, so running a tool for it
            // would act with nobody to tell.
            if (method.StartsWith("notifications/", StringComparison.Ordinal) || isNotification) return null;

            switch (method)
            {
                case "initialize":
                    return Result(id, InitializeResult(root));
                case "tools/list":
                    return Result(id, ToolsListResult());
                case "ping":
                    return Result(id, new JsonObject());
                case "tools/call":
                    return Result(id, ToolsCallResult(root));
                case "resources/list":
                    return Result(id, ResourcesListResult());
                case "resources/read":
                    return ResourcesReadResponse(id, root);
                case "logging/setLevel":
                    return LoggingSetLevelResponse(id, root);
                default:
                    return isNotification ? null : Error(id, -32601, "Method not found");
            }
        }
    }

    private static JsonElement? IdOf(JsonElement root) =>
        root.TryGetProperty("id", out var idEl) && idEl.ValueKind != JsonValueKind.Null ? idEl.Clone() : null;

    private JsonObject InitializeResult(JsonElement root)
    {
        string requested = "";
        if (root.TryGetProperty("params", out var p) && p.ValueKind == JsonValueKind.Object &&
            p.TryGetProperty("protocolVersion", out var pv) && pv.ValueKind == JsonValueKind.String)
            requested = pv.GetString()!;
        string protocol = SupportedProtocolVersions.Contains(requested)
            ? requested
            : SupportedProtocolVersions[0];

        var capabilities = new JsonObject { ["tools"] = new JsonObject() };
        if (_resourceOrder.Count > 0) capabilities["resources"] = new JsonObject();
        if (_notifier is not null) capabilities["logging"] = new JsonObject();

        return new JsonObject
        {
            ["protocolVersion"] = protocol,
            ["capabilities"] = capabilities,
            ["serverInfo"] = new JsonObject { ["name"] = "certapi", ["version"] = _version }
        };
    }

    private JsonObject ToolsListResult()
    {
        var arr = new JsonArray();
        foreach (var t in _order)
        {
            var tool = new JsonObject
            {
                ["name"] = t.Name,
                ["description"] = t.Description,
                ["inputSchema"] = t.InputSchema.DeepClone()
            };
            if (t.OutputSchema is not null) tool["outputSchema"] = t.OutputSchema.DeepClone();
            if (t.Annotations is { } a)
            {
                var hints = new JsonObject();
                if (a.ReadOnlyHint is { } ro) hints["readOnlyHint"] = ro;
                if (a.DestructiveHint is { } de) hints["destructiveHint"] = de;
                if (a.IdempotentHint is { } idem) hints["idempotentHint"] = idem;
                if (a.OpenWorldHint is { } ow) hints["openWorldHint"] = ow;
                tool["annotations"] = hints;
            }
            arr.Add(tool);
        }
        return new JsonObject { ["tools"] = arr };
    }

    private JsonObject ToolsCallResult(JsonElement root)
    {
        string name = "";
        JsonElement args = default;
        if (root.TryGetProperty("params", out var p) && p.ValueKind == JsonValueKind.Object)
        {
            if (p.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String) name = n.GetString()!;
            if (p.TryGetProperty("arguments", out var a)) args = a;
        }

        ToolResult tr;
        if (!_tools.TryGetValue(name, out var tool))
            tr = new ToolResult(JsonSerializer.Serialize(new { error = $"unknown tool '{name}'" }), IsError: true);
        else
        {
            try { tr = tool.Handler(args); }
            catch (Exception ex) { tr = new ToolResult(JsonSerializer.Serialize(new { error = ex.Message }), IsError: true); }
        }

        var result = new JsonObject
        {
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = tr.Json }),
            ["isError"] = tr.IsError
        };
        // Every tool here returns a JSON object as its text, so the structured form is the same
        // bytes parsed — a host on an older revision simply ignores the extra field.
        if (ParseObject(tr.Json) is { } structured) result["structuredContent"] = structured;
        return result;
    }

    private static JsonNode? ParseObject(string json)
    {
        try
        {
            var node = JsonNode.Parse(json);
            return node is JsonObject ? node : null;
        }
        catch (JsonException) { return null; }
    }

    private JsonObject ResourcesListResult()
    {
        var arr = new JsonArray();
        foreach (var r in _resourceOrder)
            arr.Add(new JsonObject
            {
                ["uri"] = r.Uri,
                ["name"] = r.Name,
                ["description"] = r.Description,
                ["mimeType"] = r.MimeType
            });
        return new JsonObject { ["resources"] = arr };
    }

    private string ResourcesReadResponse(JsonElement? id, JsonElement root)
    {
        string? uri = null;
        if (root.TryGetProperty("params", out var p) && p.ValueKind == JsonValueKind.Object &&
            p.TryGetProperty("uri", out var u) && u.ValueKind == JsonValueKind.String)
            uri = u.GetString();

        if (uri is null || !_resources.TryGetValue(uri, out var resource))
            return Error(id, -32002, $"Resource not found: {uri ?? "(no uri given)"}");

        string text;
        try { text = resource.Read(); }
        catch (Exception ex) { return Error(id, -32603, ex.Message); }

        return Result(id, new JsonObject
        {
            ["contents"] = new JsonArray(new JsonObject
            {
                ["uri"] = resource.Uri,
                ["mimeType"] = resource.MimeType,
                ["text"] = text
            })
        });
    }

    private string LoggingSetLevelResponse(JsonElement? id, JsonElement root)
    {
        string? level = null;
        if (root.TryGetProperty("params", out var p) && p.ValueKind == JsonValueKind.Object &&
            p.TryGetProperty("level", out var l) && l.ValueKind == JsonValueKind.String)
            level = l.GetString();

        if (_notifier is null) return Error(id, -32601, "Logging is not enabled on this server");
        if (level is null || !_notifier.TrySetMinimum(level))
            return Error(id, -32602, $"Unknown log level '{level ?? "(none)"}'");
        return Result(id, new JsonObject());
    }

    private static string Result(JsonElement? id, JsonNode result)
    {
        var env = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = IdNode(id), ["result"] = result };
        return env.ToJsonString();
    }

    private static string Error(JsonElement? id, int code, string message)
    {
        var env = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = IdNode(id),
            ["error"] = new JsonObject { ["code"] = code, ["message"] = message }
        };
        return env.ToJsonString();
    }

    private static JsonNode? IdNode(JsonElement? id) =>
        id is null ? null : JsonNode.Parse(id.Value.GetRawText());
}
