namespace ApiTester.Core;

/// <summary>`serve --vhost`: one gateway origin per upstream, named under a loopback suffix.
///
/// <para>Under the path-based gateway every upstream shares one origin and lives under a prefix, so
/// an application that writes absolute paths (<c>/static/app.js</c>, <c>Path=/</c>, a
/// <c>&lt;base href="/"&gt;</c>) or compares origins (postMessage, CSP, SameSite) trips over the
/// prefix. Here <c>api.corp</c> is reached at <c>http://api.corp.gw.localhost:8819/</c> instead:
/// its own origin, its paths untouched. Browsers resolve every <c>*.localhost</c> name to loopback
/// without a hosts-file entry, which is what makes the suffix work with no setup.</para>
///
/// <para>This class only translates. The request's Host header picks a mount, the mount's prefix is
/// put in front of the path, and the path-based gateway does the rest; a rewritten Location or
/// cookie is then expressed back in host form. Static mounts are named by their prefix
/// (<c>/api</c> → <c>api.gw.localhost</c>, <c>/</c> → <c>gw.localhost</c> itself), and an
/// allow-listed dynamic host by its own name (<c>https://hr.corp:8443</c> →
/// <c>hr.corp--8443.gw.localhost</c>; plain http gets an <c>http--</c> prefix).</para></summary>
public sealed class VirtualHosts
{
    public const string DefaultSuffix = "gw.localhost";

    private readonly GatewayRoutes _routes;

    public string Suffix { get; }
    public string Scheme { get; }
    public int Port { get; }

    public VirtualHosts(GatewayRoutes routes, string suffix, string scheme, int port)
    {
        if (string.IsNullOrWhiteSpace(suffix) || suffix.Contains('/') || suffix.Contains(':'))
            throw new ArgumentException("A virtual-host suffix is a bare DNS name such as gw.localhost.", nameof(suffix));
        _routes = routes;
        Suffix = suffix.Trim().TrimStart('.').ToLowerInvariant();
        Scheme = scheme;
        Port = port;
    }

    /// <summary>The label a static mount is reached under: the prefix with its slashes turned into
    /// hyphens, or empty for the root mount, which is the bare suffix.</summary>
    public static string LabelFor(GatewayRoute route) =>
        route.PathPrefix.StartsWith(MtlsGateway.DynamicPrefix, StringComparison.Ordinal)
            ? EncodeAuthority(route.Upstream)
            : route.Name?.ToLowerInvariant() ?? route.PathPrefix.Trim('/').Replace('/', '-').ToLowerInvariant();

    /// <summary>The host name (no port) a mount answers on.</summary>
    public string HostFor(GatewayRoute route)
    {
        string label = LabelFor(route);
        return label.Length == 0 ? Suffix : label + "." + Suffix;
    }

    /// <summary>The origin a browser uses for a mount — the LocalOrigin every rewrite is measured
    /// against while a request to that host is being answered.</summary>
    public Uri OriginFor(GatewayRoute route) => new($"{Scheme}://{HostFor(route)}:{Port}");

    /// <summary>The gateway path prefix a Host header selects: a static mount's prefix, "/" for the
    /// bare suffix (when a root mount exists), or <c>/_/scheme://authority</c> for a host-shaped
    /// label. Null when the Host is not under the suffix at all — the request is then an ordinary
    /// path-based one, which keeps 127.0.0.1 working exactly as before.</summary>
    public string? MountFor(string? hostHeader)
    {
        if (string.IsNullOrEmpty(hostHeader)) return null;
        string host = hostHeader;
        int colon = host.LastIndexOf(':');
        if (colon > 0 && !host.Contains(']')) host = host[..colon];
        host = host.ToLowerInvariant();

        string label;
        if (host == Suffix) label = "";
        else if (host.EndsWith("." + Suffix, StringComparison.Ordinal)) label = host[..^(Suffix.Length + 1)];
        else return null;

        foreach (var route in _routes.All)
            if (LabelFor(route) == label) return route.PathPrefix;
        if (label.Length == 0) return null;   // bare suffix with no root mount: nothing owns it
        // A host-shaped label names a dynamic target; whether it is allowed is the gateway's call.
        return DecodeAuthority(label) is { } authority ? MtlsGateway.DynamicPrefix + authority : null;
    }

    /// <summary>The path the gateway should see for a request to <paramref name="mount"/>.</summary>
    public static string GatewayPath(string mount, string pathAndQuery)
    {
        if (mount == "/") return pathAndQuery;
        if (pathAndQuery.StartsWith('?')) pathAndQuery = "/" + pathAndQuery;
        return mount + pathAndQuery;
    }

    /// <summary>Where a browser reaches <paramref name="target"/> in host form: the origin of the
    /// static mount that fronts that authority, or the host-shaped name of an allowed dynamic target
    /// (<paramref name="gatewayPathFor"/> says whether it is reachable at all). Null leaves the
    /// gateway, as it would in path mode.</summary>
    public string? UrlFor(Uri target, Func<Uri, string?> gatewayPathFor)
    {
        string authority = target.GetLeftPart(UriPartial.Authority);
        GatewayRoute? best = null;
        foreach (var route in _routes.All)
            if (route.Upstream.GetLeftPart(UriPartial.Authority).Equals(authority, StringComparison.OrdinalIgnoreCase) &&
                (best is null || route.PathPrefix.Length > best.PathPrefix.Length))
                best = route;
        if (best is not null) return OriginFor(best).GetLeftPart(UriPartial.Authority) + target.PathAndQuery;
        if (gatewayPathFor(target) is null) return null;
        return $"{Scheme}://{EncodeAuthority(target)}.{Suffix}:{Port}{target.PathAndQuery}";
    }

    /// <summary>A URL authority as one DNS label: <c>api.corp</c>, <c>api.corp--8443</c>,
    /// <c>http--pub.corp</c>, <c>http--pub.corp--8080</c>. Dots are allowed inside a label here
    /// because every level under *.localhost is still loopback.</summary>
    public static string EncodeAuthority(Uri upstream)
    {
        string label = upstream.Host.ToLowerInvariant();
        if (!upstream.IsDefaultPort) label += "--" + upstream.Port;
        if (upstream.Scheme == Uri.UriSchemeHttp) label = "http--" + label;
        return label;
    }

    /// <summary>The inverse of <see cref="EncodeAuthority"/>; null when the label is not host-shaped.</summary>
    public static string? DecodeAuthority(string label)
    {
        string scheme = "https";
        if (label.StartsWith("http--", StringComparison.Ordinal)) { scheme = "http"; label = label[6..]; }
        string host = label;
        int? port = null;
        int sep = label.LastIndexOf("--", StringComparison.Ordinal);
        if (sep > 0)
        {
            if (!int.TryParse(label[(sep + 2)..], out var p) || p <= 0 || p > 65535) return null;
            host = label[..sep];
            port = p;
        }
        if (host.Length == 0 || Uri.CheckHostName(host) is not (UriHostNameType.Dns or UriHostNameType.IPv4)) return null;
        return port is null ? $"{scheme}://{host}" : $"{scheme}://{host}:{port}";
    }
}
