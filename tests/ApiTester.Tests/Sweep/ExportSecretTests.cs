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

public class ParsingSweepTests
{
    [Fact]
    public void A_quoted_csv_field_may_span_lines_and_a_bom_is_not_a_header()
    {
        var rows = DataSet.ParseCsv("﻿id,note\r\n1,\"line one\nline two\"\r\n2,\"a,b\"\r\n");
        Assert.Equal(2, rows.Count);
        Assert.Equal("line one\nline two", rows[0]["note"]);
        Assert.Equal("1", rows[0]["id"]);
        Assert.Equal("a,b", rows[1]["note"]);
    }

    [Fact]
    public void Json_with_a_utf8_bom_is_still_json()
    {
        var body = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(System.Text.Encoding.UTF8.GetBytes("{\"id\":5,\"name\":\"José <b>\"}")).ToArray();
        var formatted = new ResponseFormatter().Format(new ApiResponse { StatusCode = 200, Body = body, ContentType = "application/json" });
        Assert.Equal(BodyKind.Json, formatted.Kind);
        Assert.Contains("José <b>", formatted.Text);
        var capture = ResponseCapture.Extract(new CaptureSpec("id", false, "id"), body, "application/json", Array.Empty<KeyValuePair<string, string>>());
        Assert.Equal("5", capture.Value);
    }

    [Fact]
    public void A_har_with_two_hosts_keeps_both_hosts_operations()
    {
        string Entry(string url) => $$$"""{"startedDateTime":"2026-01-01T00:00:00Z","time":1,"request":{"method":"GET","url":"{{{url}}}","httpVersion":"HTTP/1.1","headers":[],"queryString":[],"cookies":[],"headersSize":-1,"bodySize":0},"response":{"status":200,"statusText":"OK","httpVersion":"HTTP/1.1","headers":[],"cookies":[],"content":{"size":0,"mimeType":"application/json"},"redirectURL":"","headersSize":-1,"bodySize":0},"cache":{},"timings":{"send":0,"wait":1,"receive":0}}""";
        string har = "{\"log\":{\"version\":\"1.2\",\"creator\":{\"name\":\"t\",\"version\":\"1\"},\"entries\":[" +
                     string.Join(",", Entry("https://a.example/users/1"), Entry("https://b.example/users/2"),
                                      Entry("https://b.example/health"), Entry("https://a.example/health")) + "]}}";
        var collection = HarToCollection.Group(HarReader.Parse(har));
        var urls = Flatten(collection).Select(r => (r.BaseUrl ?? "") + r.Url).ToList();
        Assert.Contains(urls, u => u.Contains("a.example") && u.Contains("users"));
        Assert.Contains(urls, u => u.Contains("b.example") && u.Contains("users"));
        Assert.Contains(urls, u => u.Contains("a.example") && u.Contains("health"));
        Assert.Contains(urls, u => u.Contains("b.example") && u.Contains("health"));

        static IEnumerable<ParsedRequest> Flatten(ParsedCollection c) => c.Requests.Concat(c.Folders.SelectMany(Flatten));
    }

    [Fact]
    public void Postman_form_bodies_keep_their_variables_resolvable()
    {
        string json = """{"info":{"name":"c","schema":"https://schema.getpostman.com/json/collection/v2.1.0/collection.json"},"item":[{"name":"token","request":{"method":"POST","url":"https://idp/token","body":{"mode":"urlencoded","urlencoded":[{"key":"client_secret","value":"{{clientSecret}}"},{"key":"scope","value":"a b"}]}}}]}""";
        var result = PostmanImport.Parse(json);
        string body = Find(result.Root).Request!.Body!;
        Assert.Contains("client_secret={{clientSecret}}", body);
        Assert.Contains("scope=a%20b", body);

        static CollectionNode Find(CollectionNode n) => n.Request is not null ? n : n.Children.Select(Find).First(x => x is not null);
    }

    [Fact]
    public void An_openapi_server_prefix_matches_at_a_segment_boundary()
    {
        var c = new ParsedCollection { Name = "c", BaseUrl = "https://a/v1" };
        c.Requests.Add(new ParsedRequest { Method = "GET", Url = "https://a/v10/items" });
        string json = OpenApiExporter.ToJson(c);
        Assert.DoesNotContain("\"/0/items\"", json);
        Assert.Contains("/v10/items", json);
    }

    [Theory]
    [InlineData("12", "<", "1,5", false)]
    [InlineData("1.2", "<", "1.5", true)]
    public void Numbers_in_assertions_are_not_read_with_thousands_separators(string actual, string op, string expected, bool pass)
    {
        var rule = AssertionParser.Parse($"body price {op} {expected}");
        var result = AssertionEvaluator.Evaluate(new[] { rule },
            new ApiResponse { StatusCode = 200, Body = System.Text.Encoding.UTF8.GetBytes($"{{\"price\":{actual}}}"), ContentType = "application/json" });
        Assert.Equal(pass, result.Single().Passed);
    }
}

public class SweepReviewFollowUpTests
{
    [Fact]
    public void A_pin_on_an_international_host_matches_its_punycode_connection()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var leaf = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        var state = new AppState();
        TrustService.Trust(state, new Uri("https://bücher.example/").Host, leaf);
        var predicate = new TrustPredicates(state).For("bücher.example");
        Assert.True(TrustContext.Run(null, System.Net.Security.SslPolicyErrors.None, "xn--bcher-kva.example", () => predicate(leaf)));
    }

    [Fact]
    public void A_cookie_jar_written_before_host_only_was_recorded_keeps_its_domain_cookies()
    {
        var loaded = new System.Net.CookieContainer();
        CookieJarFile.Deserialize("""[{"Name":"par","Value":"3","Domain":"example.com","Path":"/","Secure":false,"HttpOnly":false}]""", loaded);
        Assert.Contains("par=3", loaded.GetCookieHeader(new Uri("https://api.example.com/")));
    }

    [Theory]
    [InlineData("shop.xn--p1ai", "https://shop.xn--p1ai")]
    [InlineData("xn--p1ai--8443", "https://xn--p1ai:8443")]
    [InlineData("hr.corp--notaport", null)]
    public void Punycode_labels(string label, string? expected) => Assert.Equal(expected, VirtualHosts.DecodeAuthority(label));
}

public class Round2InputTests
{
    [Fact]
    public void A_csv_quote_inside_a_field_is_a_character_not_a_new_quoted_field()
    {
        var rows = DataSet.ParseCsv("id,name\n1,5\" screen\n2,Bob\n3,\"Carol, Jr\"\n");
        Assert.Equal(3, rows.Count);
        Assert.Equal("5\" screen", rows[0]["name"]);
        Assert.Equal("Carol, Jr", rows[2]["name"]);
    }

    [Fact]
    public void Mock_scenario_values_written_with_the_wrong_json_type_are_read_or_named()
    {
        var scenario = MockScenario.Parse("""{"routes":[{"path":"/a","respond":{"status":"404","body":{"orders":[]},"headers":{"Retry-After":5}}}]}""");
        var answer = scenario.Routes[0].Next();
        Assert.Equal(404, answer.Status);
        Assert.Equal("{\"orders\":[]}", System.Text.Encoding.UTF8.GetString(answer.Body));
        Assert.Contains(answer.Headers, h => h.Key == "Retry-After" && h.Value == "5");
        Assert.Contains(scenario.Warnings, w => w.Contains("as text"));
    }

    [Fact]
    public void A_har_with_nulls_is_a_clear_format_error_or_reads_as_empty()
    {
        Assert.Throws<HarFormatException>(() => HarReader.Parse("""{"log":{"entries":[null]}}"""));
        Assert.Empty(HarReader.Parse("""{"log":{"entries":null}}""").Log.Entries);
        var har = HarReader.Parse("""{"log":{"entries":[{"request":{"method":"GET","url":"https://h/","headers":null},"response":null}]}}""");
        Assert.Empty(har.Log.Entries[0].Request.Headers);
        Assert.NotNull(HarReader.ToParsedRequest(har.Log.Entries[0]));
    }

    [Fact]
    public async Task A_url_with_an_unsupported_scheme_is_a_failed_request_not_an_exception()
    {
        var resp = await new ApiClient().SendAsync(new ApiRequest { Method = HttpMethod.Get, Url = "htps://example.com/" }, null);
        Assert.NotNull(resp.Error);
        Assert.Contains("only http", resp.Error!.Message);
    }

    [Fact]
    public void A_cookie_jar_keeps_more_than_twenty_cookies_for_one_domain()
    {
        var jar = CookieJarFile.NewContainer();
        for (int i = 0; i < 60; i++) jar.SetCookies(new Uri("https://sso.example/"), $"c{i}=v");
        var copy = CookieJarFile.NewContainer();
        Assert.Equal(60, CookieJarFile.Deserialize(CookieJarFile.Serialize(jar), copy));
        Assert.Equal(60, copy.GetAllCookies().Count);
    }

    [Fact]
    public void A_workspace_that_cannot_be_read_is_kept_before_anything_saves_over_it()
    {
        string path = Path.Combine(Path.GetTempPath(), $"state-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, "{\"TimeoutSeconds\":\"100\"}");
        try
        {
            string? kept = AppState.PreserveUnreadable(path);
            Assert.NotNull(kept);
            Assert.Equal(File.ReadAllText(path), File.ReadAllText(kept!));
            File.Delete(kept!);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Postman_path_segments_may_be_objects()
    {
        string json = """{"info":{"name":"c","schema":"https://schema.getpostman.com/json/collection/v2.0.0/collection.json"},"item":[{"name":"u","request":{"method":"GET","url":{"protocol":"https","host":["api","example"],"path":[{"type":"string","value":"users"},"1"]}}}]}""";
        var result = PostmanImport.Parse(json);
        string Find(CollectionNode n) => n.Request is not null ? n.Request.EffectiveUrl() : n.Children.Select(Find).First();
        Assert.Contains("api.example/users/1", Find(result.Root));
    }
}

public class SseRedirectTests
{
    [Fact]
    public async Task A_stream_follows_a_redirect_on_its_own_origin_and_refuses_one_to_another()
    {
        await using var other = new RawHttpServer(_ => RawHttpServer.Response(200, "OK", "data: stolen\n\n", "Content-Type: text/event-stream"));
        await using var home = new RawHttpServer(r => r.Path switch
        {
            "/start" => RawHttpServer.Response(302, "Found", "", "Location: /stream"),
            "/stream" => RawHttpServer.Response(200, "OK", "data: hello\n\n", "Content-Type: text/event-stream"),
            _ => RawHttpServer.Response(302, "Found", "", $"Location: {other.BaseUrl}/x"),
        });
        var events = new List<SseEvent>();
        await foreach (var e in SseClient.StreamAsync(home.BaseUrl + "/start", headers: new KeyValuePair<string, string>[] { new("X-Api-Key", "s3cret") })) events.Add(e);
        Assert.Equal("hello", Assert.Single(events).Data);

        await Assert.ThrowsAsync<HttpRequestException>(async () =>
        {
            await foreach (var _ in SseClient.StreamAsync(home.BaseUrl + "/away", headers: new KeyValuePair<string, string>[] { new("X-Api-Key", "s3cret") })) { }
        });
        Assert.Empty(other.Requests);
    }
}
