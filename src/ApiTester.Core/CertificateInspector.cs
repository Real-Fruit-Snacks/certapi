using System.Formats.Asn1;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace ApiTester.Core;

/// <summary>Full X.509 certificate inspection model (the Windows-native answer to
/// <c>openssl x509 -text</c>, including CNG key provider / hardware backing detection).</summary>
public sealed record CertificateDetails(
    string Subject,
    string Issuer,
    string ThumbprintSha1,
    string ThumbprintSha256,
    string SerialNumber,
    DateTime NotBeforeUtc,
    DateTime NotAfterUtc,
    bool IsExpired,
    int DaysRemaining,
    bool HasPrivateKey,
    string KeyAlgorithm,
    int KeySizeBits,
    string? KeyProvider,
    bool IsHardwareBacked,
    IReadOnlyList<string> SubjectAlternativeNames,
    IReadOnlyList<string> EnhancedKeyUsages,
    IReadOnlyList<string> KeyUsages,
    bool HasClientAuthEku,
    bool ChainValid,
    IReadOnlyList<string> ChainStatus,
    IReadOnlyList<string> ChainSubjects);

/// <summary>Inspects X.509 certificates and exports PEM blocks without external tools.</summary>
public static class CertificateInspector
{
    private const string ClientAuthOid = "1.3.6.1.5.5.7.3.2";
    private const string SanOid = "2.5.29.17";

    public static CertificateDetails Inspect(X509Certificate2 cert, bool checkRevocation = false, DateTime? utcNow = null)
    {
        var now = utcNow ?? DateTime.UtcNow;
        var notBefore = cert.NotBefore.ToUniversalTime();
        var notAfter = cert.NotAfter.ToUniversalTime();
        bool expired = notAfter <= now;
        int daysRemaining = (int)Math.Floor((notAfter - now).TotalDays);

        string sha256 = cert.GetCertHashString(HashAlgorithmName.SHA256);
        var (alg, bits) = ReadPublicKeyInfo(cert);
        string? provider = ReadKeyProvider(cert);
        bool hardware = IsHardwareProvider(provider);

        var sans = ReadSans(cert);
        var ekus = new List<string>();
        bool hasClientAuth = false;
        var keyUsages = new List<string>();

        foreach (var ext in cert.Extensions)
        {
            if (ext is X509EnhancedKeyUsageExtension ekuExt)
            {
                foreach (var oid in ekuExt.EnhancedKeyUsages)
                {
                    if (oid.Value == ClientAuthOid) hasClientAuth = true;
                    string label = string.IsNullOrEmpty(oid.FriendlyName)
                        ? (oid.Value ?? "")
                        : $"{oid.FriendlyName} ({oid.Value})";
                    ekus.Add(label);
                }
            }
            else if (ext is X509KeyUsageExtension kuExt)
            {
                foreach (X509KeyUsageFlags flag in Enum.GetValues<X509KeyUsageFlags>())
                {
                    if (flag != X509KeyUsageFlags.None && kuExt.KeyUsages.HasFlag(flag))
                        keyUsages.Add(flag.ToString());
                }
            }
        }

        bool chainValid = false;
        var chainStatus = new List<string>();
        var chainSubjects = new List<string>();
        try
        {
            using var chain = new X509Chain();
            chain.ChainPolicy.RevocationMode = checkRevocation ? X509RevocationMode.Online : X509RevocationMode.NoCheck;
            chain.ChainPolicy.VerificationTime = now.ToLocalTime();
            chainValid = chain.Build(cert);
            foreach (var elem in chain.ChainElements)
                chainSubjects.Add(elem.Certificate.Subject);
            foreach (var st in chain.ChainStatus)
                chainStatus.Add($"{st.Status}: {st.StatusInformation?.Trim()}");
        }
        catch (CryptographicException ex)
        {
            chainStatus.Add($"ChainBuildError: {ex.Message}");
        }

        return new CertificateDetails(
            Subject: cert.Subject,
            Issuer: cert.Issuer,
            ThumbprintSha1: cert.Thumbprint,
            ThumbprintSha256: sha256,
            SerialNumber: cert.SerialNumber,
            NotBeforeUtc: notBefore,
            NotAfterUtc: notAfter,
            IsExpired: expired,
            DaysRemaining: daysRemaining,
            HasPrivateKey: cert.HasPrivateKey,
            KeyAlgorithm: alg,
            KeySizeBits: bits,
            KeyProvider: provider,
            IsHardwareBacked: hardware,
            SubjectAlternativeNames: sans,
            EnhancedKeyUsages: ekus,
            KeyUsages: keyUsages,
            HasClientAuthEku: hasClientAuth,
            ChainValid: chainValid,
            ChainStatus: chainStatus,
            ChainSubjects: chainSubjects);
    }

    public static string ExportPem(X509Certificate2 cert) => cert.ExportCertificatePem();

    public static string ExportChainPem(IEnumerable<X509Certificate2> certs)
    {
        var sb = new StringBuilder();
        foreach (var c in certs)
        {
            sb.AppendLine(c.ExportCertificatePem().TrimEnd());
        }
        return sb.ToString();
    }

    public static string FormatText(CertificateDetails d)
    {
        var sb = new StringBuilder();
        sb.Append("Subject:      ").AppendLine(d.Subject);
        sb.Append("Issuer:       ").AppendLine(d.Issuer);
        sb.Append("Serial:       ").AppendLine(d.SerialNumber);
        sb.Append("SHA-1:        ").AppendLine(d.ThumbprintSha1);
        sb.Append("SHA-256:      ").AppendLine(d.ThumbprintSha256);
        string validity = d.IsExpired
            ? $"EXPIRED ({-d.DaysRemaining} day(s) ago)"
            : $"{d.DaysRemaining} day(s) remaining";
        sb.Append("Valid:        ").Append(d.NotBeforeUtc.ToString("yyyy-MM-dd HH:mm 'UTC'"))
          .Append("  ->  ").Append(d.NotAfterUtc.ToString("yyyy-MM-dd HH:mm 'UTC'"))
          .Append("  [").Append(validity).AppendLine("]");
        sb.Append("Key:          ").Append(d.KeyAlgorithm);
        if (d.KeySizeBits > 0) sb.Append(' ').Append(d.KeySizeBits).Append("-bit");
        sb.Append(d.HasPrivateKey ? "  (private key present)" : "  (public only)");
        sb.AppendLine();
        if (d.KeyProvider is not null)
        {
            sb.Append("Key Provider: ").Append(d.KeyProvider);
            if (d.IsHardwareBacked) sb.Append("  [hardware/smart-card]");
            sb.AppendLine();
        }
        if (d.SubjectAlternativeNames.Count > 0)
            sb.Append("SANs:         ").AppendLine(string.Join(", ", d.SubjectAlternativeNames));
        if (d.KeyUsages.Count > 0)
            sb.Append("Key Usage:    ").AppendLine(string.Join(", ", d.KeyUsages));
        if (d.EnhancedKeyUsages.Count > 0)
            sb.Append("EKUs:         ").AppendLine(string.Join(", ", d.EnhancedKeyUsages));
        sb.Append("Chain:        ").AppendLine(d.ChainValid ? "valid" : "issues detected");
        for (int i = 0; i < d.ChainSubjects.Count; i++)
            sb.Append("  [").Append(i).Append("] ").AppendLine(d.ChainSubjects[i]);
        foreach (var st in d.ChainStatus)
            sb.Append("  ! ").AppendLine(st);
        return sb.ToString();
    }

    private static (string Alg, int Bits) ReadPublicKeyInfo(X509Certificate2 cert)
    {
        try
        {
            using var rsa = cert.GetRSAPublicKey();
            if (rsa is not null) return ("RSA", rsa.KeySize);
        }
        catch { }
        try
        {
            using var ecdsa = cert.GetECDsaPublicKey();
            if (ecdsa is not null) return ("ECDSA", ecdsa.KeySize);
        }
        catch { }
        try
        {
            using var dsa = cert.GetDSAPublicKey();
            if (dsa is not null) return ("DSA", dsa.KeySize);
        }
        catch { }
        return (cert.PublicKey.Oid.FriendlyName ?? cert.PublicKey.Oid.Value ?? "Unknown", 0);
    }

    private static string? ReadKeyProvider(X509Certificate2 cert)
    {
        if (!cert.HasPrivateKey || !OperatingSystem.IsWindows()) return null;
        try
        {
            using var rsa = cert.GetRSAPrivateKey();
            if (rsa is RSACng rsaCng) return rsaCng.Key.Provider?.Provider;
            if (rsa is RSACryptoServiceProvider rsaCsp) return rsaCsp.CspKeyContainerInfo.ProviderName;
        }
        catch { }
        try
        {
            using var ecdsa = cert.GetECDsaPrivateKey();
            if (ecdsa is ECDsaCng ecCng) return ecCng.Key.Provider?.Provider;
        }
        catch { }
        return null;
    }

    internal static bool IsHardwareProvider(string? provider)
    {
        if (string.IsNullOrWhiteSpace(provider)) return false;
        return provider.Contains("Smart Card", StringComparison.OrdinalIgnoreCase) ||
               provider.Contains("Platform Crypto", StringComparison.OrdinalIgnoreCase) ||
               provider.Contains("TPM", StringComparison.OrdinalIgnoreCase) ||
               provider.Contains("HSM", StringComparison.OrdinalIgnoreCase) ||
               provider.Contains("Yubi", StringComparison.OrdinalIgnoreCase) ||
               provider.Contains("Hardware", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Decode Subject Alternative Name (OID 2.5.29.17) entries using ASN.1 DER parsing so
    /// the result is deterministic and independent of OS locale formatting.</summary>
    internal static List<string> ReadSans(X509Certificate2 cert)
    {
        var result = new List<string>();
        var ext = cert.Extensions[SanOid];
        if (ext is null) return result;
        try
        {
            var reader = new AsnReader(ext.RawData, AsnEncodingRules.DER);
            var seq = reader.ReadSequence();
            while (seq.HasData)
            {
                var tag = seq.PeekTag();
                if (tag.TagClass != TagClass.ContextSpecific)
                {
                    seq.ReadEncodedValue();
                    continue;
                }
                switch (tag.TagValue)
                {
                    case 1: // rfc822Name
                        result.Add("Email:" + seq.ReadCharacterString(UniversalTagNumber.IA5String, tag));
                        break;
                    case 2: // dNSName
                        result.Add("DNS:" + seq.ReadCharacterString(UniversalTagNumber.IA5String, tag));
                        break;
                    case 6: // uniformResourceIdentifier
                        result.Add("URI:" + seq.ReadCharacterString(UniversalTagNumber.IA5String, tag));
                        break;
                    case 7: // iPAddress
                        var ipBytes = seq.ReadOctetString(tag);
                        if (ipBytes.Length is 4 or 16)
                            result.Add("IP:" + new IPAddress(ipBytes));
                        break;
                    case 0: // otherName (e.g. Microsoft UPN 1.3.6.1.4.1.311.20.2.3)
                        var other = seq.ReadSequence(tag);
                        string oid = other.ReadObjectIdentifier();
                        if (oid == "1.3.6.1.4.1.311.20.2.3" && other.HasData)
                        {
                            var valWrap = other.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0, isConstructed: true));
                            if (valWrap.HasData)
                            {
                                var innerTag = valWrap.PeekTag();
                                string upn = innerTag == new Asn1Tag(UniversalTagNumber.UTF8String)
                                    ? valWrap.ReadCharacterString(UniversalTagNumber.UTF8String)
                                    : Encoding.UTF8.GetString(valWrap.ReadEncodedValue().ToArray());
                                result.Add("UPN:" + upn);
                            }
                        }
                        break;
                    default:
                        seq.ReadEncodedValue();
                        break;
                }
            }
        }
        catch
        {
            // Fallback to OS formatted string when a custom SAN tag cannot be parsed cleanly.
            var formatted = ext.Format(multiLine: false);
            if (!string.IsNullOrWhiteSpace(formatted)) result.Add(formatted);
        }
        return result;
    }
}
