using System.Net.Http;
using System.Net.WebSockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace ApiTester.Core;

/// <summary>Relays a WebSocket conversation between a browser already connected to the gateway and
/// a certificate-protected upstream. HttpClient cannot carry an HTTP upgrade, so this path never
/// touches MtlsGateway.ForwardAsync and the plain relay keeps its exact byte-faithful semantics —
/// but it connects through the very same handler configuration (<see cref="MtlsGateway.CreateHandlerFor"/>),
/// so proxy, bypass list, --resolve pins, revocation, pinned thumbprints and --ca-file apply to an
/// upgrade exactly as they apply to a request.</summary>
public sealed class GatewayWebSocketRelay
{
    /// <summary>Big enough that an ordinary message arrives in one read, small enough that a slow
    /// browser cannot make the gateway hold much on its behalf.</summary>
    private const int BufferSize = 8192;

    /// <summary>How long the far direction is given to finish relaying a close once the near one has
    /// stopped: long enough for a close handshake on a live connection, short enough that a peer
    /// which will never answer cannot pin the relay open.</summary>
    private static readonly TimeSpan CloseGrace = TimeSpan.FromSeconds(5);

    /// <summary>How long the upstream gets to answer the upgrade before the browser is told it failed.</summary>
    private static readonly TimeSpan ConnectBudget = TimeSpan.FromSeconds(30);

    /// <summary>Request headers that belong to the gateway's own hop, or that ClientWebSocket
    /// writes itself, and so are never copied from the browser's upgrade onto the upstream's.</summary>
    private static readonly HashSet<string> NotForwarded = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host", "Connection", "Upgrade", "Sec-WebSocket-Key", "Sec-WebSocket-Version",
        "Sec-WebSocket-Extensions", "Sec-WebSocket-Protocol", "Sec-WebSocket-Accept",
        "Content-Length", "Transfer-Encoding", "Keep-Alive", "Proxy-Authorization", "Proxy-Authenticate",
        "TE", "Trailer"
    };

    private readonly Func<Uri, HttpMessageInvoker> _invokerFor;
    private readonly GatewayConnectionGuard? _guard;

    /// <summary>The pre-transport constructor, kept for callers that only have a certificate: the
    /// upstream is reached with the handler's defaults (system proxy, OS trust).</summary>
    public GatewayWebSocketRelay(X509Certificate2? clientCertificate, bool ignoreServerCertificateErrors)
        : this(upstream => new HttpMessageInvoker(
            MtlsGateway.CreateHandler(clientCertificate, ignoreServerCertificateErrors, transport: null, trustServerCertificate: null),
            disposeHandler: true))
    {
    }

    /// <summary>Connect through the gateway's own handler for each upstream, so an upgrade is
    /// reached exactly the way a request to the same host is.</summary>
    public GatewayWebSocketRelay(MtlsGateway gateway)
        : this(upstream => new HttpMessageInvoker(gateway.CreateHandlerFor(upstream), disposeHandler: true)) =>
        _guard = gateway.Guard;

    private GatewayWebSocketRelay(Func<Uri, HttpMessageInvoker> invokerFor) => _invokerFor = invokerFor;

    /// <summary>Connect to <paramref name="upstream"/> and pump both directions until either side
    /// closes, then relay that close status and description to the other.</summary>
    /// <param name="subProtocols">Every subprotocol the browser offered, in its order, so the
    /// upstream picks and the browser hears the same answer.</param>
    /// <param name="requestHeaders">The browser's upgrade headers. Cookie, Authorization, Origin
    /// and anything custom travel with the upgrade — a cookie- or token-authenticated socket is
    /// the ordinary case behind a login the browser already completed through the gateway.</param>
    public async Task RelayAsync(WebSocket client, Uri upstream, IReadOnlyList<string> subProtocols,
        IReadOnlyList<KeyValuePair<string, string>> requestHeaders, CancellationToken ct)
    {
        var socket = new ClientWebSocket();
        using var invoker = _invokerFor(upstream);
        try
        {
            // Asked for on the way out so the upstream can pick one and the browser's negotiated
            // subprotocol means the same thing at both ends of the relay.
            foreach (var p in subProtocols) socket.Options.AddSubProtocol(p);
            foreach (var h in requestHeaders)
            {
                if (NotForwarded.Contains(h.Key)) continue;
                // A header ClientWebSocket refuses (there are a few more restricted names) is not
                // worth failing the upgrade over; everything a login depends on is accepted.
                try { socket.Options.SetRequestHeader(h.Key, h.Value); } catch (ArgumentException) { }
            }

            using var connectBudget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectBudget.CancelAfter(ConnectBudget);
            // The invoker form is what makes the handler's proxy, pins, roots and certificate apply:
            // the socket's own Options.Proxy / RemoteCertificateValidationCallback are ignored on
            // this path, which is the point.
            await socket.ConnectAsync(ToWebSocketUri(upstream), invoker, connectBudget.Token);
        }
        catch (Exception ex)
        {
            socket.Dispose();
            // A tunnel the proxy refused is one of the gateway's failed connections, as on the HTTP relay.
            if (_guard is not null && RefusedTunnel(ex)) _guard.CountRefusedTunnel();
            // The browser has already been upgraded, so a close frame is the only way left to tell
            // it the upgrade never reached the upstream: 1011 is "a condition stopped the server
            // fulfilling the request", carrying the reason with it.
            await TryCloseAsync(client, WebSocketCloseStatus.InternalServerError, Describe(ex), ct);
            throw;   // logging the reason is the caller's job; this type has no log of its own
        }

        // The upstream socket is this relay's own, so it is disposed here; the client's belongs to
        // the listener context that accepted it and is left for its owner to dispose.
        try { await PumpBothAsync(client, socket, ct); }
        finally
        {
            // Neither socket is left open once the conversation is over: a client that never
            // finished its half of the close handshake is stopped rather than waited on.
            if (client.State is WebSocketState.Open or WebSocketState.CloseReceived or WebSocketState.CloseSent)
                client.Abort();
            socket.Dispose();
        }
    }

    private static bool RefusedTunnel(Exception? ex)
    {
        for (; ex is not null; ex = ex.InnerException)
            if (ex is HttpRequestException { HttpRequestError: HttpRequestError.ProxyTunnelError }) return true;
        return false;
    }

    /// <summary>The single-subprotocol, header-less form the first release had.</summary>
    public Task RelayAsync(WebSocket client, Uri upstream, string? subProtocol, CancellationToken ct) =>
        RelayAsync(client, upstream,
            subProtocol is null ? Array.Empty<string>() : new[] { subProtocol },
            Array.Empty<KeyValuePair<string, string>>(), ct);

    /// <summary>Tell the client why, as far as the wire allows. A browser that has already gone
    /// cannot be told anything, and failing to say so is not a second failure worth reporting.</summary>
    private static async Task TryCloseAsync(WebSocket client, WebSocketCloseStatus status, string reason, CancellationToken ct)
    {
        try { await client.CloseOutputAsync(status, reason, ct); }
        catch (WebSocketException) { }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
    }

    /// <summary>The failure as a close reason. A close description may not exceed 123 UTF-8 bytes
    /// (RFC 6455 §5.5) and an over-long one throws instead of closing, so the reason is trimmed to
    /// fit rather than costing the browser its 1011.</summary>
    private static string Describe(Exception ex)
    {
        const int MaxCloseReasonBytes = 123;
        string reason = "Upstream WebSocket connection failed: " + ex.Message;
        while (Encoding.UTF8.GetByteCount(reason) > MaxCloseReasonBytes) reason = reason[..^1];
        return reason;
    }

    private static async Task PumpBothAsync(WebSocket client, WebSocket upstream, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var toUpstream = PumpAsync(client, upstream, cts.Token);
        var toClient = PumpAsync(upstream, client, cts.Token);

        // Whichever direction ends first, the other is given a moment rather than cut off: it is
        // normally about to read the answering close frame and relay it, and stopping it there
        // would leave a peer waiting for a close that never arrives.
        var finished = await Task.WhenAny(toUpstream, toClient);
        var other = ReferenceEquals(finished, toUpstream) ? toClient : toUpstream;
        await Task.WhenAny(other, Task.Delay(CloseGrace, cts.Token));

        cts.Cancel();                               // ends the grace timer and any receive still outstanding
        await Task.WhenAll(toUpstream, toClient);   // the pumps never fault, so this only waits
    }

    /// <summary>Forward everything <paramref name="from"/> sends to <paramref name="to"/> until the
    /// conversation ends. Never throws: a browser vanishing mid-conversation is routine, and there
    /// is nothing above this to tell about it.</summary>
    private static async Task PumpAsync(WebSocket from, WebSocket to, CancellationToken ct)
    {
        var buffer = new byte[BufferSize];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var result = await from.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    // The peer's own status and reason, so each end learns why the conversation
                    // ended rather than being told a generic closure by the relay in the middle.
                    // CloseOutputAsync, not CloseAsync: the answering frame is read by the pump
                    // going the other way, and waiting for it here would be a second concurrent
                    // receive on this socket.
                    await to.CloseOutputAsync(
                        result.CloseStatus ?? WebSocketCloseStatus.NormalClosure,
                        result.CloseStatusDescription, ct);
                    return;
                }
                // Frame by frame rather than buffered whole: a message larger than the buffer
                // arrives in pieces with EndOfMessage false, and passing that flag through relays
                // them as continuations of one message of the type the first frame carried — so a
                // text message stays text however it was split, and nothing is held in memory.
                await to.SendAsync(new ArraySegment<byte>(buffer, 0, result.Count),
                                   result.MessageType, result.EndOfMessage, ct);
            }
        }
        catch (WebSocketException) { }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
    }

    /// <summary>The upstream WebSocket address for an http(s) upstream: http→ws, https→wss.</summary>
    public static Uri ToWebSocketUri(Uri upstream) => upstream.Scheme switch
    {
        // A route's upstream is written as http(s) because that is what the plain relay forwards to;
        // the same authority and path is the WebSocket endpoint under the ws(s) scheme.
        "http" => WithScheme(upstream, "ws"),
        "https" => WithScheme(upstream, "wss"),
        // Already a WebSocket address — nothing to map.
        "ws" or "wss" => upstream,
        _ => throw new ArgumentException(
            $"Cannot open a WebSocket to '{upstream}': only http, https, ws and wss can carry one.",
            nameof(upstream))
    };

    private static Uri WithScheme(Uri uri, string scheme)
    {
        // http and ws share port 80 and https and wss share 443, so a port the upstream left
        // implicit stays implicit rather than being spelled out by the UriBuilder round-trip.
        var builder = new UriBuilder(uri) { Scheme = scheme };
        if (uri.IsDefaultPort) builder.Port = -1;
        return builder.Uri;
    }
}
