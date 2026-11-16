using System.Security.Cryptography.X509Certificates;
using ApiTester.Core;

namespace ApiTester.Cli;

/// <summary>Keeps a running gateway's client certificates current. Every few minutes it chooses
/// each certificate again the way the start did — the store query, or the file when it changed —
/// and when that finds a renewal (another certificate, valid now, lasting longer) the gateway
/// presents it from the next connection on, with no restart. It also says, ahead of time and
/// again while it lasts, when a certificate the gateway presents is about to expire or has.</summary>
internal sealed class ServeCertificateWatch
{
    /// <summary>How often a warning about one certificate is repeated: daily while it is about to
    /// expire, hourly once it has.</summary>
    internal static readonly TimeSpan SoonRepeat = TimeSpan.FromDays(1), ExpiredRepeat = TimeSpan.FromHours(1);

    private sealed class Entry(X509Certificate2 original, CertificateOrigin origin, DateTime? fileSeen)
    {
        public X509Certificate2 Original { get; } = original;
        public CertificateOrigin Origin { get; } = origin;
        public DateTime? FileSeen = fileSeen;
        public DateTime? Warned;
        public string? WarnedFor;
        public DateTime? ProblemSaid;
    }

    private readonly MtlsGateway _gateway;
    private readonly CliServices _services;
    private readonly Action<string> _log;
    private readonly Func<DateTime> _now;
    private readonly TimeSpan _interval;
    private readonly List<Entry> _entries = new();

    /// <param name="watched">Each certificate the gateway was given, with how it was chosen. One
    /// certificate given to several sites is watched once.</param>
    /// <param name="startupWarned">True when the start already printed each store certificate's
    /// expiry warning, so the first repeat is a day (or an hour) away rather than now.</param>
    public ServeCertificateWatch(MtlsGateway gateway, IEnumerable<(X509Certificate2 Certificate, CertificateOrigin Origin)> watched,
        CliServices services, Action<string> log, TimeSpan interval, Func<DateTime>? now = null, bool startupWarned = true)
    {
        _gateway = gateway;
        _services = services;
        _log = log;
        _interval = interval;
        _now = now ?? (() => DateTime.Now);
        foreach (var (cert, origin) in watched)
        {
            if (_entries.Any(e => e.Original.Thumbprint.Equals(cert.Thumbprint, StringComparison.OrdinalIgnoreCase))) continue;
            DateTime? seen = null;
            if (origin.File is { } file)
                try { seen = File.GetLastWriteTimeUtc(file); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            var entry = new Entry(cert, origin, seen);
            // The start warns about a certificate it chose from the store (CertPicker); one read from
            // a file is warned about here, once, at the start.
            if (startupWarned && origin.File is null) { entry.Warned = _now(); entry.WarnedFor = cert.Thumbprint; }
            _entries.Add(entry);
        }
        if (!startupWarned || _entries.Any(e => e.Origin.File is not null)) WarnAboutExpiry();
    }

    public int Count => _entries.Count;

    /// <summary>One look: renewals first, then what is still worth warning about.</summary>
    public void Check()
    {
        foreach (var entry in _entries)
        {
            try { LookForRenewal(entry); }
            catch (Exception ex) { Problem(entry, ex.Message); }
        }
        WarnAboutExpiry();
    }

    private void LookForRenewal(Entry entry)
    {
        if (entry.Origin.NamesOneCertificate) return;
        var current = _gateway.Current(entry.Original)!;
        var found = CliCert.ResolveAgain(entry.Origin, _services, ref entry.FileSeen, InUse, out var problem);
        if (problem is not null) { Problem(entry, problem); return; }
        entry.ProblemSaid = null;
        if (found is null) return;
        if (found.Thumbprint.Equals(current.Thumbprint, StringComparison.OrdinalIgnoreCase))
        {
            // The one presented, found again: a store copy is let go (a file's is the command's to release).
            if (!InUse(found) && entry.Origin.File is null) found.Dispose();
            return;
        }

        var now = _now();
        // A replacement is taken only when it is better: valid now, and lasting longer than the one
        // presented — never an older certificate, or one not valid yet, that a query happens to find.
        if (now < found.NotBefore || now > found.NotAfter || found.NotAfter <= current.NotAfter)
        {
            if (entry.Origin.File is null && !InUse(found)) found.Dispose();
            if (entry.Origin.File is not null)
                Problem(entry, $"{Path.GetFileName(entry.Origin.File)} changed, but its certificate ({found.Thumbprint}, " +
                               $"valid {found.NotBefore:yyyy-MM-dd} to {found.NotAfter:yyyy-MM-dd}) is not a renewal of the one presented; keeping {current.Thumbprint}");
            return;
        }
        // The PIN is applied to the renewal only, once: unlocking a smart-card key every few minutes
        // for a certificate that has not changed would be a needless trip to the card.
        var unlocked = CliCert.WithPin(found, entry.Origin.Pin, TextWriter.Null)!;
        if (!ReferenceEquals(unlocked, found)) _services.Own(unlocked);
        found = unlocked;
        _gateway.Renew(current, found);
        _log($"certificate renewed: '{found.Subject}' {found.Thumbprint}, valid until {found.NotAfter:yyyy-MM-dd}, " +
             $"is presented from now on in place of {current.Thumbprint} (valid until {current.NotAfter:yyyy-MM-dd}). Requests already under way finish on the old one.");
        entry.Warned = null;
    }

    /// <summary>A certificate the gateway presents or was given: never released here.</summary>
    private bool InUse(X509Certificate2 cert) =>
        _entries.Any(e => ReferenceEquals(e.Original, cert)) ||
        _gateway.PresentedCertificates().Any(p => ReferenceEquals(p, cert));

    private void Problem(Entry entry, string problem)
    {
        var now = _now();
        if (entry.ProblemSaid is { } said && now - said < SoonRepeat) return;
        entry.ProblemSaid = now;
        _log($"warning: could not look for a renewal of '{entry.Original.Subject}' ({entry.Original.Thumbprint}): {problem}");
    }

    private void WarnAboutExpiry()
    {
        var now = _now();
        foreach (var entry in _entries)
        {
            var current = _gateway.Current(entry.Original)!;
            var info = new CertificateInfo
            {
                Subject = current.Subject, Issuer = current.Issuer, Thumbprint = current.Thumbprint,
                NotBefore = current.NotBefore, NotAfter = current.NotAfter, HasClientAuthEku = true, Certificate = current
            };
            if (info.ExpiryWarning(now) is not { } warning) continue;
            bool expired = info.IsExpired(now);
            var repeat = expired ? ExpiredRepeat : SoonRepeat;
            if (entry.WarnedFor == current.Thumbprint && entry.Warned is { } last && now - last < repeat) continue;
            entry.Warned = now;
            entry.WarnedFor = current.Thumbprint;
            string next = entry.Origin.NamesOneCertificate
                ? "It is named by thumbprint, so its renewal will need the new thumbprint and a restart of the gateway."
                : entry.Origin.File is { } file
                    ? $"Replace {Path.GetFileName(file)} with the renewed certificate and the gateway presents it within {Minutes(_interval)}, without a restart."
                    : $"Once the renewed certificate is in the store, the gateway presents it within {Minutes(_interval)}, without a restart.";
            _log("warning: " + warning + (expired ? " Sites that check it refuse the connection. " : " ") + next);
        }
    }

    private static string Minutes(TimeSpan t) =>
        t.TotalMinutes >= 1 ? $"{t.TotalMinutes:0} minute{(t.TotalMinutes >= 1.5 ? "s" : "")}" : $"{t.TotalSeconds:0} seconds";
}
