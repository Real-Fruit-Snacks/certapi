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

    public const string Repository = ReleaseUpdater.Repository;
    public const string DefaultFeed = ReleaseUpdater.DefaultFeed;
    private const string CliAsset = ReleaseUpdater.CliAsset;
    private const string AppAsset = ReleaseUpdater.AppAsset;

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
        var updater = new ReleaseUpdater(services.Client, transportOverrides.ApplyTo(new TransportOptions()), current,
            services.UpdateFeed, services.Environment("GITHUB_TOKEN") ?? services.Environment("GH_TOKEN"));

        try
        {
            var release = updater.GetAsync(wanted, services.Cancel).GetAwaiter().GetResult();
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
            if (ReleaseUpdater.IsScoopInstall(exe))
                throw new CliDataException("This copy was installed with Scoop; update it with: scoop update certapi");
            if (app && services.IsGuiRunning())
                throw new CliDataException("The desktop app is running. Close it and run update again (or use the app's own Help › About › Check for updates), or add --no-app to update only the command line.");

            // ---- download and verify everything before replacing anything ----
            var downloads = new List<(string Target, byte[] Bytes)>();
            foreach (var (name, target) in app
                         ? new[] { (CliAsset, exe), (AppAsset, appPath!) }
                         : new[] { (CliAsset, exe) })
            {
                var asset = release.Assets.FirstOrDefault(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (asset is not null) stderr.WriteLine($"downloading {name} ({asset.Size / 1048576.0:0.0} MB) from {release.Tag} ...");
                downloads.Add((target, updater.DownloadVerifiedAsync(release, name, services.Cancel).GetAwaiter().GetResult()));
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
        catch (ReleaseUpdateException ex) when (ex.Problem is ReleaseProblem.NotFound or ReleaseProblem.MissingAsset)
        { throw new CliDataException(ex.Message); }
        catch (ReleaseUpdateException ex) { throw new CliFailureException(ex.Message); }
    }

    internal static Dictionary<string, string> ParseChecksums(string text) => ReleaseUpdater.ParseChecksums(text);
    internal static void Replace(string target, byte[] bytes) => ReleaseUpdater.Replace(target, bytes);
    public static void CleanUpPrevious(string? exe) => ReleaseUpdater.CleanUpPrevious(exe);
    internal static int CompareVersions(string a, string b) => ReleaseUpdater.CompareVersions(a, b);
}
