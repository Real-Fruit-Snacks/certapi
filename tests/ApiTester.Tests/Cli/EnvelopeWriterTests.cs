using System.Text;
using System.Text.Json;
using ApiTester.Cli.Commands;
using ApiTester.Core;

namespace ApiTester.Tests.Cli;

/// <summary>`send --json` writes its envelope field by field now, instead of serializing
/// dictionaries and anonymous objects by reflection (25 ms of every run). The text must be exactly
/// what the serializer wrote: these hold the two side by side on every shape the envelope takes.</summary>
public class EnvelopeWriterTests
{
    /// <summary>The envelope as it was built before, kept here as the reference.</summary>
    private static class Reference
    {
        public static string Build(ApiResponse r, bool includeBody, IReadOnlyList<string>? notes = null,
                                             DiffResult? diff = null, PhaseTimings? timing = null)
        {
            bool binary = false;
            string? text = null;
            if (includeBody && r.Error is null)
            {
                // UTF8.GetString never throws — invalid bytes decode to U+FFFD, which marks the body binary.
                text = Encoding.UTF8.GetString(r.Body);
                binary = text.Contains('�');
            }
            var obj = new Dictionary<string, object?>
            {
                ["status"] = r.StatusCode,
                ["reasonPhrase"] = r.ReasonPhrase,
                ["elapsedMs"] = Math.Round(r.Elapsed.TotalMilliseconds),
                ["contentType"] = r.ContentType,
                ["sizeBytes"] = r.Body.LongLength,
                ["headers"] = r.Headers
                    .GroupBy(h => h.Key, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(
                        g => g.Key,
                        g => g.Count() == 1 ? (object)g.First().Value : g.Select(h => h.Value).ToArray()),
                ["tlsProtocol"] = r.Connection?.TlsProtocol,
                ["clientCertPresented"] = r.Connection?.ClientCertificateSent ?? false,
                // Enum .ToString() here matches how error.kind is rendered above, so a consumer parses
                // one consistent style rather than two.
                ["revocationMode"] = r.Connection?.RevocationMode.ToString(),
                ["revocationStatus"] = r.Connection?.RevocationStatus.ToString(),
                // kind/message keep the shape existing consumers already parse; socketError and the
                // flattened InnerException chain are additive, and null when the failure had neither.
                ["error"] = r.Error is null ? null : new
                {
                    kind = r.Error.Kind.ToString(),
                    message = r.Error.Message,
                    socketError = r.Error.SocketErrorCode?.ToString(),
                    detail = r.Error.Detail?.Select(d => new
                    {
                        exceptionType = d.ExceptionType,
                        message = d.Message,
                        nativeErrorCode = d.NativeErrorCode
                    })
                }
            };
            // Only when something was actually followed: adding "redirects": [] to every envelope would
            // churn the output of every existing user for nothing.
            if (r.Redirects.Count > 0)
                obj["redirects"] = r.Redirects.Select(h => new
                {
                    status = h.StatusCode,
                    from = h.From,
                    to = h.To,
                    authorizationDropped = h.AuthorizationDropped,
                    schemeDowngrade = h.SchemeDowngrade
                }).ToList();
            // Only when a retry actually happened, for the same reason redirects is conditional: an
            // "attempts": 1 on every envelope would churn every existing consumer's output for nothing.
            if (r.Attempts > 1) obj["attempts"] = r.Attempts;
            // Only with --timing: milliseconds per phase, null for a phase that did not happen.
            if (timing is not null)
            {
                static double? Ms(TimeSpan? t) => t is { } v ? Math.Round(v.TotalMilliseconds, 1) : null;
                obj["timing"] = new
                {
                    dnsMs = Ms(timing.Dns), connectMs = Ms(timing.Connect), tlsMs = Ms(timing.Tls),
                    waitMs = Ms(timing.Wait), firstByteMs = Ms(timing.FirstByte), downloadMs = Ms(timing.Download),
                    totalMs = Ms(timing.Total), reused = timing.Reused, requests = timing.Requests
                };
            }
            if (diff is not null)
                obj["diff"] = new
                {
                    identical = diff.Identical,
                    statusBefore = diff.StatusBefore,
                    statusAfter = diff.StatusAfter,
                    headers = diff.Headers.Select(h => new { name = h.Name, before = h.Before, after = h.After }).ToList(),
                    body = diff.Body.Select(b => new
                    {
                        path = b.Path,
                        kind = b.Kind.ToString(),
                        before = b.Before,
                        after = b.After
                    }).ToList()
                };
            if (notes is { Count: > 0 }) obj["notes"] = notes;
            if (includeBody && r.Error is null)
            {
                if (binary) obj["bodyBase64"] = Convert.ToBase64String(r.Body);
                else obj["body"] = text;
                // Only when a cap cut the body, so every other envelope keeps its shape.
                if (r.BodyTruncated) obj["truncated"] = true;
            }
            return JsonSerializer.Serialize(obj, new JsonSerializerOptions { WriteIndented = true });
        }
    }

    private static ConnectionInfo Tls => new()
    {
        TlsProtocol = "Tls13", ClientCertificateSent = true,
        RevocationMode = RevocationMode.Online, RevocationStatus = RevocationStatus.Checked,
    };

    public static TheoryData<string> Cases => new()
    {
        "plain", "binary", "error", "error-bare", "redirects", "timing", "diff", "notes", "truncated", "no-body", "odd-text", "no-connection",
    };

    private static (ApiResponse R, bool IncludeBody, IReadOnlyList<string>? Notes, DiffResult? Diff, PhaseTimings? Timing) Case(string name)
    {
        var headers = new List<KeyValuePair<string, string>>
        {
            new("Content-Type", "application/json; charset=utf-8"),
            new("Set-Cookie", "a=1; Path=/"),
            new("set-cookie", "b=<2>&'3'"),
            new("X-Unicode", "caf\u00e9 \u2603 \U0001F600"),
            new("Link", "<https://x/?a=1&b=2>; rel=\"next\""),
        };
        var ok = new ApiResponse
        {
            StatusCode = 200, ReasonPhrase = "OK", Headers = headers, ContentType = "application/json",
            Body = Encoding.UTF8.GetBytes("{\"a\":\"<b> & 'c' + \u00e9\\n\"}"), Elapsed = TimeSpan.FromMilliseconds(12.6),
            Connection = Tls,
        };
        return name switch
        {
            "plain" => (ok, true, null, null, null),
            "binary" => (ok with { Body = [0xff, 0xfe, 0x00, 0x2b, 0x3e, 0x80, 0xfb, 0xef] }, true, null, null, null),
            "error" => (new ApiResponse
            {
                Headers = [], Elapsed = TimeSpan.FromMilliseconds(1003.4),
                Error = new ApiError(ApiErrorKind.Network, "No connection could be made <because>",
                    [new ErrorDetail("System.Net.Sockets.SocketException", "refused \"x\"", 10061), new ErrorDetail("System.IO.IOException", "io", null)],
                    System.Net.Sockets.SocketError.ConnectionRefused),
            }, true, null, null, null),
            "error-bare" => (new ApiResponse { Headers = [], Error = new ApiError(ApiErrorKind.Timeout, "timed out") }, true, null, null, null),
            "redirects" => (ok with
            {
                Redirects = [new RedirectHop(302, "http://a/x", "https://b/y?q=1&r=2", true, false), new RedirectHop(301, "https://b/y", "http://c/", false, true)],
                Attempts = 3,
            }, true, null, null, null),
            "timing" => (ok, true, null, null, new PhaseTimings
            {
                Dns = TimeSpan.FromMilliseconds(0.04), Connect = TimeSpan.FromMilliseconds(4.06), Tls = null,
                Wait = TimeSpan.FromMilliseconds(6), FirstByte = TimeSpan.FromMilliseconds(50.25), Download = TimeSpan.FromMilliseconds(1),
                Total = TimeSpan.FromMilliseconds(51.333), Reused = true, Requests = 2,
            }),
            "diff" => (ok, true, null, new DiffResult(false, 200, 500,
                [new HeaderDiff("ETag", "\"1\"", null), new HeaderDiff("X-New", null, "<v>")],
                [new BodyDiff("$.a[0]", DiffKind.Changed, "1", "2"), new BodyDiff("$.b", DiffKind.Added, null, "{\"c\":true}")]), null),
            "notes" => (ok, true, ["a note", "another <one> & 'more'"], null, null),
            "truncated" => (ok with { BodyTruncated = true }, true, null, null, null),
            "no-body" => (ok, false, null, null, null),
            "odd-text" => (ok with { Body = Encoding.UTF8.GetBytes("line\r\n\ttab \u0001 \u007f \u2028 `back` \\ \"q\"") }, true, null, null, null),
            "no-connection" => (ok with { Connection = null, StatusCode = null, ReasonPhrase = null, ContentType = null }, true, [], null, null),
            _ => throw new ArgumentOutOfRangeException(nameof(name)),
        };
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void The_envelope_is_the_text_the_serializer_wrote(string name)
    {
        var (r, includeBody, notes, diff, timing) = Case(name);
        string expected = Reference.Build(r, includeBody, notes, diff, timing);
        string actual = SendCommand.BuildEnvelope(r, includeBody, notes, diff, timing);
        Assert.Equal(expected, actual);
        using var _ = JsonDocument.Parse(actual);
    }

    [Fact]
    public void A_large_body_is_the_same_too()
    {
        var sb = new StringBuilder("[");
        for (int i = 0; i < 20000; i++) sb.Append(i == 0 ? "" : ",").Append("{\"id\":").Append(i).Append(",\"name\":\"item <").Append(i).Append(">\"}");
        sb.Append(']');
        var r = new ApiResponse { StatusCode = 200, Headers = [], Body = Encoding.UTF8.GetBytes(sb.ToString()) };
        Assert.Equal(Reference.Build(r, true), SendCommand.BuildEnvelope(r, true));
    }
}
