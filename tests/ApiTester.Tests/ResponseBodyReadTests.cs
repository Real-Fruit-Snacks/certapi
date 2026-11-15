using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using ApiTester.Core;

namespace ApiTester.Tests;

/// <summary>How a response body is read: a declared length is not trusted with memory before the
/// bytes arrive, a body too large to hold says so, and an infinite timeout really is one.</summary>
public class ResponseBodyReadTests
{
    /// <summary>A one-shot HTTP server that answers every connection with <paramref name="head"/>,
    /// then, after <paramref name="pause"/>, <paramref name="body"/>, then closes.</summary>
    private static (string Url, Task Done) Serve(string head, byte[] body, TimeSpan pause = default)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        string url = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/";
        var done = Task.Run(async () =>
        {
            try
            {
                using var client = await listener.AcceptTcpClientAsync();
                var stream = client.GetStream();
                var buffer = new byte[65536];
                var seen = new StringBuilder();
                while (!seen.ToString().Contains("\r\n\r\n"))
                {
                    int n = await stream.ReadAsync(buffer);
                    if (n == 0) return;
                    seen.Append(Encoding.ASCII.GetString(buffer, 0, n));
                }
                await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
                await stream.FlushAsync();
                if (pause > TimeSpan.Zero) await Task.Delay(pause);
                await stream.WriteAsync(body);
                await stream.FlushAsync();
            }
            finally { listener.Stop(); }
        });
        return (url, done);
    }

    private static Task<ApiResponse> Get(string url, TimeSpan? timeout = null) =>
        new ApiClient().SendAsync(
            new ApiRequest { Method = HttpMethod.Get, Url = url, Timeout = timeout ?? TimeSpan.FromSeconds(30) }, null);

    [Fact]
    public async Task A_claimed_length_is_not_allocated_before_the_bytes_arrive()
    {
        var (url, done) = Serve("HTTP/1.1 200 OK\r\nContent-Length: 1900000000\r\nContent-Type: text/plain\r\n\r\n",
            Encoding.ASCII.GetBytes("only-this."));
        long before = GC.GetTotalAllocatedBytes(precise: true);
        var response = await Get(url);
        long allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
        await done;
        // The server said 1.9 GB and sent ten bytes: that is a connection that ended mid-body,
        // not 1.9 GB of memory (which, under a memory limit, failed as OutOfMemoryException).
        Assert.NotNull(response.Error);
        Assert.Contains("Error while copying content", response.Error!.Message);
        Assert.True(allocated < 1024L * 1024 * 1024, $"allocated {allocated:N0} bytes for a 10-byte body");
    }

    [Fact]
    public async Task A_length_past_what_memory_can_hold_is_not_refused_before_it_is_read()
    {
        // HEAD carries no body whatever it declares: a 3 GB file's HEAD is an ordinary answer.
        var (url, done) = Serve("HTTP/1.1 200 OK\r\nContent-Length: 3000000000\r\n\r\n", []);
        var head = await new ApiClient().SendAsync(new ApiRequest { Method = HttpMethod.Head, Url = url }, null);
        await done;
        Assert.Null(head.Error);
        Assert.Equal(200, head.StatusCode);
        Assert.Empty(head.Body);

        // And a GET claiming as much that sends ten bytes is a body that ended early.
        (url, done) = Serve("HTTP/1.1 200 OK\r\nContent-Length: 3000000000\r\n\r\n", Encoding.ASCII.GetBytes("only-this."));
        var get = await Get(url);
        await done;
        Assert.NotNull(get.Error);
        Assert.Contains("Error while copying content", get.Error!.Message);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(ApiClient.FirstBodyBuffer)]
    [InlineData(ApiClient.FirstBodyBuffer + 1)]
    [InlineData(3 * ApiClient.FirstBodyBuffer + 12345)]
    [InlineData(ApiClient.TrustDeclaredAfter + 3 * ApiClient.FirstBodyBuffer + 7)]
    public async Task A_body_of_its_declared_length_arrives_whole(int length)
    {
        var body = new byte[length];
        new Random(length).NextBytes(body);
        var (url, done) = Serve($"HTTP/1.1 200 OK\r\nContent-Length: {length}\r\n\r\n", body);
        var response = await Get(url);
        await done;
        Assert.Null(response.Error);
        Assert.Equal(length, response.Body.Length);
        Assert.True(body.AsSpan().SequenceEqual(response.Body));
    }

    [Fact]
    public async Task An_infinite_timeout_does_not_time_the_body_out()
    {
        var (url, done) = Serve("HTTP/1.1 200 OK\r\nContent-Length: 5\r\n\r\n", Encoding.ASCII.GetBytes("hello"),
            pause: TimeSpan.FromMilliseconds(300));
        var response = await Get(url, Timeout.InfiniteTimeSpan);
        await done;
        Assert.Null(response.Error);
        Assert.Equal("hello", Encoding.ASCII.GetString(response.Body));
    }
}
