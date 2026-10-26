using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace ApiTester.Core;

/// <summary>Supplies a smart-card or HSM PIN to a certificate's private key ahead of time —
/// <c>--cert-pin</c> / <c>CERTAPI_CERT_PIN</c> — so a headless job (CI, a scheduled task, a service)
/// is not stopped by the PIN prompt the key-storage provider would otherwise show. The PIN is set as
/// the key's <c>SmartCardPin</c> property on a handle this process opens, and the certificate handed
/// to the TLS stack is re-bound to that handle, which is what makes SChannel use the cached PIN
/// instead of asking. Never stored anywhere; never logged.</summary>
public static class SmartCardPin
{
    public const string EnvironmentVariable = "CERTAPI_CERT_PIN";

    /// <summary>The certificate re-bound to a key handle that already knows the PIN, or the
    /// certificate itself with <paramref name="note"/> explaining why nothing changed (not Windows,
    /// no private key, a software key that never prompts, a provider that refuses the property).</summary>
    public static X509Certificate2 Apply(X509Certificate2 certificate, string pin, out string? note)
    {
        note = null;
        if (!OperatingSystem.IsWindows())
        {
            note = "--cert-pin only applies on Windows (CNG key-storage providers); ignored here.";
            return certificate;
        }
        if (!certificate.HasPrivateKey)
        {
            note = "--cert-pin: the certificate has no private key to unlock.";
            return certificate;
        }
        return ApplyOnWindows(certificate, pin, out note);
    }

    [SupportedOSPlatform("windows")]
    private static X509Certificate2 ApplyOnWindows(X509Certificate2 certificate, string pin, out string? note)
    {
        note = null;
        // NCRYPT_PIN_PROPERTY takes a null-terminated Unicode string.
        var property = new CngProperty("SmartCardPin", Encoding.Unicode.GetBytes(pin + "\0"), CngPropertyOptions.None);
        try
        {
            if (certificate.GetRSAPrivateKey() is RSACng rsa)
            {
                rsa.Key.SetProperty(property);
                return certificate.CopyWithPrivateKey(rsa);
            }
            if (certificate.GetECDsaPrivateKey() is ECDsaCng ecdsa)
            {
                ecdsa.Key.SetProperty(property);
                return certificate.CopyWithPrivateKey(ecdsa);
            }
            note = "--cert-pin: the key is not held by a CNG provider (a legacy CAPI key, or a software key that never prompts); ignored.";
            return certificate;
        }
        catch (CryptographicException ex)
        {
            // A provider that refuses the property (some HSM providers do) is a note, not a failure:
            // the handshake may still succeed with the provider's own prompt or cache.
            note = $"--cert-pin: the key-storage provider refused the PIN property ({ex.Message}); it may prompt instead.";
            return certificate;
        }
        catch (ArgumentException ex)
        {
            note = $"--cert-pin: could not re-bind the certificate to the unlocked key ({ex.Message}).";
            return certificate;
        }
    }
}
