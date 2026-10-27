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

    /// <summary>One host written two ways is one host: a pin stored as bücher.example (as a URL
    /// shows it) matches a connection to xn--bcher-kva.example (as TLS names it), and back.</summary>
    private static bool SameHost(string a, string b)
    {
        string x = HostKey(a), y = HostKey(b);
        if (x == y) return true;
        return AsciiHost(x) is { } ax && AsciiHost(y) is { } ay && ax == ay;
    }

    private static readonly System.Globalization.IdnMapping Idn = new();

    private static string? AsciiHost(string host)
    {
        try { return Idn.GetAscii(host).ToLowerInvariant(); }
        catch (ArgumentException) { return null; }
    }

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
    /// <remarks>Without explicit policy errors, the ones the current validation callback reported
    /// (<see cref="TrustContext.CurrentPolicyErrors"/>) apply — so a predicate written by hand
    /// inside a callback still refuses an issuer pin for a certificate issued to another name.</remarks>
    public static bool IsTrusted(AppState state, string host, X509Certificate2 leaf, X509Chain? chain) =>
        IsTrusted(state, host, leaf, chain, TrustContext.CurrentPolicyErrors);

    /// <summary>The trust decision for the connection being validated right now: its chain, its
    /// policy errors, and the host it actually reached (a redirect hop's, not the first one's).</summary>
    public static bool IsTrustedHere(AppState state, string host, X509Certificate2 leaf) =>
        IsTrusted(state, TrustContext.CurrentHost ?? host, leaf, TrustContext.CurrentChain, TrustContext.CurrentPolicyErrors);

    public static bool IsTrusted(AppState state, string host, X509Certificate2 leaf, X509Chain? chain,
                                 System.Net.Security.SslPolicyErrors policyErrors)
    {
        // A leaf or key pin names this server's own certificate, so it stands in for the name check
        // too (an IP literal, an internal alias). An issuer pin only vouches for the chain: a
        // certificate the pinned CA issued for some other name is still some other server's.
        bool nameMatches = !policyErrors.HasFlag(System.Net.Security.SslPolicyErrors.RemoteCertificateNameMismatch);
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
                    if (!nameMatches) break;
                    issuerThumbprints ??= IssuerThumbprints(leaf, chain);
                    if (issuerThumbprints.Any(t => t.Equals(pin.Thumbprint, StringComparison.OrdinalIgnoreCase))) return true;
                    break;
            }
        }
        return false;
    }

    /// <summary>The thumbprints of the certificates above the leaf in the presented chain that
    /// actually vouch for it: walking up from the leaf, each one is included only while every
    /// certificate below it was validly signed by the next and is fit to sit where it does. A leaf
    /// that merely names the pinned CA as its issuer — signed by some other key — matches nothing.
    /// With no chain to read — a platform that handed the callback none — there is nothing to
    /// match, and an issuer pin does not fall back to trusting the leaf.</summary>
    private static List<string> IssuerThumbprints(X509Certificate2 leaf, X509Chain? chain)
    {
        var result = new List<string>();
        if (chain is null) return result;
        try
        {
            var elements = chain.ChainElements;
            if (elements.Count == 0 ||
                !elements[0].Certificate.Thumbprint.Equals(leaf.Thumbprint, StringComparison.OrdinalIgnoreCase))
                return result;
            for (int i = 0; i < elements.Count; i++)
            {
                if (i > 0) result.Add(elements[i].Certificate.Thumbprint);
                var flags = X509ChainStatusFlags.NoError;
                foreach (var st in elements[i].ChainElementStatus) flags |= st.Status;
                if ((flags & BrokenLink) != 0) break; // nothing above this one vouches for it
            }
        }
        catch { /* a disposed chain contributes nothing */ }
        return result;
    }

    private const X509ChainStatusFlags BrokenLink =
        X509ChainStatusFlags.NotSignatureValid | X509ChainStatusFlags.InvalidBasicConstraints |
        X509ChainStatusFlags.HasNotSupportedCriticalExtension | X509ChainStatusFlags.Cyclic |
        X509ChainStatusFlags.InvalidNameConstraints | X509ChainStatusFlags.HasNotPermittedNameConstraint |
        X509ChainStatusFlags.HasExcludedNameConstraint | X509ChainStatusFlags.ExplicitDistrust |
        X509ChainStatusFlags.Revoked | X509ChainStatusFlags.InvalidExtension;

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
