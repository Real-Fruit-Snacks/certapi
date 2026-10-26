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
                        ? CredentialCache.DefaultCredentials
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

        public ICredentials? Credentials { get; set; } = CredentialCache.DefaultCredentials;

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
