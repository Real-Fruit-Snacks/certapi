using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace ApiTester.Core;

public sealed class ApiClient : IDisposable
{
    // Instance-scoped, never static: two ApiClient instances must never be able to see — or ever
    // dispose — each other's pooled connections.
    private readonly HttpHandlerCache _handlerCache = new();
    private bool _disposed;

    public async Task<ApiResponse> SendAsync(
        ApiRequest request,
        X509Certificate2? clientCertificate,
        TransportOptions? transport = null,
        Func<X509Certificate2?, bool>? trustServerCertificate = null,
        System.Net.CookieContainer? cookies = null,
        WireLog? wireLog = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        transport ??= new TransportOptions();
        // Refuse an unusable combination up front rather than opening a socket and quietly
        // ignoring whichever setting lost.
        if (ValidateTransport(transport, request.Url) is { } problem)
            return new ApiResponse { Error = new ApiError(ApiErrorKind.Unknown, problem) };
        // A header name the HTTP stack cannot send (a space, an accent) would be dropped without a
        // word, and the request would go out without it.
        foreach (var header in request.Headers)
            if (header.Key.Length == 0 || header.Key.Any(c => !IsTokenChar(c)))
                return new ApiResponse
                {
                    Error = new ApiError(ApiErrorKind.Unknown,
                        $"Invalid request: '{header.Key}' is not a valid header name (letters, digits and !#$%&'*+-.^_`|~ only).")
                };

        // A URL this client cannot send (htps://, data:, a browser HAR's chrome-extension:) is one
        // failed request, not an exception that ends a whole run.
        if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var target) ||
            target.Scheme is not ("http" or "https"))
            return new ApiResponse
            {
                Error = new ApiError(ApiErrorKind.Unknown,
                    $"Invalid request: only http:// and https:// URLs can be sent, got '{(request.Url.Length > 80 ? request.Url[..80] + "…" : request.Url)}'.")
            };

        int attempt = 0;
        while (true)
        {
            attempt++;
            var response = await SendOnceAsync(request, clientCertificate, transport,
                                               trustServerCertificate, cookies, wireLog, cancellationToken);
            if (!ShouldRetry(response, transport, request.Method, attempt, cancellationToken))
                return response with { Attempts = attempt };

            var delay = RetryDelayFor(response, transport, attempt);
            // Waiting is where a retrying send spends nearly all of its time, so the wait — not just
            // the request — has to answer to the caller's cancellation.
            try { await Task.Delay(delay, cancellationToken); }
            catch (OperationCanceledException) { return response with { Attempts = attempt }; }
        }
    }

    /// <summary>Disposes every handler this client ever cached — including any pooled connections
    /// they still hold open — and refuses any further send with <see cref="ObjectDisposedException"/>.
    /// Safe to call more than once.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _handlerCache.Dispose();
    }

    /// <summary>Whether the result of one attempt earns another. Kept deterministic and free of the
    /// backoff's randomness so the decision itself is exactly what the observable behavior shows.</summary>
    private static bool ShouldRetry(
        ApiResponse response, TransportOptions transport, HttpMethod method, int attempt, CancellationToken ct)
    {
        if (transport.Retries <= 0) return false;
        // Retries counts the *re*-tries, so Retries = 2 allows a third and last attempt.
        if (attempt > transport.Retries) return false;
        if (ct.IsCancellationRequested) return false;

        // The method the user asked for, not whatever a redirect rewrote it to along the way: their
        // intent is what decides whether sending it a second time is safe.
        bool idempotent = method.Method.Equals("GET", StringComparison.OrdinalIgnoreCase) ||
                          method.Method.Equals("HEAD", StringComparison.OrdinalIgnoreCase) ||
                          method.Method.Equals("OPTIONS", StringComparison.OrdinalIgnoreCase) ||
                          method.Method.Equals("PUT", StringComparison.OrdinalIgnoreCase) ||
                          method.Method.Equals("DELETE", StringComparison.OrdinalIgnoreCase);
        if (!idempotent && !transport.RetryUnsafeMethods) return false;

        if (response.Error is not null)
        {
            if (!transport.RetryOnTransportError) return false;
            // Only the failures a second attempt could plausibly survive. A refused, untrusted, or
            // revoked certificate will be refused again — retrying it just fails slower, and a
            // revoked one even more emphatically so, since it is the issuer's own permanent
            // withdrawal — a redirect loop is still a loop on the next pass, Unknown means the
            // request itself was malformed rather than the connection, and None is the cancelled
            // case, which must not be re-sent at all.
            return response.Error.Kind is ApiErrorKind.Network
                or ApiErrorKind.Timeout
                or ApiErrorKind.ConnectionRefused
                or ApiErrorKind.ConnectionReset
                or ApiErrorKind.NameResolution
                or ApiErrorKind.ProxyFailure;
        }

        return response.StatusCode is { } status && transport.RetryOn.Contains(status);
    }

    /// <summary>How long to wait before the next attempt: exponential backoff with jitter, unless the
    /// server said when to come back. Jitter keeps a fleet of clients that all failed at the same
    /// moment from returning in one synchronized wave.</summary>
    private static TimeSpan RetryDelayFor(ApiResponse response, TransportOptions transport, int attempt)
    {
        const double capMs = 30_000;

        // A server that bothers to say when to come back knows better than any computed guess.
        if (transport.HonorRetryAfter && RetryAfterDelay(response) is { } asked)
            return TimeSpan.FromMilliseconds(Math.Clamp(asked.TotalMilliseconds, 0, capMs));

        // No delay asked for is no delay, however many times it is doubled — and taking this out
        // first keeps the multiplication below from being 0 * infinity, which is NaN rather than a
        // number any clamp can rescue.
        double baseMs = transport.RetryDelay.TotalMilliseconds;
        if (baseMs <= 0) return TimeSpan.Zero;

        // Computed in milliseconds as a double and clamped before it becomes a TimeSpan: doubling a
        // TimeSpan for a large retry count overflows long before the cap would have applied.
        double ms = baseMs * Math.Pow(2, attempt - 1);
        ms *= 0.9 + (Random.Shared.NextDouble() * 0.2);
        return TimeSpan.FromMilliseconds(Math.Clamp(ms, 0, capMs));
    }

    /// <summary>The wait a Retry-After header asks for, or null when there is none this client can make
    /// sense of — in which case the computed backoff stands rather than the send failing over a header.</summary>
    private static TimeSpan? RetryAfterDelay(ApiResponse response)
    {
        string? value = response.Headers
            .FirstOrDefault(h => h.Key.Equals("Retry-After", StringComparison.OrdinalIgnoreCase)).Value;
        if (string.IsNullOrWhiteSpace(value)) return null;
        value = value.Trim();

        // Delta-seconds is the common form; an HTTP-date is equally legal and some gateways send it.
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds))
            return seconds < 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(seconds);

        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var when))
        {
            var wait = when - DateTimeOffset.UtcNow;
            // A date that has already passed means "now", not a negative wait.
            return wait < TimeSpan.Zero ? TimeSpan.Zero : wait;
        }

        return null;
    }

    /// <summary>One attempt: build the transport, send, and follow any redirect chain to its end.
    /// Everything about a single send lives here so <see cref="SendAsync"/> can be about nothing but
    /// deciding whether to do it again.</summary>
    private async Task<ApiResponse> SendOnceAsync(
        ApiRequest request,
        X509Certificate2? clientCertificate,
        TransportOptions transport,
        Func<X509Certificate2?, bool>? trustServerCertificate,
        System.Net.CookieContainer? cookies,
        WireLog? wireLog,
        CancellationToken cancellationToken)
    {
        // Set only by the proxied path's own Validate callback below. The shared direct-connection
        // handler has no per-call state a connect attempt could write into — see
        // ServerCertificateUntrustedException for how that path reports the identical fact instead.
        // Always false when this send turns out to be direct.
        bool serverUntrusted = false;

        // Also set only by the proxied path's own Validate callback: non-null exactly when
        // revocation itself is what refused the connection (a genuine revocation, or a strict-mode
        // unknown status), mirroring the direct path's own per-connection revocationRefusal local
        // inside ConnectCallback below. Always null when this send turns out to be direct.
        RevocationStatus? proxiedRefusal = null;

        // What the revocation check reported on the connection this send actually used, whether or
        // not it was accepted — set by the proxied path's own Validate callback on every validation,
        // clean or not (ruling 6). Always NotChecked when this send turns out to be direct, since
        // the direct path's per-connection state cannot write back into a field shared by every
        // connection a cached handler might ever open; BuildConnection falls back to the recorded
        // HandshakeInfo for that path instead.
        RevocationStatus revocationStatus = RevocationStatus.NotChecked;

        // Decided once, up front, from the initial URL. The handler this send ends up using also
        // carries the bypass list in its own IWebProxy (see ProxyConfiguration.Apply), so a redirect
        // hop that lands on a differently-dispositioned host is still routed correctly on the wire —
        // this decision only chooses which of the two send paths below runs and what gets reported.
        // A PAC script that cannot be fetched or run must fail the send, not quietly become DIRECT:
        // DecideProxy below swallows the error, and so does the handler when its proxy throws. The
        // script is evaluated once more for that.
        if (transport.Proxy != ProxyMode.None && transport.PacUrl is { } pacUrl &&
            Uri.TryCreate(request.Url, UriKind.Absolute, out var pacTarget) &&
            !ProxyBypass.IsBypassed(transport.NoProxy, pacTarget))
        {
            try { _ = PacProxyFor(pacUrl).GetProxy(pacTarget); }
            catch (Exception ex)
            {
                return new ApiResponse
                {
                    Error = new ApiError(ApiErrorKind.ProxyFailure, ex.Message)
                };
            }
        }

        var (disposition, bypassRule) = DecideProxy(request.Url, transport);
        bool viaProxy = disposition == ProxyDisposition.Proxied;

        // HTTP/3 is QUIC over UDP: the direct path's hand-driven TLS-over-TCP ConnectCallback can
        // never carry it, so an H3 send takes the handler-driven construction below (the proxied
        // path's) with the proxy forced off — ValidateTransport has already refused any real
        // proxy + --http3 combination, so forcing it off here changes nothing a user asked for.
        // The cost, stated rather than hidden: an H3 send reports the server certificate (the
        // SslOptions callback still runs under QUIC) but not the cipher or negotiated protocol,
        // which only the hand-driven path can observe.
        bool handlerDrivesTls = viaProxy || transport.Version is HttpVersionMode.Http3;

        // The two paths differ only in which handler answers the send, whether this call owns it
        // outright, and where its handshake diagnostics can be found. Everything else below — the
        // send/redirect/response loop, cookies, retries-of-hops, error classification — is written
        // once and runs the same for both, via that one difference.
        SendTransport sendTransport;
        if (handlerDrivesTls)
        {
            // Captured per call: this handler belongs to this one send alone, so a plain closure is
            // all the sharing it will ever need. Contrast the direct path's ConnectCallback, which is
            // shared by every call that reuses the cached handler and so cannot capture anything
            // call-specific — that is exactly why this handler is never cached, whatever else about
            // the request would otherwise have let it be: the CONNECT tunnel's TLS is established by
            // the handler itself and observed through a handler-wide callback that cannot be
            // attributed to the connection it belongs to. Sharing it would either blank these
            // diagnostics or blur them across origins.
            //
            // The tunnel's own protocol/cipher/client-cert-sent facts are never observable here —
            // only the server certificate is, because Validate runs either way — so the
            // HandshakeInfo built below hardcodes those three to null/null/false rather than
            // capturing variables nothing would ever assign.
            string? srvSubject = null, srvIssuer = null, srvThumb = null, srvSpki = null, srvIssuerThumb = null;
            DateTime? srvNotAfter = null;
            IReadOnlyList<string> chain = Array.Empty<string>();

            bool Validate(object sender, X509Certificate? cert, X509Chain? certChain, SslPolicyErrors errors)
            {
                if (cert is not null)
                {
                    using var c = new X509Certificate2(cert);
                    srvSubject = c.Subject;
                    srvIssuer = c.Issuer;
                    srvThumb = c.Thumbprint;
                    srvNotAfter = c.NotAfter;
                    srvSpki = TrustService.SpkiSha256(c);
                }
                if (certChain is not null)
                {
                    chain = certChain.ChainElements.Select(e => e.Certificate.Subject).ToList();
                    srvIssuerThumb = IssuerThumbprintOf(certChain, srvThumb);
                }

                // The shared table (RevocationCheck.Decide) replaces the hand-rolled "clean chain /
                // insecure / pinned thumbprint" ladder that used to live here: it already reproduces
                // that acceptance rule for RevocationMode.None byte for byte, plus the
                // revocation-aware rules for Offline/Online, so this callback and the direct path's
                // equivalent below can never drift on how a certificate is judged. Called on every
                // validation, including a clean chain, so Checked/NotEnforced are ever reported at
                // all (ruling 6).
                // The user's own roots (--ca-file) get to vouch for the chain before the table
                // judges it; everything else the handshake reported stands.
                var (judgedErrors, judgedFlags) = ExtraRoots.Reconsider(
                    errors, RevocationCheck.ChainFlagsOf(certChain), cert, certChain, transport.ExtraRoots);
                var decision = RevocationCheck.Decide(
                    transport.Revocation,
                    transport.RevocationStrict,
                    transport.IgnoreServerCertificateErrors,
                    judgedErrors,
                    judgedFlags,
                    trustServerCertificate is null ? null : () => TrustContext.Run(certChain, judgedErrors, TrustContext.HostOf(sender, transport.Sni), () =>
                    {
                        using var c = cert is null ? null : new X509Certificate2(cert);
                        return trustServerCertificate(c);
                    }));
                revocationStatus = decision.Status;
                if (decision.Accepted) return true;
                proxiedRefusal = decision.RefusedByRevocation;
                serverUntrusted = true;
                return false;
            }

            // This handler serves exactly one send, so it is built for that send's destination:
            // the system proxy's own failover list survives when the destination is not bypassed.
            var handler = BuildCommonHandler(transport, request.WindowsAuth,
                Uri.TryCreate(request.Url, UriKind.Absolute, out var destination) ? destination : null);
            // An H3-direct send must never consult a proxy the validation already ruled out:
            // QUIC cannot go through the TCP proxy protocols this product speaks.
            if (!viaProxy) handler.UseProxy = false;
            // Let the handler drive the tunnel/QUIC + TLS; capture the server cert in the callback.
            handler.SslOptions = new SslClientAuthenticationOptions { RemoteCertificateValidationCallback = Validate };
            RevocationCheck.Apply(handler.SslOptions, transport);
            TlsPolicy.Apply(handler.SslOptions, transport with { Sni = null });   // a tunnel's SNI is the handler's
            if (clientCertificate is not null)
                handler.SslOptions.ClientCertificates = new X509CertificateCollection { clientCertificate };

            sendTransport = new SendTransport(
                handler,
                disposeHandler: true,
                // The origin argument is meaningless here — there is exactly one handshake this call
                // could ever have observed, the one Validate above just captured.
                lookupHandshake: _ => new HandshakeInfo(
                    null, null, false, srvSubject, srvIssuer, srvThumb, srvNotAfter, chain, revocationStatus)
                    { ServerCertificateSpkiSha256 = srvSpki, ServerCertificateIssuerThumbprint = srvIssuerThumb },
                release: null);
        }
        else if (wireLog is not null)
        {
            // A wire log belongs to ONE send, and the direct handler is cached and shared by every
            // send with the same key — so a tapped handler must never enter the cache, or one
            // request's bytes would appear in another's transcript. This send owns its handler
            // outright and disposes it, which also means it does not reuse a pooled connection:
            // stated in the documentation, because that is a real (and for this purpose welcome)
            // consequence — you see the handshake as well as the request.
            var entry = CreateDirectHandlerEntry(
                clientCertificate, transport, trustServerCertificate, request.WindowsAuth, wireLog);
            // The entry owns a copy of the client certificate as well as the handler, so it is the
            // entry that is disposed when the send is over — not only the handler inside it.
            sendTransport = new SendTransport(
                entry.Handler, disposeHandler: false, lookupHandshake: entry.Lookup, release: entry.Dispose);
        }
        else
        {
            var key = HandlerKey.For(clientCertificate, transport, trustServerCertificate, request.WindowsAuth);
            var entry = _handlerCache.Rent(key, () =>
                CreateDirectHandlerEntry(clientCertificate, transport, trustServerCertificate, request.WindowsAuth));
            sendTransport = new SendTransport(
                entry.Handler,
                disposeHandler: false,
                lookupHandshake: entry.Lookup,
                release: () => _handlerCache.Return(entry));
        }

        // Declared outside the try so a failure part-way along a chain still reports the hops that
        // had already been taken — that is where the client certificate went.
        var hops = new List<RedirectHop>();
        // Every origin this send visited, in hop order, so BuildConnection can find the handshake
        // that established the connection actually used even when the very last hop opened none of
        // its own (an https -> http downgrade, say).
        var visitedOrigins = new List<string>();
        // A caller who supplies no jar gets a private one for this send only — precisely what the
        // handler's own fresh default cookie container gave every call before this, including
        // carrying a cookie across the hops of one redirect chain but never into a later SendAsync.
        var jar = cookies ?? CookieJarFile.NewContainer();

        // observed overrides what a completed handshake recorded — used by the HttpRequestException
        // catch below, where a server-certificate refusal means the handshake never completed and
        // so never reached RecordHandshake at all; every other caller leaves it null and gets
        // whatever the connection this response actually used recorded, or NotChecked if none did.
        ConnectionInfo BuildConnection(RevocationStatus? observed = null)
        {
            // Scan backwards: the most recent visited origin that has a recorded handshake is the
            // one that established the connection this response actually came back over. Never an
            // unrelated origin's data, because only origins this same send visited are ever
            // considered.
            HandshakeInfo? info = null;
            for (int i = visitedOrigins.Count - 1; i >= 0 && info is null; i--)
                info = sendTransport.LookupHandshake(visitedOrigins[i]);

            return new()
            {
                ViaProxy = viaProxy,
                ProxyUri = viaProxy ? ProxyUriFor(request.Url, transport) : null,
                ProxyDisposition = disposition,
                ProxyBypassRule = bypassRule,
                TlsProtocol = info?.TlsProtocol,
                CipherSuite = info?.CipherSuite,
                ClientCertificateSent = info?.ClientCertificateSent ?? false,
                ClientCertificateSubject = clientCertificate?.Subject,
                ServerCertificateSubject = info?.ServerCertificateSubject,
                ServerCertificateIssuer = info?.ServerCertificateIssuer,
                ServerCertificateThumbprint = info?.ServerCertificateThumbprint,
                ServerCertificateIssuerThumbprint = info?.ServerCertificateIssuerThumbprint,
                ServerCertificateSpkiSha256 = info?.ServerCertificateSpkiSha256,
                ServerCertificateNotAfter = info?.ServerCertificateNotAfter,
                ServerCertificateChain = info?.ServerCertificateChain ?? Array.Empty<string>(),
                RevocationMode = transport.Revocation,
                RevocationStatus = observed ?? info?.RevocationStatus ?? RevocationStatus.NotChecked
            };
        }

        // Disposed in this order at method exit: http first (harmless either way — the send it
        // wraps has already finished), then the lease this send holds on a cached handler (a no-op
        // for the proxied path, whose private handler http already owns via disposeHandler above).
        using var handlerLease = sendTransport;
        using var http = new HttpClient(sendTransport.Handler, sendTransport.DisposeHandler) { Timeout = request.Timeout };
        var stopwatch = Stopwatch.StartNew();
        try
        {
            string currentUrl = request.Url;
            OriginBoundCredentials.Origin.Value = Uri.TryCreate(request.Url, UriKind.Absolute, out var credentialOrigin)
                ? OriginKeyOf(credentialOrigin) : null;
            var currentMethod = request.Method;
            var sentHeaders = request.Headers.ToList();
            bool sendBody = true;

            while (true)
            {
                var hopUri = new Uri(currentUrl);
                visitedOrigins.Add(OriginKey(hopUri));
                // Where THIS hop began, so a recorded redirect can carry its own duration rather
                // than the whole chain's. A HAR entry with a zero here reads as "instant" in every
                // viewer, which is a lie about a hop that took real time.
                var hopStarted = stopwatch.Elapsed;

                using var message = BuildMessage(
                    request, transport, currentUrl, currentMethod, sentHeaders, sendBody);

                // Composed by hand rather than left to the handler (see BuildCommonHandler's
                // UseCookies = false): added even if the caller set a Cookie header themselves,
                // because .NET's own cookie layer appends rather than replaces, and this has to match.
                string cookieHeader = jar.GetCookieHeader(hopUri);
                if (!string.IsNullOrEmpty(cookieHeader))
                    message.Headers.TryAddWithoutValidation("Cookie", cookieHeader);

                using var response = await http.SendAsync(
                    message, HttpCompletionOption.ResponseContentRead, cancellationToken);

                // Applied before the redirect decision below, so the next hop carries what this one
                // just set.
                if (response.Headers.TryGetValues("Set-Cookie", out var setCookieValues))
                {
                    foreach (var value in setCookieValues)
                    {
                        // A malformed cookie is ignored, exactly as the handler's own cookie layer does.
                        try { jar.SetCookies(hopUri, value); }
                        catch (System.Net.CookieException) { /* ignored, as the handler's layer does */ }
                    }
                }

                if (transport.FollowRedirects && RedirectTarget(response, currentUrl) is { } target)
                {
                    // Refuse the hop rather than take it: a redirect loop would otherwise present
                    // the client certificate forever.
                    if (hops.Count >= transport.MaxRedirects)
                    {
                        stopwatch.Stop();
                        return new ApiResponse
                        {
                            Elapsed = stopwatch.Elapsed,
                            Error = new ApiError(ApiErrorKind.TooManyRedirects,
                                $"Stopped after {transport.MaxRedirects} redirect(s): {currentUrl} " +
                                $"redirected to {target} (raise --max-redirs to follow more)."),
                            Redirects = hops,
                            Connection = BuildConnection()
                        };
                    }

                    // A different scheme, host, or port means the client certificate — and any
                    // credential still attached — is about to be presented somewhere the user
                    // never named, which is the fact this whole record exists to expose.
                    var from = new Uri(currentUrl);
                    bool crossOrigin =
                        !string.Equals(from.Scheme, target.Scheme, StringComparison.OrdinalIgnoreCase) ||
                        !string.Equals(from.Host, target.Host, StringComparison.OrdinalIgnoreCase) ||
                        from.Port != target.Port;
                    bool carriedAuthorization = sentHeaders.Any(h =>
                        h.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase));
                    // https -> http takes the request off TLS entirely: no client certificate, and
                    // whatever is still in the headers travels in the clear.
                    bool schemeDowngrade =
                        string.Equals(from.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(target.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase);

                    hops.Add(new RedirectHop(
                        (int)response.StatusCode, currentUrl, target.ToString(),
                        crossOrigin && carriedAuthorization, schemeDowngrade)
                    { Elapsed = stopwatch.Elapsed - hopStarted });

                    // .NET strips Authorization on its own cross-origin redirects; match that, but
                    // having recorded it, so a 401 from the new origin is explicable.
                    // Cookie and Proxy-Authorization typed by the user go too, as curl does
                    // (CVE-2022-27776): the jar scopes its own cookies, a typed one is for the host
                    // it was typed for.
                    if (crossOrigin)
                        sentHeaders.RemoveAll(h => h.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase) ||
                                                   h.Key.Equals("Cookie", StringComparison.OrdinalIgnoreCase) ||
                                                   h.Key.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase));

                    // Curl and every browser rewrite a redirected POST as a GET on a 301/302; the
                    // entity was for the resource that moved, not for the one it moved to. A 303
                    // says so for every method — it exists to turn a submission into a retrieval.
                    // 307/308 exist precisely to preserve both, so they touch nothing.
                    bool isPost = string.Equals(currentMethod.Method, "POST", StringComparison.OrdinalIgnoreCase);
                    bool isSafe = string.Equals(currentMethod.Method, "GET", StringComparison.OrdinalIgnoreCase) ||
                                  string.Equals(currentMethod.Method, "HEAD", StringComparison.OrdinalIgnoreCase);
                    if (((int)response.StatusCode is 301 or 302 && isPost) ||
                        ((int)response.StatusCode is 303 && !isSafe))
                    {
                        currentMethod = HttpMethod.Get;
                        sendBody = false;
                    }

                    currentUrl = target.ToString();
                    continue;
                }

                stopwatch.Stop();

                var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
                var headers = response.Headers
                    .Concat(response.Content.Headers)
                    .SelectMany(h => h.Value.Select(v => new KeyValuePair<string, string>(h.Key, v)))
                    .ToList();

                return new ApiResponse
                {
                    StatusCode = (int)response.StatusCode,
                    ReasonPhrase = response.ReasonPhrase,
                    Headers = headers,
                    Body = bytes,
                    ContentType = response.Content.Headers.ContentType?.ToString(),
                    Elapsed = stopwatch.Elapsed,
                    Redirects = hops,
                    Connection = BuildConnection()
                };
            }
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            return new ApiResponse
            {
                Elapsed = stopwatch.Elapsed,
                Error = new ApiError(ApiErrorKind.Timeout, "The request timed out.", Flatten(ex)),
                Redirects = hops,
                Connection = BuildConnection()
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            return new ApiResponse
            {
                Elapsed = stopwatch.Elapsed,
                Error = new ApiError(ApiErrorKind.None, "Request cancelled."),
                Redirects = hops
            };
        }
        catch (HttpRequestException ex)
        {
            stopwatch.Stop();
            var socketError = FirstSocketError(ex);
            // The shared direct-path handler has no per-request state to set a flag in, so it
            // reports a server-certificate refusal through an exception instead — refusal is how
            // that fact reaches this block. proxiedRefusal is the proxied path's own equivalent.
            var refusal = FirstInner<IServerCertificateRefusal>(ex);
            // Non-null exactly when revocation itself is what refused the connection (a genuine
            // revocation, or a strict-mode unknown status) — null for an ordinary trust refusal,
            // even one that also carries a (non-fatal) revocation status of its own.
            var refusedByRevocation = proxiedRefusal ?? (refusal as ServerCertificateRevokedException)?.RevocationStatus;
            // What to report on the failing response's Connection regardless of *why* it failed:
            // the proxied path's Validate already recorded this in revocationStatus even on an
            // ordinary trust refusal; the direct path's exception carries the same fact when there
            // is one, and NotChecked (revocationStatus's untouched default for that path) otherwise.
            var observedRevocation = refusal?.RevocationStatus ?? revocationStatus;
            var kind =
                // Checked FIRST, before the ordinary server-certificate-refusal branch below: both
                // ServerCertificateRevokedException and ServerCertificateUntrustedException derive
                // from AuthenticationException on purpose (see their own remarks), so the
                // AuthenticationException branch further down would otherwise misclassify a
                // revocation refusal as a merely-refused client certificate — the same trap the
                // comment there already warns about for an ordinary trust refusal.
                refusedByRevocation == RevocationStatus.Revoked ? ApiErrorKind.ServerCertificateRevoked
                // serverUntrusted is the proxied path's own per-request flag (always false on the
                // direct path); refusal is how the shared direct-path handler reports either kind of
                // server-certificate refusal instead, including a strict-unknown refusal (ruling 2
                // asks for exactly one new error kind, not two).
                : serverUntrusted || refusal is not null ? ApiErrorKind.ServerCertificateUntrusted
                : HasInner<AuthenticationException>(ex) ? ApiErrorKind.CertificateRefused
                : socketError switch
                {
                    SocketError.ConnectionRefused => ApiErrorKind.ConnectionRefused,
                    SocketError.ConnectionReset => ApiErrorKind.ConnectionReset,
                    SocketError.HostNotFound or SocketError.TryAgain or SocketError.NoData
                        => ApiErrorKind.NameResolution,
                    _ => ex.HttpRequestError == HttpRequestError.ProxyTunnelError
                        ? ApiErrorKind.ProxyFailure
                        : ApiErrorKind.Network
                };
            // The platform's wrapped text for a revocation refusal is not guaranteed to say
            // anything about revocation at all, let alone stay stable across releases (ruling 9),
            // so these two cases use the repo's own constants instead of ex.Message. Gated on kind
            // (not merely on the observed status) so an unrelated failure that happens to follow a
            // successful-but-Unknown/NotEnforced handshake on this same connection is never
            // misreported as a strict-unknown refusal.
            var message =
                kind == ApiErrorKind.ServerCertificateRevoked ? RevocationCheck.RevokedMessage
                : kind == ApiErrorKind.ServerCertificateUntrusted && refusedByRevocation == RevocationStatus.Unknown
                    ? RevocationCheck.StrictUnknownMessage
                : ex.Message;
            return new ApiResponse
            {
                Elapsed = stopwatch.Elapsed,
                Error = new ApiError(kind, message, Flatten(ex), socketError),
                Redirects = hops,
                Connection = BuildConnection(observedRevocation)
            };
        }
        catch (IOException ex)   // a multipart file part that can't be read
        {
            stopwatch.Stop();
            return new ApiResponse
            {
                Elapsed = stopwatch.Elapsed,
                Error = new ApiError(ApiErrorKind.Unknown, "Could not read a request file: " + ex.Message, Flatten(ex)),
                Redirects = hops
            };
        }
        catch (Exception ex) when (ex is UriFormatException or FormatException or InvalidOperationException
                                       or NotSupportedException or ArgumentException)
        {
            stopwatch.Stop();
            return new ApiResponse
            {
                Elapsed = stopwatch.Elapsed,
                Error = new ApiError(ApiErrorKind.Unknown, "Invalid request: " + ex.Message, Flatten(ex)),
                Redirects = hops
            };
        }
    }

    /// <summary>The one difference between the proxied and direct send paths: which handler answers
    /// the send, whether this call owns it outright (and so must dispose it when the send ends), and
    /// where its handshake diagnostics live. Isolating exactly that is what lets the
    /// send/redirect/response loop in <see cref="SendOnceAsync"/> be written once instead of forked
    /// into two near-identical copies.</summary>
    private sealed class SendTransport : IDisposable
    {
        public SocketsHttpHandler Handler { get; }
        public bool DisposeHandler { get; }
        public Func<string, HandshakeInfo?> LookupHandshake { get; }
        private readonly Action? _release;

        public SendTransport(
            SocketsHttpHandler handler, bool disposeHandler,
            Func<string, HandshakeInfo?> lookupHandshake, Action? release)
        {
            Handler = handler;
            DisposeHandler = disposeHandler;
            LookupHandshake = lookupHandshake;
            _release = release;
        }

        // Drops this send's lease on a cached handler (direct path); a no-op for the proxied path,
        // whose private handler the owning HttpClient disposes on its own via DisposeHandler.
        public void Dispose() => _release?.Invoke();
    }

    /// <summary>The handler settings both send paths need, before either the proxied path's
    /// SslOptions or the direct path's ConnectCallback is layered on top of it.</summary>
    private static SocketsHttpHandler BuildCommonHandler(TransportOptions transport, WindowsAuthOptions? windowsAuth, Uri? destination = null)
    {
        var handler = new SocketsHttpHandler
        {
            // Use the machine's configured proxy — including "Automatically detect settings"
            // (WPAD) and a "Use automatic configuration script" (PAC) from Internet Options —
            // authenticating with the signed-in user's Windows credentials when required.
            DefaultProxyCredentials = CredentialCache.DefaultCredentials
        };
        ProxyConfiguration.Apply(handler, transport, destination);
        // Never let the handler follow redirects: it does so internally, so the intermediate
        // responses — and with them every hop, its status, and where the client certificate was
        // presented — are unobservable. SendAsync runs the chain itself instead.
        handler.AllowAutoRedirect = false;
        // Decoding is the default every other HTTP client uses; turning it off relays the bytes
        // exactly as the server framed them, which is what a byte-exact relay test needs.
        handler.AutomaticDecompression = transport.Decompress
            ? DecompressionMethods.All
            : DecompressionMethods.None;
        // A shared handler must never carry any one caller's cookie jar — see the per-send cookie
        // handling in SendOnceAsync, which reproduces the old per-handler-default-container behavior
        // by hand instead of leaving it to the handler.
        handler.UseCookies = false;
        // A separate connect budget when one was asked for: on a network that blackholes unknown
        // hosts, this is what turns a 100-second hang into "unreachable" in a few seconds.
        if (transport.ConnectTimeout is { } connect) handler.ConnectTimeout = connect;

        // Windows Integrated Auth (Negotiate/NTLM): set server credentials so the handler runs the
        // challenge/response handshake automatically. Connection-bound, so it needs the pooled
        // connection to persist across the handshake legs — which SocketsHttpHandler does.
        if (windowsAuth is { } wa)
        {
            // Bound to the origin the request was made to: redirects are followed by hand on this
            // same handler, so .NET's own rule (no credentials after a redirect) never applies, and
            // a 302 to any other host would otherwise be answered with the user's password.
            handler.Credentials = new OriginBoundCredentials(wa.UseDefaultCredentials
                ? CredentialCache.DefaultCredentials
                : new NetworkCredential(wa.Username, wa.Password, wa.Domain));
            handler.PreAuthenticate = true;
        }
        return handler;
    }

    /// <summary>Builds the one handler an entire family of equivalent direct-connection requests will
    /// share, and the <see cref="HandlerEntry"/> that owns it. Everything captured here — the client
    /// certificate, the trust decision, the transport's TLS-relevant settings — is exactly what
    /// <see cref="HandlerKey"/> keys sharing on, so every call that ever reaches this handler already
    /// agrees on all of it.</summary>
    /// <param name="wireLog">When supplied, the plaintext above the TLS stream is copied into it.
    /// A handler built with one is never cached — see the call site — because a log belongs to a
    /// single send.</param>
    private static HandlerEntry CreateDirectHandlerEntry(
        X509Certificate2? clientCertificate,
        TransportOptions transport,
        Func<X509Certificate2?, bool>? trustServerCertificate,
        WindowsAuthOptions? windowsAuth,
        WireLog? wireLog = null)
    {
        var handler = BuildCommonHandler(transport, windowsAuth);

        var certificateCopy = clientCertificate is null ? null : new X509Certificate2(clientCertificate);

        // Resolved before the callback below is ever invoked: the entry cannot exist until after the
        // handler it wraps does, but the callback needs to record into that same entry.
        HandlerEntry? self = null;

        // Establish the transport ourselves so we can read the negotiated TLS details. Unlike the
        // per-call handler it replaces, this callback runs once per *connection* this handler ever
        // opens — never once per request — because the handler itself is now shared across requests.
        handler.ConnectCallback = async (context, ct) =>
        {
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            // A --resolve override replaces the address we dial and nothing else: TargetHost
            // (SNI) below and the Host header the handler wrote both keep the original name,
            // so the server sees an ordinary request for the hostname the user typed.
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
                    // -4 / -6: resolve here and dial only the family asked for.
                    await socket.ConnectAsync(
                        await AddressSelection.ResolveAsync(context.DnsEndPoint.Host, transport.AddressFamily, ct),
                        context.DnsEndPoint.Port, ct);
            }
            catch { socket.Dispose(); throw; }

            var network = new NetworkStream(socket, ownsSocket: true);
            if (context.InitialRequestMessage.RequestUri!.Scheme != Uri.UriSchemeHttps)
                return network;

            // Declared inside the callback rather than in the method that builds the handler: this
            // callback runs once per connection, so these locals are per-connection even though the
            // handler — and this delegate instance — is shared by every request that reuses it. That
            // is what keeps a shared handler safe to call concurrently: two connections opening at
            // once never trample the same variables the way fields on the handler would.
            string? srvSubject = null, srvIssuer = null, srvThumb = null, srvSpki = null, srvIssuerThumb = null;
            DateTime? srvNotAfter = null;
            IReadOnlyList<string> chain = Array.Empty<string>();
            bool untrusted = false;
            // What the revocation check reported on this one connection, recorded on every
            // validation (clean or not, ruling 6) so a successful handshake's HandshakeInfo is
            // truthful; and, separately, whether revocation itself is what refused it (non-null for
            // a genuine revocation or a strict-mode unknown status, null for an ordinary trust
            // refusal) — read by the catch below to decide which exception type to throw.
            RevocationStatus revocationStatus = RevocationStatus.NotChecked;
            RevocationStatus? revocationRefusal = null;

            bool Validate(object sender, X509Certificate? cert, X509Chain? certChain, SslPolicyErrors errors)
            {
                if (cert is not null)
                {
                    using var c = new X509Certificate2(cert);
                    srvSubject = c.Subject;
                    srvIssuer = c.Issuer;
                    srvThumb = c.Thumbprint;
                    srvNotAfter = c.NotAfter;
                    srvSpki = TrustService.SpkiSha256(c);
                }
                if (certChain is not null)
                {
                    chain = certChain.ChainElements.Select(e => e.Certificate.Subject).ToList();
                    srvIssuerThumb = IssuerThumbprintOf(certChain, srvThumb);
                }

                // As on the proxied path above: the shared table replaces the hand-rolled trust
                // ladder that used to live here, so both callbacks can never drift on how a
                // certificate is judged. Called on every validation, including a clean chain, so
                // Checked/NotEnforced are ever reported at all (ruling 6).
                var (judgedErrors, judgedFlags) = ExtraRoots.Reconsider(
                    errors, RevocationCheck.ChainFlagsOf(certChain), cert, certChain, transport.ExtraRoots);
                var decision = RevocationCheck.Decide(
                    transport.Revocation,
                    transport.RevocationStrict,
                    transport.IgnoreServerCertificateErrors,
                    judgedErrors,
                    judgedFlags,
                    trustServerCertificate is null ? null : () => TrustContext.Run(certChain, judgedErrors, context.DnsEndPoint.Host, () =>
                    {
                        using var c = cert is null ? null : new X509Certificate2(cert);
                        return trustServerCertificate(c);
                    }));
                revocationStatus = decision.Status;
                if (decision.Accepted) return true;
                revocationRefusal = decision.RefusedByRevocation;
                untrusted = true;
                return false;
            }

            // A tapped stream when a byte transcript was asked for, an ordinary one otherwise —
            // and a SUBCLASS either way, because the handler decides whether to run its own TLS
            // handshake by checking whether this is an SslStream. See TappedSslStream's remarks.
            var ssl = wireLog is null
                ? new SslStream(network, leaveInnerStreamOpen: false, Validate)
                : new TappedSslStream(network, leaveInnerStreamOpen: false, Validate, wireLog);
            // TargetHost stays the requested hostname so SNI is unaffected by a --resolve override.
            var sslOptions = new SslClientAuthenticationOptions { TargetHost = context.DnsEndPoint.Host };
            RevocationCheck.Apply(sslOptions, transport);
            TlsPolicy.Apply(sslOptions, transport);   // the TLS floor, and --sni when one was named
            // Without ALPN a hand-driven TLS stream can never negotiate the pinned version.
            if (transport.Version is HttpVersionMode.Http2)
                sslOptions.ApplicationProtocols = [SslApplicationProtocol.Http2];
            else if (transport.Version is HttpVersionMode.Http11)
                sslOptions.ApplicationProtocols = [SslApplicationProtocol.Http11];
            // The entry-owned copy, never the caller's own object: this callback is shared by every
            // connection this handler ever opens, long after the SendAsync call that created the
            // handler has returned, so it must not depend on the caller's X509Certificate2 staying
            // alive or undisposed (see certificateCopy's own remarks above).
            if (certificateCopy is not null)
                sslOptions.ClientCertificates = new X509CertificateCollection { certificateCopy };

            try { await ssl.AuthenticateAsClientAsync(sslOptions, ct); }
            catch (Exception ex)
            {
                await ssl.DisposeAsync();
                // Checked before the ordinary untrusted case: a genuine revocation or a strict-mode
                // unknown status is revocation refusing the connection, not merely an untrusted
                // chain, and ruling 4 requires that refusal even when a pinned thumbprint would
                // otherwise have accepted it.
                if (revocationRefusal is { } refusal)
                    throw new ServerCertificateRevokedException(
                        refusal,
                        refusal == RevocationStatus.Revoked ? RevocationCheck.RevokedMessage : RevocationCheck.StrictUnknownMessage,
                        ex);
                // Only when *this* validation is what refused it: any other authentication failure
                // (protocol mismatch, no certificate presented, and so on) keeps its own
                // classification. The observed status travels with it either way, so a plain trust
                // failure under e.g. --revocation online still reports what revocation found.
                if (untrusted) throw new ServerCertificateUntrustedException(revocationStatus, ex.Message, ex);
                throw;
            }

            var info = new HandshakeInfo(
                FormatProtocol(ssl.SslProtocol),
                CipherSuiteOf(ssl),
                ssl.LocalCertificate is not null,
                srvSubject, srvIssuer, srvThumb, srvNotAfter, chain, revocationStatus)
                { ServerCertificateSpkiSha256 = srvSpki, ServerCertificateIssuerThumbprint = srvIssuerThumb };
            self!.RecordHandshake(OriginKey(context.InitialRequestMessage.RequestUri!), info);

            return ssl;
        };

        // The handler's OWN TLS options, for the one case the callback above does not cover: a
        // redirect hop that leaves the direct disposition and goes through the proxy. The callback
        // is then invoked for the tunnel request — whose URI is the proxy's, plain http — and hands
        // back a plain socket, after which the handler negotiates TLS to the origin itself. Without
        // this, that hop would go out with no client certificate, OS trust only, and none of
        // --insecure / pins / revocation / --ca-file: a login redirect to an SSO host on the other
        // side of a PAC "DIRECT" rule is exactly that shape. Same certificate, same judgement.
        handler.SslOptions = new SslClientAuthenticationOptions
        {
            RemoteCertificateValidationCallback = (sender, cert, certChain, errors) =>
            {
                var (judgedErrors, judgedFlags) = ExtraRoots.Reconsider(
                    errors, RevocationCheck.ChainFlagsOf(certChain), cert, certChain, transport.ExtraRoots);
                return RevocationCheck.Decide(
                    transport.Revocation, transport.RevocationStrict, transport.IgnoreServerCertificateErrors,
                    judgedErrors, judgedFlags,
                    trustServerCertificate is null ? null : () => TrustContext.Run(certChain, judgedErrors, TrustContext.HostOf(sender, transport.Sni), () =>
                    {
                        using var c = cert is null ? null : new X509Certificate2(cert);
                        return trustServerCertificate(c);
                    })).Accepted;
            }
        };
        RevocationCheck.Apply(handler.SslOptions, transport);
        TlsPolicy.Apply(handler.SslOptions, transport with { Sni = null });
        if (certificateCopy is not null)
            handler.SslOptions.ClientCertificates = new X509CertificateCollection { certificateCopy };

        var entry = new HandlerEntry(handler, certificateCopy);
        self = entry;
        return entry;
    }

    /// <summary>The thumbprint of the certificate directly above the leaf in the presented chain —
    /// what `trust add --from-url --ca` pins. Null for a self-signed leaf or an unreadable chain.</summary>
    private static string? IssuerThumbprintOf(X509Chain chain, string? leafThumbprint)
    {
        try
        {
            foreach (var element in chain.ChainElements)
                if (!element.Certificate.Thumbprint.Equals(leafThumbprint, StringComparison.OrdinalIgnoreCase))
                    return element.Certificate.Thumbprint;
        }
        catch { }
        return null;
    }

    // host:port, lowercased, IPv6 brackets stripped, computed from a Uri on both the recording side
    // (inside the ConnectCallback above) and the lookup side (BuildConnection in SendOnceAsync) so
    // the two can never disagree about which origin a connection belongs to.
    private static string OriginKey(Uri uri) => uri.Host.Trim('[', ']').ToLowerInvariant() + ":" + uri.Port;

    private static bool IsTokenChar(char c) =>
        c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' ||
        "!#$%&'*+-.^_`|~".IndexOf(c) >= 0;

    /// <summary>Scheme, host and port: what "the same origin" means for a credential.</summary>
    internal static string OriginKeyOf(Uri uri) => uri.Scheme.ToLowerInvariant() + "://" + OriginKey(uri);

    /// <summary>The message for one hop. A chain rebuilds it from the original <see cref="ApiRequest"/>
    /// every time rather than resending the previous message: an HttpContent can only be consumed
    /// once, and rebuilding is what re-reads a multipart file part from disk on a 307/308 replay.</summary>
    private static HttpRequestMessage BuildMessage(
        ApiRequest request, TransportOptions transport, string url, HttpMethod method,
        IEnumerable<KeyValuePair<string, string>> headers, bool sendBody)
    {
        var message = new HttpRequestMessage(method, url);
        // Pinning is exact: a server that cannot speak the chosen version must fail loudly
        // rather than quietly downgrade, which would defeat the point of asking.
        if (transport.Version is HttpVersionMode.Http11)
        {
            message.Version = HttpVersion.Version11;
            message.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
        }
        else if (transport.Version is HttpVersionMode.Http2)
        {
            message.Version = HttpVersion.Version20;
            message.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
        }
        else if (transport.Version is HttpVersionMode.Http3)
        {
            message.Version = HttpVersion.Version30;
            message.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
        }

        var bodyHeaders = ContentHeaders.AddRequestHeaders(message, headers);

        // A redirect that drops the body drops the entity entirely — no content, and so no
        // Content-Type describing one.
        if (!sendBody) return message;

        if (request.Parts is { Count: > 0 } parts)
        {
            var form = new MultipartFormDataContent();
            foreach (var part in parts)
            {
                if (part.FilePath is { Length: > 0 })
                {
                    var fileContent = new ByteArrayContent(File.ReadAllBytes(part.FilePath));
                    if (part.ContentType is { Length: > 0 })
                        fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse(part.ContentType);
                    form.Add(fileContent, part.Name, Path.GetFileName(part.FilePath));
                }
                else
                {
                    form.Add(new StringContent(part.Value ?? "", Encoding.UTF8), part.Name);
                }
            }
            message.Content = form;   // boundary + Content-Type are set by MultipartFormDataContent
        }
        else if (request.BodyBytes is not null)
        {
            message.Content = new ByteArrayContent(request.BodyBytes);
            message.Content.Headers.ContentType =
                MediaTypeHeaderValue.Parse(request.ContentType ?? "application/octet-stream");
        }
        else if (request.Body is not null)
        {
            message.Content = new StringContent(request.Body, Encoding.UTF8);
            if (request.ContentType is not null)
                message.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(request.ContentType);
        }

        // A typed Content-Type header names the body's type when nothing else did; a multipart
        // body's own type (it carries the boundary) is never replaced.
        ContentHeaders.ApplyTo(message.Content, bodyHeaders,
            keepContentType: request.ContentType is not null || request.Parts is { Count: > 0 });
        return message;
    }

    /// <summary>Where a response redirects to, resolved against the URL that produced it, or null
    /// when it is not a redirect worth following. A 3xx without a Location is a final answer —
    /// there is nowhere to go.</summary>
    private static Uri? RedirectTarget(HttpResponseMessage response, string currentUrl)
    {
        if ((int)response.StatusCode is not (301 or 302 or 303 or 307 or 308)) return null;
        if (!response.Headers.TryGetValues("Location", out var values)) return null;
        string? location = values.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(location)) return null;
        // Relative Locations are legal and common, so the current URL is the base.
        return Uri.TryCreate(new Uri(currentUrl), location, out var target) ? target : null;
    }

    /// <summary>Null when these options can be used for this URL; otherwise the reason they cannot.
    /// Callers that own a user interface (the CLI) turn a non-null result into a usage error before
    /// sending; SendAsync itself refuses the request rather than silently ignoring a setting.</summary>
    /// <summary>Whether QUIC can be used in this process (msquic loaded). Asked only on the
    /// platforms System.Net.Quic exists on.</summary>
    private static bool QuicAvailable() =>
        (OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()) &&
        System.Net.Quic.QuicConnection.IsSupported;

    /// <summary>Why a --http3 request cannot be sent here, or null when it can. A single-file
    /// Windows build loads msquic.dll from exactly one place, beside the exe, and the .NET runtime
    /// leaves it out of a single-file publish: the release ships it as a file of its own.</summary>
    internal static string? Http3Unavailable(bool quicAvailable, bool isWindows, bool singleFile,
                                             string baseDirectory, Func<string, bool> fileExists)
    {
        if (quicAvailable) return null;
        if (isWindows && singleFile && !fileExists(Path.Combine(baseDirectory, "msquic.dll")))
            return $"--http3 needs msquic.dll beside this program, in {baseDirectory.TrimEnd('\\', '/')}. " +
                   "It is part of every release (certapi update and Scoop install it); download it from " +
                   "the release into that folder, or drop the pin.";
        return isWindows
            ? "--http3 is not available here: QUIC (msquic) could not be loaded. It needs Windows 11 or Windows Server 2022 or later."
            : "--http3 is not available here: QUIC needs libmsquic, which is not installed.";
    }

    public static string? ValidateTransport(TransportOptions transport, string url)
    {
        if (transport.MaxRedirects < 1)
            return "--max-redirs must be at least 1 (use --no-redirect to stop following redirects).";

        if (transport.Proxy is ProxyMode.Explicit &&
            (string.IsNullOrWhiteSpace(transport.ProxyUrl) ||
             !Uri.TryCreate(transport.ProxyUrl, UriKind.Absolute, out var proxyUri) ||
             // The four schemes the handler itself speaks: HTTP CONNECT proxies and SOCKS.
             // socks5 is what `ssh -D` provides, which is how an API behind a jump host is
             // reached; the handler resolves the hostname locally for socks4/5 and remotely
             // for socks4a/5 per its own rules.
             proxyUri.Scheme is not ("http" or "https" or "socks5" or "socks4" or "socks4a")))
            return $"--proxy must be an absolute http(s) or socks4/socks4a/socks5 URL, got '{transport.ProxyUrl}'.";

        foreach (var pin in transport.Resolve)
        {
            if (string.IsNullOrWhiteSpace(pin.Host) ||
                pin.Port is < 1 or > 65535 ||
                !IPAddress.TryParse(pin.Address, out _))
                return $"--resolve expects host:port:ip, got '{pin.Host}:{pin.Port}:{pin.Address}'.";
        }

        // There is no ConnectCallback on the proxied path, so a pinned address could only be
        // dropped on the floor — say so instead.
        if (transport.Version is HttpVersionMode.Http3 && ProxyWillBeUsed(url, transport))
            return "--http3 cannot go through a proxy: HTTP/3 is QUIC over UDP, and the proxy " +
                   "protocols here (HTTP CONNECT, SOCKS) are TCP. Use --no-proxy, or drop the pin.";

        if (transport.Version is HttpVersionMode.Http3 && transport.Resolve.Count > 0)
            return "--resolve cannot be used with --http3: QUIC connects inside the handler, so " +
                   "there is no dial this product could re-point.";

        if (transport.Version is HttpVersionMode.Http3 &&
            Http3Unavailable(QuicAvailable(), OperatingSystem.IsWindows(),
                             singleFile: string.IsNullOrEmpty(typeof(ApiClient).Assembly.Location),
                             AppContext.BaseDirectory, File.Exists) is { } noQuic)
            return noQuic;

        // The proxy path's TLS is negotiated by the HTTP stack from the URL, which has no setting for
        // another server name; refusing beats sending the URL's name while the user believes otherwise.
        if (transport.Sni is not null && url.StartsWith("https:", StringComparison.OrdinalIgnoreCase) && ProxyWillBeUsed(url, transport))
            return $"--sni cannot be used through a proxy ({ProxyUriFor(url, transport) ?? "one applies to this URL"}): the " +
                   "tunnel's TLS is negotiated from the URL's host. Use --no-proxy to send it direct.";

        if (transport.Resolve.Count > 0 && ProxyWillBeUsed(url, transport))
            return $"--resolve cannot be used together with a proxy (the connection is tunnelled " +
                   $"through {ProxyUriFor(url, transport) ?? "a proxy"}, so the address cannot be " +
                   $"pinned). Use --no-proxy to bypass it.";

        // A bypass list narrows a proxy that is actually in play; with the proxy off entirely there
        // is nothing left for it to narrow, so the combination is a usage error rather than a
        // silently ignored flag. The command line reports this exact sentence too, off the same
        // shared constant, so the two can never drift apart.
        if (transport.Proxy is ProxyMode.None && transport.NoProxy.Count > 0)
            return ProxyBypass.NoProxyWithProxyOffMessage;

        // --revocation-strict with checking off has no unknown status to make fatal in the first
        // place, so the combination is a usage error rather than a silently ignored flag — the same
        // shape as the NoProxy check just above. Shares its message constant with the command line
        // (see RevocationCheck.StrictWithoutCheckingMessage's own remarks) so the two can never drift.
        if (transport.RevocationStrict && transport.Revocation is RevocationMode.None)
            return RevocationCheck.StrictWithoutCheckingMessage;

        return null;
    }

    /// <summary>Where this request will actually go, and why: whether it is tunnelled through a
    /// proxy, sent direct because no proxy applies, or sent direct because a bypass rule overrode a
    /// proxy that otherwise would have applied — and, in that last case, which rule. The disposition
    /// decides whether the ConnectCallback path (and with it the TLS diagnostics and --resolve) is
    /// available, and <see cref="BuildConnection"/>-style callers report it verbatim.</summary>
    public static (ProxyDisposition Disposition, string? BypassRule) DecideProxy(string url, TransportOptions transport)
    {
        try
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return (ProxyDisposition.Direct, null);
            if (uri.Scheme is not ("http" or "https")) return (ProxyDisposition.Direct, null);
            if (transport.Proxy is ProxyMode.None) return (ProxyDisposition.Direct, null);

            bool wouldProxy;
            if (transport.PacUrl is { } pac)
            {
                wouldProxy = new ProxyConfiguration.PacWebProxy(pac).GetProxy(uri) is not null;
            }
            else if (transport.Proxy is ProxyMode.Explicit)
            {
                wouldProxy = !string.IsNullOrWhiteSpace(transport.ProxyUrl) &&
                             !new WebProxy(transport.ProxyUrl).IsBypassed(uri);
            }
            else
            {
                var proxy = HttpClient.DefaultProxy;
                wouldProxy = proxy is not null && !proxy.IsBypassed(uri) && proxy.GetProxy(uri) is not null;
            }
            if (!wouldProxy) return (ProxyDisposition.Direct, null);

            // Consulted only after establishing that a proxy would otherwise have been used:
            // BypassedByRule is a claim that a rule changed the outcome, and on a machine with no
            // proxy configured at all a matching rule changed nothing — the honest answer there is
            // Direct, not a bypass that was never in play.
            if (ProxyBypass.Match(transport.NoProxy, uri) is { } rule)
                return (ProxyDisposition.BypassedByRule, rule.Text);

            return (ProxyDisposition.Proxied, null);
        }
        catch { return (ProxyDisposition.Direct, null); }
    }

    // Per send, not cached for the process: the desktop app lives for days, and a script edited
    // in that time must be read again.
    private static ProxyConfiguration.PacWebProxy PacProxyFor(string pac) => new(pac);

    /// <summary>Whether this request will actually be tunnelled through a proxy. The answer decides
    /// whether the ConnectCallback path (and with it the TLS diagnostics and --resolve) is available.</summary>
    public static bool ProxyWillBeUsed(string url, TransportOptions transport) =>
        DecideProxy(url, transport).Disposition == ProxyDisposition.Proxied;

    /// <summary>The proxy that was used, for the diagnostics panel. Only meaningful once
    /// <see cref="ProxyWillBeUsed"/> has said yes.</summary>
    private static string? ProxyUriFor(string url, TransportOptions transport)
    {
        try
        {
            // Never with credentials: this string ends up in diagnostics, logs and the app.
            if (transport.Proxy is ProxyMode.Explicit)
                return transport.ProxyUrl is null ? null : MarkdownSecrets.RedactUrl(transport.ProxyUrl);
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
            return HttpClient.DefaultProxy?.GetProxy(uri)?.ToString();
        }
        catch { return null; }
    }

    /// <summary>The exception chain, outermost first. For a refused client certificate the useful
    /// facts — the AuthenticationException and the SChannel status code — are several links down,
    /// and the outer message says nothing about them. Capped so a cyclic chain cannot run away.</summary>
    private static IReadOnlyList<ErrorDetail> Flatten(Exception exception)
    {
        var details = new List<ErrorDetail>();
        for (Exception? ex = exception; ex is not null && details.Count < 8; ex = ex.InnerException)
        {
            int? native = ex switch
            {
                SocketException se => se.ErrorCode,
                System.ComponentModel.Win32Exception w => w.NativeErrorCode,
                _ => null
            };
            details.Add(new ErrorDetail(ex.GetType().Name, ex.Message, native));
        }
        return details;
    }

    private static SocketError? FirstSocketError(Exception exception)
    {
        for (Exception? ex = exception; ex is not null; ex = ex.InnerException)
            if (ex is SocketException se) return se.SocketErrorCode;
        return null;
    }

    private static bool HasInner<T>(Exception exception) where T : Exception
    {
        for (Exception? ex = exception; ex is not null; ex = ex.InnerException)
            if (ex is T) return true;
        return false;
    }

    /// <summary>The first exception in the chain implementing <typeparamref name="T"/>, or null.
    /// Mirrors <see cref="HasInner{T}"/> but hands back the instance itself — used to read
    /// <see cref="IServerCertificateRefusal.RevocationStatus"/> off whichever server-certificate
    /// refusal, if any, is in this chain.</summary>
    private static T? FirstInner<T>(Exception exception) where T : class
    {
        for (Exception? ex = exception; ex is not null; ex = ex.InnerException)
            if (ex is T match) return match;
        return null;
    }

    private static string? FormatProtocol(SslProtocols p) => p switch
    {
        SslProtocols.Tls13 => "TLS 1.3",
        SslProtocols.Tls12 => "TLS 1.2",
        SslProtocols.None => null,
        _ => p.ToString()
    };

    /// <summary>The negotiated cipher suite, or null when the platform cannot report one ("default"
    /// means unavailable, not a real negotiated value) — its own helper so the direct-connection
    /// handshake applies exactly the same rule the per-call closure used to.</summary>
    private static string? CipherSuiteOf(SslStream ssl)
    {
        TlsCipherSuite cipher = default;
        try { cipher = ssl.NegotiatedCipherSuite; } catch { /* not available on all platforms */ }
        return cipher == default ? null : cipher.ToString();
    }
}

/// <summary>Windows credentials that answer only the origin the request was sent to. The origin
/// is set per send (an async-local, so concurrent sends sharing one handler each see their own);
/// a hop to any other scheme, host or port gets no credential at all.</summary>
internal sealed class OriginBoundCredentials(ICredentials inner) : ICredentials
{
    public static readonly AsyncLocal<string?> Origin = new();

    public NetworkCredential? GetCredential(Uri uri, string authType) =>
        IntegratedOnlyCredentials.IsIntegrated(authType) &&
        Origin.Value is { } origin && string.Equals(origin, ApiClient.OriginKeyOf(uri), StringComparison.OrdinalIgnoreCase)
            ? inner.GetCredential(uri, authType)
            : null;
}

/// <summary>Windows credentials that answer only Windows integrated authentication. A Basic (or
/// Digest) challenge would otherwise be answered with the domain password — base64 in the clear on
/// http — by whichever server asked for it.</summary>
internal sealed class IntegratedOnlyCredentials(ICredentials inner) : ICredentials
{
    private static readonly HashSet<string> IntegratedSchemes = new(StringComparer.OrdinalIgnoreCase) { "Negotiate", "NTLM", "Kerberos" };

    public static bool IsIntegrated(string authType) => IntegratedSchemes.Contains(authType);

    public NetworkCredential? GetCredential(Uri uri, string authType) =>
        IsIntegrated(authType) ? inner.GetCredential(uri, authType) : null;
}
