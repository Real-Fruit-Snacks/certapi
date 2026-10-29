using System.IO;
using System.Security.Cryptography.X509Certificates;
using ApiTester.Core;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace ApiTester.App;

/// <summary>Drives a WebView2 for the capture window: every request is fulfilled by an
/// <see cref="MtlsBrowserSession"/> (so the client certificate is presented), and each response is
/// surfaced for token/call observation. Cookies are read from WebView2's own store on demand.</summary>
public sealed class SessionCaptureController : IDisposable
{
    private readonly WebView2 _browser;
    private readonly MtlsBrowserSession _session;
    private readonly CancellationTokenSource _cts = new();
    private bool _ready;

    /// <summary>Raised on the UI thread for each response observed.</summary>
    public event Action<ObservedCall>? CallObserved;

    /// <summary>Raised on the UI thread per response as (body, contentType, responseHeaders, url)
    /// so the window can run token detection.</summary>
    public event Action<byte[], string?, IReadOnlyList<KeyValuePair<string, string>>, string>? ResponseBody;

    /// <summary>Raised on the UI thread when a resource fetch fails, with the error message.</summary>
    public event Action<string>? Failed;

    public SessionCaptureController(WebView2 browser, X509Certificate2? cert, bool ignoreServerCertErrors)
    {
        _browser = browser;
        _session = new MtlsBrowserSession(cert, ignoreServerCertErrors);
    }

    public async Task<bool> InitializeAsync()
    {
        try
        {
            await _browser.EnsureCoreWebView2Async(await AppWebView.EnvironmentAsync());
            var core = _browser.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += OnResourceRequested;
            core.ProcessFailed += OnProcessFailed;
            _ready = true;
            return true;
        }
        catch { return false; }
    }

    /// <summary>Raised on the UI thread when the browser view's process exits or hangs, with what to
    /// tell the user.</summary>
    public event Action<string>? Stopped;

    private void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        if (AppWebView.Describe(e) is not { } message) return;
        if (e.ProcessFailedKind == CoreWebView2ProcessFailedKind.BrowserProcessExited)
        {
            // This control cannot be used again; the next window starts a new browser process.
            _ready = false;
            AppWebView.Forget();
            Stopped?.Invoke(message + " Finish & save keeps what was captured; open the window again to go on.");
        }
        else Stopped?.Invoke(message + " Reload the page to try again.");
    }

    /// <summary>False before the browser has started and after its process has exited.</summary>
    public bool Ready => _ready;

    public void Navigate(string url)
    {
        if (_ready) _browser.CoreWebView2.Navigate(url);
    }

    private void OnResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        var deferral = e.GetDeferral();
        var request = e.Request;
        string method = request.Method;
        string uri = request.Uri;
        var headers = request.Headers.Select(h => new KeyValuePair<string, string>(h.Key, h.Value)).ToList();
        byte[]? body = ReadContent(request.Content);
        // Taken now, while the control is alive: the answer is built from it after the fetch.
        var environment = ((CoreWebView2)sender!).Environment;
        _ = HandleAsync(environment, e, deferral, method, uri, headers, body);
    }

    private async Task HandleAsync(
        CoreWebView2Environment environment,
        CoreWebView2WebResourceRequestedEventArgs e, CoreWebView2Deferral deferral,
        string method, string uri, List<KeyValuePair<string, string>> headers, byte[]? body)
    {
        try
        {
            var result = await _session.FetchAsync(method, new Uri(uri), headers, body, null, _cts.Token);
            var headerLines = string.Join("\r\n", result.Headers
                .Where(h => Forwardable(h.Key)).Select(h => $"{h.Key}: {h.Value}"));
            e.Response = environment.CreateWebResourceResponse(
                new MemoryStream(result.Body), result.StatusCode,
                string.IsNullOrEmpty(result.ReasonPhrase) ? "OK" : result.ReasonPhrase, headerLines);

            var observedHeaders = result.Headers ?? new List<KeyValuePair<string, string>>();
            _browser.Dispatcher.Invoke(() =>
            {
                CallObserved?.Invoke(new ObservedCall(method, uri, result.StatusCode, result.ContentType));
                ResponseBody?.Invoke(result.Body ?? Array.Empty<byte>(), result.ContentType, observedHeaders, uri);
            });
        }
        catch (Exception ex)
        {
            e.Response = environment.CreateWebResourceResponse(
                new MemoryStream(Array.Empty<byte>()), 502, "Bad Gateway", "");
            var msg = ex.Message;
            _browser.Dispatcher.Invoke(() => Failed?.Invoke(msg));
        }
        finally { deferral.Complete(); }
    }

    /// <summary>Read the cookies WebView2 accumulated for an origin (includes HttpOnly session cookies).</summary>
    public async Task<IReadOnlyList<SessionCookie>> ReadCookiesAsync(string origin)
    {
        if (!_ready) return Array.Empty<SessionCookie>();
        var list = await _browser.CoreWebView2.CookieManager.GetCookiesAsync(origin);
        return list.Select(c => new SessionCookie
        {
            Origin = origin,
            Name = c.Name,
            Value = c.Value,
            Path = string.IsNullOrEmpty(c.Path) ? "/" : c.Path,
            Domain = c.Domain,
            Secure = c.IsSecure,
            HttpOnly = c.IsHttpOnly,
            ExpiresUtc = c.IsSession ? null : c.Expires.ToUniversalTime()
        }).ToList();
    }

    // Mirrors MainWindow.Forwardable — hop-by-hop / content-coding headers are not forwarded.
    private static bool Forwardable(string name) =>
        !name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) &&
        !name.Equals("Content-Encoding", StringComparison.OrdinalIgnoreCase) &&
        !name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase) &&
        !name.Equals("Connection", StringComparison.OrdinalIgnoreCase);

    private static byte[]? ReadContent(Stream? content)
    {
        if (content is null) return null;
        try { using var ms = new MemoryStream(); content.CopyTo(ms); return ms.Length == 0 ? null : ms.ToArray(); }
        catch { return null; }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
        _session.Dispose();
    }
}
