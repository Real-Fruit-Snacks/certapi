using System.Security.Cryptography.X509Certificates;

namespace ApiTester.Core;

/// <summary>A single pinned server certificate: this exact thumbprint is trusted for this exact
/// host, regardless of the certificate's chain of trust. Narrower than the blanket
/// <see cref="AppState.IgnoreServerCertErrors"/> bypass, which trusts anything for one request.</summary>
public sealed class TrustedServerCert
{
    public string Host { get; set; } = "";          // lowercase host, matches TokenService scoping
    /// <summary>What is pinned: an upper-case SHA-1 thumbprint for <see cref="PinKind.Leaf"/> and
    /// <see cref="PinKind.Issuer"/>, a base64 SHA-256 of the SubjectPublicKeyInfo for
    /// <see cref="PinKind.Spki"/>.</summary>
    public string Thumbprint { get; set; } = "";
    public string? Subject { get; set; }            // for display only
    public DateTime AddedUtc { get; set; }
    /// <summary>Null in files written before kinds existed, which means <see cref="PinKind.Leaf"/>.</summary>
    public PinKind? Kind { get; set; }

    public PinKind EffectiveKind => Kind ?? PinKind.Leaf;
}

/// <summary>What a pin matches against.</summary>
public enum PinKind
{
    /// <summary>The server's own certificate, by SHA-1 thumbprint. Breaks on every renewal.</summary>
    Leaf,
    /// <summary>Any certificate in the presented chain above the leaf, by SHA-1 thumbprint — the
    /// issuing CA. Survives renewals, and is the right primitive for a TLS-intercepting proxy that
    /// re-mints a leaf per site: pin the proxy's CA once.</summary>
    Issuer,
    /// <summary>The leaf's public key, by SHA-256 of its SubjectPublicKeyInfo (the HPKP form).
    /// Survives a renewal that keeps the key.</summary>
    Spki
}

/// <summary>Per-site server-certificate trust: a host may pin one or more thumbprints (a server
/// can present more than one certificate over its lifetime). Never throws — a bad or missing
/// host/thumbprint simply yields "not trusted" / is silently ignored on write, mirroring
/// <see cref="TokenService"/>'s shape.</summary>
public static class TrustService
{
    /// <summary>One spelling of a host for pin lookups: lower-case, and an IPv6 literal without the
    /// brackets a URL puts around it — so a pin typed as <c>::1</c> matches a connection whose
    /// <c>Uri.Host</c> is <c>[::1]</c>, and the other way round.</summary>
    public static string HostKey(string host)
    {
        string h = host.Trim().ToLowerInvariant();
        if (h.StartsWith('[') && h.EndsWith(']')) h = h[1..^1];
        return h;
    }

    private static bool SameHost(string a, string b) => HostKey(a) == HostKey(b);

    /// <summary>True when <paramref name="host"/> has this exact <paramref name="thumbprint"/>
    /// pinned. Both sides are compared case-insensitively.</summary>
    public static bool IsTrusted(AppState state, string host, string thumbprint)
    {
        if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(thumbprint)) return false;
        return state.TrustedServerCerts.Any(t =>
            t.EffectiveKind == PinKind.Leaf &&
            SameHost(t.Host, host) &&
            t.Thumbprint.Equals(thumbprint, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Every kind of pin at once: the leaf's thumbprint, the leaf's public key, or any
    /// issuer in <paramref name="chain"/>. The chain is the one the handshake built (it carries the
    /// intermediates the server sent, which a chain rebuilt from the leaf alone would not).</summary>
    public static bool IsTrusted(AppState state, string host, X509Certificate2 leaf, X509Chain? chain)
    {
        if (string.IsNullOrEmpty(host)) return false;
        string? spki = null;
        List<string>? issuerThumbprints = null;
        foreach (var pin in state.TrustedServerCerts)
        {
            if (!SameHost(pin.Host, host)) continue;
            switch (pin.EffectiveKind)
            {
                case PinKind.Leaf:
                    if (pin.Thumbprint.Equals(leaf.Thumbprint, StringComparison.OrdinalIgnoreCase)) return true;
                    break;
                case PinKind.Spki:
                    spki ??= SpkiSha256(leaf);
                    if (pin.Thumbprint.Equals(spki, StringComparison.Ordinal)) return true;
                    break;
                case PinKind.Issuer:
                    issuerThumbprints ??= IssuerThumbprints(leaf, chain);
                    if (issuerThumbprints.Any(t => t.Equals(pin.Thumbprint, StringComparison.OrdinalIgnoreCase))) return true;
                    break;
            }
        }
        return false;
    }

    /// <summary>The thumbprints of everything above the leaf in the presented chain. With no chain
    /// to read — a platform that handed the callback none — there is nothing to match, and an
    /// issuer pin does not fall back to trusting the leaf.</summary>
    private static List<string> IssuerThumbprints(X509Certificate2 leaf, X509Chain? chain)
    {
        var result = new List<string>();
        if (chain is null) return result;
        try
        {
            foreach (var element in chain.ChainElements)
                if (!element.Certificate.Thumbprint.Equals(leaf.Thumbprint, StringComparison.OrdinalIgnoreCase))
                    result.Add(element.Certificate.Thumbprint);
        }
        catch { /* a disposed chain contributes nothing */ }
        return result;
    }

    /// <summary>Base64 SHA-256 of the certificate's SubjectPublicKeyInfo — the value
    /// <c>openssl x509 -pubkey | openssl pkey -pubin -outform der | openssl dgst -sha256 -binary | base64</c>
    /// prints, and the form HPKP used.</summary>
    public static string SpkiSha256(X509Certificate2 certificate) =>
        Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(certificate.PublicKey.ExportSubjectPublicKeyInfo()));

    /// <summary>Upsert a pin: replaces any existing entry for the same host+thumbprint pair.
    /// A host may carry more than one pinned thumbprint at once — only an exact host+thumbprint
    /// match is replaced. Does nothing when host or thumbprint is null/empty.</summary>
    public static void Trust(AppState state, string host, string thumbprint, string? subject) =>
        Trust(state, host, thumbprint, subject, PinKind.Leaf);

    public static void Trust(AppState state, string host, string thumbprint, string? subject, PinKind kind)
    {
        if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(thumbprint)) return;
        string normalizedHost = HostKey(host);
        state.TrustedServerCerts.RemoveAll(t =>
            SameHost(t.Host, normalizedHost) &&
            t.EffectiveKind == kind &&
            t.Thumbprint.Equals(thumbprint, StringComparison.OrdinalIgnoreCase));
        state.TrustedServerCerts.Add(new TrustedServerCert
        {
            Host = normalizedHost,
            Thumbprint = thumbprint,
            Subject = subject,
            AddedUtc = DateTime.UtcNow,
            Kind = kind == PinKind.Leaf ? null : kind   // files written before kinds existed stay readable as written
        });
    }

    /// <summary>Convenience overload: pins the certificate's own thumbprint and subject.</summary>
    public static void Trust(AppState state, string host, X509Certificate2 serverCert) =>
        Trust(state, host, serverCert.Thumbprint, serverCert.Subject);

    /// <summary>Remove a pin. With <paramref name="thumbprint"/> null/empty, removes every pin for
    /// <paramref name="host"/>; otherwise removes only the exact host+thumbprint match.</summary>
    public static void Untrust(AppState state, string host, string? thumbprint = null)
    {
        if (string.IsNullOrEmpty(thumbprint))
            state.TrustedServerCerts.RemoveAll(t => SameHost(t.Host, host));
        else
            state.TrustedServerCerts.RemoveAll(t =>
                SameHost(t.Host, host) &&
                t.Thumbprint.Equals(thumbprint, StringComparison.OrdinalIgnoreCase));
    }
}
