namespace ApiTester.Cli.Mcp;

/// <summary>Decides whether a URL may be contacted. Empty allow-list means any http(s) host is
/// permitted; otherwise the host must equal an allowed entry or be a dotted subdomain of one.</summary>
public sealed class HostAllowlist
{
    private readonly List<string> _allowed;

    public HostAllowlist(IReadOnlyList<string> allowed)
    {
        foreach (var a in allowed)
            if (EntryProblem(a) is { } problem)
                throw new ArgumentException($"allow-list entry '{a}': {problem}", nameof(allowed));
        _allowed = allowed.Select(a => a.Trim().ToLowerInvariant()).ToList();
    }

    /// <summary>How many hosts are allowed; zero means any host.</summary>
    public int Count => _allowed.Count;

    /// <summary>The entries as parsed.</summary>
    public IReadOnlyList<string> Entries => _allowed;

    /// <summary>Why <paramref name="entry"/> cannot be an allow-list entry, or null. An entry is a
    /// bare host name or IP address; anything else would either match nothing (a scheme, port or
    /// path) or — the empty value — silently mean "any host".</summary>
    public static string? EntryProblem(string? entry)
    {
        string e = (entry ?? "").Trim();
        if (e.Length == 0) return "an empty value is not a host (omit --allow to allow any host)";
        if (e.Contains("://", StringComparison.Ordinal)) return "give the host name only, without a scheme";
        if (e.Contains('*')) return "wildcards are not supported; an entry already allows its subdomains";
        if (e.Contains(',')) return "give each host as its own --allow";
        if (e.Contains('/') || e.Contains('?') || e.Contains('#') || e.Contains('@')) return "give the host name only, without a path";
        if (e.Any(char.IsWhiteSpace)) return "a host name has no spaces";
        bool bracketedIpv6 = e.StartsWith('[') && e.EndsWith(']');
        if (!bracketedIpv6 && e.Contains(':'))
            return System.Net.IPAddress.TryParse(e, out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
                ? $"write an IPv6 address in brackets: [{e}]"
                : "ports are not part of an entry; any port on the host is allowed";
        if (e.EndsWith('.')) return "drop the trailing dot";
        return null;
    }

    public bool IsAllowed(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;
        if (_allowed.Count == 0) return true;

        string host = uri.Host.ToLowerInvariant();
        foreach (var entry in _allowed)
            if (host == entry || host.EndsWith("." + entry, StringComparison.Ordinal))
                return true;
        return false;
    }
}
