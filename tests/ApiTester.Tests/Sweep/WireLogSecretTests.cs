using System.IO;
using System.Text;
using ApiTester.Core;

namespace ApiTester.Tests.Sweep;

public class WireLogSecretTests
{
    [Fact]
    public void A_secret_header_in_a_chunk_shown_as_hex_is_masked()
    {
        var log = new WireLog();
        var head = Encoding.ASCII.GetBytes("POST /up HTTP/1.1\r\nHost: h\r\nAuthorization: Bearer SECRETTOKEN123\r\nContent-Length: 400\r\n\r\n");
        var body = Enumerable.Range(0, 400).Select(i => (byte)(i % 7 == 0 ? 0 : i)).ToArray();
        log.Record(WireDirection.Sent, head.Concat(body).ToArray());
        string text = log.Render();
        Assert.DoesNotContain("SECRETTOKEN", text);
        Assert.DoesNotContain("SECR", text.Replace("\n", "").Replace("|   ", ""));
        Assert.Contains("SECRETTOKEN123", log.Render(includeSecrets: true).Replace("|\n", "").Replace("\n", ""), StringComparison.Ordinal);
    }

    [Fact]
    public void A_header_split_across_two_reads_is_masked_in_both()
    {
        var log = new WireLog();
        log.Record(WireDirection.Received, Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nSet-Cookie: sess=AAAA"));
        log.Record(WireDirection.Received, Encoding.ASCII.GetBytes("SECRETSESSIONVALUE; Path=/\r\nContent-Length: 0\r\n\r\n"));
        string text = log.Render();
        Assert.DoesNotContain("AAAA", text);
        Assert.DoesNotContain("SECRETSESSIONVALUE", text);
        Assert.Contains("Content-Length: 0", text);
    }

    [Fact]
    public void A_custom_credential_header_is_masked_too()
    {
        var log = new WireLog();
        log.Record(WireDirection.Sent, Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nX-Api-Key: SK_LIVE_1\r\n\r\n"));
        Assert.DoesNotContain("SK_LIVE_1", log.Render());
    }
}
