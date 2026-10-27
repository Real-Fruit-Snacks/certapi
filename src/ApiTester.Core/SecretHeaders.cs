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
    // CORS headers are policy, and a replayed or exported response must keep them working.
    private static readonly HashSet<string> NeverSecret = new(StringComparer.OrdinalIgnoreCase)
    {
        "WWW-Authenticate", "Proxy-Authenticate", "Authentication-Info", "Proxy-Authentication-Info",
        "Authentication-Results",
    };

    // Anywhere in the name: no ordinary header carries these letters.
    private static readonly string[] Substrings = { "token", "secret", "password", "passwd", "apikey", "api-key", "api_key" };

    // Whole words of the name (split on - and _): "auth" is X-Auth-User, not X-Author.
    private static readonly HashSet<string> Words = new(StringComparer.OrdinalIgnoreCase)
    {
        "auth", "authorization", "session", "sessionid", "credential", "credentials", "signature", "sig", "cookie",
    };

    /// <summary>True for the standard credential headers and for any name that says it carries a
    /// token, key, secret, password, session or credential (X-Api-Key, X-Auth-Token, X-CSRF-Token, …).</summary>
    public static bool IsSecret(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        string n = name.Trim();
        if (Exact.Contains(n)) return true;
        if (NeverSecret.Contains(n) || n.StartsWith("Access-Control-", StringComparison.OrdinalIgnoreCase)) return false;
        foreach (var f in Substrings)
            if (n.Contains(f, StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var w in n.Split('-', '_'))
            if (Words.Contains(w)) return true;
        return false;
    }
}
