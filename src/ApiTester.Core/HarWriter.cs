using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ApiTester.Core;

/// <summary>Builds HAR entries from a completed request/response exchange, and serializes a HAR
/// document. Pure — no I/O, no sockets.</summary>
public static class HarWriter
{
    private static JsonSerializerOptions? _options;

    /// <summary>The shared <see cref="JsonSerializerOptions"/> used for both writing and reading HAR
    /// documents: camelCase properties, fields included (for <see cref="HarPostData"/> and
    /// <see cref="HarTimings"/>), and nulls omitted.</summary>
    internal static JsonSerializerOptions Options => _options ??= new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        IncludeFields = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>Is this header name one of the sensitive ones redacted when
    /// <c>includeSecrets</c> is false (see <see cref="SecretHeaders.IsSecret"/>).</summary>
    public static bool IsSecretHeader(string name) => SecretHeaders.IsSecret(name);

    /// <summary>Redact the values of secret headers (see <see cref="IsSecretHeader"/>) to
    /// <c>[redacted]</c>, keeping the header name — or return the headers unchanged when
    /// <paramref name="includeSecrets"/> is true.</summary>
    public static List<HarNameValue> Redact(IEnumerable<HarNameValue> headers, bool includeSecrets)
        => headers.Select(h => includeSecrets || !IsSecretHeader(h.Name)
                ? h
                : h with { Value = "[redacted]" })
            .ToList();

    /// <summary>Build one HAR entry for the final response of an exchange (no redirect hops).</summary>
    public static HarEntry FromExchange(ApiRequest request, ApiResponse response, bool includeSecrets)
    {
        double timeMs = response.Elapsed.TotalMilliseconds;

        var requestHeaders = Redact(request.Headers.Select(h => new HarNameValue(h.Key, h.Value)), includeSecrets);

        // The URL is redacted for the same reason the headers beside it are, and it was not:
        // `?api_key=…` and `https://svc:pw@host/…` are credentials that happen to look like part
        // of an address. The query array is derived from the REDACTED url rather than the original,
        // so the two halves of the archive can never disagree about what the request was.
        string recordedUrl = RecordedUrl(request.Url, includeSecrets);
        var (_, rawQuery) = QueryString.Split(recordedUrl);
        var queryString = QueryString.Parse(rawQuery).Select(q => new HarNameValue(q.Key, q.Value)).ToList();

        HarPostData? postData = null;
        if (request.BodyBytes is not null)
            postData = new HarPostData
            {
                MimeType = request.ContentType ?? "application/octet-stream",
                Text = Convert.ToBase64String(request.BodyBytes),
                Encoding = "base64",
            };
        else if (request.Body is not null)
        {
            string text = includeSecrets ? request.Body : RedactJsonBody(request.Body);
            postData = new HarPostData { MimeType = request.ContentType ?? "", Text = text };
        }

        var responseHeaders = Redact(response.Headers.Select(h => new HarNameValue(h.Key, h.Value)), includeSecrets);

        string redirectUrl = "";
        if (response.StatusCode is >= 300 and < 400)
        {
            var location = response.Headers.FirstOrDefault(h => string.Equals(h.Key, "Location", StringComparison.OrdinalIgnoreCase));
            redirectUrl = RecordedUrl(location.Value ?? "", includeSecrets);
        }

        var content = BuildContent(response.Body, response.ContentType);

        HarCertapi? certapi = response.Connection is { } conn
            ? new HarCertapi
            {
                ClientCertificateSent = conn.ClientCertificateSent,
                ClientCertificateSubject = conn.ClientCertificateSubject,
                TlsProtocol = conn.TlsProtocol,
                ServerCertificateThumbprint = conn.ServerCertificateThumbprint,
                ViaProxy = conn.ViaProxy
            }
            : null;

        return new HarEntry
        {
            StartedDateTime = DateTimeOffset.UtcNow,
            Time = timeMs,
            Request = new HarRequest
            {
                Method = request.Method.Method,
                Url = recordedUrl,
                HttpVersion = "HTTP/1.1",
                Headers = requestHeaders,
                QueryString = queryString,
                PostData = postData
            },
            Response = new HarResponse
            {
                Status = response.StatusCode ?? 0,
                StatusText = response.ReasonPhrase ?? "",
                HttpVersion = "HTTP/1.1",
                Headers = responseHeaders,
                Content = content,
                RedirectUrl = redirectUrl
            },
            Timings = new HarTimings { Wait = timeMs, Send = -1, Receive = -1 },
            Certapi = certapi
        };
    }

    /// <summary>Build one HAR entry per redirect hop (oldest first) followed by the final entry for
    /// the resolved response — the shape of one command that "performs" a request through however
    /// many redirects it took.</summary>
    public static IReadOnlyList<HarEntry> FromExchangeWithRedirects(ApiRequest request, ApiResponse response, bool includeSecrets)
    {
        var entries = new List<HarEntry>();
        var requestHeaders = Redact(request.Headers.Select(h => new HarNameValue(h.Key, h.Value)), includeSecrets);

        foreach (var hop in response.Redirects)
        {
            string hopUrl = RecordedUrl(hop.From, includeSecrets);
            var (_, rawQuery) = QueryString.Split(hopUrl);
            var queryString = QueryString.Parse(rawQuery).Select(q => new HarNameValue(q.Key, q.Value)).ToList();

            // A hop's own duration, not zero: every HAR viewer renders `time` as a bar, and a zero
            // draws a redirect that took real time as instant — the one place a reader looks to
            // find out that a chain of hops, not the destination, is what made a request slow.
            double hopMs = hop.Elapsed.TotalMilliseconds;
            entries.Add(new HarEntry
            {
                StartedDateTime = DateTimeOffset.UtcNow,
                Time = hopMs,
                Request = new HarRequest
                {
                    Method = request.Method.Method,
                    Url = hopUrl,
                    HttpVersion = "HTTP/1.1",
                    Headers = requestHeaders,
                    QueryString = queryString,
                    PostData = null
                },
                Response = new HarResponse
                {
                    Status = hop.StatusCode,
                    StatusText = "",
                    HttpVersion = "HTTP/1.1",
                    Headers = new List<HarNameValue>(),
                    Content = new HarContent { Size = 0 },
                    RedirectUrl = RecordedUrl(hop.To, includeSecrets)
                },
                // -1 is HAR's own "not applicable", which is the truth for send/receive here; wait
                // carries the measured time. An unmeasured hop (Elapsed default) still reports -1
                // rather than claiming zero.
                Timings = new HarTimings { Wait = hopMs > 0 ? hopMs : -1, Send = -1, Receive = -1 },
                Certapi = null
            });
        }

        var finalEntry = FromExchange(request, response, includeSecrets);
        // Redacted here as well as inside FromExchange: this deliberately overwrites what that
        // built, so leaving it raw would undo the redaction a line after applying it.
        string finalUrl = RecordedUrl(
            response.Redirects.Count > 0 ? response.Redirects[^1].To : request.Url, includeSecrets);
        finalEntry.Request.Url = finalUrl;
        var (_, finalRawQuery) = QueryString.Split(finalUrl);
        finalEntry.Request.QueryString = QueryString.Parse(finalRawQuery).Select(q => new HarNameValue(q.Key, q.Value)).ToList();
        entries.Add(finalEntry);

        return entries;
    }

    /// <summary>Serialize entries into a pretty-printed HAR document.</summary>
    public static string Write(IEnumerable<HarEntry> entries, string creatorVersion)
    {
        var har = new Har
        {
            Log = new HarLog
            {
                Creator = new HarCreator("certapi", creatorVersion),
                Entries = entries.ToList()
            }
        };
        return JsonSerializer.Serialize(har, Options);
    }

    private static HarContent BuildContent(byte[] body, string? contentType)
    {
        if (body.Length == 0) return new HarContent { Size = 0, MimeType = contentType ?? "", Text = "", Encoding = null };

        string text = Encoding.UTF8.GetString(body);
        bool binary = text.Contains('�');
        return binary
            ? new HarContent { Size = body.LongLength, MimeType = contentType ?? "", Text = Convert.ToBase64String(body), Encoding = "base64" }
            : new HarContent { Size = body.LongLength, MimeType = contentType ?? "", Text = text, Encoding = null };
    }

    /// <summary>Drop top-level <c>access_token</c>/<c>password</c> properties from a JSON object
    /// body before it is written into a redacted HAR. Never throws — a body that isn't a JSON
    /// object is left as-is.</summary>
    /// <summary>A URL as it should be written into an archive: credentials in the query string and
    /// in a <c>user:password@host</c> prefix masked, unless the caller asked to keep them.
    /// <para>Public because three different places build archives — a send, the gateway's recorder,
    /// and the desktop application's network export — and a redaction rule that only one of them
    /// applied is the shape of every leak found so far.</para>
    /// <para>One consequence, stated because it is a real trade: an archive replayed with
    /// <c>mock --har</c> or <c>serve --replay</c> matches on the recorded URL, so a request that is
    /// distinguished <em>only</em> by a secret query value can no longer be told apart from its
    /// sibling. That is what <c>--har-include-secrets</c> is for; the default stays "safe to hand
    /// to someone else", which is what an archive is usually for.</para></summary>
    public static string RecordedUrl(string url, bool includeSecrets) =>
        includeSecrets ? url : MarkdownSecrets.RedactUrl(url);

    private static string RedactJsonBody(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return body;

            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    if (string.Equals(prop.Name, "access_token", StringComparison.OrdinalIgnoreCase)) continue;
                    if (string.Equals(prop.Name, "password", StringComparison.OrdinalIgnoreCase)) continue;
                    prop.WriteTo(writer);
                }
                writer.WriteEndObject();
            }
            return Encoding.UTF8.GetString(stream.ToArray());
        }
        catch (JsonException)
        {
            return body;
        }
    }
}
