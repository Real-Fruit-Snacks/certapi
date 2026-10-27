using ApiTester.Cli;

using var cts = new CancellationTokenSource();
// The first Ctrl+C cancels the work in hand, so nothing is cut off mid-write; a second one ends the
// process, so a command blocked reading stdin (mcp, fuzz -w -) can always be stopped.
Console.CancelKeyPress += (_, e) => { e.Cancel = !cts.IsCancellationRequested; cts.Cancel(); };

return CliApp.Run(args, Console.In, Console.Out, Console.Error, Console.OpenStandardOutput(),
                  new CliServices { Cancel = cts.Token });
