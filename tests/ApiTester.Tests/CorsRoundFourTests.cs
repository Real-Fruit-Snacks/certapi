using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ApiTester.Cli;
using ApiTester.Cli.Commands;
using ApiTester.Cli.Mcp;
using ApiTester.Core;

namespace ApiTester.Tests;

/// <summary>CORS fourth round: Fetch spec edge cases (forbidden methods, 1024-byte safelist cap,
/// inverted Range, Max-Age &lt;= 0, preflight Vary, trailing-slash hint, cross-site Set-Cookie
/// SameSite/Secure/Partitioned, CORP/COEP/COOP, Timing-Allow-Origin), expanded --audit probes,
/// --diff, --from-openapi, --max-age + --suggest in --json + 8 new frameworks, cors exposes +
/// multi-origin assertions, run --cors-preflight, MCP parity, and new MockCorsFault modes.</summary>
public class CorsRoundFourTests
{
    private static List<KeyValuePair<string, string>> H(params string[] lines) =>
        lines.Select(l => { int c = l.IndexOf(':'); return new KeyValuePair<string, string>(l[..c].Trim(), l[(c + 1)..].Trim()); }).ToList();

    private static CorsRequest Req(string method = "GET", bool credentials = false, params string[] headers) =>
        new("https://app.example.com", method, H(headers), credentials);

    private static string TempState() => Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");

    private static (int Code, string Out, string Err) Cli(params string[] args)
    {
        var so = new StringWriter();
        var se = new StringWriter();
        int code = CliApp.Run(args.Append("--no-config").ToArray(), new StringReader(""), so, se, new MemoryStream(),
            new CliServices { LiveStatePath = TempState(), UserConfigPath = null });
        return (code, so.ToString(), se.ToString());
    }

    // ---- Fetch spec edge cases ----

    [Theory]
    [InlineData("CONNECT")]
    [InlineData("TRACE")]
    [InlineData("TRACK")]
    [InlineData("trace")]
    public async Task Forbidden_browser_methods_are_blocked_before_sending(string method)
    {
        Assert.True(CorsRules.ForbiddenMethod(method));
        bool sent = false;
        var res = await CorsProbe.RunAsync("https://api.example.com/x", Req(method), null, null, true,
            _ => { sent = true; return Task.FromResult(new ApiResponse { StatusCode = 200 }); },
            TimeSpan.FromSeconds(5));
        Assert.False(sent);
        Assert.False(res.Verdict.Allowed);
        Assert.Equal("forbidden browser method", res.NotSent);
        Assert.Contains(res.Verdict.Problems, p => p.Contains("forbidden method"));
    }

    [Fact]
    public void Method_override_to_forbidden_method_and_pna_header_are_forbidden()
    {
        Assert.True(CorsRules.Forbidden("Access-Control-Request-Private-Network"));
        Assert.True(CorsRules.Forbidden("X-HTTP-Method-Override", "TRACE"));
        Assert.True(CorsRules.Forbidden("X-Method-Override", "POST, CONNECT"));
        Assert.False(CorsRules.Forbidden("X-HTTP-Method-Override", "PUT"));
    }

    [Fact]
    public void Inverted_range_and_over_1024_byte_safelisted_headers_require_preflight()
    {
        Assert.True(CorsRules.SafelistedRequestHeader("Range", "bytes=0-100"));
        Assert.True(CorsRules.SafelistedRequestHeader("Range", "bytes=50-"));
        Assert.False(CorsRules.SafelistedRequestHeader("Range", "bytes=100-50"));
        Assert.Contains("Range", CorsRules.PreflightReason(Req("GET", false, "Range: bytes=100-50")));

        // 10 safelisted headers of 110 chars each = 1100 bytes > 1024-byte combined cap.
        string val = new string('a', 110);
        var many = Enumerable.Range(0, 10).Select(_ => new KeyValuePair<string, string>("Accept-Language", val)).ToList();
        var req = new CorsRequest("https://app.example.com", "GET", many, false);
        Assert.True(CorsRules.ExceedsSafelistedTotal(req));
        Assert.Contains("1024 bytes", CorsRules.PreflightReason(req));
    }

    [Fact]
    public void Trailing_slash_in_allow_origin_and_negative_max_age_and_preflight_vary_are_explained()
    {
        var (problems, _, _) = CorsRules.CheckPreflight(Req("PUT"), 204, H(
            "Access-Control-Allow-Origin: https://app.example.com/",
            "Access-Control-Allow-Methods: PUT"));
        Assert.Contains(problems, p => p.Contains("drop the trailing slash"));

        var (ok, _, notes) = CorsRules.CheckPreflight(Req("PUT", false, "X-Api-Key: k"), 204, H(
            "Access-Control-Allow-Origin: https://app.example.com",
            "Access-Control-Allow-Methods: PUT",
            "Access-Control-Allow-Headers: X-Api-Key",
            "Access-Control-Max-Age: -1",
            "Vary: Origin"));
        Assert.Empty(ok);
        Assert.Contains(notes, n => n.Contains("disables preflight caching"));
        Assert.Contains(notes, n => n.Contains("Access-Control-Request-Method") && n.Contains("Access-Control-Request-Headers"));

        var (_, _, positiveNotes) = CorsRules.CheckPreflight(Req("PUT"), 204, H(
            "Access-Control-Allow-Origin: https://app.example.com",
            "Access-Control-Allow-Methods: PUT",
            "Access-Control-Max-Age: 3600",
            "Vary: Origin, Access-Control-Request-Method"));
        Assert.Contains(positiveNotes, n => n.Contains("Safari caps at 10 min"));
        Assert.DoesNotContain(positiveNotes, n => n.Contains("preflight Vary omits"));
    }

    [Fact]
    public void Credentialed_set_cookie_checks_samesite_secure_and_partitioned()
    {
        var (_, _, missingSameSite, _, _) = CorsRules.CheckResponse(Req("GET", credentials: true), H(
            "Access-Control-Allow-Origin: https://app.example.com",
            "Access-Control-Allow-Credentials: true",
            "Vary: Origin",
            "Set-Cookie: sid=abc; HttpOnly"), "https://api.other.org/me");
        Assert.Contains(missingSameSite, n => n.Contains("sid") && n.Contains("SameSite=None; Secure"));

        var (_, _, noSecure, _, _) = CorsRules.CheckResponse(Req("GET", credentials: true), H(
            "Access-Control-Allow-Origin: https://app.example.com",
            "Access-Control-Allow-Credentials: true",
            "Vary: Origin",
            "Set-Cookie: sid=abc; SameSite=None"), "https://api.other.org/me");
        Assert.Contains(noSecure, n => n.Contains("SameSite=None without Secure"));

        var (_, _, noChips, _, _) = CorsRules.CheckResponse(Req("GET", credentials: true), H(
            "Access-Control-Allow-Origin: https://app.example.com",
            "Access-Control-Allow-Credentials: true",
            "Vary: Origin",
            "Set-Cookie: sid=abc; SameSite=None; Secure"), "https://api.other.org/me");
        Assert.Contains(noChips, n => n.Contains("Partitioned (CHIPS)"));

        // Same-site subdomain with Lax does not trigger cross-site SameSite=None warning.
        var (_, _, sameSiteNotes, _, _) = CorsRules.CheckResponse(Req("GET", credentials: true), H(
            "Access-Control-Allow-Origin: https://app.example.com",
            "Access-Control-Allow-Credentials: true",
            "Vary: Origin",
            "Set-Cookie: sid=abc; SameSite=Lax; Secure"), "https://api.example.com/me");
        Assert.DoesNotContain(sameSiteNotes, n => n.Contains("SameSite=None"));
    }

    [Fact]
    public void Corp_coep_coop_and_timing_allow_origin_are_reported()
    {
        var (_, passed, notes, _, _) = CorsRules.CheckResponse(Req(), H(
            "Access-Control-Allow-Origin: https://app.example.com",
            "Vary: Origin",
            "Cross-Origin-Resource-Policy: same-origin",
            "Cross-Origin-Embedder-Policy: require-corp",
            "Cross-Origin-Opener-Policy: same-origin",
            "Timing-Allow-Origin: https://other.example"), "https://api.other.org/x");
        Assert.Contains(notes, n => n.Contains("Cross-Origin-Resource-Policy: same-origin"));
        Assert.Contains(notes, n => n.Contains("Cross-Origin-Embedder-Policy: require-corp"));
        Assert.Contains(notes, n => n.Contains("Cross-Origin-Opener-Policy: same-origin"));
        Assert.Contains(notes, n => n.Contains("Timing-Allow-Origin") && n.Contains("zeroed"));

        var (_, passedCross, notesCross, _, _) = CorsRules.CheckResponse(Req(), H(
            "Access-Control-Allow-Origin: https://app.example.com",
            "Vary: Origin",
            "Cross-Origin-Resource-Policy: cross-origin",
            "Timing-Allow-Origin: *"), "https://api.other.org/x");
        Assert.Contains(passedCross, p => p.Contains("Cross-Origin-Resource-Policy: cross-origin"));
        Assert.Contains(notesCross, n => n.Contains("PerformanceResourceTiming metrics"));
    }

    // ---- Expanded --audit ----

    [Fact]
    public async Task Audit_catches_port_confusion_missing_vary_and_dev_localhost()
    {
        // Server compares hostname ignoring port, omits Vary: Origin, and allows http://localhost:3000.
        await using var leaky = new RawHttpServer(req =>
        {
            string origin = req.Header("Origin") ?? "";
            if (origin == "http://localhost:3000" ||
                (Uri.TryCreate(origin, UriKind.Absolute, out var u) && u.Host == "app.example.com" && u.Scheme == "https"))
                return RawHttpServer.Response(200, "OK", "{}",
                    "Access-Control-Allow-Origin: " + origin,
                    "Access-Control-Allow-Credentials: true");
            return RawHttpServer.Response(200, "OK", "{}");
        });

        var client = new ApiClient();
        var result = await CorsAudit.RunAsync(leaky.BaseUrl + "/me", new Uri("https://app.example.com"),
            Array.Empty<KeyValuePair<string, string>>(),
            r => client.SendAsync(r, null, new TransportOptions(), null),
            TimeSpan.FromSeconds(10), includeDevOrigins: true);

        Assert.Contains(result.Findings, f => f.Title.Contains("Non-standard ports"));
        Assert.Contains(result.Findings, f => f.Title.Contains("Localhost dev-server origin"));
        Assert.Contains(result.Findings, f => f.Title.Contains("without Vary: Origin"));
    }

    [Fact]
    public void Audit_probes_include_port_userinfo_underscore_backtick_and_optional_dev_origins()
    {
        var withDev = CorsAudit.ProbesFor(new Uri("https://app.example.com"), includeDevOrigins: true)
            .ToDictionary(p => p.Kind, p => p.Origin);
        Assert.Equal("https://app.example.com:8443", withDev["port look-alike"]);
        Assert.Contains("@certapi-cors-audit.example", withDev["userinfo look-alike"]);
        Assert.Contains("_.certapi-cors-audit.example", withDev["underscore look-alike"]);
        Assert.Contains("%60.certapi-cors-audit.example", withDev["backtick look-alike"]);
        Assert.Equal("http://localhost", withDev["localhost origin"]);
        Assert.Equal("http://localhost:3000", withDev["localhost dev port"]);
    }

    // ---- --suggest frameworks, --max-age, and --json ----

    [Fact]
    public void Suggest_supports_all_new_frameworks_and_custom_max_age()
    {
        var needs = new CorsNeeds(new[] { "https://app.example.com" }, new[] { "GET", "PUT" },
            new[] { "x-api-key" }, new[] { "X-Request-Id" }, true, true);
        Assert.Contains("<cors allow-credentials=\"true\">", CorsSuggest.For(needs, "apim", 1800));
        Assert.Contains("preflight-result-max-age=\"1800\"", CorsSuggest.For(needs, "apim", 1800));
        Assert.Contains("MapReverseProxy()", CorsSuggest.For(needs, "yarp", 900));
        Assert.Contains("Cloudflare Worker", CorsSuggest.For(needs, "cloudflare", 900));
        Assert.Contains("Caddyfile", CorsSuggest.For(needs, "caddy", 900));
        Assert.Contains("accessControlMaxAge: 900", CorsSuggest.For(needs, "traefik", 900));
        Assert.Contains("SetEnvIf Origin", CorsSuggest.For(needs, "apache", 900));
        Assert.Contains("CORSMiddleware", CorsSuggest.For(needs, "fastapi", 900));
        Assert.Contains("rs/cors", CorsSuggest.For(needs, "go", 900));
    }

    [Fact]
    public async Task Cors_suggest_is_included_in_json_output_with_custom_max_age()
    {
        await using var mock = MockServer.Start(0, MockTlsMode.Http, cors: new MockCors(new[] { "https://app.example.com" }));
        var r = Cli("cors", mock.BaseUrl + "orders", "--origin", "https://app.example.com",
            "-X", "PUT", "--suggest", "apim", "--max-age", "3600", "--json");
        Assert.Equal(0, r.Code);
        var j = JsonDocument.Parse(r.Out).RootElement;
        string suggestion = j.GetProperty("suggestion").GetString()!;
        Assert.Contains("Azure API Management", suggestion);
        Assert.Contains("3600", suggestion);
    }

    // ---- --diff ----

    [Fact]
    public async Task Cors_diff_detects_matching_and_diverging_endpoints()
    {
        await using var left = MockServer.Start(0, MockTlsMode.Http, cors: new MockCors(new[] { "https://app.example.com" }));
        await using var same = MockServer.Start(0, MockTlsMode.Http, cors: new MockCors(new[] { "https://app.example.com" }));
        await using var diff = MockServer.Start(0, MockTlsMode.Http, cors: new MockCors(new[] { "https://other.example.com" }));

        var ok = Cli("cors", left.BaseUrl + "orders", "--diff", same.BaseUrl + "orders",
            "--origin", "https://app.example.com", "-X", "GET,PUT", "--json");
        Assert.Equal(0, ok.Code);
        Assert.True(JsonDocument.Parse(ok.Out).RootElement.GetProperty("identical").GetBoolean());

        var changed = Cli("cors", left.BaseUrl + "orders", "--diff", diff.BaseUrl + "orders",
            "--origin", "https://app.example.com", "-X", "GET", "--json");
        Assert.Equal(1, changed.Code);
        var cj = JsonDocument.Parse(changed.Out).RootElement;
        Assert.False(cj.GetProperty("identical").GetBoolean());
        Assert.True(cj.GetProperty("differences").GetArrayLength() > 0);
    }

    // ---- --from-openapi ----

    [Fact]
    public async Task Cors_from_openapi_sweeps_every_operation()
    {
        await using var mock = MockServer.Start(0, MockTlsMode.Http, cors: new MockCors(new[] { "https://app.example.com" }));
        string specFile = Path.Combine(Path.GetTempPath(), $"openapi-cors-{Guid.NewGuid():N}.json");
        File.WriteAllText(specFile, $$"""
            {
              "openapi": "3.0.3",
              "info": { "title": "Orders", "version": "1.0" },
              "servers": [{ "url": "https://prod.invalid" }],
              "paths": {
                "/orders": {
                  "get": { "responses": { "200": { "description": "ok" } } },
                  "post": { "responses": { "201": { "description": "created" } } }
                },
                "/orders/{id}": {
                  "delete": { "responses": { "204": { "description": "deleted" } } }
                }
              }
            }
            """);
        try
        {
            var ok = Cli("cors", "--from-openapi", specFile, "--base-url", mock.BaseUrl,
                "--origin", "https://app.example.com", "--json");
            Assert.Equal(0, ok.Code);
            var checks = JsonDocument.Parse(ok.Out).RootElement.GetProperty("checks");
            Assert.Equal(3, checks.GetArrayLength());
            Assert.All(checks.EnumerateArray(), c => Assert.True(c.GetProperty("allowed").GetBoolean()));

            var blocked = Cli("cors", "--from-openapi", specFile, "--base-url", mock.BaseUrl,
                "--origin", "https://evil.example");
            Assert.Equal(1, blocked.Code);
            Assert.Contains("0 of 3 operation check(s) allowed", blocked.Out);
        }
        finally { File.Delete(specFile); }
    }

    // ---- Assertions: cors exposes + multi-origin ----

    [Fact]
    public void Cors_exposes_assertion_parses_and_evaluates()
    {
        var rule = AssertionParser.Parse("cors https://app.example.com exposes X-Correlation-Id");
        Assert.Equal(AssertTarget.Cors, rule.Target);
        Assert.Equal("exposes X-Correlation-Id", rule.Value);

        var notRule = AssertionParser.Parse("cors https://app.example.com !exposes X-Secret");
        Assert.Equal(AssertOp.NotEquals, notRule.Op);
        Assert.Equal("exposes X-Secret", notRule.Value);

        var exposedResp = new ApiResponse
        {
            StatusCode = 200,
            Headers = H("Access-Control-Allow-Origin: https://app.example.com",
                        "Access-Control-Expose-Headers: X-Correlation-Id",
                        "X-Correlation-Id: 42",
                        "X-Secret: top")
        };
        Assert.True(AssertionEvaluator.Evaluate(new[] { rule }, exposedResp)[0].Passed);
        Assert.True(AssertionEvaluator.Evaluate(new[] { notRule }, exposedResp)[0].Passed);

        var hiddenResp = new ApiResponse
        {
            StatusCode = 200,
            Headers = H("Access-Control-Allow-Origin: https://app.example.com", "X-Correlation-Id: 42")
        };
        var failed = AssertionEvaluator.Evaluate(new[] { rule }, hiddenResp)[0];
        Assert.False(failed.Passed);
        Assert.Contains("hidden", failed.Actual);
    }

    [Fact]
    public async Task Multi_origin_cors_assertions_catch_a_reflecting_server()
    {
        // Reflecting server echoes any origin: sending only app.example would falsely pass evil.example blocked.
        await using var reflecting = MockServer.Start(0, MockTlsMode.Http, cors: new MockCors(null));
        var r = Cli("send", reflecting.BaseUrl + "x",
            "--assert", "cors https://app.example.com credentials",
            "--assert", "cors https://evil.example blocked",
            "-o", Path.GetTempFileName());
        Assert.NotEqual(0, r.Code);
        Assert.Contains("PASS  CORS for https://app.example.com == credentials", r.Err);
        Assert.Contains("FAIL  CORS for https://evil.example == blocked", r.Err);

        await using var strict = MockServer.Start(0, MockTlsMode.Http, cors: new MockCors(new[] { "https://app.example.com" }));
        var ok = Cli("send", strict.BaseUrl + "x",
            "--assert", "cors https://app.example.com credentials",
            "--assert", "cors https://evil.example blocked",
            "-o", Path.GetTempFileName());
        Assert.True(ok.Code == 0, ok.Err);
    }

    // ---- run --origin --cors-preflight ----

    [Fact]
    public async Task Run_cors_preflight_checks_preflights_for_non_simple_requests()
    {
        await using var brokenPreflight = MockServer.Start(0, MockTlsMode.Http,
            cors: new MockCors(new[] { "https://app.example.com" }) { Fault = MockCorsFault.PreflightUnauthorized });
        var state = new AppState();
        var folder = new CollectionNode { Name = "suite", IsFolder = true };
        folder.Children.Add(new CollectionNode
        {
            Name = "update",
            Request = new RequestModel { Method = "PUT", Path = brokenPreflight.BaseUrl + "item" }
        });
        state.Collections.Add(folder);
        string ws = Path.Combine(Path.GetTempPath(), $"certapi-pf-{Guid.NewGuid():N}.json");
        state.SaveTo(ws);
        try
        {
            var r = Cli("run", "suite", "--workspace", ws, "--origin", "https://app.example.com", "--cors-preflight");
            Assert.Equal(1, r.Code);
            Assert.Contains("CORS preflight for https://app.example.com", r.Out + r.Err);
        }
        finally { File.Delete(ws); }
    }

    // ---- New MockCorsFault modes ----

    [Fact]
    public void New_mock_cors_faults_produce_expected_broken_headers()
    {
        var starCreds = new MockCors(null) { Fault = MockCorsFault.StarWithCredentials };
        var (_, scH) = starCreds.Preflight("https://app.example.com", "PUT", null);
        Assert.Contains(scH, h => h.Key == "Access-Control-Allow-Origin" && h.Value == "*");
        Assert.Contains(scH, h => h.Key == "Access-Control-Allow-Credentials" && h.Value == "true");

        var nullOrigin = new MockCors(null) { Fault = MockCorsFault.NullOrigin };
        var (_, noH) = nullOrigin.Preflight("https://app.example.com", "PUT", null);
        Assert.Contains(noH, h => h.Key == "Access-Control-Allow-Origin" && h.Value == "null");

        var badMethods = new MockCors(null) { Fault = MockCorsFault.BadMethods };
        var (_, bmH) = badMethods.Preflight("https://app.example.com", "DELETE", null);
        Assert.Contains(bmH, h => h.Key == "Access-Control-Allow-Methods" && h.Value == "GET");

        var badHeaders = new MockCors(null) { Fault = MockCorsFault.BadHeaders };
        var (_, bhH) = badHeaders.Preflight("https://app.example.com", "PUT", "x-api-key");
        Assert.DoesNotContain(bhH, h => h.Key == "Access-Control-Allow-Headers");

        var noPna = new MockCors(null) { Fault = MockCorsFault.PreflightNoPna };
        var (_, pnaH) = noPna.Preflight("https://app.example.com", "GET", null, privateNetwork: true);
        Assert.DoesNotContain(pnaH, h => h.Key == "Access-Control-Allow-Private-Network");

        var reqCert = new MockCors(null) { Fault = MockCorsFault.PreflightRequireCert };
        Assert.Equal(403, reqCert.Preflight("https://app.example.com", "PUT", null, hasClientCert: false).Status);
        Assert.Equal(204, reqCert.Preflight("https://app.example.com", "PUT", null, hasClientCert: true).Status);

        var missExpose = new MockCors(null) { Fault = MockCorsFault.MissingExpose };
        string rewritten = missExpose.Rewrite("HTTP/1.1 200 OK\r\nX-Custom: 1\r\nConnection: close\r\n\r\n", "https://app.example.com");
        Assert.DoesNotContain("Access-Control-Expose-Headers", rewritten);
    }

    // ---- MCP cors_check parity ----

    [Fact]
    public async Task Mcp_cors_check_supports_matrix_suggest_diff_and_websockets()
    {
        await using var left = MockServer.Start(0, MockTlsMode.Http, cors: new MockCors(new[] { "https://app.example.com" }));
        await using var right = MockServer.Start(0, MockTlsMode.Http, cors: new MockCors(new[] { "https://other.example.com" }));
        var tools = McpCommand.BuildTools(null, new HostAllowlist(new[] { "127.0.0.1" }),
            insecure: false, timeout: 30, includeLocalMachine: false, workspace: null,
            noAutoToken: true, new CliServices { LiveStatePath = TempState() });
        var tool = tools.First(t => t.Name == "cors_check");

        using var matrixArgs = JsonDocument.Parse($$"""
            {"url":"{{left.BaseUrl}}orders","origins":["https://app.example.com","https://evil.example"],"methods":["GET","PUT"],"suggest":"apim","maxAge":1200}
            """);
        var matrixRes = tool.Handler(matrixArgs.RootElement);
        Assert.False(matrixRes.IsError, matrixRes.Json);
        var mj = JsonDocument.Parse(matrixRes.Json).RootElement;
        Assert.Equal(4, mj.GetProperty("results").GetArrayLength());
        Assert.Contains("1200", mj.GetProperty("suggestion").GetString());

        using var diffArgs = JsonDocument.Parse($$"""
            {"url":"{{left.BaseUrl}}orders","diffUrl":"{{right.BaseUrl}}orders","origin":"https://app.example.com"}
            """);
        var diffRes = tool.Handler(diffArgs.RootElement);
        Assert.False(diffRes.IsError, diffRes.Json);
        Assert.False(JsonDocument.Parse(diffRes.Json).RootElement.GetProperty("identical").GetBoolean());

        // WebSocket via MockServer's built-in upgrade endpoint
        string wsUrl = left.BaseUrl.Replace("http://", "ws://") + "ws";
        using var wsArgs = JsonDocument.Parse($$"""{"url":"{{wsUrl}}","origin":"https://app.example.com"}""");
        var wsRes = tool.Handler(wsArgs.RootElement);
        Assert.False(wsRes.IsError, wsRes.Json);
        Assert.True(JsonDocument.Parse(wsRes.Json).RootElement.GetProperty("probes")[0].GetProperty("accepted").GetBoolean());
    }

    // ---- Tab completion choices ----

    [Fact]
    public void Completion_offers_suggest_frameworks_and_mock_cors_faults()
    {
        var services = new CliServices { LiveStatePath = TempState() };
        var frameworks = CompletionCommand.Complete(new[] { "cors", "https://api.example", "--suggest", "" }, services).ToList();
        Assert.Contains("apim", frameworks);
        Assert.Contains("yarp", frameworks);
        Assert.Contains("cloudflare", frameworks);

        var faults = CompletionCommand.Complete(new[] { "mock", "--cors", "--cors-fault", "" }, services).ToList();
        Assert.Contains("star-with-credentials", faults);
        Assert.Contains("preflight-require-cert", faults);
    }
}
