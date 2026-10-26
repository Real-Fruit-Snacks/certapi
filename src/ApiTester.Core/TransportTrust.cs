using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace ApiTester.Core;

/// <summary>Root certificates trusted for this run in addition to the machine's store — the
/// <c>--ca-file</c> flag. Exists for the network where a TLS-intercepting proxy re-signs every site
/// with a private CA that is handed out as a PEM file rather than installed, where the choice used
/// to be <c>--insecure</c> or nothing. Immutable once loaded; <see cref="Fingerprint"/> is what
/// makes two option sets with the same roots share a pooled connection.</summary>
public sealed class ExtraRootSet
{
    private readonly X509Certificate2Collection _roots;

    private ExtraRootSet(X509Certificate2Collection roots, IReadOnlyList<string> sources)
    {
        _roots = roots;
        Sources = sources;
        using var sha = SHA256.Create();
        var hashes = roots.Cast<X509Certificate2>()
            .Select(c => c.GetCertHashString(HashAlgorithmName.SHA256))
            .OrderBy(h => h, StringComparer.Ordinal);
        Fingerprint = Convert.ToHexString(sha.ComputeHash(System.Text.Encoding.ASCII.GetBytes(string.Join(",", hashes))));
    }

    /// <summary>The files the roots came from, for diagnostics.</summary>
    public IReadOnlyList<string> Sources { get; }

    /// <summary>A stable hash of the set's certificates, independent of file names or order.</summary>
    public string Fingerprint { get; }

    public int Count => _roots.Count;

    public IEnumerable<X509Certificate2> Certificates => _roots.Cast<X509Certificate2>();

    /// <summary>Load one or more PEM (or DER) files. A PEM file may carry several CERTIFICATE
    /// blocks — a bundle — and every one of them is taken.</summary>
    /// <exception cref="CertificateFileException">A file is missing, unreadable, or holds no
    /// certificate — refused rather than silently trusting nothing, which would leave the user
    /// believing the proxy's root was accepted.</exception>
    public static ExtraRootSet Load(IReadOnlyList<string> paths)
    {
        var roots = new X509Certificate2Collection();
        foreach (var path in paths)
        {
            if (!File.Exists(path))
                throw new CertificateFileException($"--ca-file: file not found: {path}");
            int before = roots.Count;
            try
            {
                string text = File.ReadAllText(path);
                if (text.Contains("-----BEGIN", StringComparison.Ordinal))
                    roots.ImportFromPem(text);
                else
                    roots.Add(X509CertificateLoader.LoadCertificate(File.ReadAllBytes(path)));
            }
            catch (CryptographicException ex)
            {
                throw new CertificateFileException($"--ca-file: could not read a certificate from {path}: {ex.Message}");
            }
            if (roots.Count == before)
                throw new CertificateFileException($"--ca-file: {path} holds no certificate.");
        }
        return new ExtraRootSet(roots, paths.ToList());
    }

    /// <summary>Build a chain for <paramref name="leaf"/> that trusts <em>these</em> roots (and
    /// only these: the machine store already had its say in the chain the handshake built). True
    /// when the chain is valid under them. Revocation is deliberately not checked here — the
    /// handshake's own chain carries the revocation flags <see cref="RevocationCheck.Decide"/> reads,
    /// and this rebuild answers one question only: is the root a root the user named?</summary>
    public bool Trusts(X509Certificate2 leaf, X509Chain? original)
    {
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.AddRange(_roots);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.DisableCertificateDownloads = true;
        // The intermediates the server actually sent, so a chain that is complete on the wire is
        // complete here too without an AIA fetch a restricted network would block.
        if (original is not null)
        {
            try
            {
                foreach (var element in original.ChainElements)
                    chain.ChainPolicy.ExtraStore.Add(element.Certificate);
            }
            catch { /* a disposed chain contributes nothing */ }
        }
        try { return chain.Build(leaf); }
        catch (CryptographicException) { return false; }
    }
}

/// <summary>Where the extra roots meet the validation callbacks: given what the handshake decided,
/// clear the chain-trust errors when the user's own roots vouch for the certificate, and leave
/// everything else — name mismatch, expiry, revocation flags — exactly as the handshake reported it.
/// Called from every callback in the product, so a root honoured by <c>send</c> is honoured by
/// <c>serve</c>, <c>grpc</c> and the streams too.</summary>
public static class ExtraRoots
{
    private const X509ChainStatusFlags TrustFlags =
        X509ChainStatusFlags.UntrustedRoot | X509ChainStatusFlags.PartialChain;

    public static (SslPolicyErrors Errors, X509ChainStatusFlags Flags) Reconsider(
        SslPolicyErrors errors, X509ChainStatusFlags flags, X509Certificate? certificate,
        X509Chain? chain, ExtraRootSet? roots)
    {
        if (roots is null || roots.Count == 0 || certificate is null) return (errors, flags);
        if (!errors.HasFlag(SslPolicyErrors.RemoteCertificateChainErrors)) return (errors, flags);
        // Only a trust problem is ours to reconsider; a chain that is also expired, revoked or
        // otherwise broken keeps those flags and is judged on them.
        if ((flags & ~(TrustFlags | X509ChainStatusFlags.RevocationStatusUnknown | X509ChainStatusFlags.OfflineRevocation)) != X509ChainStatusFlags.NoError)
            return (errors, flags);

        using var leaf = new X509Certificate2(certificate);
        if (!roots.Trusts(leaf, chain)) return (errors, flags);

        var remaining = flags & ~TrustFlags;
        var remainingErrors = errors & ~SslPolicyErrors.RemoteCertificateChainErrors;
        // The revocation-unknown flags survive on purpose: the user's root vouches for the chain,
        // and RevocationCheck.Decide still owns whether an unverifiable status is acceptable.
        if ((remaining & (X509ChainStatusFlags.RevocationStatusUnknown | X509ChainStatusFlags.OfflineRevocation)) != 0)
            remainingErrors |= SslPolicyErrors.RemoteCertificateChainErrors;
        return (remainingErrors, remaining);
    }
}

/// <summary>The TLS version floor — <c>--tls-min</c>. Default is whatever the OS allows, which is
/// what this client always did; 1.2 refuses TLS 1.0/1.1 servers plainly, 1.3 proves a server (or a
/// middlebox) really speaks it.</summary>
public enum TlsFloor
{
    Default,
    Tls12,
    Tls13
}

public static class TlsPolicy
{
    public static void Apply(SslClientAuthenticationOptions options, TransportOptions transport)
    {
        options.EnabledSslProtocols = transport.MinTls switch
        {
            TlsFloor.Tls12 => SslProtocols.Tls12 | SslProtocols.Tls13,
            TlsFloor.Tls13 => SslProtocols.Tls13,
            _ => SslProtocols.None
        };
        if (transport.Sni is { } sni) options.TargetHost = sni;
    }

    public static TlsFloor ParseFloor(string raw) => raw.Trim().ToLowerInvariant() switch
    {
        "1.2" or "tls1.2" or "tls12" => TlsFloor.Tls12,
        "1.3" or "tls1.3" or "tls13" => TlsFloor.Tls13,
        _ => throw new ArgumentException($"--tls-min expects 1.2 or 1.3, got '{raw}'.")
    };
}
