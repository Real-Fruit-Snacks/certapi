using System.IO;
using System.Net.Http;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ApiTester.Core;

namespace ApiTester.Tests;

/// <summary>The library halves of the 1.98 additions: client assertions, the dialer's new routes
/// (connect-to, Unix sockets, named pipes), public-key pins, the crawler's parsers, OpenAPI
/// operation lookup, and the SignalR and MQTT codecs.</summary>
public class ProtocolAdditionsTests
{
    // ---- RFC 7523 client assertions ----

    private static X509Certificate2 RsaCert()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=assertion", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    }

    private static X509Certificate2 EcCert(ECCurve curve)
    {
        using var ec = ECDsa.Create(curve);
        var req = new CertificateRequest("CN=assertion-ec", ec, HashAlgorithmName.SHA256);
        return req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    }

    private static byte[] B64(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight((s.Length + 3) / 4 * 4, '='));
    }

    private static string B64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    [Fact]
    public void An_RSA_client_assertion_is_an_RS256_JWT_signed_by_the_certificate_with_its_thumbprints()
    {
        using var cert = RsaCert();
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        string jwt = ClientAssertion.Create(cert, "client-1", "https://login.example/token", now);
        var parts = jwt.Split('.');
        Assert.Equal(3, parts.Length);

        using var header = JsonDocument.Parse(B64(parts[0]));
        Assert.Equal("RS256", header.RootElement.GetProperty("alg").GetString());
        Assert.Equal(B64Url(cert.GetCertHash(HashAlgorithmName.SHA1)), header.RootElement.GetProperty("x5t").GetString());
        Assert.Equal(B64Url(cert.GetCertHash(HashAlgorithmName.SHA256)), header.RootElement.GetProperty("x5t#S256").GetString());

        using var payload = JsonDocument.Parse(B64(parts[1]));
        var p = payload.RootElement;
        Assert.Equal("client-1", p.GetProperty("iss").GetString());
        Assert.Equal("client-1", p.GetProperty("sub").GetString());
        Assert.Equal("https://login.example/token", p.GetProperty("aud").GetString());
        Assert.Equal(1_800_000_000, p.GetProperty("iat").GetInt64());
        Assert.Equal(1_800_000_300, p.GetProperty("exp").GetInt64());
        Assert.False(string.IsNullOrEmpty(p.GetProperty("jti").GetString()));

        using var pub = cert.GetRSAPublicKey()!;
        Assert.True(pub.VerifyData(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), B64(parts[2]),
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    }

    [Theory]
    [InlineData("nistP256", "ES256")]
    [InlineData("nistP384", "ES384")]
    [InlineData("nistP521", "ES512")]
    public void An_EC_client_assertion_uses_the_matching_ES_algorithm(string curve, string alg)
    {
        using var cert = EcCert(ECCurve.CreateFromFriendlyName(curve));
        string jwt = ClientAssertion.Create(cert, "c", "aud", DateTimeOffset.UtcNow);
        var parts = jwt.Split('.');
        using var header = JsonDocument.Parse(B64(parts[0]));
        Assert.Equal(alg, header.RootElement.GetProperty("alg").GetString());
        var hash = alg switch { "ES256" => HashAlgorithmName.SHA256, "ES384" => HashAlgorithmName.SHA384, _ => HashAlgorithmName.SHA512 };
        using var pub = cert.GetECDsaPublicKey()!;
        Assert.True(pub.VerifyData(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), B64(parts[2]), hash,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
    }

    [Fact]
    public void A_token_binding_is_read_from_cnf_and_an_opaque_token_has_none()
    {
        using var cert = RsaCert();
        using var other = RsaCert();
        string Token(string thumb) => "e30." + B64Url(Encoding.UTF8.GetBytes($"{{\"cnf\":{{\"x5t#S256\":\"{thumb}\"}}}}")) + ".sig";
        string mine = B64Url(cert.GetCertHash(HashAlgorithmName.SHA256));
        Assert.True(ClientAssertion.BoundTo(Token(mine), cert));
        Assert.False(ClientAssertion.BoundTo(Token(mine), other));
        Assert.Null(ClientAssertion.BoundTo("opaque-token", cert));
        Assert.Null(ClientAssertion.BoundTo("e30." + B64Url(Encoding.UTF8.GetBytes("{\"sub\":\"x\"}")) + ".sig", cert));
    }

    // ---- --connect-to ----

    [Theory]
    [InlineData("api.example.com:443:10.0.0.5:8443", "api.example.com", 443, "10.0.0.5", 8443)]
    [InlineData("::backend:", "", null, "backend", null)]
    [InlineData("[::1]:80:[fe80::2]:81", "::1", 80, "fe80::2", 81)]
    public void Connect_to_is_parsed_the_way_curl_writes_it(string spec, string fromHost, int? fromPort, string toHost, int? toPort)
    {
        var c = ConnectToOverride.Parse(spec);
        Assert.NotNull(c);
        Assert.Equal(new ConnectToOverride(fromHost, fromPort, toHost, toPort), c);
    }

    [Theory]
    [InlineData("a:b")]
    [InlineData("a:x:b:1")]
    [InlineData("a:1:b:70000")]
    public void A_malformed_connect_to_is_refused(string spec) => Assert.Null(ConnectToOverride.Parse(spec));

    [Fact]
    public void An_empty_connect_to_host_or_port_matches_any()
    {
        var any = new ConnectToOverride("", null, "x", null);
        Assert.True(any.Matches("whatever", 1));
        var exact = new ConnectToOverride("API.example.com", 443, "x", null);
        Assert.True(exact.Matches("api.example.com", 443));
        Assert.False(exact.Matches("api.example.com", 80));
    }

    [Fact]
    public async Task Connect_to_sends_a_request_for_one_host_to_another_address_keeping_the_Host_header()
    {
        await using var server = new RawHttpServer(_ => RawHttpServer.Response(200, "OK", "routed"));
        var transport = new TransportOptions
        {
            Proxy = ProxyMode.None,
            ConnectTo = new[] { new ConnectToOverride("api.invalid", 80, "127.0.0.1", server.Port) }
        };
        using var client = new ApiClient();
        var response = await client.SendAsync(new ApiRequest { Method = HttpMethod.Get, Url = "http://api.invalid/x" }, null, transport);
        Assert.Null(response.Error);
        Assert.Equal("routed", Encoding.UTF8.GetString(response.Body));
        Assert.True(server.Requests.TryDequeue(out var seen));
        Assert.Equal("api.invalid", seen!.Header("Host"));
    }

    [Fact]
    public async Task A_unix_domain_socket_carries_the_request()
    {
        string path = Path.Combine(Path.GetTempPath(), "certapi-" + Guid.NewGuid().ToString("N")[..8] + ".sock");
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(path));
        listener.Listen(1);
        var serve = Task.Run(async () =>
        {
            using var s = await listener.AcceptAsync();
            await using var stream = new NetworkStream(s, ownsSocket: false);
            await AnswerOnceAsync(stream, "over-uds");
        });
        try
        {
            var transport = new TransportOptions { Proxy = ProxyMode.None, UnixSocket = path };
            using var client = new ApiClient();
            var response = await client.SendAsync(new ApiRequest { Method = HttpMethod.Get, Url = "http://localhost/v1.43/info" }, null, transport);
            Assert.Null(response.Error);
            Assert.Equal("over-uds", Encoding.UTF8.GetString(response.Body));
            await serve;
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task A_named_pipe_carries_the_request()
    {
        string name = "certapi-test-" + Guid.NewGuid().ToString("N")[..8];
        await using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var serve = Task.Run(async () =>
        {
            await pipe.WaitForConnectionAsync();
            await AnswerOnceAsync(pipe, "over-pipe");
        });
        var transport = new TransportOptions { Proxy = ProxyMode.None, NamedPipe = name };
        using var client = new ApiClient();
        var response = await client.SendAsync(new ApiRequest { Method = HttpMethod.Get, Url = "http://localhost/_ping" }, null, transport);
        Assert.Null(response.Error);
        Assert.Equal("over-pipe", Encoding.UTF8.GetString(response.Body));
        await serve;
    }

    private static async Task AnswerOnceAsync(Stream stream, string body)
    {
        var seen = new List<byte>();
        var one = new byte[1];
        while (!(seen.Count >= 4 && seen[^4] == '\r' && seen[^3] == '\n' && seen[^2] == '\r' && seen[^1] == '\n'))
        {
            if (await stream.ReadAsync(one) == 0) return;
            seen.Add(one[0]);
        }
        var response = Encoding.ASCII.GetBytes(RawHttpServer.Response(200, "OK", body));
        await stream.WriteAsync(response);
        await stream.FlushAsync();
    }

    [Fact]
    public void Unix_socket_and_named_pipe_together_are_refused()
    {
        var transport = new TransportOptions { UnixSocket = "/tmp/x.sock", NamedPipe = "docker_engine" };
        Assert.NotNull(ApiClient.ValidateTransport(transport, "http://localhost/"));
    }

    // ---- --pinnedpubkey ----

    [Fact]
    public void A_public_key_pin_allows_only_the_pinned_key()
    {
        using var cert = RsaCert();
        string pin = Convert.ToBase64String(SHA256.HashData(cert.PublicKey.ExportSubjectPublicKeyInfo()));
        using var other = RsaCert();
        string otherPin = Convert.ToBase64String(SHA256.HashData(other.PublicKey.ExportSubjectPublicKeyInfo()));
        Assert.True(ApiClient.PinAllows(new TransportOptions(), cert));
        Assert.True(ApiClient.PinAllows(new TransportOptions { PinnedPublicKeys = new[] { pin } }, cert));
        Assert.True(ApiClient.PinAllows(new TransportOptions { PinnedPublicKeys = new[] { otherPin, pin } }, cert));
        Assert.False(ApiClient.PinAllows(new TransportOptions { PinnedPublicKeys = new[] { otherPin } }, cert));
    }

    // ---- crawler parsers ----

    [Fact]
    public void Links_are_extracted_from_attributes_srcset_and_css_resolved_against_the_page_and_base()
    {
        var page = new Uri("https://site.example/docs/index.html");
        string html = """
            <a href="guide.html">g</a> <img src='/img/a.png' srcset="/img/a-2x.png 2x, b.png 3x">
            <!-- <a href="hidden.html"> --> <link href=style.css> <a href="mailto:x@y"> <a href="#top">
            <a href="https://other.example/x">
            """;
        var links = LinkExtractor.Extract(html, page, css: false).Select(u => u.ToString()).ToList();
        Assert.Contains("https://site.example/docs/guide.html", links);
        Assert.Contains("https://site.example/img/a.png", links);
        Assert.Contains("https://site.example/img/a-2x.png", links);
        Assert.Contains("https://site.example/docs/b.png", links);
        Assert.Contains("https://site.example/docs/style.css", links);
        Assert.Contains("https://other.example/x", links);
        Assert.DoesNotContain(links, l => l.Contains("hidden"));
        Assert.DoesNotContain(links, l => l.StartsWith("mailto", StringComparison.Ordinal));

        var withBase = LinkExtractor.Extract("<base href=\"https://cdn.example/v2/\"><a href=\"x.js\">", page, css: false);
        Assert.Contains(new Uri("https://cdn.example/v2/x.js"), withBase);

        var css = LinkExtractor.Extract("body{background:url('../bg.png')} @font-face{src:url(f.woff2)}", new Uri("https://site.example/css/a.css"), css: true);
        Assert.Contains(new Uri("https://site.example/bg.png"), css);
        Assert.Contains(new Uri("https://site.example/css/f.woff2"), css);
    }

    [Fact]
    public void Robots_rules_for_every_agent_are_honoured_with_the_longest_match_winning()
    {
        var robots = RobotsRules.Parse("""
            User-agent: googlebot
            Disallow: /

            User-agent: *
            Disallow: /private/
            Allow: /private/open/
            """);
        Assert.True(robots.Allows(new Uri("https://s.example/public")));
        Assert.False(robots.Allows(new Uri("https://s.example/private/secret")));
        Assert.True(robots.Allows(new Uri("https://s.example/private/open/page")));
    }

    [Fact]
    public void Mirror_paths_stay_under_the_root_and_give_a_directory_an_index()
    {
        string root = Path.Combine(Path.GetTempPath(), "mirror");
        string index = MirrorPath.For(root, new Uri("https://s.example/docs/"), "text/html");
        Assert.Equal(Path.Combine(root, "s.example", "docs", "index.html"), index);
        string escaped = MirrorPath.For(root, new Uri("https://s.example/%2e%2e/%2e%2e/etc/passwd"), "text/plain");
        Assert.StartsWith(Path.GetFullPath(root), Path.GetFullPath(escaped));
        string port = MirrorPath.For(root, new Uri("http://s.example:8080/a.js"), "application/javascript");
        Assert.Equal(Path.Combine(root, "s.example_8080", "a.js"), port);
    }

    // ---- OpenAPI operations ----

    private const string Spec = """
        {"openapi":"3.0.0","servers":[{"url":"https://api.example/v1"}],
         "paths":{"/pets/{petId}":{"parameters":[{"name":"petId","in":"path","required":true}],
           "get":{"operationId":"getPet","parameters":[{"name":"verbose","in":"query"},{"name":"X-Trace","in":"header"}],
             "responses":{"200":{"content":{"application/json":{"schema":{"$ref":"#/components/schemas/Pet"}}}},
                          "4XX":{"description":"bad"}}}}},
         "components":{"schemas":{"Pet":{"type":"object","required":["id"],"properties":{"id":{"type":"integer"}}}}}}
        """;

    [Fact]
    public void An_operation_is_found_by_id_or_method_and_path_with_its_parameters_merged()
    {
        var op = OpenApiImporter.FindOperation(Spec, "getPet");
        Assert.NotNull(op);
        Assert.Equal("GET", op!.Method);
        Assert.Equal("/pets/{petId}", op.PathTemplate);
        Assert.Equal("https://api.example/v1", op.BaseUrl);
        Assert.Contains(op.Parameters, p => p is { Name: "petId", In: "path", Required: true });
        Assert.Contains(op.Parameters, p => p is { Name: "verbose", In: "query" });
        Assert.Contains(op.Parameters, p => p is { Name: "X-Trace", In: "header" });
        var byPath = OpenApiImporter.FindOperation(Spec, "get /pets/{petId}");
        Assert.Equal("getPet", byPath?.OperationId);
        Assert.Null(OpenApiImporter.FindOperation(Spec, "nope"));
        Assert.Contains("getPet", string.Join("\n", OpenApiImporter.ListOperations(Spec)));
    }

    [Fact]
    public void A_status_finds_its_exact_schema_then_its_range()
    {
        var op = OpenApiImporter.FindOperation(Spec, "getPet")!;
        Assert.Equal("200", op.SchemaForStatus(200).Key);
        Assert.Contains("\"id\"", op.SchemaForStatus(200).Schema);
        Assert.Equal("4XX", op.SchemaForStatus(404).Key);
        Assert.Null(op.SchemaForStatus(500).Key);
    }

    // ---- SignalR ----

    [Fact]
    public void SignalR_negotiate_and_websocket_addresses_keep_the_hub_query()
    {
        var hub = new Uri("https://api.example/hubs/chat?tenant=7");
        Assert.Equal("https://api.example/hubs/chat/negotiate?tenant=7&negotiateVersion=1", SignalRProtocol.NegotiateUri(hub).ToString());
        Assert.Equal("wss://api.example/hubs/chat?tenant=7&id=a%2Bb", SignalRProtocol.WebSocketUri(hub, "a+b").ToString());
        Assert.Equal("ws://localhost:5000/hub", SignalRProtocol.WebSocketUri(new Uri("http://localhost:5000/hub"), null).ToString());
    }

    [Fact]
    public void SignalR_negotiate_answers_are_read_including_the_Azure_redirect()
    {
        var n = SignalRProtocol.ParseNegotiate("""
            {"connectionId":"c","connectionToken":"t","negotiateVersion":1,
             "availableTransports":[{"transport":"WebSockets","transferFormats":["Text","Binary"]},{"transport":"LongPolling"}]}
            """);
        Assert.Equal("t", n.ConnectionToken);
        Assert.Equal(new[] { "WebSockets", "LongPolling" }, n.Transports);
        var azure = SignalRProtocol.ParseNegotiate("""{"url":"https://x.service.signalr.net/client/?hub=chat","accessToken":"jwt"}""");
        Assert.Equal("https://x.service.signalr.net/client/?hub=chat", azure.RedirectUrl);
        Assert.Equal("jwt", azure.AccessToken);
    }

    [Fact]
    public void SignalR_calls_and_records_round_trip()
    {
        var (target, args) = SignalRProtocol.ParseCall("Send [\"ann\", 2]");
        Assert.Equal("Send", target);
        Assert.Equal("[\"ann\",2]", args.ToJsonString());
        Assert.Equal("[{\"a\":1}]", SignalRProtocol.ParseCall("Update {\"a\":1}").Arguments.ToJsonString());
        Assert.Equal("[]", SignalRProtocol.ParseCall("Ping").Arguments.ToJsonString());
        Assert.Throws<FormatException>(() => SignalRProtocol.ParseCall("Send [oops"));

        string record = SignalRProtocol.InvocationRecord("Send", args, "1");
        Assert.EndsWith("\u001e", record);
        var parsed = SignalRProtocol.Parse(record.TrimEnd('\u001e'));
        Assert.Equal(SignalRProtocol.Invocation, parsed.Type);
        Assert.Equal("1", parsed.InvocationId);
        Assert.Equal("Send \"ann\" 2", SignalRProtocol.Describe(parsed));

        var split = SignalRProtocol.Split("{}\u001e{\"type\":6}\u001e").ToList();
        Assert.Equal(new[] { "{}", "{\"type\":6}" }, split);
        Assert.Null(SignalRProtocol.HandshakeError("{}"));
        Assert.Equal("nope", SignalRProtocol.HandshakeError("{\"error\":\"nope\"}"));
    }

    // ---- MQTT ----

    [Fact]
    public void An_MQTT_311_connect_packet_matches_the_specification()
    {
        byte[] p = MqttCodec.EncodeConnect(4, "dev1", "user", "pw", 60, cleanStart: true);
        byte[] expected =
        {
            0x10, 26,
            0, 4, (byte)'M', (byte)'Q', (byte)'T', (byte)'T', 4, 0xC2, 0, 60,
            0, 4, (byte)'d', (byte)'e', (byte)'v', (byte)'1',
            0, 4, (byte)'u', (byte)'s', (byte)'e', (byte)'r',
            0, 2, (byte)'p', (byte)'w'
        };
        Assert.Equal(expected, p);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    public async Task An_MQTT_publish_decodes_to_what_was_encoded(int version)
    {
        byte[] payload = new byte[300];   // long enough for a two-byte remaining length
        new Random(1).NextBytes(payload);
        byte[] packet = MqttCodec.EncodePublish(version, "a/b", payload, qos: 1, retain: true, packetId: 513);
        var read = await MqttCodec.ReadAsync(new MemoryStream(packet), CancellationToken.None);
        Assert.NotNull(read);
        Assert.Equal(MqttCodec.Publish, read!.Type);
        var message = MqttCodec.DecodePublish(version, read, out var id);
        Assert.Equal((ushort)513, id);
        Assert.Equal("a/b", message.Topic);
        Assert.Equal(payload, message.Payload);
        Assert.Equal(1, message.Qos);
        Assert.True(message.Retain);
    }

    [Fact]
    public void A_SUBACK_reports_each_filters_result()
    {
        var v3 = new MqttPacket(MqttCodec.SubAck, 0, new byte[] { 0, 1, 1, 0x80 });
        Assert.Equal(new[] { 1, 0x80 }, MqttCodec.SubAckCodes(4, v3));
        var v5 = new MqttPacket(MqttCodec.SubAck, 0, new byte[] { 0, 1, 0, 0, 0x87 });
        Assert.Equal(new[] { 0, 0x87 }, MqttCodec.SubAckCodes(5, v5));
        Assert.Equal("bad user name or password", MqttCodec.ConnAckReason(4, 4));
        Assert.Equal("not authorized", MqttCodec.ConnAckReason(5, 0x87));
    }
}
