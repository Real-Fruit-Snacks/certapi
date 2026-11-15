using System.IO;
using System.Security.Cryptography.X509Certificates;
using ApiTester.Cli;
using ApiTester.Core;

namespace ApiTester.Tests.Cli;

public class StreamingCliTests
{
    private static string TempState() => Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");

    private static CliServices ServicesWith(X509Certificate2 clientCert) => new()
    {
        LiveStatePath = TempState(),
        IsGuiRunning = () => false,
        ListCertificates = _ => new[]
        {
            new CertificateInfo
            {
                Subject = "CN=CliClient", Issuer = "CN=CA", Thumbprint = clientCert.Thumbprint!,
                NotBefore = DateTime.Now.AddDays(-1), NotAfter = DateTime.Now.AddDays(30),
                HasClientAuthEku = true, Certificate = clientCert
            }
        }
    };

    [Fact]
    public async Task Sse_command_prints_each_event()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("CliClient", ca, false, true);
        var events = new List<(string?, string)> { ("greeting", "hello"), (null, "world") };
        await using var server = await LoopbackMtlsServer.StartSseAsync(serverCert, clientCert.Thumbprint!, events);

        var so = new StringWriter();
        var se = new StringWriter();
        int code = CliApp.Run(
            new[] { "sse", server.BaseUrl, "--cert", "CliClient", "--insecure", "--max-events", "2" },
            new StringReader(""), so, se, new MemoryStream(), ServicesWith(clientCert));

        Assert.Equal(0, code);
        string outp = so.ToString();
        Assert.Contains("event: greeting", outp);
        Assert.Contains("hello", outp);
        Assert.Contains("world", outp);
    }

    [Fact]
    public async Task Ws_command_sends_messages_and_prints_replies()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("CliClient", ca, false, true);
        await using var server = await LoopbackMtlsServer.StartWebSocketEchoAsync(serverCert, clientCert.Thumbprint!);

        var so = new StringWriter();
        var se = new StringWriter();
        int code = CliApp.Run(
            new[] { "ws", server.WebSocketUrl, "--cert", "CliClient", "--insecure",
                    "-m", "alpha", "-m", "beta", "--expect", "2" },
            new StringReader(""), so, se, new MemoryStream(), ServicesWith(clientCert));

        Assert.Equal(0, code);
        string outp = so.ToString();
        Assert.Contains("alpha", outp);
        Assert.Contains("beta", outp);
    }

    [Fact]
    public async Task Ws_command_sends_lines_piped_on_stdin()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("CliClient", ca, false, true);
        await using var server = await LoopbackMtlsServer.StartWebSocketEchoAsync(serverCert, clientCert.Thumbprint!);

        var so = new StringWriter();
        var se = new StringWriter();
        int code = CliApp.Run(
            new[] { "ws", server.WebSocketUrl, "--cert", "CliClient", "--insecure", "--expect", "1" },
            new StringReader("pinged\n"), so, se, new MemoryStream(), ServicesWith(clientCert));

        Assert.Equal(0, code);
        Assert.Contains("pinged", so.ToString());
    }

    /// <summary>A stdin that is a pipe nobody ever closes: no line, and no end.</summary>
    private sealed class NeverEndingInput : TextReader
    {
        public override string? ReadLine() { Thread.Sleep(Timeout.Infinite); return null; }
        public override async ValueTask<string?> ReadLineAsync(CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            return null;
        }
    }

    [Fact]
    public async Task Ws_does_not_wait_for_a_stdin_that_never_ends()
    {
        // A CI step or a scheduled task often runs with stdin a pipe that stays open: -m and
        // --expect must finish there instead of waiting to read stdin to its end first.
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("CliClient", ca, false, true);
        await using var server = await LoopbackMtlsServer.StartWebSocketEchoAsync(serverCert, clientCert.Thumbprint!);

        var so = new StringWriter();
        var run = Task.Run(() => CliApp.Run(
            new[] { "ws", server.WebSocketUrl, "--cert", "CliClient", "--insecure", "-m", "hello", "--expect", "1" },
            new NeverEndingInput(), so, new StringWriter(), new MemoryStream(), ServicesWith(clientCert)));
        Assert.Same(run, await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(15))));
        Assert.Equal(0, await run);
        Assert.Contains("hello", so.ToString());
    }

    [Fact]
    public async Task Ws_expect_zero_sends_without_waiting_for_a_reply()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("CliClient", ca, false, true);
        await using var server = await LoopbackMtlsServer.StartWebSocketEchoAsync(serverCert, clientCert.Thumbprint!);

        var so = new StringWriter();
        var se = new StringWriter();
        // The echo server never closes; with --expect 0 the command must return promptly anyway.
        var run = Task.Run(() => CliApp.Run(
            new[] { "ws", server.WebSocketUrl, "--cert", "CliClient", "--insecure", "-m", "fire", "--expect", "0" },
            new StringReader(""), so, se, new MemoryStream(), ServicesWith(clientCert)));

        var finished = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(15)));
        Assert.True(finished == run, "ws --expect 0 should not block waiting for a reply");
        Assert.Equal(0, await run);
    }

    [Fact]
    public void Ws_bad_expect_is_a_usage_error()
    {
        var so = new StringWriter();
        var se = new StringWriter();
        int code = CliApp.Run(
            new[] { "ws", "wss://x.example", "--expect", "-1" },
            new StringReader(""), so, se, new MemoryStream(), new CliServices { LiveStatePath = TempState() });
        Assert.Equal(2, code);
    }
}
