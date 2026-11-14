using System.Diagnostics;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ApiTester.Core;

namespace ApiTester.Cli.Mcp;

/// <summary>The streaming tools, ws_exchange and sse_read. Each call opens its stream, gathers what
/// arrives within a count and a time limit, closes it and returns — a tool call has to end, and an
/// agent reasons about a transcript, not a live socket.</summary>
internal static class McpStreamTools
{
    private const int DefaultMessages = 20;
    private const double DefaultSeconds = 10;

    /// <summary>The http(s) form of a ws(s) address, which is what the allowlist, captured tokens
    /// and trust pins are keyed on.</summary>
    internal static string HttpForm(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return url;
        string? scheme = uri.Scheme switch { "wss" => Uri.UriSchemeHttps, "ws" => Uri.UriSchemeHttp, _ => null };
        if (scheme is null) return url;
        return new UriBuilder(uri) { Scheme = scheme, Port = uri.IsDefaultPort ? -1 : uri.Port }.Uri.ToString();
    }

    public static ToolResult WebSocket(JsonElement a, X509Certificate2? cert, Func<string, string?> gate,
        TransportOptions transport, TrustPredicates predicates, AppState state, bool noAutoToken,
        int timeout, long maxBody, CancellationToken ct)
    {
        string? url = Str(a, "url");
        if (string.IsNullOrWhiteSpace(url)) return Err("url is required");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("ws" or "wss"))
            return Err("url must be an absolute ws:// or wss:// URL");
        if (gate(url!) is { } refused) return Err(refused);
        if (!Limits(a, "maxMessages", out int maxMessages, out double seconds, timeout, out var limitProblem)) return Err(limitProblem!);

        var headers = Pairs(a, "headers");
        if (HeaderProblem(url!, headers) is { } headerProblem) return Err(headerProblem);
        var notes = new List<string>();
        AttachToken(state, url!, headers, noAutoToken, notes);

        var toSend = new List<string>();
        if (a.ValueKind == JsonValueKind.Object && a.TryGetProperty("messages", out var m) && m.ValueKind == JsonValueKind.Array)
            foreach (var item in m.EnumerateArray())
                if (item.ValueKind == JsonValueKind.String) toSend.Add(item.GetString()!);
                else return Err("messages must be an array of strings");

        return Run().GetAwaiter().GetResult();

        async Task<ToolResult> Run()
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(TimeSpan.FromSeconds(timeout));
            await using var session = new WebSocketSession();
            var clock = Stopwatch.StartNew();
            try
            {
                await session.ConnectAsync(url!, cert, headers, ignoreServerCertificateErrors: false, transport,
                    predicates.For(uri.Host), limit.Token);
            }
            catch (Exception ex) when (ex is System.Net.WebSockets.WebSocketException or HttpRequestException
                                           or InvalidOperationException or OperationCanceledException)
            {
                return Err(ct.IsCancellationRequested ? "cancelled" : "could not connect: " + ex.Message);
            }

            try
            {
                foreach (var text in toSend) await session.SendTextAsync(text, limit.Token);
            }
            catch (Exception ex) when (ex is System.Net.WebSockets.WebSocketException or OperationCanceledException or InvalidOperationException)
            {
                return Err(ct.IsCancellationRequested ? "cancelled" : "the connection ended while sending: " + ex.Message);
            }

            var received = new JsonArray();
            var budget = new Budget(maxBody);
            string ended = "count";
            string? closeReason = null;
            if (maxMessages > 0)
            {
                limit.CancelAfter(TimeSpan.FromSeconds(seconds));
                try
                {
                    await foreach (var msg in session.ReceiveAllAsync(limit.Token))
                    {
                        if (msg.IsClose) { ended = "closed"; closeReason = msg.CloseDescription; break; }
                        var entry = new JsonObject();
                        if (msg.IsText) entry["text"] = budget.Take(msg.Text);
                        else entry["binary"] = budget.Take(Convert.ToBase64String(msg.Bytes));
                        if (budget.Cut) entry["truncated"] = true;
                        received.Add(entry);
                        if (budget.Spent || received.Count >= maxMessages) { ended = budget.Spent ? "max-body" : "count"; break; }
                    }
                    if (ended == "count" && received.Count < maxMessages) ended = "closed";
                    // The session ends its stream quietly when the token fires: tell time from a close.
                    if (ended == "closed" && closeReason is null && limit.IsCancellationRequested)
                    {
                        if (ct.IsCancellationRequested) return Err("cancelled");
                        ended = "time";
                    }
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { ended = "time"; }
                catch (OperationCanceledException) { return Err("cancelled"); }
            }
            await session.CloseAsync(CancellationToken.None);

            var result = new JsonObject
            {
                ["connected"] = true,
                ["sent"] = toSend.Count,
                ["messages"] = received,
                ["ended"] = session.Fault is not null ? "broken" : ended,
                ["elapsedMs"] = (long)clock.Elapsed.TotalMilliseconds
            };
            if (closeReason is { Length: > 0 }) result["closeReason"] = closeReason;
            if (session.Fault is { } fault) result["error"] = fault;
            if (notes.Count > 0) result["notes"] = new JsonArray(notes.Select(n => (JsonNode)n).ToArray());
            return new ToolResult(result.ToJsonString(), IsError: session.Fault is not null);
        }
    }

    public static ToolResult EventStream(JsonElement a, X509Certificate2? cert, Func<string, string?> gate,
        TransportOptions transport, TrustPredicates predicates, AppState state, bool noAutoToken,
        int timeout, long maxBody, CancellationToken ct)
    {
        string? url = Str(a, "url");
        if (string.IsNullOrWhiteSpace(url)) return Err("url is required");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return Err("url must be an absolute http:// or https:// URL");
        if (gate(url!) is { } refused) return Err(refused);
        if (!Limits(a, "maxEvents", out int maxEvents, out double seconds, timeout, out var limitProblem)) return Err(limitProblem!);
        if (maxEvents == 0) return Err("maxEvents must be at least 1");

        var headers = Pairs(a, "headers");
        if (Str(a, "lastEventId") is { } lastId) headers.Add(new("Last-Event-ID", lastId));
        if (HeaderProblem(url!, headers) is { } headerProblem) return Err(headerProblem);
        var notes = new List<string>();
        AttachToken(state, url!, headers, noAutoToken, notes);

        return Run().GetAwaiter().GetResult();

        async Task<ToolResult> Run()
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(TimeSpan.FromSeconds(seconds));
            var clock = Stopwatch.StartNew();
            var events = new JsonArray();
            var budget = new Budget(maxBody);
            string ended = "closed";
            try
            {
                await foreach (var ev in SseClient.StreamAsync(url!, cert, headers, ignoreServerCertificateErrors: false,
                                   transport, predicates.For(uri.Host), limit.Token))
                {
                    var entry = new JsonObject();
                    if (ev.Event is not null) entry["event"] = ev.Event;
                    if (ev.Id is not null) entry["id"] = ev.Id;
                    entry["data"] = budget.Take(ev.Data);
                    if (budget.Cut) entry["truncated"] = true;
                    events.Add(entry);
                    if (budget.Spent) { ended = "max-body"; break; }
                    if (events.Count >= maxEvents) { ended = "count"; break; }
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                ended = "time";
            }
            catch (OperationCanceledException) { return Err("cancelled"); }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or IOException)
            {
                if (events.Count == 0) return Err(ex.Message);
                ended = "broken";
                notes.Add(ex.Message);
            }
            if (ended == "closed" && limit.IsCancellationRequested)
            {
                if (ct.IsCancellationRequested) return Err("cancelled");
                ended = "time";
            }

            var result = new JsonObject
            {
                ["events"] = events,
                ["ended"] = ended,
                ["elapsedMs"] = (long)clock.Elapsed.TotalMilliseconds
            };
            if (events.LastOrDefault() is JsonObject last && last["id"] is { } id) result["lastEventId"] = id.DeepClone();
            if (notes.Count > 0) result["notes"] = new JsonArray(notes.Select(n => (JsonNode)n).ToArray());
            return new ToolResult(result.ToJsonString(), IsError: ended == "broken");
        }
    }

    /// <summary>The count and time a call is bounded by. The time never exceeds --timeout, which
    /// is what an operator set as the longest any one call may hold the server.</summary>
    private static bool Limits(JsonElement a, string countName, out int count, out double seconds, int timeout, out string? problem)
    {
        count = DefaultMessages;
        seconds = Math.Min(DefaultSeconds, timeout);
        problem = null;
        if (a.ValueKind != JsonValueKind.Object) return true;
        if (a.TryGetProperty(countName, out var c))
        {
            if (c.ValueKind != JsonValueKind.Number || !c.TryGetInt32(out count) || count < 0 || count > 10_000)
            { problem = $"{countName} must be a whole number from 0 to 10000"; return false; }
        }
        if (a.TryGetProperty("seconds", out var s))
        {
            if (s.ValueKind != JsonValueKind.Number || !s.TryGetDouble(out seconds) || seconds <= 0 || double.IsNaN(seconds))
            { problem = "seconds must be a positive number"; return false; }
            seconds = Math.Min(seconds, timeout);
        }
        return true;
    }

    private static string? HeaderProblem(string url, List<KeyValuePair<string, string>> headers)
    {
        if (RequestGuards.HostHeaderMismatch(HttpForm(url), headers) is { } hostProblem) return hostProblem;
        foreach (var h in headers)
            if (HeaderValues.Problem(h.Key, h.Value) is { } valueProblem) return valueProblem;
        return null;
    }

    private static void AttachToken(AppState state, string url, List<KeyValuePair<string, string>> headers, bool noAutoToken, List<string> notes)
    {
        if (noAutoToken) return;
        string http = HttpForm(url);
        if (TokenService.AutoAttach(state, http, headers, out var expired) is not null)
            notes.Add($"using captured token for {TokenService.HostOf(http)}");
        else if (expired is not null)
            notes.Add($"captured token for {TokenService.HostOf(http)} has expired");
    }

    /// <summary>What is left of --max-body across all of a call's messages.</summary>
    private sealed class Budget(long max)
    {
        private long _left = max;
        public bool Cut { get; private set; }
        public bool Spent => _left <= 0;

        public string Take(string text)
        {
            Cut = false;
            long bytes = Encoding.UTF8.GetByteCount(text);
            if (bytes <= _left) { _left -= bytes; return text; }
            // Cut on a character, never inside a surrogate pair.
            var sb = new StringBuilder();
            long used = 0;
            for (int i = 0; i < text.Length; i++)
            {
                int width = char.IsHighSurrogate(text[i]) && i + 1 < text.Length ? 2 : 1;
                int size = Encoding.UTF8.GetByteCount(text.AsSpan(i, width));
                if (used + size > _left) break;
                sb.Append(text, i, width);
                used += size;
                i += width - 1;
            }
            _left = 0;
            Cut = true;
            return sb.ToString();
        }
    }

    private static ToolResult Err(string message) => new(JsonSerializer.Serialize(new { error = message }), true);

    private static string? Str(JsonElement a, string name) =>
        a.ValueKind == JsonValueKind.Object && a.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    private static List<KeyValuePair<string, string>> Pairs(JsonElement a, string name)
    {
        var list = new List<KeyValuePair<string, string>>();
        if (a.ValueKind == JsonValueKind.Object && a.TryGetProperty(name, out var o) && o.ValueKind == JsonValueKind.Object)
            foreach (var p in o.EnumerateObject())
                if (p.Value.ValueKind == JsonValueKind.String) list.Add(new(p.Name, p.Value.GetString()!));
        return list;
    }
}
