using System.Net;
using System.Net.Http;

namespace ApiTester.Core;

/// <summary>The one place a <see cref="SocketsHttpHandler"/> learns how to reach a server: the
/// three-way <see cref="ProxyMode"/>, narrowed by <see cref="TransportOptions.NoProxy"/> when there
/// is a bypass list. <see cref="ApiClient"/>'s direct and proxied paths both call this, and a later
/// slice points <c>MtlsGateway</c> and <c>GrpcChannelFactory</c> at it too, so the bypass cannot end
/// up applying on one path and not another.</summary>
public static class ProxyConfiguration
{
    /// <summary>The signed-in user's Windows credentials for a proxy's Negotiate/NTLM challenge:
    /// <see cref="CredentialCache.DefaultCredentials"/>, behind an object of its own.
    /// <para>When a handler's proxy credentials are that object itself, .NET keys its connection
    /// pools by the user's name, and reads the name for every request
    /// (<c>WindowsIdentity.GetCurrent().Name</c>, a lookup in lsass). This process only ever runs as
    /// one user, so the same credentials behind another object answer the same challenges without
    /// that. Basic and Digest get nothing, as with the object itself: it has no password to give.</para></summary>
    public static readonly ICredentials SignedInUser = new IntegratedOnlyCredentials(CredentialCache.DefaultCredentials);

    /// <summary>Configure how this handler reaches a server: the three-way proxy mode, narrowed by
    /// the bypass list when there is one.</summary>
    public static void Apply(SocketsHttpHandler handler, TransportOptions transport) =>
        Apply(handler, transport, destination: null);

    /// <param name="destination">The URL this handler is being built for, when there is one
    /// request (or one upstream) it will ever serve. With the system proxy and a bypass list, a
    /// destination the list does <em>not</em> match leaves the handler's proxy untouched — the
    /// system proxy object is left as .NET built it, failover list and all — rather than wrapping
    /// it in a decorator that can only answer one proxy. A PAC that says
    /// <c>PROXY a; PROXY b; DIRECT</c> then fails over past a dead <c>a</c> exactly as it does with
    /// no bypass list. A destination the list matches, or no destination at all, keeps the
    /// decorator, so a later redirect hop is still routed per host.</param>
    public static void Apply(SocketsHttpHandler handler, TransportOptions transport, Uri? destination)
    {
        // A named PAC script replaces the system's proxy decision outright, bypass list included on
        // top, however the mode reads: the script is the decision the user asked to test.
        if (transport.PacUrl is { } pac && transport.Proxy != ProxyMode.None)
        {
            IWebProxy pacProxy = new PacWebProxy(pac);
            handler.Proxy = transport.NoProxy.Count > 0 ? new BypassingProxy(pacProxy, transport.NoProxy) : pacProxy;
            handler.UseProxy = true;
            return;
        }

        switch (transport.Proxy)
        {
            case ProxyMode.None:
                // Bypassing the proxy also restores the ConnectCallback path, which is the only
                // place the TLS details can be read. A bypass list is meaningless here — that
                // combination is refused by ValidateTransport before a send ever gets this far —
                // so nothing about NoProxy is consulted.
                handler.UseProxy = false;
                break;

            case ProxyMode.Explicit:
            {
                // WebProxy(string) ignores userinfo, so credentials written into the URL
                // (http://user:pass@proxy) are lifted out here when nothing more explicit was
                // given — the command line already does this; this covers a saved request.
                string? user = transport.ProxyUser, password = transport.ProxyPassword, url = transport.ProxyUrl;
                if (user is null && url is not null && Uri.TryCreate(url, UriKind.Absolute, out var parsed) && parsed.UserInfo.Length > 0)
                {
                    int colon = parsed.UserInfo.IndexOf(':');
                    user = Uri.UnescapeDataString(colon < 0 ? parsed.UserInfo : parsed.UserInfo[..colon]);
                    password = colon < 0 ? "" : Uri.UnescapeDataString(parsed.UserInfo[(colon + 1)..]);
                    url = new UriBuilder(parsed) { UserName = "", Password = "" }.Uri.ToString();
                }
                var explicitProxy = new WebProxy(url)
                {
                    Credentials = user is null
                        ? SignedInUser
                        : new NetworkCredential(user, password)
                };
                // Only wrapped when there is something to narrow: an empty list has nothing to add
                // over the plain WebProxy this handler has always been given.
                handler.Proxy = transport.NoProxy.Count > 0
                    ? new BypassingProxy(explicitProxy, transport.NoProxy)
                    : explicitProxy;
                handler.UseProxy = true;
                break;
            }

            case ProxyMode.System:
                // With an empty bypass list this must change nothing at all: handler.Proxy stays
                // unset, exactly as before a bypass list existed, so the handler keeps consulting
                // HttpClient.DefaultProxy — and DefaultProxyCredentials above — entirely on its own.
                // Only a non-empty list is worth installing a decorator for — and even then not
                // for a known destination the list does not match (see the destination parameter).
                if (transport.NoProxy.Count > 0 &&
                    (destination is null || ProxyBypass.IsBypassed(transport.NoProxy, destination)))
                {
                    handler.Proxy = new BypassingProxy(HttpClient.DefaultProxy, transport.NoProxy);
                    handler.UseProxy = true;
                }
                break;
        }
    }

    /// <summary>The system proxy for the gateway when it is configured automatically (a PAC script
    /// or WPAD): asked once per site, and the answer remembered, instead of asked before every
    /// request.
    /// <para>The handler asks its proxy before every request, and with automatic configuration
    /// .NET's system proxy answers by calling <c>WinHttpGetProxyForUrl</c>: a blocking call into
    /// the WinHTTP auto-proxy service that every program on the machine shares (.NET skips it for
    /// two minutes after WPAD finds no script, and not otherwise). When that service is slow — a PAC
    /// address it cannot reach, a script that waits on DNS — every request through the gateway
    /// blocked a thread on it, and a busy page piled up hundreds. Here the first request for a site
    /// asks; a proxy is then remembered for <see cref="Lifetime"/>, and "no proxy" for
    /// <see cref="DirectLifetime"/>, because a lookup that fails also answers "no proxy".</para>
    /// <para>A manual proxy, or none, is answered by .NET without WinHTTP, so
    /// <see cref="ForThisMachine"/> leaves those alone. A site whose PAC answer is a failover list
    /// (<c>PROXY a; PROXY b</c>), or whose answer could not be told, is left to .NET's own proxy,
    /// which fails over and which a wrapper cannot: <see cref="Takes"/> says no for it.</para></summary>
    public sealed class CachedSystemProxy : IWebProxy
    {
        public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);
        public static readonly TimeSpan DirectLifetime = TimeSpan.FromSeconds(30);

        private sealed record Answer(Uri? Proxy, bool Bypassed, long Expires);

        private readonly IWebProxy _inner;
        private readonly Func<Uri, bool> _isFailoverList;
        private readonly Func<long> _now;
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Lazy<Answer>> _answers = new(StringComparer.OrdinalIgnoreCase);
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _takes = new(StringComparer.OrdinalIgnoreCase);
        private ICredentials? _credentials;
        private long _asked;

        /// <param name="inner">The system proxy (<see cref="HttpClient.DefaultProxy"/>).</param>
        /// <param name="isFailoverList">Whether Windows' answer for a URL names more than one proxy;
        /// it should say yes when the answer cannot be had at all.</param>
        /// <param name="now">Milliseconds on a monotonic clock, for tests.</param>
        public CachedSystemProxy(IWebProxy inner, Func<Uri, bool> isFailoverList, Func<long>? now = null)
        {
            _inner = inner;
            _isFailoverList = isFailoverList;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            _now = now ?? (() => clock.ElapsedMilliseconds);
        }

        /// <summary>How many times the system proxy was actually asked.</summary>
        public long Asked => Interlocked.Read(ref _asked);

        /// <summary>The gateway's own instance, or null when the system proxy needs none: it is not
        /// Windows' (an HTTP_PROXY variable, another platform), or Windows' settings name a manual
        /// proxy or none, which .NET answers without WinHTTP. Read as .NET reads them, through
        /// <c>WinHttpGetIEProxyConfigForCurrentUser</c>.</summary>
        public static CachedSystemProxy? ForThisMachine()
        {
            if (HttpClient.DefaultProxy is not { } p || p.GetType().Name != "HttpWindowsProxy") return null;
            var settings = ProxyIntrospection.ReadEffectiveSettings();
            if (!settings.AutoDetect && string.IsNullOrWhiteSpace(settings.AutoConfigUrl)) return null;
            return new CachedSystemProxy(p, destination => WindowsAnswersWithAList(destination, settings));
        }

        /// <summary>Whether to stand in for the system proxy for <paramref name="destination"/>: yes,
        /// unless Windows answers it with a failover list, or cannot answer. Asked once per site, when
        /// its handler is made.</summary>
        public bool Takes(Uri destination) =>
            _takes.GetOrAdd(destination.GetLeftPart(UriPartial.Authority), _ =>
            {
                try { return !_isFailoverList(destination); }
                catch { return false; }
            });

        /// <summary>The signed-in user's credentials, as the handler gives the system proxy, but not
        /// as the <see cref="CredentialCache.DefaultCredentials"/> object itself (see
        /// <see cref="SignedInUser"/>). The shared system proxy is never changed.</summary>
        public ICredentials? Credentials
        {
            get => _credentials ?? _inner.Credentials ?? SignedInUser;
            set => _credentials = value;
        }

        public Uri? GetProxy(Uri destination) => AnswerFor(destination).Proxy;

        public bool IsBypassed(Uri host) => AnswerFor(host).Bypassed;

        private Answer AnswerFor(Uri destination)
        {
            string key = destination.GetLeftPart(UriPartial.Authority);
            while (true)
            {
                var lazy = _answers.GetOrAdd(key, _ => new Lazy<Answer>(() => Ask(destination)));
                var answer = lazy.Value;
                if (answer.Expires > _now()) return answer;
                // Expired: one caller replaces it; everyone then waits on the same fresh question.
                _answers.TryUpdate(key, new Lazy<Answer>(() => Ask(destination)), lazy);
            }
        }

        private Answer Ask(Uri destination)
        {
            Interlocked.Increment(ref _asked);
            Uri? proxy = null;
            bool bypassed = true;
            try
            {
                bypassed = _inner.IsBypassed(destination);
                if (!bypassed) proxy = _inner.GetProxy(destination);
            }
            catch { /* what the handler does with a lookup that fails: go direct */ }
            bool direct = bypassed || proxy is null;
            return new Answer(proxy, direct, _now() + (long)(direct ? DirectLifetime : Lifetime).TotalMilliseconds);
        }

        private static bool WindowsAnswersWithAList(Uri destination, ProxySettings settings)
        {
            // As .NET asks: signing in to fetch the script only when it asks for that.
            var (proxy, error) = ProxyIntrospection.Answer(destination.AbsoluteUri, settings, autoLogonWhenChallenged: true);
            if (error is not null) return true;   // no answer to go by: leave the site to .NET's proxy
            return proxy is not null &&
                   proxy.Split(new[] { ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length > 1;
        }
    }

    /// <summary>A proxy that asks a PAC script the caller named — `--pac` — through
    /// <see cref="ProxyIntrospection.EvaluatePac"/>, once per host (a PAC answers per URL, but the
    /// scripts that matter decide by host, and one WinHTTP call per request would be the slow way
    /// to learn that). The first proxy of a failover list is used; DIRECT is honoured.</summary>
    public sealed class PacWebProxy : IWebProxy
    {
        private readonly string _pac;
        private readonly Func<string, string, (string? Proxy, string? Error)> _evaluate;
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Uri?> _byHost = new(StringComparer.OrdinalIgnoreCase);

        public PacWebProxy(string pac, Func<string, string, (string? Proxy, string? Error)>? evaluate = null)
        {
            _pac = pac;
            _evaluate = evaluate ?? ProxyIntrospection.EvaluatePac;
        }

        public ICredentials? Credentials { get; set; } = SignedInUser;

        public Uri? GetProxy(Uri destination) =>
            _byHost.GetOrAdd(destination.GetLeftPart(UriPartial.Authority), _ => Evaluate(destination));

        public bool IsBypassed(Uri host) => GetProxy(host) is null;

        private Uri? Evaluate(Uri destination)
        {
            var (proxy, error) = _evaluate(_pac, destination.AbsoluteUri);
            if (error is not null)
                throw new InvalidOperationException($"The PAC script could not be evaluated for {destination.Host}: {error}");
            if (proxy is null) return null;
            // "PROXY a:8080; PROXY b:8080; DIRECT" / "a:8080;b:8080": the first candidate.
            string first = proxy.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "";
            if (first.StartsWith("PROXY ", StringComparison.OrdinalIgnoreCase)) first = first[6..].Trim();
            if (first.Length == 0 || first.Equals("DIRECT", StringComparison.OrdinalIgnoreCase)) return null;
            if (!first.Contains("://", StringComparison.Ordinal)) first = "http://" + first;
            return Uri.TryCreate(first, UriKind.Absolute, out var uri)
                ? uri
                : throw new InvalidOperationException($"The PAC script answered '{proxy}' for {destination.Host}, which is not a proxy address.");
        }
    }

    /// <summary>Narrows an inner <see cref="IWebProxy"/> by a NO_PROXY-style bypass list: a
    /// destination the list matches is answered "go direct" (null), and everything else is forwarded
    /// to the inner proxy unchanged. The inner proxy may be null — <see cref="HttpClient.DefaultProxy"/>
    /// is not documented to be non-null in every hosting configuration — in which case this decorator
    /// behaves as "no proxy, narrowed by the list", which for every destination not in the list is
    /// simply "no proxy".</summary>
    internal sealed class BypassingProxy : IWebProxy
    {
        private readonly IWebProxy? _inner;
        private readonly IReadOnlyList<ProxyBypassRule> _rules;

        public BypassingProxy(IWebProxy? inner, IReadOnlyList<ProxyBypassRule> rules)
        {
            _inner = inner;
            _rules = rules;
        }

        public ICredentials? Credentials
        {
            get => _inner?.Credentials;
            set
            {
                if (_inner is not null) _inner.Credentials = value;
            }
        }

        public Uri? GetProxy(Uri destination) =>
            ProxyBypass.IsBypassed(_rules, destination) ? null : _inner?.GetProxy(destination);

        public bool IsBypassed(Uri host) =>
            ProxyBypass.IsBypassed(_rules, host) || (_inner?.IsBypassed(host) ?? false);
    }
}
