using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using ApiTester.Cli;
using ApiTester.Core;

namespace ApiTester.Tests;

public class HmacSigningTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 14, 1, 2, 3, TimeSpan.Zero);

    private static HmacSigner.SignInput Input(string method = "POST", string url = "https://api.test/orders?b=2&a=1",
        string body = "{\"x\":1}", params (string, string)[] headers) =>
        new(method, new Uri(url), headers.Select(h => new KeyValuePair<string, string>(h.Item1, h.Item2)).ToList(), Encoding.UTF8.GetBytes(body));

    private static string Header(IReadOnlyList<(string Name, string Value)> added, string name) =>
        added.Single(h => h.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;

    [Fact]
    public void Rfc9421_appendix_b25_hmac_sha256()
    {
        // RFC 9421 B.2.5, against the B.2 test request and the B.1.5 shared secret.
        var input = new HmacSigner.SignInput("POST", new Uri("https://example.com/foo?param=Value&Pet=dog"),
            new List<KeyValuePair<string, string>>
            {
                new("Date", "Tue, 20 Apr 2021 02:07:55 GMT"),
                new("Content-Type", "application/json")
            }, Encoding.UTF8.GetBytes("{\"hello\": \"world\"}"));
        byte[] key = Convert.FromBase64String("uzvJfB4u3N0Jy4T7NZ75MDVcr8zSTInedJtkgcu46YW4XByzNJjxBdtjUkdJPBtbmHhIDi6pcl8jsasjlTMtDQ==");
        string signature = HmacSigner.Rfc9421Signature(input, new[] { "date", "@authority", "content-type" },
            "(\"date\" \"@authority\" \"content-type\");created=1618884473;keyid=\"test-shared-secret\"", key);
        Assert.Equal("pxcQw6G3AjtMBQjwo8XzkZf/bws5LelbaMk5rGIGtE8=", signature);
    }

    [Fact]
    public void Rfc9421_writes_signature_input_signature_and_a_content_digest()
    {
        var options = new HmacOptions(new HmacSettings { Scheme = HmacScheme.Rfc9421 }, "secret", "my-key");
        var added = HmacSigner.Sign(Input(headers: ("Content-Type", "application/json")), options, Now, "n");

        string body = "{\"x\":1}";
        Assert.Equal("sha-256=:" + Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(body))) + ":", Header(added, "Content-Digest"));
        string input = Header(added, "Signature-Input");
        Assert.Equal("sig1=(\"@method\" \"@authority\" \"@path\" \"@query\" \"content-type\" \"content-digest\");created=" +
                     Now.ToUnixTimeSeconds() + ";keyid=\"my-key\";alg=\"hmac-sha256\"", input);

        // The signature is the HMAC of the base the RFC defines, recomputed by hand here.
        string expectedBase = string.Join("\n",
            "\"@method\": POST", "\"@authority\": api.test", "\"@path\": /orders", "\"@query\": ?b=2&a=1",
            "\"content-type\": application/json", "\"content-digest\": " + Header(added, "Content-Digest"),
            "\"@signature-params\": " + input["sig1=".Length..]);
        string expected = Convert.ToBase64String(HMACSHA256.HashData("secret"u8.ToArray(), Encoding.UTF8.GetBytes(expectedBase)));
        Assert.Equal($"sig1=:{expected}:", Header(added, "Signature"));
    }

    [Fact]
    public void Rfc9421_refuses_a_covered_header_the_request_lacks()
    {
        var options = new HmacOptions(new HmacSettings { Scheme = HmacScheme.Rfc9421, Components = "@method x-missing" }, "s", "k");
        var ex = Assert.Throws<HmacSigningException>(() => HmacSigner.Sign(Input(), options, Now, "n"));
        Assert.Contains("x-missing", ex.Message);
    }

    [Fact]
    public void Cavage_signs_request_target_host_date_and_digest()
    {
        var options = new HmacOptions(new HmacSettings { Scheme = HmacScheme.Cavage }, "secret", "k1");
        var added = HmacSigner.Sign(Input(), options, Now, "n");

        string date = Header(added, "Date");
        Assert.Equal("Sat, 14 Nov 2026 01:02:03 GMT", date);
        string digest = Header(added, "Digest");
        string signingString = $"(request-target): post /orders?b=2&a=1\nhost: api.test\ndate: {date}\ndigest: {digest}";
        string mac = Convert.ToBase64String(HMACSHA256.HashData("secret"u8.ToArray(), Encoding.UTF8.GetBytes(signingString)));
        Assert.Equal($"keyId=\"k1\",algorithm=\"hmac-sha256\",headers=\"(request-target) host date digest\",signature=\"{mac}\"",
            Header(added, "Signature"));
    }

    [Fact]
    public void A_template_signs_what_it_describes_and_extra_headers_share_the_clock()
    {
        var settings = new HmacSettings
        {
            StringToSign = "{method}\\n{path}\\n{sortedQuery}\\n{header:X-Timestamp}\\n{bodySha256}",
            Header = "Authorization: HMAC {keyId}:{signature}",
            Encoding = "base64",
            AlsoSend = { "X-Timestamp: {timestamp}", "X-Nonce: {nonce}" }
        };
        var added = HmacSigner.Sign(Input(), new HmacOptions(settings, "secret", "client-7"), Now, "abc");

        string ts = Now.ToUnixTimeSeconds().ToString();
        Assert.Equal(ts, Header(added, "X-Timestamp"));
        Assert.Equal("abc", Header(added, "X-Nonce"));
        string toSign = $"POST\n/orders\na=1&b=2\n{ts}\n" + Convert.ToHexStringLower(SHA256.HashData("{\"x\":1}"u8.ToArray()));
        string mac = Convert.ToBase64String(HMACSHA256.HashData("secret"u8.ToArray(), Encoding.UTF8.GetBytes(toSign)));
        Assert.Equal($"HMAC client-7:{mac}", Header(added, "Authorization"));
    }

    [Theory]
    [InlineData("sha512", "hex")]
    [InlineData("sha1", "base64url")]
    public void Algorithms_and_encodings(string alg, string encoding)
    {
        var settings = new HmacSettings { StringToSign = "{method} {url}", Algorithm = alg, Encoding = encoding };
        string sig = Header(HmacSigner.Sign(Input("GET", "https://api.test/x", ""), new HmacOptions(settings, "k"), Now, "n"), "X-Signature");
        byte[] mac = alg == "sha512" ? HMACSHA512.HashData("k"u8.ToArray(), "GET https://api.test/x"u8.ToArray())
                                     : HMACSHA1.HashData("k"u8.ToArray(), "GET https://api.test/x"u8.ToArray());
        string expected = encoding == "hex" ? Convert.ToHexStringLower(mac) : Convert.ToBase64String(mac).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Assert.Equal(expected, sig);
    }

    [Fact]
    public void A_base64_secret_is_decoded_first()
    {
        var settings = new HmacSettings { StringToSign = "x", SecretEncoding = "base64" };
        string sig = Header(HmacSigner.Sign(Input(), new HmacOptions(settings, Convert.ToBase64String("raw-key"u8.ToArray())), Now, "n"), "X-Signature");
        Assert.Equal(Convert.ToHexStringLower(HMACSHA256.HashData("raw-key"u8.ToArray(), "x"u8.ToArray())), sig);
    }

    [Theory]
    [InlineData("{method} {nope}", "{nope}")]
    [InlineData("{header}", "{header}")]
    [InlineData("{signature}", "{signature}")]
    public void Unknown_placeholders_are_refused_up_front(string template, string named)
    {
        var problem = new HmacOptions(new HmacSettings { StringToSign = template }, "k").Problem();
        Assert.NotNull(problem);
        Assert.Contains(named, problem);
    }

    [Fact]
    public void Settings_round_trip_through_the_saved_scope()
    {
        var settings = new HmacSettings
        {
            Scheme = HmacScheme.Template, StringToSign = "{method}\n{path}", Header = "X-Sig: {signature}",
            Encoding = "base64", AlsoSend = { "X-Ts: {timestamp}" }, Algorithm = "sha512"
        };
        var back = HmacSettings.FromScope(settings.ToScope());
        Assert.Equal(settings.StringToSign, back.StringToSign);
        Assert.Equal(settings.AlsoSend, back.AlsoSend);
        Assert.Equal((HmacScheme.Template, "sha512", "base64", "X-Sig: {signature}"), (back.Scheme, back.Algorithm, back.Encoding, back.Header));
        Assert.Contains("\"Template\"", settings.ToScope());
    }

    [Fact]
    public async Task The_client_signs_each_send_and_never_a_hop_elsewhere()
    {
        await using var other = new RawHttpServer(_ => "HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        await using var server = new RawHttpServer(_ =>
            $"HTTP/1.1 302 Found\r\nLocation: {other.BaseUrl}/landed\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        var response = await new ApiClient().SendAsync(new ApiRequest
        {
            Method = HttpMethod.Get,
            Url = server.BaseUrl + "/start",
            Hmac = new HmacOptions(new HmacSettings { StringToSign = "{method} {pathAndQuery}" }, "k")
        }, null, new TransportOptions { FollowRedirects = true });
        Assert.Equal(200, response.StatusCode);
        Assert.Equal(Convert.ToHexStringLower(HMACSHA256.HashData("k"u8.ToArray(), "GET /start"u8.ToArray())),
            Assert.Single(server.Requests).Header("X-Signature"));
        Assert.Null(Assert.Single(other.Requests).Header("X-Signature"));
    }

    [Fact]
    public async Task Send_takes_the_template_flags()
    {
        await using var server = new RawHttpServer(_ => "HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        var err = new StringWriter();
        int code = CliApp.Run(["send", server.BaseUrl + "/x", "--hmac", "k", "--hmac-sign", "{method}\\n{path}\\n{header:X-Ts}",
                "--hmac-set", "X-Ts: 1700000000", "--hmac-header", "X-Auth: v1={signature}", "--no-auto-token"],
            new StringReader(""), new StringWriter(), err, new MemoryStream(),
            new CliServices { LiveStatePath = Path.Combine(Path.GetTempPath(), $"certapi-none-{Guid.NewGuid():N}.json") });
        Assert.True(code == 0, err.ToString());
        var seen = Assert.Single(server.Requests);
        Assert.Equal("1700000000", seen.Header("X-Ts"));
        Assert.Equal("v1=" + Convert.ToHexStringLower(HMACSHA256.HashData("k"u8.ToArray(), "GET\n/x\n1700000000"u8.ToArray())), seen.Header("X-Auth"));
    }

    [Theory]
    [InlineData("send", "https://a.test/", "--hmac-sign", "{method}")]                          // no secret
    [InlineData("send", "https://a.test/", "--hmac", "k")]                                     // no template, no scheme
    [InlineData("send", "https://a.test/", "--hmac", "k", "--http-signature", "rfc9421")]       // no key id
    [InlineData("send", "https://a.test/", "--hmac", "k", "--hmac-sign", "{bogus}")]
    [InlineData("send", "https://a.test/", "--hmac", "k", "--http-signature", "cavage", "--hmac-key-id", "i", "--hmac-sign", "x")]
    public void Send_refuses_incomplete_or_contradictory_hmac_options(params string[] args)
    {
        int code = CliApp.Run(args, new StringReader(""), new StringWriter(), new StringWriter(), new MemoryStream(), new CliServices());
        Assert.Equal(2, code);
    }
}
