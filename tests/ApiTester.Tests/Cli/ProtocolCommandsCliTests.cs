using System.IO;
using System.Net.Http;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using ApiTester.Cli;
using ApiTester.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ApiTester.Tests.Cli;

/// <summary>The 1.98 command-line additions end to end: the curl-compatible send flags, -w,
/// call, crawl, tls, token's certificate client authentication and code grant, signalr against a
/// real ASP.NET Core hub, mqtt against a small broker, and browse's argument checks.</summary>
public class ProtocolCommandsCliTests
{
    private static string TempState() => Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");

    private static (int Code, string Out, string Err, string Body) Run(params string[] args) =>
        RunWith(new CliServices { LiveStatePath = TempState(), UserConfigPath = null }, args);

    private static (int Code, string Out, string Err, string Body) RunWith(CliServices services, params string[] args)
    {
        var so = new StringWriter();
        var se = new StringWriter();
        var body = new MemoryStream();
        int code = CliApp.Run(args.Append("--no-config").ToArray(), so, se, body, services);
        return (code, so.ToString(), se.ToString(), Encoding.UTF8.GetString(body.ToArray()));
    }

    private static (int Code, string Out, string Err) RunStreaming(params string[] args)
    {
        var so = new StringWriter();
        var se = new StringWriter();
        int code = CliApp.Run(args.Append("--no-config").ToArray(), new StringReader(""), so, se, new MemoryStream(),
            new CliServices { LiveStatePath = TempState(), UserConfigPath = null });
        return (code, so.ToString(), se.ToString());
    }

    private static JsonElement Echo(string body) => JsonDocument.Parse(body).RootElement;

    // ---- send: curl-compatible additions ----

    [Fact]
    public async Task Data_urlencode_encodes_each_value_joins_with_data_and_posts_a_form()
    {
        await using var srv = MockServer.Start(0, MockTlsMode.Http);
        var r = Run("send", srv.BaseUrl.TrimEnd('/') + "/echo", "-d", "x=1", "--data-urlencode", "q=a b&c",
            "--data-urlencode", "=solo value", "-s");
        Assert.Equal(0, r.Code);
        var e = Echo(r.Body);
        Assert.Equal("POST", e.GetProperty("method").GetString());
        Assert.Equal("x=1&q=a%20b%26c&solo%20value", e.GetProperty("body").GetString());
        Assert.StartsWith("application/x-www-form-urlencoded", e.GetProperty("headers").GetProperty("Content-Type").GetString());
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("=x y", "x%20y")]
    [InlineData("name=a+b", "name=a%2Bb")]
    public void Url_encoded_parts_follow_curls_forms(string spec, string expected) =>
        Assert.Equal(expected, ApiTester.Cli.Commands.SendCommand.UrlEncodedPart(spec));

    [Fact]
    public async Task Cookie_sends_a_string_or_the_matching_lines_of_a_Netscape_cookie_file()
    {
        await using var srv = MockServer.Start(0, MockTlsMode.Http);
        var direct = Run("send", srv.BaseUrl.TrimEnd('/') + "/echo", "-b", "a=1; b=2", "-s");
        Assert.Equal("a=1; b=2", Echo(direct.Body).GetProperty("headers").GetProperty("Cookie").GetString());

        string jar = Path.GetTempFileName();
        File.WriteAllText(jar, string.Join("\n",
            "# Netscape HTTP Cookie File",
            "127.0.0.1\tFALSE\t/\tFALSE\t0\tsid\tabc",
            "#HttpOnly_127.0.0.1\tFALSE\t/\tFALSE\t0\thid\tdef",
            "127.0.0.1\tFALSE\t/other\tFALSE\t0\tnope\tx",
            "127.0.0.1\tFALSE\t/\tFALSE\t1\texpired\tx",
            "elsewhere.example\tFALSE\t/\tFALSE\t0\tforeign\tx"));
        try
        {
            var fromFile = Run("send", srv.BaseUrl.TrimEnd('/') + "/echo", "--cookie", jar, "-s");
            Assert.Equal(0, fromFile.Code);
            string cookie = Echo(fromFile.Body).GetProperty("headers").GetProperty("Cookie").GetString()!;
            Assert.Contains("sid=abc", cookie);
            Assert.Contains("hid=def", cookie);
            Assert.DoesNotContain("nope", cookie);
            Assert.DoesNotContain("expired", cookie);
            Assert.DoesNotContain("foreign", cookie);
        }
        finally { File.Delete(jar); }
    }

    [Fact]
    public async Task Max_filesize_refuses_a_body_larger_than_the_cap()
    {
        string big = new('x', 4000);
        await using var server = new RawHttpServer(_ => RawHttpServer.Response(200, "OK", big));
        var refused = Run("send", server.BaseUrl + "/", "--max-filesize", "1k");
        Assert.NotEqual(0, refused.Code);
        Assert.Contains("--max-filesize", refused.Err + refused.Out);
        Assert.Equal("", refused.Body);
        var allowed = Run("send", server.BaseUrl + "/", "--max-filesize", "10k", "-s");
        Assert.Equal(0, allowed.Code);
        Assert.Equal(big, allowed.Body);
    }

    [Fact]
    public async Task Range_asks_for_part_of_the_body()
    {
        await using var server = new RawHttpServer(_ => RawHttpServer.Response(206, "Partial Content", "abc"));
        var r = Run("send", server.BaseUrl + "/f", "-r", "0-2", "-s");
        Assert.Equal(0, r.Code);
        Assert.True(server.Requests.TryDequeue(out var seen));
        Assert.Equal("bytes=0-2", seen!.Header("Range"));
    }

    [Fact]
    public async Task Continue_at_resumes_a_download_from_the_end_of_the_file()
    {
        await using var server = new RawHttpServer(req =>
            req.Header("Range") == "bytes=5-"
                ? RawHttpServer.Response(206, "Partial Content", " world", "Content-Range: bytes 5-10/11")
                : RawHttpServer.Response(200, "OK", "hello world"));
        string file = Path.GetTempFileName();
        File.WriteAllText(file, "hello");
        try
        {
            var r = Run("send", server.BaseUrl + "/f", "-C", "-", "-o", file, "-s");
            Assert.Equal(0, r.Code);
            Assert.Equal("hello world", File.ReadAllText(file));
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void Continue_at_without_an_output_file_is_a_usage_error()
    {
        var r = Run("send", "http://127.0.0.1:9/", "-C", "-");
        Assert.Equal(2, r.Code);
    }

    [Fact]
    public async Task Write_out_prints_the_variables_after_the_transfer()
    {
        await using var srv = MockServer.Start(0, MockTlsMode.Http);
        var r = Run("send", srv.BaseUrl.TrimEnd('/') + "/status/201", "-w", "%{http_code} %{method} %{num_redirects}\\n", "-s");
        Assert.Equal(0, r.Code);
        Assert.Contains("201 GET 0", r.Out);

        var times = Run("send", srv.BaseUrl.TrimEnd('/') + "/echo", "-w", "%{time_total}", "-s");
        Assert.Matches(@"\d+\.\d{6}", times.Out);

        var header = Run("send", srv.BaseUrl.TrimEnd('/') + "/echo", "-w", "%{header{content-type}}", "-s");
        Assert.Contains("application/json", header.Out);
    }

    [Fact]
    public void An_unknown_write_out_variable_is_a_usage_error()
    {
        var r = Run("send", "http://127.0.0.1:9/", "-w", "%{no_such_thing}");
        Assert.Equal(2, r.Code);
        Assert.Contains("no_such_thing", r.Err);
    }

    [Fact]
    public async Task Connect_to_and_proxy_header_are_parsed_on_send()
    {
        await using var server = new RawHttpServer(_ => RawHttpServer.Response(200, "OK", "here"));
        var r = Run("send", "http://api.invalid/x", "--connect-to", $"api.invalid:80:127.0.0.1:{server.Port}", "-s");
        Assert.Equal(0, r.Code);
        Assert.Equal("here", r.Body);
        var bad = Run("send", "http://127.0.0.1:9/", "--proxy-header", "X-A: 1");
        Assert.NotEqual(0, bad.Code);
        Assert.Contains("--proxy", bad.Err);
    }

    [Fact]
    public async Task Proxy_header_reaches_the_proxy_on_the_CONNECT_and_not_the_origin()
    {
        await using var proxy = await LoopbackConnectProxy.StartRequiringBasicAuthAsync("pu", "pp");
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("Tunnel Client", ca, false, true);
        string pfx = WritePfx(clientCert);
        await using var origin = await LoopbackMtlsServer.StartEchoAsync(serverCert, clientCert.Thumbprint);
        string auth = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("pu:pp"));
        var r = Run("send", origin.BaseUrl, "--proxy", proxy.Url, "--proxy-header", "Proxy-Authorization: " + auth,
            "--cert-file", pfx, "--cert-password", "pw", "--insecure", "-s",
            "--noproxy", "only.this.invalid");   // a NO_PROXY in the environment must not bypass the loopback proxy
        File.Delete(pfx);
        Assert.True(r.Code == 0, r.Err);
        Assert.Equal(1, proxy.ConnectCount);
        Assert.Contains(auth, proxy.ProxyAuthorizations);
        Assert.DoesNotContain("Proxy-Authorization", r.Body);
    }

    // ---- call ----

    private static string PetSpec(string baseUrl) =>
        """
        {"openapi":"3.0.0","servers":[{"url":"BASE"}],"paths":{"/pets/{petId}":{"get":{"operationId":"getPet","parameters":[{"name":"petId","in":"path","required":true},{"name":"verbose","in":"query"},{"name":"X-Trace","in":"header"}],"responses":{"200":{"content":{"application/json":{"schema":{"type":"object","required":["server","path"],"properties":{"server":{"type":"string"},"path":{"type":"string"}}}}}}}}},"/strict":{"get":{"operationId":"strict","responses":{"200":{"content":{"application/json":{"schema":{"type":"object","required":["id"]}}}}}}}}}
        """.Replace("BASE", baseUrl);

    [Fact]
    public async Task Call_places_each_parameter_and_checks_the_response_against_the_schema()
    {
        await using var srv = MockServer.Start(0, MockTlsMode.Http);
        string spec = Path.GetTempFileName();
        File.WriteAllText(spec, PetSpec(srv.BaseUrl.TrimEnd('/')));
        try
        {
            var r = Run("call", spec, "getPet", "--param", "petId=7", "--param", "verbose=true", "--param", "X-Trace=abc");
            Assert.Equal(0, r.Code);
            var e = Echo(r.Body);
            Assert.Equal("/pets/7", e.GetProperty("path").GetString());
            Assert.Equal("verbose=true", e.GetProperty("query").GetString());
            Assert.Equal("abc", e.GetProperty("headers").GetProperty("X-Trace").GetString());

            var fails = Run("call", spec, "strict");
            Assert.Equal(1, fails.Code);
            Assert.Equal(0, Run("call", spec, "strict", "--no-validate").Code);

            var missing = Run("call", spec, "getPet");
            Assert.Equal(2, missing.Code);
            Assert.Contains("petId", missing.Err);

            var list = Run("call", spec, "--list");
            Assert.Equal(0, list.Code);
            Assert.Contains("getPet", list.Out);
            Assert.Contains("strict", list.Out);
        }
        finally { File.Delete(spec); }
    }

    // ---- crawl ----

    [Fact]
    public async Task Crawl_follows_in_scope_links_obeys_robots_and_reports_broken_ones()
    {
        await using var site = new RawHttpServer(req => req.Path switch
        {
            "/" => RawHttpServer.Response(200, "OK", "<a href=\"/a\">a</a> <a href=\"/missing\">m</a> <a href=\"/private/x\">p</a>",
                "Content-Type: text/html"),
            "/a" => RawHttpServer.Response(200, "OK", "<img src=\"/img.png\">", "Content-Type: text/html"),
            "/img.png" => RawHttpServer.Response(200, "OK", "png", "Content-Type: image/png"),
            "/robots.txt" => RawHttpServer.Response(200, "OK", "User-agent: *\nDisallow: /private/", "Content-Type: text/plain"),
            _ => RawHttpServer.Response(404, "Not Found", "")
        });
        var r = Run("crawl", site.BaseUrl + "/", "--json");
        Assert.Equal(1, r.Code);   // one broken link
        var visits = r.Out.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => JsonDocument.Parse(l).RootElement).ToList();
        string StatusOf(string path) => visits.Where(v => v.GetProperty("Url").GetString()!.EndsWith(path, StringComparison.Ordinal))
            .Select(v => v.GetProperty("Status").ValueKind == JsonValueKind.Number ? v.GetProperty("Status").GetInt32().ToString() : "none").FirstOrDefault() ?? "unseen";
        Assert.Equal("200", StatusOf("/a"));
        Assert.Equal("200", StatusOf("/img.png"));
        Assert.Equal("404", StatusOf("/missing"));
        Assert.DoesNotContain(site.Requests, q => q.Path.StartsWith("/private", StringComparison.Ordinal));

        string mirror = Path.Combine(Path.GetTempPath(), "certapi-mirror-" + Guid.NewGuid().ToString("N")[..6]);
        try
        {
            var m = Run("crawl", site.BaseUrl + "/", "--mirror", mirror);
            Assert.True(Directory.Exists(mirror), m.Err);
            string listing = string.Join("\n", Directory.GetFiles(mirror, "*", SearchOption.AllDirectories));
            Assert.True(File.Exists(Path.Combine(mirror, $"127.0.0.1_{site.Port}", "index.html")), listing);
            Assert.True(File.Exists(Path.Combine(mirror, $"127.0.0.1_{site.Port}", "index.html")));
            Assert.True(File.Exists(Path.Combine(mirror, $"127.0.0.1_{site.Port}", "img.png")));
        }
        finally { if (Directory.Exists(mirror)) Directory.Delete(mirror, true); }
    }

    // ---- tls ----

    [Fact]
    public async Task Tls_reports_the_handshake_and_that_a_client_certificate_was_asked_for()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("Probe CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("Probe Client", ca, false, true);
        string pfx = WritePfx(clientCert);
        await using var server = await LoopbackMtlsServer.StartAsync(serverCert, clientCert.Thumbprint);
        var uri = new Uri(server.BaseUrl);
        var r = Run("tls", $"127.0.0.1:{uri.Port}", "--cert-file", pfx, "--cert-password", "pw", "--json");
        File.Delete(pfx);
        Assert.Equal(0, r.Code);
        var j = JsonDocument.Parse(r.Out).RootElement;
        Assert.True(j.GetProperty("Connected").GetBoolean());
        Assert.False(string.IsNullOrEmpty(j.GetProperty("Protocol").GetString()));
        Assert.True(j.GetProperty("ClientCertificateRequested").GetBoolean());
        Assert.False(j.GetProperty("ServerTrusted").GetBoolean());   // a private CA
        Assert.Contains("Probe Client", j.GetProperty("ClientCertificateSent").GetString());
    }

    [Fact]
    public void Tls_to_a_closed_port_fails_with_exit_1()
    {
        var r = Run("tls", "127.0.0.1:9", "--timeout", "3");
        Assert.Equal(1, r.Code);
    }

    // ---- token: certificate client authentication and the code grant ----

    private static string Field(byte[] form, string name) =>
        Encoding.UTF8.GetString(form).Split('&').Select(p => p.Split('=', 2))
            .Where(kv => kv[0] == name).Select(kv => Uri.UnescapeDataString(kv[1].Replace('+', ' '))).FirstOrDefault() ?? "";

    private static string WritePfx(X509Certificate2 cert)
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".pfx");
        File.WriteAllBytes(path, cert.Export(X509ContentType.Pfx, "pw"));
        return path;
    }

    [Fact]
    public async Task Private_key_jwt_sends_a_client_assertion_signed_by_the_certificate()
    {
        await using var server = new RawHttpServer(_ => RawHttpServer.Response(200, "OK",
            "{\"access_token\":\"tok\",\"token_type\":\"Bearer\",\"expires_in\":60}", "Content-Type: application/json"));
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var client = SelfSignedCertificateFactory.CreateSignedCertificate("JwtClient", ca, false, true);
        string pfx = WritePfx(client);
        try
        {
            var r = Run("token", "--grant", "client_credentials", "--token-url", server.BaseUrl + "/token", "--client-id", "app-1",
                "--client-auth", "private_key_jwt", "--cert-file", pfx, "--cert-password", "pw");
            Assert.Equal(0, r.Code);
            Assert.True(server.Requests.TryDequeue(out var seen));
            Assert.Equal(ClientAssertion.Type, Field(seen!.Body, "client_assertion_type"));
            Assert.Equal("app-1", Field(seen.Body, "client_id"));
            Assert.Equal("", Field(seen.Body, "client_secret"));
            string jwt = Field(seen.Body, "client_assertion");
            var parts = jwt.Split('.');
            Assert.Equal(3, parts.Length);
            string payload = Encoding.UTF8.GetString(Convert.FromBase64String(parts[1].Replace('-', '+').Replace('_', '/').PadRight((parts[1].Length + 3) / 4 * 4, '=')));
            Assert.Contains(server.BaseUrl + "/token", payload);
            using var pub = client.GetRSAPublicKey()!;
            Assert.True(pub.VerifyData(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]),
                Convert.FromBase64String(parts[2].Replace('-', '+').Replace('_', '/').PadRight((parts[2].Length + 3) / 4 * 4, '=')),
                System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1));
        }
        finally { File.Delete(pfx); }
    }

    [Fact]
    public void Certificate_client_auth_without_a_certificate_or_with_a_secret_is_a_usage_error()
    {
        Assert.Equal(2, Run("token", "--token-url", "http://127.0.0.1:9/t", "--client-id", "a", "--client-auth", "private_key_jwt").Code);
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var client = SelfSignedCertificateFactory.CreateSignedCertificate("C", ca, false, true);
        string pfx = WritePfx(client);
        try
        {
            var r = Run("token", "--token-url", "http://127.0.0.1:9/t", "--client-id", "a", "--client-auth", "tls_client_auth",
                "--client-secret", "s", "--cert-file", pfx, "--cert-password", "pw");
            Assert.Equal(2, r.Code);
            Assert.Contains("--client-secret", r.Err);
        }
        finally { File.Delete(pfx); }
    }

    [Fact]
    public async Task The_code_grant_catches_the_redirect_and_exchanges_the_code_with_the_PKCE_verifier()
    {
        await using var server = new RawHttpServer(_ => RawHttpServer.Response(200, "OK",
            "{\"access_token\":\"from-code\",\"token_type\":\"Bearer\"}", "Content-Type: application/json"));
        string? opened = null;
        var services = new CliServices
        {
            LiveStatePath = TempState(),
            UserConfigPath = null,
            OpenFile = url =>
            {
                opened = url;
                var q = System.Web.HttpUtility.ParseQueryString(new Uri(url).Query);
                string redirect = q["redirect_uri"]!;
                _ = Task.Run(async () =>
                {
                    using var http = new HttpClient();
                    for (int i = 0; i < 50; i++)
                    {
                        try { await http.GetAsync($"{redirect}?code=the-code&state={q["state"]}"); return; }
                        catch (HttpRequestException) { await Task.Delay(100); }
                    }
                });
            }
        };
        var r = RunWith(services, "token", "--grant", "code", "--authorize-url", "https://idp.example/authorize",
            "--token-url", server.BaseUrl + "/token", "--client-id", "spa", "--scope", "openid");
        Assert.Equal(0, r.Code);
        Assert.Contains("from-code", r.Out);
        Assert.StartsWith("https://idp.example/authorize?", opened);
        Assert.Contains("code_challenge_method=S256", opened);
        Assert.True(server.Requests.TryDequeue(out var seen));
        Assert.Equal("authorization_code", Field(seen!.Body, "grant_type"));
        Assert.Equal("the-code", Field(seen.Body, "code"));
        string verifier = Field(seen.Body, "code_verifier");
        Assert.True(verifier.Length >= 43);
        var challenge = System.Web.HttpUtility.ParseQueryString(new Uri(opened!).Query)["code_challenge"];
        Assert.Equal(OAuthAuthorization.CodeChallenge(verifier), challenge);
    }

    // ---- gRPC-Web ----

    [Fact]
    public void Grpc_web_without_a_descriptor_set_is_a_usage_error()
    {
        var r = RunStreaming("grpc", "call", "http://127.0.0.1:9", "certapi.test.Echo/Unary", "--grpc-web");
        Assert.Equal(2, r.Code);
        Assert.Contains("--protoset", r.Err);
    }

    // ---- signalr ----

    public sealed class TestHub : Hub
    {
        public string Echo(string text) => text;

        public Task Broadcast(string text) => Clients.Caller.SendAsync("Said", text, 42);

        public void Fail() => throw new HubException("nope");

        public async IAsyncEnumerable<int> Counter(int count)
        {
            for (int i = 0; i < count; i++)
            {
                await Task.Yield();
                yield return i;
            }
        }
    }

    private static async Task<(WebApplication App, string Url)> StartHubAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = AppContext.BaseDirectory });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0));
        builder.Services.AddSignalR(o => o.EnableDetailedErrors = true);
        var app = builder.Build();
        app.UseWebSockets();
        app.MapHub<TestHub>("/hub");
        await app.StartAsync();
        string address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return (app, address + "/hub");
    }

    [Fact]
    public async Task Signalr_invokes_a_method_and_prints_its_result()
    {
        var (app, hub) = await StartHubAsync();
        await using var _ = app;
        var r = RunStreaming("signalr", hub, "--invoke", "Echo \"hello\"", "-q");
        Assert.Equal(0, r.Code);
        Assert.Equal("\"hello\"", r.Out.Trim());
    }

    [Fact]
    public async Task Signalr_listens_for_events_the_hub_sends()
    {
        var (app, hub) = await StartHubAsync();
        await using var _ = app;
        var r = RunStreaming("signalr", hub, "--send", "Broadcast \"hi\"", "--on", "Said", "--count", "1", "--seconds", "20", "-q");
        Assert.Equal(0, r.Code);
        Assert.Equal("Said \"hi\" 42", r.Out.Trim());
    }

    [Fact]
    public async Task Signalr_streams_items_and_reports_a_hub_error()
    {
        var (app, hub) = await StartHubAsync();
        await using var _ = app;
        var stream = RunStreaming("signalr", hub, "--stream", "Counter 3", "-q");
        Assert.Equal(0, stream.Code);
        Assert.Equal(new[] { "0", "1", "2" }, stream.Out.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()));

        var failed = RunStreaming("signalr", hub, "--invoke", "Fail", "-q");
        Assert.Equal(1, failed.Code);
        Assert.Contains("nope", failed.Err);
    }

    [Fact]
    public async Task Signalr_against_something_that_is_not_a_hub_says_so()
    {
        await using var srv = new RawHttpServer(_ => RawHttpServer.Response(404, "Not Found"));
        var r = RunStreaming("signalr", srv.BaseUrl + "/nothub", "--invoke", "X");
        Assert.Equal(1, r.Code);
        Assert.Contains("negotiate", r.Err);
    }

    // ---- mqtt ----

    /// <summary>Just enough broker: CONNACK (or a refusal), SUBACK, PUBACK, and every publish
    /// delivered back to the connection when it subscribed to a matching filter.</summary>
    private sealed class TinyBroker : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _cts = new();
        private readonly int _connAck;
        private readonly X509Certificate2? _tls;
        public string? ClientId, Username, ClientCertificateSubject, Failure;

        public TinyBroker(int connAck = 0, X509Certificate2? tls = null)
        {
            _connAck = connAck;
            _tls = tls;
            _listener.Start();
            _ = Task.Run(LoopAsync);
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        private async Task LoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient c;
                try { c = await _listener.AcceptTcpClientAsync(_cts.Token); } catch { return; }
                _ = Task.Run(() => ServeAsync(c));
            }
        }

        private async Task ServeAsync(TcpClient c)
        {
            using (c)
            {
                try
                {
                    Stream s = c.GetStream();
                    if (_tls is not null)
                    {
                        var ssl = new SslStream(s, false);
                        await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                        {
                            ServerCertificate = _tls,
                            ClientCertificateRequired = true,
                            RemoteCertificateValidationCallback = (_, _, _, _) => true
                        });
                        ClientCertificateSubject = ssl.RemoteCertificate?.Subject;
                        s = ssl;
                    }
                    var filters = new List<string>();
                    while (await MqttCodec.ReadAsync(s, _cts.Token) is { } p)
                    {
                        switch (p.Type)
                        {
                            case MqttCodec.Connect:
                                int at = 2 + 4;
                                at += 1;               // level
                                byte flags = p.Body[at++];
                                at += 2;               // keepalive
                                ClientId = Str(p.Body, ref at);
                                if ((flags & 0x80) != 0) Username = Str(p.Body, ref at);
                                await s.WriteAsync(new byte[] { 0x20, 2, 0, (byte)_connAck });
                                if (_connAck != 0) return;
                                break;
                            case MqttCodec.Subscribe:
                                int i = 2;
                                while (i < p.Body.Length) { filters.Add(Str(p.Body, ref i)); i++; }
                                var ack = new List<byte> { 0x90, (byte)(2 + filters.Count), p.Body[0], p.Body[1] };
                                ack.AddRange(filters.Select(f => f == "denied" ? (byte)0x80 : (byte)1));
                                await s.WriteAsync(ack.ToArray());
                                break;
                            case MqttCodec.Publish:
                                var msg = MqttCodec.DecodePublish(4, p, out var id);
                                if (msg.Qos == 1) await s.WriteAsync(MqttCodec.EncodePubAck(id));
                                if (filters.Any(f => f == "#" || f == msg.Topic || (f.EndsWith("/#") && msg.Topic.StartsWith(f[..^1]))))
                                    await s.WriteAsync(MqttCodec.EncodePublish(4, msg.Topic, msg.Payload, 0, false, 0));
                                break;
                            case MqttCodec.PingReq:
                                await s.WriteAsync(new byte[] { 0xD0, 0 });
                                break;
                            case MqttCodec.Disconnect:
                                return;
                        }
                    }
                }
                catch (Exception ex) { Failure = ex.ToString(); }
            }
        }

        private static string Str(byte[] b, ref int at)
        {
            int len = (b[at] << 8) | b[at + 1];
            string s = Encoding.UTF8.GetString(b, at + 2, len);
            at += 2 + len;
            return s;
        }

        public ValueTask DisposeAsync()
        {
            _cts.Cancel();
            _listener.Stop();
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task Mqtt_publishes_and_receives_its_own_message_through_a_subscription()
    {
        await using var broker = new TinyBroker();
        var r = RunStreaming("mqtt", $"mqtt://127.0.0.1:{broker.Port}", "-t", "sensors/#", "--publish", "sensors/7", "-m", "21.5",
            "--qos", "1", "--count", "1", "--seconds", "20", "--client-id", "dev-7", "--username", "ann", "-q");
        Assert.Equal(0, r.Code);
        Assert.Equal("sensors/7 21.5", r.Out.Trim());
        Assert.Equal("dev-7", broker.ClientId);
        Assert.Equal("ann", broker.Username);
    }

    [Fact]
    public async Task Mqtt_reports_a_refused_connection_and_a_refused_subscription()
    {
        await using (var refusing = new TinyBroker(connAck: 5))
        {
            var r = RunStreaming("mqtt", $"mqtt://127.0.0.1:{refusing.Port}", "-t", "a");
            Assert.Equal(1, r.Code);
            Assert.Contains("not authorized", r.Err);
        }
        await using var broker = new TinyBroker();
        var denied = RunStreaming("mqtt", $"mqtt://127.0.0.1:{broker.Port}", "-t", "denied");
        Assert.Equal(1, denied.Code);
        Assert.Contains("denied", denied.Err);
    }

    [Fact]
    public async Task Mqtts_presents_the_client_certificate()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("MQTT CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        using var client = SelfSignedCertificateFactory.CreateSignedCertificate("Device 7", ca, false, true);
        string pfx = WritePfx(client);
        try
        {
            await using var broker = new TinyBroker(tls: serverCert);
            var r = RunStreaming("mqtt", $"mqtts://127.0.0.1:{broker.Port}", "--publish", "t", "-m", "x", "--qos", "1",
                "--cert-file", pfx, "--cert-password", "pw", "--insecure", "-q");
            Assert.True(r.Code == 0, r.Err + broker.Failure);
            Assert.Contains("Device 7", broker.ClientCertificateSubject);
        }
        finally { File.Delete(pfx); }
    }

    [Theory]
    [InlineData("--qos", "2")]
    [InlineData("--publish", "a/+")]
    [InlineData("--mqtt-version", "4.0")]
    public void Mqtt_argument_mistakes_are_usage_errors(string flag, string value)
    {
        var r = RunStreaming("mqtt", "mqtt://127.0.0.1:9", "-t", "a", flag, value, "-m", "x");
        Assert.Equal(2, r.Code);
    }

    // ---- browse ----

    [Theory]
    [InlineData("ftp://x.example/")]
    [InlineData("https://x.example/", "--full-page")]
    [InlineData("https://x.example/", "--landscape")]
    [InlineData("https://x.example/", "--width", "10")]
    public void Browse_argument_mistakes_are_usage_errors(params string[] args)
    {
        var r = Run(new[] { "browse" }.Concat(args).ToArray());
        Assert.Equal(2, r.Code);
    }
}
