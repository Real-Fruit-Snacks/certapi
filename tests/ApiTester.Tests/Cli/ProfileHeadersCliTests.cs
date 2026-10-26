using System.IO;
using ApiTester.Cli;
using ApiTester.Core;

namespace ApiTester.Tests.Cli;

/// <summary>A profile's `headers` reach every request `send` and `run` make — behind whatever the
/// command line or the saved request sets for the same name.</summary>
public class ProfileHeadersCliTests
{
    private static string WriteConfig(string json)
    {
        string dir = Path.Combine(Path.GetTempPath(), "certapi-profhdr-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, "certapi.config.json");
        File.WriteAllText(file, json);
        return file;
    }

    [Fact]
    public void Standing_headers_are_merged_and_a_typed_name_wins()
    {
        var profile = ConfigLoader.Parse("""{"profiles":{"corp":{"headers":{"X-Env":"staging","Authorization":"Bearer fromprofile"}}}}""", null, _ => null).Resolve("corp")!;
        var merged = profile.WithStandingHeaders(new List<KeyValuePair<string, string>> { new("authorization", "Bearer typed") });
        Assert.Equal(2, merged.Count);
        Assert.Equal("Bearer typed", merged.Single(h => h.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase)).Value);
        Assert.Equal("staging", merged.Single(h => h.Key == "X-Env").Value);
    }

    [Fact]
    public async Task Send_carries_the_profile_headers_and_the_command_line_overrides_one()
    {
        await using var mock = MockServer.Start(0, MockTlsMode.Http);
        string file = WriteConfig("""{"profiles":{"corp":{"noProxy":true,"headers":{"X-Env":"staging","X-Team":"platform"}}}}""");
        try
        {
            var bodyOut = new MemoryStream(); var se = new StringWriter();
            int code = CliApp.Run(new[] { "send", $"{mock.BaseUrl}/echo", "--config", file, "--profile", "corp", "-H", "X-Env: typed", "-q" },
                new StringWriter(), se, bodyOut);
            Assert.True(code == ExitCodes.Ok, se.ToString());
            string body = System.Text.Encoding.UTF8.GetString(bodyOut.ToArray());
            Assert.Contains("\"X-Team\": \"platform\"", body);
            Assert.Contains("\"X-Env\": \"typed\"", body);
            Assert.DoesNotContain("staging", body);
        }
        finally { Directory.Delete(Path.GetDirectoryName(file)!, true); }
    }

    [Fact]
    public async Task Run_carries_the_profile_headers_into_a_saved_request()
    {
        await using var mock = MockServer.Start(0, MockTlsMode.Http);
        string file = WriteConfig("""{"profiles":{"corp":{"noProxy":true,"headers":{"X-Team":"platform"}}}}""");
        var state = new AppState();
        var request = new RequestModel { Method = "GET", Path = $"{mock.BaseUrl}/echo" };
        request.Assertions.Add(new AssertionRule { Target = AssertTarget.BodyText, Op = AssertOp.Contains, Value = "\"X-Team\": \"platform\"" });
        state.Collections.Add(new CollectionNode { Name = "api", Request = request });
        string ws = Path.Combine(Path.GetTempPath(), $"certapi-profhdr-{Guid.NewGuid():N}.json");
        state.SaveTo(ws);
        try
        {
            var svc = new CliServices { LiveStatePath = ws, IsGuiRunning = () => false };
            var se = new StringWriter();
            int with = CliApp.Run(new[] { "run", "api", "--workspace", ws, "--no-record", "--config", file, "--profile", "corp" }, new StringWriter(), se, services: svc);
            Assert.True(with == ExitCodes.Ok, se.ToString());
            int without = CliApp.Run(new[] { "run", "api", "--workspace", ws, "--no-record", "--no-config", "--no-proxy" }, new StringWriter(), new StringWriter(),
                services: new CliServices { LiveStatePath = ws, IsGuiRunning = () => false });
            Assert.NotEqual(ExitCodes.Ok, without);
        }
        finally { File.Delete(ws); Directory.Delete(Path.GetDirectoryName(file)!, true); }
    }
}
