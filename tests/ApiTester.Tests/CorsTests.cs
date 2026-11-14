using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using ApiTester.Cli;
using ApiTester.Cli.Commands;
using ApiTester.Cli.Mcp;
using ApiTester.Core;

namespace ApiTester.Tests;

/// <summary>The CORS additions: the Fetch standard's rules as functions, `certapi cors` against a
/// mock with a real policy, `send --origin`, `mock --cors`, `serve --cors-headers`/`--cors-expose`
/// and the MCP `cors_check` tool.</summary>
public class CorsTests
{
    private static List<KeyValuePair<string, string>> H(params string[] lines) =>
        lines.Select(l => { int c = l.IndexOf(':'); return new KeyValuePair<string, string>(l[..c].Trim(), l[(c + 1)..].Trim()); }).ToList();

    private static CorsRequest Req(string method = "GET", bool credentials = false, params string[] headers) =>
        new("https://app.example", method, H(headers), credentials);

    // ---- when a preflight is needed ----

    [Fact]
    public void A_simple_request_needs_no_preflight()
    {
        Assert.Null(CorsRules.PreflightReason(Req()));
        Assert.Null(CorsRules.PreflightReason(Req("POST", false, "Content-Type: application/x-www-form-urlencoded", "Accept: */*")));
        Assert.Null(CorsRules.PreflightReason(Req("GET", false, "Range: bytes=0-99", "Cookie: a=1", "Accept-Language: en-GB,en;q=0.9")));
    }

    [Fact]
    public void An_unsafe_method_a_custom_header_or_a_json_body_needs_a_preflight()
    {
        Assert.Contains("PUT", CorsRules.PreflightReason(Req("PUT")));
        Assert.Contains("X-Api-Key", CorsRules.PreflightReason(Req("GET", false, "X-Api-Key: k")));
        Assert.Contains("application/json", CorsRules.PreflightReason(Req("POST", false, "Content-Type: application/json")));
        Assert.Contains("Authorization", CorsRules.PreflightReason(Req("GET", false, "Authorization: Bearer t")));
    }

    [Fact]
    public void A_preflight_carries_the_method_and_the_sorted_lower_case_header_names_only()
    {
        var headers = CorsRules.PreflightHeaders(Req("PATCH", false, "X-B: 1", "Content-Type: application/json", "X-A: 2", "Accept: */*"));
        Assert.Equal(new[]
        {
            new KeyValuePair<string, string>("Origin", "https://app.example"),
            new KeyValuePair<string, string>("Access-Control-Request-Method", "PATCH"),
            new KeyValuePair<string, string>("Access-Control-Request-Headers", "content-type,x-a,x-b")
        }, headers);
    }

    // ---- judging a preflight ----

    [Fact]
    public void A_good_preflight_passes_every_rule()
    {
        var (problems, passed, _) = CorsRules.CheckPreflight(Req("PUT", true, "X-Api-Key: k"), 204, H(
            "Access-Control-Allow-Origin: https://app.example", "Access-Control-Allow-Credentials: true",
            "Access-Control-Allow-Methods: GET, PUT", "Access-Control-Allow-Headers: X-Api-Key", "Vary: Origin"));
        Assert.Empty(problems);
        Assert.Contains(passed, p => p.Contains("PUT"));
        Assert.Contains(passed, p => p.Contains("x-api-key"));
    }

    [Theory]
    [InlineData(401, "Access-Control-Allow-Origin: https://app.example", "2xx")]
    [InlineData(204, "Access-Control-Allow-Origin: https://other.example", "other.example")]
    [InlineData(204, "Vary: Origin", "no Access-Control-Allow-Origin")]
    public void A_preflight_with_a_bad_status_or_origin_fails(int status, string header, string expected)
    {
        var (problems, _, _) = CorsRules.CheckPreflight(Req("DELETE"), status, H(header, "Access-Control-Allow-Methods: DELETE"));
        Assert.Contains(problems, p => p.Contains(expected));
    }

    [Fact]
    public void Wildcards_do_not_count_with_credentials_and_never_cover_Authorization()
    {
        var wild = H("Access-Control-Allow-Origin: *", "Access-Control-Allow-Methods: *", "Access-Control-Allow-Headers: *");
        var (anonymous, _, _) = CorsRules.CheckPreflight(Req("PUT", false, "X-A: 1"), 204, wild);
        Assert.Empty(anonymous);

        var (credentialed, _, _) = CorsRules.CheckPreflight(Req("PUT", true, "X-A: 1"), 204, wild);
        Assert.Contains(credentialed, p => p.Contains("Access-Control-Allow-Origin: *"));
        Assert.Contains(credentialed, p => p.Contains("method PUT"));
        Assert.Contains(credentialed, p => p.Contains("x-a"));

        var (auth, _, _) = CorsRules.CheckPreflight(Req("GET", false, "Authorization: Bearer t"), 204, wild);
        Assert.Contains(auth, p => p.Contains("authorization") && p.Contains("never covers Authorization"));
    }

    [Fact]
    public void A_credentialed_answer_needs_Allow_Credentials_true()
    {
        var (problems, _, _, _, _) = CorsRules.CheckResponse(Req("GET", true), H("Access-Control-Allow-Origin: https://app.example"));
        Assert.Contains(problems, p => p.Contains("Access-Control-Allow-Credentials: true"));
    }

    [Fact]
    public void Several_origins_in_one_answer_fail_and_an_echo_without_Vary_is_noted()
    {
        var (problems, _, _, _, _) = CorsRules.CheckResponse(Req(), H("Access-Control-Allow-Origin: https://app.example, https://b.example"));
        Assert.Contains(problems, p => p.Contains("exactly one"));
        var (ok, _, notes, _, _) = CorsRules.CheckResponse(Req(), H("Access-Control-Allow-Origin: https://app.example"));
        Assert.Empty(ok);
        Assert.Contains(notes, n => n.Contains("Vary: Origin"));
    }

    [Fact]
    public void Only_safelisted_and_exposed_headers_are_readable()
    {
        var (_, _, _, readable, hidden) = CorsRules.CheckResponse(Req(), H(
            "Access-Control-Allow-Origin: *", "Access-Control-Expose-Headers: X-Total",
            "Content-Type: application/json", "X-Total: 5", "X-Request-Id: r", "Set-Cookie: a=1"));
        Assert.Equal(new[] { "Content-Type", "X-Total" }, readable);
        Assert.Contains("X-Request-Id", hidden);
        Assert.Contains("Set-Cookie", hidden);
    }

    // ---- mock --cors ----

    [Fact]
    public void Mock_cors_answers_a_preflight_for_an_allowed_origin_and_refuses_another()
    {
        var policy = new MockCors(new[] { "https://app.example" });
        var (status, headers) = policy.Preflight("https://app.example", "PUT", "x-a");
        Assert.Equal(204, status);
        Assert.Contains(headers, h => h.Key == "Access-Control-Allow-Headers" && h.Value == "x-a");
        Assert.Equal(403, policy.Preflight("https://evil.example", "PUT", null).Status);

        string head = "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nX-Total: 5\r\nAccess-Control-Allow-Origin: *\r\nConnection: close\r\n\r\n";
        string allowed = policy.Rewrite(head, "https://app.example");
        Assert.Contains("Access-Control-Allow-Origin: https://app.example\r\n", allowed);
        Assert.Contains("Access-Control-Expose-Headers: X-Total", allowed);
        Assert.DoesNotContain("Access-Control-Allow-Origin: *", allowed);
        Assert.EndsWith("\r\n\r\n", allowed);
        Assert.DoesNotContain("Access-Control-", policy.Rewrite(head, "https://evil.example"));
    }

    private static string TempState() => Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");

    private static (int Code, string Out, string Err) Cli(params string[] args)
    {
        var so = new StringWriter();
        var se = new StringWriter();
        int code = CliApp.Run(args.Append("--no-config").ToArray(), so, se, new MemoryStream(),
            new CliServices { LiveStatePath = TempState(), UserConfigPath = null });
        return (code, so.ToString(), se.ToString());
    }

    [Fact]
    public async Task Cors_command_passes_an_allowed_origin_through_a_preflight()
    {
        await using var mock = MockServer.Start(0, MockTlsMode.Http, cors: new MockCors(new[] { "https://app.example" }));
        var r = Cli("cors", mock.BaseUrl + "orders", "--origin", "https://app.example", "-X", "PUT", "-H", "X-Api-Key: k",
            "--credentials", "--actual");
        Assert.True(r.Code == 0, r.Out + r.Err);
        Assert.Contains("needed because the method is PUT", r.Out);
        Assert.Contains("verdict    allowed", r.Out);

        var preflightOnly = Cli("cors", mock.BaseUrl + "orders", "--origin", "https://app.example", "-X", "PUT");
        Assert.Equal(0, preflightOnly.Code);
        Assert.Contains("verdict    the preflight allows it", preflightOnly.Out);
    }

    [Fact]
    public async Task Cors_command_blocks_an_origin_the_policy_does_not_allow()
    {
        await using var mock = MockServer.Start(0, MockTlsMode.Http, cors: new MockCors(new[] { "https://app.example" }));
        var preflighted = Cli("cors", mock.BaseUrl + "orders", "--origin", "https://evil.example", "-X", "DELETE");
        Assert.Equal(1, preflighted.Code);
        Assert.Contains("403", preflighted.Out);
        Assert.Contains("verdict    blocked", preflighted.Out);

        var simple = Cli("cors", mock.BaseUrl + "orders", "--origin", "https://evil.example", "--json");
        Assert.Equal(1, simple.Code);
        var j = JsonDocument.Parse(simple.Out).RootElement;
        Assert.False(j.GetProperty("allowed").GetBoolean());
        Assert.False(j.GetProperty("preflightNeeded").GetBoolean());
        Assert.Contains("no Access-Control-Allow-Origin", j.GetProperty("problems")[0].GetString());
    }

    [Fact]
    public async Task Cors_command_does_not_send_an_unsafe_request_unless_asked()
    {
        await using var mock = MockServer.Start(0, MockTlsMode.Http, cors: new MockCors(null));
        var r = Cli("cors", mock.BaseUrl + "orders/7", "--origin", "http://localhost:3000", "-X", "DELETE", "--json");
        Assert.Equal(0, r.Code);
        var j = JsonDocument.Parse(r.Out).RootElement;
        Assert.Contains("DELETE", j.GetProperty("notSent").GetString());
        Assert.Equal(JsonValueKind.Null, j.GetProperty("response").ValueKind);
    }

    [Fact]
    public async Task The_star_answer_of_the_plain_mock_fails_only_with_credentials()
    {
        await using var mock = MockServer.Start(0, MockTlsMode.Http);
        Assert.Equal(0, Cli("cors", mock.BaseUrl, "--origin", "https://app.example").Code);
        var credentialed = Cli("cors", mock.BaseUrl, "--origin", "https://app.example", "--credentials");
        Assert.Equal(1, credentialed.Code);
        Assert.Contains("refuses for a request with credentials", credentialed.Out);
    }

    [Theory]
    [InlineData("--origin", "app.example")]
    [InlineData("-X", "PUT")]
    public void Cors_argument_mistakes_are_usage_errors(string flag, string value)
    {
        var args = flag == "--origin"
            ? new[] { "cors", "http://127.0.0.1:9/", flag, value }
            : new[] { "cors", "http://127.0.0.1:9/", flag, value, "--actual", "--preflight-only", "--origin", "https://a.example" };
        Assert.Equal(2, Cli(args).Code);
        Assert.Equal(2, Cli("cors", "http://127.0.0.1:9/").Code);   // no --origin
    }

    [Fact]
    public void An_origin_typed_with_a_path_is_trimmed_to_the_origin()
    {
        Assert.Equal("https://app.example:8443", CorsCommand.NormalizeOrigin("https://app.example:8443/some/page"));
        Assert.Equal("null", CorsCommand.NormalizeOrigin("null"));
    }

    // ---- send --origin ----

    [Fact]
    public async Task Send_origin_sends_the_header_and_says_what_a_browser_would_allow()
    {
        await using var mock = MockServer.Start(0, MockTlsMode.Http);
        var so = new StringWriter();
        var se = new StringWriter();
        var body = new MemoryStream();
        int code = CliApp.Run(new[] { "send", mock.BaseUrl + "echo", "--origin", "https://app.example/", "--no-config" }, so, se, body,
            new CliServices { LiveStatePath = TempState(), UserConfigPath = null });
        Assert.Equal(0, code);
        var echo = JsonDocument.Parse(Encoding.UTF8.GetString(body.ToArray())).RootElement;
        Assert.Equal("https://app.example", echo.GetProperty("headers").GetProperty("Origin").GetString());
        Assert.Contains("cors: a page on https://app.example can read this response without credentials, not with them", se.ToString());
    }

    // ---- serve --cors-headers / --cors-expose ----

    [Fact]
    public void A_fixed_allow_list_answers_every_preflight_the_same_and_expose_limits_what_is_readable()
    {
        var options = new BrowserOptions(true, null, false, false, false, new Uri("http://127.0.0.1:8819"))
        {
            CorsAllowHeaders = new[] { "Content-Type", "X-Api-Key" },
            CorsExposeHeaders = new[] { "X-Total" }
        };
        var preflight = BrowserRewriter.TryPreflight(new GatewayRequest("OPTIONS", "/x",
            H("Origin: http://localhost:3000", "Access-Control-Request-Method: PUT", "Access-Control-Request-Headers: x-other"), null, null), options);
        Assert.NotNull(preflight);
        Assert.Contains(preflight!.Headers, h => h.Key == "Access-Control-Allow-Headers" && h.Value == "Content-Type, X-Api-Key");
        var problems = CorsRules.CheckPreflight(
            new CorsRequest("http://localhost:3000", "PUT", H("X-Other: 1"), true), preflight.StatusCode, preflight.Headers).Problems;
        Assert.Contains(problems, p => p.Contains("x-other"));

        var response = BrowserRewriter.RewriteResponseHeaders(H("Content-Type: text/plain", "X-Total: 5", "X-Secret: s"),
            "http://localhost:3000", new GatewayRoute("/", new Uri("https://api.internal/")), options, out _);
        Assert.Contains(response, h => h.Key == "Access-Control-Expose-Headers" && h.Value == "X-Total");
        var (_, _, _, readable, hidden) = CorsRules.CheckResponse(new CorsRequest("http://localhost:3000", "GET", H(), true), response);
        Assert.Contains("X-Total", readable);
        Assert.Contains("X-Secret", hidden);
    }

    [Fact]
    public void Serve_refuses_cors_headers_without_cors()
    {
        var r = Cli("serve", "https://example.invalid", "--port", "8819", "--cors-headers", "X-A", "--check");
        Assert.Equal(2, r.Code);
        Assert.Contains("--cors", r.Err);
    }

    // ---- MCP cors_check ----

    [Fact]
    public async Task The_mcp_cors_check_tool_returns_the_verdict_for_an_allowed_host()
    {
        await using var mock = MockServer.Start(0, MockTlsMode.Http, cors: new MockCors(new[] { "https://app.example" }));
        var tools = McpCommand.BuildTools(null, new HostAllowlist(new[] { "127.0.0.1" }),
            insecure: false, timeout: 30, includeLocalMachine: false, workspace: null,
            noAutoToken: true, new CliServices { LiveStatePath = TempState() });
        var tool = tools.First(t => t.Name == "cors_check");
        using var args = JsonDocument.Parse($$"""{"url":"{{mock.BaseUrl}}x","origin":"https://app.example","method":"PUT","credentials":true}""");
        var result = tool.Handler(args.RootElement);
        Assert.False(result.IsError, result.Json);
        var j = JsonDocument.Parse(result.Json).RootElement;
        Assert.True(j.GetProperty("allowed").GetBoolean());
        Assert.True(j.GetProperty("preflightNeeded").GetBoolean());

        using var off = JsonDocument.Parse("""{"url":"https://evil.example/x","origin":"https://app.example"}""");
        Assert.True(tool.Handler(off.RootElement).IsError);
    }
}
