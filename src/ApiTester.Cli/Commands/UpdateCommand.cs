using System.Security.Cryptography;
using System.Text.Json;
using ApiTester.Core;

namespace ApiTester.Cli.Commands;

/// <summary><c>certapi update</c>: replace this certapi.exe (and the desktop app beside it) with the
/// latest GitHub release, after checking every download against the SHA-256 GitHub publishes for
/// it.</summary>
public static class UpdateCommand
{
    public const string Help = """
        Usage: certapi update [--check] [--version <tag>] [--no-app] [--force] [transport options]

        Looks up the latest release of certapi on GitHub and, when it is newer than this copy,
        downloads it and replaces this certapi.exe — and ApiTester.App.exe too, when the desktop
        app sits in the same folder. Every file is checked against the SHA-256 GitHub publishes for
        it before anything is replaced; a file that does not match is discarded and nothing changes.

          --check           Only say whether a newer release exists. Exit 0 when this copy is
                            current, 1 when an update is available
          --version <tag>   Install this release instead of the latest (v1.93.0 or 1.93.0), even
                            when it is older — the way back from a bad release
          --force           Reinstall even when the release is the version already here
          --no-app          Leave the desktop app alone

        Runs through the same transport options as every other command, so a corporate proxy, a
        PAC file or a private root works here too (--proxy, --pac, --ca-file, --no-proxy, ...).
        GITHUB_TOKEN or GH_TOKEN, when set, is sent to the GitHub API (not to the download) to lift
        its hourly limit for anonymous callers.

        The running certapi.exe is renamed to certapi.exe.old and the new one put in its place;
        the .old file is removed the next time certapi runs. Close the desktop app before updating
        it. A copy installed with Scoop is updated with 'scoop update certapi' instead.

        Global: --debug (verbose diagnostics) and --log-file <path> work here too.

        Examples:
          certapi update
          certapi update --check
          certapi update --version v1.93.0
          certapi update --proxy http://proxy.corp:8080

        Exit 0 updated or already current, 1 a download or replacement failed (or --check found a
        newer release), 2 usage, 3 data error.
        """;

    public const string Repository = "Real-Fruit-Snacks/certapi";
    public const string DefaultFeed = "https://api.github.com/repos/" + Repository + "/releases";
    private const string CliAsset = "certapi.exe";
    private const string AppAsset = "ApiTester.App.exe";

    public static int Run(Args args, TextWriter stdout, TextWriter stderr, CliServices services)
    {
        bool checkOnly = args.Flag("--check");
        string? wanted = args.Value("--version");
        bool force = args.Flag("--force");
        bool noApp = args.Flag("--no-app");
        var transportOverrides = TransportFlags.Parse(args, out _, environment: null, services.Profile);
        if (args.Positionals().Count != 0) throw new CliUsageException(Help);

        string current = CliApp.ProductVersion;
        string? exe = services.ExecutablePath;

        // ---- which release ----
        string url = wanted is null
            ? services.UpdateFeed + "/latest"
            : services.UpdateFeed + "/tags/" + (wanted.StartsWith('v') ? wanted : "v" + wanted);
        var transport = transportOverrides.ApplyTo(new TransportOptions());
        var headers = new List<KeyValuePair<string, string>>
        {
            new("User-Agent", $"certapi/{current} (+https://github.com/{Repository})"),
            new("Accept", "application/vnd.github+json"),
        };
        if ((services.Environment("GITHUB_TOKEN") ?? services.Environment("GH_TOKEN")) is { Length: > 0 } token)
            headers.Add(new("Authorization", "Bearer " + token));
        var api = Get(url, headers, transport, services);
        if (api.Error is not null)
            throw new CliFailureException($"could not reach GitHub ({url}): {api.Error.Message}");
        if (api.StatusCode == 404)
            throw new CliDataException(wanted is null ? $"{Repository} has no published release yet." : $"There is no release {wanted} of {Repository}.");
        if (api.StatusCode is 403 or 429)
            throw new CliFailureException("GitHub refused the request — its hourly limit for anonymous callers, most likely. " +
                                          "Try again later, or set GITHUB_TOKEN.");
        if (api.StatusCode is not 200)
            throw new CliFailureException($"GitHub answered {api.StatusCode} {api.ReasonPhrase} for {url}.");

        Release release;
        try { release = Release.Parse(api.Body); }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        { throw new CliFailureException($"GitHub's answer for {url} was not a release: {ex.Message}"); }

        int order = CompareVersions(release.Version, current);
        string? appPath = exe is null ? null : Path.Combine(Path.GetDirectoryName(exe) ?? ".", AppAsset);
        bool app = !noApp && appPath is not null && File.Exists(appPath);

        if (checkOnly)
        {
            if (order > 0)
            {
                stdout.WriteLine($"certapi {release.Version} is available (this is {current}): {release.Page}");
                stdout.WriteLine("  run 'certapi update' to install it.");
                return ExitCodes.Failure;
            }
            stdout.WriteLine($"certapi {current} is current (latest release {release.Version}).");
            return ExitCodes.Ok;
        }

        if (order == 0 && !force)
        {
            stdout.WriteLine($"certapi {current} is already {(wanted is null ? "the latest release" : release.Tag)}. --force reinstalls it.");
            return ExitCodes.Ok;
        }
        if (order < 0 && wanted is null && !force)
        {
            stdout.WriteLine($"certapi {current} is newer than the latest release ({release.Version}); nothing to do.");
            return ExitCodes.Ok;
        }

        // ---- where it goes ----
        if (exe is null || !Path.GetFileName(exe).Equals(CliAsset, StringComparison.OrdinalIgnoreCase))
            throw new CliDataException($"update replaces {CliAsset}, but this copy is running as '{exe ?? "(unknown)"}'. " +
                                       $"Download {release.Page} instead.");
        if (exe.Replace('/', '\\').Contains(@"\scoop\apps\", StringComparison.OrdinalIgnoreCase))
            throw new CliDataException("This copy was installed with Scoop; update it with: scoop update certapi");
        if (app && services.IsGuiRunning())
            throw new CliDataException("The desktop app is running. Close it and run update again, or add --no-app to update only the command line.");

        // ---- download and verify everything before replacing anything ----
        var downloads = new List<(string Target, byte[] Bytes)>();
        foreach (var (name, target) in app
                     ? new[] { (CliAsset, exe), (AppAsset, appPath!) }
                     : new[] { (CliAsset, exe) })
        {
            var asset = release.Assets.FirstOrDefault(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                        ?? throw new CliDataException($"Release {release.Tag} has no {name}.");
            string expected = asset.Sha256 ?? ChecksumFromList(release, name, headers, transport, services)
                ?? throw new CliFailureException($"GitHub publishes no SHA-256 for {name} in {release.Tag}, so it cannot be checked. Nothing was changed.");
            stderr.WriteLine($"downloading {name} ({asset.Size / 1048576.0:0.0} MB) from {release.Tag} ...");
            var file = Get(asset.Url, DownloadHeaders(headers), transport, services, TimeSpan.FromMinutes(15));
            if (file.Error is not null || file.StatusCode is not 200)
                throw new CliFailureException($"could not download {name}: {file.Error?.Message ?? $"{file.StatusCode} {file.ReasonPhrase}"}. Nothing was changed.");
            string actual = Convert.ToHexStringLower(SHA256.HashData(file.Body));
            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new CliFailureException($"{name} does not match its published SHA-256 (expected {expected}, got {actual}). Nothing was changed.");
            downloads.Add((target, file.Body));
        }

        // The app first: it is not running, so if anything is going to refuse, it is the one more
        // likely to — and then the command line is still the old, consistent one.
        downloads.Reverse();
        var done = new List<string>();
        foreach (var (target, bytes) in downloads)
        {
            try { Replace(target, bytes); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new CliFailureException($"could not replace {target}: {ex.Message}" +
                    (done.Count == 0 ? " Nothing was changed." : $" Already replaced: {string.Join(", ", done)} (now {release.Version}); run update again to finish."));
            }
            done.Add(target);
            stdout.WriteLine($"replaced {target}");
        }
        stdout.WriteLine($"certapi {current} -> {release.Version}. Release notes: {release.Page}");
        return ExitCodes.Ok;
    }

    private static ApiResponse Get(string url, IReadOnlyList<KeyValuePair<string, string>> headers, TransportOptions transport,
                                   CliServices services, TimeSpan? timeout = null) =>
        services.Client.SendAsync(
            new ApiRequest { Method = HttpMethod.Get, Url = url, Headers = headers, Timeout = timeout ?? TimeSpan.FromSeconds(60) },
            clientCertificate: null, transport: transport, cancellationToken: services.Cancel).GetAwaiter().GetResult();

    /// <summary>A download goes to GitHub's file storage, not the API: no token, and any content.</summary>
    private static List<KeyValuePair<string, string>> DownloadHeaders(IEnumerable<KeyValuePair<string, string>> apiHeaders) =>
        apiHeaders.Where(h => h.Key == "User-Agent").Append(new("Accept", "application/octet-stream")).ToList();

    /// <summary>The hash from a SHA256SUMS.txt asset, for a release whose assets carry no digest.</summary>
    private static string? ChecksumFromList(Release release, string name, IReadOnlyList<KeyValuePair<string, string>> headers,
                                            TransportOptions transport, CliServices services)
    {
        var list = release.Assets.FirstOrDefault(a => a.Name.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase));
        if (list is null) return null;
        var r = Get(list.Url, DownloadHeaders(headers), transport, services);
        if (r.Error is not null || r.StatusCode is not 200) return null;
        return ParseChecksums(System.Text.Encoding.UTF8.GetString(r.Body)).TryGetValue(name, out var h) ? h : null;
    }

    /// <summary>sha256sum's format: <c>&lt;hex&gt;  &lt;name&gt;</c> (a '*' before the name means
    /// binary mode and is ignored).</summary>
    internal static Dictionary<string, string> ParseChecksums(string text)
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
    internal static void Replace(string target, byte[] bytes)
    {
        string fresh = target + ".new";
        string old = target + ".old";
        File.WriteAllBytes(fresh, bytes);
        if (File.Exists(old)) File.Delete(old);
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

    /// <summary>Removes what an earlier update left behind beside <paramref name="exe"/>. Called on
    /// every start, so it must cost nothing when there is nothing to do and never fail.</summary>
    public static void CleanUpPrevious(string? exe)
    {
        if (exe is null) return;
        string? dir = Path.GetDirectoryName(exe);
        if (dir is null) return;
        foreach (var name in new[] { CliAsset + ".old", AppAsset + ".old" })
        {
            string path = Path.Combine(dir, name);
            try { if (File.Exists(path)) File.Delete(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>Compares two versions (1.93.0, v1.94.0-beta.1). A pre-release sorts before its
    /// release. Unparseable parts compare as text.</summary>
    internal static int CompareVersions(string a, string b)
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
        return string.CompareOrdinal(pa, pb);
    }

    internal sealed record Asset(string Name, string Url, long Size, string? Sha256);

    internal sealed record Release(string Tag, string Page, IReadOnlyList<Asset> Assets)
    {
        public string Version => Tag.TrimStart('v', 'V');

        public static Release Parse(byte[] json)
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var assets = new List<Asset>();
            if (root.TryGetProperty("assets", out var list) && list.ValueKind == JsonValueKind.Array)
                foreach (var a in list.EnumerateArray())
                {
                    string? digest = a.TryGetProperty("digest", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
                    assets.Add(new Asset(
                        a.GetProperty("name").GetString()!,
                        a.GetProperty("browser_download_url").GetString()!,
                        a.TryGetProperty("size", out var s) && s.TryGetInt64(out var n) ? n : 0,
                        digest is not null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest[7..].ToLowerInvariant() : null));
                }
            return new Release(
                root.GetProperty("tag_name").GetString()!,
                root.TryGetProperty("html_url", out var page) ? page.GetString() ?? "" : "",
                assets);
        }
    }
}
