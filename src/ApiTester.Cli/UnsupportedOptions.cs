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

        // curl options people type out of habit. Most are accepted where they mean the same thing
        // (see Args.Aliases, and send's own curl spellings); a command that reaches this table for
        // one of these does not have it, so the message says where it does work. -c and -v keep
        // their place on every command: their meaning differs enough that quietly accepting them
        // would do something the user did not ask for. `certapi curl` reads both as curl does.
        ["-L"] = "-L is accepted on commands that send HTTP requests, where redirects are followed by default. This command does not follow redirects.",
        ["--location"] = "--location is accepted on commands that send HTTP requests, where redirects are followed by default. This command does not follow redirects.",
        ["--compressed"] = "--compressed is accepted on commands that send HTTP requests, where responses are decompressed by default. This command has no response to decompress.",
        ["-c"] = "Use --cookie-jar <file>: it reads cookies before the request and saves new ones after. To send cookies only, -b/--cookie. certapi curl reads -c as curl does.",
        ["-G"] = SendOnly + " Elsewhere, --query name=value adds the pair to the URL, percent-encoded (repeatable).",
        ["--get"] = SendOnly + " Elsewhere, --query name=value adds the pair to the URL, percent-encoded (repeatable).",
        ["-v"] = "Use --include to see the response status and headers, --debug for everything the tool did, or --trace for DNS/TCP/TLS. certapi curl reads -v as curl does.",
        ["--verbose"] = "Use --include to see the response status and headers, --debug for everything the tool did, or --trace for DNS/TCP/TLS. certapi curl reads --verbose as curl does.",
        ["-O"] = SendOnly + " Elsewhere, -o <file> names the file.",
        ["--remote-name"] = SendOnly + " Elsewhere, -o <file> names the file.",
        ["-e"] = SendOnly + " Elsewhere, -H \"Referer: <url>\".",
        ["--referer"] = SendOnly + " Elsewhere, -H \"Referer: <url>\".",
        ["-E"] = "-E <file[:password]> is accepted where client certificates are: this command takes none. --cert picks one from the Windows store by thumbprint or subject, --cert-file reads a .pfx/.p12/.pem.",
        ["--tlsv1.2"] = "--tlsv1.2 is accepted on commands that make TLS connections (it is --tls-min 1.2). This command makes none.",
        ["--tlsv1.3"] = "--tlsv1.3 is accepted on commands that make TLS connections (it is --tls-min 1.3). This command makes none.",
        ["-T"] = SendOnly + " Elsewhere, --data-file <path> with -X PUT.",
        ["--upload-file"] = SendOnly + " Elsewhere, --data-file <path> with -X PUT.",
        ["--data-binary"] = SendOnly + " Elsewhere, -d @file or --data-file <path>: a file that is not UTF-8 text is sent byte for byte.",
        ["--data-raw"] = SendOnly + " Elsewhere, -d/--data; a body that really starts with @ goes in a file passed to --data-file.",
    };

    private const string SendOnly = "certapi send (and certapi curl) accept this as curl does; this command does not.";

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
