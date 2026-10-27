using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace ApiTester.Core;

/// <summary>One Server-Sent Event parsed from a <c>text/event-stream</c>: the optional event name,
/// the (possibly multi-line) data payload, and the optional id and retry hint as the server sent them.</summary>
public sealed record SseEvent(string? Event, string Data, string? Id, string? Retry);

/// <summary>Streams Server-Sent Events from an endpoint, authenticating with an optional client
/// certificate (mTLS). Parses the event-stream format per the WHATWG spec: <c>event:</c>, <c>data:</c>
/// (joined with newlines), <c>id:</c>, and <c>retry:</c> fields, blank line dispatches, and <c>:</c> comments.</summary>
public static class SseClient
{
    private const string EventStream = "text/event-stream";

    public static async IAsyncEnumerable<SseEvent> StreamAsync(
        string url,
        X509Certificate2? clientCertificate = null,
        IEnumerable<KeyValuePair<string, string>>? headers = null,
        bool ignoreServerCertificateErrors = false,
        TransportOptions? transport = null,
        Func<X509Certificate2?, bool>? trustServerCertificate = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var options = transport ?? new TransportOptions();
        if (ignoreServerCertificateErrors) options = options with { IgnoreServerCertificateErrors = true };
        var handler = StreamTransport.CreateHandler(clientCertificate, options, trustServerCertificate,
            Uri.TryCreate(url, UriKind.Absolute, out var destination) ? destination : null);
        handler.PooledConnectionIdleTimeout = Timeout.InfiniteTimeSpan;

        // Redirects are followed here, by hand, and only within the origin asked for: the handler's
        // own following would carry every typed header (an X-Api-Key, a Cookie) and present the
        // client certificate to whatever host a 302 names.
        handler.AllowAutoRedirect = false;
        using var http = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        var current = new Uri(url);
        HttpResponseMessage response;
        for (int hops = 0; ; hops++)
        {
            using var message = new HttpRequestMessage(HttpMethod.Get, current);
            message.Headers.TryAddWithoutValidation("Accept", EventStream);
            message.Headers.TryAddWithoutValidation("Cache-Control", "no-cache");
            if (headers is not null)
                foreach (var h in headers) message.Headers.TryAddWithoutValidation(h.Key, h.Value);

            response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct);
            if ((int)response.StatusCode is not (301 or 302 or 303 or 307 or 308) || response.Headers.Location is not { } location)
                break;
            var next = location.IsAbsoluteUri ? location : new Uri(current, location);
            response.Dispose();
            if (!SameOriginOrUpgrade(current, next))
                throw new HttpRequestException(
                    $"The stream redirected to another origin ({next.GetLeftPart(UriPartial.Authority)}); it is not followed, because " +
                    "the request's headers and client certificate would go with it. Connect to that URL directly if it is the right one.");
            if (hops >= 10) throw new HttpRequestException("The stream redirected more than 10 times.");
            current = next;
        }
        using var _response = response;
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        var data = new StringBuilder();
        string? eventName = null, id = null, retry = null;

        while (!ct.IsCancellationRequested)
        {
            string? line = await reader.ReadLineAsync(ct);
            if (line is null) break;                    // stream closed

            if (line.Length == 0)                       // blank line dispatches the pending event
            {
                // Only an event with data is dispatched (the WHATWG rule): a block of just "retry:"
                // or "id:" sets state for the stream, it is not a message. Its retry rides along
                // on the next real event so it is still reported.
                if (data.Length > 0)
                {
                    string payload = data.ToString();
                    if (payload.EndsWith('\n')) payload = payload[..^1];   // trim the trailing separator
                    yield return new SseEvent(eventName, payload, id, retry);
                    retry = null;
                }
                data.Clear();
                eventName = null;
                continue;
            }
            if (line[0] == ':') continue;               // comment / keep-alive heartbeat

            int colon = line.IndexOf(':');
            string field = colon < 0 ? line : line[..colon];
            string value = colon < 0 ? "" : line[(colon + 1)..];
            if (value.StartsWith(' ')) value = value[1..];   // one optional leading space is stripped

            switch (field)
            {
                case "event": eventName = value; break;
                case "data": data.Append(value).Append('\n'); break;
                case "id": id = value; break;
                case "retry": retry = value; break;
                // unknown fields are ignored per the spec
            }
        }
    }

    /// <summary>A redirect the stream follows: the same scheme, host and port, or the same host
    /// moving from http to https (on 443 or the same port) — an upgrade, never a downgrade.</summary>
    internal static bool SameOriginOrUpgrade(Uri from, Uri to)
    {
        if (!string.Equals(from.Host, to.Host, StringComparison.OrdinalIgnoreCase)) return false;
        if (string.Equals(from.Scheme, to.Scheme, StringComparison.OrdinalIgnoreCase)) return from.Port == to.Port;
        return from.Scheme == Uri.UriSchemeHttp && to.Scheme == Uri.UriSchemeHttps && (to.Port == 443 || to.Port == from.Port);
    }
}

/// <summary>A message received over a WebSocket: text or binary payload, or a close notification.</summary>
public sealed record WebSocketMessage(bool IsText, string Text, byte[] Bytes, bool IsClose, string? CloseDescription);

/// <summary>A client WebSocket connection with optional mTLS. Wraps <see cref="ClientWebSocket"/> so the
/// GUI and CLI share one send/receive surface: connect (with a client certificate and custom headers),
/// send text, receive whole messages, and close cleanly.</summary>
public sealed class WebSocketSession : IAsyncDisposable
{
    private readonly ClientWebSocket _ws = new();

    public WebSocketState State => _ws.State;

    public async Task ConnectAsync(
        string url,
        X509Certificate2? clientCertificate = null,
        IEnumerable<KeyValuePair<string, string>>? headers = null,
        bool ignoreServerCertificateErrors = false,
        TransportOptions? transport = null,
        Func<X509Certificate2?, bool>? trustServerCertificate = null,
        CancellationToken ct = default)
    {
        if (headers is not null)
            foreach (var h in headers) _ws.Options.SetRequestHeader(h.Key, h.Value);

        // The handshake goes through an invoker built on the shared stream-transport handler
        // rather than ClientWebSocketOptions' own certificate/validation knobs: the invoker owns
        // the whole transport, which is what lets a WebSocket honor the same proxy, revocation,
        // and trust-pin settings as every other connection this product opens. (When an invoker
        // is supplied, ClientWebSocket refuses those options anyway — they must live here.)
        var options = transport ?? new TransportOptions();
        if (ignoreServerCertificateErrors) options = options with { IgnoreServerCertificateErrors = true };
        var handler = StreamTransport.CreateHandler(clientCertificate, options, trustServerCertificate,
            Uri.TryCreate(url, UriKind.Absolute, out var destination) ? destination : null);
        // Kept for the session's lifetime, not a using: the upgraded connection still belongs to
        // the invoker's handler, so disposing it here would tear down the socket under the session.
        // A WebSocket handshake answered with a redirect is a failed handshake, never a hop to
        // wherever the Location names with this request's headers and certificate.
        handler.AllowAutoRedirect = false;
        _invoker = new HttpMessageInvoker(handler, disposeHandler: true);
        await _ws.ConnectAsync(new Uri(url), _invoker, ct);
    }

    private HttpMessageInvoker? _invoker;

    /// <summary>Why the stream stopped when the connection broke rather than closing — a reset, an
    /// abort — so a caller can tell that from a clean close; null otherwise.</summary>
    public string? Fault { get; private set; }

    public Task SendTextAsync(string text, CancellationToken ct = default) =>
        _ws.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, endOfMessage: true, ct);

    /// <summary>Yield each whole message as it arrives until the peer closes, the token cancels, or the
    /// socket faults. A fragmented message is reassembled before it is yielded.</summary>
    public async IAsyncEnumerable<WebSocketMessage> ReceiveAllAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        var buffer = new byte[8192];
        while (_ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            using var ms = new MemoryStream();
            WebSocketReceiveResult? result = null;
            bool faulted = false;
            do
            {
                // yield can't appear inside try/catch, so a fault sets a flag and we break out to yield after.
                try { result = await _ws.ReceiveAsync(buffer, ct); }
                catch (WebSocketException ex) { faulted = true; Fault = ex.Message; }
                catch (OperationCanceledException) { faulted = true; }
                if (faulted) break;
                if (result!.MessageType == WebSocketMessageType.Close) break;
                ms.Write(buffer, 0, result.Count);
            } while (!result!.EndOfMessage);

            if (faulted) yield break;
            if (result!.MessageType == WebSocketMessageType.Close)
            {
                yield return new WebSocketMessage(false, "", Array.Empty<byte>(), true, _ws.CloseStatusDescription);
                yield break;
            }

            var bytes = ms.ToArray();
            bool isText = result.MessageType == WebSocketMessageType.Text;
            yield return new WebSocketMessage(isText, isText ? Encoding.UTF8.GetString(bytes) : "", bytes, false, null);
        }
    }

    public async Task CloseAsync(CancellationToken ct = default)
    {
        if (_ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            try { await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", ct); }
            catch { /* best effort — the peer may already be gone */ }
        }
    }

    public ValueTask DisposeAsync()
    {
        _ws.Dispose();
        _invoker?.Dispose();
        return ValueTask.CompletedTask;
    }
}
