using System.IO;
using System.Net;
using System.Net.Http;
using ApiTester.Core;
using ApiTester.Cli;
using static ApiTester.Tests.Cli.ServeFixture;

namespace ApiTester.Tests.Cli;

/// <summary>What a multi-step login through the gateway needs: a cross-host redirect that stays
/// on the gateway, Origin/Referer the login form will accept, cookies that follow the session
/// across hosts, and a CSP that does not block the page's own fetches.</summary>
public class ServeLoginFlowTests
{
    [Fact]
    public async Task A_redirect_to_another_mount_comes_back_through_that_mount()
    {
        var (ca, server, client) = Certs();
        using (ca) using (server) using (client)
        {
            await using var app = await LoopbackMtlsServer.StartEchoAsync(server, client.Thumbprint!);
            await using var login = await LoopbackMtlsServer.StartRedirectAsync(server, client.Thumbprint!, app.BaseUrl.TrimEnd('/') + "/home?sso=1");
            await using var serve = ServeHost.Start(client,
                "--upstream", $"/auth={login.BaseUrl}", "--upstream", $"/app={app.BaseUrl}", "--rewrite-location");

            using var http = NewClient();
            var resp = await Poll(() => http.GetAsync($"{serve.Origin}/auth/start"));
            Assert.Equal(HttpStatusCode.Found, resp.StatusCode);
            Assert.Equal($"{serve.Origin}/app/home?sso=1", resp.Headers.Location!.ToString());
        }
    }

    [Fact]
    public async Task A_redirect_to_an_allowed_dynamic_host_comes_back_through_the_dynamic_prefix()
    {
        var (ca, server, client) = Certs();
        using (ca) using (server) using (client)
        {
            await using var app = await LoopbackMtlsServer.StartEchoAsync(server, client.Thumbprint!);
            var appUri = new Uri(app.BaseUrl);
            await using var login = await LoopbackMtlsServer.StartRedirectAsync(server, client.Thumbprint!, app.BaseUrl.TrimEnd('/') + "/landing");
            await using var serve = ServeHost.Start(client, login.BaseUrl, "--allow", appUri.Host, "--rewrite-location");

            using var http = NewClient();
            var resp = await Poll(() => http.GetAsync($"{serve.Origin}/start"));
            Assert.Equal(HttpStatusCode.Found, resp.StatusCode);
            Assert.Equal($"{serve.Origin}/_/{appUri.Scheme}://{appUri.Authority}/landing", resp.Headers.Location!.ToString());
        }
    }

    [Fact]
    public async Task Origin_and_referer_point_at_the_upstream_when_rewritten()
    {
        var (ca, server, client) = Certs();
        using (ca) using (server) using (client)
        {
            await using var login = await LoopbackMtlsServer.StartEchoAsync(server, client.Thumbprint!);
            var upstream = new Uri(login.BaseUrl);
            await using var serve = ServeHost.Start(client, "--upstream", $"/auth={login.BaseUrl}", "--rewrite-origin");

            using var http = NewClient();
            var req = new HttpRequestMessage(HttpMethod.Post, $"{serve.Origin}/auth/login") { Content = new StringContent("u=a&p=b") };
            req.Headers.TryAddWithoutValidation("Origin", serve.Origin);
            req.Headers.TryAddWithoutValidation("Referer", $"{serve.Origin}/auth/form?step=2");
            string echo = await (await Poll(() => http.SendAsync(req))).Content.ReadAsStringAsync();

            Assert.Contains($"Origin: {upstream.GetLeftPart(UriPartial.Authority)}", echo);
            Assert.Contains($"Referer: {upstream.GetLeftPart(UriPartial.Authority)}/form?step=2", echo);
            Assert.DoesNotContain(serve.Origin, echo.Split("\r\n").First(l => l.StartsWith("Referer:")));
        }
    }

    [Fact]
    public async Task Without_rewrite_origin_the_gateway_origin_is_relayed_as_sent()
    {
        var (ca, server, client) = Certs();
        using (ca) using (server) using (client)
        {
            await using var login = await LoopbackMtlsServer.StartEchoAsync(server, client.Thumbprint!);
            await using var serve = ServeHost.Start(client, login.BaseUrl);

            using var http = NewClient();
            var req = new HttpRequestMessage(HttpMethod.Get, $"{serve.Origin}/x");
            req.Headers.TryAddWithoutValidation("Origin", serve.Origin);
            string echo = await (await Poll(() => http.SendAsync(req))).Content.ReadAsStringAsync();
            Assert.Contains($"Origin: {serve.Origin}", echo);
        }
    }

    [Fact]
    public async Task Csp_is_stripped_only_when_asked()
    {
        var (ca, server, client) = Certs();
        using (ca) using (server) using (client)
        {
            var headers = new[]
            {
                new KeyValuePair<string, string>("Content-Security-Policy", "default-src 'self' https://api.corp"),
                new KeyValuePair<string, string>("X-Keep", "yes")
            };
            await using var site = await LoopbackMtlsServer.StartWithHeadersAsync(server, client.Thumbprint!, headers, "<html/>", "text/html");
            await using var plain = ServeHost.Start(client, site.BaseUrl);
            await using var stripped = ServeHost.Start(client, site.BaseUrl, "--strip-csp");

            using var http = NewClient();
            var kept = await Poll(() => http.GetAsync($"{plain.Origin}/"));
            Assert.Single(HeaderValues(kept, "Content-Security-Policy"));
            var gone = await Poll(() => http.GetAsync($"{stripped.Origin}/"));
            Assert.Empty(HeaderValues(gone, "Content-Security-Policy"));
            Assert.Single(HeaderValues(gone, "X-Keep"));
        }
    }

    [Fact]
    public async Task The_gateway_cookie_jar_carries_an_upstream_cookie_to_the_next_request()
    {
        var (ca, server, client) = Certs();
        using (ca) using (server) using (client)
        {
            // The cookie-echo server sets srv=ok and echoes whatever Cookie it received. A client
            // that sends no cookies of its own still gets srv=ok on the second request, because the
            // gateway kept it against the real upstream URL.
            await using var site = await LoopbackMtlsServer.StartCookieEchoAsync(server, client.Thumbprint!);
            await using var serve = ServeHost.Start(client, site.BaseUrl, "--cookie-jar");

            using var http = new HttpClient(new HttpClientHandler { UseCookies = false }) { Timeout = Limit };
            string first = await Poll(() => http.GetStringAsync($"{serve.Origin}/one"));
            string second = await http.GetStringAsync($"{serve.Origin}/two");
            Assert.DoesNotContain("srv=ok", first);
            Assert.Contains("srv=ok", second);
        }
    }

    [Fact]
    public async Task A_captured_token_is_attached_for_the_upstream_origin_when_asked()
    {
        var (ca, server, client) = Certs();
        using (ca) using (server) using (client)
        {
            await using var api = await LoopbackMtlsServer.StartEchoAsync(server, client.Thumbprint!);
            string workspace = Path.Combine(Path.GetTempPath(), "certapi-ws-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                var state = new AppState();
                TokenService.Capture(state, api.BaseUrl, System.Text.Encoding.UTF8.GetBytes("{\"access_token\":\"tok-123\"}"), "application/json", Array.Empty<KeyValuePair<string, string>>());
                state.SaveTo(workspace);

                await using var withToken = ServeHost.Start(client, api.BaseUrl, "--auto-token", "--workspace", workspace);
                await using var without = ServeHost.Start(client, api.BaseUrl, "--workspace", workspace);
                using var http = NewClient();
                string attached = await Poll(() => http.GetStringAsync($"{withToken.Origin}/orders"));
                Assert.Contains("Authorization: Bearer tok-123", attached);
                string bare = await Poll(() => http.GetStringAsync($"{without.Origin}/orders"));
                Assert.DoesNotContain("Authorization:", bare);

                // The caller's own Authorization is never overridden.
                var own = new HttpRequestMessage(HttpMethod.Get, $"{withToken.Origin}/orders");
                own.Headers.TryAddWithoutValidation("Authorization", "Bearer mine");
                string kept = await (await http.SendAsync(own)).Content.ReadAsStringAsync();
                Assert.Contains("Authorization: Bearer mine", kept);
                Assert.DoesNotContain("tok-123", kept);
            }
            finally { File.Delete(workspace); }
        }
    }
}

public class GatewayCookieJarTests
{
    [Fact]
    public void A_domain_cookie_set_by_one_host_is_sent_to_a_sibling_host()
    {
        var jar = new GatewayCookieJar();
        jar.Store(new Uri("https://login.internal.corp/sso"), new[] { "sid=abc; Domain=.internal.corp; Path=/; HttpOnly" });
        Assert.Equal("sid=abc", jar.Merge(new Uri("https://app.internal.corp/home"), null));
        Assert.Null(jar.Merge(new Uri("https://other.example/"), null));
    }

    [Fact]
    public void The_browsers_own_value_wins_and_missing_ones_are_appended()
    {
        var jar = new GatewayCookieJar();
        jar.Store(new Uri("https://app.corp/"), new[] { "a=jar; Path=/", "b=jar; Path=/" });
        Assert.Equal("a=browser; b=jar", jar.Merge(new Uri("https://app.corp/x"), "a=browser"));
        Assert.Equal("a=browser; b=browser", jar.Merge(new Uri("https://app.corp/x"), "a=browser; b=browser"));
    }

    [Fact]
    public void A_secure_cookie_is_not_sent_to_a_plain_http_upstream()
    {
        var jar = new GatewayCookieJar();
        jar.Store(new Uri("https://app.corp/"), new[] { "s=1; Secure; Path=/" });
        Assert.Null(jar.Merge(new Uri("http://app.corp/"), null));
        Assert.Equal("s=1", jar.Merge(new Uri("https://app.corp/"), null));
    }
}

public class CookieJarFileTests
{
    [Fact]
    public void A_jar_round_trips_through_the_file_and_drops_expired_cookies()
    {
        string path = Path.Combine(Path.GetTempPath(), "certapi-jar-" + Guid.NewGuid().ToString("N"), "jar.json");
        try
        {
            var jar = new CookieContainer();
            jar.Add(new Cookie("sid", "abc", "/", "app.corp") { HttpOnly = true });
            jar.Add(new Cookie("keep", "1", "/", "app.corp") { Expires = DateTime.UtcNow.AddHours(1) });
            jar.Add(new Cookie("gone", "0", "/", "app.corp") { Expires = DateTime.UtcNow.AddHours(-1) });
            Assert.Equal(2, CookieJarFile.Save(path, jar));

            var loaded = new CookieContainer();
            Assert.Equal(2, CookieJarFile.Load(path, loaded));
            string header = loaded.GetCookieHeader(new Uri("http://app.corp/"));
            Assert.Contains("sid=abc", header);
            Assert.Contains("keep=1", header);
            Assert.DoesNotContain("gone", header);
        }
        finally { if (File.Exists(path)) Directory.Delete(Path.GetDirectoryName(path)!, true); }
    }

    [Fact]
    public void A_missing_file_is_an_empty_jar_and_garbage_is_refused()
    {
        var jar = new CookieContainer();
        Assert.Equal(0, CookieJarFile.Load(Path.Combine(Path.GetTempPath(), "nope-" + Guid.NewGuid()), jar));
        string bad = Path.GetTempFileName();
        File.WriteAllText(bad, "not json");
        try { Assert.Throws<InvalidDataException>(() => CookieJarFile.Load(bad, jar)); }
        finally { File.Delete(bad); }
    }
}

public class RequestHeaderRewriteTests
{
    private static readonly BrowserOptions Options = new(false, null, false, false, false, new Uri("http://127.0.0.1:8819")) { RewriteOrigin = true };
    private static readonly GatewayRoute Route = new("/auth", new Uri("https://login.corp"));

    [Fact]
    public void Origin_and_referer_from_the_gateway_are_mapped_and_others_left_alone()
    {
        var headers = new[]
        {
            new KeyValuePair<string, string>("Origin", "http://127.0.0.1:8819"),
            new KeyValuePair<string, string>("Referer", "http://127.0.0.1:8819/auth/form?x=1"),
            new KeyValuePair<string, string>("X-Other", "v")
        };
        var result = BrowserRewriter.RewriteRequestHeaders(headers, Route, Options,
            p => p.StartsWith("/auth") ? new Uri("https://login.corp" + p[5..]) : null);
        Assert.Equal("https://login.corp", result.Single(h => h.Key == "Origin").Value);
        Assert.Equal("https://login.corp/form?x=1", result.Single(h => h.Key == "Referer").Value);
        Assert.Equal("v", result.Single(h => h.Key == "X-Other").Value);

        var foreign = new[] { new KeyValuePair<string, string>("Origin", "https://evil.example") };
        Assert.Equal("https://evil.example", BrowserRewriter.RewriteRequestHeaders(foreign, Route, Options, _ => null).Single().Value);
        Assert.Same(headers, BrowserRewriter.RewriteRequestHeaders(headers, Route, Options with { RewriteOrigin = false }, _ => null));
    }

    [Fact]
    public void A_cross_host_location_is_rewritten_when_the_gateway_can_serve_it()
    {
        var options = new BrowserOptions(false, null, false, RewriteLocation: true, false, new Uri("http://127.0.0.1:8819"))
        {
            GatewayPathFor = u => u.Host == "app.corp" ? "/app" + u.PathAndQuery : null
        };
        string Rewrite(string loc) => BrowserRewriter.RewriteResponseHeaders(
            new[] { new KeyValuePair<string, string>("Location", loc) }, null, Route, options, out _).Single().Value;
        Assert.Equal("http://127.0.0.1:8819/app/home?sso=1", Rewrite("https://app.corp/home?sso=1"));
        Assert.Equal("https://elsewhere.example/x", Rewrite("https://elsewhere.example/x"));
        Assert.Equal("http://127.0.0.1:8819/auth/next", Rewrite("https://login.corp/next"));
    }
}

public class RefererFallbackTests
{
    private static MtlsGateway Gateway()
    {
        var gw = new MtlsGateway(new GatewayRoutes(new[] { new GatewayRoute("/api", new Uri("https://api.static")) }),
            clientCertificate: null, ignoreServerCertificateErrors: true, TimeSpan.FromSeconds(5));
        gw.EnableDynamicUpstreams(new UpstreamAllowlist(new[] { "*.corp" }));
        return gw;
    }

    private static readonly Uri Local = new("http://127.0.0.1:8819");

    [Fact]
    public void An_absolute_path_from_a_dynamic_page_lands_under_that_host()
    {
        using var gw = Gateway();
        Assert.Equal("/_/https://app.corp/static/app.js?v=2",
            gw.FallbackPathFor("/static/app.js?v=2", "http://127.0.0.1:8819/_/https://app.corp/index.html", Local));
    }

    [Fact]
    public void An_absolute_path_from_a_mounted_page_lands_under_that_mount()
    {
        using var gw = Gateway();
        Assert.Equal("/api/static/app.js", gw.FallbackPathFor("/static/app.js", "http://127.0.0.1:8819/api/page", Local));
    }

    [Fact]
    public void A_path_that_resolves_a_foreign_referer_or_no_referer_is_left_alone()
    {
        using var gw = Gateway();
        Assert.Null(gw.FallbackPathFor("/api/x", "http://127.0.0.1:8819/_/https://app.corp/", Local));   // already owned
        Assert.Null(gw.FallbackPathFor("/static/x", "https://evil.example/page", Local));                   // not the gateway's
        Assert.Null(gw.FallbackPathFor("/static/x", null, Local));
        Assert.Null(gw.FallbackPathFor("/static/x", "http://127.0.0.1:8819/nowhere", Local));               // referer resolves nowhere
        Assert.Null(gw.FallbackPathFor("/static/x", "http://127.0.0.1:8819/_/https://evil.example/", Local)); // referer host not allowed
    }

    [Fact]
    public async Task The_gateway_redirects_with_307_so_the_browser_lands_on_the_canonical_path()
    {
        var (ca, server, client) = ServeFixture.Certs();
        using (ca) using (server) using (client)
        {
            await using var app = await LoopbackMtlsServer.StartEchoAsync(server, client.Thumbprint!);
            var appUri = new Uri(app.BaseUrl);
            await using var serve = ServeHost.Start(client, "--allow", appUri.Host, "--referer-fallback");

            using var http = ServeFixture.NewClient();
            var req = new HttpRequestMessage(HttpMethod.Get, $"{serve.Origin}/static/app.js");
            req.Headers.TryAddWithoutValidation("Referer", $"{serve.Origin}/_/{appUri.Scheme}://{appUri.Authority}/index.html");
            var resp = await ServeFixture.Poll(() => http.SendAsync(req));
            Assert.Equal(HttpStatusCode.TemporaryRedirect, resp.StatusCode);
            Assert.Equal($"{serve.Origin}/_/{appUri.Scheme}://{appUri.Authority}/static/app.js", resp.Headers.Location!.ToString());

            // Following it reaches the upstream with the right path.
            string echo = await http.GetStringAsync(resp.Headers.Location);
            Assert.Contains("GET /static/app.js HTTP/1.1", echo);

            // Without a Referer the path is still a 404.
            var bare = await http.GetAsync($"{serve.Origin}/static/app.js");
            Assert.Equal(HttpStatusCode.NotFound, bare.StatusCode);
        }
    }
}

public class ServeWindowsAuthTests
{
    [Fact]
    public void The_gateway_handler_carries_the_credentials_and_preauthenticates()
    {
        // Wrapped so that they answer Negotiate/NTLM only, never a Basic or Digest challenge.
        var upstream = new Uri("https://upstream.example/");
        using var sso = MtlsGateway.CreateHandler(null, true, null, null, new WindowsAuthOptions(true));
        var ssoCreds = Assert.IsType<IntegratedOnlyCredentials>(sso.Credentials);
        Assert.Same(CredentialCache.DefaultCredentials.GetCredential(upstream, "Negotiate"), ssoCreds.GetCredential(upstream, "Negotiate"));
        Assert.Null(ssoCreds.GetCredential(upstream, "Basic"));
        Assert.True(sso.PreAuthenticate);

        using var explicitCreds = MtlsGateway.CreateHandler(null, true, null, null, WindowsAuthOptions.FromCredentials(@"CORP\matt", "pw"));
        var wrapped = Assert.IsType<IntegratedOnlyCredentials>(explicitCreds.Credentials);
        Assert.Null(wrapped.GetCredential(upstream, "Basic"));
        var cred = wrapped.GetCredential(upstream, "NTLM")!;
        Assert.Equal("matt", cred.UserName);
        Assert.Equal("CORP", cred.Domain);
        Assert.Equal("pw", cred.Password);

        using var none = MtlsGateway.CreateHandler(null, true, null, null);
        Assert.Null(none.Credentials);
        Assert.False(none.PreAuthenticate);
    }

    [Fact]
    public void A_password_without_a_user_is_a_usage_error()
    {
        var stderr = new StringWriter();
        int exit = CliApp.Run(new[] { "serve", "https://x", "--port", "1", "--windows-password", "pw" }, TextWriter.Null, stderr);
        Assert.Equal(ExitCodes.Usage, exit);
        Assert.Contains("--windows-user", stderr.ToString());
    }

    [Fact]
    public async Task The_gateway_answers_an_upstream_ntlm_challenge_on_the_callers_behalf()
    {
        // NTLM with default credentials needs the Windows security package; elsewhere the route
        // only proves the challenge is relayed untouched when the flag is absent.
        await using var upstream = MockServer.Start(0, MockTlsMode.Http);
        var (ca, server, client) = ServeFixture.Certs();
        using (ca) using (server) using (client)
        {
            await using var plain = ServeHost.Start(client, upstream.BaseUrl.TrimEnd('/'));
            using var http = ServeFixture.NewClient();
            var challenged = await ServeFixture.Poll(() => http.GetAsync($"{plain.Origin}/windows-auth"));
            Assert.Equal(HttpStatusCode.Unauthorized, challenged.StatusCode);
            Assert.Contains("NTLM", string.Join(",", challenged.Headers.WwwAuthenticate.Select(w => w.Scheme)));

            if (!OperatingSystem.IsWindows()) return;
            await using var sso = ServeHost.Start(client, upstream.BaseUrl.TrimEnd('/'), "--windows-auth");
            var authed = await ServeFixture.Poll(() => http.GetAsync($"{sso.Origin}/windows-auth"));
            Assert.Equal(HttpStatusCode.OK, authed.StatusCode);
            Assert.Contains("\"authenticated\":\"NTLM\"", await authed.Content.ReadAsStringAsync());
        }
    }
}

public class GatewayCookieJarFileTests
{
    /// <summary>A protector that encrypts: enough to prove the jar is never written in the clear
    /// and round-trips through whatever protector the machine has.</summary>
    private sealed class FakeProtector : ISecretProtector
    {
        public bool IsAvailable => true;
        public string? Protect(string plaintext) =>
            SecretProtection.Prefix + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(plaintext).Select(b => (byte)(b ^ 0x5A)).Concat(new byte[32]).ToArray());
        public string? Unprotect(string stored)
        {
            if (!stored.StartsWith(SecretProtection.Prefix)) return null;
            var bytes = Convert.FromBase64String(stored[SecretProtection.Prefix.Length..]);
            return System.Text.Encoding.UTF8.GetString(bytes[..^32].Select(b => (byte)(b ^ 0x5A)).ToArray());
        }
    }

    [Fact]
    public void The_jar_round_trips_encrypted_and_refuses_plaintext_or_an_unavailable_protector()
    {
        string path = Path.Combine(Path.GetTempPath(), "certapi-gwjar-" + Guid.NewGuid().ToString("N") + ".bin");
        try
        {
            var jar = new GatewayCookieJar();
            jar.Store(new Uri("https://login.example.corp/"), new[] { "sid=abc; Domain=.example.corp; Path=/" });
            jar.Save(path, new FakeProtector());

            string onDisk = File.ReadAllText(path);
            Assert.DoesNotContain("abc", onDisk);
            Assert.StartsWith(SecretProtection.Prefix, onDisk);

            var reloaded = new GatewayCookieJar();
            Assert.Equal(1, reloaded.Load(path, new FakeProtector()));
            Assert.Equal("sid=abc", reloaded.Merge(new Uri("https://app.example.corp/x"), null));

            Assert.Throws<InvalidOperationException>(() => jar.Save(path + ".x", new UnavailableSecretProtector()));
            File.WriteAllText(path, "[]");
            Assert.Throws<InvalidDataException>(() => new GatewayCookieJar().Load(path, new FakeProtector()));
            Assert.Equal(0, new GatewayCookieJar().Load(path + ".missing", new FakeProtector()));
        }
        finally { File.Delete(path); File.Delete(path + ".x"); }
    }

    [Fact]
    public void Serve_refuses_a_jar_file_where_the_machine_cannot_encrypt()
    {
        if (SecretProtection.Default.IsAvailable) return;   // Windows: the file form works there
        var err = new StringWriter();
        int code = CliApp.Run(new[] { "serve", "https://x", "--port", "1", "--cookie-jar", Path.GetTempFileName() }, TextWriter.Null, err);
        Assert.Equal(ExitCodes.Data, code);
        Assert.Contains("DPAPI", err.ToString());
    }
}
