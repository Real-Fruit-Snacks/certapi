using System.Net;
using System.Net.Http;
using ApiTester.Cli;
using ApiTester.Core;
using static ApiTester.Tests.Cli.ServeFixture;

namespace ApiTester.Tests.Cli;

/// <summary>`serve --vhost`: one origin per upstream under *.gw.localhost, translated onto the
/// path-based gateway and back.</summary>
public class VirtualHostsTests
{
    private static GatewayRoutes Routes() => new(new[]
    {
        new GatewayRoute("/", new Uri("https://portal.corp")),
        new GatewayRoute("/api", new Uri("https://api.corp")),
        new GatewayRoute("/auth/sso", new Uri("https://login.corp:8443")),
    });

    [Fact]
    public void Mounts_are_named_by_their_prefix_and_the_root_is_the_bare_suffix()
    {
        var v = new VirtualHosts(Routes(), "gw.localhost", "http", 8819);
        Assert.Equal("/", v.MountFor("gw.localhost:8819"));
        Assert.Equal("/api", v.MountFor("API.gw.localhost:8819"));
        Assert.Equal("/auth/sso", v.MountFor("auth-sso.gw.localhost"));
        Assert.Null(v.MountFor("127.0.0.1:8819"));
        Assert.Null(v.MountFor("other.example"));
        Assert.Null(v.MountFor(null));
        Assert.Equal(new Uri("http://api.gw.localhost:8819"), v.OriginFor(Routes().All[1]));
        Assert.Equal(new Uri("http://gw.localhost:8819"), v.OriginFor(Routes().All[0]));
    }

    [Fact]
    public void A_host_shaped_label_is_a_dynamic_target_and_round_trips()
    {
        var v = new VirtualHosts(Routes(), "gw.localhost", "http", 8819);
        Assert.Equal("/_/https://hr.corp", v.MountFor("hr.corp.gw.localhost:8819"));
        Assert.Equal("/_/https://hr.corp:8443", v.MountFor("hr.corp--8443.gw.localhost"));
        Assert.Equal("/_/http://pub.corp:8080", v.MountFor("http--pub.corp--8080.gw.localhost"));
        Assert.Null(v.MountFor("hr.corp--notaport.gw.localhost"));
        Assert.Equal("hr.corp--8443", VirtualHosts.EncodeAuthority(new Uri("https://hr.corp:8443/x")));
        Assert.Equal("http--pub.corp", VirtualHosts.EncodeAuthority(new Uri("http://pub.corp/")));
    }

    [Fact]
    public void A_target_is_expressed_in_host_form_when_the_gateway_can_reach_it()
    {
        var v = new VirtualHosts(Routes(), "gw.localhost", "http", 8819);
        string? PathFor(Uri u) => u.Host == "hr.corp" ? "/_/https://hr.corp" + u.PathAndQuery : null;
        Assert.Equal("http://api.gw.localhost:8819/orders?x=1", v.UrlFor(new Uri("https://api.corp/orders?x=1"), PathFor));
        Assert.Equal("http://gw.localhost:8819/home", v.UrlFor(new Uri("https://portal.corp/home"), PathFor));
        Assert.Equal("http://hr.corp.gw.localhost:8819/me", v.UrlFor(new Uri("https://hr.corp/me"), PathFor));
        Assert.Null(v.UrlFor(new Uri("https://elsewhere.example/"), PathFor));
        Assert.Equal("/api/orders", VirtualHosts.GatewayPath("/api", "/orders"));
        Assert.Equal("/orders", VirtualHosts.GatewayPath("/", "/orders"));
        Assert.Equal("/api/?q=1", VirtualHosts.GatewayPath("/api", "?q=1"));
    }

    [Fact]
    public void A_bad_suffix_is_a_usage_error()
    {
        Assert.Throws<ArgumentException>(() => new VirtualHosts(Routes(), "gw/localhost", "http", 1));
        var err = new StringWriter();
        Assert.Equal(ExitCodes.Usage, CliApp.Run(new[] { "serve", "https://x", "--port", "1", "--vhost", "a:b" }, TextWriter.Null, err));
        Assert.Contains("--vhost", err.ToString());
    }
}

public class ServeVirtualHostTests
{
    private static HttpRequestMessage To(ServeHost serve, string host, string path) =>
        new(HttpMethod.Get, $"{serve.Origin}{path}") { Headers = { Host = $"{host}:{serve.Port}" } };

    [Fact]
    public async Task Each_virtual_host_reaches_its_own_mount_and_the_path_form_still_works()
    {
        var (ca, server, client) = Certs();
        using (ca) using (server) using (client)
        {
            await using var api = await LoopbackMtlsServer.StartEchoAsync(server, client.Thumbprint!);
            await using var portal = await LoopbackMtlsServer.StartCookieEchoAsync(server, client.Thumbprint!);
            await using var serve = ServeHost.Start(client, "--upstream", $"/api={api.BaseUrl}", "--upstream", $"/={portal.BaseUrl}", "--vhost");
            using var http = NewClient();

            var viaHost = await Poll(() => http.SendAsync(To(serve, "api.gw.localhost", "/orders/7?x=1")));
            Assert.Equal(HttpStatusCode.OK, viaHost.StatusCode);
            Assert.Contains("GET /orders/7?x=1", await viaHost.Content.ReadAsStringAsync());

            var root = await http.SendAsync(To(serve, "gw.localhost", "/home"));
            Assert.Equal(HttpStatusCode.OK, root.StatusCode);

            var viaPath = await http.GetAsync($"{serve.Origin}/api/orders/8");
            Assert.Equal(HttpStatusCode.OK, viaPath.StatusCode);
            Assert.Contains("GET /orders/8", await viaPath.Content.ReadAsStringAsync());

            var unknown = await http.SendAsync(To(serve, "nothing.gw.localhost", "/"));
            Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        }
    }

    [Fact]
    public async Task Rewrites_speak_host_form_cookies_keep_their_path_and_a_cross_mount_redirect_lands_on_the_other_host()
    {
        var (ca, server, client) = Certs();
        using (ca) using (server) using (client)
        {
            await using var app = await LoopbackMtlsServer.StartCookieEchoAsync(server, client.Thumbprint!);
            await using var login = await LoopbackMtlsServer.StartRedirectAsync(server, client.Thumbprint!, app.BaseUrl.TrimEnd('/') + "/home?sso=1");
            await using var serve = ServeHost.Start(client,
                "--upstream", $"/auth={login.BaseUrl}", "--upstream", $"/app={app.BaseUrl}", "--vhost", "--rewrite-location", "--rewrite-cookies");
            using var http = NewClient();

            var resp = await Poll(() => http.SendAsync(To(serve, "auth.gw.localhost", "/start")));
            Assert.Equal(HttpStatusCode.Found, resp.StatusCode);
            Assert.Equal($"http://app.gw.localhost:{serve.Port}/home?sso=1", resp.Headers.Location!.ToString());

            var home = await http.SendAsync(To(serve, "app.gw.localhost", "/home"));
            Assert.Equal(HttpStatusCode.OK, home.StatusCode);
            // Under the path gateway this cookie would be re-rooted to Path=/app; on its own origin
            // it is exactly what the upstream wrote.
            Assert.Contains("srv=ok; Path=/", HeaderValues(home, "Set-Cookie").Single());
            Assert.DoesNotContain("Path=/app", HeaderValues(home, "Set-Cookie").Single());
        }
    }

    [Fact]
    public async Task An_allowed_dynamic_host_answers_under_its_own_name()
    {
        var (ca, server, client) = Certs();
        using (ca) using (server) using (client)
        {
            await using var app = await LoopbackMtlsServer.StartEchoAsync(server, client.Thumbprint!);
            var appUri = new Uri(app.BaseUrl);
            await using var serve = ServeHost.Start(client, "--allow", appUri.Host, "--vhost");
            using var http = NewClient();

            string label = VirtualHosts.EncodeAuthority(appUri);
            var resp = await Poll(() => http.SendAsync(To(serve, $"{label}.gw.localhost", "/dyn")));
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            Assert.Contains("GET /dyn", await resp.Content.ReadAsStringAsync());

            var refused = await http.SendAsync(To(serve, "evil.example.gw.localhost", "/"));
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        }
    }
}
