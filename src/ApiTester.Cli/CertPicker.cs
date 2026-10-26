using ApiTester.Core;

namespace ApiTester.Cli;

public static class CertPicker
{
    public static CertificateInfo Resolve(IReadOnlyList<CertificateInfo> certs, string query, TextWriter stderr) =>
        Resolve(certs, query, issuer: null, serial: null, stderr);

    /// <param name="query">Thumbprint or subject substring (`--cert`); null when only the filters
    /// below select. At least one of the three must be given.</param>
    /// <param name="issuer">`--cert-issuer`: a substring of the issuer's distinguished name, AND-ed
    /// with the query. The filter for a subject that several CAs have issued to — the renewed
    /// certificate and the old one, or the corporate CA and the test CA.</param>
    /// <param name="serial">`--cert-serial`: the serial number in hex (separators and case
    /// ignored). Exact, and survives a renewal's new thumbprint when the serial is what the
    /// server's allow list names.</param>
    public static CertificateInfo Resolve(IReadOnlyList<CertificateInfo> certs, string? query, string? issuer, string? serial, TextWriter stderr)
    {
        IEnumerable<CertificateInfo> candidates = certs;
        if (query is not null)
        {
            string normalized = Normalize(query);
            var byThumb = certs.Where(c => Normalize(c.Thumbprint) == normalized).ToList();
            candidates = byThumb.Count > 0
                ? byThumb
                : certs.Where(c => c.Subject.Contains(query, StringComparison.OrdinalIgnoreCase));
        }
        if (issuer is not null)
            candidates = candidates.Where(c => c.Issuer.Contains(issuer, StringComparison.OrdinalIgnoreCase));
        if (serial is not null)
        {
            string wanted = Normalize(serial).TrimStart('0');
            candidates = candidates.Where(c => Normalize(c.Certificate.SerialNumber).TrimStart('0') == wanted);
        }
        var hits = candidates.ToList();

        string described = string.Join(" ", new[]
        {
            query is null ? null : $"'{query}'",
            issuer is null ? null : $"issuer '{issuer}'",
            serial is null ? null : $"serial {serial}"
        }.Where(s => s is not null));
        if (hits.Count == 0)
            throw new CliDataException(
                $"No client certificate matches {described}. Run 'certapi certs' to list what's available.");
        if (hits.Count > 1)
            throw new CliDataException(
                $"{described} matches {hits.Count} certificates – be more specific (--cert-issuer or --cert-serial narrow it):\n" +
                string.Join("\n", hits.Select(h => $"  {h.Subject}  {h.Thumbprint}  issued by {h.Issuer}")));

        var hit = hits[0];
        // One wording, shared with the desktop application's certificate row: already expired (the
        // loud case, unchanged) or expiring inside the warning window — the notice that exists so a
        // renewal ticket is raised before an outage, not after one.
        if (hit.ExpiryWarning() is { } warning) stderr.WriteLine("warning: " + warning);
        return hit;
    }

    // Intentionally broader than whitespace-stripping: thumbprints are hex, so dropping every
    // non-alphanumeric also forgives colon- or dash-separated forms pasted from other tools.
    private static string Normalize(string s) =>
        new(s.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
}
