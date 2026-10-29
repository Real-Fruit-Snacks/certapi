using System.Security.Cryptography.X509Certificates;
using ApiTester.Core;

namespace ApiTester.Cli;

/// <summary>Resolves the client certificate for a command from either the Windows store
/// (<c>--cert</c> / <c>--store</c>) or a file (<c>--cert-file</c> / <c>--cert-password</c> /
/// <c>--key-file</c>). Consumes those options from <paramref name="args"/>; returns null when none given.</summary>
public static class CliCert
{
    /// <summary>The shared help lines documenting the certificate options.</summary>
    public const string HelpLines = """
          --cert <thumb|subject>  Client certificate from the Windows store
          --cert-issuer <substr>  Narrow --cert to certificates issued by a matching CA (or use alone)
          --cert-serial <hex>     Pick the certificate with this serial number (or use alone)
          --store <location>      CurrentUser (default); LocalMachine searches both stores
          --cert-file <path>      Client certificate from a file (.pfx/.p12 or .pem/.crt) instead
          --cert-password <pw>    Password for a .pfx/.p12 certificate file
          --key-file <path>       Private-key file for a PEM certificate whose key is separate
          --cert-pin <pin>        Smart-card / HSM PIN for the key, supplied ahead of time so a
                                  headless job is not stopped by the provider's prompt. Prefer the
                                  CERTAPI_CERT_PIN environment variable, which keeps it out of the
                                  command line and the shell history. Windows (CNG) only
        """;

    /// <summary>Resolve the certificate. The caller passes the already-parsed <paramref name="store"/>
    /// (from <c>--store</c>) since some commands also need it; this consumes only the cert options.</summary>
    public static X509Certificate2? Resolve(Args args, string store, CliServices services, TextWriter stderr) =>
        Resolve(args, store, services, stderr, Environment.GetEnvironmentVariable);

    /// <param name="environment">Where CERTAPI_CERT_PIN is read from; injectable so a test never
    /// touches the process environment.</param>
    public static X509Certificate2? Resolve(Args args, string store, CliServices services, TextWriter stderr,
        Func<string, string?> environment)
    {
        // The precedence rule for the whole product, written as the null-coalescing chain it is:
        // an explicitly typed flag is non-null and wins; otherwise the configuration profile
        // supplies it; otherwise the built-in default stands. There is no separate precedence
        // engine, and adding one would be the thing that made this hard to reason about.
        var profile = services.Profile;
        string? certQueryFlag = args.Value("--cert");
        string? certFileFlag = args.Value("--cert-file");
        string? certIssuer = args.Value("--cert-issuer");
        string? certSerial = args.Value("--cert-serial");
        // A filter alone is a store selection too: "the one the corporate CA issued" needs no subject.
        if (certQueryFlag is null && certFileFlag is null && (certIssuer is not null || certSerial is not null))
            certQueryFlag = "";

        // Naming one source on the command line is a choice of source, so the profile's OTHER
        // source must not be applied on top of it — otherwise a profile with a store certificate
        // would make `--cert-file` fail as "mutually exclusive" against a value the user never
        // typed. Only when neither is typed does the profile decide which source is used.
        string? certQuery = certQueryFlag ?? (certFileFlag is null ? profile?.Cert : null);
        string? certFile = certFileFlag ?? (certQueryFlag is null ? profile?.CertFile : null);
        string? certPassword = args.Value("--cert-password") ?? profile?.CertPassword;
        string? keyFile = args.Value("--key-file") ?? profile?.KeyFile;
        string? pin = args.Value("--cert-pin") ?? environment(SmartCardPin.EnvironmentVariable);

        // Validate --store unconditionally (a bad value is a usage error whether or not it's used).
        bool localMachine = store.Equals("LocalMachine", StringComparison.OrdinalIgnoreCase);
        if (!localMachine && !store.Equals("CurrentUser", StringComparison.OrdinalIgnoreCase))
            throw new CliUsageException("--store must be CurrentUser or LocalMachine.");

        // Reachable two ways now: both typed on the line, or a profile that sets both with neither
        // typed. Both are the same mistake and get the same message.
        if (certFile is not null && certQuery is not null)
            throw new CliUsageException("--cert and --cert-file are mutually exclusive.");
        if (certFile is not null && (certIssuer is not null || certSerial is not null))
            throw new CliUsageException("--cert-issuer and --cert-serial select from the Windows store; they do not apply to --cert-file.");

        X509Certificate2? cert;
        if (certFile is not null)
        {
            try { cert = services.Own(CertificateFileLoader.Load(certFile, certPassword, keyFile)); }
            catch (CertificateFileException ex) { throw new CliDataException(ex.Message); }
            NoteMissingIntermediates(certFile, cert.Thumbprint!, stderr);
        }
        else if (certQuery is null) return null;
        else cert = CertPicker.Resolve(services.ListCertificates(localMachine),
            certQuery.Length == 0 ? null : certQuery, certIssuer, certSerial, stderr).Certificate;

        var unlocked = WithPin(cert, pin, stderr);
        // A PIN makes a copy bound to the unlocked key; it is this invocation's too.
        if (!ReferenceEquals(unlocked, cert)) services.Own(unlocked);
        return unlocked;
    }

    /// <summary>A PEM that ships its intermediates is a trap: the TLS stack builds the client chain
    /// from the Windows stores, so an intermediate that lives only in the file never reaches the
    /// server, and the server refuses a chain it cannot build. Said once, with the command that
    /// fixes it, rather than left to be diagnosed from the far end.</summary>
    internal static void NoteMissingIntermediates(string certFile, string leafThumbprint, TextWriter stderr)
    {
        var intermediates = CertificateFileLoader.LoadIntermediates(certFile, leafThumbprint);
        if (intermediates.Count == 0) return;
        var missing = CertificateFileLoader.MissingFromStore(intermediates);
        foreach (var c in intermediates) if (!missing.Contains(c)) c.Dispose();
        if (missing.Count == 0) return;
        stderr.WriteLine($"note: {Path.GetFileName(certFile)} carries {missing.Count} intermediate certificate(s) that are not in the Windows CA store, " +
                         "so the server will not receive them with the client certificate:");
        foreach (var c in missing) stderr.WriteLine($"      {c.Subject}");
        stderr.WriteLine("      If the server refuses the chain, import them once: " +
                         $"certutil -user -addstore CA \"{certFile}\"");
        foreach (var c in missing) c.Dispose();
    }

    /// <summary>Unlock a smart-card key with the PIN when one was supplied. The PIN itself never
    /// reaches stderr; only what became of it does.</summary>
    internal static X509Certificate2? WithPin(X509Certificate2? cert, string? pin, TextWriter stderr)
    {
        if (cert is null || string.IsNullOrEmpty(pin)) return cert;
        var unlocked = SmartCardPin.Apply(cert, pin, out var note);
        if (note is not null) stderr.WriteLine("note: " + note);
        return unlocked;
    }
}
