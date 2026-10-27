namespace ApiTester.Core;

/// <summary>The one answer to "is this header's value a credential?", shared by every place that
/// redacts or encrypts one — HAR files, Markdown notes, wire logs, the gateway's recordings, the
/// saved workspace — so a header hidden in one of them is never left showing in another.</summary>
public static class SecretHeaders
{
    private static readonly HashSet<string> Exact = new(StringComparer.OrdinalIgnoreCase)
    {
        "Authorization", "Proxy-Authorization", "Cookie", "Set-Cookie", "Set-Cookie2",
        "X-Certapi-Token", "X-Amz-Security-Token",
    };

    // A challenge says how to authenticate, not with what; hiding it would hide why a 401 happened.
    private static readonly HashSet<string> NeverSecret = new(StringComparer.OrdinalIgnoreCase)
    {
        "WWW-Authenticate", "Proxy-Authenticate", "Authentication-Info", "Proxy-Authentication-Info",
    };

    private static readonly string[] Fragments =
        { "token", "secret", "password", "passwd", "api-key", "apikey", "api_key", "auth", "session", "credential", "signature" };

    /// <summary>True for the standard credential headers and for any name that says it carries a
    /// token, key, secret, password or session (X-Api-Key, X-Auth-Token, X-CSRF-Token, …).</summary>
    public static bool IsSecret(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        string n = name.Trim();
        if (Exact.Contains(n)) return true;
        if (NeverSecret.Contains(n)) return false;
        foreach (var f in Fragments)
            if (n.Contains(f, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
