using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace ApiTester.Core;

/// <summary>Thrown when a client certificate file cannot be loaded (missing, wrong password,
/// no private key, or malformed).</summary>
public sealed class CertificateFileException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Loads a client certificate (with its private key) from a file — a PKCS#12 <c>.pfx</c>/<c>.p12</c>
/// or a PEM <c>.pem</c>/<c>.crt</c> — for endpoints whose certificate isn't in the Windows store.</summary>
public static class CertificateFileLoader
{
    /// <summary>Load a usable client certificate from <paramref name="certPath"/>. For PEM inputs the
    /// private key may be in the same file or in a separate <paramref name="keyPath"/>. The result is
    /// round-tripped through PKCS#12 so SslStream/SChannel can use the key on Windows.</summary>
    public static X509Certificate2 Load(string certPath, string? password = null, string? keyPath = null)
    {
        if (string.IsNullOrWhiteSpace(certPath) || !File.Exists(certPath))
            throw new CertificateFileException($"Certificate file not found: {certPath}");
        if (keyPath is { Length: > 0 } && !File.Exists(keyPath))
            throw new CertificateFileException($"Key file not found: {keyPath}");

        string ext = Path.GetExtension(certPath).ToLowerInvariant();
        // Decided by content, not only by extension: a DER-encoded .crt, or a .pem that is really a
        // PKCS#12, each gets the loader that can read it rather than a misleading PEM error.
        bool looksPem = LooksLikePem(certPath);
        bool pkcs12 = ext is ".pfx" or ".p12" || (!looksPem && LooksLikePkcs12(certPath));
        if (pkcs12 && keyPath is { Length: > 0 })
            throw new CertificateFileException(
                $"'{Path.GetFileName(certPath)}' is a PKCS#12 file and carries its own key; --key-file does not apply to it.");

        X509Certificate2 result;
        try
        {
            if (pkcs12)
                result = X509CertificateLoader.LoadPkcs12FromFile(certPath, password, X509KeyStorageFlags.Exportable);
            else
            {
                // PEM / CRT: the cert plus its key (in the same file, or a separate key file). CreateFromPem
                // yields an ephemeral key SChannel can't use, so re-import through PKCS#12 (Exportable).
                // An "ENCRYPTED PRIVATE KEY" block takes the password the same flag supplies for a .pfx.
                string keySource = keyPath is { Length: > 0 } ? keyPath : certPath;
                bool encryptedKey = File.ReadAllText(keySource).Contains("ENCRYPTED PRIVATE KEY", StringComparison.Ordinal);
                if (encryptedKey && password is null)
                    throw new CertificateFileException(
                        $"The private key in '{Path.GetFileName(keySource)}' is encrypted; pass its password with --cert-password.");
                using var pem = encryptedKey
                    ? X509Certificate2.CreateFromEncryptedPemFile(certPath, password, keyPath is { Length: > 0 } ? keyPath : null)
                    : keyPath is { Length: > 0 }
                        ? X509Certificate2.CreateFromPemFile(certPath, keyPath)
                        : X509Certificate2.CreateFromPemFile(certPath);
                result = X509CertificateLoader.LoadPkcs12(
                    pem.Export(X509ContentType.Pkcs12), (string?)null, X509KeyStorageFlags.Exportable);
            }
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException)
        {
            throw new CertificateFileException(Hint(ext, certPath, password, ex.Message), ex);
        }

        // A client certificate is useless without its private key — fail clearly rather than
        // presenting a key-less certificate that the handshake would silently reject.
        if (!result.HasPrivateKey)
        {
            result.Dispose();
            throw new CertificateFileException(Hint(ext, certPath, password,
                "the file has no usable private key"));
        }
        return result;
    }

    /// <summary>The certificates in a PEM file other than the leaf — the intermediates a CA bundle
    /// ships alongside it. SChannel builds the client chain from the Windows stores, not from the
    /// file, so an intermediate that is only in the file is one the server never receives; this is
    /// what lets a caller say so, and say which store to put it in.</summary>
    /// <param name="leafThumbprint">The certificate <see cref="Load"/> chose — the one the private
    /// key belongs to. Everything else in the file is an intermediate (or the root, which the store
    /// needs too), however the file is ordered.</param>
    public static IReadOnlyList<X509Certificate2> LoadIntermediates(string certPath, string leafThumbprint)
    {
        if (!File.Exists(certPath) || !LooksLikePem(certPath)) return Array.Empty<X509Certificate2>();
        var all = new X509Certificate2Collection();
        try { all.ImportFromPemFile(certPath); }
        catch (CryptographicException) { return Array.Empty<X509Certificate2>(); }
        var extras = new List<X509Certificate2>();
        foreach (var c in all)
        {
            if (c.Thumbprint.Equals(leafThumbprint, StringComparison.OrdinalIgnoreCase)) c.Dispose();
            else extras.Add(c);
        }
        return extras;
    }

    /// <summary>Which of <paramref name="intermediates"/> the machine's stores do not already hold,
    /// so a note names only the ones that matter. On a platform with no CA store every one is
    /// reported.</summary>
    public static IReadOnlyList<X509Certificate2> MissingFromStore(IReadOnlyList<X509Certificate2> intermediates)
    {
        if (intermediates.Count == 0) return intermediates;
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, location) in new[]
                 {
                     (StoreName.CertificateAuthority, StoreLocation.CurrentUser), (StoreName.CertificateAuthority, StoreLocation.LocalMachine),
                     (StoreName.Root, StoreLocation.CurrentUser), (StoreName.Root, StoreLocation.LocalMachine)
                 })
        {
            try
            {
                using var store = new X509Store(name, location);
                store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
                foreach (var c in store.Certificates) known.Add(c.Thumbprint);
            }
            catch (CryptographicException) { }
            catch (PlatformNotSupportedException) { }
        }
        return intermediates.Where(c => !known.Contains(c.Thumbprint)).ToList();
    }

    private static bool LooksLikePem(string path)
    {
        try
        {
            using var reader = new StreamReader(path);
            var head = new char[4096];
            int n = reader.Read(head, 0, head.Length);
            return new string(head, 0, n).Contains("-----BEGIN", StringComparison.Ordinal);
        }
        catch (IOException) { return false; }
    }

    /// <summary>A PKCS#12 file is a DER SEQUENCE whose first element is the INTEGER version 3.</summary>
    private static bool LooksLikePkcs12(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            Span<byte> head = stackalloc byte[8];
            int n = stream.Read(head);
            if (n < 4 || head[0] != 0x30) return false;
            int lengthBytes = (head[1] & 0x80) != 0 ? head[1] & 0x7F : 0;
            int i = 2 + lengthBytes;
            return i + 2 < n && head[i] == 0x02 && head[i + 1] == 0x01 && head[i + 2] == 0x03;
        }
        catch (IOException) { return false; }
    }

    private static string Hint(string ext, string certPath, string? password, string message)
    {
        string baseMsg = $"Could not load certificate '{Path.GetFileName(certPath)}': {message}";
        return ext is ".pfx" or ".p12"
            ? baseMsg + (password is null ? " (a password may be required — pass one)" : "")
            : baseMsg + ". A client certificate needs its private key — use a .pfx, a PEM containing the key, or a separate key file.";
    }
}
