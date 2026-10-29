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
    if (Console.IsInputRedirected)
    {
        try { Console.InputEncoding = new System.Text.UTF8Encoding(false); inputSwitched = true; }
        catch (IOException) { /* no console to configure */ }
    }
    try { Console.OutputEncoding = new System.Text.UTF8Encoding(false); outputSwitched = true; }
    catch (IOException) { /* no console to configure */ }
}
try
{
    return CliApp.Run(args, Console.In, Console.Out, Console.Error, Console.OpenStandardOutput(),
                      new CliServices
                      {
                          Cancel = cts.Token,
                          StdoutIsTerminal = !Console.IsOutputRedirected,
                          StderrIsTerminal = !Console.IsErrorRedirected,
                      });
}
finally
{
    if (OperatingSystem.IsWindows())
    {
        try { if (inputSwitched) Console.InputEncoding = inputBefore; } catch (IOException) { }
        try { if (outputSwitched) Console.OutputEncoding = outputBefore; } catch (IOException) { }
    }
}
