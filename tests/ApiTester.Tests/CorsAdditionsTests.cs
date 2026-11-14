using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using ApiTester.Cli;
using ApiTester.Cli.Commands;
using ApiTester.Cli.Mcp;
using ApiTester.Core;

namespace ApiTester.Tests;

/// <summary>The second round of CORS: redirects followed hop by hop, Private Network Access,
/// `cors --audit`, the origin × method table, the cors assertion, and `doctor --origin`.</summary>
public class CorsAdditionsTests
{
    private static string TempState() => Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");

    private static (int Code, string Out, string Err) Cli(params string[] args)
    {
        var so = new StringWriter();
        var se = new StringWriter();
        int code = CliApp.Run(args.Append("--no-config").ToArray(), so, se, new MemoryStream(),
            new CliServices { LiveStatePath = TempState(), UserConfigPath = null });
        return (code, so.ToString(), se.ToString());
    }

    private static List<KeyValuePair<string, string>> H(params string[] lines) =>
        lines.Select(l => { int c = l.IndexOf(':'); return new KeyValuePair<string, string>(l[..c].Trim(), l[(c + 1)..].Trim()); }).ToList();

    // ---- redirects ----

    [Fact]
    public async Task A_cross_origin_redirect_is_followed_with_Origin_null_and_checked_at_each_hop()
    {
        string? seenOrigin = null;
        await using var target = new RawHttpServer(req =>
        {
            seenOrigin = req.Header("Origin");
            return RawHttpServer.Response(200, "OK", "{}", "Access-Control-Allow-Origin: null");
        });
        await using var start = new RawHttpServer(_ => RawHttpServer.Response(302, "Found", "",
            $"Location: {target.BaseUrl}/end", "Access-Control-Allow-Origin: https://app.example"));
        var r = Cli("cors", start.BaseUrl + "/start", "--origin", "https://app.example", "--json");
        Assert.True(r.Code == 0, r.Out + r.Err);
        Assert.Equal("null", seenOrigin);
        var j = JsonDocument.Parse(r.Out).RootElement;
        Assert.Equal(2, j.GetProperty("hops").GetArrayLength());
        Assert.Equal("null", j.GetProperty("hops")[1].GetProperty("origin").GetString());
        Assert.Contains(j.GetProperty("notes").EnumerateArray(), n => n.GetString()!.Contains("Origin: null"));
    }

    [Fact]
    public async Task A_redirect_without_CORS_headers_or_a_target_that_wants_the_page_origin_is_blocked()
    {
        await using var target = new RawHttpServer(_ => RawHttpServer.Response(200, "OK", "{}", "Access-Control-Allow-Origin: https://app.example"));
        await using var bare = new RawHttpServer(_ => RawHttpServer.Response(302, "Found", "", $"Location: {target.BaseUrl}/end"));
        var noHeaders = Cli("cors", bare.BaseUrl + "/start", "--origin", "https://app.example");
        Assert.Equal(1, noHeaders.Code);
        Assert.Contains("the response has no Access-Control-Allow-Origin", noHeaders.Out);

        await using var allowing = new RawHttpServer(_ => RawHttpServer.Response(302, "Found", "",
            $"Location: {target.BaseUrl}/end", "Access-Control-Allow-Origin: https://app.example"));
        var wantsOrigin = Cli("cors", allowing.BaseUrl + "/start", "--origin", "https://app.example");
        Assert.Equal(1, wantsOrigin.Code);
        Assert.Contains("after redirect 1, the response allows the origin https://app.example, not null", wantsOrigin.Out);

        var stopped = Cli("cors", allowing.BaseUrl + "/start", "--origin", "https://app.example", "--no-redirect", "--json");
        Assert.Equal(0, stopped.Code);
        Assert.Equal(1, JsonDocument.Parse(stopped.Out).RootElement.GetProperty("hops").GetArrayLength());
    }

    [Fact]
    public void A_same_origin_request_needs_no_CORS_at_all()
    {
        var r = Cli("cors", "http://127.0.0.1:9/x", "--origin", "http://127.0.0.1:9");
        Assert.Equal(0, r.Code);
        Assert.Contains("share an origin", r.Out);
    }

    // ---- Private Network Access ----

    [Fact]
    public void A_private_network_preflight_needs_Allow_Private_Network()
    {
        var request = new CorsRequest("https://app.example", "GET", H(), false) { PrivateNetwork = true };
        Assert.Contains("Private Network Access", CorsRules.PreflightReason(request));
        Assert.Contains(CorsRules.PreflightHeaders(request), h => h.Key == "Access-Control-Request-Private-Network" && h.Value == "true");
        var (missing, _, _) = CorsRules.CheckPreflight(request, 204, H("Access-Control-Allow-Origin: https://app.example"));
        Assert.Contains(missing, p => p.Contains("Access-Control-Allow-Private-Network"));
        var (none, _, _) = CorsRules.CheckPreflight(request, 204, H("Access-Control-Allow-Origin: https://app.example", "Access-Control-Allow-Private-Network: true"));
        Assert.Empty(none);
    }

    [Fact]
    public async Task Cors_private_network_passes_against_a_mock_that_grants_it_and_notes_the_case_unasked()
    {
        await using var mock = MockServer.Start(0, MockTlsMode.Http, cors: new MockCors(null));
        var asked = Cli("cors", mock.BaseUrl + "x", "--origin", "https://app.example", "--private-network");
        Assert.True(asked.Code == 0, asked.Out);
        Assert.Contains("allows Private Network Access", asked.Out);
        var unasked = Cli("cors", mock.BaseUrl + "x", "--origin", "https://app.example");
        Assert.Contains("local network", unasked.Out);
        var localPage = Cli("cors", mock.BaseUrl + "x", "--origin", "http://localhost:3000");
        Assert.DoesNotContain("local network", localPage.Out);
    }

    [Theory]
    [InlineData("10.1.2.3", true)]
    [InlineData("172.20.0.1", true)]
    [InlineData("192.168.1.1", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("fd00::1", true)]
    [InlineData("8.8.8.8", false)]
    [InlineData("172.32.0.1", false)]
    public void Private_addresses_are_recognised(string address, bool expected) =>
        Assert.Equal(expected, AddressSpace.IsPrivate(System.Net.IPAddress.Parse(address)));

    // ---- audit ----

    [Fact]
    public async Task The_audit_flags_a_server_that_reflects_any_origin_with_credentials()
    {
        await using var mock = MockServer.Start(0, MockTlsMode.Http, cors: new MockCors(null));
        var r = Cli("cors", mock.BaseUrl + "me", "--audit", "--origin", "https://app.example.com", "--json");
        Assert.Equal(1, r.Code);
        var j = JsonDocument.Parse(r.Out).RootElement;
        Assert.Equal("high", j.GetProperty("worst").GetString());
        var titles = j.GetProperty("findings").EnumerateArray().Select(f => f.GetProperty("title").GetString()).ToList();
        Assert.Contains("Any origin is reflected, with credentials", titles);
        Assert.Contains("The null origin is trusted, with credentials", titles);
        Assert.Contains("Preflights grant whatever they ask for", titles);
    }

    [Fact]
    public async Task The_audit_passes_a_strict_list_and_reports_star_as_information()
    {
        await using var strict = MockServer.Start(0, MockTlsMode.Http, cors: new MockCors(new[] { "https://app.example.com" }));
        var ok = Cli("cors", strict.BaseUrl + "me", "--audit", "--origin", "https://app.example.com");
        Assert.True(ok.Code == 0, ok.Out);
        Assert.Contains("findings   none", ok.Out);

        await using var star = MockServer.Start(0, MockTlsMode.Http);
        var any = Cli("cors", star.BaseUrl + "me", "--audit", "--origin", "https://app.example.com", "-q");
        Assert.Equal(0, any.Code);
        Assert.Contains("INFO", any.Out);
    }

    [Fact]
    public async Task The_audit_catches_a_check_that_only_looks_at_the_end_of_the_name()
    {
        await using var naive = new RawHttpServer(req =>
        {
            string origin = req.Header("Origin") ?? "";
            return origin.EndsWith("app.example.com", StringComparison.Ordinal)
                ? RawHttpServer.Response(200, "OK", "{}", "Access-Control-Allow-Origin: " + origin, "Access-Control-Allow-Credentials: true", "Vary: Origin")
                : RawHttpServer.Response(200, "OK", "{}");
        });
        var r = Cli("cors", naive.BaseUrl + "/me", "--audit", "--origin", "https://app.example.com", "--json");
        Assert.Equal(1, r.Code);
        var findings = JsonDocument.Parse(r.Out).RootElement.GetProperty("findings").EnumerateArray().ToList();
        Assert.Contains(findings, f => f.GetProperty("title").GetString()!.StartsWith("The origin check matches the end of the name") && f.GetProperty("severity").GetString() == "high");
        Assert.Contains(findings, f => f.GetProperty("title").GetString()!.StartsWith("Every subdomain is trusted"));
        Assert.DoesNotContain(findings, f => f.GetProperty("title").GetString()!.StartsWith("Any origin"));
    }

    [Fact]
    public void Audit_probes_cover_the_usual_mistakes()
    {
        var probes = CorsAudit.ProbesFor(new Uri("https://app.example.com")).ToDictionary(p => p.Kind, p => p.Origin);
        Assert.Equal("null", probes["null origin"]);
        Assert.Equal("https://certapi-cors-auditapp.example.com", probes["prefix look-alike"]);
        Assert.Equal("https://app.example.com.certapi-cors-audit.example", probes["suffix look-alike"]);
        Assert.Equal("https://appxexample.com", probes["unescaped dot"]);
        Assert.Equal("http://app.example.com", probes["plain http"]);
        Assert.Equal("https://certapi-cors-audit.app.example.com", probes["subdomain"]);
    }

    [Fact]
    public void Audit_refuses_options_it_does_not_use()
    {
        Assert.Equal(2, Cli("cors", "http://127.0.0.1:9/", "--audit", "-X", "PUT").Code);
        Assert.Equal(2, Cli("cors", "http://127.0.0.1:9/", "--audit", "--origin", "https://a.example", "--origin", "https://b.example").Code);
    }

    // ---- the table ----

    [Fact]
    public async Task Several_origins_and_methods_make_a_table_and_any_block_fails_it()
    {
        await using var mock = MockServer.Start(0, MockTlsMode.Http, cors: new MockCors(new[] { "https://app.example" }));
        var r = Cli("cors", mock.BaseUrl + "x", "--origin", "https://app.example,https://evil.example", "-X", "GET", "-X", "PUT");
        Assert.Equal(1, r.Code);
        var lines = r.Out.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd()).ToList();
        Assert.StartsWith("origin", lines[0]);
        Assert.Contains(lines, l => l.StartsWith("https://app.example ") && l.Contains("GET") && l.EndsWith("allowed"));
        Assert.Contains(lines, l => l.StartsWith("https://app.example ") && l.Contains("PUT") && l.Contains("preflight ok"));
        Assert.Contains(lines, l => l.StartsWith("https://evil.example") && l.Contains("BLOCKED"));

        var json = Cli("cors", mock.BaseUrl + "x", "--origin", "https://app.example", "-X", "GET,HEAD", "--json");
        Assert.Equal(2, JsonDocument.Parse(json.Out).RootElement.GetArrayLength());
    }

    // ---- the assertion ----

    [Theory]
    [InlineData("cors https://app.example allowed", "https://app.example", AssertOp.Equals, "allowed")]
    [InlineData("cors https://app.example/path/ == credentials", "https://app.example", AssertOp.Equals, "credentials")]
    [InlineData("cors null != blocked", "null", AssertOp.NotEquals, "blocked")]
    public void A_cors_assertion_parses(string expr, string origin, AssertOp op, string value)
    {
        var rule = AssertionParser.Parse(expr);
        Assert.Equal(AssertTarget.Cors, rule.Target);
        Assert.Equal(origin, rule.Path);
        Assert.Equal(op, rule.Op);
        Assert.Equal(value, rule.Value);
    }

    [Theory]
    [InlineData("cors https://app.example")]
    [InlineData("cors app.example allowed")]
    [InlineData("cors https://app.example maybe")]
    public void A_bad_cors_assertion_says_what_is_wrong(string expr) =>
        Assert.Throws<FormatException>(() => AssertionParser.Parse(expr));

    [Fact]
    public void A_cors_assertion_judges_the_response()
    {
        var echo = new ApiResponse { StatusCode = 200, Headers = H("Access-Control-Allow-Origin: https://app.example", "Access-Control-Allow-Credentials: true") };
        var star = new ApiResponse { StatusCode = 200, Headers = H("Access-Control-Allow-Origin: *") };
        var none = new ApiResponse { StatusCode = 200, Headers = H() };
        bool Passes(string expr, ApiResponse r) => AssertionEvaluator.Evaluate(new[] { AssertionParser.Parse(expr) }, r)[0].Passed;
        Assert.True(Passes("cors https://app.example credentials", echo));
        Assert.True(Passes("cors https://app.example allowed", echo));
        Assert.True(Passes("cors https://app.example allowed", star));
        Assert.False(Passes("cors https://app.example credentials", star));
        Assert.True(Passes("cors https://app.example blocked", none));
        Assert.True(Passes("cors https://other.example blocked", echo));
        var failed = AssertionEvaluator.Evaluate(new[] { AssertionParser.Parse("cors https://app.example credentials") }, star)[0];
        Assert.Contains("refuses for a request with credentials", failed.Actual);
        Assert.Equal("https://app.example", AssertionEvaluator.CorsOrigin(new[] { AssertionParser.Parse("cors https://app.example allowed") }, H()));
        Assert.Null(AssertionEvaluator.CorsOrigin(new[] { AssertionParser.Parse("cors https://app.example allowed") }, H("Origin: https://x.example")));
    }

    [Fact]
    public async Task Send_assert_cors_sends_the_origin_and_checks_the_answer()
    {
        await using var mock = MockServer.Start(0, MockTlsMode.Http, cors: new MockCors(new[] { "https://app.example" }));
        var ok = Cli("send", mock.BaseUrl + "x", "--assert", "cors https://app.example credentials", "-o", Path.GetTempFileName());
        Assert.True(ok.Code == 0, ok.Err);
        Assert.Contains("PASS  CORS for https://app.example == credentials", ok.Err);
        var blocked = Cli("send", mock.BaseUrl + "x", "--assert", "cors https://evil.example allowed", "-o", Path.GetTempFileName());
        Assert.NotEqual(0, blocked.Code);
        Assert.Contains("FAIL  CORS for https://evil.example", blocked.Err);
    }

    [Fact]
    public void Markdown_names_a_cors_assertion()
    {
        Assert.Equal("CORS for https://app.example == allowed", MarkdownExport.Describe(AssertionParser.Parse("cors https://app.example allowed")));
    }

    [Fact]
    public async Task The_mcp_tool_audits_and_refuses_a_redirect_off_the_allowlist()
    {
        await using var mock = MockServer.Start(0, MockTlsMode.Http, cors: new MockCors(null));
        var tools = McpCommand.BuildTools(null, new HostAllowlist(new[] { "127.0.0.1" }),
            insecure: false, timeout: 30, includeLocalMachine: false, workspace: null,
            noAutoToken: true, new CliServices { LiveStatePath = TempState() });
        var tool = tools.First(t => t.Name == "cors_check");
        using var audit = JsonDocument.Parse($$"""{"url":"{{mock.BaseUrl}}me","origin":"https://app.example.com","audit":true}""");
        var result = tool.Handler(audit.RootElement);
        Assert.False(result.IsError, result.Json);
        Assert.Equal("high", JsonDocument.Parse(result.Json).RootElement.GetProperty("worst").GetString());

        await using var away = new RawHttpServer(_ => RawHttpServer.Response(302, "Found", "",
            "Location: https://elsewhere.invalid/x", "Access-Control-Allow-Origin: https://app.example.com"));
        using var redirect = JsonDocument.Parse($$"""{"url":"{{away.BaseUrl}}/x","origin":"https://app.example.com"}""");
        var hop = JsonDocument.Parse(tool.Handler(redirect.RootElement).Json).RootElement;
        Assert.False(hop.GetProperty("allowed").GetBoolean());
        Assert.Contains(hop.GetProperty("problems").EnumerateArray(), p => p.GetString()!.Contains("elsewhere.invalid"));
    }

    [Fact]
    public void The_diagnostics_cors_lines_appear_only_for_a_request_with_an_Origin()
    {
        var response = new ApiResponse { StatusCode = 200, Headers = H("Access-Control-Allow-Origin: *", "X-Total: 1") };
        Assert.Null(CorsReport.For(H("Accept: */*"), response));
        string report = CorsReport.For(H("Origin: https://app.example"), response)!;
        Assert.Contains("CORS  (Origin: https://app.example)", report);
        Assert.Contains("✓ a page there can read this response", report);
        Assert.Contains("✗ not with credentials", report);
        Assert.Contains("hidden:   X-Total", report);
        Assert.Contains("✗ a page there cannot read it", CorsReport.For(H("Origin: https://app.example"), new ApiResponse { StatusCode = 200, Headers = H() })!);
    }

    // ---- doctor --origin ----

    [Fact]
    public async Task Doctor_origin_adds_a_cors_stage()
    {
        await using var allowing = MockServer.Start(0, MockTlsMode.Http, cors: new MockCors(new[] { "https://app.example" }));
        var ok = Cli("doctor", allowing.BaseUrl + "x", "--origin", "https://app.example", "--offline", "--json");
        var stages = JsonDocument.Parse(ok.Out).RootElement.GetProperty("stages").EnumerateArray().ToList();
        var cors = stages.Last();
        Assert.Equal("cors", cors.GetProperty("name").GetString());
        Assert.True(cors.GetProperty("ok").GetBoolean(), ok.Out);

        var blocked = Cli("doctor", allowing.BaseUrl + "x", "--origin", "https://evil.example", "--offline");
        Assert.Equal(1, blocked.Code);
        Assert.Contains("[FAIL] cors", blocked.Out);
    }
}
