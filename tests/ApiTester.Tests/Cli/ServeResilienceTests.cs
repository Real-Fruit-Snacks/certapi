using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using ApiTester.Cli;
using ApiTester.Core;
using Microsoft.AspNetCore.Http;

namespace ApiTester.Tests.Cli;

/// <summary>`serve` with --retry, --http2 and a certificate renewed while it runs, end to end: the
/// flags the command parsed, the request lines it logged and what /_certapi/health says.</summary>
public class ServeResilienceTests
{
    /// <summary>A StringWriter several threads can write to (the gateway logs from a background thread).</summary>
    private sealed class SharedLog : StringWriter
    {
        public override void Write(char value) { lock (this) base.Write(value); }
        public override void Write(string? value) { lock (this) base.Write(value); }
        public override void WriteLine(string? value) { lock (this) base.WriteLine(value); }
        public override string ToString() { lock (this) return base.ToString(); }
    }

    private static CertificateInfo Info(X509Certificate2 c, string subject = "CN=GatewayClient") => new()
    {
        Subject = subject, Issuer = "CN=CA", Thumbprint = c.Thumbprint, NotBefore = c.NotBefore, NotAfter = c.NotAfter,
        HasClientAuthEku = true, Certificate = c
    };

    [Fact]
    public async Task Retries_http2_and_a_renewal_while_it_runs()
    {
        var (ca, server, client) = ServeFixture.Certs();
        using (ca) using (server) using (client)
        using (var renewed = LaterLeaf(ca, client.NotAfter.AddDays(30)))
        {
            // The renewal must last longer than the certificate it replaces to be taken.
            Assert.True(renewed.NotAfter > client.NotAfter);
            await using var upstream = await KestrelUpstream.StartAsync(server, (c, n) =>
            {
                c.Response.StatusCode = n <= 2 ? 503 : 200;
                return c.Response.WriteAsync(n <= 2 ? "busy" : "ok");
            });
            var store = new List<CertificateInfo> { Info(client) };
            var log = new SharedLog();
            using var cts = new CancellationTokenSource();
            int port = ServeFixture.FreePort();
            var services = new CliServices
            {
                Cancel = cts.Token, UserConfigPath = null,
                ListCertificates = _ => { lock (store) return store.ToList(); },
                ServeCertificateCheckInterval = TimeSpan.FromMilliseconds(200),
            };
            var run = Task.Run(() => CliApp.Run(
                ["serve", upstream.Url, "--port", port.ToString(), "--no-config", "--cert", "GatewayClient", "--insecure",
                 "--retry", "2", "--retry-delay", "10", "--http2", "--connection-lifetime", "300"],
                TextWriter.Null, log, services: services));
            try
            {
                using var http = ServeFixture.NewClient();
                var first = await ServeFixture.Poll(() => http.GetAsync($"http://127.0.0.1:{port}/orders"));
                Assert.Equal(HttpStatusCode.OK, first.StatusCode);
                Assert.Equal("ok", await first.Content.ReadAsStringAsync());
                Assert.Contains(upstream.ClientThumbprints, t => t == client.Thumbprint);

                // The renewal lands in the store; within a check or two the gateway presents it.
                lock (store) store.Add(Info(renewed));
                var giveUp = DateTime.UtcNow + ServeFixture.Limit;
                while (!log.ToString().Contains("certificate renewed") && DateTime.UtcNow < giveUp) await Task.Delay(50);
                // A new connection is opened for the renewed certificate (its pool is new).
                Assert.Equal(HttpStatusCode.OK, (await http.GetAsync($"http://127.0.0.1:{port}/orders")).StatusCode);
                Assert.Equal(renewed.Thumbprint, upstream.ClientThumbprints.Last());

                using var health = JsonDocument.Parse(await http.GetStringAsync($"http://127.0.0.1:{port}/_certapi/health"));
                var certs = health.RootElement.GetProperty("certificates");
                Assert.Equal(1, certs.GetArrayLength());
                Assert.Equal(renewed.Thumbprint, certs[0].GetProperty("thumbprint").GetString());
                Assert.True(certs[0].GetProperty("daysLeft").GetInt32() > 300);
            }
            finally
            {
                cts.Cancel();
                await run.WaitAsync(ServeFixture.Limit);
            }
            string text = log.ToString();
            Assert.Contains("retry 2", text);              // the startup table
            Assert.Contains("http2", text);
            Assert.Contains("(3 attempts)", text);         // the first request's line
            Assert.Contains("HTTP/2", text);
            Assert.Contains("certificate renewed: 'CN=GatewayClient' " + renewed.Thumbprint, text);
            Assert.All(upstream.Protocols, p => Assert.Equal("HTTP/2", p));
        }
    }

    /// <summary>CN=GatewayClient from <paramref name="ca"/>, valid from now until <paramref name="notAfter"/>.</summary>
    private static X509Certificate2 LaterLeaf(X509Certificate2 ca, DateTime notAfter)
    {
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var req = new CertificateRequest("CN=GatewayClient", rsa, System.Security.Cryptography.HashAlgorithmName.SHA256,
            System.Security.Cryptography.RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new System.Security.Cryptography.OidCollection { new("1.3.6.1.5.5.7.3.2") }, false));
        using var signed = req.Create(ca, DateTimeOffset.UtcNow, new DateTimeOffset(notAfter.ToUniversalTime(), TimeSpan.Zero) < ca.NotAfter
            ? new DateTimeOffset(notAfter.ToUniversalTime(), TimeSpan.Zero) : ca.NotAfter.ToUniversalTime(), [9, 8, 7, 6]);
        // Through a PFX, as the factory does: Windows' TLS stack cannot present an in-memory key.
        using var withKey = signed.CopyWithPrivateKey(rsa);
        return X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pfx), (string?)null);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("soon")]
    [InlineData("90000")]
    public void A_connection_lifetime_that_is_not_seconds_is_a_usage_error(string value)
    {
        var err = new StringWriter();
        int code = CliApp.Run(["serve", "https://api.example", "--port", "1", "--no-config", "--connection-lifetime", value, "--check"],
            TextWriter.Null, err, services: new CliServices { UserConfigPath = null });
        Assert.Equal(ExitCodes.Usage, code);
        Assert.Contains("--connection-lifetime", err.ToString());
    }

    [Fact]
    public void Check_shows_the_retry_and_http2_settings()
    {
        var output = new StringWriter();
        var err = new StringWriter();
        int code = CliApp.Run(["serve", "https://api.example", "--port", "18999", "--no-config", "--retry", "3", "--http2",
                               "--connection-lifetime", "0", "--check"],
            output, err, services: new CliServices { UserConfigPath = null });
        Assert.Equal(ExitCodes.Ok, code);
        string all = output.ToString() + err.ToString();
        Assert.Contains("retry 3", all);
        Assert.Contains("http2", all);
    }
}
