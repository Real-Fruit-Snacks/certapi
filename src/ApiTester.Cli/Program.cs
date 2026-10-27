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

return CliApp.Run(args, Console.In, Console.Out, Console.Error, Console.OpenStandardOutput(),
                  new CliServices { Cancel = cts.Token });
