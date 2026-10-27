using System.Net;
using System.Net.Http;
using System.Text.Json;
using ApiTester.Core;
using static ApiTester.Tests.Cli.ServeFixture;

namespace ApiTester.Tests.Cli;

/// <summary>serve --rewrite-body and /_certapi under a virtual host, end to end.</summary>
public class ServeLeftoverTests
{
    private static MockServer PageServer(out int port)
    {
        port = FreePort();
        string self = $"http://127.0.0.1:{port}";
        var routes = new object[]
        {
            new { match = new { path = "/page" }, respond = new { status = 200,
                  headers = new Dictionary<string, string> { ["Content-Type"] = "text/html; charset=utf-8", ["Refresh"] = $"9; url={self}/later" },
                  body = $"<a href=\"{self}/orders\">orders</a> <a href=\"https://elsewhere.example/\">x</a>" } },
            new { match = new { path = "/blob" }, respond = new { status = 200,
                  headers = new Dictionary<string, string> { ["Content-Type"] = "application/octet-stream" },
                  body = $"{self}/orders" } },
        };
        return MockServer.Start(port, MockTlsMode.Http, scenario: MockScenario.Parse(JsonSerializer.Serialize(new { routes })));
    }

    [Fact]
    public async Task Rewrite_body_points_a_pages_own_links_back_through_the_gateway()
    {
        var (ca, server, client) = Certs();
        using (ca) using (server) using (client)
        {
            await using var upstream = PageServer(out int upPort);
            await using var serve = ServeHost.Start(client, "--upstream", $"app=http://127.0.0.1:{upPort}", "--rewrite-body", "--rewrite-location");
            using var http = NewClient();

            var page = await Poll(() => http.GetAsync($"{serve.Origin}/app/page"));
            string html = await page.Content.ReadAsStringAsync();
            Assert.Contains($"href=\"{serve.Origin}/app/orders\"", html);
            Assert.Contains("https://elsewhere.example/", html);
            Assert.Equal(System.Text.Encoding.UTF8.GetByteCount(html), page.Content.Headers.ContentLength);
            Assert.Equal($"9; url={serve.Origin}/app/later", HeaderValues(page, "Refresh").Single());

            // Not text: relayed as it came.
            var blob = await http.GetAsync($"{serve.Origin}/app/blob");
            Assert.Equal($"http://127.0.0.1:{upPort}/orders", await blob.Content.ReadAsStringAsync());
        }
    }

    [Fact]
    public async Task Without_the_flag_the_body_is_untouched()
    {
        var (ca, server, client) = Certs();
        using (ca) using (server) using (client)
        {
            await using var upstream = PageServer(out int upPort);
            await using var serve = ServeHost.Start(client, "--upstream", $"app=http://127.0.0.1:{upPort}");
            using var http = NewClient();
            var page = await Poll(() => http.GetAsync($"{serve.Origin}/app/page"));
            Assert.Contains($"http://127.0.0.1:{upPort}/orders", await page.Content.ReadAsStringAsync());
        }
    }

    [WildcardBindingFact]
    public async Task The_gateways_own_endpoints_answer_under_a_virtual_host()
    {
        var (ca, server, client) = Certs();
        using (ca) using (server) using (client)
        {
            await using var api = await LoopbackMtlsServer.StartEchoAsync(server, client.Thumbprint!);
            await using var serve = ServeHost.Start(client, "--upstream", $"api={api.BaseUrl}", "--vhost");
            using var http = NewClient();
            var health = await Poll(() => http.SendAsync(new HttpRequestMessage(HttpMethod.Get, $"{serve.Origin}/_certapi/health")
                { Headers = { Host = $"api.gw.localhost:{serve.Port}" } }));
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);
            using var doc = JsonDocument.Parse(await health.Content.ReadAsStringAsync());
            Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
            Assert.DoesNotContain("GET /_certapi", await health.Content.ReadAsStringAsync());   // never forwarded
        }
    }
}
