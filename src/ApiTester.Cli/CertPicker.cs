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
        var (hit, error, note) = Select(certs, query, issuer, serial);
        if (hit is null) throw new CliDataException(error!);
        if (note is not null) stderr.WriteLine("note: " + note);
        // One wording, shared with the desktop application's certificate row: already expired (the
        // loud case, unchanged) or expiring inside the warning window — the notice that exists so a
        // renewal ticket is raised before an outage, not after one.
        if (hit.ExpiryWarning() is { } warning) stderr.WriteLine("warning: " + warning);
        return hit;
    }

    /// <summary>The certificate the query names, or why there is none. Several that match and are
    /// all one subject from one issuer are a renewal — the old certificate stays in the store
    /// beside its replacement — so the newest is the one meant: valid now, made for client
    /// authentication, latest expiry. Several from different issuers are still a choice for the
    /// user to make.</summary>
    public static (CertificateInfo? Hit, string? Error, string? Note) Select(
        IReadOnlyList<CertificateInfo> certs, string? query, string? issuer, string? serial, DateTime? now = null)
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
            return (null, $"No client certificate matches {described}. Run 'certapi certs' to list what's available.", null);
        if (hits.Count == 1) return (hits[0], null, null);

        if (IsRenewal(hits))
        {
            var newest = Newest(hits, now ?? DateTime.Now);
            bool sameCertificate = hits.All(h => Normalize(h.Thumbprint) == Normalize(newest.Thumbprint));
            return (newest, null, sameCertificate ? null :
                $"{hits.Count} certificates match {described}, all '{newest.Subject}' from the same issuer (a renewal): " +
                $"using the newest, valid until {newest.NotAfter:yyyy-MM-dd} ({newest.Thumbprint}). Give its thumbprint to use another.");
        }
        return (null,
            $"{described} matches {hits.Count} certificates – be more specific (--cert-issuer or --cert-serial narrow it):\n" +
            string.Join("\n", hits.Select(h => $"  {h.Subject}  {h.Thumbprint}  issued by {h.Issuer}")), null);
    }

    /// <summary>One subject from one issuer: the same identity, renewed.</summary>
    internal static bool IsRenewal(IReadOnlyList<CertificateInfo> hits) =>
        hits.All(h => string.Equals(h.Subject, hits[0].Subject, StringComparison.OrdinalIgnoreCase) &&
                      string.Equals(h.Issuer, hits[0].Issuer, StringComparison.OrdinalIgnoreCase));

    /// <summary>The one of several renewals to present: valid now first, then one made for client
    /// authentication, then the latest expiry (and, between equals, the latest issued).</summary>
    internal static CertificateInfo Newest(IEnumerable<CertificateInfo> hits, DateTime now) =>
        hits.OrderByDescending(h => !h.IsExpired(now))
            .ThenByDescending(h => h.HasClientAuthEku)
            .ThenByDescending(h => h.NotAfter)
            .ThenByDescending(h => h.NotBefore)
            .First();

    // Intentionally broader than whitespace-stripping: thumbprints are hex, so dropping every
    // non-alphanumeric also forgives colon- or dash-separated forms pasted from other tools.
    private static string Normalize(string s) =>
        new(s.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
}
