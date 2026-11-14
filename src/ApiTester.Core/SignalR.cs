using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ApiTester.Core;

/// <summary>What a SignalR negotiate call answered: either a connection to open (a token and the
/// transports on offer) or a redirect to another service (Azure SignalR's url + accessToken).</summary>
public sealed record SignalRNegotiation(
    string? ConnectionToken,
    string? ConnectionId,
    IReadOnlyList<string> Transports,
    string? RedirectUrl,
    string? AccessToken,
    string? Error);

/// <summary>One record received from a hub, decoded from the JSON hub protocol.</summary>
public sealed record SignalRMessage(int Type, string? Target, JsonArray? Arguments, string? InvocationId,
    JsonNode? Result, string? Error, JsonNode? Item, string Raw);

/// <summary>The ASP.NET Core SignalR JSON hub protocol, as plain functions: negotiate, the
/// handshake, record framing (each JSON record ends with 0x1E), and the message shapes. Kept free of
/// any connection so every piece is testable on its own.</summary>
public static class SignalRProtocol
{
    public const char Separator = '\u001e';

    public const int Invocation = 1, StreamItem = 2, Completion = 3, StreamInvocation = 4,
        CancelInvocation = 5, Ping = 6, Close = 7;

    public static string Handshake => "{\"protocol\":\"json\",\"version\":1}" + Separator;

    public static string PingRecord => "{\"type\":6}" + Separator;

    /// <summary>The hub's negotiate endpoint: the hub path plus /negotiate, the hub's own query
    /// kept, and negotiateVersion=1 so the server returns a connectionToken.</summary>
    public static Uri NegotiateUri(Uri hub)
    {
        var b = new UriBuilder(hub);
        b.Path = b.Path.TrimEnd('/') + "/negotiate";
        string query = b.Query.TrimStart('?');
        b.Query = (query.Length == 0 ? "" : query + "&") + "negotiateVersion=1";
        return b.Uri;
    }

    public static SignalRNegotiation ParseNegotiate(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        string? S(string name) =>
            root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        var transports = new List<string>();
        if (root.TryGetProperty("availableTransports", out var t) && t.ValueKind == JsonValueKind.Array)
            foreach (var entry in t.EnumerateArray())
                if (entry.TryGetProperty("transport", out var name) && name.GetString() is { } n)
                    transports.Add(n);
        return new SignalRNegotiation(S("connectionToken"), S("connectionId"), transports, S("url"), S("accessToken"), S("error"));
    }

    /// <summary>The WebSocket address for a negotiated connection: http(s) becomes ws(s), and the
    /// connection token goes in id=. Without a token (negotiation skipped) the hub URL is used as is.</summary>
    public static Uri WebSocketUri(Uri hub, string? connectionToken)
    {
        var b = new UriBuilder(hub)
        {
            Scheme = hub.Scheme switch { "https" => "wss", "http" => "ws", _ => hub.Scheme },
            Port = hub.IsDefaultPort ? -1 : hub.Port
        };
        if (connectionToken is not null)
        {
            string query = b.Query.TrimStart('?');
            b.Query = (query.Length == 0 ? "" : query + "&") + "id=" + Uri.EscapeDataString(connectionToken);
        }
        return b.Uri;
    }

    /// <summary>Split a WebSocket text message into its records: one message can carry several,
    /// and the separator ends each one.</summary>
    public static IEnumerable<string> Split(string text) =>
        text.Split(Separator, StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Parse "Method", "Method [args…]" or "Method value": the method name, then an
    /// optional JSON array of arguments (any other JSON value is the single argument).</summary>
    public static (string Target, JsonArray Arguments) ParseCall(string spec)
    {
        spec = spec.Trim();
        int space = spec.IndexOfAny(new[] { ' ', '\t' });
        string target = space < 0 ? spec : spec[..space];
        string rest = space < 0 ? "" : spec[(space + 1)..].Trim();
        if (target.Length == 0) throw new FormatException("the call names no hub method");
        if (rest.Length == 0) return (target, new JsonArray());
        JsonNode? node;
        try { node = JsonNode.Parse(rest); }
        catch (JsonException ex) { throw new FormatException($"the arguments to {target} are not JSON: {ex.Message}"); }
        return (target, node as JsonArray ?? new JsonArray(node));
    }

    public static string InvocationRecord(string target, JsonArray arguments, string? invocationId, bool stream = false)
    {
        var o = new JsonObject { ["type"] = stream ? StreamInvocation : Invocation };
        if (invocationId is not null) o["invocationId"] = invocationId;
        o["target"] = target;
        o["arguments"] = arguments.DeepClone();
        return o.ToJsonString() + Separator;
    }

    public static string CompletionErrorRecord(string invocationId, string error) =>
        new JsonObject { ["type"] = Completion, ["invocationId"] = invocationId, ["error"] = error }.ToJsonString() + Separator;

    public static string CloseRecord => "{\"type\":7}" + Separator;

    /// <summary>The handshake answer is {} on success or {"error":"…"}; null error means accepted.</summary>
    public static string? HandshakeError(string record)
    {
        using var doc = JsonDocument.Parse(record);
        return doc.RootElement.TryGetProperty("error", out var e) ? e.GetString() ?? "handshake refused" : null;
    }

    public static SignalRMessage Parse(string record)
    {
        var node = JsonNode.Parse(record) as JsonObject ?? throw new FormatException("a hub record is not a JSON object");
        int type = node["type"]?.GetValue<int>() ?? 0;
        string? S(string name) => node[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
        return new SignalRMessage(type, S("target"), node["arguments"] as JsonArray, S("invocationId"),
            node["result"]?.DeepClone(), S("error"), node["item"]?.DeepClone(), record);
    }

    /// <summary>"Target arg1 arg2" as one line, each argument as compact JSON.</summary>
    public static string Describe(SignalRMessage m)
    {
        var sb = new StringBuilder(m.Target ?? "");
        if (m.Arguments is { } args)
            foreach (var a in args) sb.Append(' ').Append(a?.ToJsonString() ?? "null");
        return sb.ToString();
    }
}
