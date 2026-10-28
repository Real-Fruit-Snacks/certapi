using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ApiTester.Cli;
using ApiTester.Cli.Commands;
using ApiTester.Core;

namespace ApiTester.Tests.Cli;

/// <summary>`certapi update` against a local stand-in for the GitHub releases API.</summary>
public class UpdateCommandTests
{
    private const string NewCli = "new certapi.exe bytes";
    private const string NewApp = "new ApiTester.App.exe bytes";
    private const string NewQuic = "new msquic.dll bytes";

    private static string Sha(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    /// <summary>A releases API with one release, its assets served by the same server.</summary>
    private static MockServer Feed(int port, string tag, bool digests = true, string? cliBody = null, bool sums = false, bool quic = false)
    {
        string dl = $"http://127.0.0.1:{port}/dl";
        object Asset(string name, string body) => digests
            ? new { name, browser_download_url = $"{dl}/{name}", size = body.Length, digest = (string?)("sha256:" + Sha(body)) }
            : new { name, browser_download_url = $"{dl}/{name}", size = body.Length, digest = (string?)null };
        var assets = new List<object> { Asset("certapi.exe", NewCli), Asset("ApiTester.App.exe", NewApp) };
        if (quic) assets.Add(Asset("msquic.dll", NewQuic));
        if (sums) assets.Add(Asset("SHA256SUMS.txt", $"{Sha(NewCli)}  certapi.exe\n{Sha(NewApp)} *ApiTester.App.exe\n"));
        string release = JsonSerializer.Serialize(new { tag_name = tag, html_url = $"https://example.invalid/{tag}", assets });
        var routes = new object[]
        {
            new { match = new { path = "/releases/latest" }, respond = new { status = 200, body = release } },
            new { match = new { path = $"/releases/tags/{tag}" }, respond = new { status = 200, body = release } },
            new { match = new { path = "/releases/tags/*" }, respond = new { status = 404, body = "{}" } },
            new { match = new { path = "/dl/certapi.exe" }, respond = new { status = 200, body = cliBody ?? NewCli } },
            new { match = new { path = "/dl/ApiTester.App.exe" }, respond = new { status = 200, body = NewApp } },
            new { match = new { path = "/dl/msquic.dll" }, respond = new { status = 200, body = NewQuic } },
            new { match = new { path = "/dl/SHA256SUMS.txt" }, respond = new { status = 200, body = $"{Sha(NewCli)}  certapi.exe\n{Sha(NewApp)} *ApiTester.App.exe\n" } },
        };
        var scenario = MockScenario.Parse(JsonSerializer.Serialize(new { routes }));
        return MockServer.Start(port, MockTlsMode.Http, scenario: scenario);
    }

    private sealed record Install(string Dir, string Exe, string App) : IDisposable
    {
        public void Dispose() { try { Directory.Delete(Dir, true); } catch (IOException) { } }
    }

    private static Install Installed(bool withApp = true)
    {
        var dir = Directory.CreateTempSubdirectory("certapi-update-").FullName;
        var exe = Path.Combine(dir, "certapi.exe");
        var app = Path.Combine(dir, "ApiTester.App.exe");
        File.WriteAllText(exe, "old cli");
        if (withApp) File.WriteAllText(app, "old app");
        return new Install(dir, exe, app);
    }

    private static (int Code, string Out, string Err) Run(Install i, int port, bool guiRunning = false, params string[] args)
    {
        var so = new StringWriter();
        var se = new StringWriter();
        int code = CliApp.Run(new[] { "update", "--no-config" }.Concat(args).ToArray(), so, se, services: new CliServices
        {
            ExecutablePath = i.Exe,
            UpdateFeed = $"http://127.0.0.1:{port}/releases",
            Environment = _ => null,
            IsGuiRunning = () => guiRunning,
            UserConfigPath = null,
            LiveStatePath = Path.Combine(i.Dir, "state.json"),
        });
        return (code, so.ToString(), se.ToString());
    }

    [Fact]
    public async Task A_newer_release_replaces_the_cli_and_the_app_beside_it()
    {
        int port = FreePort();
        await using var feed = Feed(port, "v99.0.0");
        using var i = Installed();
        var r = Run(i, port);
        Assert.True(r.Code == 0, r.Err);
        Assert.Equal(NewCli, File.ReadAllText(i.Exe));
        Assert.Equal(NewApp, File.ReadAllText(i.App));
        Assert.Contains("-> 99.0.0", r.Out);
        Assert.False(File.Exists(i.Exe + ".new"));
    }

    [Fact]
    public async Task No_app_leaves_the_desktop_app_alone()
    {
        int port = FreePort();
        await using var feed = Feed(port, "v99.0.0");
        using var i = Installed();
        Assert.Equal(0, Run(i, port, false, "--no-app").Code);
        Assert.Equal(NewCli, File.ReadAllText(i.Exe));
        Assert.Equal("old app", File.ReadAllText(i.App));
    }

    [Fact]
    public async Task Without_an_app_beside_it_only_the_cli_is_downloaded()
    {
        int port = FreePort();
        await using var feed = Feed(port, "v99.0.0");
        using var i = Installed(withApp: false);
        Assert.Equal(0, Run(i, port).Code);
        Assert.False(File.Exists(i.App));
    }

    [Fact]
    public async Task A_running_desktop_app_stops_the_update_before_anything_changes()
    {
        int port = FreePort();
        await using var feed = Feed(port, "v99.0.0");
        using var i = Installed();
        var r = Run(i, port, guiRunning: true);
        Assert.Equal(3, r.Code);
        Assert.Contains("--no-app", r.Err);
        Assert.Equal("old cli", File.ReadAllText(i.Exe));
    }

    [Fact]
    public async Task A_download_that_does_not_match_its_hash_changes_nothing()
    {
        int port = FreePort();
        await using var feed = Feed(port, "v99.0.0", cliBody: "tampered");
        using var i = Installed();
        var r = Run(i, port);
        Assert.Equal(1, r.Code);
        Assert.Contains("does not match its published SHA-256", r.Err);
        Assert.Equal("old cli", File.ReadAllText(i.Exe));
        Assert.Equal("old app", File.ReadAllText(i.App));
    }

    [Fact]
    public async Task Without_digests_the_checksum_list_is_used()
    {
        int port = FreePort();
        await using var feed = Feed(port, "v99.0.0", digests: false, sums: true);
        using var i = Installed();
        Assert.Equal(0, Run(i, port).Code);
        Assert.Equal(NewApp, File.ReadAllText(i.App));
    }

    [Fact]
    public async Task With_nothing_to_check_against_nothing_is_installed()
    {
        int port = FreePort();
        await using var feed = Feed(port, "v99.0.0", digests: false, sums: false);
        using var i = Installed();
        var r = Run(i, port);
        Assert.Equal(1, r.Code);
        Assert.Contains("cannot be checked", r.Err);
        Assert.Equal("old cli", File.ReadAllText(i.Exe));
    }

    [Fact]
    public async Task Check_says_an_update_is_available_with_exit_1_and_changes_nothing()
    {
        int port = FreePort();
        await using var feed = Feed(port, "v99.0.0");
        using var i = Installed();
        var r = Run(i, port, false, "--check");
        Assert.Equal(1, r.Code);
        Assert.Contains("99.0.0 is available", r.Out);
        Assert.Equal("old cli", File.ReadAllText(i.Exe));
    }

    [Fact]
    public async Task Check_on_a_current_copy_exits_0()
    {
        int port = FreePort();
        await using var feed = Feed(port, "v" + CliApp.ProductVersion);
        using var i = Installed();
        var r = Run(i, port, false, "--check");
        Assert.Equal(0, r.Code);
        Assert.Contains("is current", r.Out);
    }

    [Fact]
    public async Task The_same_version_is_left_alone_unless_forced()
    {
        int port = FreePort();
        await using var feed = Feed(port, "v" + CliApp.ProductVersion);
        using var i = Installed();
        Assert.Equal(0, Run(i, port).Code);
        Assert.Equal("old cli", File.ReadAllText(i.Exe));
        Assert.Equal(0, Run(i, port, false, "--force").Code);
        Assert.Equal(NewCli, File.ReadAllText(i.Exe));
    }

    [Fact]
    public async Task An_older_named_version_is_installed_on_request()
    {
        int port = FreePort();
        await using var feed = Feed(port, "v0.1.0");
        using var i = Installed();
        Assert.Equal(0, Run(i, port).Code);                    // latest is older: nothing to do
        Assert.Equal("old cli", File.ReadAllText(i.Exe));
        Assert.Equal(0, Run(i, port, false, "--version", "0.1.0").Code);
        Assert.Equal(NewCli, File.ReadAllText(i.Exe));
    }

    [Fact]
    public async Task A_missing_version_is_a_data_error()
    {
        int port = FreePort();
        await using var feed = Feed(port, "v99.0.0");
        using var i = Installed();
        var r = Run(i, port, false, "--version", "v1.2.3");
        Assert.Equal(3, r.Code);
        Assert.Contains("no release v1.2.3", r.Err);
    }

    [Fact]
    public async Task A_copy_not_running_as_certapi_exe_is_refused()
    {
        int port = FreePort();
        await using var feed = Feed(port, "v99.0.0");
        using var i = Installed();
        var so = new StringWriter(); var se = new StringWriter();
        int code = CliApp.Run(new[] { "update", "--no-config" }, so, se, services: new CliServices
        {
            ExecutablePath = Path.Combine(i.Dir, "dotnet.exe"), UpdateFeed = $"http://127.0.0.1:{port}/releases",
            Environment = _ => null, IsGuiRunning = () => false, UserConfigPath = null,
        });
        Assert.Equal(3, code);
        Assert.Contains("replaces certapi.exe", se.ToString());
    }

    [Fact]
    public async Task A_scoop_install_is_pointed_at_scoop()
    {
        int port = FreePort();
        await using var feed = Feed(port, "v99.0.0");
        using var i = Installed();
        var scoopExe = Path.Combine(i.Dir, "scoop", "apps", "certapi", "current", "certapi.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(scoopExe)!);
        File.WriteAllText(scoopExe, "old cli");
        var se = new StringWriter();
        int code = CliApp.Run(new[] { "update", "--no-config" }, new StringWriter(), se, services: new CliServices
        {
            ExecutablePath = scoopExe, UpdateFeed = $"http://127.0.0.1:{port}/releases",
            Environment = _ => null, IsGuiRunning = () => false, UserConfigPath = null,
        });
        Assert.Equal(3, code);
        Assert.Contains("scoop update certapi", se.ToString());
        Assert.Equal("old cli", File.ReadAllText(scoopExe));
    }

    [Fact]
    public void Replace_moves_the_old_file_aside_and_the_next_run_cleans_it_up()
    {
        using var i = Installed();
        UpdateCommand.Replace(i.Exe, Encoding.UTF8.GetBytes("v2"));
        Assert.Equal("v2", File.ReadAllText(i.Exe));
        File.WriteAllText(i.Exe + ".old", "left behind");          // as if the old one was running
        UpdateCommand.CleanUpPrevious(i.Exe);
        Assert.False(File.Exists(i.Exe + ".old"));
    }

    [Fact]
    public void A_release_without_a_tag_is_not_a_release()
    {
        Assert.Throws<InvalidOperationException>(() => ReleaseInfo.Parse(Encoding.UTF8.GetBytes("""{"tag_name":null,"assets":[]}""")));
        var r = ReleaseInfo.Parse(Encoding.UTF8.GetBytes("""{"tag_name":"v1.0.0","assets":[{"name":null,"browser_download_url":"x"},{"name":"a","browser_download_url":"u"}]}"""));
        Assert.Single(r.Assets);
    }

    [Theory]
    [InlineData("1.94.0", "1.93.0", 1)]
    [InlineData("v1.93.0", "1.93.0", 0)]
    [InlineData("1.93.0", "1.93.1", -1)]
    [InlineData("1.94.0-beta.1", "1.94.0", -1)]
    [InlineData("2.0", "1.99.9", 1)]
    [InlineData("1.10.0", "1.9.0", 1)]
    public void Versions_compare_as_numbers(string a, string b, int sign) =>
        Assert.Equal(sign, Math.Sign(UpdateCommand.CompareVersions(a, b)));

    [Fact]
    public void Checksum_lists_are_read_in_sha256sum_format()
    {
        var map = UpdateCommand.ParseChecksums($"{new string('a', 64)}  certapi.exe\r\n{new string('B', 64)} *App.exe\nnot a line\n");
        Assert.Equal(new string('a', 64), map["certapi.exe"]);
        Assert.Equal(new string('b', 64), map["App.exe"]);
        Assert.Equal(2, map.Count);
    }

    [Fact]
    public async Task An_update_installs_msquic_beside_the_exes_when_the_release_has_it()
    {
        int port = FreePort();
        await using var feed = Feed(port, "v99.0.0", quic: true);
        using var i = Installed();
        var r = Run(i, port);
        Assert.True(r.Code == 0, r.Err);
        Assert.Equal(NewQuic, File.ReadAllText(Path.Combine(i.Dir, "msquic.dll")));
        Assert.Equal(NewCli, File.ReadAllText(i.Exe));
        Assert.Equal(NewApp, File.ReadAllText(i.App));
    }

    [Fact]
    public async Task A_current_copy_without_msquic_gets_only_msquic()
    {
        int port = FreePort();
        await using var feed = Feed(port, "v" + CliApp.ProductVersion, quic: true);
        using var i = Installed();
        var r = Run(i, port);
        Assert.True(r.Code == 0, r.Err);
        Assert.Equal(NewQuic, File.ReadAllText(Path.Combine(i.Dir, "msquic.dll")));
        Assert.Equal("old cli", File.ReadAllText(i.Exe));      // the exes are already this release
        Assert.Equal("old app", File.ReadAllText(i.App));
        Assert.Contains("added what it was missing (msquic.dll", r.Out);
    }

    [Fact]
    public async Task A_current_copy_with_msquic_is_left_alone()
    {
        int port = FreePort();
        await using var feed = Feed(port, "v" + CliApp.ProductVersion, quic: true);
        using var i = Installed();
        File.WriteAllText(Path.Combine(i.Dir, "msquic.dll"), "installed msquic");
        var r = Run(i, port);
        Assert.Equal(0, r.Code);
        Assert.Contains("is already the latest release", r.Out);
        Assert.Equal("installed msquic", File.ReadAllText(Path.Combine(i.Dir, "msquic.dll")));
    }

    [Fact]
    public async Task A_release_without_msquic_still_updates_and_leaves_none()
    {
        int port = FreePort();
        await using var feed = Feed(port, "v99.0.0", quic: false);
        using var i = Installed();
        var r = Run(i, port);
        Assert.True(r.Code == 0, r.Err);
        Assert.Equal(NewCli, File.ReadAllText(i.Exe));
        Assert.False(File.Exists(Path.Combine(i.Dir, "msquic.dll")));
    }

    [Fact]
    public void Start_up_cleanup_removes_an_old_msquic()
    {
        using var i = Installed();
        File.WriteAllText(Path.Combine(i.Dir, "msquic.dll.old"), "left behind");
        UpdateCommand.CleanUpPrevious(i.Exe);
        Assert.False(File.Exists(Path.Combine(i.Dir, "msquic.dll.old")));
    }
}
