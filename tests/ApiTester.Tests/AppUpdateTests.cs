using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ApiTester.App;
using ApiTester.Core;

namespace ApiTester.Tests;

/// <summary>The desktop app's update: opt-in startup check, and installing both programs from one
/// release after their SHA-256 checks.</summary>
public class AppUpdateTests
{
    private static string Sha(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static MockServer Feed(out string feed, string tag, string appBody = "new app", string cliBody = "new cli", string? servedApp = null)
    {
        int port = Cli.ServeFixture.FreePort();
        string dl = $"http://127.0.0.1:{port}/dl";
        feed = $"http://127.0.0.1:{port}/releases";
        var assets = new object[]
        {
            new { name = "ApiTester.App.exe", browser_download_url = $"{dl}/ApiTester.App.exe", size = appBody.Length, digest = "sha256:" + Sha(appBody) },
            new { name = "certapi.exe", browser_download_url = $"{dl}/certapi.exe", size = cliBody.Length, digest = "sha256:" + Sha(cliBody) },
        };
        string release = JsonSerializer.Serialize(new { tag_name = tag, html_url = "https://example.invalid/" + tag, assets });
        var routes = new object[]
        {
            new { match = new { path = "/releases/latest" }, respond = new { status = 200, body = release } },
            new { match = new { path = "/dl/ApiTester.App.exe" }, respond = new { status = 200, body = servedApp ?? appBody } },
            new { match = new { path = "/dl/certapi.exe" }, respond = new { status = 200, body = cliBody } },
        };
        return MockServer.Start(port, MockTlsMode.Http, scenario: MockScenario.Parse(JsonSerializer.Serialize(new { routes })));
    }

    [Fact]
    public void The_startup_check_is_off_until_turned_on_and_then_daily()
    {
        var now = new DateTime(2026, 10, 27, 12, 0, 0, DateTimeKind.Utc);
        Assert.False(AppUpdate.StartupCheckDue(new AppState(), now));
        Assert.True(AppUpdate.StartupCheckDue(new AppState { CheckForUpdatesOnStart = true }, now));
        Assert.False(AppUpdate.StartupCheckDue(new AppState { CheckForUpdatesOnStart = true, LastUpdateCheckUtc = now.AddHours(-3) }, now));
        Assert.True(AppUpdate.StartupCheckDue(new AppState { CheckForUpdatesOnStart = true, LastUpdateCheckUtc = now.AddHours(-25) }, now));
        // A clock that went backwards does not silence it forever.
        Assert.True(AppUpdate.StartupCheckDue(new AppState { CheckForUpdatesOnStart = true, LastUpdateCheckUtc = now.AddDays(3) }, now));
    }

    [Fact]
    public void The_setting_survives_a_save_and_load()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        try
        {
            var state = new AppState { CheckForUpdatesOnStart = true, LastUpdateCheckUtc = new DateTime(2026, 10, 27, 0, 0, 0, DateTimeKind.Utc) };
            state.SaveTo(path);
            var back = AppState.LoadFrom(path);
            Assert.True(back.CheckForUpdatesOnStart);
            Assert.Equal(state.LastUpdateCheckUtc, back.LastUpdateCheckUtc);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task A_newer_release_is_reported_and_the_same_one_is_not()
    {
        await using (var srv = Feed(out var feed, "v99.0.0"))
        {
            using var client = new ApiClient();
            Assert.Equal("99.0.0", (await AppUpdate.CheckAsync(client, default, feed))!.Version);
        }
        await using (var srv = Feed(out var feed, "v" + AppUpdate.CurrentVersion))
        {
            using var client = new ApiClient();
            Assert.Null(await AppUpdate.CheckAsync(client, default, feed));
        }
    }

    [Fact]
    public async Task Install_replaces_the_app_and_the_command_line_beside_it()
    {
        var dir = Directory.CreateTempSubdirectory("certapi-appupdate-").FullName;
        try
        {
            var app = Path.Combine(dir, "ApiTester.App.exe");
            var cli = Path.Combine(dir, "certapi.exe");
            File.WriteAllText(app, "old app");
            File.WriteAllText(cli, "old cli");
            await using var srv = Feed(out var feed, "v99.0.0");
            using var client = new ApiClient();
            var release = (await AppUpdate.CheckAsync(client, default, feed))!;
            var done = await AppUpdate.InstallAsync(client, release, app, null, default, feed);
            Assert.Equal(new[] { cli, app }, done);   // the running app last
            Assert.Equal("new app", File.ReadAllText(app));
            Assert.Equal("new cli", File.ReadAllText(cli));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task A_tampered_download_changes_nothing()
    {
        var dir = Directory.CreateTempSubdirectory("certapi-appupdate-").FullName;
        try
        {
            var app = Path.Combine(dir, "ApiTester.App.exe");
            File.WriteAllText(app, "old app");
            await using var srv = Feed(out var feed, "v99.0.0", servedApp: "tampered");
            using var client = new ApiClient();
            var release = (await AppUpdate.CheckAsync(client, default, feed))!;
            var ex = await Assert.ThrowsAsync<ReleaseUpdateException>(() => AppUpdate.InstallAsync(client, release, app, null, default, feed));
            Assert.Equal(ReleaseProblem.Mismatch, ex.Problem);
            Assert.Equal("old app", File.ReadAllText(app));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Theory]
    [InlineData(@"C:\tools\certapi\ApiTester.App.exe", false)]
    [InlineData(@"C:\Users\m\scoop\apps\certapi\current\ApiTester.App.exe", true)]
    [InlineData(@"C:\Program Files\dotnet\dotnet.exe", true)]
    public void Only_a_copy_running_as_itself_outside_scoop_replaces_itself(string exe, bool refused) =>
        Assert.Equal(refused, AppUpdate.CannotInstall(exe) is not null);
}
