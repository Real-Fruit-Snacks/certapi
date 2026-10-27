using System.IO;
using System.Net.Http;
using ApiTester.Core;

namespace ApiTester.Tests.Sweep;

public class ExportSecretTests
{
    [Theory]
    [InlineData("{\"user\":\"alice\",\"password\":\"HUNTER2\",\"nested\":{\"client_secret\":\"CS\"}}", "application/json")]
    [InlineData("grant_type=password&username=alice&password=HUNTER2&client_secret=CS", "application/x-www-form-urlencoded")]
    public void Credential_fields_in_a_body_are_redacted(string body, string type)
    {
        string redacted = MarkdownSecrets.RedactBody(body, type);
        Assert.DoesNotContain("HUNTER2", redacted);
        Assert.DoesNotContain("\"CS\"", redacted);
        Assert.DoesNotContain("=CS", redacted);
        Assert.Contains("alice", redacted);
        Assert.Equal(body, MarkdownSecrets.RedactBody(body, type, includeSecrets: true));
    }

    [Fact]
    public void A_variable_reference_is_not_a_secret_and_a_plain_body_is_untouched()
    {
        Assert.Equal("{\"password\":\"{{pw}}\"}", MarkdownSecrets.RedactBody("{\"password\":\"{{pw}}\"}", "application/json"));
        Assert.Equal("hello password", MarkdownSecrets.RedactBody("hello password", "text/plain"));
    }

    [Fact]
    public void An_openapi_export_carries_no_credential_examples()
    {
        var collection = new ParsedCollection { Name = "c" };
        collection.Requests.Add(new ParsedRequest
        {
            Method = "POST",
            Url = "https://api.example/login?api_key=QUERYSECRET&page=2",
            Headers = { new("X-Api-Key", "HEADERSECRET"), new("Cookie", "sid=COOKIESECRET"), new("X-Trace", "keep") },
            Body = "{\"password\":\"BODYSECRET\"}",
            ContentType = "application/json",
        });
        string json = OpenApiExporter.ToJson(collection);
        foreach (var secret in new[] { "QUERYSECRET", "HEADERSECRET", "COOKIESECRET", "BODYSECRET" })
            Assert.DoesNotContain(secret, json);
        Assert.Contains("keep", json);
    }

    [Fact]
    public void A_doctor_note_for_an_unparseable_url_does_not_put_its_credentials_in_the_title()
    {
        var report = new DoctorReport("https://svc:hunter2@api.example.com:44x/orders?api_key=SECRET", Array.Empty<DoctorStage>());
        string note = DoctorMarkdown.Render(report, DateTimeOffset.UtcNow);
        Assert.DoesNotContain("hunter2", note);
        Assert.DoesNotContain("SECRET", note);
    }
}

public class PacFailureTests
{
    [Fact]
    public async Task A_pac_script_that_cannot_be_run_fails_the_send_rather_than_going_direct()
    {
        await using var server = new RawHttpServer(_ => RawHttpServer.Response(200, "OK", "direct"));
        string missing = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.pac");
        var resp = await new ApiClient().SendAsync(new ApiRequest { Method = HttpMethod.Get, Url = server.BaseUrl + "/" },
            null, new TransportOptions { PacUrl = missing });
        Assert.Equal(ApiErrorKind.ProxyFailure, resp.Error?.Kind);
        Assert.Empty(server.Requests);
    }
}

public class PrereleaseOrderTests
{
    [Theory]
    [InlineData("1.94.0-beta.10", "1.94.0-beta.9", 1)]
    [InlineData("1.94.0-beta.2", "1.94.0-beta.11", -1)]
    [InlineData("1.94.0-alpha", "1.94.0-alpha.1", -1)]
    [InlineData("1.94.0-1", "1.94.0-alpha", -1)]
    [InlineData("1.94.0", "1.94.0-rc.1", 1)]
    public void Prereleases_order_as_semver_says(string a, string b, int sign) =>
        Assert.Equal(sign, Math.Sign(ReleaseUpdater.CompareVersions(a, b)));
}

public class SseDispatchTests
{
    [Fact]
    public async Task Blocks_with_no_data_are_not_events()
    {
        await using var server = new RawHttpServer(_ => RawHttpServer.Response(200, "OK",
            "retry: 3000\n\nid: 7\n\ndata: hello\n\ndata:\n\n", "Content-Type: text/event-stream"));
        var events = new List<SseEvent>();
        await foreach (var e in SseClient.StreamAsync(server.BaseUrl + "/s")) events.Add(e);
        Assert.Equal(2, events.Count);
        Assert.Equal("hello", events[0].Data);
        Assert.Equal("7", events[0].Id);
        Assert.Equal("3000", events[0].Retry);
        Assert.Equal("", events[1].Data);   // "data:" with no value is still an event
    }
}

public class GatewaySweepTests
{
    [Theory]
    [InlineData("xn--bcher-kva.example", "https://xn--bcher-kva.example")]
    [InlineData("my--host.corp", "https://my--host.corp")]
    [InlineData("api.corp--8443", "https://api.corp:8443")]
    [InlineData("http--api.corp--8080", "http://api.corp:8080")]
    public void A_host_with_double_dashes_is_still_a_host(string label, string expected) =>
        Assert.Equal(expected, VirtualHosts.DecodeAuthority(label));

    [Fact]
    public void A_scenario_body_file_is_served_byte_for_byte()
    {
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0xFF, 0xFE, 0x00, 0xC3 };
        var scenario = MockScenario.Parse("""{"routes":[{"path":"/img","respond":{"bodyFile":"a.png"}}]}""",
            readBytes: _ => png);
        Assert.Empty(scenario.Warnings);
        Assert.Equal(png, scenario.Routes[0].Next().Body);
    }

    [Fact]
    public void Corrupt_brotli_is_left_alone_rather_than_failing()
    {
        Assert.Null(BodyRewriter.Decode(System.Text.Encoding.ASCII.GetBytes("<html>not brotli at all</html>"), "br"));
    }
}

public class MockSweepTests
{
    [Fact]
    public async Task A_message_sent_in_pieces_is_echoed_whole()
    {
        await using var srv = MockServer.Start(0, MockTlsMode.Http);
        using var ws = new System.Net.WebSockets.ClientWebSocket();
        await ws.ConnectAsync(new Uri("ws://127.0.0.1:" + srv.Port + "/ws"), default);
        await ws.SendAsync("part1-"u8.ToArray(), System.Net.WebSockets.WebSocketMessageType.Text, endOfMessage: false, default);
        await ws.SendAsync("part2"u8.ToArray(), System.Net.WebSockets.WebSocketMessageType.Text, endOfMessage: true, default);
        var buffer = new byte[1024];
        var all = new MemoryStream();
        System.Net.WebSockets.WebSocketReceiveResult r;
        do { r = await ws.ReceiveAsync(buffer, default); all.Write(buffer, 0, r.Count); } while (!r.EndOfMessage);
        Assert.Equal("part1-part2", System.Text.Encoding.UTF8.GetString(all.ToArray()));
    }

    [Fact]
    public async Task A_chunked_request_body_is_decoded_before_it_is_echoed()
    {
        await using var srv = MockServer.Start(0, MockTlsMode.Http);
        using var http = new HttpClient();
        var content = new StreamContent(new MemoryStream("{\"hello\":\"world\"}"u8.ToArray()));
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        using var req = new HttpRequestMessage(HttpMethod.Post, srv.BaseUrl + "echo") { Content = content };
        req.Headers.TransferEncodingChunked = true;
        using var resp = await http.SendAsync(req);
        string body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("hello", body);
        Assert.DoesNotContain("\\r\\n0\\r\\n", body);
        Assert.DoesNotContain("11\\r\\n", body);
    }
}
