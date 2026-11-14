using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using ApiTester.Core;
using Microsoft.Web.WebView2.Core;

namespace ApiTester.Cli.Commands;

public static class BrowseCommand
{
    public const string Help = """
        Usage: certapi browse <url> [options]

        Loads a page in a real browser engine (Microsoft Edge WebView2) with every request — the
        document, scripts, styles, images, fetch/XHR — sent by certapi with the client certificate,
        then saves what the page rendered. For a page or single-page app that only works with a
        client certificate, where send shows only the HTML shell.

        Output (any combination; with none, a summary of the load is printed):
          --screenshot <file>       Save a PNG (or .jpg) of the viewport
          --full-page               With --screenshot, capture the whole page, not just the viewport
          --pdf <file>              Print the page to PDF
          --landscape               With --pdf, landscape pages
          --html <file|->           Save the rendered DOM (after scripts ran); - for stdout
          --script <js>             Run JavaScript after the page settles and print its result as
                                    JSON (@file reads the script from a file)

        Waiting:
          --wait <ms>               Extra time after the page loads, for late scripts (default 500)
          --wait-for <selector>     Wait until this CSS selector matches an element
          --timeout <seconds>       Give up after this long (default 30)

        Page:
          --width <px>              Viewport width (default 1280)
          --height <px>             Viewport height (default 800)
          --user-agent <ua>         Replace the browser's user agent
          -H, --header "k: v"       Add a header to requests to the page's own origin (repeatable;
                                    not sent to third-party origins)
          --bearer <token>          Shorthand for -H "Authorization: Bearer <token>" (own origin only)
          --show                    Show the browser window instead of rendering off-screen
          --profile <dir>           Keep cookies and storage in this folder between runs (default:
                                    a fresh, temporary profile every run)
          -v, --requests            List each request the page made on stderr
          -q, --quiet               No progress notices on stderr

        TLS / certificates:
        """ + "\n" + CliCert.HelpLines + "\n" + """
          --insecure                Ignore server certificate errors

        """ + "\n" + TransportFlags.StreamHelp + "\n" + """

        Needs the WebView2 Runtime, which Windows 10 and 11 include. Exit 0 when the page loaded
        with a status below 400, 1 otherwise.

        Global: --debug (verbose diagnostics) and --log-file <path> work here too.

        Examples:
          certapi browse https://portal.example.com/dashboard --cert "CN=My Client" --screenshot dash.png --full-page
          certapi browse https://portal.example.com/report --cert-file me.pfx --wait-for "#chart svg" --pdf report.pdf
          certapi browse https://app.example.com --cert "CN=My Client" --script "document.title"
        """;

    public static int Run(Args args, TextReader input, TextWriter stdout, TextWriter stderr, CliServices services)
    {
        string? screenshot = args.Value("--screenshot");
        bool fullPage = args.Flag("--full-page");
        string? pdf = args.Value("--pdf");
        bool landscape = args.Flag("--landscape");
        string? html = args.Value("--html");
        string? script = args.Value("--script");
        int waitMs = NonNegative(args.Value("--wait"), "--wait") ?? 500;
        string? waitFor = args.Value("--wait-for");
        int timeout = NonNegative(args.Value("--timeout"), "--timeout") is { } t and > 0 ? t : 30;
        int width = NonNegative(args.Value("--width"), "--width") ?? 1280;
        int height = NonNegative(args.Value("--height"), "--height") ?? 800;
        string? userAgent = args.Value("--user-agent");
        var headers = args.Values("-H", "--header");
        string? bearer = args.Value("--bearer");
        bool show = args.Flag("--show");
        string? profile = args.Value("--profile");
        bool listRequests = args.Flag("-v", "--requests");
        bool quiet = args.Flag("-q", "--quiet");
        string store = args.Value("--store") ?? services.Profile?.Store ?? "CurrentUser";
        bool insecure = args.Switch("--insecure", services.Profile?.Insecure);
        var transport = TransportFlags.ParseStreamSubset(args, insecure, environment: null, services.Profile);
        var cert = CliCert.Resolve(args, store, services, stderr);

        var positionals = args.Positionals();
        if (positionals.Count != 1) throw new CliUsageException(Help);
        if (!Uri.TryCreate(positionals[0], UriKind.Absolute, out var start) || start.Scheme is not ("http" or "https"))
            throw new CliUsageException($"browse needs an http:// or https:// URL, got '{positionals[0]}'.");
        if (width is < 100 or > 8000 || height is < 100 or > 8000)
            throw new CliUsageException("--width and --height must be between 100 and 8000 pixels.");
        if (fullPage && screenshot is null) throw new CliUsageException("--full-page needs --screenshot <file>.");
        if (landscape && pdf is null) throw new CliUsageException("--landscape needs --pdf <file>.");
        if (script is not null && script.StartsWith('@')) script = File.ReadAllText(script[1..]);
        if (!OperatingSystem.IsWindows())
        {
            stderr.WriteLine("error: certapi browse needs Windows (it renders with Microsoft Edge WebView2).");
            return ExitCodes.Failure;
        }

        var ownHeaders = new List<KeyValuePair<string, string>>();
        foreach (var raw in headers)
        {
            int colon = raw.IndexOf(':');
            if (colon <= 0) throw new CliUsageException($"Header must be \"Name: value\", got '{raw}'.");
            ownHeaders.Add(new(raw[..colon].Trim(), raw[(colon + 1)..].Trim()));
        }
        if (bearer is not null) ownHeaders.Add(new("Authorization", "Bearer " + bearer));

        var state = CliWorkspace.Load(null, services.LiveStatePath, stderr);
        var predicates = new TrustPredicates(state);

        var handler = StreamTransport.CreateHandler(cert, transport, predicates.For(start.Host), start);
        handler.AutomaticDecompression = DecompressionMethods.All;
        // The browser keeps its own cookie jar and sends it on every request; a second jar here
        // would send each cookie twice.
        handler.UseCookies = false;
        handler.AllowAutoRedirect = false;
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(Math.Max(timeout, 30)) };

        string userData = profile ?? Path.Combine(Path.GetTempPath(), "certapi-browse-" + Guid.NewGuid().ToString("N")[..8]);
        int requests = 0, failures = 0;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(services.Cancel);
        cts.CancelAfter(TimeSpan.FromSeconds(timeout));

        try
        {
            return Win32Host.Run(width, height, show, "certapi browse — " + start.Host, host => RunAsync(host));
        }
        catch (WebView2RuntimeNotFoundException)
        {
            stderr.WriteLine("error: the Microsoft Edge WebView2 Runtime is not installed. Get it from https://go.microsoft.com/fwlink/p/?LinkId=2124703");
            return ExitCodes.Failure;
        }
        finally
        {
            if (profile is null) TryDelete(userData);
        }

        async Task<int> RunAsync(Win32Host host)
        {
            var watch = Stopwatch.StartNew();
            var environment = await CoreWebView2Environment.CreateAsync(null, userData,
                new CoreWebView2EnvironmentOptions(additionalBrowserArguments: "--disable-gpu"));
            var controller = await environment.CreateCoreWebView2ControllerAsync(host.Window);
            try
            {
                controller.ShouldDetectMonitorScaleChanges = false;
                controller.RasterizationScale = 1.0;
                controller.Bounds = new System.Drawing.Rectangle(0, 0, width, height);
                controller.IsVisible = true;
                var core = controller.CoreWebView2;
                core.Settings.AreDefaultScriptDialogsEnabled = false;
                core.Settings.IsStatusBarEnabled = false;
                if (userAgent is not null) core.Settings.UserAgent = userAgent;
                core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
                core.WebResourceRequested += (sender, e) => _ = FulfilAsync(environment, e);

                var loaded = new TaskCompletionSource<CoreWebView2NavigationCompletedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
                core.NavigationCompleted += (_, e) => loaded.TrySetResult(e);
                using var registration = cts.Token.Register(() => loaded.TrySetCanceled());

                if (!quiet) stderr.WriteLine($"loading {start} …");
                core.Navigate(start.ToString());
                CoreWebView2NavigationCompletedEventArgs done;
                try { done = await loaded.Task; }
                catch (TaskCanceledException)
                {
                    stderr.WriteLine($"error: the page did not finish loading within {timeout} s.");
                    return ExitCodes.Failure;
                }

                int status = done.HttpStatusCode;
                bool ok = done.IsSuccess && (status == 0 || status < 400);
                if (!done.IsSuccess)
                    stderr.WriteLine($"error: navigation failed: {done.WebErrorStatus}");

                if (waitFor is not null)
                {
                    string probe = $"!!document.querySelector({JsonSerializer.Serialize(waitFor)})";
                    while (await core.ExecuteScriptAsync(probe) != "true")
                    {
                        if (cts.IsCancellationRequested)
                        {
                            stderr.WriteLine($"error: nothing matched {waitFor} within {timeout} s.");
                            return ExitCodes.Failure;
                        }
                        await Task.Delay(100);
                    }
                }
                if (waitMs > 0) await Task.Delay(waitMs);

                string title = core.DocumentTitle;
                string final = core.Source;

                if (screenshot is not null)
                {
                    await SaveScreenshotAsync(core, screenshot, fullPage);
                    if (!quiet) stderr.WriteLine($"saved {screenshot}");
                }
                if (pdf is not null)
                {
                    var settings = environment.CreatePrintSettings();
                    settings.ShouldPrintBackgrounds = true;
                    settings.Orientation = landscape ? CoreWebView2PrintOrientation.Landscape : CoreWebView2PrintOrientation.Portrait;
                    if (!await core.PrintToPdfAsync(Path.GetFullPath(pdf), settings))
                    {
                        stderr.WriteLine($"error: could not print the page to {pdf}");
                        ok = false;
                    }
                    else if (!quiet) stderr.WriteLine($"saved {pdf}");
                }
                if (html is not null)
                {
                    string encoded = await core.ExecuteScriptAsync("document.documentElement.outerHTML");
                    string dom = "<!DOCTYPE html>\n" + (JsonSerializer.Deserialize<string>(encoded) ?? "");
                    if (html == "-") stdout.Write(dom);
                    else
                    {
                        await File.WriteAllTextAsync(html, dom);
                        if (!quiet) stderr.WriteLine($"saved {html}");
                    }
                }
                if (script is not null)
                {
                    string result = await core.ExecuteScriptAsync(script);
                    stdout.WriteLine(result);
                }
                if (screenshot is null && pdf is null && html is null && script is null)
                {
                    stdout.WriteLine($"status   {(status == 0 ? "(not reported)" : status.ToString(System.Globalization.CultureInfo.InvariantCulture))}");
                    stdout.WriteLine($"url      {final}");
                    stdout.WriteLine($"title    {title}");
                    stdout.WriteLine($"requests {requests}{(failures > 0 ? $" ({failures} failed)" : "")}");
                    stdout.WriteLine($"time     {watch.Elapsed.TotalSeconds:F1} s");
                }
                else if (!quiet)
                    stderr.WriteLine($"{(status == 0 ? "" : status + " · ")}{requests} request{(requests == 1 ? "" : "s")}{(failures > 0 ? $" ({failures} failed)" : "")} · {watch.Elapsed.TotalSeconds:F1} s");
                return ok ? ExitCodes.Ok : ExitCodes.Failure;
            }
            finally
            {
                controller.Close();
            }
        }

        async Task FulfilAsync(CoreWebView2Environment environment, CoreWebView2WebResourceRequestedEventArgs e)
        {
            var deferral = e.GetDeferral();
            var request = e.Request;
            string method = request.Method;
            var uri = new Uri(request.Uri);
            try
            {
                // Only the web: data:, blob: and the browser's own schemes stay with the browser.
                if (uri.Scheme is not ("http" or "https")) return;
                using var message = new HttpRequestMessage(new HttpMethod(method), uri);
                byte[]? body = null;
                if (request.Content is { } content)
                {
                    using var ms = new MemoryStream();
                    await content.CopyToAsync(ms);
                    if (ms.Length > 0) body = ms.ToArray();
                }
                var contentHeaders = new List<KeyValuePair<string, string>>();
                foreach (var h in request.Headers)
                {
                    if (h.Key is "Host" or "Connection" or "Content-Length") continue;
                    if (h.Key.StartsWith("Content-", StringComparison.OrdinalIgnoreCase)) { contentHeaders.Add(new(h.Key, h.Value)); continue; }
                    message.Headers.TryAddWithoutValidation(h.Key, h.Value);
                }
                if (SameOrigin(uri, start))
                    foreach (var h in ownHeaders)
                    {
                        message.Headers.Remove(h.Key);
                        message.Headers.TryAddWithoutValidation(h.Key, h.Value);
                    }
                if (body is not null)
                {
                    message.Content = new ByteArrayContent(body);
                    foreach (var h in contentHeaders) message.Content.Headers.TryAddWithoutValidation(h.Key, h.Value);
                }

                using var response = await http.SendAsync(message, cts.Token);
                byte[] bytes = await response.Content.ReadAsByteArrayAsync(cts.Token);
                requests++;
                if ((int)response.StatusCode >= 400) failures++;
                if (listRequests) stderr.WriteLine($"{(int)response.StatusCode} {method} {uri}");

                var lines = new List<string>();
                foreach (var h in response.Headers.Concat(response.Content.Headers))
                {
                    if (h.Key is "Content-Length" or "Content-Encoding" or "Transfer-Encoding" or "Connection") continue;
                    foreach (var v in h.Value) lines.Add($"{h.Key}: {v}");
                }
                e.Response = environment.CreateWebResourceResponse(new MemoryStream(bytes), (int)response.StatusCode,
                    string.IsNullOrEmpty(response.ReasonPhrase) ? response.StatusCode.ToString() : response.ReasonPhrase,
                    string.Join("\r\n", lines));
            }
            catch (Exception ex)
            {
                requests++;
                failures++;
                if (listRequests || !quiet) stderr.WriteLine($"failed {method} {uri}: {ex.Message}");
                e.Response = environment.CreateWebResourceResponse(new MemoryStream(), 502, "Bad Gateway", "");
            }
            finally { deferral.Complete(); }
        }
    }

    private static async Task SaveScreenshotAsync(CoreWebView2 core, string path, bool fullPage)
    {
        bool jpeg = path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase);
        if (!fullPage)
        {
            await using var file = File.Create(path);
            await core.CapturePreviewAsync(jpeg ? CoreWebView2CapturePreviewImageFormat.Jpeg : CoreWebView2CapturePreviewImageFormat.Png, file);
            return;
        }
        // The whole page: ask the engine for the content size, then capture beyond the viewport.
        using var metrics = JsonDocument.Parse(await core.CallDevToolsProtocolMethodAsync("Page.getLayoutMetrics", "{}"));
        var size = metrics.RootElement.TryGetProperty("cssContentSize", out var css) ? css : metrics.RootElement.GetProperty("contentSize");
        double w = size.GetProperty("width").GetDouble(), h = size.GetProperty("height").GetDouble();
        string request = JsonSerializer.Serialize(new
        {
            format = jpeg ? "jpeg" : "png",
            captureBeyondViewport = true,
            clip = new { x = 0, y = 0, width = w, height = h, scale = 1 }
        });
        using var shot = JsonDocument.Parse(await core.CallDevToolsProtocolMethodAsync("Page.captureScreenshot", request));
        await File.WriteAllBytesAsync(path, Convert.FromBase64String(shot.RootElement.GetProperty("data").GetString()!));
    }

    internal static bool SameOrigin(Uri a, Uri b) =>
        a.Scheme.Equals(b.Scheme, StringComparison.OrdinalIgnoreCase) &&
        a.Host.Equals(b.Host, StringComparison.OrdinalIgnoreCase) && a.Port == b.Port;

    private static void TryDelete(string folder)
    {
        for (int attempt = 0; attempt < 10 && Directory.Exists(folder); attempt++)
        {
            try { Directory.Delete(folder, recursive: true); }
            catch (IOException) { Thread.Sleep(200); }
            catch (UnauthorizedAccessException) { Thread.Sleep(200); }
        }
    }

    private static int? NonNegative(string? raw, string flag)
    {
        if (raw is null) return null;
        if (!int.TryParse(raw, out var n) || n < 0)
            throw new CliUsageException($"{flag} expects a non-negative number, got '{raw}'.");
        return n;
    }
}
