using System.IO;
using System.Reflection;
using ApiTester.Core;

namespace ApiTester.App;

/// <summary>The desktop app's side of updating: asks GitHub for the latest release, and installs
/// it — ApiTester.App.exe, and certapi.exe when it sits in the same folder — after checking every
/// file against the SHA-256 GitHub publishes for it. The same <see cref="ReleaseUpdater"/> the
/// command line's <c>certapi update</c> uses, so the two can never disagree about what is safe.</summary>
public static class AppUpdate
{
    /// <summary>The version this app carries, without build metadata.</summary>
    public static string CurrentVersion
    {
        get
        {
            var v = typeof(AppUpdate).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                    ?? typeof(AppUpdate).Assembly.GetName().Version?.ToString() ?? "0.0.0";
            int plus = v.IndexOf('+');
            return plus >= 0 ? v[..plus] : v;
        }
    }

    /// <summary>How long a startup check stays good: one call to GitHub a day at most.</summary>
    public static readonly TimeSpan StartupInterval = TimeSpan.FromHours(24);

    /// <summary>True when a startup check is due under the opt-in setting.</summary>
    public static bool StartupCheckDue(AppState state, DateTime nowUtc) =>
        state.CheckForUpdatesOnStart &&
        (state.LastUpdateCheckUtc is not { } last || nowUtc - last >= StartupInterval || last > nowUtc);

    /// <summary>The latest release when it is newer than this app, else null.</summary>
    public static async Task<ReleaseInfo?> CheckAsync(ApiClient client, CancellationToken ct = default,
                                                     string feed = ReleaseUpdater.DefaultFeed)
    {
        var release = await new ReleaseUpdater(client, null, CurrentVersion, feed).GetAsync(null, ct);
        return ReleaseUpdater.CompareVersions(release.Version, CurrentVersion) > 0 ? release : null;
    }

    /// <summary>Why this copy cannot replace itself, or null when it can.</summary>
    public static string? CannotInstall(string? exe)
    {
        if (exe is null || !Path.GetFileName(exe).Equals(ReleaseUpdater.AppAsset, StringComparison.OrdinalIgnoreCase))
            return $"This copy is not running as {ReleaseUpdater.AppAsset}, so it cannot replace itself. Download the release instead.";
        if (ReleaseUpdater.IsScoopInstall(exe))
            return "This copy was installed with Scoop; update it with: scoop update certapi";
        return null;
    }

    /// <summary>Downloads and checks every file first, then replaces them. Returns the files
    /// replaced. Nothing changes when a download or a check fails.</summary>
    public static async Task<IReadOnlyList<string>> InstallAsync(ApiClient client, ReleaseInfo release, string exe,
                                                                 IProgress<string>? progress = null, CancellationToken ct = default,
                                                                 string feed = ReleaseUpdater.DefaultFeed)
    {
        var updater = new ReleaseUpdater(client, null, CurrentVersion, feed);
        string dir = Path.GetDirectoryName(exe) ?? ".";
        // The command line first and the running app last: if anything is going to refuse, it is
        // better that the app — the copy this install is running from — is still the old one, so
        // the next attempt starts from a consistent state.
        var targets = new List<(string Asset, string Path)>();
        string cli = Path.Combine(dir, ReleaseUpdater.CliAsset);
        if (File.Exists(cli)) targets.Add((ReleaseUpdater.CliAsset, cli));
        targets.Add((ReleaseUpdater.AppAsset, exe));

        var downloads = new List<(string Path, byte[] Bytes)>();
        foreach (var (asset, path) in targets)
        {
            progress?.Report($"Downloading {asset} from {release.Tag} and checking its SHA-256…");
            downloads.Add((path, await updater.DownloadVerifiedAsync(release, asset, ct)));
        }

        var done = new List<string>();
        foreach (var (path, bytes) in downloads)
        {
            progress?.Report($"Replacing {Path.GetFileName(path)}…");
            try { ReleaseUpdater.Replace(path, bytes); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new IOException($"Could not replace {path}: {ex.Message}" +
                    (done.Count == 0 ? " Nothing was changed." : $" Already replaced: {string.Join(", ", done)}."), ex);
            }
            done.Add(path);
        }
        return done;
    }
}
