using System.Net;
using System.Security.Cryptography.X509Certificates;
using Certapi.Test;
using Grpc.AspNetCore.Web;
using Grpc.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ApiTester.Tests.Grpc;

/// <summary>An in-process gRPC server for the tests: real Kestrel, real HTTP/2, real server
/// reflection — so GrpcCaller (a later slice) is exercised against a genuine server rather than a
/// stand-in. This slice proves the harness itself works, using the generated <see cref="Echo"/>
/// client stub directly.</summary>
internal sealed class GrpcTestServer : IAsyncDisposable
{
    private readonly WebApplication _app;

    private GrpcTestServer(WebApplication app, string address)
    {
        _app = app;
        Address = address;
        Uri = new Uri(address);
    }

    /// <summary>e.g. "http://127.0.0.1:51234" — no trailing slash.</summary>
    public string Address { get; }

    public Uri Uri { get; }

    /// <summary>Plaintext HTTP/2 (h2c) on a free loopback port. The default for the functional
    /// tests: no TLS to go wrong where TLS is not what is under test. <paramref name="reflection"/>
    /// set to false hosts the Echo service without the reflection service, so the "server does not
    /// implement reflection" path (exit 3 in the CLI, D4 of the spec) is testable against a real
    /// server rather than only asserted from the reflection client's own code.</summary>
    public static Task<GrpcTestServer> StartAsync(bool reflection = true) =>
        StartInternalAsync(serverCertificate: null, requireClientCertificate: false, reflection);

    /// <summary>Plaintext HTTP/1.1 and HTTP/2 with gRPC-Web turned on for every service, the way a
    /// browser-facing ASP.NET Core server (UseGrpcWeb) or an Envoy filter would serve it.</summary>
    public static Task<GrpcTestServer> StartWebAsync() =>
        StartInternalAsync(serverCertificate: null, requireClientCertificate: false, reflection: true, web: true);

    /// <summary>HTTPS on a free loopback port. <paramref name="requireClientCertificate"/> makes
    /// the mutual-TLS variant: the handshake fails unless the client presents one.</summary>
    public static Task<GrpcTestServer> StartTlsAsync(X509Certificate2 serverCertificate, bool requireClientCertificate) =>
        StartInternalAsync(serverCertificate, requireClientCertificate, reflection: true);

    private static async Task<GrpcTestServer> StartInternalAsync(
        X509Certificate2? serverCertificate, bool requireClientCertificate, bool reflection, bool web = false)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = AppContext.BaseDirectory
        });
        // The harness must not spray Kestrel/ASP.NET Core's own logging into the test output.
        builder.Logging.ClearProviders();

        // Kestrel's HTTPS layer wants a certificate it can hand to SChannel; re-importing through a
        // PFX round-trip with X509KeyStorageFlags.Exportable (exactly what SelfSignedCertificateFactory
        // already does internally for its own certificates) makes any in-memory certificate usable
        // here too, without changing SelfSignedCertificateFactory itself.
        X509Certificate2? kestrelCertificate = serverCertificate is null
            ? null
            : X509CertificateLoader.LoadPkcs12(
                serverCertificate.Export(X509ContentType.Pfx), (string?)null, X509KeyStorageFlags.Exportable);

        builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0, listen =>
        {
            // gRPC-Web is HTTP/1.1 by design (that is its point); plain h2c cannot also offer it.
            listen.Protocols = web ? HttpProtocols.Http1 : HttpProtocols.Http2;
            if (kestrelCertificate is not null)
            {
                listen.UseHttps(https =>
                {
                    https.ServerCertificate = kestrelCertificate;
                    https.ClientCertificateMode = requireClientCertificate
                        ? ClientCertificateMode.RequireCertificate
                        : ClientCertificateMode.AllowCertificate;
                    // The server side is not what is under test here: the fact under test is that
                    // the client presented a certificate at all, not that the server validated it.
                    https.ClientCertificateValidation = (_, _, _) => true;
                });
            }
        }));

        builder.Services.AddGrpc(o => o.EnableDetailedErrors = true);
        if (reflection) builder.Services.AddGrpcReflection();

        var app = builder.Build();
        if (web) app.UseGrpcWeb(new GrpcWebOptions { DefaultEnabled = true });
        app.MapGrpcService<EchoService>();
        if (reflection) app.MapGrpcReflectionService();

        await app.StartAsync();

        var addressesFeature = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
        string boundAddress = addressesFeature!.Addresses.First();

        return new GrpcTestServer(app, boundAddress);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    private sealed class EchoService : Echo.EchoBase
    {
        public override Task<EchoReply> Unary(EchoRequest request, ServerCallContext context)
        {
            context.ResponseTrailers.Add("x-test-trailer", "trailed");
            return Task.FromResult(BuildReply(request, context, 0, request.Text));
        }

        public override async Task ServerStream(
            EchoRequest request, IServerStreamWriter<EchoReply> responseStream, ServerCallContext context)
        {
            int count = request.Count <= 0 ? 3 : request.Count;
            for (int i = 0; i < count; i++)
            {
                await responseStream.WriteAsync(BuildReply(request, context, i, $"{request.Text}-{i}"));
            }
        }

        public override async Task StreamThenFail(
            EchoRequest request, IServerStreamWriter<EchoReply> responseStream, ServerCallContext context)
        {
            for (int i = 0; i < 2; i++)
            {
                await responseStream.WriteAsync(BuildReply(request, context, i, $"{request.Text}-{i}"));
            }
            throw new RpcException(new Status(StatusCode.ResourceExhausted, "stream cut short"));
        }

        public override Task<EchoReply> Failing(EchoRequest request, ServerCallContext context)
        {
            throw new RpcException(
                new Status(StatusCode.PermissionDenied, "test denied"),
                new Metadata { { "x-test-trailer", "trailed" } });
        }

        public override async Task<EchoReply> ClientStream(
            IAsyncStreamReader<EchoRequest> requestStream, ServerCallContext context)
        {
            int count = 0;
            while (await requestStream.MoveNext(context.CancellationToken))
            {
                count++;
            }
            return new EchoReply { Count = count };
        }

        public override async Task BidiStream(
            IAsyncStreamReader<EchoRequest> requestStream, IServerStreamWriter<EchoReply> responseStream,
            ServerCallContext context)
        {
            int index = 0;
            while (await requestStream.MoveNext(context.CancellationToken))
            {
                var request = requestStream.Current;
                // Reply to each request as it arrives instead of buffering the whole stream first
                // — buffering would silently turn this into ServerStream-after-the-fact and defeat
                // the fixture's entire purpose (proving sending never blocks receiving).
                await responseStream.WriteAsync(BuildReply(request, context, index++, request.Text));
            }
        }

        public override async Task<EchoReply> ClientStreamThenFail(
            IAsyncStreamReader<EchoRequest> requestStream, ServerCallContext context)
        {
            // Ignore whether a message actually arrived: a zero-message stream fails identically,
            // so the client-streaming caller can be tested on the failure path with or without data.
            await requestStream.MoveNext(context.CancellationToken);
            throw new RpcException(
                new Status(StatusCode.PermissionDenied, "client stream refused"),
                new Metadata { { "x-test-trailer", "trailed" } });
        }

        public override async Task BidiStreamThenFail(
            IAsyncStreamReader<EchoRequest> requestStream, IServerStreamWriter<EchoReply> responseStream,
            ServerCallContext context)
        {
            if (await requestStream.MoveNext(context.CancellationToken))
            {
                await responseStream.WriteAsync(BuildReply(requestStream.Current, context, 0, requestStream.Current.Text));
            }
            // Unconditional: even with no messages at all, the call still ends in this failure so
            // a duplex caller always has a non-OK status to observe.
            throw new RpcException(new Status(StatusCode.ResourceExhausted, "duplex cut short"));
        }

        private static EchoReply BuildReply(EchoRequest request, ServerCallContext context, int messageIndex, string text)
        {
            var reply = new EchoReply
            {
                Text = text,
                Count = request.Count,
                Color = request.Color,
                Nested = request.Nested,
                All = request.All,
                MessageIndex = messageIndex,
                ObservedMetadata = ObservedMetadata(context),
                ObservedClientCertThumbprint = context.GetHttpContext().Connection.ClientCertificate?.Thumbprint ?? string.Empty
            };
            reply.Tags.Add(request.Tags);
            return reply;
        }

        private static string ObservedMetadata(ServerCallContext context)
        {
            foreach (var entry in context.RequestHeaders)
            {
                if (entry.Key == "x-test-key") return entry.Value;
            }
            return string.Empty;
        }
    }
}
