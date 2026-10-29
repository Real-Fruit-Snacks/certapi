using System.IO;
using System.Net;
using System.Text;
using ApiTester.Cli;

namespace ApiTester.Tests.Cli;

/// <summary>`send` prints to a console only what a console can draw: a binary body is held back and
/// a long text body is cut at 1 MB, each with a note. A file, a pipe and `-o -` get every byte.</summary>
public class ConsoleBodyTests
{
    /// <summary>Answers every request with the same body.</summary>
    private sealed class Fixed : IDisposable
    {
        private readonly HttpListener _listener = new();
        public string Url { get; }

        public Fixed(byte[] body, string contentType)
        {
            int port = ServeFixture.FreePort();
            Url = $"http://127.0.0.1:{port}/";
            _listener.Prefixes.Add(Url);
            _listener.Start();
            _ = Task.Run(async () =>
            {
                while (_listener.IsListening)
                {
                    HttpListenerContext ctx;
                    try { ctx = await _listener.GetContextAsync(); } catch { return; }
                    ctx.Response.ContentType = contentType;
                    ctx.Response.ContentLength64 = body.Length;
                    try { await ctx.Response.OutputStream.WriteAsync(body); } catch { }
                    ctx.Response.Close();
                }
            });
        }

        public void Dispose() { try { _listener.Stop(); } catch { } }
    }

    private static (int Code, byte[] Body, string Out, string Err) Send(bool console, params string[] args)
    {
        var so = new StringWriter();
        var se = new StringWriter();
        var body = new MemoryStream();
        int code = CliApp.Run(new[] { "send" }.Concat(args).Append("--no-config").ToArray(), so, se, body,
            new CliServices
            {
                UserConfigPath = null,
                LiveStatePath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json"),
                StdoutIsTerminal = console,
            });
        return (code, body.ToArray(), so.ToString(), se.ToString());
    }

    private static readonly byte[] Pdf = Encoding.ASCII.GetBytes("%PDF-1.7\n").Concat(new byte[] { 0, 0x1B, 0x5B, 0x32, 0x4A, 0xFF }).ToArray();

    [Fact]
    public void A_binary_body_is_not_printed_to_a_console()
    {
        using var server = new Fixed(Pdf, "application/pdf");
        var r = Send(console: true, server.Url);
        Assert.Equal(0, r.Code);
        Assert.Empty(r.Body);
        Assert.Contains("the body is binary", r.Err);
        Assert.Contains("application/pdf", r.Err);
        Assert.Contains("-o - prints it here anyway", r.Err);
    }

    [Fact]
    public void A_binary_body_reaches_a_pipe_byte_for_byte()
    {
        using var server = new Fixed(Pdf, "application/pdf");
        var r = Send(console: false, server.Url);
        Assert.Equal(0, r.Code);
        Assert.Equal(Pdf, r.Body);
        Assert.DoesNotContain("binary", r.Err);
    }

    [Fact]
    public void Dash_o_dash_prints_a_binary_body_to_the_console_anyway()
    {
        using var server = new Fixed(Pdf, "application/pdf");
        var r = Send(console: true, server.Url, "-o", "-");
        Assert.Equal(0, r.Code);
        Assert.Equal(Pdf, r.Body);
        Assert.DoesNotContain("note:", r.Err);
        Assert.False(File.Exists("-"), "-o - wrote a file named '-'");
    }

    [Fact]
    public void A_long_text_body_is_cut_at_1_MB_on_a_console_and_whole_elsewhere()
    {
        // é is two bytes; an odd limit would land inside one.
        var text = Encoding.UTF8.GetBytes("a" + new string('é', 1_600_000));
        using var server = new Fixed(text, "text/plain; charset=utf-8");

        var console = Send(console: true, server.Url);
        Assert.Equal(0, console.Code);
        Assert.InRange(console.Body.Length, ConsoleBody.MaxBytes - 3, ConsoleBody.MaxBytes);
        Assert.True(System.Text.Unicode.Utf8.IsValid(console.Body), "the cut split a character");
        Assert.Contains("the console shows the first 1 MB of this 3.1 MB body", console.Err);

        var pipe = Send(console: false, server.Url);
        Assert.Equal(text, pipe.Body);

        var all = Send(console: true, server.Url, "-o", "-");
        Assert.Equal(text, all.Body);
    }

    [Fact]
    public void A_short_text_body_is_printed_whole_to_a_console()
    {
        var json = Encoding.UTF8.GetBytes("{\"ok\":true}");
        using var server = new Fixed(json, "application/json");
        var r = Send(console: true, server.Url);
        Assert.Equal(json, r.Body);
        Assert.DoesNotContain("note:", r.Err);
    }

    [Fact]
    public void Pretty_shows_only_the_start_of_a_large_binary_body_as_hex_on_a_console()
    {
        var blob = new byte[300_000];
        new Random(7).NextBytes(blob);
        blob[0] = 0;
        using var server = new Fixed(blob, "application/octet-stream");

        var r = Send(console: true, server.Url, "--pretty");
        Assert.Equal(0, r.Code);
        Assert.Equal(ConsoleBody.MaxHexBytes / 16, Lines(r.Out));
        Assert.Contains("first 64 KB of this 293.0 KB binary body as hex", r.Err);

        var pipe = Send(console: false, server.Url, "--pretty");
        Assert.Equal(blob.Length / 16 + (blob.Length % 16 == 0 ? 0 : 1), Lines(pipe.Out));

        // Lines with text in them, however they end: the dump's own lines end in \n, and the
        // line after it in the platform's newline (\r\n on Windows).
        static int Lines(string text) => text.Split('\n').Count(l => l.TrimEnd('\r').Length > 0);
    }

    [Fact]
    public void Pretty_prints_a_body_too_large_to_format_as_received_on_a_console()
    {
        var json = Encoding.UTF8.GetBytes("[" + string.Join(",", Enumerable.Repeat("{\"n\":1}", 700_000)) + "]");
        Assert.True(json.Length > ConsoleBody.MaxFormatBytes);
        using var server = new Fixed(json, "application/json");

        var r = Send(console: true, server.Url, "--pretty");
        Assert.Equal(0, r.Code);
        Assert.Equal(json.AsSpan(0, ConsoleBody.MaxBytes).ToArray(), r.Body);   // the start, not indented
        Assert.Contains("too large to format on the console", r.Err);
    }

    [Theory]
    [InlineData(new byte[] { 0x48, 0x69, 0x00 }, "text/plain", true)]          // a NUL is never text
    [InlineData(new byte[] { 0x63, 0x61, 0x66, 0xE9 }, "text/html; charset=iso-8859-1", false)]  // café in Latin-1
    [InlineData(new byte[] { 0x63, 0x61, 0x66, 0xE9 }, "application/octet-stream", true)]
    [InlineData(new byte[] { 0x63, 0x61, 0x66, 0xE9 }, null, true)]
    [InlineData(new byte[] { 0x63, 0x61, 0x66, 0xC3, 0xA9 }, null, false)]    // café in UTF-8
    public void What_counts_as_binary(byte[] body, string? contentType, bool binary) =>
        Assert.Equal(binary, ConsoleBody.LooksBinary(body, contentType));

    [Fact]
    public void A_cut_never_ends_inside_a_character()
    {
        var bytes = Encoding.UTF8.GetBytes("ab€");   // € is three bytes: 61 62 E2 82 AC
        Assert.Equal(2, ConsoleBody.CutAt(bytes, 3));
        Assert.Equal(2, ConsoleBody.CutAt(bytes, 4));
        Assert.Equal(5, ConsoleBody.CutAt(bytes, 5));
        Assert.Equal(5, ConsoleBody.CutAt(bytes, 100));
    }
}
