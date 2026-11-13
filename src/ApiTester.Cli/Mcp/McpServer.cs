using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

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
/// The handler receives the call's <c>arguments</c> object (JsonElement; Undefined when absent).
/// Arguments are checked against <see cref="InputSchema"/> before the handler runs.</summary>
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

/// <summary>The server-to-client log channel. Tools push lines here as they work. While the server
/// is handling a request, lines go to that request's own sink and are sent, filtered, just before
/// its response — so one request never receives another's notes, and a request on the 2026-07-28
/// revision receives none unless it asked. Every line is also written to stderr, the channel the
/// current revision recommends. Outside a request (unit tests, older callers) lines queue for
/// <see cref="Drain"/>, filtered by the connection-wide minimum.</summary>
public sealed class McpNotifier
{
    internal static readonly string[] Ladder =
        { "debug", "info", "notice", "warning", "error", "critical", "alert", "emergency" };

    private readonly Queue<(string Level, string Message)> _pending = new();
    private readonly AsyncLocal<List<(string Level, string Message)>?> _sink = new();
    private int _minimum = Array.IndexOf(Ladder, "info");

    /// <summary>Where every line is also written; null writes nowhere.</summary>
    public TextWriter? Log { get; set; }

    /// <summary>The minimum level a request on a handshake-era revision receives.</summary>
    internal int Minimum => _minimum;

    /// <summary>The position of <paramref name="level"/> on the ladder, or -1 when the protocol does
    /// not name it.</summary>
    internal static int LevelIndex(string? level) =>
        level is null ? -1 : Array.IndexOf(Ladder, level.ToLowerInvariant());

    /// <summary>True when <paramref name="level"/> is a level the protocol names.</summary>
    public bool TrySetMinimum(string level)
    {
        int index = LevelIndex(level);
        if (index < 0) return false;
        _minimum = index;
        return true;
    }

    public void Info(string message) => Push("info", message);
    public void Debug(string message) => Push("debug", message);
    public void Warning(string message) => Push("warning", message);

    private void Push(string level, string message)
    {
        if (Log is { } log)
            lock (log) { log.WriteLine($"mcp [{level}] {message}"); log.Flush(); }
        if (_sink.Value is { } sink) { lock (sink) sink.Add((level, message)); return; }
        if (LevelIndex(level) >= _minimum) _pending.Enqueue((level, message));
    }

    /// <summary>Routes lines pushed on this async flow into <paramref name="sink"/> until disposed.</summary>
    internal IDisposable Capture(List<(string Level, string Message)> sink)
    {
        var previous = _sink.Value;
        _sink.Value = sink;
        return new Restore(() => _sink.Value = previous);
    }

    private sealed class Restore(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }

    internal static string Format(string level, string message) => new JsonObject
    {
        ["jsonrpc"] = "2.0",
        ["method"] = "notifications/message",
        ["params"] = new JsonObject { ["level"] = level, ["logger"] = "certapi", ["data"] = message }
    }.ToJsonString();

    /// <summary>Everything pushed outside a request since the last drain, as ready-to-write
    /// notification lines.</summary>
    public IReadOnlyList<string> Drain()
    {
        if (_pending.Count == 0) return Array.Empty<string>();
        var lines = new List<string>(_pending.Count);
        while (_pending.Count > 0)
        {
            var (level, message) = _pending.Dequeue();
            lines.Add(Format(level, message));
        }
        return lines;
    }
}

/// <summary>A Model Context Protocol server over stdio: JSON-RPC 2.0, one compact JSON object per
/// line. Dual-era: a client that opens with <c>initialize</c> is served by the handshake-era rules
/// (2024-11-05 through 2025-11-25), and a request that carries its protocol version in
/// <c>_meta</c> is served statelessly by the 2026-07-28 rules — <c>server/discover</c>,
/// <c>resultType</c>, caching hints and the newer error codes. Both may arrive on one process.
/// <para>A reader thread keeps reading while a tool runs, so <c>notifications/cancelled</c>, a
/// listing or the end of input are acted on at once. Tool calls and resource reads run one at a
/// time, in arrival order, because they share the session's captured state.</para></summary>
public sealed class McpServer
{
    /// <summary>The handshake-era revisions, newest first. Initialize echoes the client's version
    /// only when it is one of these; anything else is answered with the newest, per the
    /// specification's negotiation rule.</summary>
    internal static readonly string[] SupportedProtocolVersions = { "2025-11-25", "2025-06-18", "2025-03-26", "2024-11-05" };

    /// <summary>The revisions served per request from <c>_meta</c>, newest first.</summary>
    internal static readonly string[] ModernProtocolVersions = { "2026-07-28" };

    internal const string MetaVersion = "io.modelcontextprotocol/protocolVersion";
    internal const string MetaCapabilities = "io.modelcontextprotocol/clientCapabilities";
    internal const string MetaLogLevel = "io.modelcontextprotocol/logLevel";
    internal const string MetaServerInfo = "io.modelcontextprotocol/serverInfo";

    private const int InvalidRequest = -32600, MethodNotFound = -32601, InvalidParams = -32602,
                      InternalError = -32603, ParseError = -32700, UnsupportedVersion = -32022,
                      LegacyResourceNotFound = -32002;

    private static readonly AsyncLocal<CancellationToken?> CallToken = new();

    /// <summary>The cancellation token of the request now being served on this async flow: it fires
    /// on <c>notifications/cancelled</c> for that request, on the end of input, and on Ctrl+C. Null
    /// outside <see cref="Run"/>.</summary>
    public static CancellationToken? CurrentCallCancellation => CallToken.Value;

    private readonly Dictionary<string, ToolDef> _tools;
    private readonly IReadOnlyList<ToolDef> _order;
    private readonly Dictionary<string, ResourceDef> _resources;
    private readonly IReadOnlyList<ResourceDef> _resourceOrder;
    private readonly McpNotifier? _notifier;
    private readonly string _version;

    /// <summary>How long a shut-down server waits for the call in flight to stop after it was
    /// cancelled, before giving up on it.</summary>
    internal TimeSpan ShutdownGrace { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>How long queued work may run on after the end of input before it is cancelled.</summary>
    internal TimeSpan DrainGrace { get; init; } = TimeSpan.FromSeconds(2);

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

    // ------------------------------------------------------------------ the loop

    public void Run(TextReader input, TextWriter output, TextWriter log, CancellationToken ct)
    {
        output.NewLine = "\n";
        var writeLock = new object();
        void Write(IEnumerable<string> lines)
        {
            lock (writeLock)
            {
                foreach (var l in lines) output.WriteLine(l);
                output.Flush();
            }
        }

        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var inFlight = new Dictionary<string, CancellationTokenSource>(StringComparer.Ordinal);
        var queue = Channel.CreateUnbounded<(JsonElement Root, string? Key, CancellationTokenSource Cts)>(
            new UnboundedChannelOptions { SingleReader = true });

        // One worker: calls share the session's state, so they run in arrival order.
        var worker = Task.Run(async () =>
        {
            await foreach (var (root, key, cts) in queue.Reader.ReadAllAsync())
            {
                try
                {
                    if (cts.IsCancellationRequested) continue;   // cancelled before it started
                    var lines = Serve(root, cts.Token);
                    if (!cts.IsCancellationRequested) Write(lines);
                }
                catch (Exception ex)
                {
                    WriteLog(log, "mcp: " + ex.Message);
                    if (!cts.IsCancellationRequested) Write(new[] { Error(null, InternalError, "Internal error") });
                }
                finally
                {
                    if (key is not null) lock (inFlight) inFlight.Remove(key);
                    cts.Dispose();
                }
            }
        });

        bool first = true;
        try
        {
            string? line;
            while (!shutdown.IsCancellationRequested && (line = input.ReadLine()) is not null)
            {
                if (first) { line = line.TrimStart('﻿'); first = false; }
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    JsonElement root;
                    try
                    {
                        using var doc = JsonDocument.Parse(line);
                        root = doc.RootElement.Clone();
                    }
                    catch (JsonException) { Write(new[] { Error(null, ParseError, "Parse error") }); continue; }

                    if (root.ValueKind == JsonValueKind.Object && IsCancellation(root, out var cancelled))
                    {
                        lock (inFlight)
                            if (inFlight.TryGetValue(cancelled!, out var target)) target.Cancel();
                        continue;
                    }

                    if (NeedsWorker(root))
                    {
                        var cts = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
                        string? key = root.ValueKind == JsonValueKind.Object ? KeyOf(root) : null;
                        if (key is not null) lock (inFlight) inFlight[key] = cts;
                        queue.Writer.TryWrite((root, key, cts));
                    }
                    else
                    {
                        Write(Serve(root, shutdown.Token));
                    }
                }
                catch (Exception ex)
                {
                    // One bad message must never end the session.
                    WriteLog(log, "mcp: " + ex.Message);
                    Write(new[] { Error(null, InternalError, "Internal error") });
                }
            }
        }
        finally
        {
            // End of input: what is already queued gets a short grace to finish (a client that pipes
            // a request and closes stdin still gets its answer), then everything still running is
            // cancelled and answers nothing. Ctrl+C cancels at once.
            queue.Writer.TryComplete();
            bool finished = false;
            try { finished = worker.Wait(ct.IsCancellationRequested ? TimeSpan.Zero : DrainGrace); }
            catch (AggregateException) { finished = true; }
            if (!finished)
            {
                shutdown.Cancel();
                try { worker.Wait(ShutdownGrace); } catch (AggregateException) { }
            }
        }
    }

    private static void WriteLog(TextWriter log, string message)
    {
        try { lock (log) { log.WriteLine(message); log.Flush(); } } catch (ObjectDisposedException) { }
    }

    private static bool IsCancellation(JsonElement root, out string? requestKey)
    {
        requestKey = null;
        if (root.TryGetProperty("id", out _)) return false;
        if (!root.TryGetProperty("method", out var m) || m.ValueKind != JsonValueKind.String) return false;
        try { if (m.GetString() != "notifications/cancelled") return false; }
        catch (InvalidOperationException) { return false; }
        if (root.TryGetProperty("params", out var p) && p.ValueKind == JsonValueKind.Object &&
            p.TryGetProperty("requestId", out var rid) && rid.ValueKind is JsonValueKind.String or JsonValueKind.Number)
            requestKey = rid.GetRawText();
        return true;   // a malformed cancellation is ignored, as the specification says
    }

    private static string? KeyOf(JsonElement root) =>
        root.TryGetProperty("id", out var id) && id.ValueKind is JsonValueKind.String or JsonValueKind.Number
            ? id.GetRawText() : null;

    /// <summary>True for messages that run a tool or read workspace state: they go to the worker.</summary>
    private static bool NeedsWorker(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array) return true;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("id", out _)) return false;
        if (!root.TryGetProperty("method", out var m) || m.ValueKind != JsonValueKind.String) return false;
        try { return m.GetString() is "tools/call" or "resources/read"; }
        catch (InvalidOperationException) { return false; }
    }

    /// <summary>The lines to write for one message: its notifications, then its response.</summary>
    private List<string> Serve(JsonElement root, CancellationToken ct)
    {
        var lines = new List<string>();
        if (root.ValueKind == JsonValueKind.Array)
        {
            if (root.GetArrayLength() == 0) { lines.Add(Error(null, InvalidRequest, "Invalid request: empty batch")); return lines; }
            var responses = new JsonArray();
            foreach (var element in root.EnumerateArray())
            {
                if (ct.IsCancellationRequested) break;
                var (notes, response) = HandleWithNotes(element, ct);
                lines.AddRange(notes);
                if (response is not null) responses.Add(JsonNode.Parse(response));
            }
            if (responses.Count > 0) lines.Add(responses.ToJsonString());
            return lines;
        }
        var (n, r) = HandleWithNotes(root, ct);
        lines.AddRange(n);
        if (r is not null) lines.Add(r);
        return lines;
    }

    private (List<string> Notes, string? Response) HandleWithNotes(JsonElement root, CancellationToken ct)
    {
        var sink = new List<(string Level, string Message)>();
        var request = new RequestInfo();
        string? response;
        var previous = CallToken.Value;
        CallToken.Value = ct;
        try
        {
            using (_notifier?.Capture(sink))
                response = Handle(root, request);
        }
        finally { CallToken.Value = previous; }

        var notes = new List<string>();
        if (_notifier is not null && sink.Count > 0)
        {
            // Handshake era: the connection-wide minimum. 2026-07-28: only what this request asked for.
            int minimum = request.Modern ? request.LogLevel ?? int.MaxValue : _notifier.Minimum;
            foreach (var (level, message) in sink)
                if (McpNotifier.LevelIndex(level) >= minimum) notes.Add(McpNotifier.Format(level, message));
        }
        return (notes, response);
    }

    // ------------------------------------------------------------------ one message

    private sealed class RequestInfo
    {
        public bool Modern;
        public int? LogLevel;
    }

    /// <summary>Process one request line; returns the response line, or null for a notification.
    /// Tools run inline here; <see cref="Run"/> is the entry point that also handles cancellation.</summary>
    public string? HandleLine(string line)
    {
        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(line.TrimStart('﻿'));
            root = doc.RootElement.Clone();
        }
        catch (JsonException) { return Error(null, ParseError, "Parse error"); }

        try
        {
            if (root.ValueKind == JsonValueKind.Array)
            {
                var lines = Serve(root, CancellationToken.None);
                return lines.Count == 0 ? null : lines[^1];
            }
            return Handle(root, new RequestInfo());
        }
        catch (Exception) { return Error(null, InternalError, "Internal error"); }
    }

    private string? Handle(JsonElement root, RequestInfo info)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return Error(null, InvalidRequest, "Invalid request: a message must be a JSON object");

        bool isNotification = !root.TryGetProperty("id", out var idEl);
        JsonElement? id = null;
        if (!isNotification)
        {
            if (idEl.ValueKind == JsonValueKind.String || (idEl.ValueKind == JsonValueKind.Number && idEl.TryGetInt64(out _)))
                id = idEl.Clone();
            else
                return Error(null, InvalidRequest, "Invalid request: id must be a string or an integer");
        }

        if (!root.TryGetProperty("jsonrpc", out var rpc) || rpc.ValueKind != JsonValueKind.String || !SafeString(rpc, out var rpcText) || rpcText != "2.0")
            return isNotification ? null : Error(id, InvalidRequest, "Invalid request: jsonrpc must be \"2.0\"");

        if (!root.TryGetProperty("method", out var methodEl) || methodEl.ValueKind != JsonValueKind.String ||
            !SafeString(methodEl, out var method))
            return isNotification ? null : Error(id, InvalidRequest, "Invalid request: method must be a string");

        // A notification never gets a reply, and a call sent as one is not run: JSON-RPC gives it no
        // way to report its result, so running a tool for it would act with nobody to tell.
        if (isNotification) return null;
        if (method.StartsWith("notifications/", StringComparison.Ordinal))
            return Error(id, MethodNotFound, $"Method not found: {method} is a notification and takes no id");

        JsonElement p = default;
        bool hasParams = root.TryGetProperty("params", out p);
        if (hasParams && p.ValueKind is not (JsonValueKind.Object or JsonValueKind.Null))
            return Error(id, InvalidParams, "Invalid params: params must be an object");
        if (hasParams && p.ValueKind == JsonValueKind.Null) hasParams = false;

        // The 2026-07-28 revision: the version, capabilities and log level travel on each request.
        if (hasParams && p.TryGetProperty("_meta", out var meta) && meta.ValueKind == JsonValueKind.Object &&
            meta.TryGetProperty(MetaVersion, out var ver))
        {
            info.Modern = true;
            if (ver.ValueKind != JsonValueKind.String || !SafeString(ver, out var requested))
                return Error(id, InvalidParams, $"Invalid params: {MetaVersion} must be a string");
            if (!ModernProtocolVersions.Contains(requested))
                return Error(id, UnsupportedVersion, "Unsupported protocol version", new JsonObject
                {
                    ["supported"] = new JsonArray(ModernProtocolVersions.Concat(SupportedProtocolVersions).Select(v => (JsonNode)v).ToArray()),
                    ["requested"] = requested
                });
            if (!meta.TryGetProperty(MetaCapabilities, out var caps) || caps.ValueKind != JsonValueKind.Object)
                return Error(id, InvalidParams, $"Invalid params: {MetaCapabilities} is required");
            if (meta.TryGetProperty(MetaLogLevel, out var lvl))
            {
                if (lvl.ValueKind != JsonValueKind.String || !SafeString(lvl, out var level) || McpNotifier.LevelIndex(level) < 0)
                    return Error(id, InvalidParams, $"Invalid params: {MetaLogLevel} is not a log level");
                info.LogLevel = McpNotifier.LevelIndex(level);
            }
        }

        bool modern = info.Modern;
        switch (method)
        {
            case "server/discover":
                if (!modern) return Error(id, InvalidParams, $"Invalid params: server/discover needs {MetaVersion} in _meta");
                return Result(id, Modern(DiscoverResult(), ttlMs: 300_000));
            case "initialize":
                if (modern) return Error(id, MethodNotFound, "Method not found: initialize is not part of revision " + ModernProtocolVersions[0]);
                return Result(id, InitializeResult(hasParams ? p : default));
            case "ping":
                if (modern) return Error(id, MethodNotFound, "Method not found: ping was removed in revision " + ModernProtocolVersions[0]);
                return Result(id, new JsonObject());
            case "logging/setLevel":
                if (modern) return Error(id, MethodNotFound, $"Method not found: use {MetaLogLevel} in _meta");
                return LoggingSetLevelResponse(id, hasParams ? p : default);
            case "tools/list":
                return Result(id, modern ? Modern(ToolsListResult(), ttlMs: 300_000) : ToolsListResult());
            case "tools/call":
                return ToolsCallResponse(id, hasParams ? p : default, modern);
            case "resources/list":
                return Result(id, modern ? Modern(ResourcesListResult(), ttlMs: 60_000) : ResourcesListResult());
            case "resources/templates/list":
                var templates = new JsonObject { ["resourceTemplates"] = new JsonArray() };
                return Result(id, modern ? Modern(templates, ttlMs: 300_000) : templates);
            case "resources/read":
                return ResourcesReadResponse(id, hasParams ? p : default, modern);
            default:
                return Error(id, MethodNotFound, "Method not found");
        }
    }

    /// <summary>Reads a JSON string without throwing on an unpaired surrogate escape.</summary>
    private static bool SafeString(JsonElement e, out string value)
    {
        try { value = e.GetString() ?? ""; return true; }
        catch (InvalidOperationException) { value = ""; return false; }
    }

    // ------------------------------------------------------------------ results

    private JsonObject Capabilities()
    {
        var capabilities = new JsonObject { ["tools"] = new JsonObject() };
        if (_resourceOrder.Count > 0) capabilities["resources"] = new JsonObject();
        if (_notifier is not null) capabilities["logging"] = new JsonObject();
        return capabilities;
    }

    private JsonObject ServerInfo() => new() { ["name"] = "certapi", ["version"] = _version };

    /// <summary>A 2026-07-28 result: <c>resultType</c>, the server's identity, and — for the
    /// cacheable operations — the caching hints. Everything the server lists comes from this one
    /// operator's workspace, so it is private to the caller.</summary>
    private JsonObject Modern(JsonObject result, int? ttlMs = null)
    {
        result["resultType"] = "complete";
        result["_meta"] = new JsonObject { [MetaServerInfo] = ServerInfo() };
        if (ttlMs is { } ttl)
        {
            result["ttlMs"] = ttl;
            result["cacheScope"] = "private";
        }
        return result;
    }

    private JsonObject DiscoverResult() => new()
    {
        ["supportedVersions"] = new JsonArray(ModernProtocolVersions.Concat(SupportedProtocolVersions).Select(v => (JsonNode)v).ToArray()),
        ["capabilities"] = Capabilities()
    };

    private JsonObject InitializeResult(JsonElement p)
    {
        string requested = "";
        if (p.ValueKind == JsonValueKind.Object &&
            p.TryGetProperty("protocolVersion", out var pv) && pv.ValueKind == JsonValueKind.String)
            SafeString(pv, out requested);
        string protocol = SupportedProtocolVersions.Contains(requested)
            ? requested
            : SupportedProtocolVersions[0];

        return new JsonObject
        {
            ["protocolVersion"] = protocol,
            ["capabilities"] = Capabilities(),
            ["serverInfo"] = ServerInfo()
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

    private string ToolsCallResponse(JsonElement? id, JsonElement p, bool modern)
    {
        if (p.ValueKind != JsonValueKind.Object || !p.TryGetProperty("name", out var n) ||
            n.ValueKind != JsonValueKind.String || !SafeString(n, out var name))
            return Error(id, InvalidParams, "Invalid params: name is required");
        if (!_tools.TryGetValue(name, out var tool))
            return Error(id, InvalidParams, $"Unknown tool: {name}");

        JsonElement args;
        if (!p.TryGetProperty("arguments", out args) || args.ValueKind == JsonValueKind.Null)
        {
            using var empty = JsonDocument.Parse("{}");
            args = empty.RootElement.Clone();
        }
        else if (args.ValueKind != JsonValueKind.Object)
            return Error(id, InvalidParams, "Invalid params: arguments must be an object");

        ToolResult tr;
        // Wrong or unknown arguments are reported as a tool error, so the model can correct itself.
        if (ArgumentCheck.Problem(tool.InputSchema, args, "") is { } problem)
            tr = new ToolResult(JsonSerializer.Serialize(new { error = problem }), IsError: true);
        else
        {
            try { tr = tool.Handler(args); }
            catch (OperationCanceledException) { tr = new ToolResult(JsonSerializer.Serialize(new { error = "cancelled" }), IsError: true); }
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
        return Result(id, modern ? Modern(result) : result);
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

    private string ResourcesReadResponse(JsonElement? id, JsonElement p, bool modern)
    {
        string? uri = null;
        if (p.ValueKind == JsonValueKind.Object &&
            p.TryGetProperty("uri", out var u) && u.ValueKind == JsonValueKind.String && SafeString(u, out var text0))
            uri = text0;

        if (uri is null || !_resources.TryGetValue(uri, out var resource))
            return Error(id, modern ? InvalidParams : LegacyResourceNotFound,
                $"Resource not found: {uri ?? "(no uri given)"}",
                uri is null ? null : new JsonObject { ["uri"] = uri });

        string text;
        try { text = resource.Read(); }
        catch (Exception ex) { return Error(id, InternalError, ex.Message); }

        var result = new JsonObject
        {
            ["contents"] = new JsonArray(new JsonObject
            {
                ["uri"] = resource.Uri,
                ["mimeType"] = resource.MimeType,
                ["text"] = text
            })
        };
        // Captures change what a resource says, so a read is stale at once.
        return Result(id, modern ? Modern(result, ttlMs: 0) : result);
    }

    private string LoggingSetLevelResponse(JsonElement? id, JsonElement p)
    {
        string? level = null;
        if (p.ValueKind == JsonValueKind.Object &&
            p.TryGetProperty("level", out var l) && l.ValueKind == JsonValueKind.String && SafeString(l, out var t))
            level = t;

        if (_notifier is null) return Error(id, MethodNotFound, "Logging is not enabled on this server");
        if (level is null || !_notifier.TrySetMinimum(level))
            return Error(id, InvalidParams, $"Unknown log level '{level ?? "(none)"}'");
        return Result(id, new JsonObject());
    }

    private static string Result(JsonElement? id, JsonNode result)
    {
        var env = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = IdNode(id), ["result"] = result };
        return env.ToJsonString();
    }

    private static string Error(JsonElement? id, int code, string message, JsonNode? data = null)
    {
        var error = new JsonObject { ["code"] = code, ["message"] = message };
        if (data is not null) error["data"] = data;
        var env = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = IdNode(id),
            ["error"] = error
        };
        return env.ToJsonString();
    }

    private static JsonNode? IdNode(JsonElement? id) =>
        id is null ? null : JsonNode.Parse(id.Value.GetRawText());
}

/// <summary>Checks tool arguments against the subset of JSON Schema the tools here use: type,
/// properties, required, additionalProperties (false or a schema) and items. A schema with no type
/// accepts anything. The message names the field so a model can correct the call.</summary>
internal static class ArgumentCheck
{
    public static string? Problem(JsonNode? schema, JsonElement value, string path)
    {
        if (schema is not JsonObject s) return null;
        string where = path.Length == 0 ? "arguments" : $"'{path}'";

        if (s["type"] is JsonValue tv && tv.TryGetValue<string>(out var type))
        {
            bool ok = type switch
            {
                "object" => value.ValueKind == JsonValueKind.Object,
                "array" => value.ValueKind == JsonValueKind.Array,
                "string" => value.ValueKind == JsonValueKind.String,
                "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
                "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
                "number" => value.ValueKind == JsonValueKind.Number,
                "null" => value.ValueKind == JsonValueKind.Null,
                _ => true
            };
            if (!ok) return $"{where} must be {(type is "object" or "array" or "integer" ? "an " : "a ")}{type}";
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            try { value.GetString(); }
            catch (InvalidOperationException) { return $"{where} is not valid text (an unpaired surrogate)"; }
        }

        if (value.ValueKind == JsonValueKind.Object)
        {
            var properties = s["properties"] as JsonObject;
            if (s["required"] is JsonArray required)
                foreach (var r in required)
                    if (r?.GetValue<string>() is { } name && !value.TryGetProperty(name, out _))
                        return $"{Join(path, name)} is required";
            foreach (var prop in value.EnumerateObject())
            {
                string child = Join(path, prop.Name);
                if (properties is not null && properties.TryGetPropertyValue(prop.Name, out var propSchema))
                {
                    if (Problem(propSchema, prop.Value, child.Trim('\'')) is { } p) return p;
                }
                else if (s["additionalProperties"] is JsonValue ap && ap.TryGetValue<bool>(out var allowed))
                {
                    if (!allowed) return $"{child} is not an argument this tool takes";
                }
                else if (s["additionalProperties"] is JsonObject apSchema)
                {
                    if (Problem(apSchema, prop.Value, child.Trim('\'')) is { } p) return p;
                }
            }
        }

        if (value.ValueKind == JsonValueKind.Array && s["items"] is JsonObject items)
        {
            int i = 0;
            foreach (var item in value.EnumerateArray())
            {
                if (Problem(items, item, $"{path}[{i}]") is { } p) return p;
                i++;
            }
        }
        return null;
    }

    private static string Join(string path, string name) => path.Length == 0 ? $"'{name}'" : $"'{path}.{name}'";
}
