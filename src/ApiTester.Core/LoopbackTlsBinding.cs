using System.Diagnostics;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;

namespace ApiTester.Core;

/// <summary>A certificate binding on a loopback port, as `netsh http show sslcert` reports it.</summary>
public sealed record BoundCertificate(string Thumbprint, string? AppId);

/// <summary>A binding certapi made: the thumbprint bound and the exact command that was run.</summary>
public sealed record BindResult(string Thumbprint, string Command);

/// <summary>Binds a TLS certificate to a loopback port in the Windows HTTP Server API (the same
/// mechanism `netsh http add sslcert` configures), so `serve --tls` can listen on
/// `https://127.0.0.1:&lt;port&gt;/`. Binding is machine state that requires elevation, so it is
/// handled explicitly rather than assumed: callers construct the exact command up front (testable
/// without a process), and only <see cref="Bind"/>/<see cref="Unbind"/>/<see cref="Describe"/>
/// actually touch the machine.</summary>
public static class LoopbackTlsBinding
{
    /// <summary>The application id certapi registers its bindings under, so a binding it made is
    /// recognizable in `netsh http show sslcert` output.</summary>
    public const string AppId = "{0e5f0a4f-9d3a-4a3f-9c2d-6a1e7f3b52c8}";

    // --- pure: string in, string out; no process is started, nothing changes ---

    public static string BuildShowCommand(int port) => $"netsh {ShowArgs(port)}";

    public static string BuildAddCommand(int port, string thumbprint) => $"netsh {AddArgs(port, thumbprint)}";

    public static string BuildDeleteCommand(int port) => $"netsh {DeleteArgs(port)}";

    private static string ShowArgs(int port) => $"http show sslcert ipport=127.0.0.1:{port}";

    private static string AddArgs(int port, string thumbprint) =>
        $"http add sslcert ipport=127.0.0.1:{port} certhash={NormalizeThumbprint(thumbprint)} appid={AppId} certstorename=MY";

    private static string DeleteArgs(int port) => $"http delete sslcert ipport=127.0.0.1:{port}";

    /// <summary>The binding described by `netsh http show sslcert` output, or null when the output
    /// describes no binding. The label match ("Certificate Hash", "Application ID") assumes netsh's
    /// English output; a localized system simply reports "no binding" instead, which is the safe
    /// direction — it never mistakenly claims a port is free.</summary>
    public static BoundCertificate? ParseShowOutput(string output)
    {
        string? thumbprint = null;
        string? appId = null;

        foreach (var rawLine in output.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r');
            int colon = line.IndexOf(':');
            if (colon < 0) continue;
            string label = line[..colon].Trim();
            string value = line[(colon + 1)..].Trim();
            if (thumbprint is null && label.Equals("Certificate Hash", StringComparison.OrdinalIgnoreCase))
                thumbprint = NormalizeThumbprint(value);
            else if (appId is null && label.Equals("Application ID", StringComparison.OrdinalIgnoreCase))
                appId = value;
        }

        // netsh translates its labels (German: "Zertifikathash", "Anwendungs-ID"), so when the
        // English ones are not there the values are recognised by shape instead: the only
        // 40-hex-digit value is the certificate hash, the only {GUID} the application id.
        if (thumbprint is null || appId is null)
        {
            foreach (var rawLine in output.Split('\n'))
            {
                int colon = rawLine.IndexOf(" : ", StringComparison.Ordinal);
                string value = (colon >= 0 ? rawLine[(colon + 3)..] : rawLine).Trim();
                if (thumbprint is null && System.Text.RegularExpressions.Regex.IsMatch(value, "^[0-9A-Fa-f]{40}$"))
                    thumbprint = NormalizeThumbprint(value);
                else if (appId is null && System.Text.RegularExpressions.Regex.IsMatch(value, "^\\{[0-9A-Fa-f-]{36}\\}$"))
                    appId = value;
            }
        }

        return thumbprint is null ? null : new BoundCertificate(thumbprint, appId);
    }

    /// <summary>What `serve --tls` prints before exiting 2 when the binding needs elevation.</summary>
    public static string NotElevatedMessage(int port, string thumbprint) =>
        $"""
        Serving over HTTPS needs a certificate bound to 127.0.0.1:{port} in the Windows HTTP Server API,
        which requires an elevated (Run as administrator) prompt. Run this command from an elevated shell:

          {BuildAddCommand(port, thumbprint)}

        The certificate must be in the LocalMachine\My store for that command to find it.
        To undo the binding later, run:

          {BuildDeleteCommand(port)}
        """;

    /// <summary>What `serve --tls` prints before exiting 2 when the port already carries someone
    /// else's binding.</summary>
    public static string AlreadyBoundMessage(int port, BoundCertificate bound) =>
        $"""
        127.0.0.1:{port} already has a certificate bound to it (thumbprint "{bound.Thumbprint}"),
        and certapi did not create it — so it was left alone rather than clobbered.
        To remove it yourself, run:

          {BuildDeleteCommand(port)}
        """;

    /// <summary>Thumbprints as netsh wants them: uppercase hex, no spaces.</summary>
    public static string NormalizeThumbprint(string thumbprint)
    {
        Span<char> buffer = stackalloc char[thumbprint.Length];
        int n = 0;
        foreach (char c in thumbprint)
            if (!char.IsWhiteSpace(c)) buffer[n++] = c;
        return new string(buffer[..n]).ToUpperInvariant();
    }

    // --- machine state: NEVER called by a test ---

    public static bool IsElevated()
    {
        // The HTTP Server API binding only exists on Windows; elsewhere there is nothing to elevate for.
        if (!OperatingSystem.IsWindows()) return false;
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>Runs `netsh http show sslcert` for this port and parses the result; any exception
    /// (netsh missing, access denied, etc.) is treated as "nothing to report" rather than thrown.</summary>
    public static BoundCertificate? Describe(int port)
    {
        try
        {
            var (_, stdout, _) = RunNetsh(ShowArgs(port));
            return ParseShowOutput(stdout);
        }
        catch
        {
            return null;
        }
    }

    public static bool IsBound(int port) => Describe(port) is not null;

    private static bool HasUsableKey(X509Certificate2 certificate)
    {
        if (!certificate.HasPrivateKey) return false;
        try
        {
            using var rsa = certificate.GetRSAPrivateKey();
            using var ecdsa = rsa is null ? certificate.GetECDsaPrivateKey() : null;
            return rsa is not null || ecdsa is not null;
        }
        catch (System.Security.Cryptography.CryptographicException) { return false; }
    }

    /// <summary>Bind a generated certificate to the port (requires elevation). Returns the
    /// certificate's thumbprint and the exact command that was run, for the log.</summary>
    public static BindResult Bind(int port, X509Certificate2 certificate)
    {
        string thumbprint = NormalizeThumbprint(certificate.Thumbprint!);
        if (!IsElevated())
            throw new InvalidOperationException(NotElevatedMessage(port, thumbprint));

        // netsh's certstorename=MY resolves against the machine store, so the certificate must be
        // installed there first (unless it already is).
        using (var store = new X509Store(StoreName.My, StoreLocation.LocalMachine))
        {
            store.Open(OpenFlags.ReadWrite);
            var existing = store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, validOnly: false);
            // An entry an earlier version added points at a key that went with that process: it is
            // replaced, or the binding fails exactly as before.
            if (existing.Count > 0 && !existing.Cast<X509Certificate2>().Any(HasUsableKey))
            {
                store.RemoveRange(existing);
                existing = new X509Certificate2Collection();
            }
            if (existing.Count == 0)
            {
                // HTTP.sys runs as SYSTEM and reads the key from the machine's key store. The cached
                // certificate's key lives in a temporary per-user container that goes when this
                // process does, so it is stored again with its key persisted for the machine.
                X509Certificate2? persisted = null;
                try
                {
                    persisted = X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pkcs12), (string?)null,
                        X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.PersistKeySet);
                }
                catch (System.Security.Cryptography.CryptographicException) { /* not exportable: add it as it is */ }
                using (persisted) store.Add(persisted ?? certificate);
            }
        }

        string command = BuildAddCommand(port, thumbprint);
        var (exitCode, stdout, stderr) = RunNetsh(AddArgs(port, thumbprint));
        if (exitCode != 0)
            throw new InvalidOperationException($"'{command}' failed:{Environment.NewLine}{stdout}{stderr}");

        return new BindResult(thumbprint, command);
    }

    public static void Unbind(int port)
    {
        // Ignore a non-zero exit — there may be nothing to delete.
        RunNetsh(DeleteArgs(port));
    }

    private static (int ExitCode, string Stdout, string Stderr) RunNetsh(string arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "netsh",
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            }
        };
        process.Start();
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, stdout, stderr);
    }
}
