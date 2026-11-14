using System.IO;
using System.Net.Http;
using System.Text.Json;
using ApiTester.Cli;
using ApiTester.Core;

namespace ApiTester.Tests;

/// <summary>The CORS knobs on `mock` and `serve`: fixed lists, star, deliberate faults, a policy
/// per scenario route, the gateway's methods and no-credentials options, the profile keys, and
/// the note when an upstream answers CORS itself.</summary>
public class CorsMockServeTests
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

    private static HttpClient Client() => new(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false });

    private static async Task<HttpResponseMessage> Preflight(HttpClient http, string url, string origin, string method, string? headers = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Options, url);
        request.Headers.TryAddWithoutValidation("Origin", origin);
        request.Headers.TryAddWithoutValidation("Access-Control-Request-Method", method);
        if (headers is not null) request.Headers.TryAddWithoutValidation("Access-Control-Request-Headers", headers);
        return await http.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> Get(HttpClient http, string url, string origin)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Origin", origin);
        return await http.SendAsync(request);
    }

    private static string? One(HttpResponseMessage r, string name) =>
        r.Headers.TryGetValues(name, out var v) ? string.Join(", ", v) :
        r.Content.Headers.TryGetValues(name, out var c) ? string.Join(", ", c) : null;

    private static int Count(HttpResponseMessage r, string name) =>
        r.Headers.TryGetValues(name, out var v) ? v.Count() : 0;

    // ---- mock policy ----

    [Fact]
    public void Fixed_lists_and_max_age_shape_the_preflight_and_the_response()
    {
        var cors = new MockCors(null)
        {
            AllowHeaders = new[] { "Content-Type" }, AllowMethods = new[] { "GET", "POST" },
            Expose = new[] { "X-Total" }, MaxAge = 30
        };
        var (status, headers) = cors.Preflight("https://app.example", "PUT", "x-api-key");
        Assert.Equal(204, status);
        Assert.Contains(headers, h => h.Key == "Access-Control-Allow-Methods" && h.Value == "GET, POST");
        Assert.Contains(headers, h => h.Key == "Access-Control-Allow-Headers" && h.Value == "Content-Type");
        Assert.Contains(headers, h => h.Key == "Access-Control-Max-Age" && h.Value == "30");
        var problems = CorsRules.CheckPreflight(new CorsRequest("https://app.example", "PUT", H("X-Api-Key: k"), true), status, headers).Problems;
        Assert.Contains(problems, p => p.Contains("PUT"));
        Assert.Contains(problems, p => p.Contains("x-api-key", StringComparison.OrdinalIgnoreCase));

        string head = cors.Rewrite("HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nX-Total: 4\r\nX-Secret: s\r\n\r\n", "https://app.example");
        Assert.Contains("Access-Control-Expose-Headers: X-Total\r\n", head);
    }

    [Fact]
    public void Star_answers_a_wildcard_without_credentials_which_a_credentialed_request_fails()
    {
        var cors = new MockCors(null) { Star = true };
        string head = cors.Rewrite("HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\n\r\n", "https://app.example");
        Assert.Contains("Access-Control-Allow-Origin: *\r\n", head);
        Assert.DoesNotContain("Allow-Credentials", head);
        var headers = H(head.Split("\r\n").Skip(1).Where(l => l.Contains(':')).ToArray());
        Assert.NotEmpty(CorsRules.CheckResponse(new CorsRequest("https://app.example", "GET", H(), true), headers).Problems);
        Assert.Empty(CorsRules.CheckResponse(new CorsRequest("https://app.example", "GET", H(), false), headers).Problems);
    }

    [Theory]
    [InlineData("double-origin")]
    [InlineData("preflight-401")]
    [InlineData("preflight-redirect")]
    [InlineData("preflight-only")]
    [InlineData("no-vary")]
    public async Task Each_deliberate_fault_is_caught_by_the_cors_check(string fault)
    {
        Assert.True(MockCors.TryParseFault(fault, out var parsed));
        await using var mock = MockServer.Start(0, MockTlsMode.Http, cors: new MockCors(null) { Fault = parsed });
        // A preflight is forced with a custom header, so the preflight faults are reached too.
        var r = Cli("cors", mock.BaseUrl + "x", "--origin", "https://app.example", "-H", "X-Api-Key: k", "--json");
        var j = JsonDocument.Parse(r.Out).RootElement;
        if (fault == "no-vary")
        {
            // Not a block — a caching hazard, which the check reports as a note.
            Assert.Contains(j.GetProperty("notes").EnumerateArray(), n => n.GetString()!.Contains("Vary"));
            return;
        }
        Assert.Equal(1, r.Code);
        Assert.False(j.GetProperty("allowed").GetBoolean());
    }

    [Fact]
    public async Task A_route_can_turn_cors_off_or_carry_its_own_policy()
    {
        var scenario = MockScenario.Parse("""
            {
              "routes": [
                { "match": { "path": "/public/**" }, "cors": { "star": true }, "respond": { "status": 200, "body": "pub" } },
                { "match": { "path": "/internal" }, "cors": false, "respond": { "status": 200, "body": "no" } },
                { "match": { "method": "PUT", "path": "/admin" }, "cors": { "origins": ["https://admin.example"], "methods": ["PUT"] },
                  "respond": { "status": 204 } },
                { "match": { "path": "/plain" }, "respond": { "status": 200, "body": "plain" } }
              ]
            }
            """);
        Assert.Empty(scenario.Warnings);
        await using var mock = MockServer.Start(0, MockTlsMode.Http, scenario: scenario,
            cors: new MockCors(new[] { "https://app.example" }));
        using var http = Client();

        var pub = await Get(http, mock.BaseUrl + "public/a", "https://anyone.example");
        Assert.Equal("*", One(pub, "Access-Control-Allow-Origin"));

        var off = await Get(http, mock.BaseUrl + "internal", "https://app.example");
        Assert.Null(One(off, "Access-Control-Allow-Origin"));
        var offPreflight = await Preflight(http, mock.BaseUrl + "internal", "https://app.example", "PUT");
        Assert.Null(One(offPreflight, "Access-Control-Allow-Origin"));

        // The preflight is matched to the route its real request would reach, by that method.
        var admin = await Preflight(http, mock.BaseUrl + "admin", "https://admin.example", "PUT");
        Assert.Equal(204, (int)admin.StatusCode);
        Assert.Equal("PUT", One(admin, "Access-Control-Allow-Methods"));
        var adminWrongOrigin = await Preflight(http, mock.BaseUrl + "admin", "https://app.example", "PUT");
        Assert.Equal(403, (int)adminWrongOrigin.StatusCode);

        // A route without its own policy keeps the mock's.
        var plain = await Get(http, mock.BaseUrl + "plain", "https://app.example");
        Assert.Equal("https://app.example", One(plain, "Access-Control-Allow-Origin"));
        var stranger = await Get(http, mock.BaseUrl + "plain", "https://other.example");
        Assert.Null(One(stranger, "Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task A_route_policy_applies_even_when_the_mock_has_no_cors()
    {
        var scenario = MockScenario.Parse("""
            { "routes": [ { "match": { "path": "/a" }, "cors": { "origins": "https://app.example", "fault": "double-origin" }, "respond": { "status": 200 } } ] }
            """);
        await using var mock = MockServer.Start(0, MockTlsMode.Http, scenario: scenario);
        using var http = Client();
        var r = await Get(http, mock.BaseUrl + "a", "https://app.example");
        Assert.Equal(2, Count(r, "Access-Control-Allow-Origin"));
    }

    [Fact]
    public void A_bad_route_cors_is_a_warning_not_a_dropped_route()
    {
        var scenario = MockScenario.Parse("""
            { "routes": [ { "match": { "path": "/a" }, "cors": { "fault": "sometimes" }, "respond": { "status": 200 } },
                          { "match": { "path": "/b" }, "cors": 5, "respond": { "status": 200 } } ] }
            """);
        Assert.Equal(2, scenario.Routes.Count);
        Assert.Contains(scenario.Warnings, w => w.Contains("sometimes"));
        Assert.Contains(scenario.Warnings, w => w.Contains("route 1"));
    }

    [Fact]
    public void Mock_refuses_cors_knobs_without_cors_and_bad_values()
    {
        Assert.Contains("--cors", Cli("mock", "--port", "0", "--cors-star").Err);
        Assert.Contains("--cors-fault expects", Cli("mock", "--port", "0", "--cors", "--cors-fault", "sometimes").Err);
        Assert.Contains("--cors-max-age", Cli("mock", "--port", "0", "--cors", "--cors-max-age", "-1").Err);
        Assert.Contains("--cors-star", Cli("mock", "--port", "0", "--cors", "https://a.example", "--cors-star").Err);
    }

    // ---- serve ----

    [Fact]
    public void Serve_fixed_methods_and_no_credentials_shape_its_answers()
    {
        var options = new BrowserOptions(true, null, false, false, false, new Uri("http://127.0.0.1:8819"))
        {
            CorsAllowMethods = new[] { "GET", "POST" }, CorsCredentials = false
        };
        var preflight = BrowserRewriter.TryPreflight(new GatewayRequest("OPTIONS", "/x",
            H("Origin: http://localhost:3000", "Access-Control-Request-Method: DELETE"), null, null), options)!;
        Assert.Contains(preflight.Headers, h => h.Key == "Access-Control-Allow-Methods" && h.Value == "GET, POST");
        Assert.DoesNotContain(preflight.Headers, h => h.Key == "Access-Control-Allow-Credentials");
        Assert.Contains(CorsRules.CheckPreflight(new CorsRequest("http://localhost:3000", "DELETE", H(), false),
            preflight.StatusCode, preflight.Headers).Problems, p => p.Contains("DELETE"));

        var response = BrowserRewriter.RewriteResponseHeaders(H("Content-Type: text/plain"),
            "http://localhost:3000", new GatewayRoute("/", new Uri("https://api.internal/")), options, out _);
        Assert.DoesNotContain(response, h => h.Key == "Access-Control-Allow-Credentials");
        Assert.NotEmpty(CorsRules.CheckResponse(new CorsRequest("http://localhost:3000", "GET", H(), true), response).Problems);
    }

    [Fact]
    public void Serve_notes_when_it_replaces_an_upstreams_own_cors()
    {
        var options = new BrowserOptions(true, null, false, false, false, new Uri("http://127.0.0.1:8819"));
        var route = new GatewayRoute("/", new Uri("https://api.internal/"));
        BrowserRewriter.RewriteResponseHeaders(H("Access-Control-Allow-Origin: https://portal.internal", "Content-Type: text/plain"),
            "http://localhost:3000", route, options, out var warnings);
        var note = Assert.Single(warnings, w => w.StartsWith(BrowserRewriter.UpstreamCorsNote));
        Assert.Contains("api.internal", note);
        Assert.Contains("https://portal.internal", note);

        BrowserRewriter.RewriteResponseHeaders(H("Content-Type: text/plain"), "http://localhost:3000", route, options, out var none);
        Assert.DoesNotContain(none, w => w.StartsWith(BrowserRewriter.UpstreamCorsNote));
    }

    [Fact]
    public void Serve_profile_reads_the_cors_keys_and_refuses_a_wrong_shape()
    {
        var config = ConfigLoader.Parse("""
            { "profiles": { "p": { "serve": {
                "cors": true, "corsHeaders": ["Content-Type", "X-Api-Key"], "corsExpose": "X-Total",
                "corsMethods": ["GET", "POST"], "corsMaxAge": 60, "corsCredentials": false } } } }
            """);
        var serve = config.Profiles["p"].Serve!;
        Assert.Equal(new[] { "Content-Type", "X-Api-Key" }, serve.CorsHeaders);
        Assert.Equal(new[] { "X-Total" }, serve.CorsExpose);
        Assert.Equal(new[] { "GET", "POST" }, serve.CorsMethods);
        Assert.Equal(60, serve.CorsMaxAge);
        Assert.False(serve.CorsCredentials);

        var bad = ConfigLoader.Parse("""{ "profiles": { "p": { "serve": { "corsHeaders": 5 } } } }""");
        Assert.Throws<ConfigException>(() => { _ = bad.Profiles["p"]; });
    }

    [Fact]
    public void Serve_refuses_methods_and_no_credentials_without_cors()
    {
        var r = Cli("serve", "https://example.invalid", "--port", "8819", "--cors-no-credentials", "--check");
        Assert.Equal(2, r.Code);
        Assert.Contains("--cors", r.Err);
        var m = Cli("serve", "https://example.invalid", "--port", "8819", "--cors", "--cors-methods", "GET,PO ST", "--check");
        Assert.Equal(2, m.Code);
        Assert.Contains("not an HTTP method", m.Err);
    }
}
