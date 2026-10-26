using System.Net;
using System.Net.Http;
using System.Text.Json;
using ApiTester.Core;
using static ApiTester.Tests.Cli.ServeFixture;

namespace ApiTester.Tests.Cli;

/// <summary>`serve --allow`: one gateway for many sites, with the allowlist as the only thing
/// standing between a local caller and an open relay carrying the user's client certificate.</summary>
public class ServeDynamicTests
{
    [Fact]
    public async Task A_dynamic_target_on_the_allow_list_is_forwarded_with_the_prefix_stripped()
    {
        var (ca, server, client) = Certs();
        using (ca) using (server) using (client)
        {
            await using var api = await LoopbackMtlsServer.StartEchoAsync(server, client.Thumbprint!);
            var upstream = new Uri(api.BaseUrl);
            await using var serve = ServeHost.Start(client, "--allow", $"{upstream.Host},*.example");

            using var http = NewClient();
            string echo = await Poll(() => http.GetStringAsync($"{serve.Origin}/_/{upstream.Scheme}://{upstream.Authority}/v1/orders?page=2"));

            Assert.Contains("GET /v1/orders?page=2 HTTP/1.1", echo);
            Assert.Contains($"Host: {upstream.Authority}", echo);
        }
    }

    [Fact]
    public async Task A_dynamic_target_off_the_allow_list_is_403_and_contacts_nothing()
    {
        var (ca, server, client) = Certs();
        using (ca) using (server) using (client)
        {
            await using var api = await LoopbackMtlsServer.StartEchoAsync(server, client.Thumbprint!);
            await using var serve = ServeHost.Start(client, "--allow", "nothing.example");

            using var http = NewClient();
            var resp = await Poll(() => http.GetAsync($"{serve.Origin}/_/{api.BaseUrl.TrimEnd('/')}/x"));
            Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
            // Health still answers, which proves the gateway is up and the 403 was a decision.
            var health = await http.GetStringAsync($"{serve.Origin}/_certapi/health");
            Assert.Contains("\"ok\"", health);
        }
    }

    [Fact]
    public async Task Without_allow_the_dynamic_prefix_is_a_404()
    {
        var (ca, server, client) = Certs();
        using (ca) using (server) using (client)
        {
            await using var api = await LoopbackMtlsServer.StartEchoAsync(server, client.Thumbprint!);
            await using var serve = ServeHost.Start(client, api.BaseUrl);

            using var http = NewClient();
            var resp = await Poll(() => http.GetAsync($"{serve.Origin}/_/https://anything.example/x"));
            Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        }
    }

    [Fact]
    public async Task The_routes_endpoint_describes_mounts_and_the_allow_list()
    {
        var (ca, server, client) = Certs();
        using (ca) using (server) using (client)
        {
            await using var api = await LoopbackMtlsServer.StartEchoAsync(server, client.Thumbprint!);
            await using var serve = ServeHost.Start(client, "--upstream", $"/api={api.BaseUrl}", "--allow", "*.corp", "--forwarded", "--max-body", "2k");

            using var http = NewClient();
            string json = await Poll(() => http.GetStringAsync($"{serve.Origin}/_certapi/routes"));
            using var doc = JsonDocument.Parse(json);
            Assert.Equal("/api", doc.RootElement.GetProperty("routes")[0].GetProperty("prefix").GetString());
            Assert.Equal("*.corp", doc.RootElement.GetProperty("allow")[0].GetString());
            Assert.True(doc.RootElement.GetProperty("forwarded").GetBoolean());
            Assert.Equal(2048, doc.RootElement.GetProperty("maxBody").GetInt64());
        }
    }

    [Fact]
    public async Task Forwarded_adds_the_x_forwarded_family_and_via()
    {
        var (ca, server, client) = Certs();
        using (ca) using (server) using (client)
        {
            await using var api = await LoopbackMtlsServer.StartEchoAsync(server, client.Thumbprint!);
            await using var serve = ServeHost.Start(client, api.BaseUrl, "--forwarded");

            using var http = NewClient();
            string echo = await Poll(() => http.GetStringAsync($"{serve.Origin}/ping"));
            Assert.Contains("X-Forwarded-For: 127.0.0.1", echo);
            Assert.Contains("X-Forwarded-Proto: http", echo);
            Assert.Contains("X-Forwarded-Host: 127.0.0.1:", echo);
            Assert.Contains("Via: 1.1 certapi", echo);
        }
    }

    [Fact]
    public async Task A_body_over_max_body_is_refused_with_413()
    {
        var (ca, server, client) = Certs();
        using (ca) using (server) using (client)
        {
            await using var api = await LoopbackMtlsServer.StartEchoAsync(server, client.Thumbprint!);
            await using var serve = ServeHost.Start(client, api.BaseUrl, "--max-body", "16");

            using var http = NewClient();
            var small = await Poll(() => http.PostAsync($"{serve.Origin}/x", new StringContent("tiny")));
            Assert.Equal(HttpStatusCode.OK, small.StatusCode);
            var big = await http.PostAsync($"{serve.Origin}/x", new StringContent(new string('a', 64)));
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, big.StatusCode);
        }
    }

    [Fact]
    public async Task The_gateway_token_is_stripped_before_forwarding_and_a_bodiless_post_carries_a_length()
    {
        var (ca, server, client) = Certs();
        using (ca) using (server) using (client)
        {
            await using var api = await LoopbackMtlsServer.StartEchoAsync(server, client.Thumbprint!);
            await using var serve = ServeHost.Start(client, api.BaseUrl, "--token", "s3cret");

            using var http = NewClient();
            var req = new HttpRequestMessage(HttpMethod.Post, $"{serve.Origin}/submit");
            req.Headers.Add("X-Certapi-Token", "s3cret");
            req.Headers.Add("X-App", "keep-me");
            var resp = await Poll(() => http.SendAsync(req));
            string echo = await resp.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            Assert.DoesNotContain("s3cret", echo);
            Assert.DoesNotContain("X-Certapi-Token", echo);
            Assert.Contains("X-App: keep-me", echo);
            Assert.Contains("Content-Length: 0", echo);

            var bearer = new HttpRequestMessage(HttpMethod.Get, $"{serve.Origin}/x");
            bearer.Headers.TryAddWithoutValidation("Authorization", "Bearer s3cret");
            string bearerEcho = await (await http.SendAsync(bearer)).Content.ReadAsStringAsync();
            Assert.DoesNotContain("Authorization", bearerEcho);

            var wrong = new HttpRequestMessage(HttpMethod.Get, $"{serve.Origin}/x");
            wrong.Headers.Add("X-Certapi-Token", "s3cre7");
            Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(wrong)).StatusCode);
        }
    }

    [Fact]
    public async Task A_preflight_is_answered_before_the_token_check()
    {
        var (ca, server, client) = Certs();
        using (ca) using (server) using (client)
        {
            await using var api = await LoopbackMtlsServer.StartEchoAsync(server, client.Thumbprint!);
            await using var serve = ServeHost.Start(client, api.BaseUrl, "--token", "s3cret", "--cors");

            using var http = NewClient();
            var preflight = new HttpRequestMessage(HttpMethod.Options, $"{serve.Origin}/x");
            preflight.Headers.Add("Origin", "http://localhost:3000");
            preflight.Headers.Add("Access-Control-Request-Method", "POST");
            var resp = await Poll(() => http.SendAsync(preflight));
            Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
            Assert.Equal("http://localhost:3000", HeaderValues(resp, "Access-Control-Allow-Origin").Single());
        }
    }

    [Fact]
    public async Task A_declared_request_length_is_forwarded_as_content_length_not_chunked()
    {
        var (ca, server, client) = Certs();
        using (ca) using (server) using (client)
        {
            await using var api = await LoopbackMtlsServer.StartEchoAsync(server, client.Thumbprint!);
            await using var serve = ServeHost.Start(client, api.BaseUrl);

            using var http = NewClient();
            var resp = await Poll(() => http.PostAsync($"{serve.Origin}/submit", new StringContent("hello-body")));
            string echo = await resp.Content.ReadAsStringAsync();
            Assert.Contains("Content-Length: 10", echo);
            Assert.DoesNotContain("Transfer-Encoding: chunked", echo);
            Assert.Contains("hello-body", echo);
        }
    }

    [Theory]
    [InlineData("/_", true)]
    [InlineData("/_certapi", true)]
    [InlineData("/_certapi/x", true)]
    [InlineData("/_x", false)]
    [InlineData("/api", false)]
    public void Reserved_prefixes_cannot_be_mounted(string prefix, bool refused)
    {
        var routes = new[] { new GatewayRoute(prefix, new Uri("https://example")) };
        if (refused) Assert.Throws<ArgumentException>(() => new GatewayRoutes(routes));
        else Assert.Single(new GatewayRoutes(routes).All);
    }

    [Theory]
    [InlineData("1024", 1024L)]
    [InlineData("2k", 2048L)]
    [InlineData("3M", 3L * 1024 * 1024)]
    [InlineData("1g", 1024L * 1024 * 1024)]
    [InlineData("0", null)]
    [InlineData("lots", null)]
    public void Max_body_sizes_parse_with_suffixes(string raw, long? expected) =>
        Assert.Equal(expected, ApiTester.Cli.Commands.ServeCommand.ParseSize(raw));
}

public class UpstreamAllowlistTests
{
    [Theory]
    [InlineData("api.corp", "api.corp", true)]
    [InlineData("API.CORP", "api.corp", true)]
    [InlineData("api.corp", "evil.corp", false)]
    [InlineData("*.corp", "api.corp", true)]
    [InlineData("*.corp", "a.b.corp", true)]
    [InlineData("*.corp", "corp", false)]
    [InlineData("*.corp", "notcorp", false)]
    [InlineData(".corp", "x.corp", true)]
    [InlineData("*", "anything.at.all", true)]
    public void Matches_exact_and_suffix_entries(string entry, string host, bool allowed) =>
        Assert.Equal(allowed, new UpstreamAllowlist(new[] { entry }).IsAllowed(host));

    [Fact]
    public void Star_is_reported_as_any_host()
    {
        Assert.True(new UpstreamAllowlist(new[] { "a.corp", "*" }).AllowsAnyHost);
        Assert.False(new UpstreamAllowlist(new[] { "a.corp" }).AllowsAnyHost);
    }
}

public class DynamicTargetResolutionTests
{
    private static MtlsGateway Gateway(string? allow)
    {
        var gw = new MtlsGateway(new GatewayRoutes(new[] { new GatewayRoute("/api", new Uri("https://api.static")) }),
            clientCertificate: null, ignoreServerCertificateErrors: true, TimeSpan.FromSeconds(5));
        if (allow is not null) gw.EnableDynamicUpstreams(new UpstreamAllowlist(allow.Split(',')));
        return gw;
    }

    [Fact]
    public void A_dynamic_path_names_its_upstream_and_keeps_the_rest()
    {
        using var gw = Gateway("*.corp");
        var route = gw.ResolveTarget("/_/https://wiki.corp:8443/page/1?x=y", out var target, out var forwarded);
        Assert.NotNull(route);
        Assert.Equal("https://wiki.corp:8443/", route!.Upstream.ToString());
        Assert.Equal("/_/https://wiki.corp:8443", route.PathPrefix);
        Assert.Equal("https://wiki.corp:8443/page/1?x=y", target!.ToString());
        Assert.Equal("/page/1?x=y", forwarded);
    }

    [Fact]
    public void A_bare_dynamic_authority_forwards_the_root()
    {
        using var gw = Gateway("wiki.corp");
        var route = gw.ResolveTarget("/_/https://wiki.corp", out var target, out var forwarded);
        Assert.NotNull(route);
        Assert.Equal("https://wiki.corp/", target!.ToString());
        Assert.Equal("/", forwarded);
    }

    [Fact]
    public void A_host_outside_the_list_throws_before_anything_is_contacted()
    {
        using var gw = Gateway("*.corp");
        Assert.Throws<GatewayHostNotAllowedException>(() => gw.ResolveTarget("/_/https://evil.example/x", out _, out _));
    }

    [Theory]
    [InlineData("/_/ftp://x.corp/")]
    [InlineData("/_/x.corp/path")]
    [InlineData("/_/https://user:pw@x.corp/")]
    [InlineData("/_/")]
    public void Malformed_dynamic_targets_resolve_to_nothing(string path)
    {
        using var gw = Gateway("*.corp");
        Assert.Null(gw.ResolveTarget(path, out _, out _));
    }

    [Fact]
    public void Static_routes_still_win_and_dynamic_is_off_by_default()
    {
        using var gw = Gateway(null);
        Assert.Null(gw.ResolveTarget("/_/https://x.corp/", out _, out _));
        var route = gw.ResolveTarget("/api/orders", out var target, out _);
        Assert.Equal("https://api.static/orders", target!.ToString());
        Assert.Equal("/api", route!.PathPrefix);
    }

    [Fact]
    public void Location_and_cookie_rewrites_follow_the_dynamic_prefix()
    {
        using var gw = Gateway("*.corp");
        var route = gw.ResolveTarget("/_/https://login.corp/start", out _, out _)!;
        var options = new BrowserOptions(false, null, RewriteCookies: true, RewriteLocation: true, false, new Uri("http://127.0.0.1:8819"));
        var rewritten = BrowserRewriter.RewriteResponseHeaders(new[]
        {
            new KeyValuePair<string, string>("Location", "https://login.corp/next?x=1"),
            new KeyValuePair<string, string>("Set-Cookie", "sid=1; Path=/app; Domain=login.corp; HttpOnly")
        }, null, route, options, out _);
        Assert.Equal("http://127.0.0.1:8819/_/https://login.corp/next?x=1", rewritten.Single(h => h.Key == "Location").Value);
        Assert.Equal("sid=1; Path=/_/https://login.corp/app; HttpOnly", rewritten.Single(h => h.Key == "Set-Cookie").Value);
    }
}

public class ServeListenTests
{
    [Theory]
    [InlineData("+", "http://+:8819/")]
    [InlineData("0.0.0.0", "http://+:8819/")]
    [InlineData("::", "http://+:8819/")]
    [InlineData("10.0.0.5", "http://10.0.0.5:8819/")]
    [InlineData("fd00::5", "http://[fd00::5]:8819/")]
    [InlineData("127.0.0.1", "http://127.0.0.1:8819/")]
    [InlineData("::1", "http://[::1]:8819/")]
    public void Listen_addresses_become_listener_prefixes(string listen, string prefix) =>
        Assert.Equal(prefix, ApiTester.Cli.Commands.ServeCommand.ListenPrefix(listen, "http", 8819));

    [Fact]
    public void Listening_beyond_loopback_needs_a_token_and_refuses_tls_and_garbage()
    {
        static (int Code, string Err) Run(params string[] extra)
        {
            var err = new StringWriter();
            var args = new List<string> { "serve", "https://x", "--port", "1" };
            args.AddRange(extra);
            return (ApiTester.Cli.CliApp.Run(args.ToArray(), TextWriter.Null, err), err.ToString());
        }
        var noToken = Run("--listen", "0.0.0.0");
        Assert.Equal(ApiTester.Cli.ExitCodes.Usage, noToken.Code);
        Assert.Contains("--token", noToken.Err);

        var withTls = Run("--listen", "0.0.0.0", "--token", "t", "--tls");
        Assert.Equal(ApiTester.Cli.ExitCodes.Usage, withTls.Code);
        Assert.Contains("--tls", withTls.Err);

        var garbage = Run("--listen", "not-an-address", "--token", "t");
        Assert.Equal(ApiTester.Cli.ExitCodes.Usage, garbage.Code);
    }

    [Fact]
    public async Task Loopback_spellings_are_accepted_without_a_token_and_the_gateway_still_answers()
    {
        var (ca, server, client) = Certs();
        using (ca) using (server) using (client)
        {
            await using var api = await LoopbackMtlsServer.StartEchoAsync(server, client.Thumbprint!);
            await using var serve = ServeHost.Start(client, api.BaseUrl, "--listen", "127.0.0.1");
            using var http = NewClient();
            string echo = await Poll(() => http.GetStringAsync($"{serve.Origin}/ping"));
            Assert.Contains("GET /ping", echo);
        }
    }
}
