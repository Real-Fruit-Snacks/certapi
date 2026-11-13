using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using ApiTester.Cli;
using ApiTester.Core;
using static ApiTester.Tests.Cli.ServeFixture;

namespace ApiTester.Tests.Cli;

/// <summary>End-to-end coverage of `certapi serve` as a browser sees it: several mounted upstreams,
/// CORS, cookie and Location rewriting and WebSocket relaying, all through a real listener talking to
/// real loopback mTLS upstreams. Every assertion is on what a client actually receives on
/// http://127.0.0.1:&lt;port&gt; or on the raw request an echo upstream reports it was sent.</summary>
public class ServeBrowserTests
{
    /// <summary>The preflight every CORS test here sends, stated once: OPTIONS on /orders carrying
    /// the headers a browser actually puts on one. A fresh message per call, because an
    /// HttpRequestMessage cannot be sent twice and several tests send two.</summary>
    private static HttpRequestMessage Preflight(
        string gateway, string origin, string method = "GET",
        string? requestHeaders = null, bool privateNetwork = false)
    {
        var m = new HttpRequestMessage(HttpMethod.Options, $"{gateway}/orders");
        m.Headers.Add("Origin", origin);
        m.Headers.Add("Access-Control-Request-Method", method);
        if (requestHeaders is not null) m.Headers.Add("Access-Control-Request-Headers", requestHeaders);
        if (privateNetwork) m.Headers.Add("Access-Control-Request-Private-Network", "true");
        return m;
    }

    [Fact]
    public async Task Each_mounted_upstream_receives_the_request_with_its_prefix_stripped()
    {
        var (ca, server, client) = Certs();
        using (ca) using (server) using (client)
        {
            await using var api = await LoopbackMtlsServer.StartEchoAsync(server, client.Thumbprint!);
            await using var auth = await LoopbackMtlsServer.StartEchoAsync(server, client.Thumbprint!);
            await using var serve = ServeHost.Start(client,
                "--upstream", $"/api={api.BaseUrl}", "--upstream", $"/auth={auth.BaseUrl}");

            using var http = NewClient();
            string apiEcho = await Poll(() => http.GetStringAsync($"{serve.Origin}/api/orders?page=2"));
            string authEcho = await http.GetStringAsync($"{serve.Origin}/auth/token");

            // The echo body is the raw request the upstream received: the request line shows the
            // mount point was stripped, and the Host header shows which upstream received it.
            Assert.Contains("GET /orders?page=2 HTTP/1.1", apiEcho);
            Assert.Contains($"Host: {new Uri(api.BaseUrl).Authority}", apiEcho);
            Assert.Contains("GET /token HTTP/1.1", authEcho);
            Assert.Contains($"Host: {new Uri(auth.BaseUrl).Authority}", authEcho);
        }
    }

    [Fact]
    public async Task A_path_that_matches_no_mounted_prefix_is_a_404_and_reaches_no_upstream()
    {
        var (ca, server, client) = Certs();
        using (ca) using (server) using (client)
        {
            await using var api = await LoopbackMtlsServer.StartEchoAsync(server, client.Thumbprint!);
            await using var serve = ServeHost.Start(client, "--upstream", $"/api={api.BaseUrl}");

            using var http = NewClient();
            var resp = await Poll(() => http.GetAsync($"{serve.Origin}/nowhere/orders"));
            string body = await resp.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
            // An echo upstream answers with the request it was sent, so an answer with no request
            // line in it is an answer no upstream produced.
            Assert.DoesNotContain("GET /", body);
        }
    }

    [Fact]
    public async Task A_preflight_is_answered_by_the_gateway_and_never_forwarded()
    {
        var (ca, server, client) = Certs();
        using (ca) using (server) using (client)
        {
            // Nothing listens on this port, so an answer that is not a 502 is one the gateway wrote
            // itself without contacting anything.
            string unreachable = $"https://127.0.0.1:{FreePort()}";
            await using var serve = ServeHost.Start(client, "--upstream", $"/={unreachable}", "--cors");

            using var http = NewClient();
            var resp = await Poll(() => http.SendAsync(
                Preflight(serve.Origin, "http://localhost:3000", "PUT", "authorization, x-trace")));

            Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
            Assert.Equal(new[] { "http://localhost:3000" }, HeaderValues(resp, "Access-Control-Allow-Origin"));
            Assert.Equal(new[] { "PUT" }, HeaderValues(resp, "Access-Control-Allow-Methods"));
            Assert.Equal(new[] { "authorization, x-trace" }, HeaderValues(resp, "Access-Control-Allow-Headers"));
            Assert.Equal(new[] { "true" }, HeaderValues(resp, "Access-Control-Allow-Credentials"));

            // A real request on the same route does reach for the upstream, and fails — which is
            // what makes the 204 above proof that the preflight went nowhere.
            var forwarded = await http.GetAsync($"{serve.Origin}/orders");
            Assert.Equal(HttpStatusCode.BadGateway, forwarded.StatusCode);
        }
    }

    [Fact]
    public async Task An_origin_outside_an_explicit_cors_allowlist_is_refused()
    {
        var (ca, server, client) = Certs();
        using (ca) using (server) using (client)
        {
            await using var api = await LoopbackMtlsServer.StartEchoAsync(server, client.Thumbprint!);
            await using var serve = ServeHost.Start(client,
                "--upstream", $"/={api.BaseUrl}", "--cors", "http://localhost:3000");

            using var http = NewClient();
            var listed = await Poll(() => http.SendAsync(Preflight(serve.Origin, "http://localhost:3000")));
            var stranger = await http.SendAsync(Preflight(serve.Origin, "https://evil.example"));

            Assert.Equal(HttpStatusCode.NoContent, listed.StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, stranger.StatusCode);
            Assert.Empty(HeaderValues(stranger, "Access-Control-Allow-Origin"));
        }
    }

    [Fact]
    public async Task A_preflight_requesting_private_network_access_is_granted_when_the_origin_is_allowed()
    {
        var (ca, server, client) = Certs();
        using (ca) using (server) using (client)
        {
            // Nothing listens on this port, so an answer that is not a 502 is one the gateway wrote
            // itself without contacting anything.
            string unreachable = $"https://127.0.0.1:{FreePort()}";
            await using var serve = ServeHost.Start(client, "--upstream", $"/={unreachable}", "--cors");

            using var http = NewClient();
            var resp = await Poll(() => http.SendAsync(
                Preflight(serve.Origin, "http://localhost:3000", privateNetwork: true)));

            Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
            Assert.Equal(new[] { "http://localhost:3000" }, HeaderValues(resp, "Access-Control-Allow-Origin"));
            Assert.Equal(new[] { "true" }, HeaderValues(resp, "Access-Control-Allow-Private-Network"));
        }
    }

    [Fact]
    public async Task A_preflight_requesting_private_network_access_is_refused_for_an_origin_outside_an_explicit_allowlist()
    {
        var (ca, server, client) = Certs();
        using (ca) using (server) using (client)
        {
            string unreachable = $"https://127.0.0.1:{FreePort()}";
            await using var serve = ServeHost.Start(client,
                "--upstream", $"/={unreachable}", "--cors", "http://localhost:3000");

            using var http = NewClient();
            var allowed = await Poll(() => http.SendAsync(
                Preflight(serve.Origin, "http://localhost:3000", privateNetwork: true)));
            var stranger = await http.SendAsync(
                Preflight(serve.Origin, "https://evil.example", privateNetwork: true));

            // The allowlisted origin gets the same 204-plus-PNA answer as any other allowed
            // preflight, so this is the boundary the allowlist draws rather than PNA being refused
            // outright.
            Assert.Equal(HttpStatusCode.NoContent, allowed.StatusCode);
            Assert.Equal(new[] { "true" }, HeaderValues(allowed, "Access-Control-Allow-Private-Network"));

            Assert.Equal(HttpStatusCode.Forbidden, stranger.StatusCode);
            Assert.Empty(HeaderValues(stranger, "Access-Control-Allow-Private-Network"));
            Assert.Empty(HeaderValues(stranger, "Access-Control-Allow-Origin"));
        }
    }

    [Fact]
    public async Task A_preflight_that_did_not_ask_for_private_network_access_does_not_get_it_in_the_answer()
    {
        var (ca, server, client) = Certs();
        using (ca) using (server) using (client)
        {
            string unreachable = $"https://127.0.0.1:{FreePort()}";
            await using var serve = ServeHost.Start(client, "--upstream", $"/={unreachable}", "--cors");

            using var http = NewClient();
            var resp = await Poll(() => http.SendAsync(Preflight(serve.Origin, "http://localhost:3000")));

            Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
            Assert.Empty(HeaderValues(resp, "Access-Control-Allow-Private-Network"));
        }
    }

    [Theory]
    // --cors-max-age names the lifetime; without it the answer is the 600 seconds the gateway has
    // always sent, so an existing user's preflight is cached exactly as long as it was before.
    [InlineData(new[] { "--cors-max-age", "3600" }, "3600")]
    [InlineData(new string[0], "600")]
    public async Task A_preflight_carries_the_configured_max_age(string[] maxAgeArgs, string expected)
    {
        var (ca, server, client) = Certs();
        using (ca) using (server) using (client)
        {
            string unreachable = $"https://127.0.0.1:{FreePort()}";
            await using var serve = ServeHost.Start(client,
                new[] { "--upstream", $"/={unreachable}", "--cors" }.Concat(maxAgeArgs).ToArray());

            using var http = NewClient();
            var resp = await Poll(() => http.SendAsync(Preflight(serve.Origin, "http://localhost:3000")));

            Assert.Equal(new[] { expected }, HeaderValues(resp, "Access-Control-Max-Age"));
        }
    }

    [Fact]
    public void Cors_max_age_without_cors_is_a_usage_error()
    {
        var stderr = new StringWriter();
        int code = CliApp.Run(
            new[] { "serve", "--port", FreePort().ToString(), "--upstream", "/=https://api.internal",
                     "--cors-max-age", "3600" },
            TextWriter.Null, stderr, services: new CliServices());

        Assert.Equal(2, code);
        Assert.Contains("--cors-max-age", stderr.ToString());
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("-1")]
    public void A_non_numeric_or_negative_cors_max_age_is_a_usage_error(string value)
    {
        var stderr = new StringWriter();
        int code = CliApp.Run(
            new[] { "serve", "--port", FreePort().ToString(), "--upstream", "/=https://api.internal",
                     "--cors", "--cors-max-age", value },
            TextWriter.Null, stderr, services: new CliServices());

        Assert.Equal(2, code);
        Assert.Contains("--cors-max-age", stderr.ToString());
    }

    [Fact]
    public async Task A_response_carries_exactly_one_allow_origin_even_when_the_upstream_sent_its_own()
    {
        var (ca, server, client) = Certs();
        using (ca) using (server) using (client)
        {
            await using var api = await LoopbackMtlsServer.StartWithHeadersAsync(
                server, client.Thumbprint!,
                new[]
                {
                    new KeyValuePair<string, string>(
                        "Access-Control-Allow-Origin", "https://upstream.example")
                },
                responseBody: "ok", responseContentType: "text/plain");
            await using var serve = ServeHost.Start(client, "--upstream", $"/={api.BaseUrl}", "--cors");

            using var http = NewClient();
            HttpRequestMessage FromApp()
            {
                var m = new HttpRequestMessage(HttpMethod.Get, $"{serve.Origin}/orders");
                m.Headers.Add("Origin", "http://localhost:3000");
                return m;
            }
            var resp = await Poll(() => http.SendAsync(FromApp()));

            // Two allow-origin values is a hard browser failure, not a warning: the upstream's own
            // must be gone, and exactly the caller's origin left in its place.
            Assert.Equal(new[] { "http://localhost:3000" }, HeaderValues(resp, "Access-Control-Allow-Origin"));
            Assert.Equal(new[] { "true" }, HeaderValues(resp, "Access-Control-Allow-Credentials"));
        }
    }

    [Fact]
    public async Task An_upstream_cookie_is_rewritten_to_one_the_loopback_browser_can_store()
    {
        var (ca, server, client) = Certs();
        using (ca) using (server) using (client)
        {
            await using var api = await LoopbackMtlsServer.StartWithHeadersAsync(
                server, client.Thumbprint!,
                new[]
                {
                    new KeyValuePair<string, string>(
                        "Set-Cookie", "sid=abc; Domain=api.internal; Path=/; Secure; HttpOnly; SameSite=None")
                },
                responseBody: "ok", responseContentType: "text/plain");
            await using var serve = ServeHost.Start(client, "--upstream", $"/={api.BaseUrl}", "--rewrite-cookies");

            using var http = NewClient();
            var resp = await Poll(() => http.GetAsync($"{serve.Origin}/orders"));

            // Domain scopes the cookie to a host the browser is not talking to, Secure means nothing
            // is stored over plaintext loopback, and SameSite=None without Secure is rejected
            // outright; everything else the upstream asked for survives untouched.
            Assert.Equal(new[] { "sid=abc; Path=/; HttpOnly; SameSite=Lax" },
                         HeaderValues(resp, "Set-Cookie"));
        }
    }

    [Fact]
    public async Task A_redirect_to_the_upstream_comes_back_pointing_at_the_gateway()
    {
        var (ca, server, client) = Certs();
        using (ca) using (server) using (client)
        {
            // This upstream redirects to itself, which is the case worth rewriting: left alone, the
            // browser would leave the gateway and the client certificate behind.
            await using var api = await LoopbackMtlsServer.StartRedirectChainAsync(
                server, client.Thumbprint!, hops: 1);
            await using var serve = ServeHost.Start(client,
                "--upstream", $"/svc={api.BaseUrl}", "--rewrite-location");

            using var http = NewClient();
            var resp = await Poll(() => http.GetAsync($"{serve.Origin}/svc/"));

            Assert.Equal(HttpStatusCode.Found, resp.StatusCode);
            // Back through the mount point, so following it reaches the same upstream again.
            Assert.Equal(new[] { $"{serve.Origin}/svc/hop1" }, HeaderValues(resp, "Location"));
        }
    }

    [Fact]
    public async Task A_websocket_message_round_trips_through_the_gateway_on_the_negotiated_subprotocol()
    {
        var (ca, server, client) = Certs();
        using (ca) using (server) using (client)
        {
            await using var api = await LoopbackMtlsServer.StartWebSocketEchoAsync(server, client.Thumbprint!);
            await using var serve = ServeHost.Start(client, "--upstream", $"/ws={api.BaseUrl}", "--allow-upgrade");

            using var cts = new CancellationTokenSource(Limit);
            var url = new Uri($"ws://127.0.0.1:{serve.Port}/ws/socket");
            // A ClientWebSocket cannot be reconnected once a connect has failed, so each attempt
            // while the listener binds needs a socket of its own.
            using var browser = await Poll(async () =>
            {
                var ws = new ClientWebSocket();
                ws.Options.AddSubProtocol("chat");
                try { await ws.ConnectAsync(url, cts.Token); return ws; }
                catch { ws.Dispose(); throw; }
            });

            await browser.SendAsync(Encoding.UTF8.GetBytes("hello upstream"),
                                    WebSocketMessageType.Text, true, cts.Token);
            var buffer = new byte[8192];
            var received = await browser.ReceiveAsync(buffer, cts.Token);

            Assert.Equal("chat", browser.SubProtocol);
            Assert.Equal(WebSocketMessageType.Text, received.MessageType);
            Assert.Equal("hello upstream", Encoding.UTF8.GetString(buffer, 0, received.Count));
        }
    }

    [Fact]
    public async Task Without_the_browser_flags_the_upstream_headers_are_relayed_untouched()
    {
        var (ca, server, client) = Certs();
        using (ca) using (server) using (client)
        {
            await using var api = await LoopbackMtlsServer.StartWithHeadersAsync(
                server, client.Thumbprint!,
                new[]
                {
                    new KeyValuePair<string, string>(
                        "Access-Control-Allow-Origin", "https://upstream.example"),
                    new KeyValuePair<string, string>(
                        "Set-Cookie", "sid=abc; Domain=api.internal; Secure; SameSite=None")
                },
                responseBody: "ok", responseContentType: "text/plain");
            await using var serve = ServeHost.Start(client, "--upstream", $"/={api.BaseUrl}");

            using var http = NewClient();
            HttpRequestMessage FromApp()
            {
                var m = new HttpRequestMessage(HttpMethod.Get, $"{serve.Origin}/orders");
                m.Headers.Add("Origin", "http://localhost:3000");
                return m;
            }
            var resp = await Poll(() => http.SendAsync(FromApp()));

            // The default is a faithful relay: nothing is stripped, nothing is added, and an
            // existing non-browser caller sees exactly what it saw before.
            Assert.Equal(new[] { "https://upstream.example" },
                         HeaderValues(resp, "Access-Control-Allow-Origin"));
            Assert.Equal(new[] { "sid=abc; Domain=api.internal; Secure; SameSite=None" },
                         HeaderValues(resp, "Set-Cookie"));
            Assert.Empty(HeaderValues(resp, "Access-Control-Allow-Credentials"));
        }
    }

    [Theory]
    [InlineData("https://api.internal")]      // no prefix and no '=' at all
    [InlineData("=https://api.internal")]     // nothing before the '='
    [InlineData(" =https://api.internal")]    // and nothing but blanks before it
    [InlineData("/api=api.internal")]         // not an absolute URL
    [InlineData("/api=ftp://api.internal")]   // not a URL the gateway can forward to
    public void A_malformed_upstream_spec_is_a_usage_error(string spec)
    {
        var stderr = new StringWriter();
        int code = CliApp.Run(new[] { "serve", "--port", FreePort().ToString(), "--upstream", spec },
                              TextWriter.Null, stderr, services: new CliServices());

        Assert.Equal(2, code);
        Assert.Contains(spec.Trim(), stderr.ToString());   // the offending spec is quoted back
    }

    [Fact]
    public void Two_upstreams_on_the_same_mount_point_are_a_usage_error()
    {
        var stderr = new StringWriter();
        // Same prefix, differently spelled: last-wins would silently send /api somewhere the user
        // did not mean.
        int code = CliApp.Run(new[] { "serve", "--port", FreePort().ToString(),
                                      "--upstream", "/api=https://a.internal",
                                      "--upstream", "/api/=https://b.internal" },
                              TextWriter.Null, stderr, services: new CliServices());

        Assert.Equal(2, code);
        Assert.Contains("/api", stderr.ToString());
    }

    [Fact]
    public void A_positional_upstream_that_collides_with_a_root_mount_is_a_usage_error()
    {
        var stderr = new StringWriter();
        int code = CliApp.Run(new[] { "serve", "https://a.internal", "--port", FreePort().ToString(),
                                      "--upstream", "/=https://b.internal" },
                              TextWriter.Null, stderr, services: new CliServices());

        Assert.Equal(2, code);
    }

    [Theory]
    // A proxy address that is not an address.
    [InlineData("--proxy not-a-url")]
    // A pinned address cannot survive a proxy, which tunnels the connection somewhere else.
    [InlineData("--proxy http://proxy.corp:8080 --resolve api.internal:443:10.0.0.1")]
    public void Transport_settings_the_gateway_cannot_honour_are_a_usage_error(string flags)
    {
        var args = new List<string> { "serve", "--port", FreePort().ToString(),
                                      "--upstream", "/=https://api.internal" };
        args.AddRange(flags.Split(' '));

        int code = CliApp.Run(args.ToArray(), TextWriter.Null, new StringWriter(), services: new CliServices());

        // Said before the listener binds, rather than dropped on the floor once traffic arrives.
        Assert.Equal(2, code);
    }

    [Fact]
    public void Serving_with_no_upstream_at_all_is_a_usage_error()
    {
        int code = CliApp.Run(new[] { "serve", "--port", FreePort().ToString() },
                              TextWriter.Null, new StringWriter(), services: new CliServices());

        Assert.Equal(2, code);
    }
}
