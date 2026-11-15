using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace ApiTester.Core;

public static class SelfSignedCertificateFactory
{
    /// <summary>A new RSA-2048 key, generated now rather than on first use. Generating one takes
    /// 70–120 ms on Windows, so a caller that needs several makes them in parallel with this and
    /// passes each to the factory (which then owns and disposes it).</summary>
    public static RSA NewRsaKey()
    {
        var rsa = RSA.Create(2048);
        _ = rsa.ExportRSAPublicKey();   // CNG generates the key lazily: make it happen here
        return rsa;
    }

    /// <summary>Starts <paramref name="count"/> keys generating on the thread pool.</summary>
    public static Task<RSA>[] NewRsaKeys(int count) =>
        Enumerable.Range(0, count).Select(_ => Task.Run(NewRsaKey)).ToArray();

    public static X509Certificate2 CreateCertificateAuthority(string name, RSA? key = null)
    {
        using var rsa = key ?? RSA.Create(2048);
        var req = new CertificateRequest($"CN={name}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.DigitalSignature, true));
        req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));

        // CA validity must be wider than leaf certs (+1y) so a leaf's notAfter can never exceed the issuer's,
        // avoiding the intermittent CertificateRequest.Create failure.
        using var cert = req.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10));
        // Round-trip through PKCS#12 so the private key is usable by SslStream on Windows.
        // SChannel/SslStream cannot access ephemeral keys; use Exportable-only to create a temporary
        // non-persisted container that SChannel can use, auto-deleted on Dispose.
        return X509CertificateLoader.LoadPkcs12(
            cert.Export(X509ContentType.Pfx), (string?)null,
            X509KeyStorageFlags.Exportable);
    }

    /// <summary>Loopback port 1: nothing ever listens there, so a connection attempt is REFUSED
    /// immediately by the operating system rather than left to time out. A test that hands a
    /// certificate carrying this distribution point to a real revocation check therefore fails (or
    /// passes) off an observable event — the refusal — not off the clock. Do not "helpfully" swap
    /// this for a TEST-NET address (192.0.2.0/24) or an unresolvable hostname: both of those are
    /// unreachable in a different way, by going silent, and silence is exactly what turns into a
    /// wall-clock-raced test. This project has been bitten by that six releases running.</summary>
    public const string UnroutableCrlDistributionPoint = "http://127.0.0.1:1/certapi-test.crl";

    /// <param name="crlDistributionPoint">When non-null, a URL added to the certificate as a
    /// certificate revocation list (CRL) distribution point extension. This exists for exactly one
    /// reason: proving that a requested revocation mode actually reached the TLS stack rather than
    /// being parsed and silently dropped. A platform can only report a "revocation status unknown"
    /// chain flag if it genuinely attempted to fetch a CRL from this address and failed, so a
    /// certificate advertising an unreachable endpoint (see <see cref="UnroutableCrlDistributionPoint"/>)
    /// is the one artifact that turns "the flag was set" into proof the check actually ran. Left
    /// null (the default), no such extension is added at all, and the certificate is byte-for-byte
    /// the same shape it was before this parameter existed — every existing caller depends on that.</param>
    /// <param name="notBefore">When this certificate starts being valid; defaults to yesterday.
    /// Together with <paramref name="notAfter"/> this is what lets a caller mint a deliberately
    /// expired or not-yet-valid certificate — the mock's TLS-misbehaviour modes, which make the
    /// client's own error paths reachable from a terminal rather than only from a test.</param>
    /// <param name="notAfter">When it stops; defaults to a year out.</param>
    /// <param name="key">The key to certify, made ahead (see <see cref="NewRsaKey"/>); the factory
    /// owns it from here. Null for a new one.</param>
    /// <param name="ipAddresses">Addresses added to the Subject Alternative Name beside the DNS names.</param>
    public static X509Certificate2 CreateSignedCertificate(
        string name, X509Certificate2 issuer, bool serverAuth, bool clientAuth,
        IEnumerable<string>? dnsNames = null,
        string? crlDistributionPoint = null,
        DateTimeOffset? notBefore = null,
        DateTimeOffset? notAfter = null,
        IEnumerable<System.Net.IPAddress>? ipAddresses = null,
        RSA? key = null)
    {
        using var rsa = key ?? RSA.Create(2048);
        var req = new CertificateRequest($"CN={name}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));

        var ekus = new OidCollection();
        if (serverAuth) ekus.Add(new Oid("1.3.6.1.5.5.7.3.1"));
        if (clientAuth) ekus.Add(new Oid("1.3.6.1.5.5.7.3.2"));
        if (ekus.Count > 0)
            req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(ekus, false));

        req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));

        if (dnsNames is not null || ipAddresses is not null)
        {
            var san = new SubjectAlternativeNameBuilder();
            foreach (var d in dnsNames ?? []) san.AddDnsName(d);
            foreach (var ip in ipAddresses ?? []) san.AddIpAddress(ip);
            req.CertificateExtensions.Add(san.Build());
        }

        if (crlDistributionPoint is not null)
        {
            req.CertificateExtensions.Add(
                CertificateRevocationListBuilder.BuildCrlDistributionPointExtension(
                    new[] { crlDistributionPoint }));
        }

        var serial = new byte[16];
        RandomNumberGenerator.Fill(serial);

        using var signed = req.Create(
            issuer,
            notBefore ?? DateTimeOffset.UtcNow.AddDays(-1),
            notAfter ?? DateTimeOffset.UtcNow.AddYears(1),
            serial);
        using var withKey = signed.CopyWithPrivateKey(rsa);
        // SChannel/SslStream cannot access ephemeral keys; use Exportable-only to create a temporary
        // non-persisted container that SChannel can use, auto-deleted on Dispose.
        return X509CertificateLoader.LoadPkcs12(
            withKey.Export(X509ContentType.Pfx), (string?)null,
            X509KeyStorageFlags.Exportable);
    }

    /// <summary>A self-signed TLS server certificate for a local gateway: subject
    /// CN=<paramref name="commonName"/>, server-authentication extended key usage, and a Subject
    /// Alternative Name (SAN) covering the given DNS names and IP addresses. Self-signed rather than
    /// issued by a generated certificate authority so that installing this one certificate into a
    /// trust store is enough for a browser to accept it — a leaf chaining to an untrusted authority
    /// would still warn.</summary>
    public static X509Certificate2 CreateSelfSignedServerCertificate(
        string commonName,
        IEnumerable<string>? dnsNames = null,
        IEnumerable<System.Net.IPAddress>? ipAddresses = null)
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest($"CN={commonName}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));
        req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));

        var san = new SubjectAlternativeNameBuilder();
        if (dnsNames is not null)
            foreach (var d in dnsNames) san.AddDnsName(d);
        if (ipAddresses is not null)
            foreach (var ip in ipAddresses) san.AddIpAddress(ip);
        req.CertificateExtensions.Add(san.Build());

        using var cert = req.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        // SChannel/SslStream cannot access ephemeral keys; use Exportable-only to create a temporary
        // non-persisted container that SChannel can use, auto-deleted on Dispose.
        return X509CertificateLoader.LoadPkcs12(
            cert.Export(X509ContentType.Pfx), (string?)null,
            X509KeyStorageFlags.Exportable);
    }
}
