using System.IO;
using System.Text;
using System.Text.Json;
using ApiTester.Cli;
using ApiTester.Core;

namespace ApiTester.Tests.Cli;

/// <summary>What happens when someone types curl: the options with a direct equivalent are aliases,
/// the ones that differ say what to use instead, and a typo of a real option gets a suggestion.</summary>
public class CurlHabitsTests
{
    private static string TempState() => Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");

    private static (int Code, string Out, string Err, string Body) Run(params string[] args)
    {
        var so = new StringWriter();
        var se = new StringWriter();
        var body = new MemoryStream();
        int code = CliApp.Run(args.Append("--no-config").ToArray(), so, se, body,
            new CliServices { LiveStatePath = TempState(), UserConfigPath = null });
        return (code, so.ToString(), se.ToString(), Encoding.UTF8.GetString(body.ToArray()));
    }

    private static JsonElement Echo(string body) => JsonDocument.Parse(body).RootElement;

    // ---- aliases ----

    [Fact]
    public async Task User_agent_query_and_basic_aliases_reach_the_server()
    {
        await using var srv = MockServer.Start(0, MockTlsMode.Http);
        var r = Run("send", srv.BaseUrl.TrimEnd('/') + "/echo?a=1", "-A", "certapi-test/1", "--query", "q=a b&c",
                    "--query", "flag", "-u", "me:pw", "-s");
        Assert.Equal(0, r.Code);
        var e = Echo(r.Body);
        Assert.Equal("a=1&q=a%20b%26c&flag=", e.GetProperty("query").GetString());
        var headers = e.GetProperty("headers");
        Assert.Equal("certapi-test/1", headers.GetProperty("User-Agent").GetString());
        Assert.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("me:pw")),
            headers.GetProperty("Authorization").GetString());
        Assert.Equal("", r.Err);                       // -s is --quiet: no metadata line
    }

    [Fact]
    public async Task Dash_capital_I_sends_HEAD_and_prints_the_headers()
    {
        await using var srv = MockServer.Start(0, MockTlsMode.Http);
        var r = Run("send", srv.BaseUrl.TrimEnd('/') + "/x", "-I");
        Assert.Equal(0, r.Code);
        Assert.Contains("200", r.Out);                 // status line printed, as --include does
        Assert.Equal("", r.Body);                      // HEAD has no body
    }

    [Fact]
    public void Dash_capital_I_with_another_method_is_a_usage_error()
    {
        var r = Run("send", "http://127.0.0.1:9/", "-I", "-X", "POST");
        Assert.Equal(2, r.Code);
        Assert.Contains("HEAD", r.Err);
    }

    [Fact]
    public async Task Dash_i_includes_headers_and_dash_f_fails_on_4xx()
    {
        await using var srv = MockServer.Start(0, MockTlsMode.Http);
        var r = Run("send", srv.BaseUrl.TrimEnd('/') + "/status/404", "-i", "-f");
        Assert.Equal(1, r.Code);
        Assert.Contains("404", r.Out);
    }

    [Fact]
    public async Task Data_at_file_reads_the_body_from_the_file()
    {
        await using var srv = MockServer.Start(0, MockTlsMode.Http);
        var file = Path.GetTempFileName();
        File.WriteAllText(file, "{\"from\":\"file\"}");
        try
        {
            var r = Run("send", srv.BaseUrl.TrimEnd('/') + "/echo", "-X", "POST", "-d", "@" + file);
            Assert.Equal(0, r.Code);
            Assert.Contains("from", Echo(r.Body).GetProperty("body").GetString());
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void Data_at_a_missing_file_is_a_data_error()
    {
        var r = Run("send", "http://127.0.0.1:9/", "-d", "@no-such-file.json");
        Assert.Equal(3, r.Code);
        Assert.Contains("no-such-file.json", r.Err);
    }

    [Theory]
    [InlineData("-k", "--insecure")]
    [InlineData("-x", "--proxy")]
    [InlineData("--max-time", "--timeout")]
    [InlineData("--cacert", "--ca-file")]
    public void An_alias_is_read_wherever_its_option_is(string alias, string option)
    {
        var a = new Args(new[] { alias, "v" });
        if (option == "--insecure") Assert.True(a.Flag(option));
        else Assert.Equal("v", a.Value(option));
    }

    [Fact]
    public void An_alias_is_unknown_where_its_option_is_not_read()
    {
        var a = new Args(new[] { "-k" });
        Assert.Contains("Unknown option '-k'", Assert.Throws<CliUsageException>(() => a.Positionals()).Message);
    }

    [Fact]
    public void Single_letter_aliases_do_not_collide_across_case()
    {
        // -i is --include, -I is --head; -x is --proxy, -X is --method.
        var a = new Args(new[] { "-i", "-I", "-x", "p", "-X", "m" });
        Assert.True(a.Flag("--head"));
        Assert.True(a.Flag("--include"));
        Assert.Equal("m", a.Value("-X", "--method"));
        Assert.Equal("p", a.Value("--proxy"));
        Assert.Empty(a.Positionals());
    }

    // ---- explanations ----

    [Theory]
    [InlineData("-L", "already followed")]
    [InlineData("--compressed", "already decompressed")]
    [InlineData("-b", "--cookie-jar")]
    [InlineData("--data-urlencode", "--query")]
    [InlineData("-G", "--query")]
    [InlineData("-v", "--debug")]
    [InlineData("--tlsv1.3", "--tls-min 1.3")]
    [InlineData("-E", "--cert-file")]
    public void A_curl_option_that_differs_says_what_to_use(string option, string advice)
    {
        var r = Run("send", "http://127.0.0.1:9/", option);
        Assert.Equal(2, r.Code);
        Assert.Contains($"Option '{option}' does not exist.", r.Err);
        Assert.Contains(advice, r.Err);
    }

    [Fact]
    public void Explanations_for_single_letters_are_case_sensitive()
    {
        Assert.NotNull(UnsupportedOptions.Explain("-e"));
        Assert.NotEqual(UnsupportedOptions.Explain("-e"), UnsupportedOptions.Explain("-E"));
        Assert.Null(UnsupportedOptions.Explain("-l"));
        Assert.NotNull(UnsupportedOptions.Explain("--COMPRESSED"));
    }

    // ---- suggestions ----

    [Theory]
    [InlineData("--insecur", "--insecure")]
    [InlineData("--insceure", "--insecure")]
    [InlineData("--heder", "--header")]
    [InlineData("--prety", "--pretty")]
    [InlineData("--cookiejar", "--cookie-jar")]
    public void A_near_miss_gets_a_suggestion(string typed, string meant)
    {
        var r = Run("send", "http://127.0.0.1:9/", typed);
        Assert.Equal(2, r.Code);
        Assert.Contains($"Unknown option '{typed}'. Did you mean '{meant}'?", r.Err);
    }

    [Fact]
    public void Nothing_close_means_no_suggestion()
    {
        var r = Run("send", "http://127.0.0.1:9/", "--frobnicate-everything");
        Assert.Equal(2, r.Code);
        Assert.Contains("Unknown option '--frobnicate-everything'.", r.Err);
        Assert.DoesNotContain("Did you mean", r.Err);
    }

    [Fact]
    public void Suggestions_come_from_the_command_being_run()
    {
        // --upstream is a serve option; send has no such thing and should not suggest it.
        var r = Run("send", "http://127.0.0.1:9/", "--upstrem");
        Assert.DoesNotContain("--upstream", r.Err);
    }

    [Theory]
    [InlineData("--insecure", "--insecure", 0)]
    [InlineData("--insecur", "--insecure", 1)]
    [InlineData("--insceure", "--insecure", 1)]
    [InlineData("abc", "xyz", 3)]
    public void Distance_counts_edits(string a, string b, int expected) =>
        Assert.Equal(expected, Args.Distance(a, b));
}
