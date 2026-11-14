using System.IO;
using System.Text.Json;
using ApiTester.Core;
using ApiTester.Grpc;

namespace ApiTester.Tests.Grpc;

/// <summary>gRPC-Web, against a real ASP.NET Core server serving it over HTTP/1.1 only — so a call
/// that succeeds cannot have gone over native gRPC. Descriptors come from a descriptor set, as the
/// command line requires, because reflection is a bidirectional stream gRPC-Web cannot carry.</summary>
public class GrpcWebTests
{
    private static readonly IReadOnlyList<KeyValuePair<string, string>> NoMetadata =
        Array.Empty<KeyValuePair<string, string>>();

    private static async Task<(GrpcTestServer Server, GrpcCaller Caller, string Path)> ConnectAsync(GrpcWire wire)
    {
        var server = await GrpcTestServer.StartWebAsync();
        string path = DescriptorSetFixture.WriteTemp(DescriptorSetFixture.Complete());
        var caller = new GrpcCaller(server.Uri, clientCertificate: null, new TransportOptions { GrpcWire = wire },
            trustServerCertificate: null, GrpcDescriptorSet.Load(path));
        return (server, caller, path);
    }

    [Theory]
    [InlineData(GrpcWire.Web)]
    [InlineData(GrpcWire.WebText)]
    public async Task A_unary_call_works_over_gRPC_Web(GrpcWire wire)
    {
        var (server, caller, path) = await ConnectAsync(wire);
        try
        {
            await caller.DiscoverAsync(CancellationToken.None);
            var result = await caller.InvokeAsync("certapi.test.Echo", "Unary", """{"text":"web"}""", NoMetadata, CancellationToken.None);
            Assert.Equal(0, result.StatusCode);
            using var doc = JsonDocument.Parse(result.ResponseJson);
            Assert.Equal("web", doc.RootElement.GetProperty("text").GetString());
        }
        finally
        {
            await caller.DisposeAsync();
            await server.DisposeAsync();
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Server_streaming_works_over_gRPC_Web()
    {
        var (server, caller, path) = await ConnectAsync(GrpcWire.Web);
        try
        {
            await caller.DiscoverAsync(CancellationToken.None);
            var items = new List<string>();
            await foreach (var item in caller.InvokeStreamingAsync("certapi.test.Echo", "ServerStream",
                               """{"text":"s","count":3}""", NoMetadata, CancellationToken.None))
                items.Add(item);
            Assert.Equal(3, items.Count);
        }
        finally
        {
            await caller.DisposeAsync();
            await server.DisposeAsync();
            File.Delete(path);
        }
    }

    private static async IAsyncEnumerable<string> One()
    {
        await Task.Yield();
        yield return "{}";
    }

    [Fact]
    public async Task A_client_streaming_call_is_refused_before_anything_is_sent()
    {
        var (server, caller, path) = await ConnectAsync(GrpcWire.Web);
        try
        {
            await caller.DiscoverAsync(CancellationToken.None);
            var ex = await Assert.ThrowsAsync<GrpcUnsupportedMethodException>(() =>
                caller.InvokeClientStreamingAsync("certapi.test.Echo", "ClientStream", One(), NoMetadata, CancellationToken.None));
            Assert.Contains("gRPC-Web", ex.Message);
        }
        finally
        {
            await caller.DisposeAsync();
            await server.DisposeAsync();
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Native_gRPC_cannot_reach_a_server_that_only_speaks_gRPC_Web_over_HTTP_1_1()
    {
        var (server, caller, path) = await ConnectAsync(GrpcWire.Native);
        try
        {
            await caller.DiscoverAsync(CancellationToken.None);
            GrpcCallResult? result = null;
            try { result = await caller.InvokeAsync("certapi.test.Echo", "Unary", """{"text":"x"}""", NoMetadata, CancellationToken.None); }
            catch (Exception) { return; }   // refused outright is as good as a failed status
            Assert.NotEqual(0, result.StatusCode);
        }
        finally
        {
            await caller.DisposeAsync();
            await server.DisposeAsync();
            File.Delete(path);
        }
    }
}
