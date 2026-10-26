using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;

namespace ApiTester.Core;

/// <summary>Thrown when a forwarded request's target resolves to a host other than the
/// configured upstream — refused so the client certificate can never reach an arbitrary host.</summary>
public sealed class GatewayTargetException(string target)
    : Exception($"Request target '{target}' resolves off the configured upstream host and was refused.");

/// <summary>Thrown when a forwarded request's path matches no configured route — nothing is
/// contacted, because there is no upstream to contact.</summary>
public sealed class GatewayRouteNotFoundException(string target)
    : Exception($"Request target '{target}' matches no configured upstream route.");

/// <summary>Thrown when a dynamic target (<c>/_/https://host/…</c>) names a host outside the
/// <c>--allow</c> list. Nothing is contacted: an allowlist that could be talked around would make
/// the gateway an open proxy carrying the user's client certificate.</summary>
public sealed class GatewayHostNotAllowedException(string host)
    : Exception($"Upstream host '{host}' is not in the --allow list and was refused.");

/// <summary>Hop-by-hop headers that must never be forwarded through a proxy (RFC 7230 §6.1),
/// plus Host/Content-Length which the HTTP client manages itself.</summary>
public static class HopByHop
{
    private static readonly string[] AllNames =
    {
        "Connection", "Keep-Alive", "Proxy-Authenticate", "Proxy-Authorization",
        "TE", "Trailer", "Transfer-Encoding", "Upgrade", "Host", "Content-Length"
    };

    private static readonly HashSet<string> Lookup = new(AllNames, StringComparer.OrdinalIgnoreCase);

    /// <summary>The same ten names <see cref="Is"/> matches, exposed read-only so a pinning test can
    /// assert every one of them is also a name <see cref="HeaderRules"/> refuses to let a user
    /// manage: <see cref="MtlsGateway"/>.ForwardAsync strips these names from the outgoing request
    /// only after HeaderRules has already applied its rules, so a name added here but not to
    /// HeaderRules' Refused set would be a rule `certapi serve` accepts on the command line and then
    /// silently discards.</summary>
    public static IReadOnlyCollection<string> Names { get; } = Array.AsReadOnly(AllNames);

    public static bool Is(string headerName) => Lookup.Contains(headerName);
}

/// <summary>One incoming request to forward upstream.</summary>
/// <param name="ContentLength">The caller's declared body length, when it declared one. Carried
/// through so the upstream sees the same framing the caller used: a known length is forwarded as
/// Content-Length rather than re-framed as chunked, which strict servers (IIS, WAFs, signed-upload
/// endpoints) refuse with 411 or reject outright.</param>
public sealed record GatewayRequest(
    string Method,
    string PathAndQuery,
    IReadOnlyList<KeyValuePair<string, string>> Headers,
    Stream? Body,
    string? ContentType)
{
    public long? ContentLength { get; init; }
}

/// <summary>The upstream response. Copy <see cref="Body"/> to your output, then dispose
/// <see cref="Lifetime"/> (which releases the underlying HttpResponseMessage).</summary>
public sealed record GatewayResponse(
    int StatusCode,
    string? ReasonPhrase,
    IReadOnlyList<KeyValuePair<string, string>> Headers,
    Stream Body,
    IDisposable Lifetime);

/// <summary>The hosts a dynamic target may name. An entry is an exact host or a <c>*.suffix</c>
/// / <c>.suffix</c> wildcard; <c>*</c> alone allows every host and is the caller's explicit choice to
/// run an open relay on loopback. Matching is case-insensitive and ignores the port.</summary>
public sealed class UpstreamAllowlist
{
    private readonly List<string> _exact = new();
    private readonly List<string> _suffixes = new();
    private readonly bool _any;

    public UpstreamAllowlist(IReadOnlyList<string> entries)
    {
        foreach (var raw in entries)
        {
            string e = raw.Trim().ToLowerInvariant();
            if (e.Length == 0) continue;
            if (e == "*") { _any = true; continue; }
            if (e.StartsWith("*.", StringComparison.Ordinal)) _suffixes.Add(e[1..]);
            else if (e.StartsWith('.')) _suffixes.Add(e);
            else _exact.Add(e);
        }
        Entries = entries.Select(e => e.Trim()).Where(e => e.Length > 0).ToList();
    }

    public IReadOnlyList<string> Entries { get; }
    public bool AllowsAnyHost => _any;

    public bool IsAllowed(string host)
    {
        if (_any) return true;
        string h = host.ToLowerInvariant();
        if (_exact.Contains(h)) return true;
        foreach (var s in _suffixes)
            if (h.EndsWith(s, StringComparison.Ordinal) && h.Length > s.Length) return true;
        return false;
    }
}

/// <summary>Forwards HTTP requests to the upstreams of a <see cref="GatewayRoutes"/> table over
/// mutual TLS with a client certificate. Long-lived: construct once, forward many requests
/// concurrently, dispose at shutdown. Redirects are not followed and bodies are relayed as raw
/// bytes.</summary>
public sealed class MtlsGateway : IDisposable
{
    /// <summary>The reserved mount point for dynamic targets: <c>/_/https://host[:port]/path</c>.
    /// Reserved whether or not dynamic mode is on, so a static route can never be shadowed by it.</summary>
    public const string DynamicPrefix = "/_/";

    private readonly GatewayRoutes _routes;
    private readonly X509Certificate2? _clientCertificate;
    private readonly bool _ignoreServerCertificateErrors;
    private readonly TimeSpan _timeout;
    private readonly TransportOptions? _transport;
    private readonly Func<string, Func<X509Certificate2?, bool>>? _trustForHost;

    /// <summary>One client per distinct upstream authority, so routes that share a host share a
    /// connection pool and a TLS session rather than opening a second one each. Concurrent because
    /// dynamic mode creates entries on demand from request threads.</summary>
    private readonly ConcurrentDictionary<string, Lazy<HttpClient>> _clients;

    private UpstreamAllowlist? _dynamic;
    private WindowsAuthOptions? _windowsAuth;

    /// <summary>A single upstream mounted at the root — the shape the gateway had before it could
    /// carry several upstreams at once.</summary>
    public MtlsGateway(Uri upstreamBase, X509Certificate2? clientCertificate,
                       bool ignoreServerCertificateErrors, TimeSpan timeout)
        : this(new GatewayRoutes(new[] { new GatewayRoute("/", upstreamBase) }),
               clientCertificate, ignoreServerCertificateErrors, timeout)
    {
    }

    /// <param name="trustForHost">The per-host pinned-thumbprint predicate factory
    /// (<see cref="TrustPredicates.For"/>), so an upstream pinned with `certapi trust add` is
    /// reachable without --insecure — the same seam every other connection uses. Null keeps the
    /// pre-pin behavior.</param>
    public MtlsGateway(GatewayRoutes routes, X509Certificate2? clientCertificate,
                       bool ignoreServerCertificateErrors, TimeSpan timeout,
                       TransportOptions? transport = null,
                       Func<string, Func<X509Certificate2?, bool>>? trustForHost = null)
    {
        _routes = routes;
        _clientCertificate = clientCertificate;
        _ignoreServerCertificateErrors = ignoreServerCertificateErrors;
        _timeout = timeout;
        _transport = transport;
        _trustForHost = trustForHost;
        _clients = new ConcurrentDictionary<string, Lazy<HttpClient>>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Answer the upstream's Negotiate/NTLM challenges with these credentials — `serve
    /// --windows-auth`. Set before the first request; the handler is built with them.</summary>
    public void UseWindowsAuth(WindowsAuthOptions options) => _windowsAuth = options;

    public WindowsAuthOptions? WindowsAuth => _windowsAuth;

    /// <summary>Turn on dynamic targets under <see cref="DynamicPrefix"/>, limited to
    /// <paramref name="allow"/>. Off unless called: a gateway nobody asked to be dynamic forwards
    /// only to the routes it was given.</summary>
    public void EnableDynamicUpstreams(UpstreamAllowlist allow) => _dynamic = allow;

    public UpstreamAllowlist? DynamicAllowlist => _dynamic;

    /// <summary>The static routes this gateway was built with.</summary>
    public IReadOnlyList<GatewayRoute> Routes => _routes.All;

    /// <summary>The static site that fronts this upstream's authority, if any. The WebSocket relay
    /// is handed a URL rather than a route, so this is how it connects as the right site.</summary>
    public GatewayRoute? SiteFor(Uri upstream)
    {
        string authority = Authority(upstream);
        foreach (var r in _routes.All)
            if (Authority(r.Upstream).Equals(authority, StringComparison.OrdinalIgnoreCase))
                return r;
        return null;
    }

    /// <summary>The client certificate presented to <paramref name="upstream"/>: the site's own
    /// when a static mount to that authority names one, otherwise the gateway's default.</summary>
    public X509Certificate2? CertificateFor(Uri upstream) => SiteFor(upstream)?.Certificate ?? _clientCertificate;

    /// <summary>The connection settings a site actually gets: its own where it has them, the
    /// gateway's flags everywhere else. Null <paramref name="site"/> (a dynamic target) gets the
    /// gateway's settings unchanged.</summary>
    public (X509Certificate2? Certificate, bool Insecure, TransportOptions? Transport, WindowsAuthOptions? WindowsAuth) Effective(GatewayRoute? site)
    {
        var s = site?.Settings;
        var cert = site?.Certificate ?? _clientCertificate;
        bool insecure = s?.Insecure ?? _ignoreServerCertificateErrors;
        var transport = _transport;
        if (s is not null && (s.ExtraRoots is not null || s.Revocation is not null || s.Insecure is not null))
        {
            var baseline = transport ?? new TransportOptions();
            transport = baseline with
            {
                ExtraRoots = s.ExtraRoots ?? baseline.ExtraRoots,
                Revocation = s.Revocation ?? baseline.Revocation,
                IgnoreServerCertificateErrors = insecure
            };
        }
        var windowsAuth = s is { WindowsAuthOff: true } ? null : s?.WindowsAuth ?? _windowsAuth;
        return (cert, insecure, transport, windowsAuth);
    }

    private HttpClient ClientFor(GatewayRoute route)
    {
        var upstream = route.Upstream;
        var (cert, insecure, transport, windowsAuth) = Effective(route);
        // One pool per authority, per certificate and per site settings: two mounts to one host
        // presenting different identities or trusting different roots must not share a session.
        string key = Authority(upstream) + "|" + (cert?.Thumbprint ?? "") + "|" + (route.Settings?.PoolKey ?? "");
        string host = upstream.Host;
        return _clients.GetOrAdd(key, _ => new Lazy<HttpClient>(() => new HttpClient(
            CreateHandler(cert, insecure, transport, _trustForHost?.Invoke(host), windowsAuth, upstream),
            disposeHandler: true)
        {
            // Infinite on the client: the per-request timeout below covers the wait for headers,
            // and a body that streams for longer than that (SSE, long polls, large downloads) is
            // exactly what a relay must not cut off in the middle.
            Timeout = Timeout.InfiniteTimeSpan
        })).Value;
    }

    /// <summary>The handler every gateway connection uses — HTTP relay and WebSocket relay alike —
    /// so proxy, bypass list, --resolve pins, revocation and pinned thumbprints can never apply on
    /// one path and not the other.</summary>
    internal static SocketsHttpHandler CreateHandler(X509Certificate2? clientCertificate,
        bool ignoreServerCertificateErrors, TransportOptions? transport,
        Func<X509Certificate2?, bool>? trustServerCertificate, WindowsAuthOptions? windowsAuth = null,
        Uri? destination = null)
    {
        // A gateway built without transport options gets the defaults, which is RevocationMode.None —
        // exactly the behavior this gateway had before revocation checking existed.
        var options = transport ?? new TransportOptions();

        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,                       // the caller's app decides on 3xx
            AutomaticDecompression = DecompressionMethods.None, // relay bytes exactly as received
            DefaultProxyCredentials = CredentialCache.DefaultCredentials,
            SslOptions = new SslClientAuthenticationOptions
            {
                // The same shared table ApiClient and GrpcChannelFactory use: a revocation setting
                // honored on `send` but ignored on `serve` would be exactly the inconsistency the
                // previous release's proxy unification (ProxyConfiguration) fixed for the proxy
                // switch. The gateway relays bytes rather than reporting diagnostics, so it enforces
                // the outcome without reporting a status. The pinned-thumbprint delegate is the
                // upstream host's `certapi trust add` pin, when the caller supplied one.
                RemoteCertificateValidationCallback = (_, cert, chain, errors) =>
                {
                    var (judgedErrors, judgedFlags) = ExtraRoots.Reconsider(
                        errors, RevocationCheck.ChainFlagsOf(chain), cert, chain, options.ExtraRoots);
                    return RevocationCheck.Decide(
                        options.Revocation, options.RevocationStrict, ignoreServerCertificateErrors,
                        judgedErrors, judgedFlags,
                        trustServerCertificate is null
                            ? null
                            : () => TrustContext.Run(chain, () => trustServerCertificate(cert as X509Certificate2))).Accepted;
                }
            }
        };
        RevocationCheck.Apply(handler.SslOptions, options);
        TlsPolicy.Apply(handler.SslOptions, options with { Sni = null });
        if (clientCertificate is not null)
            handler.SslOptions.ClientCertificates = new X509CertificateCollection { clientCertificate };
        if (options.ConnectTimeout is { } connect) handler.ConnectTimeout = connect;

        // Windows Integrated Auth to the upstream, on the browser's behalf: the gateway's own
        // connection answers the 401 Negotiate/NTLM challenge, exactly as `send --windows-auth`
        // does. Connection-bound, which is why it lives on the handler and not on a request.
        if (windowsAuth is { } wa)
        {
            handler.Credentials = wa.UseDefaultCredentials
                ? CredentialCache.DefaultCredentials
                : new NetworkCredential(wa.Username, wa.Password, wa.Domain);
            handler.PreAuthenticate = true;
        }

        if (transport is not null) ApplyTransport(handler, transport, destination);

        return handler;
    }

    /// <summary>The transport controls that make sense for a relay: how to reach the upstream.
    /// Decompression and redirects are deliberately not among them — the gateway hands the browser
    /// the bytes and the 3xx the upstream sent.</summary>
    private static void ApplyTransport(SocketsHttpHandler handler, TransportOptions transport, Uri? destination)
    {
        // One client per upstream authority, so the destination is known: the system proxy's
        // failover list survives for an upstream the bypass list does not match.
        ProxyConfiguration.Apply(handler, transport, destination);

        // Only install the callback when something is actually pinned, so the ordinary path stays
        // the handler's own connect logic.
        if (transport.Resolve.Count == 0 && transport.AddressFamily == AddressFamilyPreference.Any) return;
        handler.ConnectCallback = async (context, ct) =>
        {
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            // A --resolve override replaces the address dialled and nothing else: the TLS server
            // name and the Host header still carry the hostname the route names.
            var pinned = transport.Resolve.FirstOrDefault(r =>
                r.Port == context.DnsEndPoint.Port &&
                string.Equals(r.Host, context.DnsEndPoint.Host, StringComparison.OrdinalIgnoreCase));
            try
            {
                if (pinned is not null)
                    await socket.ConnectAsync(new IPEndPoint(IPAddress.Parse(pinned.Address), context.DnsEndPoint.Port), ct);
                else if (transport.AddressFamily == AddressFamilyPreference.Any)
                    await socket.ConnectAsync(context.DnsEndPoint, ct);
                else
                    await socket.ConnectAsync(
                        await AddressSelection.ResolveAsync(context.DnsEndPoint.Host, transport.AddressFamily, ct),
                        context.DnsEndPoint.Port, ct);
                // Hand back the plain stream: the handler still applies its own SslOptions — the
                // client certificate and the validation callback — on top of it, so mutual TLS is
                // exactly what it is without a pinned address.
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        };
    }

    private static string Authority(Uri uri) => uri.GetLeftPart(UriPartial.Authority);

    /// <summary>Where a request target goes: the static route that owns it, or — under
    /// <see cref="DynamicPrefix"/> with dynamic mode on — the upstream named in the path itself,
    /// presented as a synthetic route so Location and cookie rewriting work the same way. Null when
    /// nothing owns it. Shared by the HTTP relay and the WebSocket relay so the two can never
    /// disagree about what a path means.</summary>
    public GatewayRoute? ResolveTarget(string pathAndQuery, out Uri? target, out string forwardedPathAndQuery)
    {
        target = null;
        forwardedPathAndQuery = pathAndQuery;

        if (pathAndQuery.StartsWith(DynamicPrefix, StringComparison.Ordinal))
        {
            if (_dynamic is null) return null;
            // /_/https://host:port/path?q → upstream https://host:port, forwarded /path?q
            string rest = pathAndQuery[DynamicPrefix.Length..];
            int schemeEnd = rest.IndexOf("://", StringComparison.Ordinal);
            if (schemeEnd <= 0) return null;
            string scheme = rest[..schemeEnd].ToLowerInvariant();
            if (scheme != "http" && scheme != "https") return null;
            string afterScheme = rest[(schemeEnd + 3)..];
            int slash = afterScheme.IndexOfAny(new[] { '/', '?' });
            string authority = slash < 0 ? afterScheme : afterScheme[..slash];
            string path = slash < 0 ? "/" : afterScheme[slash..];
            if (path.StartsWith('?')) path = "/" + path;
            if (!Uri.TryCreate($"{scheme}://{authority}", UriKind.Absolute, out var upstream) ||
                upstream.Host.Length == 0 || upstream.UserInfo.Length > 0)
                return null;
            if (!_dynamic.IsAllowed(upstream.Host)) throw new GatewayHostNotAllowedException(upstream.Host);

            var baseUri = new Uri(upstream.GetLeftPart(UriPartial.Authority));
            var route = new GatewayRoute(DynamicPrefix + baseUri.GetLeftPart(UriPartial.Authority), baseUri);
            forwardedPathAndQuery = path;
            target = Combine(baseUri, path);
            if (Authority(target) != Authority(baseUri)) throw new GatewayTargetException(pathAndQuery);
            return route;
        }

        var staticRoute = _routes.Resolve(pathAndQuery, out forwardedPathAndQuery);
        if (staticRoute is null) return null;
        target = Combine(staticRoute.Upstream, forwardedPathAndQuery);
        // Re-applied per route, and the reason a target like "//evil.com/x" cannot walk the client
        // certificate to a host the user never configured: it resolves off this route's upstream.
        if (Authority(target) != Authority(staticRoute.Upstream))
            throw new GatewayTargetException(pathAndQuery);
        return staticRoute;
    }

    private static Uri Combine(Uri upstream, string pathAndQuery) => new(upstream, pathAndQuery);

    /// <summary>Where a request that matches nothing probably meant to go: the mount (or dynamic
    /// host) its <c>Referer</c> came from. A page served from <c>/_/https://app.corp/</c> that
    /// references <c>/static/app.js</c> by absolute path makes the browser ask the gateway for
    /// <c>/static/app.js</c>, which no route owns; its Referer says which upstream the page was.
    /// Returns the gateway path that reaches the same resource on that upstream, or null when the
    /// path already resolves, the Referer is not the gateway's, or the Referer resolves nowhere.
    /// Only ever consulted for a path that resolves to nothing, so a real mount is never shadowed.</summary>
    public string? FallbackPathFor(string pathAndQuery, string? referer, Uri localOrigin)
    {
        if (string.IsNullOrEmpty(referer) || !pathAndQuery.StartsWith('/')) return null;
        try { if (ResolveTarget(pathAndQuery, out _, out _) is not null) return null; }
        catch (GatewayTargetException) { return null; }
        catch (GatewayHostNotAllowedException) { return null; }

        string local = localOrigin.GetLeftPart(UriPartial.Authority);
        if (!referer.StartsWith(local, StringComparison.OrdinalIgnoreCase)) return null;
        string refererPath = referer[local.Length..];
        if (refererPath.Length == 0) refererPath = "/";

        GatewayRoute? origin;
        try { origin = ResolveTarget(refererPath, out _, out _); }
        catch (GatewayTargetException) { return null; }
        catch (GatewayHostNotAllowedException) { return null; }
        if (origin is null || origin.PathPrefix == "/") return null;   // the root mount already owns everything
        return origin.PathPrefix + pathAndQuery;
    }

    /// <summary>The gateway path that reaches <paramref name="target"/>, or null when nothing the
    /// gateway serves does: the longest static mount whose upstream has that authority, else the
    /// dynamic prefix when the host is on the allow list. What Location rewriting uses to keep a
    /// cross-host redirect on the gateway.</summary>
    public string? PathFor(Uri target)
    {
        if (target.Scheme != Uri.UriSchemeHttp && target.Scheme != Uri.UriSchemeHttps) return null;
        string authority = Authority(target);
        GatewayRoute? best = null;
        foreach (var route in _routes.All)
            if (Authority(route.Upstream).Equals(authority, StringComparison.OrdinalIgnoreCase) &&
                (best is null || route.PathPrefix.Length > best.PathPrefix.Length))
                best = route;
        if (best is not null)
            return (best.PathPrefix == "/" ? "" : best.PathPrefix) + target.PathAndQuery;
        if (_dynamic is not null && _dynamic.IsAllowed(target.Host))
            return DynamicPrefix + authority + target.PathAndQuery;
        return null;
    }

    /// <summary>The handler the WebSocket relay should connect through to <paramref name="upstream"/>:
    /// identical configuration to the HTTP relay's, including the host's pin.</summary>
    public SocketsHttpHandler CreateHandlerFor(Uri upstream)
    {
        var (cert, insecure, transport, windowsAuth) = Effective(SiteFor(upstream));
        return CreateHandler(cert, insecure, transport, _trustForHost?.Invoke(upstream.Host), windowsAuth, upstream);
    }

    public async Task<GatewayResponse> ForwardAsync(GatewayRequest request, CancellationToken ct)
    {
        var route = ResolveTarget(request.PathAndQuery, out var uri, out _)
                    ?? throw new GatewayRouteNotFoundException(request.PathAndQuery);
        var http = ClientFor(route);
        using var message = new HttpRequestMessage(new HttpMethod(request.Method), uri!);

        string? contentType = request.ContentType;
        foreach (var h in request.Headers)
        {
            if (HopByHop.Is(h.Key)) continue;
            if (h.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)) { contentType ??= h.Value; continue; }
            message.Headers.TryAddWithoutValidation(h.Key, h.Value);
        }

        if (MethodAllowsBody(request.Method))
        {
            if (request.Body is not null)
            {
                message.Content = new StreamContent(request.Body);
                // The caller's own framing, forwarded as such — see GatewayRequest.ContentLength.
                if (request.ContentLength is { } declared && declared >= 0)
                    message.Content.Headers.ContentLength = declared;
            }
            else
            {
                // A POST/PUT with no body still needs a frame: without Content-Length: 0 a strict
                // server answers 411 Length Required.
                message.Content = new ByteArrayContent(Array.Empty<byte>());
            }
            if (!string.IsNullOrEmpty(contentType) && MediaTypeHeaderValue.TryParse(contentType, out var mt))
                message.Content.Headers.ContentType = mt;
        }

        // The per-request timeout covers the wait for the upstream's headers only; the body streams
        // under the caller's cancellation alone (see ClientFor).
        using var headerTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        headerTimeout.CancelAfter(_timeout);
        HttpResponseMessage response;
        try
        {
            // ResponseHeadersRead: return as soon as headers arrive; the body streams lazily.
            response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, headerTimeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"The upstream did not answer within {_timeout.TotalSeconds:0} s.");
        }

        try
        {
            var headers = new List<KeyValuePair<string, string>>();
            foreach (var h in response.Headers)
                if (!HopByHop.Is(h.Key))
                    foreach (var v in h.Value) headers.Add(new(h.Key, v));
            foreach (var h in response.Content.Headers)
                // Content-Length is hop-by-hop for the forwarded *request* (HttpClient sets it), but on
                // the *response* it frames the body the caller receives, so relay it through.
                if (!HopByHop.Is(h.Key) || h.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                    foreach (var v in h.Value) headers.Add(new(h.Key, v));

            var bodyStream = await response.Content.ReadAsStreamAsync(ct);
            return new GatewayResponse(
                (int)response.StatusCode, response.ReasonPhrase, headers, bodyStream, response);
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    private static bool MethodAllowsBody(string method) =>
        !method.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
        !method.Equals("HEAD", StringComparison.OrdinalIgnoreCase);

    public void Dispose()
    {
        foreach (var client in _clients.Values)
            if (client.IsValueCreated) client.Value.Dispose();
    }
}
