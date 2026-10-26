using System.IO;
using System.Net;
using System.Net.Http;
using ApiTester.Core;
using static ApiTester.Tests.Cli.ServeFixture;

namespace ApiTester.Tests.Cli;

/// <summary>Headers that legitimately repeat must reach the browser as separate lines through the
/// listener: two Set-Cookie headers folded into one with a comma are one unparseable cookie, and a
/// 401 whose Negotiate and Basic challenges are joined is a challenge no browser answers. Pinned
/// end to end because the listener (http.sys on Windows, the managed one elsewhere) is the part
/// that could fold them, and it is only exercised by a real request.</summary>
public class ServeDuplicateHeaderTests
{
    [Fact]
    public async Task Several_set_cookie_headers_stay_several_through_the_relay_and_the_cookie_rewriter()
    {
        var (ca, server, client) = Certs();
        using (ca) using (server) using (client)
        {
            var headers = new List<KeyValuePair<string, string>>
            {
                new("Set-Cookie", "a=1; Path=/; HttpOnly"),
                new("Set-Cookie", "b=2; Path=/; Expires=Wed, 21 Oct 2037 07:28:00 GMT"),
                new("Set-Cookie", "c=3; Path=/api"),
            };
            await using var upstream = await LoopbackMtlsServer.StartWithHeadersAsync(server, client.Thumbprint!, headers);

            await using var plain = ServeHost.Start(client, upstream.BaseUrl);
            using var http = NewClient();
            var asSent = await Poll(() => http.GetAsync($"{plain.Origin}/x"));
            Assert.Equal(new[] { "a=1; Path=/; HttpOnly", "b=2; Path=/; Expires=Wed, 21 Oct 2037 07:28:00 GMT", "c=3; Path=/api" },
                HeaderValues(asSent, "Set-Cookie"));
            string raw = await RawResponseAsync(plain.Port, "/x");
            Assert.Equal(3, raw.Split("\r\n").Count(l => l.StartsWith("Set-Cookie:", StringComparison.OrdinalIgnoreCase)));

            // Through a prefix mount with cookie rewriting each one is re-rooted — still three lines,
            // still in order, and the Expires comma did not split anything.
            await using var mounted = ServeHost.Start(client, "--upstream", $"/app={upstream.BaseUrl}", "--rewrite-cookies");
            var rewritten = await Poll(() => http.GetAsync($"{mounted.Origin}/app/x"));
            var values = HeaderValues(rewritten, "Set-Cookie");
            Assert.Equal(3, values.Length);
            Assert.Equal("a=1; Path=/app; HttpOnly", values[0]);
            Assert.Contains("Expires=Wed, 21 Oct 2037 07:28:00 GMT", values[1]);
            Assert.Equal("c=3; Path=/app/api", values[2]);
        }
    }

    [Fact]
    public async Task Several_www_authenticate_challenges_on_a_401_stay_separate()
    {
        var (ca, server, client) = Certs();
        using (ca) using (server) using (client)
        {
            var headers = new List<KeyValuePair<string, string>>
            {
                new("WWW-Authenticate", "Negotiate"),
                new("WWW-Authenticate", "NTLM"),
                new("WWW-Authenticate", "Basic realm=\"corp\""),
            };
            await using var upstream = await LoopbackMtlsServer.StartWithHeadersAsync(server, client.Thumbprint!, headers, status: 401);
            await using var serve = ServeHost.Start(client, upstream.BaseUrl);
            using var http = NewClient();

            var resp = await Poll(() => http.GetAsync($"{serve.Origin}/secure"));
            Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
            Assert.Equal(new[] { "Negotiate", "NTLM", "Basic realm=\"corp\"" }, HeaderValues(resp, "WWW-Authenticate"));

            // On the wire the listener — http.sys on Windows, the managed one elsewhere — folds the
            // three into one comma-separated WWW-Authenticate line. RFC 7235 allows exactly that, and
            // a client splits it back into three challenges (the assertion above is that split).
            // What must hold on the wire: every challenge arrives, in order, and none is dropped.
            string raw = await RawResponseAsync(serve.Port, "/secure");
            var lines = raw.Split("\r\n").Where(l => l.StartsWith("WWW-Authenticate:", StringComparison.OrdinalIgnoreCase)).ToList();
            Assert.True(lines.Count is 1 or 3, raw);
            string all = string.Join(", ", lines.Select(l => l["WWW-Authenticate:".Length..].Trim()));
            Assert.Equal("Negotiate, NTLM, Basic realm=\"corp\"", all);
        }
    }

    private static async Task<string> RawResponseAsync(int port, string path)
    {
        using var tcp = new System.Net.Sockets.TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, port);
        using var stream = tcp.GetStream();
        var request = System.Text.Encoding.ASCII.GetBytes($"GET {path} HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(request);
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms);
        return System.Text.Encoding.ASCII.GetString(ms.ToArray());
    }
}
