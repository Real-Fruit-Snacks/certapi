using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using ApiTester.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.Logging;

namespace ApiTester.Tests;

/// <summary>An https upstream for gateway tests (Kestrel): it can speak HTTP/2, sees the client
/// certificate, and counts the connections and requests it was sent.</summary>
internal sealed class KestrelUpstream : IAsyncDisposable
{
    private readonly WebApplication _app;
    public string Url { get; }
    public ConcurrentDictionary<string, byte> Connections { get; } = new();
    public ConcurrentQueue<string> ClientThumbprints { get; } = new();
    public ConcurrentQueue<string> Protocols { get; } = new();
    public ConcurrentQueue<string> Bodies { get; } = new();
    private int _requests;
    public int Requests => Volatile.Read(ref _requests);

    private KestrelUpstream(WebApplication app, string url) { _app = app; Url = url; }

    /// <param name="answer">Writes the response for request number n (1-based).</param>
    public static async Task<KestrelUpstream> StartAsync(X509Certificate2 serverCert, Func<HttpContext, int, Task> answer,
        HttpProtocols protocols = HttpProtocols.Http1AndHttp2)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0, listen =>
        {
            listen.Protocols = protocols;
            listen.UseHttps(https =>
            {
                https.ServerCertificate = serverCert;
                https.ClientCertificateMode = ClientCertificateMode.AllowCertificate;
                https.AllowAnyClientCertificate();
            });
        }));
        var app = builder.Build();
        KestrelUpstream? self = null;
        app.Run(async context =>
        {
            int n = Interlocked.Increment(ref self!._requests);
            self.Connections.TryAdd(context.Connection.Id, 0);
            self.Protocols.Enqueue(context.Request.Protocol);
            if (context.Connection.ClientCertificate is { } cc) self.ClientThumbprints.Enqueue(cc.Thumbprint);
            using (var reader = new StreamReader(context.Request.Body)) self.Bodies.Enqueue(await reader.ReadToEndAsync());
            await answer(context, n);
        });
        await app.StartAsync();
        var port = new Uri(app.Urls.First()).Port;
        self = new KestrelUpstream(app, $"https://localhost:{port}");
        return self;
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}

public class GatewayResilienceTests
{
    private static (X509Certificate2 ca, X509Certificate2 server, X509Certificate2 client) Certs()
    {
        var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        var server = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        var client = SelfSignedCertificateFactory.CreateSignedCertificate("GatewayClient", ca, false, true);
        return (ca, server, client);
    }

    private static MtlsGateway Gateway(string upstream, X509Certificate2 client, TransportOptions? transport = null, int timeoutSeconds = 30) =>
        new(new GatewayRoutes(new[] { new GatewayRoute("/", new Uri(upstream)) }), client, ignoreServerCertificateErrors: true,
            TimeSpan.FromSeconds(timeoutSeconds), transport);

    private static GatewayRequest Get(string path = "/") =>
        new("GET", path, Array.Empty<KeyValuePair<string, string>>(), null, null);

    private static async Task<string> Read(GatewayResponse r)
    {
        using (r.Lifetime)
        using (var sr = new StreamReader(r.Body))
            return await sr.ReadToEndAsync();
    }

    private static Task Answer(HttpContext c, int status, string body = "", string? retryAfter = null)
    {
        c.Response.StatusCode = status;
        if (retryAfter is not null) c.Response.Headers["Retry-After"] = retryAfter;
        return c.Response.WriteAsync(body);
    }

    private static TransportOptions Retrying(int n, bool unsafeToo = false) =>
        new() { Retries = n, RetryDelay = TimeSpan.FromMilliseconds(10), RetryUnsafeMethods = unsafeToo };

    // ---- --retry ----

    [Fact]
    public async Task A_503_is_retried_until_the_upstream_answers_and_the_attempts_are_counted()
    {
        var (ca, server, client) = Certs();
        using (ca) using (server) using (client)
        {
            await using var upstream = await KestrelUpstream.StartAsync(server, (c, n) => n <= 2 ? Answer(c, 503) : Answer(c, 200, "ok"));
            using var gw = Gateway(upstream.Url, client, Retrying(2));
            var r = await gw.ForwardAsync(Get(), default);
            Assert.Equal(200, r.StatusCode);
            Assert.Equal(3, r.Attempts);
            Assert.Equal("ok", await Read(r));
            Assert.Equal(3, upstream.Requests);
        }
    }

    [Fact]
    public async Task Without_retry_the_first_answer_is_relayed()
    {
        var (ca, server, client) = Certs();
        using (ca) using (server) using (client)
        {
            await using var upstream = await KestrelUpstream.StartAsync(server, (c, n) => Answer(c, 503));
            using var gw = Gateway(upstream.Url, client);
            var r = await gw.ForwardAsync(Get(), default);
            Assert.Equal(503, r.StatusCode);
            Assert.Equal(1, r.Attempts);
            await Read(r);
            Assert.Equal(1, upstream.Requests);
        }
    }

    [Fact]
    public async Task When_retries_run_out_the_last_answer_is_relayed()
    {
        var (ca, server, client) = Certs();
        using (ca) using (server) using (client)
        {
            await using var upstream = await KestrelUpstream.StartAsync(server, (c, n) => Answer(c, 502, "busy " + n));
            using var gw = Gateway(upstream.Url, client, Retrying(2));
            var r = await gw.ForwardAsync(Get(), default);
            Assert.Equal(502, r.StatusCode);
            Assert.Equal(3, r.Attempts);
            Assert.Equal("busy 3", await Read(r));
        }
    }

    [Fact]
    public async Task A_post_is_retried_only_with_retry_unsafe_and_then_its_body_goes_again_whole()
    {
        var (ca, server, client) = Certs();
        using (ca) using (server) using (client)
        {
            // 1: the plain gateway's only attempt; 2: the --retry-unsafe one's first; 3: its retry.
            await using var upstream = await KestrelUpstream.StartAsync(server, (c, n) => n <= 2 ? Answer(c, 503) : Answer(c, 200, "ok"));
            GatewayRequest Post() => new("POST", "/orders", Array.Empty<KeyValuePair<string, string>>(),
                new MemoryStream(Encoding.UTF8.GetBytes("{\"id\":7}")), "application/json") { ContentLength = 8 };

            using (var safe = Gateway(upstream.Url, client, Retrying(2)))
            {
                var r = await safe.ForwardAsync(Post(), default);
                Assert.Equal(503, r.StatusCode);
                Assert.Equal(1, r.Attempts);
                await Read(r);
            }
            using (var unsafeToo = Gateway(upstream.Url, client, Retrying(2, unsafeToo: true)))
            {
                var r = await unsafeToo.ForwardAsync(Post(), default);
                Assert.Equal(200, r.StatusCode);
                Assert.Equal(2, r.Attempts);
                await Read(r);
            }
            Assert.Equal(3, upstream.Bodies.Count);
            Assert.All(upstream.Bodies, b => Assert.Equal("{\"id\":7}", b));
        }
    }

    [Fact]
    public async Task A_body_held_in_memory_is_sent_again_from_its_start()
    {
        var (ca, server, client) = Certs();
        using (ca) using (server) using (client)
        {
            await using var upstream = await KestrelUpstream.StartAsync(server, (c, n) => n <= 2 ? Answer(c, 503) : Answer(c, 200));
            using var gw = Gateway(upstream.Url, client, Retrying(3));
            var put = new GatewayRequest("PUT", "/doc", Array.Empty<KeyValuePair<string, string>>(),
                new MemoryStream(Encoding.UTF8.GetBytes("same bytes")), "text/plain") { ContentLength = 10 };
            var r = await gw.ForwardAsync(put, default);
            Assert.Equal(200, r.StatusCode);
            Assert.Equal(3, r.Attempts);
            await Read(r);
            Assert.Equal(new[] { "same bytes", "same bytes", "same bytes" }, upstream.Bodies.ToArray());
        }
    }

    /// <summary>A stream that can only be read once, as a large upload arriving from the caller is.</summary>
    private sealed class OneWayStream(byte[] data) : Stream
    {
        private readonly MemoryStream _inner = new(data);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task A_streamed_body_that_was_sent_is_not_sent_again()
    {
        var (ca, server, client) = Certs();
        using (ca) using (server) using (client)
        {
            await using var upstream = await KestrelUpstream.StartAsync(server, (c, n) => Answer(c, 503));
            using var gw = Gateway(upstream.Url, client, Retrying(3));
            var put = new GatewayRequest("PUT", "/big", Array.Empty<KeyValuePair<string, string>>(),
                new OneWayStream(Encoding.UTF8.GetBytes("streamed")), "text/plain");
            var r = await gw.ForwardAsync(put, default);
            Assert.Equal(503, r.StatusCode);
            Assert.Equal(1, r.Attempts);
            await Read(r);
            Assert.Equal(1, upstream.Requests);
        }
    }

    [Fact]
    public async Task A_retry_after_longer_than_the_timeout_relays_the_answer_at_once()
    {
        var (ca, server, client) = Certs();
        using (ca) using (server) using (client)
        {
            await using var upstream = await KestrelUpstream.StartAsync(server, (c, n) => Answer(c, 503, "later", retryAfter: "20"));
            using var gw = Gateway(upstream.Url, client, Retrying(3), timeoutSeconds: 5);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var r = await gw.ForwardAsync(Get(), default);
            Assert.Equal(503, r.StatusCode);
            Assert.Equal(1, r.Attempts);
            Assert.Equal("later", await Read(r));
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(4), $"waited {clock.Elapsed.TotalSeconds:F1} s");
        }
    }

    [Fact]
    public async Task A_refused_connection_is_retried_and_the_error_says_how_many_attempts()
    {
        var (ca, _, client) = Certs();
        using (ca) using (client)
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            int closedPort = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            using var gw = Gateway($"https://127.0.0.1:{closedPort}", client, Retrying(2));
            var ex = await Assert.ThrowsAsync<HttpRequestException>(() => gw.ForwardAsync(Get(), default));
            Assert.Contains("(3 attempts)", ex.Message);
        }
    }

    [Fact]
    public async Task An_untrusted_server_certificate_is_not_retried()
    {
        var (ca, server, client) = Certs();
        using (ca) using (server) using (client)
        {
            await using var upstream = await KestrelUpstream.StartAsync(server, (c, n) => Answer(c, 200));
            // Certificate errors not ignored: the test CA is trusted nowhere, so the handshake fails.
            using var gw = new MtlsGateway(new GatewayRoutes(new[] { new GatewayRoute("/", new Uri(upstream.Url)) }), client,
                ignoreServerCertificateErrors: false, TimeSpan.FromSeconds(30), Retrying(3));
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var ex = await Assert.ThrowsAsync<HttpRequestException>(() => gw.ForwardAsync(Get(), default));
            Assert.Equal(HttpRequestError.SecureConnectionError, ex.HttpRequestError);
            Assert.DoesNotContain("attempts", ex.Message);
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5));
        }
    }

    // ---- --http2 ----

    [Fact]
    public async Task With_http2_requests_share_one_connection_and_say_so()
    {
        var (ca, server, client) = Certs();
        using (ca) using (server) using (client)
        {
            await using var upstream = await KestrelUpstream.StartAsync(server, (c, n) => Answer(c, 200, "ok"));
            using var gw = Gateway(upstream.Url, client, new TransportOptions { Version = HttpVersionMode.Http2 });
            var responses = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => gw.ForwardAsync(Get(), default)));
            foreach (var r in responses)
            {
                Assert.Equal("2", r.Version);
                await Read(r);
            }
            Assert.All(upstream.Protocols, p => Assert.Equal("HTTP/2", p));
            Assert.Single(upstream.Connections);
        }
    }

    [Fact]
    public async Task Without_http2_the_gateway_speaks_http11_as_before()
    {
        var (ca, server, client) = Certs();
        using (ca) using (server) using (client)
        {
            await using var upstream = await KestrelUpstream.StartAsync(server, (c, n) => Answer(c, 200));
            using var gw = Gateway(upstream.Url, client);
            var r = await gw.ForwardAsync(Get(), default);
            Assert.Equal("1.1", r.Version);
            await Read(r);
            Assert.Equal("HTTP/1.1", upstream.Protocols.Single());
        }
    }

    [Fact]
    public async Task With_http2_a_site_that_has_only_http11_still_answers()
    {
        var (ca, server, client) = Certs();
        using (ca) using (server) using (client)
        {
            await using var upstream = await KestrelUpstream.StartAsync(server, (c, n) => Answer(c, 200, "ok"), HttpProtocols.Http1);
            using var gw = Gateway(upstream.Url, client, new TransportOptions { Version = HttpVersionMode.Http2 });
            var r = await gw.ForwardAsync(Get(), default);
            Assert.Equal(200, r.StatusCode);
            Assert.Equal("1.1", r.Version);
            Assert.Equal("ok", await Read(r));
        }
    }

    // ---- --connection-lifetime ----

    [Fact]
    public async Task Connections_are_replaced_once_their_lifetime_is_over()
    {
        var (ca, server, client) = Certs();
        using (ca) using (server) using (client)
        {
            await using var upstream = await KestrelUpstream.StartAsync(server, (c, n) => Answer(c, 200), HttpProtocols.Http1);
            using var gw = Gateway(upstream.Url, client);
            gw.ConnectionLifetime = TimeSpan.FromMilliseconds(250);
            for (int i = 0; i < 6; i++)
            {
                await Read(await gw.ForwardAsync(Get(), default));
                await Task.Delay(120);
            }
            Assert.True(upstream.Connections.Count >= 2, $"{upstream.Connections.Count} connection(s) for six requests over 700 ms");
        }
    }

    [Fact]
    public async Task By_default_a_connection_in_steady_use_is_kept()
    {
        var (ca, server, client) = Certs();
        using (ca) using (server) using (client)
        {
            await using var upstream = await KestrelUpstream.StartAsync(server, (c, n) => Answer(c, 200), HttpProtocols.Http1);
            using var gw = Gateway(upstream.Url, client);
            Assert.Equal(MtlsGateway.DefaultConnectionLifetime, gw.ConnectionLifetime);
            for (int i = 0; i < 6; i++)
            {
                await Read(await gw.ForwardAsync(Get(), default));
                await Task.Delay(50);
            }
            Assert.Single(upstream.Connections);
        }
    }

    // ---- renewal ----

    [Fact]
    public async Task A_renewed_certificate_is_presented_from_the_next_connection_on()
    {
        var (ca, server, client) = Certs();
        using (ca) using (server) using (client)
        using (var renewed = SelfSignedCertificateFactory.CreateSignedCertificate("GatewayClient", ca, false, true))
        {
            await using var upstream = await KestrelUpstream.StartAsync(server, (c, n) => Answer(c, 200), HttpProtocols.Http1);
            using var gw = Gateway(upstream.Url, client);
            await Read(await gw.ForwardAsync(Get(), default));
            gw.Renew(client, renewed);
            await Read(await gw.ForwardAsync(Get(), default));

            Assert.Equal(new[] { client.Thumbprint, renewed.Thumbprint }, upstream.ClientThumbprints.ToArray());
            Assert.Same(renewed, gw.Current(client));
            Assert.Same(renewed, gw.CertificateFor(new Uri(upstream.Url)));
            Assert.Equal(new[] { renewed.Thumbprint }, gw.PresentedCertificates().Select(c => c.Thumbprint).ToArray());
        }
    }

    [Fact]
    public async Task A_response_still_streaming_finishes_on_the_old_certificate()
    {
        var (ca, server, client) = Certs();
        using (ca) using (server) using (client)
        using (var renewed = SelfSignedCertificateFactory.CreateSignedCertificate("GatewayClient", ca, false, true))
        {
            var release = new TaskCompletionSource();
            await using var upstream = await KestrelUpstream.StartAsync(server, async (c, n) =>
            {
                await c.Response.WriteAsync("first half ");
                await c.Response.Body.FlushAsync();
                if (n == 1) await release.Task;
                await c.Response.WriteAsync("second half");
            }, HttpProtocols.Http1);
            using var gw = Gateway(upstream.Url, client);
            var streaming = await gw.ForwardAsync(Get(), default);
            gw.Renew(client, renewed);
            var reading = Read(streaming);
            release.SetResult();
            Assert.Equal("first half second half", await reading);
        }
    }
}
