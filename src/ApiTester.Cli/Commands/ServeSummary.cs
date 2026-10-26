using System.Security.Cryptography.X509Certificates;
using System.Text;
using ApiTester.Core;

namespace ApiTester.Cli.Commands;

/// <summary>What a running gateway fronts, as a person reads it: one row per site with the URL to
/// open, where it goes, the certificate it presents and whatever it does differently from the
/// rest; then one line for what the gateway does to every site. Printed at startup and by
/// `serve --check`, so the two can never disagree.</summary>
internal static class ServeSummary
{
    internal sealed record Row(string Site, string Open, string Upstream, string Cert, string Settings);

    /// <summary>The gateway-wide settings, in the words the table's footer uses.</summary>
    internal sealed record GatewayFacts
    {
        public X509Certificate2? Certificate { get; init; }
        public bool Insecure { get; init; }
        public string? CaFiles { get; init; }
        public string? Proxy { get; init; }
        public RevocationMode Revocation { get; init; }
        public IReadOnlyList<string> Browser { get; init; } = Array.Empty<string>();
        public bool BrowserBundle { get; init; }
        public string? CookieJar { get; init; }   // "memory" or a path; null = off
        public bool AutoToken { get; init; }
        public WindowsAuthOptions? WindowsAuth { get; init; }
        public bool Token { get; init; }
        public string? Listen { get; init; }
        public bool Forwarded { get; init; }
        public long? MaxBody { get; init; }
        public bool HeaderRules { get; init; }
        public bool Tls { get; init; }
    }

    public static IReadOnlyList<Row> Rows(GatewayRoutes routes, MtlsGateway gateway, VirtualHosts? vhosts,
        UpstreamAllowlist? allow, string scheme, int port)
    {
        var rows = new List<Row>();
        foreach (var r in routes.All)
        {
            string open = vhosts is not null
                ? vhosts.OriginFor(r).GetLeftPart(UriPartial.Authority) + "/"
                : $"{scheme}://127.0.0.1:{port}{(r.PathPrefix == "/" ? "/" : r.PathPrefix + "/")}";
            var (cert, _, _, _) = gateway.Effective(r);
            string settings = r.Settings is null ? "" : string.Join(", ", r.Settings.Describe());
            if (r.Certificate is not null) settings = settings.Length == 0 ? "own cert" : "own cert, " + settings;
            rows.Add(new Row(r.DisplayName, open, r.Upstream.GetLeftPart(UriPartial.Authority), ShortCert(cert), settings));
        }
        if (allow is not null)
        {
            string open = vhosts is not null
                ? $"{scheme}://<host>.{vhosts.Suffix}:{port}/"
                : $"{scheme}://127.0.0.1:{port}/_/https://<host>/";
            rows.Add(new Row("*", open, "any of " + string.Join(", ", allow.Entries),
                ShortCert(gateway.Effective(null).Certificate), allow.AllowsAnyHost ? "open relay" : ""));
        }
        return rows;
    }

    /// <summary>The table, aligned, with a header — and nothing that is a secret.</summary>
    public static string Render(IReadOnlyList<Row> rows)
    {
        string[] head = { "SITE", "OPEN", "UPSTREAM", "CERT", "SETTINGS" };
        var cells = rows.Select(r => new[] { r.Site, r.Open, r.Upstream, r.Cert, r.Settings.Length == 0 ? "-" : r.Settings }).ToList();
        var widths = Enumerable.Range(0, head.Length)
            .Select(i => Math.Max(head[i].Length, cells.Count == 0 ? 0 : cells.Max(c => c[i].Length))).ToArray();
        var sb = new StringBuilder();
        void Line(string[] c)
        {
            sb.Append("  ");
            for (int i = 0; i < c.Length; i++)
                sb.Append(i == c.Length - 1 ? c[i] : c[i].PadRight(widths[i] + 2));
            sb.Append('\n');
        }
        Line(head);
        foreach (var c in cells) Line(c);
        return sb.ToString().TrimEnd('\n');
    }

    /// <summary>What applies to every site, one phrase each, in the order an operator checks them:
    /// identity, trust, how upstreams are reached, what browsers get, who may call.</summary>
    public static string Gateway(GatewayFacts f)
    {
        var parts = new List<string> { "cert " + ShortCert(f.Certificate) };
        if (f.Insecure) parts.Add("insecure");
        if (f.CaFiles is not null) parts.Add("ca " + f.CaFiles);
        if (f.Revocation != RevocationMode.None) parts.Add("revocation " + f.Revocation.ToString().ToLowerInvariant());
        if (f.Proxy is not null) parts.Add("proxy " + f.Proxy);
        if (f.BrowserBundle) parts.Add("browser");
        else if (f.Browser.Count > 0) parts.Add(string.Join(", ", f.Browser));
        if (f.CookieJar is not null) parts.Add("cookie-jar " + f.CookieJar);
        if (f.AutoToken) parts.Add("auto-token");
        if (f.WindowsAuth is { } wa) parts.Add(wa.UseDefaultCredentials ? "windows-auth" : $"windows-auth {wa.Domain}\\{wa.Username}".Replace(" \\", " "));
        if (f.HeaderRules) parts.Add("header rules");
        if (f.Forwarded) parts.Add("forwarded");
        if (f.MaxBody is { } mb) parts.Add("max-body " + mb);
        if (f.Tls) parts.Add("tls");
        parts.Add(f.Token ? "token required" : "no token");
        parts.Add(f.Listen is null ? "loopback only" : "listen " + f.Listen);
        return "  every site: " + string.Join(" · ", parts);
    }

    /// <summary>A certificate as a person names it: its CN, else its whole subject.</summary>
    public static string ShortCert(X509Certificate2? cert)
    {
        if (cert is null) return "none";
        string cn = cert.GetNameInfo(X509NameType.SimpleName, false);
        return string.IsNullOrEmpty(cn) ? cert.Subject : cn;
    }
}
