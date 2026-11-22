using ApiTester.Cli;

using var cts = new CancellationTokenSource();
// Ctrl+C cancels the work in hand, so nothing is cut off mid-write (serve's recording, a cookie
// jar). A command blocked reading stdin (mcp, fuzz -w -) cannot notice that, so for those alone a
// second Ctrl+C ends the process.
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = !(cts.IsCancellationRequested && CliApp.SecondCtrlCQuits);
    cts.Cancel();
};

// UTF-8 on Windows, whose console otherwise reads and writes the OEM code page: an MCP client's
// {"name":"Zoë"} arrived as "Zo├½", and `send --pretty > out.json` wrote cp437 bytes. Input is
// switched only when it is piped: typed input through the UTF-8 code page turns non-ASCII keys
// into NULs on older Windows 10 consoles. The code pages belong to the console window, not to
// this process, so they are put back after.
var (inputBefore, outputBefore) = (Console.InputEncoding, Console.OutputEncoding);
bool inputSwitched = false, outputSwitched = false;
if (OperatingSystem.IsWindows())
{
    var utf8 = new System.Text.UTF8Encoding(false);
    if (Console.IsInputRedirected && (inputBefore.CodePage != 65001 || inputBefore.GetPreamble().Length != 0))
    {
        try { Console.InputEncoding = utf8; inputSwitched = true; }
        catch (IOException) { /* no console to configure */ }
    }
    if (outputBefore.CodePage != 65001 || outputBefore.GetPreamble().Length != 0)
    {
        try { Console.OutputEncoding = utf8; outputSwitched = true; }
        catch (IOException) { /* no console to configure */ }
    }
}
// Console.Out writes through a 256-byte buffer and flushes after every write, and on Windows each
// flush is a system call: a 60 MB formatted body (--pretty, --json) going to a file or a pipe took
// over ten seconds, against one second for the same bytes as a raw body. Redirected output gets a
// 64 KB buffer instead — still flushed after every write, so streamed output (sse, ws, a server's
// log, an MCP reply) appears as it happens. A console keeps the console's own writer.
TextWriter stdout = Console.Out;
if (Console.IsOutputRedirected)
    stdout = TextWriter.Synchronized(new StreamWriter(Console.OpenStandardOutput(), Console.Out.Encoding, bufferSize: 64 * 1024)
    {
        AutoFlush = true
    });
// The same for stderr, which carries --trace-verbose and --debug's full output when redirected.
TextWriter stderr = Console.Error;
if (Console.IsErrorRedirected)
    stderr = TextWriter.Synchronized(new StreamWriter(Console.OpenStandardError(), Console.Error.Encoding, bufferSize: 64 * 1024)
    {
        AutoFlush = true
    });
try
{
    return CliApp.Run(args, Console.In, stdout, stderr, Console.OpenStandardOutput(),
                      new CliServices
                      {
                          Cancel = cts.Token,
                          StdoutIsTerminal = !Console.IsOutputRedirected,
                          StderrIsTerminal = !Console.IsErrorRedirected,
                      });
}
finally
{
    try { stdout.Flush(); } catch (IOException) { /* the reader went away */ }
    try { stderr.Flush(); } catch (IOException) { /* the reader went away */ }
    if (OperatingSystem.IsWindows())
    {
        try { if (inputSwitched) Console.InputEncoding = inputBefore; } catch (IOException) { }
        try { if (outputSwitched) Console.OutputEncoding = outputBefore; } catch (IOException) { }
    }
}
