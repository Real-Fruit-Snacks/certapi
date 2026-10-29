using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using ApiTester.Cli;
using ApiTester.Core;

namespace ApiTester.Tests.Cli;

/// <summary>A console that blocks or draws slowly must never hold up the gateway: the log is
/// written by its own thread, bounded, and rate-limited to a terminal.</summary>
public class ServeLogTests
{
    /// <summary>A console in a QuickEdit selection: every write waits until it is released.</summary>
    private sealed class BlockedConsole : TextWriter
    {
        private readonly ManualResetEventSlim _open = new(false);
        private readonly StringBuilder _text = new();
        public int Writes;
        public override Encoding Encoding => Encoding.UTF8;
        public void Release() => _open.Set();
        public override void Write(char value) => WriteLine(value.ToString());
        public override void WriteLine(string? value)
        {
            _open.Wait();
            lock (_text) { _text.AppendLine(value); Writes++; }
        }
        public override string ToString() { lock (_text) return _text.ToString(); }
    }

    private static string[] Lines(StringWriter w) => w.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    [Fact]
    public async Task A_blocked_console_never_blocks_the_caller()
    {
        var console = new BlockedConsole();
        using var log = new ServeLog(console, rateLimit: false, capacity: 100);
        var done = Task.Run(() => { for (int i = 0; i < 500; i++) log.WriteRequest($"GET /{i}  -> 200"); });
        Assert.True(await Task.WhenAny(done, Task.Delay(TimeSpan.FromSeconds(5))) == done, "writing to the log waited for the console");
        console.Release();
        Assert.True(log.Drain(TimeSpan.FromSeconds(5)));
        string text = console.ToString();
        Assert.Contains("GET /0  -> 200", text);
        // What did not fit while the console was blocked is counted, not lost silently.
        Assert.Contains("log lines not written: the console could not keep up", text);
    }

    [Fact]
    public void To_a_terminal_request_lines_are_limited_per_second_and_summed_up()
    {
        // The clock stands still: every line falls in one second, and the summary comes with the drain.
        var output = new StringWriter();
        var heldBack = new List<string>();
        var log = new ServeLog(output, rateLimit: true, maxPerSecond: 5, now: () => 0, heldBack: heldBack.Add);
        for (int i = 0; i < 12; i++) log.WriteRequest($"GET /{i}  -> 200  3 ms");
        log.WriteRequest("warning: Set-Cookie for another domain was dropped");
        log.WriteRequest("POST   /orders  -> 502 (upstream refused the connection)");
        log.WriteRequest("GET    /live  -> websocket failed (reset)");
        log.Write("listening on http://127.0.0.1:8443/");
        Assert.True(log.Drain(TimeSpan.FromSeconds(5)));

        var lines = Lines(output);
        Assert.Equal(5, lines.Count(l => l.StartsWith("GET /", StringComparison.Ordinal)));
        Assert.Contains(lines, l => l.StartsWith("warning:", StringComparison.Ordinal));        // always shown
        Assert.Contains(lines, l => l.Contains("-> 502", StringComparison.Ordinal));            // a failure: always shown
        Assert.Contains(lines, l => l.Contains("websocket failed", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("listening on", StringComparison.Ordinal));   // not a request line
        Assert.Contains(lines, l => l.Contains("and 7 more requests in that second", StringComparison.Ordinal));
        // What the console did not show still reaches --log-file.
        Assert.Equal(Enumerable.Range(5, 7).Select(i => $"GET /{i}  -> 200  3 ms"), heldBack);
    }

    [Fact]
    public void Failures_have_a_higher_limit_of_their_own()
    {
        // An upstream that is down fails every request: shown, but not at the full request rate.
        var output = new StringWriter();
        var log = new ServeLog(output, rateLimit: true, maxPerSecond: 5, now: () => 0);
        for (int i = 0; i < 40; i++) log.WriteRequest($"GET /{i}  -> 502 (refused)");
        for (int i = 0; i < 10; i++) log.WriteRequest($"GET /ok{i}  -> 200  1 ms");
        Assert.True(log.Drain(TimeSpan.FromSeconds(5)));

        var lines = Lines(output);
        Assert.Equal(25, lines.Count(l => l.Contains("-> 502", StringComparison.Ordinal)));
        Assert.Equal(5, lines.Count(l => l.Contains("-> 200", StringComparison.Ordinal)));
        Assert.Contains(lines, l => l.Contains("and 20 more requests in that second", StringComparison.Ordinal));
    }

    [Fact]
    public void A_line_the_queue_refuses_still_reaches_the_log_file()
    {
        var console = new BlockedConsole();
        var file = new List<string>();
        var log = new ServeLog(console, rateLimit: false, capacity: 1, heldBack: l => { lock (file) file.Add(l); });
        for (int i = 0; i < 50; i++) log.WriteRequest($"GET /{i}  -> 200");
        lock (file) Assert.NotEmpty(file);   // the console holds one line and the queue one: the rest were refused
        console.Release();
        Assert.True(log.Drain(TimeSpan.FromSeconds(5)));
    }

    [Theory]
    [InlineData("GET    /a  -> 200  4 ms", false)]
    [InlineData("GET    /a  -> 404 (no route)", false)]
    [InlineData("GET    /a  -> 500  4 ms", true)]
    [InlineData("GET    /a  -> 504 (the upstream did not answer in 100 s)", true)]
    [InlineData("GET    /a  -> 5000", false)]
    [InlineData("GET    /a  -> websocket failed (reset)", true)]
    [InlineData("warning: a site has no certificate", true)]
    [InlineData("error: could not bind", true)]
    public void Failures_and_warnings_are_the_lines_always_shown(string line, bool important) =>
        Assert.Equal(important, ServeLog.Important(line));

    [Fact]
    public void To_a_file_or_pipe_every_line_is_written()
    {
        var output = new StringWriter();
        var log = new ServeLog(output, rateLimit: false, maxPerSecond: 5);
        for (int i = 0; i < 200; i++) log.WriteRequest($"GET /{i}  -> 200");
        Assert.True(log.Drain(TimeSpan.FromSeconds(5)));
        Assert.Equal(200, Lines(output).Count(l => l.StartsWith("GET ", StringComparison.Ordinal)));
        Assert.DoesNotContain("more request", output.ToString());
    }

    [Fact]
    public void Lines_keep_their_order()
    {
        var output = new StringWriter();
        var log = new ServeLog(output, rateLimit: false);
        for (int i = 0; i < 1000; i++) log.WriteRequest(i.ToString());
        Assert.True(log.Drain(TimeSpan.FromSeconds(5)));
        Assert.Equal(Enumerable.Range(0, 1000).Select(i => i.ToString()), Lines(output));
    }

    [Fact]
    public void Quick_edit_is_left_alone_off_a_console()
    {
        // Not a console (the test host's stdin is redirected, or this is not Windows): nothing to do.
        using var suspended = ConsoleQuickEdit.SuspendWhile(console: false);
        suspended.Dispose();   // twice is harmless
    }

    [Fact]
    public async Task The_gateway_keeps_answering_while_its_console_is_blocked()
    {
        await using var upstream = MockServer.Start(0, MockTlsMode.Http);
        var console = new BlockedConsole();
        using var cts = new CancellationTokenSource();
        Task<int>? run = null;
        int port = 0;
        for (int attempt = 0; attempt < 5 && run is null; attempt++)
        {
            port = ServeFixture.FreePort();
            int chosen = port;
            var candidate = Task.Run(() => CliApp.Run(
                ["serve", upstream.BaseUrl, "--port", chosen.ToString(), "--no-config"],
                TextWriter.Null, console, services: new CliServices { Cancel = cts.Token, UserConfigPath = null }));
            if (await Task.WhenAny(candidate, Task.Delay(750)) != candidate) run = candidate;   // still running: it bound
        }
        Assert.NotNull(run);
        try
        {
            using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(10) };
            // Every request is answered although no log line has reached the console.
            for (int i = 0; i < 20; i++)
            {
                var response = await ServeFixture.Poll(() => http.GetAsync($"http://127.0.0.1:{port}/echo/{i}"));
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            }
            Assert.Equal(0, console.Writes);
        }
        finally
        {
            console.Release();
            cts.Cancel();
            await run!.WaitAsync(ServeFixture.Limit);
        }
        Assert.Contains("listening on", console.ToString());
        Assert.Contains("/echo/19", console.ToString());
    }
}
