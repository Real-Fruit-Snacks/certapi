using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace ApiTester.Core;

/// <summary>The result of fetching one resource for the rendered browser view.</summary>
public sealed class FetchResult
{
    public int StatusCode { get; set; }
    public string? ReasonPhrase { get; set; }
    public string? ContentType { get; set; }
    public byte[] Body { get; set; } = Array.Empty<byte>();
    public double ElapsedMs { get; set; }
    public List<KeyValuePair<string, string>> Headers { get; set; } = new();
}

/// <summary>A long-lived HTTP client carrying the selected client certificate, used to fetch
/// every resource a rendered page requests (document, CSS, JS, images, XHR) so the whole page
/// authenticates with mutual TLS. Configured like <see cref="ApiClient"/> (system proxy +
/// client certificate), with automatic decompression for the browser.</summary>
public sealed class MtlsBrowserSession : IDisposable
{
    private readonly HttpClient _http;

    // Request headers WebView2 supplies that HttpClient must manage itself.
    private static readonly HashSet<string> SkipRequestHeaders =
        new(StringComparer.OrdinalIgnoreCase) { "Host", "Connection", "Content-Length" };

    public MtlsBrowserSession(X509Certificate2? clientCertificate, bool ignoreServerCertificateErrors)
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            DefaultProxyCredentials = CredentialCache.DefaultCredentials,
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, _, _, errors) =>
                    errors == SslPolicyErrors.None || ignoreServerCertificateErrors
            }
        };
        if (clientCertificate is not null)
            handler.SslOptions.ClientCertificates = new X509CertificateCollection { clientCertificate };

        _http = new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(100) };
    }

    public async Task<FetchResult> FetchAsync(
        string method, Uri uri,
        IEnumerable<KeyValuePair<string, string>> headers,
        byte[]? body, string? contentType,
        CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(new HttpMethod(method), uri);

        string? headerContentType = contentType;
        var kept = new List<KeyValuePair<string, string>>();
        foreach (var h in headers)
        {
            if (h.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)) { headerContentType ??= h.Value; continue; }
            if (SkipRequestHeaders.Contains(h.Key)) continue;
            kept.Add(h);
        }
        var bodyHeaders = ContentHeaders.AddRequestHeaders(message, kept);

        if (body is { Length: > 0 })
        {
            message.Content = new ByteArrayContent(body);
            if (!string.IsNullOrEmpty(headerContentType) &&
                MediaTypeHeaderValue.TryParse(headerContentType, out var mt))
                message.Content.Headers.ContentType = mt;
            ContentHeaders.ApplyTo(message.Content, bodyHeaders, keepContentType: true);
        }

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        using var response = await _http.SendAsync(message, HttpCompletionOption.ResponseContentRead, cancellationToken);
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        stopwatch.Stop();

        var result = new FetchResult
        {
            StatusCode = (int)response.StatusCode,
            ReasonPhrase = response.ReasonPhrase,
            ContentType = response.Content.Headers.ContentType?.ToString(),
            Body = bytes,
            ElapsedMs = stopwatch.Elapsed.TotalMilliseconds
        };
        foreach (var h in response.Headers)
            foreach (var v in h.Value) result.Headers.Add(new(h.Key, v));
        foreach (var h in response.Content.Headers)
            foreach (var v in h.Value) result.Headers.Add(new(h.Key, v));
        return result;
    }

    public void Dispose() => _http.Dispose();
}
