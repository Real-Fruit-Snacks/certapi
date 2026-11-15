using System.IO;
using System.Net.Sockets;
using System.Text;
using ApiTester.Core;

namespace ApiTester.Tests;

[CollectionDefinition(nameof(MockKeepAliveTimeoutTests), DisableParallelization = true)]
public class MockKeepAliveTimeoutCollection { }

/// <summary>The idle limit of a kept-alive connection is how long the mock waits for the next
/// request to begin, not how long that request may take to arrive: a large or slow upload on a
/// reused connection is read to its end.</summary>
[Collection(nameof(MockKeepAliveTimeoutTests))]
public class MockKeepAliveTimeoutTests
{
    private static async Task<string> ReadResponseAsync(NetworkStream stream)
    {
        var buffer = new byte[65536];
        var text = new StringBuilder();
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            string s = text.ToString();
            int end = s.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (end >= 0)
            {
                var m = System.Text.RegularExpressions.Regex.Match(s, @"Content-Length: (\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (m.Success && s.Length - end - 4 >= int.Parse(m.Groups[1].Value)) return s;
            }
            int n = await stream.ReadAsync(buffer, limit.Token);
            if (n == 0) return s;
            text.Append(Encoding.UTF8.GetString(buffer, 0, n));
        }
    }

    [Fact]
    public async Task A_slow_body_on_a_kept_alive_connection_is_read_to_its_end()
    {
        var idle = MockServer.KeepAliveIdle;
        MockServer.KeepAliveIdle = TimeSpan.FromSeconds(1);
        try
        {
            await using var srv = MockServer.Start(0, MockTlsMode.Http);
            using var client = new TcpClient();
            await client.ConnectAsync(new Uri(srv.BaseUrl).Host, new Uri(srv.BaseUrl).Port);
            var stream = client.GetStream();

            await stream.WriteAsync(Encoding.ASCII.GetBytes("GET /echo HTTP/1.1\r\nHost: x\r\n\r\n"));
            Assert.StartsWith("HTTP/1.1 200", await ReadResponseAsync(stream));

            // The second request starts at once, and its body takes three idle limits to arrive.
            string body = "0123456789";
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"POST /echo HTTP/1.1\r\nHost: x\r\nContent-Length: {body.Length}\r\n\r\n"));
            foreach (char c in body)
            {
                await Task.Delay(300);
                await stream.WriteAsync(new[] { (byte)c });
            }
            string second = await ReadResponseAsync(stream);
            Assert.StartsWith("HTTP/1.1 200", second);
            Assert.Contains(body, second);

            // And the idle limit itself still holds: nothing sent, the connection is closed.
            await Task.Delay(TimeSpan.FromSeconds(2.5));
            var one = new byte[1];
            using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            Assert.Equal(0, await stream.ReadAsync(one, wait.Token));
        }
        finally { MockServer.KeepAliveIdle = idle; }
    }

    [Fact]
    public async Task A_first_request_has_its_allowance_once_not_again_from_its_first_byte()
    {
        var allowance = MockServer.RequestReadTimeout;
        MockServer.RequestReadTimeout = TimeSpan.FromSeconds(2);
        try
        {
            await using var srv = MockServer.Start(0, MockTlsMode.Http);
            using var client = new TcpClient();
            await client.ConnectAsync(new Uri(srv.BaseUrl).Host, new Uri(srv.BaseUrl).Port);
            var stream = client.GetStream();
            var started = System.Diagnostics.Stopwatch.StartNew();
            await Task.Delay(1500);
            // A request that begins late and never finishes: dropped at two seconds from the
            // connection, not two seconds from its first byte.
            var closed = Task.Run(async () =>
            {
                var one = new byte[1];
                try { return await stream.ReadAsync(one) == 0; } catch (IOException) { return true; }
            });
            foreach (char c in "GET /echo HTTP/1.1\r\nHost: x\r\n")
            {
                if (closed.IsCompleted) break;
                try { await stream.WriteAsync(new[] { (byte)c }); } catch (IOException) { break; }
                await Task.Delay(100);
            }
            Assert.True(await closed.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.True(started.Elapsed < TimeSpan.FromSeconds(3), $"closed after {started.Elapsed.TotalSeconds:F1} s");
        }
        finally { MockServer.RequestReadTimeout = allowance; }
    }
}
