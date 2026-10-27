using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace ApiTester.Core;

public sealed class CertificateStoreService
{
    private const string ClientAuthOid = "1.3.6.1.5.5.7.3.2";

    public IReadOnlyList<CertificateInfo> ListClientCertificates(bool includeLocalMachine = false)
    {
        var all = new X509Certificate2Collection();
        all.AddRange(ReadStore(StoreLocation.CurrentUser));
        if (includeLocalMachine)
        {
            // A certificate imported into both stores is one certificate: listed once (the
            // CurrentUser copy), so --cert <thumbprint> --store LocalMachine is not "ambiguous".
            var seen = new HashSet<string>(all.Cast<X509Certificate2>().Select(c => c.Thumbprint), StringComparer.OrdinalIgnoreCase);
            foreach (var c in ReadStore(StoreLocation.LocalMachine))
            {
                if (seen.Add(c.Thumbprint)) all.Add(c);
                else c.Dispose();
            }
        }
        return BuildCertificateInfos(all);
    }

    public X509Certificate2? FindByThumbprint(string thumbprint, bool includeLocalMachine = false)
    {
        return ListClientCertificates(includeLocalMachine)
            .FirstOrDefault(c => c.Thumbprint.Equals(thumbprint, StringComparison.OrdinalIgnoreCase))
            ?.Certificate;
    }

    private static X509Certificate2Collection ReadStore(StoreLocation location)
    {
        try
        {
            using var store = new X509Store(StoreName.My, location);
            store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
            // Copy out so certs survive store disposal.
            return new X509Certificate2Collection(
                store.Certificates.Cast<X509Certificate2>().ToArray());
        }
        catch (CryptographicException)
        {
            return new X509Certificate2Collection(); // store unavailable (e.g. LocalMachine w/o rights)
        }
    }

    internal static IReadOnlyList<CertificateInfo> BuildCertificateInfos(X509Certificate2Collection certs)
    {
        var result = new List<CertificateInfo>();
        foreach (var cert in certs.Cast<X509Certificate2>())
        {
            if (!cert.HasPrivateKey) continue;
            result.Add(new CertificateInfo
            {
                Subject = cert.Subject,
                Issuer = cert.Issuer,
                Thumbprint = cert.Thumbprint,
                NotBefore = cert.NotBefore,
                NotAfter = cert.NotAfter,
                HasClientAuthEku = HasClientAuth(cert),
                Certificate = cert
            });
        }
        return result
            .OrderByDescending(c => c.HasClientAuthEku)
            .ThenBy(c => c.Subject)
            .ToList();
    }

    private static bool HasClientAuth(X509Certificate2 cert)
    {
        foreach (var ext in cert.Extensions.OfType<X509EnhancedKeyUsageExtension>())
            foreach (var oid in ext.EnhancedKeyUsages)
                if (oid.Value == ClientAuthOid) return true;
        return false;
    }
}
