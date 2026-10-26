using System.Net;

namespace ApiTester.Core;

/// <summary>One entry of a NO_PROXY-style bypass list, compiled into the form the matcher needs.</summary>
public sealed record ProxyBypassRule
{
    private enum Kind { All, Address, Range, Domain }

    private readonly Kind _kind;
    private readonly int? _port;
    private readonly IPAddress? _address;
    private readonly IPNetwork? _network;
    private readonly string? _suffix;

    /// <summary>The entry in its normalized form — trimmed and lowercased. This is what round-trips
    /// through TransportSettings and what the diagnostics name, so it must parse back to an equal rule.</summary>
    public string Text { get; }

    private ProxyBypassRule(string text, Kind kind, int? port, IPAddress? address, IPNetwork? network, string? suffix)
    {
        Text = text;
        _kind = kind;
        _port = port;
        _address = address;
        _network = network;
        _suffix = suffix;
    }

    /// <summary>Compile one entry. False, with <paramref name="problem"/> naming the entry and what is
    /// wrong with it, when it cannot be understood.</summary>
    public static bool TryParse(string entry, out ProxyBypassRule? rule, out string? problem)
    {
        rule = null;
        problem = null;

        string trimmed = entry.Trim();
        if (trimmed.Length == 0)
        {
            problem = "a bypass entry cannot be empty";
            return false;
        }

        // The whole entry is normalized, not just the host part: it is what round-trips through
        // TransportSettings and what a re-parse of Text must reproduce exactly.
        string normalized = trimmed.ToLowerInvariant();

        if (normalized == "*")
        {
            rule = new ProxyBypassRule(normalized, Kind.All, null, null, null, null);
            return true;
        }

        string hostPart;
        int? port = null;

        if (normalized[0] == '[')
        {
            int close = normalized.IndexOf(']');
            if (close < 0)
            {
                problem = $"'{trimmed}' has an opening '[' with no matching ']'";
                return false;
            }
            hostPart = normalized[1..close];
            string rest = normalized[(close + 1)..];
            if (rest.Length > 0)
            {
                if (rest[0] != ':' || !int.TryParse(rest[1..], out var bracketPort) || bracketPort is < 1 or > 65535)
                {
                    problem = $"'{trimmed}' needs a port between 1 and 65535 after ']', got '{rest}'";
                    return false;
                }
                port = bracketPort;
            }
        }
        else
        {
            int colonCount = normalized.Count(c => c == ':');
            if (colonCount == 0)
            {
                hostPart = normalized;
            }
            else if (colonCount == 1)
            {
                int idx = normalized.IndexOf(':');
                hostPart = normalized[..idx];
                string portRaw = normalized[(idx + 1)..];
                if (!int.TryParse(portRaw, out var p) || p is < 1 or > 65535)
                {
                    problem = $"'{trimmed}' needs a port between 1 and 65535, got '{portRaw}'";
                    return false;
                }
                port = p;
            }
            else
            {
                // Two or more colons with no brackets: a bare IPv6 literal such as ::1, not host:port.
                hostPart = normalized;
            }
        }

        if (hostPart.Length == 0)
        {
            problem = $"'{trimmed}' has no host";
            return false;
        }

        if (hostPart.Contains('/'))
        {
            if (!IPNetwork.TryParse(hostPart, out var network))
            {
                problem = $"'{trimmed}' is not a valid network range — write it with its prefix length and " +
                           "host bits clear, e.g. 10.0.0.0/8";
                return false;
            }
            rule = new ProxyBypassRule(normalized, Kind.Range, port, null, network, null);
            return true;
        }

        if (IPAddress.TryParse(hostPart, out var address))
        {
            rule = new ProxyBypassRule(normalized, Kind.Address, port, address, null, null);
            return true;
        }

        if (hostPart == "*")
        {
            rule = new ProxyBypassRule(normalized, Kind.All, port, null, null, null);
            return true;
        }

        string suffix = hostPart;
        if (suffix.StartsWith("*."))
            suffix = suffix[2..];
        else if (suffix[0] == '.')
            suffix = suffix[1..];

        if (suffix.EndsWith('.'))
            suffix = suffix[..^1];

        if (suffix.Length == 0)
        {
            problem = $"'{trimmed}' has no host left after the wildcard/leading dot";
            return false;
        }
        if (suffix.Contains('*'))
        {
            problem = $"'{trimmed}' has a '*' where only a leading '*.' wildcard is supported";
            return false;
        }
        if (suffix.Any(char.IsWhiteSpace))
        {
            problem = $"'{trimmed}' contains whitespace";
            return false;
        }

        rule = new ProxyBypassRule(normalized, Kind.Domain, port, null, null, suffix);
        return true;
    }

    /// <summary>Whether this rule bypasses the proxy for this host and port. Never resolves DNS and
    /// never touches the network.</summary>
    public bool Matches(string host, int port)
    {
        // Checked first for every kind: a port-specific entry that does not match the port bypasses
        // nothing, no matter what kind of host rule it carries.
        if (_port is { } wantPort && wantPort != port)
            return false;

        return _kind switch
        {
            Kind.All => true,
            Kind.Address => IPAddress.TryParse(host, out var candidate) && candidate.Equals(_address),
            Kind.Range => IPAddress.TryParse(host, out var candidateAddr) && _network!.Value.Contains(candidateAddr),
            Kind.Domain => DomainMatches(host),
            _ => false
        };
    }

    private bool DomainMatches(string host)
    {
        string trimmedHost = host.TrimEnd('.');
        return trimmedHost.Equals(_suffix, StringComparison.OrdinalIgnoreCase)
            || trimmedHost.EndsWith("." + _suffix, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>Parsing, formatting, and matching for a NO_PROXY-style bypass list.</summary>
public static class ProxyBypass
{
    /// <summary>The empty list — no host bypasses the proxy.</summary>
    public static IReadOnlyList<ProxyBypassRule> None { get; } = Array.Empty<ProxyBypassRule>();

    /// <summary>Compile a comma-separated list. False, with <paramref name="problem"/> naming the
    /// offending entry, when any entry cannot be understood.</summary>
    public static bool TryParse(string? spec, out IReadOnlyList<ProxyBypassRule> rules, out string? problem)
    {
        rules = None;
        problem = null;

        var compiled = new List<ProxyBypassRule>();
        foreach (var token in (spec ?? "").Split(','))
        {
            // Separator noise, not a typo: "a,,b" and a trailing comma are fine.
            string trimmed = token.Trim();
            if (trimmed.Length == 0) continue;

            if (!ProxyBypassRule.TryParse(trimmed, out var rule, out problem))
                return false;
            compiled.Add(rule!);
        }

        rules = compiled;
        return true;
    }

    /// <summary>The same parse, dropping entries it cannot understand instead of failing.</summary>
    public static IReadOnlyList<ProxyBypassRule> ParseLenient(string? spec) => ParseLenient(spec, out _);

    /// <summary>As above, naming what was dropped so a caller can say so: an ambient variable that
    /// nobody typed for this run must not stop the run, but must not be silently half-applied either.</summary>
    public static IReadOnlyList<ProxyBypassRule> ParseLenient(string? spec, out IReadOnlyList<string> dropped)
    {
        var compiled = new List<ProxyBypassRule>();
        var skipped = new List<string>();
        foreach (var token in (spec ?? "").Split(','))
        {
            string trimmed = token.Trim();
            if (trimmed.Length == 0) continue;

            if (ProxyBypassRule.TryParse(trimmed, out var rule, out var problem))
                compiled.Add(rule!);
            else
                skipped.Add($"'{trimmed}' ({problem})");
        }
        dropped = skipped;
        return compiled;
    }

    /// <summary>The rule that sends this URL direct, or null when none does.</summary>
    public static ProxyBypassRule? Match(IReadOnlyList<ProxyBypassRule> rules, Uri uri)
    {
        string host = uri.Host.Trim('[', ']');
        foreach (var rule in rules)
        {
            if (rule.Matches(host, uri.Port))
                return rule;
        }
        return null;
    }

    /// <summary>Whether this list sends this URL direct.</summary>
    public static bool IsBypassed(IReadOnlyList<ProxyBypassRule> rules, Uri uri) => Match(rules, uri) is not null;

    /// <summary>The list's text form, comma-separated — what TransportSettings persists and what the
    /// diagnostics print. Round-trips through <see cref="TryParse"/>.</summary>
    public static string Format(IReadOnlyList<ProxyBypassRule> rules) =>
        string.Join(",", rules.Select(r => r.Text));

    /// <summary>NO_PROXY (or no_proxy) from the environment, or null when neither is set to anything.
    /// <paramref name="lookup"/> exists so a test can supply an environment without touching the
    /// process's own, which is global state a parallel test run must never mutate.</summary>
    public static string? EnvironmentSpec(Func<string, string?>? lookup = null)
    {
        var get = lookup ?? Environment.GetEnvironmentVariable;
        // NO_PROXY wins over no_proxy when both are set, matching curl and every other tool that
        // honors this pair: the upper-case form is the one actually documented.
        string? value = get("NO_PROXY");
        if (string.IsNullOrWhiteSpace(value))
            value = get("no_proxy");
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    /// <summary>The one wording for "a bypass list with the proxy switched off entirely", so the CLI
    /// and the transport validator cannot drift apart.</summary>
    public const string NoProxyWithProxyOffMessage =
        "--noproxy needs a proxy to bypass, but --no-proxy turns the proxy off entirely; use one or the other.";
}
