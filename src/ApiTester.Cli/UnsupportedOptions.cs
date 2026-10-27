namespace ApiTester.Cli;

/// <summary>Options this tool deliberately does not have, paired with the reason and the thing to
/// use instead.
///
/// <para>The point is to answer the question the user actually asked. Someone who types
/// <c>--keylog</c> read somewhere that it exists and wants to decrypt traffic; "Unknown option
/// '--keylog'." is technically true and completely useless to them. A one-line explanation at the
/// exact moment they hit the wall is worth more than the same paragraph buried in a wiki page they
/// have no reason to open.</para>
///
/// <para>Only add entries here for options a reasonable person would expect to exist. This is not
/// a place to list typos.</para></summary>
public static class UnsupportedOptions
{
    private static readonly Dictionary<string, string> Explanations = new(StringComparer.Ordinal)
    {
        // Verified on .NET 9 / Windows: the switch and SSLKEYLOGFILE were tried in code, in
        // runtimeconfig.json, and in the process environment, and no key log was ever written.
        // Key logging in .NET is implemented behind OpenSSL; Windows TLS uses SChannel, which does
        // not expose session secrets. A flag here would only ever produce an empty file.
        ["--keylog"] = KeyLog,
        ["--key-log"] = KeyLog,
        ["--sslkeylogfile"] = KeyLog,
        ["--ssl-key-log-file"] = KeyLog,

        // curl options people type out of habit. The ones with a direct equivalent are accepted
        // as aliases (see Args.Aliases); these are the ones whose meaning differs enough that
        // quietly accepting them would do something the user did not ask for.
        ["-L"] = "Redirects are already followed (curl's -L is the default here). Drop it; --no-redirect turns following off.",
        ["--location"] = "Redirects are already followed (curl's --location is the default here). Drop it; --no-redirect turns following off.",
        ["--compressed"] = "Responses are already decompressed (gzip, deflate, br). Drop it; --no-decompress keeps the bytes as received.",
        ["-b"] = "Use --cookie-jar <file>: it reads cookies before the request and saves new ones after, curl's -b and -c in one. For a one-off cookie, -H \"Cookie: name=value\".",
        ["--cookie"] = "Use --cookie-jar <file> (reads and saves, curl's -b and -c in one), or -H \"Cookie: name=value\" for a one-off cookie.",
        ["-c"] = "Use --cookie-jar <file>: it reads cookies before the request and saves new ones after, curl's -b and -c in one.",
        ["-G"] = "Use --query name=value: it adds the pair to the URL, percent-encoded (repeatable).",
        ["--get"] = "Use --query name=value: it adds the pair to the URL, percent-encoded (repeatable).",
        ["--data-urlencode"] = "Use --query name=value to put an encoded pair in the URL, or -F name=value for a form body.",
        ["-v"] = "Use --include to see the response status and headers, --debug for everything the tool did, or --trace for DNS/TCP/TLS.",
        ["--verbose"] = "Use --include to see the response status and headers, --debug for everything the tool did, or --trace for DNS/TCP/TLS.",
        ["-O"] = "Use -o <file> and name the file.",
        ["--remote-name"] = "Use -o <file> and name the file.",
        ["-e"] = "Use -H \"Referer: <url>\".",
        ["--referer"] = "Use -H \"Referer: <url>\".",
        ["-E"] = "certapi's --cert picks a certificate from the Windows store by thumbprint or subject. For a file, use --cert-file <path> (.pfx/.p12/.pem) with --cert-password or --key-file.",
        ["--tlsv1.2"] = "Use --tls-min 1.2.",
        ["--tlsv1.3"] = "Use --tls-min 1.3.",
        ["-T"] = "Use --data-file <path> with -X PUT.",
        ["--upload-file"] = "Use --data-file <path> with -X PUT.",
        ["--data-raw"] = "Use -d/--data. As in curl, -d @file reads the body from a file; a body that really starts with @ goes in a file passed to --data-file.",
    };

    private const string KeyLog =
        "TLS key logging is not available on Windows: .NET writes key logs only through OpenSSL, " +
        "and Windows TLS uses SChannel, which does not expose session secrets. Use '--wire' " +
        "instead — it prints the decrypted request and response directly, which is what the keys " +
        "would have been for. See wiki page 23 (Troubleshooting).";

    /// <summary>The explanation for <paramref name="option"/>, or null if it is simply unknown.</summary>
    public static string? Explain(string option) =>
        Explanations.TryGetValue(
            option.StartsWith("--", StringComparison.Ordinal) ? option.ToLowerInvariant() : option, out var why)
            ? why : null;
}
