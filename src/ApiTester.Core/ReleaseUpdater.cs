using System.Security.Cryptography;
using System.Text.Json;

namespace ApiTester.Core;

/// <summary>One file of a GitHub release. <see cref="Sha256"/> is the digest GitHub publishes for
/// it, lower-case hex, or null for a release made before GitHub recorded digests.</summary>
public sealed record ReleaseAsset(string Name, string Url, long Size, string? Sha256);

/// <summary>A GitHub release, as much of it as an update needs.</summary>
public sealed record ReleaseInfo(string Tag, string Page, IReadOnlyList<ReleaseAsset> Assets)
{
    public string Version => Tag.TrimStart('v', 'V');

    public static ReleaseInfo Parse(byte[] json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var assets = new List<ReleaseAsset>();
        if (root.TryGetProperty("assets", out var list) && list.ValueKind == JsonValueKind.Array)
            foreach (var a in list.EnumerateArray())
            {
                string? digest = a.TryGetProperty("digest", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
                if (a.GetProperty("name").GetString() is not { } assetName ||
                    a.GetProperty("browser_download_url").GetString() is not { } assetUrl) continue;
                assets.Add(new ReleaseAsset(
                    assetName,
                    assetUrl,
                    a.TryGetProperty("size", out var s) && s.TryGetInt64(out var n) ? n : 0,
                    digest is not null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest[7..].ToLowerInvariant() : null));
            }
        return new ReleaseInfo(
            root.GetProperty("tag_name").GetString() is { Length: > 0 } tag ? tag : throw new InvalidOperationException("the release has no tag_name"),
            root.TryGetProperty("html_url", out var page) ? page.GetString() ?? "" : "",
            assets);
    }
}

public enum ReleaseProblem { Unreachable, NotFound, RateLimited, BadAnswer, MissingAsset, NoChecksum, DownloadFailed, Mismatch }

/// <summary>Why an update could not go ahead. Nothing on disk has changed when this is thrown.</summary>
public sealed class ReleaseUpdateException(ReleaseProblem problem, string message) : Exception(message)
{
    public ReleaseProblem Problem { get; } = problem;
}

/// <summary>Finds certapi releases on GitHub, downloads their files and checks each one against the
/// SHA-256 GitHub publishes for it, and swaps a program file for a new one — the parts the command
/// line's <c>update</c> and the desktop app's update share.</summary>
public sealed class ReleaseUpdater
{
    public const string Repository = "Real-Fruit-Snacks/certapi";
    public const string DefaultFeed = "https://api.github.com/repos/" + Repository + "/releases";
    public const string CliAsset = "certapi.exe";
    public const string AppAsset = "ApiTester.App.exe";
    /// <summary>The QUIC library a single-file build loads HTTP/3 from, shipped beside the exes
    /// (the .NET runtime leaves it out of a single-file publish). Older releases do not have it.</summary>
    public const string QuicAsset = "msquic.dll";

    /// <summary>The files to install beside the exes from <paramref name="release"/>: msquic.dll
    /// when the release has it, so moving to an older release (which has none) still works.</summary>
    public static IEnumerable<string> CompanionAssets(ReleaseInfo release) =>
        release.Assets.Any(a => a.Name.Equals(QuicAsset, StringComparison.OrdinalIgnoreCase)) ? [QuicAsset] : [];

    private readonly ApiClient _client;
    private readonly TransportOptions _transport;
    private readonly string _feed;
    private readonly List<KeyValuePair<string, string>> _apiHeaders;

    /// <param name="token">A GitHub token for the API call only (never sent with a download), to
    /// lift the hourly limit on anonymous calls. Null for none.</param>
    public ReleaseUpdater(ApiClient client, TransportOptions? transport, string currentVersion,
                          string feed = DefaultFeed, string? token = null)
    {
        _client = client;
        _transport = transport ?? new TransportOptions();
        _feed = feed.TrimEnd('/');
        _apiHeaders = new()
        {
            new("User-Agent", $"certapi/{currentVersion} (+https://github.com/{Repository})"),
            new("Accept", "application/vnd.github+json"),
        };
        if (!string.IsNullOrEmpty(token)) _apiHeaders.Add(new("Authorization", "Bearer " + token));
    }

    /// <summary>The latest release, or the one tagged <paramref name="tag"/> (with or without its v).</summary>
    public async Task<ReleaseInfo> GetAsync(string? tag, CancellationToken ct = default)
    {
        string url = tag is null ? _feed + "/latest" : _feed + "/tags/" + (tag.StartsWith('v') ? tag : "v" + tag);
        var api = await GetAsync(url, _apiHeaders, TimeSpan.FromSeconds(60), ct);
        if (api.Error is not null)
            throw new ReleaseUpdateException(ReleaseProblem.Unreachable, $"could not reach GitHub ({url}): {api.Error.Message}");
        if (api.StatusCode == 404)
            throw new ReleaseUpdateException(ReleaseProblem.NotFound,
                tag is null ? $"{Repository} has no published release yet." : $"There is no release {tag} of {Repository}.");
        if (api.StatusCode is 403 or 429)
            throw new ReleaseUpdateException(ReleaseProblem.RateLimited,
                "GitHub refused the request — its hourly limit for anonymous callers, most likely. Try again later, or set GITHUB_TOKEN.");
        if (api.StatusCode is not 200)
            throw new ReleaseUpdateException(ReleaseProblem.BadAnswer, $"GitHub answered {api.StatusCode} {api.ReasonPhrase} for {url}.");
        try { return ReleaseInfo.Parse(api.Body); }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        { throw new ReleaseUpdateException(ReleaseProblem.BadAnswer, $"GitHub's answer for {url} was not a release: {ex.Message}"); }
    }

    /// <summary>The bytes of <paramref name="assetName"/>, checked against its published SHA-256
    /// (the asset's digest, or the release's SHA256SUMS.txt). Throws rather than return anything
    /// unchecked.</summary>
    public async Task<byte[]> DownloadVerifiedAsync(ReleaseInfo release, string assetName, CancellationToken ct = default)
    {
        var asset = release.Assets.FirstOrDefault(a => a.Name.Equals(assetName, StringComparison.OrdinalIgnoreCase))
                    ?? throw new ReleaseUpdateException(ReleaseProblem.MissingAsset, $"Release {release.Tag} has no {assetName}.");
        string expected = asset.Sha256 ?? await ChecksumFromListAsync(release, assetName, ct)
            ?? throw new ReleaseUpdateException(ReleaseProblem.NoChecksum,
                   $"GitHub publishes no SHA-256 for {assetName} in {release.Tag}, so it cannot be checked. Nothing was changed.");
        var file = await GetAsync(asset.Url, DownloadHeaders(), TimeSpan.FromMinutes(15), ct);
        if (file.Error is not null || file.StatusCode is not 200)
            throw new ReleaseUpdateException(ReleaseProblem.DownloadFailed,
                $"could not download {assetName}: {file.Error?.Message ?? $"{file.StatusCode} {file.ReasonPhrase}"}. Nothing was changed.");
        string actual = Convert.ToHexStringLower(SHA256.HashData(file.Body));
        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new ReleaseUpdateException(ReleaseProblem.Mismatch,
                $"{assetName} does not match its published SHA-256 (expected {expected}, got {actual}). Nothing was changed.");
        return file.Body;
    }

    private Task<ApiResponse> GetAsync(string url, IReadOnlyList<KeyValuePair<string, string>> headers, TimeSpan timeout, CancellationToken ct) =>
        _client.SendAsync(new ApiRequest { Method = HttpMethod.Get, Url = url, Headers = headers, Timeout = timeout },
                          clientCertificate: null, transport: _transport, cancellationToken: ct);

    /// <summary>A download goes to GitHub's file storage, not the API: no token, and any content.</summary>
    private List<KeyValuePair<string, string>> DownloadHeaders() =>
        _apiHeaders.Where(h => h.Key == "User-Agent").Append(new("Accept", "application/octet-stream")).ToList();

    private async Task<string?> ChecksumFromListAsync(ReleaseInfo release, string name, CancellationToken ct)
    {
        var list = release.Assets.FirstOrDefault(a => a.Name.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase));
        if (list is null) return null;
        var r = await GetAsync(list.Url, DownloadHeaders(), TimeSpan.FromSeconds(60), ct);
        if (r.Error is not null || r.StatusCode is not 200) return null;
        return ParseChecksums(System.Text.Encoding.UTF8.GetString(r.Body)).TryGetValue(name, out var h) ? h : null;
    }

    /// <summary>sha256sum's format: <c>&lt;hex&gt;  &lt;name&gt;</c> (a '*' before the name means
    /// binary mode and is ignored).</summary>
    public static Dictionary<string, string> ParseChecksums(string text)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            int space = line.IndexOf(' ');
            if (space != 64) continue;
            map[line[(space + 1)..].TrimStart(' ', '*')] = line[..64].ToLowerInvariant();
        }
        return map;
    }

    /// <summary>Puts <paramref name="bytes"/> at <paramref name="target"/>. Windows will not
    /// overwrite a running program but will rename it, so the old file moves aside to .old first;
    /// if the new file cannot then be put in place, the old one is put back.</summary>
    public static void Replace(string target, byte[] bytes)
    {
        string fresh = target + ".new";
        string old = target + ".old";
        File.WriteAllBytes(fresh, bytes);
        if (File.Exists(old))
        {
            // The previous update's .old is still running when an earlier copy (a long-lived
            // `certapi mcp`, `serve`) is: Windows refuses to delete a running image but lets it be
            // renamed, so it moves aside to a unique name that the next start cleans up.
            try { File.Delete(old); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                File.Move(old, old + "-" + Guid.NewGuid().ToString("N")[..8]);
            }
        }
        bool hadOld = File.Exists(target);
        if (hadOld) File.Move(target, old);
        try { File.Move(fresh, target); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (!hadOld) { TryDelete(fresh); throw; }
            try { File.Move(old, target); }
            catch (Exception back) when (back is IOException or UnauthorizedAccessException)
            {
                // Neither move worked: keep both copies and say where they are, rather than
                // deleting the only good one.
                throw new IOException($"{ex.Message} Putting the old file back failed too ({back.Message}). " +
                                      $"The new version is {fresh} and the previous one {old}; rename either to {Path.GetFileName(target)}.", ex);
            }
            TryDelete(fresh);
            throw;
        }
        // A program that is running cannot be deleted; the next run removes it (CleanUpPrevious).
        try { File.Delete(old); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>How old a leftover <c>.new</c> download must be before it is taken as abandoned.</summary>
    internal static readonly TimeSpan StaleDownload = TimeSpan.FromMinutes(10);

    /// <summary>Removes what an earlier update left beside <paramref name="exe"/>. Called on every
    /// start of either program, so it must cost nothing when there is nothing to do and never fail.</summary>
    public static void CleanUpPrevious(string? exe)
    {
        if (exe is null) return;
        string? dir = Path.GetDirectoryName(exe);
        if (dir is null) return;
        foreach (var asset in new[] { CliAsset, AppAsset, QuicAsset })
        {
            // .old, the .old-<id> copies moved aside while still running, and a .new left by an
            // update that stopped half way — only a stale one: a fresh .new may be an update in
            // progress in the other program, between writing the download and moving it into place.
            string[] leftovers;
            try { leftovers = Directory.GetFiles(dir, asset + ".old*"); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            string pending = Path.Combine(dir, asset + ".new");
            try
            {
                if (File.Exists(pending) && DateTime.UtcNow - File.GetLastWriteTimeUtc(pending) > StaleDownload)
                    leftovers = leftovers.Append(pending).ToArray();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            foreach (var path in leftovers)
            {
                try { if (File.Exists(path)) File.Delete(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    /// <summary>Compares two versions (1.93.0, v1.94.0-beta.1). A pre-release sorts before its
    /// release.</summary>
    public static int CompareVersions(string a, string b)
    {
        static (Version core, string? pre) Split(string v)
        {
            v = v.Trim().TrimStart('v', 'V');
            int plus = v.IndexOf('+');
            if (plus >= 0) v = v[..plus];
            int dash = v.IndexOf('-');
            string core = dash >= 0 ? v[..dash] : v;
            string? pre = dash >= 0 ? v[(dash + 1)..] : null;
            return (Version.TryParse(core.Count(c => c == '.') == 0 ? core + ".0" : core, out var parsed) ? parsed : new Version(0, 0), pre);
        }
        var (ca, pa) = Split(a);
        var (cb, pb) = Split(b);
        int c = ca.CompareTo(cb);
        if (c != 0) return c;
        if (pa is null && pb is null) return 0;
        if (pa is null) return 1;
        if (pb is null) return -1;
        return ComparePrerelease(pa, pb);
    }

    /// <summary>SemVer's rule: dot-separated identifiers left to right, numeric ones as numbers
    /// (beta.10 after beta.9), a numeric one before an alphanumeric one, and a shorter list first
    /// when it is a prefix of the longer.</summary>
    private static int ComparePrerelease(string a, string b)
    {
        var x = a.Split('.');
        var y = b.Split('.');
        for (int i = 0; i < Math.Min(x.Length, y.Length); i++)
        {
            bool xn = ulong.TryParse(x[i], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var xv);
            bool yn = ulong.TryParse(y[i], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var yv);
            int c = xn && yn ? xv.CompareTo(yv) : xn ? -1 : yn ? 1 : string.CompareOrdinal(x[i], y[i]);
            if (c != 0) return c;
        }
        return x.Length.CompareTo(y.Length);
    }

    /// <summary>True when <paramref name="exe"/> lives in a Scoop install, which Scoop itself updates.</summary>
    public static bool IsScoopInstall(string exe) =>
        exe.Replace('/', '\\').Contains(@"\scoop\apps\", StringComparison.OrdinalIgnoreCase);
}
