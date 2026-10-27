using System.IO;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace ApiTester.Tests;

/// <summary>A minimal plain-HTTP server for tests that need to answer with exact status lines and
/// headers (redirects, auth challenges) and see exactly what the client sent. One request per
/// connection; the handler gets the request line and headers and returns the whole response.</summary>
internal sealed class RawHttpServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cts = new();
    private readonly Func<RawRequest, string> _handler;
    private readonly Task _loop;

    public ConcurrentQueue<RawRequest> Requests { get; } = new();
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    public string BaseUrl => $"http://127.0.0.1:{Port}";

    public RawHttpServer(Func<RawRequest, string> handler)
    {
        _handler = handler;
        _listener.Start();
        _loop = Task.Run(LoopAsync);
    }

    private async Task LoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_cts.Token); }
            catch { return; }
            _ = Task.Run(async () =>
            {
                using (client)
                {
                    try
                    {
                        var stream = client.GetStream();
                        var buf = new List<byte>();
                        var one = new byte[1];
                        while (!EndsWithBlankLine(buf) && await stream.ReadAsync(one) == 1) buf.Add(one[0]);
                        var text = Encoding.ASCII.GetString(buf.ToArray());
                        var lines = text.Split("\r\n");
                        var headers = new List<KeyValuePair<string, string>>();
                        foreach (var l in lines.Skip(1))
                        {
                            int c = l.IndexOf(':');
                            if (c > 0) headers.Add(new(l[..c].Trim(), l[(c + 1)..].Trim()));
                        }
                        int length = headers.Where(h => h.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                                            .Select(h => int.Parse(h.Value)).FirstOrDefault();
                        var body = new byte[length];
                        int got = 0;
                        while (got < length)
                        {
                            int n = await stream.ReadAsync(body.AsMemory(got));
                            if (n == 0) break;
                            got += n;
                        }
                        var req = new RawRequest(lines[0], headers, body);
                        Requests.Enqueue(req);
                        var response = Encoding.UTF8.GetBytes(_handler(req));
                        await stream.WriteAsync(response);
                    }
                    catch { /* the client went away */ }
                }
            });
        }
    }

    private static bool EndsWithBlankLine(List<byte> b) =>
        b.Count >= 4 && b[^4] == '\r' && b[^3] == '\n' && b[^2] == '\r' && b[^1] == '\n';

    /// <summary>A complete response with a body and Connection: close.</summary>
    public static string Response(int status, string reason, string body = "", params string[] headers)
    {
        var sb = new StringBuilder();
        sb.Append($"HTTP/1.1 {status} {reason}\r\n");
        foreach (var h in headers) sb.Append(h).Append("\r\n");
        sb.Append($"Content-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n\r\n").Append(body);
        return sb.ToString();
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        try { await _loop; } catch { }
        _cts.Dispose();
    }
}

internal sealed record RawRequest(string RequestLine, List<KeyValuePair<string, string>> Headers, byte[] Body)
{
    public string Path => RequestLine.Split(' ')[1];
    public string? Header(string name) =>
        Headers.Where(h => h.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Select(h => h.Value).FirstOrDefault();
}
