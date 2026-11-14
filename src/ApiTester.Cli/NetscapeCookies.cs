namespace ApiTester.Cli;

/// <summary>Reads a Netscape cookie file — what curl's -c writes and browser extensions export —
/// for <c>send -b &lt;file&gt;</c>: the cookies that would be sent to a URL, as one Cookie value.</summary>
public static class NetscapeCookies
{
    public static string HeaderFor(string path, string url)
    {
        if (!File.Exists(path)) throw new CliDataException($"-b/--cookie: '{path}' is not a cookie file (and has no '=' to be a cookie).");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return "";
        return string.Join("; ", Matching(File.ReadAllLines(path), uri, DateTimeOffset.UtcNow).Select(c => c.Name + "=" + c.Value));
    }

    internal static IEnumerable<(string Name, string Value)> Matching(IEnumerable<string> lines, Uri uri, DateTimeOffset now)
    {
        foreach (var raw in lines)
        {
            string line = raw.TrimEnd('\r');
            if (line.StartsWith("#HttpOnly_", StringComparison.Ordinal)) line = line["#HttpOnly_".Length..];
            else if (line.Length == 0 || line.StartsWith('#')) continue;
            var f = line.Split('\t');
            if (f.Length < 7) continue;
            string domain = f[0].TrimStart('.');
            bool subdomains = f[1].Equals("TRUE", StringComparison.OrdinalIgnoreCase) || f[0].StartsWith('.');
            string path = f[2].Length == 0 ? "/" : f[2];
            bool secure = f[3].Equals("TRUE", StringComparison.OrdinalIgnoreCase);
            if (long.TryParse(f[4], out long expires) && expires > 0 && DateTimeOffset.FromUnixTimeSeconds(expires) < now) continue;
            bool hostOk = uri.Host.Equals(domain, StringComparison.OrdinalIgnoreCase) ||
                          (subdomains && uri.Host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase));
            bool pathOk = uri.AbsolutePath.StartsWith(path, StringComparison.Ordinal) &&
                          (uri.AbsolutePath.Length == path.Length || path.EndsWith('/') || uri.AbsolutePath[path.Length] == '/');
            if (!hostOk || !pathOk || (secure && uri.Scheme != Uri.UriSchemeHttps)) continue;
            yield return (f[5], f[6]);
        }
    }
}
