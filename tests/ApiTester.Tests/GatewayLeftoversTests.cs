using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using ApiTester.Core;
using ApiTester.Tests.Cli;

namespace ApiTester.Tests;

/// <summary>Body and Refresh rewriting, a site's own SNI, the gateway certificate under --vhost,
/// and /_certapi under a virtual host.</summary>
public class GatewayLeftoversTests
{
    private static readonly GatewayRoute Api = new("/api", new Uri("https://api.corp"));

    private static BrowserOptions Options(Func<Uri, string?>? pathFor = null) =>
        new(false, null, false, true, false, new Uri("http://127.0.0.1:8819")) { RewriteBody = true, GatewayPathFor = pathFor };

    private static string Rewrite(string body, string contentType = "text/html", Func<Uri, string?>? pathFor = null) =>
        BodyRewriter.Rewrite(Encoding.UTF8.GetBytes(body), null, contentType, Api, Options(pathFor)) is { } b
            ? Encoding.UTF8.GetString(b) : body;

    // ---- body ----

    [Fact]
    public void Absolute_and_protocol_relative_urls_to_the_upstream_go_through_the_gateway()
    {
        string html = """<a href="https://api.corp/orders?x=1">o</a><script src="//api.corp/app.js"></script><img src='https://API.corp:443/i.png'>""";
        Assert.Equal(
            """<a href="http://127.0.0.1:8819/api/orders?x=1">o</a><script src="http://127.0.0.1:8819/api/app.js"></script><img src='http://127.0.0.1:8819/api/i.png'>""",
            Rewrite(html));
    }

    [Fact]
    public void Json_escaped_slashes_stay_escaped()
    {
        Assert.Equal("""{"base":"http:\/\/127.0.0.1:8819\/api\/v1"}""",
            Rewrite("""{"base":"https:\/\/api.corp\/v1"}""", "application/json"));
    }

    [Fact]
    public void A_meta_refresh_is_rewritten_with_the_rest_of_the_page()
    {
        Assert.Contains("url=http://127.0.0.1:8819/api/home",
            Rewrite("""<meta http-equiv="refresh" content="0; url=https://api.corp/home">"""));
    }

    [Fact]
    public void Other_hosts_go_through_the_gateway_only_when_it_can_reach_them()
    {
        string body = """<a href="https://wiki.corp/p">w</a> <a href="https://elsewhere.example/">e</a>""";
        string result = Rewrite(body, pathFor: u => u.Host == "wiki.corp" ? "/_/https://wiki.corp" + u.PathAndQuery : null);
        Assert.Contains("http://127.0.0.1:8819/_/https://wiki.corp/p", result);
        Assert.Contains("https://elsewhere.example/", result);
    }

    [Fact]
    public void Nothing_to_rewrite_returns_null_so_the_original_bytes_go_out()
    {
        Assert.Null(BodyRewriter.Rewrite(Encoding.UTF8.GetBytes("<p>no links</p>"), null, "text/html", Api, Options()));
        Assert.Null(BodyRewriter.Rewrite(Encoding.UTF8.GetBytes("https://api.corp/"), null, "text/html; charset=iso-8859-1", Api, Options()));
        Assert.Null(BodyRewriter.Rewrite(new byte[] { 0xff, 0xfe, 0x00 }, null, "text/html", Api, Options()));
        Assert.Null(BodyRewriter.Rewrite(Encoding.UTF8.GetBytes("https://api.corp/"), "compress", "text/html", Api, Options()));
    }

    [Fact]
    public void A_compressed_body_is_decoded_and_sent_plain()
    {
        using var packed = new MemoryStream();
        using (var gz = new GZipStream(packed, CompressionLevel.Fastest, leaveOpen: true))
            gz.Write(Encoding.UTF8.GetBytes("""<a href="https://api.corp/x">"""));
        var result = BodyRewriter.Rewrite(packed.ToArray(), "gzip", "text/html", Api, Options());
        Assert.Equal("""<a href="http://127.0.0.1:8819/api/x">""", Encoding.UTF8.GetString(result!));
    }

    [Theory]
    [InlineData("text/html; charset=utf-8", true)]
    [InlineData("application/javascript", true)]
    [InlineData("application/problem+json", true)]
    [InlineData("text/css", true)]
    [InlineData("image/png", false)]
    [InlineData("application/octet-stream", false)]
    [InlineData(null, false)]
    public void Only_text_formats_are_read(string? contentType, bool applies) =>
        Assert.Equal(applies, BodyRewriter.Applies(contentType));

    // ---- Refresh header ----

    [Theory]
    [InlineData("0; url=https://api.corp/next", "0; url=http://127.0.0.1:8819/api/next")]
    [InlineData("5;URL='https://api.corp/a?b=1'", "5;URL='http://127.0.0.1:8819/api/a?b=1'")]
    [InlineData("3", "3")]
    public void A_refresh_header_is_rewritten_like_a_location(string upstream, string expected)
    {
        var headers = BrowserRewriter.RewriteResponseHeaders(
            new[] { new KeyValuePair<string, string>("Refresh", upstream) }, null, Api, Options(), out _);
        Assert.Equal(expected, headers.Single().Value);
    }

    // ---- SNI per site ----

    [Fact]
    public void A_routes_file_site_may_name_its_own_sni()
    {
        var sites = GatewayRoutesFile.Parse("""[{ "name": "hr", "upstream": "https://10.0.0.5", "sni": "hr.corp" }]""");
        Assert.Equal("hr.corp", sites.Single().Sni);
        Assert.True(sites.Single().HasSettings);
        Assert.Throws<InvalidDataException>(() =>
            GatewayRoutesFile.Parse("""[{ "name": "hr", "upstream": "https://10.0.0.5", "sni": "not a host" }]"""));
    }

    [Fact]
    public async Task The_gateway_sends_a_sites_sni_in_its_handshake()
    {
        const string sni = "certapi-sni.example";
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate(sni, ca, true, false, new[] { sni });
        using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("Client", ca, false, true);
        await using var upstream = await LoopbackMtlsServer.StartEchoAsync(serverCert, clientCert.Thumbprint!);

        var routes = new GatewayRoutes(new[]
        {
            new GatewayRoute("/", new Uri(upstream.BaseUrl)) { Settings = new SiteSettings { Sni = sni } }
        });
        using var gw = new MtlsGateway(routes, clientCert, ignoreServerCertificateErrors: true, TimeSpan.FromSeconds(30), new TransportOptions());
        var resp = await gw.ForwardAsync(new GatewayRequest("GET", "/x", Array.Empty<KeyValuePair<string, string>>(), null, null), default);
        using (resp.Lifetime) Assert.Equal(200, resp.StatusCode);
        Assert.Equal(sni, upstream.LastSniHost);
        Assert.Contains("sni " + sni, routes.All.Single().Settings!.Describe());
    }

    [Fact]
    public void Sni_through_a_proxy_is_refused()
    {
        var problem = ApiClient.ValidateTransport(
            new TransportOptions { Sni = "x.corp", Proxy = ProxyMode.Explicit, ProxyUrl = "http://proxy.corp:8080" }, "https://api.corp/");
        Assert.Contains("--sni cannot be used through a proxy", problem);
    }

    // ---- gateway certificate under --vhost ----

    [Fact]
    public void Under_vhost_the_gateway_certificate_names_the_suffix_and_its_wildcard()
    {
        var dir = Directory.CreateTempSubdirectory("certapi-gwcert-").FullName;
        try
        {
            using var plain = GatewayCertificate.LoadOrCreate(dir, null, out var r0);
            Assert.Null(r0);
            Assert.DoesNotContain("*.gw.localhost", GatewayCertificate.DnsNames(plain));

            using var vhost = GatewayCertificate.LoadOrCreate(dir, new[] { "gw.localhost", "*.gw.localhost" }, out var r1);
            Assert.Equal(plain.Thumbprint, r1);
            Assert.Contains("*.gw.localhost", GatewayCertificate.DnsNames(vhost));
            Assert.Contains("localhost", GatewayCertificate.DnsNames(vhost));

            using var again = GatewayCertificate.LoadOrCreate(dir, new[] { "gw.localhost", "*.gw.localhost" }, out var r2);
            Assert.Null(r2);
            Assert.Equal(vhost.Thumbprint, again.Thumbprint);

            // Another suffix adds to what is named rather than replacing it.
            using var both = GatewayCertificate.LoadOrCreate(dir, new[] { "lab.test", "*.lab.test" }, out _);
            Assert.Contains("*.gw.localhost", GatewayCertificate.DnsNames(both));
            Assert.Contains("*.lab.test", GatewayCertificate.DnsNames(both));
        }
        finally { Directory.Delete(dir, true); }
    }
}
