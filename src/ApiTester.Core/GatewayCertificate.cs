using System.Net;
using System.Security.Cryptography.X509Certificates;

namespace ApiTester.Core;

/// <summary>The TLS certificate `certapi serve --tls` presents on loopback: CN=127.0.0.1 with a
/// Subject Alternative Name (SAN) covering 127.0.0.1 and localhost, cached under
/// %LocalAppData%\CertApiTester\gateway-tls\ and reused across runs — a fresh certificate every run
/// would retrain the browser's trust prompt every time. The key file is encrypted for the Windows
/// user (DPAPI) and kept out of the roaming profile, so it does not travel to other machines.</summary>
public static class GatewayCertificate
{
    private const string PfxFileName = "gateway.pfx";
    private const string CerFileName = "gateway.cer";
    private const string PreviousFileName = "previous-thumbprints.txt";

    /// <summary>%LocalAppData%\CertApiTester\gateway-tls. A cache an earlier version left in the
    /// roaming folder is moved here on first use.</summary>
    public static string DefaultDirectory
    {
        get
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                      "CertApiTester", "gateway-tls");
            MoveFromRoaming(dir);
            return dir;
        }
    }

    private static void MoveFromRoaming(string localDir)
    {
        try
        {
            string roaming = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                                          "CertApiTester", "gateway-tls");
            if (string.Equals(Path.GetFullPath(roaming), Path.GetFullPath(localDir), StringComparison.OrdinalIgnoreCase)) return;
            if (!Directory.Exists(roaming) || File.Exists(Path.Combine(localDir, PfxFileName))) return;
            Directory.CreateDirectory(localDir);
            foreach (var file in Directory.GetFiles(roaming))
            {
                string target = Path.Combine(localDir, Path.GetFileName(file));
                if (Path.GetFileName(file).Equals(PfxFileName, StringComparison.OrdinalIgnoreCase))
                    WritePfx(target, ReadPfx(file));          // encrypted on the way across
                else
                    File.Copy(file, target, overwrite: false);
                File.Delete(file);
            }
            try { Directory.Delete(roaming); } catch (IOException) { }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {
            // A cache that cannot be moved is regenerated in the new place.
        }
    }

    /// <summary>The PKCS#12 bytes of a cached key file: decrypted when it was stored encrypted, as
    /// they are when an earlier version wrote it in the clear.</summary>
    internal static byte[] ReadPfx(string path) => ReadPfx(path, out _);

    internal static byte[] ReadPfx(string path, out bool wasSealed)
    {
        byte[] raw = File.ReadAllBytes(path);
        var prefix = System.Text.Encoding.ASCII.GetBytes(SecretProtection.Prefix);
        wasSealed = raw.AsSpan().StartsWith(prefix);
        if (!wasSealed) return raw;
        string asText = System.Text.Encoding.ASCII.GetString(raw);
        string? base64 = SecretProtection.Default.Unprotect(asText.Trim());
        if (base64 is null) throw new System.Security.Cryptography.CryptographicException("the cached gateway key could not be decrypted");
        return Convert.FromBase64String(base64);
    }

    /// <summary>Writes the key file encrypted for this user where that is possible (DPAPI), and
    /// in the clear only where no protector exists.</summary>
    internal static void WritePfx(string path, byte[] pfx)
    {
        var protector = SecretProtection.Default;
        string? sealedText = protector.IsAvailable ? protector.Protect(Convert.ToBase64String(pfx)) : null;
        string temp = path + ".tmp";
        File.WriteAllBytes(temp, sealedText is null ? pfx : System.Text.Encoding.ASCII.GetBytes(sealedText));
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>The cached certificate, generated and cached on first use, and regenerated when the
    /// cached one is missing, unreadable, or within 7 days of expiry.</summary>
    public static X509Certificate2 LoadOrCreate(string? directory = null) => LoadOrCreate(directory, null, out _);

    /// <summary>As <see cref="LoadOrCreate(string?)"/>, and the certificate also names every one of
    /// <paramref name="dnsNames"/> — the virtual-host suffix and its wildcard, under --vhost. A
    /// cached certificate that lacks one is replaced by one naming everything it named plus the
    /// new names, so switching between suffixes never loses one; <paramref name="replaced"/> is
    /// the thumbprint of the certificate it replaced, so the caller can move a binding along.</summary>
    public static X509Certificate2 LoadOrCreate(string? directory, IReadOnlyCollection<string>? dnsNames, out string? replaced)
    {
        replaced = null;
        string dir = directory ?? DefaultDirectory;
        Directory.CreateDirectory(dir);
        string pfxPath = Path.Combine(dir, PfxFileName);
        var names = new List<string> { "localhost" };

        if (File.Exists(pfxPath))
        {
            try
            {
                byte[] stored = ReadPfx(pfxPath, out bool wasSealed);
                var cached = X509CertificateLoader.LoadPkcs12(stored, (string?)null, X509KeyStorageFlags.Exportable);
                // A key file an earlier version wrote in the clear is encrypted now.
                if (!wasSealed) try { WritePfx(pfxPath, stored); } catch (IOException) { }
                var has = DnsNames(cached);
                bool covers = dnsNames is null || dnsNames.All(n => has.Contains(n, StringComparer.OrdinalIgnoreCase));
                if (cached.NotAfter > DateTime.Now.AddDays(7) && covers) return cached;
                // Replaced — near expiry, or missing a name: either way the caller must move a
                // binding to the old one along, and the new one keeps every name the old one had.
                replaced = cached.Thumbprint;
                names.AddRange(has);
                cached.Dispose();
                try { File.AppendAllText(Path.Combine(dir, PreviousFileName), replaced + Environment.NewLine); }
                catch (IOException) { /* only --tls-untrust's memory of it is lost */ }
            }
            catch
            {
                // Missing, unreadable, or corrupt — fall through and regenerate.
            }
        }

        if (dnsNames is not null) names.AddRange(dnsNames);
        var cert = Create(names.Distinct(StringComparer.OrdinalIgnoreCase).ToList());
        WritePfx(pfxPath, cert.Export(X509ContentType.Pfx));
        File.WriteAllBytes(Path.Combine(dir, CerFileName), cert.Export(X509ContentType.Cert));
        return cert;
    }

    /// <summary>The cached gateway certificate when one exists and loads, else null. Never generates
    /// and never writes — the read-only counterpart to <see cref="LoadOrCreate"/>.</summary>
    public static X509Certificate2? TryLoadCached(string? directory = null)
    {
        string dir = directory ?? DefaultDirectory;
        string pfxPath = Path.Combine(dir, PfxFileName);
        if (!File.Exists(pfxPath)) return null;
        try
        {
            return X509CertificateLoader.LoadPkcs12(
                ReadPfx(pfxPath), (string?)null, X509KeyStorageFlags.Exportable);
        }
        catch
        {
            // Missing, unreadable, or corrupt — nothing cached to return.
            return null;
        }
    }

    /// <summary>A fresh gateway certificate, not cached — no file is written.</summary>
    public static X509Certificate2 Create() => Create(new[] { "localhost" });

    /// <summary>A fresh gateway certificate naming these DNS names (and 127.0.0.1).</summary>
    public static X509Certificate2 Create(IEnumerable<string> dnsNames) =>
        SelfSignedCertificateFactory.CreateSelfSignedServerCertificate(
            "127.0.0.1", dnsNames, new[] { IPAddress.Loopback });

    /// <summary>The DNS names a certificate's Subject Alternative Name extension lists.</summary>
    public static IReadOnlyList<string> DnsNames(X509Certificate2 certificate) =>
        certificate.Extensions.OfType<X509SubjectAlternativeNameExtension>()
            .SelectMany(e => e.EnumerateDnsNames()).ToList();

    /// <summary>Install into CurrentUser\Root. Only ever called under the explicit --tls-trust flag.</summary>
    public static void Trust(X509Certificate2 certificate)
    {
        using var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);
        store.Add(certificate);
    }

    /// <summary>Every gateway certificate this tool has generated and since replaced, newest last —
    /// so --tls-untrust can remove a trusted one the cache no longer holds.</summary>
    public static IReadOnlyList<string> PreviousThumbprints(string? directory = null)
    {
        string path = Path.Combine(directory ?? DefaultDirectory, PreviousFileName);
        try
        {
            return File.Exists(path)
                ? File.ReadAllLines(path).Select(l => l.Trim()).Where(l => l.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                : Array.Empty<string>();
        }
        catch (IOException) { return Array.Empty<string>(); }
    }

    /// <summary>Remove a certificate with this thumbprint from CurrentUser\Root; true when one was
    /// removed. Only ever called under the explicit --tls-untrust flag.</summary>
    public static bool Untrust(string thumbprint)
    {
        string normalized = LoopbackTlsBinding.NormalizeThumbprint(thumbprint);
        using var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);
        var found = store.Certificates.Find(X509FindType.FindByThumbprint, normalized, validOnly: false);
        if (found.Count == 0) return false;
        store.RemoveRange(found);
        return true;
    }

    /// <summary>Whether a certificate with this thumbprint is present in CurrentUser\Root.</summary>
    public static bool IsTrusted(string thumbprint)
    {
        string normalized = LoopbackTlsBinding.NormalizeThumbprint(thumbprint);
        using var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadOnly);
        return store.Certificates.Find(X509FindType.FindByThumbprint, normalized, validOnly: false).Count > 0;
    }
}
