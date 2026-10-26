using System.Net;
namespace ApiTester.Core;

/// <summary>How the connection reaches the server. The distinction matters for mTLS: a proxy
/// tunnels the TLS session, so the client certificate and the handshake details are no longer
/// something this client can see or control.</summary>
public enum ProxyMode
{
    /// <summary>Use the machine's configured proxy (including WPAD/PAC) — the historical behavior.</summary>
    System,
    /// <summary>Ignore any configured proxy and connect directly.</summary>
    None,
    /// <summary>Route through the proxy named by <see cref="TransportOptions.ProxyUrl"/>.</summary>
    Explicit
}

/// <summary>How a request actually reached the server, once <see cref="ProxyMode"/> and
/// <see cref="TransportOptions.NoProxy"/> have both had their say. <see cref="Direct"/> and
/// <see cref="BypassedByRule"/> both mean "no proxy was used", but they must stay distinguishable:
/// bypass rules are exactly the kind of thing people get subtly wrong — a typo'd suffix, a rule that
/// matches more or less than intended — and "why did this go direct?" is otherwise guesswork.</summary>
public enum ProxyDisposition
{
    /// <summary>No proxy was in play — either none is configured or the request asked for none.</summary>
    Direct,
    /// <summary>Tunnelled through a proxy.</summary>
    Proxied,
    /// <summary>A proxy was configured and a bypass rule sent this request direct instead.</summary>
    BypassedByRule
}

/// <summary>Which HTTP version to insist on. Pinning is a diagnostic tool: a gateway that
/// behaves differently on HTTP/1.1 and HTTP/2 is otherwise very hard to catch.</summary>
public enum HttpVersionMode
{
    /// <summary>Let the handler negotiate — the historical behavior.</summary>
    Auto,
    Http11,
    Http2,
    /// <summary>HTTP/3 over QUIC (UDP). Exact, like the others: a server that cannot speak it
    /// fails loudly rather than downgrading. Cannot go through a proxy or a --resolve pin — the
    /// proxy protocols here are TCP, and QUIC connects inside the handler.</summary>
    Http3
}

/// <summary>Pin <paramref name="Host"/>:<paramref name="Port"/> to <paramref name="Address"/> so the
/// connection is dialled at a chosen address while the TLS server name and the Host header still
/// carry the original hostname — the only way to test one node of a load-balanced endpoint.</summary>
public sealed record ResolveOverride(string Host, int Port, string Address);

/// <summary>*How* to send a request, as opposed to <see cref="ApiRequest"/>, which is *what* to send.
/// Every default reproduces the behavior this client had before transport control existed, apart
/// from <see cref="Decompress"/>, which now matches every other HTTP client.</summary>
public sealed record TransportOptions
{
    public ProxyMode Proxy { get; init; } = ProxyMode.System;
    public string? ProxyUrl { get; init; }
    /// <summary>A PAC script (URL or file) to decide the proxy with, through WinHTTP's own engine
    /// — <c>--pac</c>. Replaces the system decision; null is the system's own configuration.</summary>
    public string? PacUrl { get; init; }
    public string? ProxyUser { get; init; }
    public string? ProxyPassword { get; init; }

    public bool FollowRedirects { get; init; } = true;
    public int MaxRedirects { get; init; } = 20;

    /// <summary>Decode gzip/deflate/brotli responses. Turn it off to relay the bytes exactly as the
    /// server sent them.</summary>
    public bool Decompress { get; init; } = true;
    public HttpVersionMode Version { get; init; } = HttpVersionMode.Auto;

    /// <summary>Accept any server certificate. A TLS concern, so it lives with the other transport
    /// settings rather than with the request content.</summary>
    public bool IgnoreServerCertificateErrors { get; init; }

    public IReadOnlyList<ResolveOverride> Resolve { get; init; } = Array.Empty<ResolveOverride>();

    /// <summary>Hosts that skip the proxy even when one is selected. Empty reproduces the behavior
    /// this client had before a bypass list existed — every proxyable request goes through whatever
    /// proxy <see cref="Proxy"/> selects. A rule here narrows <see cref="ProxyMode.System"/> and
    /// <see cref="ProxyMode.Explicit"/> alike, but is meaningless with <see cref="ProxyMode.None"/>,
    /// where there is no proxy to bypass in the first place.</summary>
    public IReadOnlyList<ProxyBypassRule> NoProxy { get; init; } = ProxyBypass.None;

    /// <summary>How many times to retry a failed request. 0 = off, which is the behavior this client
    /// had before retry existed.</summary>
    public int Retries { get; init; }

    /// <summary>Response statuses that earn a retry. The default set is the four a load balancer or
    /// gateway returns when the answer is "not now" rather than "no".</summary>
    public IReadOnlyList<int> RetryOn { get; init; } = new[] { 429, 502, 503, 504 };

    /// <summary>The first backoff delay; each further attempt doubles it, with jitter, capped at 30s.</summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Retry connection refused/reset, DNS failures, and timeouts.</summary>
    public bool RetryOnTransportError { get; init; } = true;

    /// <summary>Let a Retry-After header override the computed backoff. The server knows better than
    /// the client's guess when it bothers to say.</summary>
    public bool HonorRetryAfter { get; init; } = true;

    /// <summary>Also retry POST and PATCH. Off by default: re-sending a POST nobody confirmed can
    /// charge a card twice, and a client cannot tell a lost response from a lost request.</summary>
    public bool RetryUnsafeMethods { get; init; }

    /// <summary>Whether to check that the server's certificate has not been revoked by its issuer,
    /// and how hard to look. <see cref="RevocationMode.None"/> is the default and reproduces the
    /// behavior this client had before revocation checking existed: SslStream does not check
    /// revocation unless it is asked to, so it was never asked. An operator opts in with
    /// <c>--revocation offline</c> or <c>--revocation online</c> once they have a public-key
    /// infrastructure that can actually answer the question.</summary>
    public RevocationMode Revocation { get; init; } = RevocationMode.None;

    /// <summary>Whether a revocation status that could not be determined is fatal. Off by default,
    /// and deliberately so: on a locked-down corporate network a blocked or unreachable certificate
    /// revocation list (CRL) or Online Certificate Status Protocol (OCSP) endpoint is the ordinary
    /// state of affairs, and failing on it would make this tool unusable on exactly the networks it
    /// exists to test. Meaningless with <see cref="RevocationMode.None"/>, where nothing was checked
    /// and so nothing can come back unknown.</summary>
    public bool RevocationStrict { get; init; }

    /// <summary>Roots trusted for this run in addition to the machine store — <c>--ca-file</c>.
    /// Null is the behavior this client always had. Judged by <see cref="ExtraRoots.Reconsider"/>
    /// inside every validation callback, so a chain that ends in one of these is accepted the
    /// way a store-trusted chain is, while name, expiry and revocation problems stay refused.</summary>
    public ExtraRootSet? ExtraRoots { get; init; }

    /// <summary>The TLS version floor — <c>--tls-min</c>. <see cref="TlsFloor.Default"/> leaves the
    /// choice to the OS, as before.</summary>
    public TlsFloor MinTls { get; init; } = TlsFloor.Default;

    /// <summary>A server name to send in the TLS handshake instead of the URL's host — <c>--sni</c>.
    /// Direct connections only; a proxy tunnel's TLS is negotiated by the handler from the URL.
    /// Null, the default, sends the hostname being connected to.</summary>
    public string? Sni { get; init; }

    /// <summary>How long to wait for the TCP connection (and proxy tunnel) alone — <c>--connect-timeout</c>.
    /// Null is the handler's default (no separate limit, the overall timeout governs). On a network
    /// that blackholes unknown hosts, a short value tells "unreachable" from "slow" in seconds.</summary>
    public TimeSpan? ConnectTimeout { get; init; }

    /// <summary>Touch nothing on the network but the request itself — <c>--offline</c>: no
    /// certificate downloads while building the chain (AIA fetching), no online revocation, and
    /// doctor skips its internet-connectivity probe. For an air-gapped machine, or one where every
    /// unexpected outbound connection is a ticket. The proxy decision is not changed by it; name
    /// the proxy with <c>--proxy</c> or none with <c>--no-proxy</c> when WPAD must not run either.</summary>
    public bool Offline { get; init; }

    /// <summary>Which address family to dial — <c>-4</c> / <c>-6</c>. Direct connections only: a
    /// proxy resolves the target itself. Null, the default, takes the resolver's order.</summary>
    public AddressFamilyPreference AddressFamily { get; init; } = AddressFamilyPreference.Any;
}

public enum AddressFamilyPreference
{
    Any,
    IPv4,
    IPv6
}

/// <summary>Resolves a host to the addresses the preference allows — shared by every connect
/// callback so <c>-4</c>/<c>-6</c> mean the same thing on the direct path and in the gateway.
/// A dual-stack host whose IPv6 route is advertised but blackholed is the case: the resolver's
/// order tries it first and the connect hangs until the timeout.</summary>
public static class AddressSelection
{
    public static async Task<IPAddress[]> ResolveAsync(string host, AddressFamilyPreference preference, CancellationToken ct)
    {
        if (IPAddress.TryParse(host, out var literal)) return new[] { literal };
        var all = await System.Net.Dns.GetHostAddressesAsync(host, ct);
        var wanted = preference switch
        {
            AddressFamilyPreference.IPv4 => all.Where(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork).ToArray(),
            AddressFamilyPreference.IPv6 => all.Where(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6).ToArray(),
            _ => all
        };
        if (wanted.Length == 0 && all.Length > 0)
            throw new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.HostNotFound,
                $"{host} has no {(preference == AddressFamilyPreference.IPv4 ? "IPv4" : "IPv6")} address ({all.Length} of the other family).");
        return wanted;
    }
}
