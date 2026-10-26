namespace ApiTester.Core;

/// <summary>One upstream mounted at a path prefix on the gateway — a "site".</summary>
public sealed record GatewayRoute(string PathPrefix, Uri Upstream)
{
    /// <summary>The client certificate this route presents, when it has one of its own — a
    /// `serve --routes` file or profile site can name one. Null means the gateway's default (--cert).</summary>
    public System.Security.Cryptography.X509Certificates.X509Certificate2? Certificate { get; init; }

    /// <summary>What the site is called: <c>--upstream api=…</c>, or <c>"name"</c> in a routes file
    /// or profile. Also its virtual-host label under <c>--vhost</c>. Null for a mount written as a
    /// path, which is then named by its prefix.</summary>
    public string? Name { get; init; }

    /// <summary>Settings this site has that differ from the gateway's. Null: all inherited.</summary>
    public SiteSettings? Settings { get; init; }

    /// <summary>The name a person reads: the site name, else the prefix without its slashes, else
    /// "(root)" for the mount at <c>/</c>.</summary>
    public string DisplayName => Name ?? (PathPrefix.Trim('/') is { Length: > 0 } p ? p : "(root)");

    /// <summary>A site name is also a DNS label (its virtual host) and a path segment (its mount):
    /// letters, digits and inner hyphens.</summary>
    public static bool IsValidName(string name) =>
        name.Length is > 0 and <= 63 &&
        char.IsAsciiLetterOrDigit(name[0]) && char.IsAsciiLetterOrDigit(name[^1]) &&
        name.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');
}

/// <summary>A site's own connection settings — `serve` sites in a routes file or profile. Every
/// field is an override: unset means "whatever the gateway's flags say".</summary>
public sealed record SiteSettings
{
    /// <summary>Accept this site's server certificate whatever is wrong with it (true), or insist
    /// on a valid one even when the gateway runs with --insecure (false).</summary>
    public bool? Insecure { get; init; }

    /// <summary>The roots this site trusts beyond the Windows store: the gateway's --ca-file roots
    /// plus the site's own <c>caFile</c>, already merged.</summary>
    public ExtraRootSet? ExtraRoots { get; init; }

    /// <summary>The files the site added, for display (the merged set includes the gateway's).</summary>
    public IReadOnlyList<string> CaFiles { get; init; } = Array.Empty<string>();

    public RevocationMode? Revocation { get; init; }

    /// <summary>Answer this site's Negotiate/NTLM challenge with these credentials.</summary>
    public WindowsAuthOptions? WindowsAuth { get; init; }

    /// <summary>True: never answer this site's Windows-auth challenge, even with --windows-auth.</summary>
    public bool WindowsAuthOff { get; init; }

    /// <summary>Headers set (replace-or-add) on every request forwarded to this site.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> RequestHeaders { get; init; } =
        Array.Empty<KeyValuePair<string, string>>();

    /// <summary>Identity for connection pooling: two sites on one host with different settings
    /// must not share a pool, and two with the same settings may.</summary>
    public string PoolKey =>
        $"{Insecure}|{ExtraRoots?.Fingerprint}|{Revocation}|{(WindowsAuthOff ? "off" : WindowsAuth is null ? "" : WindowsAuth.UseDefaultCredentials ? "sso" : AccountName(WindowsAuth))}";

    /// <summary>The settings as short phrases for the startup table — never a secret's value.</summary>
    public IEnumerable<string> Describe()
    {
        if (Insecure == true) yield return "insecure";
        if (Insecure == false) yield return "verify";
        foreach (var f in CaFiles) yield return "ca " + Path.GetFileName(f);
        if (Revocation is { } r) yield return "revocation " + r.ToString().ToLowerInvariant();
        if (WindowsAuthOff) yield return "no windows-auth";
        else if (WindowsAuth is { } wa)
            yield return wa.UseDefaultCredentials ? "windows-auth" : "windows-auth " + AccountName(wa);
        if (RequestHeaders.Count > 0) yield return "headers " + string.Join(",", RequestHeaders.Select(h => h.Key));
    }

    private static string AccountName(WindowsAuthOptions wa) =>
        string.IsNullOrEmpty(wa.Domain) ? wa.Username ?? "" : wa.Domain + "\\" + wa.Username;
}

/// <summary>The gateway's routing table: which upstream a request path belongs to, and what path
/// to forward once the mount point has been stripped off.</summary>
public sealed class GatewayRoutes
{
    private const string Root = "/";

    /// <summary>Mount points the gateway keeps for itself: dynamic targets and its own endpoints.</summary>
    public static readonly string[] Reserved = { "/_", "/_certapi" };

    /// <summary>An empty table is legal — a gateway running only dynamic targets (`serve --allow`)
    /// has no static mounts — and then nothing static ever matches.</summary>
    public GatewayRoutes(IReadOnlyList<GatewayRoute> routes)
    {
        var normalized = new List<GatewayRoute>(routes.Count);
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var route in routes)
        {
            string prefix = Normalize(route.PathPrefix);
            foreach (var reserved in Reserved)
                if (prefix.Equals(reserved, StringComparison.OrdinalIgnoreCase) ||
                    prefix.StartsWith(reserved + "/", StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException(
                        $"The path prefix '{prefix}' is reserved for the gateway itself and cannot be mounted.", nameof(routes));
            // Two upstreams on one mount point is a configuration mistake with no right answer;
            // silently keeping the last one would send traffic to a host the user did not mean.
            if (!taken.Add(prefix))
                throw new ArgumentException(
                    $"Two upstreams are mounted at the same path prefix '{prefix}'.", nameof(routes));
            if (route.Name is { } name && !names.Add(name))
                throw new ArgumentException($"Two sites are named '{name}'.", nameof(routes));
            normalized.Add(route with { PathPrefix = prefix });
        }
        All = normalized;
    }

    public IReadOnlyList<GatewayRoute> All { get; }

    /// <summary>The route that owns <paramref name="pathAndQuery"/>, or null when none does — in
    /// which case <paramref name="forwardedPathAndQuery"/> is the input unchanged and the caller
    /// answers 404 without contacting anything. Longest prefix wins.
    /// <para>A request target that is not a rooted path — HttpListener will hand over
    /// "//evil.com/x" or "https://evil.com/x" as written — cannot match a mounted prefix, so it can
    /// only ever land on a root route, where it is passed through untouched. That is deliberate:
    /// routing is not the place to decide it is hostile, and the gateway's authority check refuses
    /// it a moment later, before the client certificate can reach that host.</para></summary>
    public GatewayRoute? Resolve(string pathAndQuery, out string forwardedPathAndQuery)
    {
        forwardedPathAndQuery = pathAndQuery;

        int mark = pathAndQuery.IndexOf('?');
        string path = mark < 0 ? pathAndQuery : pathAndQuery[..mark];
        string query = mark < 0 ? string.Empty : pathAndQuery[mark..];

        GatewayRoute? best = null;
        foreach (var route in All)
        {
            if (!Matches(route.PathPrefix, path)) continue;
            if (best is null || route.PathPrefix.Length > best.PathPrefix.Length) best = route;
        }
        if (best is null) return null;

        // The mount point is the gateway's, not the upstream's: strip it so the upstream sees the
        // path it would have served directly. A bare prefix leaves nothing, which is the root.
        // The root route mounts the whole namespace, so there is nothing to strip.
        if (best.PathPrefix != Root)
        {
            string rest = path[best.PathPrefix.Length..];
            forwardedPathAndQuery = (rest.Length == 0 ? Root : rest) + query;
        }
        return best;
    }

    /// <summary>One spelling per mount point, so "api", "/api" and "/api/" are the same route and
    /// the matching and stripping below can assume the canonical form.</summary>
    private static string Normalize(string pathPrefix)
    {
        string prefix = pathPrefix.Trim();
        if (prefix.Length == 0) return Root;
        if (prefix[0] != '/') prefix = Root + prefix;
        while (prefix.Length > 1 && prefix[^1] == '/') prefix = prefix[..^1];
        return prefix;
    }

    /// <summary>Whether <paramref name="prefix"/> owns <paramref name="path"/>. Matching is on whole
    /// segments, so /api owns /api, /api/ and /api/orders but not /apiary — a mount point that
    /// swallowed a longer word would send that word's traffic to the wrong host.</summary>
    private static bool Matches(string prefix, string path)
    {
        if (prefix == Root) return true;
        // Case-insensitively: a browser that upper-cases a path segment must still reach the host
        // the user mounted, and the paths themselves are relayed to the upstream verbatim.
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        return path.Length == prefix.Length || path[prefix.Length] == '/';
    }
}
