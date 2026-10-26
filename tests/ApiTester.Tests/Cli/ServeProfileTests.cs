using System.IO;
using System.Net;
using ApiTester.Cli;
using ApiTester.Core;
using static ApiTester.Tests.Cli.ServeFixture;

namespace ApiTester.Tests.Cli;

/// <summary>A profile's serve section: `certapi serve --profile corp` is the whole command.</summary>
public class ServeProfileTests
{
    private const string Profile = """
        {
          "defaultProfile": "corp",
          "profiles": {
            "corp": {
              "noProxy": true,
              "serve": {
                "port": 18819, "vhost": true, "browser": true, "token": "${env:GW_TOKEN}",
                "allow": ["*.corp", "*.lab"], "cookieJar": "jar.bin", "forwarded": true, "maxBody": "10m",
                "windowsUser": "CORP\\svc", "windowsPassword": "${env:GW_PW}",
                "sites": [
                  { "name": "api",  "upstream": "https://api.corp" },
                  { "name": "hr",   "upstream": "https://hr.corp:8443", "windowsAuth": false, "caFile": "hr-root.pem",
                    "headers": { "X-Key": "${env:HR_KEY}" } }
                ]
              }
            },
            "broken": { "serve": { "sites": [ { "name": "x" } ] } }
          }
        }
        """;

    private static string? Env(string name) => name switch { "GW_TOKEN" => "tok", "GW_PW" => "pw", "HR_KEY" => "k", _ => null };

    [Fact]
    public void The_serve_section_is_read_with_env_references_and_paths_against_the_file()
    {
        string dir = Path.Combine(Path.GetTempPath(), "cfgdir");
        var config = ConfigLoader.Parse(Profile, new ConfigSource(Path.Combine(dir, "certapi.config.json"), "--config"), Env);
        var s = config.Resolve("corp")!.Serve!;
        Assert.Equal(18819, s.Port);
        Assert.True(s.Vhost);
        Assert.Null(s.VhostSuffix);
        Assert.True(s.Browser);
        Assert.Equal("tok", s.Token);
        Assert.Equal(new[] { "*.corp", "*.lab" }, s.Allow);
        Assert.Equal(Path.GetFullPath(Path.Combine(dir, "jar.bin")), s.CookieJarFile);
        Assert.Equal("10m", s.MaxBody);
        Assert.Equal("pw", s.WindowsPassword);
        Assert.Equal(2, s.Sites.Count);
        Assert.Equal(Path.GetFullPath(Path.Combine(dir, "hr-root.pem")), s.Sites[1].CaFiles.Single());
        Assert.Equal("k", s.Sites[1].Headers.Single().Value);
        Assert.Equal("corp", config.Resolve("corp")!.Name);
    }

    [Fact]
    public void A_broken_serve_section_names_the_profile_and_does_not_break_the_others()
    {
        var config = ConfigLoader.Parse(Profile, null, Env);
        Assert.NotNull(config.Resolve("corp"));
        var ex = Assert.Throws<ConfigException>(() => config.Resolve("broken"));
        Assert.Contains("profile 'broken'", ex.Message);
        Assert.Contains("upstream", ex.Message);

        Assert.Contains("unknown field \"prot\"", Assert.Throws<ConfigException>(() =>
            ConfigLoader.Parse("""{"profiles":{"p":{"serve":{"prot":1}}}}""").Resolve("p")).Message);
        Assert.Contains("GW_TOKEN", Assert.Throws<ConfigException>(() =>
            ConfigLoader.Parse("""{"profiles":{"p":{"serve":{"token":"${env:GW_TOKEN}"}}}}""", null, _ => null).Resolve("p")).Message);
    }

    private static (int code, string stdout, string stderr) Run(string configFile, params string[] args)
    {
        var o = new StringWriter(); var e = new StringWriter();
        var all = new List<string> { "serve", "--check", "--config", configFile };
        all.AddRange(args);
        int code = CliApp.Run(all.ToArray(), o, e,
            services: new CliServices { IsGuiRunning = () => false, LiveStatePath = Path.Combine(Path.GetTempPath(), $"certapi-sp-{Guid.NewGuid():N}.json") });
        return (code, o.ToString(), e.ToString());
    }

    private static string WriteConfig(string json)
    {
        string dir = Path.Combine(Path.GetTempPath(), "certapi-sp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, "certapi.config.json");
        File.WriteAllText(file, json);
        return file;
    }

    [Fact]
    public void The_profile_is_the_whole_command_and_typed_flags_still_win()
    {
        string file = WriteConfig("""
            { "profiles": { "corp": { "noProxy": true, "serve": {
                "port": 18820, "vhost": "gw.test", "token": "t", "allow": "*.lab",
                "sites": [ { "name": "api", "upstream": "https://api.corp" }, { "name": "hr", "upstream": "https://hr.corp", "insecure": true } ] } } } }
            """);
        try
        {
            var (code, output, err) = Run(file, "--profile", "corp");
            Assert.True(code == ExitCodes.Ok, err);
            Assert.Contains("2 sites and dynamic hosts (*.lab)", output);
            Assert.Contains("http://api.gw.test:18820/", output);
            Assert.Contains("http://hr.gw.test:18820/", output);
            Assert.Contains("insecure", output);
            Assert.Contains("token required", output);

            // A typed port wins; typed sites replace the profile's list whole, and say so.
            var (code2, output2, err2) = Run(file, "--profile", "corp", "--port", "18999", "--upstream", "wiki=https://wiki.corp");
            Assert.Equal(ExitCodes.Ok, code2);
            Assert.Contains("http://wiki.gw.test:18999/", output2);
            Assert.DoesNotContain("api.gw.test", output2);
            Assert.Contains("replace the 2 in profile 'corp'", err2);

            // A typed --allow replaces the profile's.
            var (_, output3, _) = Run(file, "--profile", "corp", "--allow", "*.other");
            Assert.Contains("dynamic hosts (*.other)", output3);
            Assert.DoesNotContain("*.lab", output3);
        }
        finally { Directory.Delete(Path.GetDirectoryName(file)!, true); }
    }

    [Fact]
    public void A_profile_without_a_port_says_where_one_goes()
    {
        string file = WriteConfig("""{ "profiles": { "corp": { "serve": { "sites": [ { "name": "api", "upstream": "https://api.corp" } ] } } } }""");
        try
        {
            var (code, _, err) = Run(file, "--profile", "corp");
            Assert.Equal(ExitCodes.Usage, code);
            Assert.Contains("\"port\" in the profile's serve section", err);
        }
        finally { Directory.Delete(Path.GetDirectoryName(file)!, true); }
    }

    [Fact]
    public void Config_show_lists_the_sites_and_masks_every_secret()
    {
        string file = WriteConfig(Profile);
        try
        {
            var o = new StringWriter();
            Environment.SetEnvironmentVariable("GW_TOKEN", "tok-secret");
            Environment.SetEnvironmentVariable("GW_PW", "pw-secret");
            Environment.SetEnvironmentVariable("HR_KEY", "key-secret");
            try { Assert.Equal(ExitCodes.Ok, CliApp.Run(new[] { "config", "show", "--config", file, "--profile", "corp" }, o, TextWriter.Null)); }
            finally
            {
                Environment.SetEnvironmentVariable("GW_TOKEN", null);
                Environment.SetEnvironmentVariable("GW_PW", null);
                Environment.SetEnvironmentVariable("HR_KEY", null);
            }
            string shown = o.ToString();
            Assert.DoesNotContain("tok-secret", shown);
            Assert.DoesNotContain("pw-secret", shown);
            Assert.DoesNotContain("key-secret", shown);
            Assert.Contains("serve.token", shown);
            Assert.Contains("serve.site", shown);
            Assert.Contains("hr -> https://hr.corp:8443", shown);
            Assert.Contains("no windows-auth", shown);
            Assert.Contains("headers X-Key", shown);
        }
        finally { Directory.Delete(Path.GetDirectoryName(file)!, true); }
    }

    [Fact]
    public async Task A_gateway_started_from_a_profile_forwards_to_its_sites()
    {
        var (ca, server, client) = Certs();
        using (ca) using (server) using (client)
        {
            await using var upstream = await LoopbackMtlsServer.StartEchoAsync(server, client.Thumbprint!);
            string file = WriteConfig($$"""
                { "profiles": { "corp": { "serve": {
                    "sites": [ { "name": "echo", "upstream": "{{upstream.BaseUrl}}", "headers": { "X-From": "profile" } } ] } } } }
                """);
            try
            {
                await using var serve = ServeHost.Start(client, "--config", file, "--profile", "corp");
                using var http = NewClient();
                var resp = await Poll(() => http.GetAsync($"{serve.Origin}/echo/hello"));
                Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
                string body = await resp.Content.ReadAsStringAsync();
                Assert.Contains("GET /hello", body);
                Assert.Contains("X-From: profile", body);
            }
            finally { Directory.Delete(Path.GetDirectoryName(file)!, true); }
        }
    }
}
