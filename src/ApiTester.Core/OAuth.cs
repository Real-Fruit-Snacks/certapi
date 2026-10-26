using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace ApiTester.Core;

/// <summary>The OAuth 2.0 grant types this client can run against a token endpoint.</summary>
public enum OAuthGrant { ClientCredentials, Password, RefreshToken, AuthorizationCode, DeviceCode }

/// <summary>How the client authenticates to the token endpoint.</summary>
public enum OAuthClientAuth
{
    /// <summary>client_id / client_secret in the form body (client_secret_post).</summary>
    Body,
    /// <summary>HTTP Basic with client_id:client_secret (client_secret_basic).</summary>
    Basic
}

/// <summary>Everything needed to ask a token endpoint for a token. Only the fields a given grant
/// uses need to be set (e.g. Username/Password for the password grant).</summary>
public sealed record OAuthRequest
{
    public OAuthGrant Grant { get; init; }
    public string TokenEndpoint { get; init; } = "";
    public string? ClientId { get; init; }
    public string? ClientSecret { get; init; }
    public OAuthClientAuth ClientAuth { get; init; } = OAuthClientAuth.Body;
    public string? Scope { get; init; }

    // Password grant.
    public string? Username { get; init; }
    public string? Password { get; init; }

    // Refresh-token grant.
    public string? RefreshToken { get; init; }

    // Authorization-code grant.
    public string? Code { get; init; }
    public string? RedirectUri { get; init; }
    public string? CodeVerifier { get; init; }   // PKCE

    // Device-code grant (RFC 8628).
    public string? DeviceAuthorizationEndpoint { get; init; }
    public string? DeviceCode { get; init; }

    /// <summary>Extra form parameters (audience, resource, custom vendor fields).</summary>
    public IReadOnlyList<KeyValuePair<string, string>>? ExtraParams { get; init; }
}

/// <summary>The outcome of a token request. <see cref="Success"/> is true only when the endpoint
/// returned an access token; otherwise <see cref="Error"/> carries the reason.</summary>
public sealed record OAuthTokenResult(
    bool Success,
    string? AccessToken,
    string? TokenType,
    string? RefreshToken,
    string? Scope,
    DateTime? ExpiresUtc,
    int? ExpiresInSeconds,
    string? Error,
    string? ErrorDescription,
    string RawResponse)
{
    /// <summary>A one-line reason for a failure, combining the error code and description.</summary>
    public string FailureMessage =>
        Error is null ? "The token endpoint did not return an access_token."
        : ErrorDescription is { Length: > 0 } d ? $"{Error}: {d}" : Error;
}

/// <summary>Runs the non-interactive OAuth 2.0 grants against a token endpoint and parses the token
/// response. The request can carry a client certificate (mTLS token endpoints are supported) and
/// the same insecure toggle the rest of the app uses.</summary>
public static class OAuthClient
{
    public static async Task<OAuthTokenResult> RequestTokenAsync(
        OAuthRequest req,
        X509Certificate2? clientCertificate = null,
        bool ignoreServerCertificateErrors = false,
        TransportOptions? transport = null,
        Func<X509Certificate2?, bool>? trustServerCertificate = null,
        CancellationToken ct = default)
    {
        var form = new List<KeyValuePair<string, string>>
        {
            new("grant_type", GrantType(req.Grant))
        };

        switch (req.Grant)
        {
            case OAuthGrant.Password:
                Add(form, "username", req.Username);
                Add(form, "password", req.Password);
                break;
            case OAuthGrant.RefreshToken:
                Add(form, "refresh_token", req.RefreshToken);
                break;
            case OAuthGrant.AuthorizationCode:
                Add(form, "code", req.Code);
                Add(form, "redirect_uri", req.RedirectUri);
                Add(form, "code_verifier", req.CodeVerifier);
                break;
            case OAuthGrant.DeviceCode:
                Add(form, "device_code", req.DeviceCode);
                break;
        }
        Add(form, "scope", req.Scope);

        // client_secret_post puts the credentials in the body; client_secret_basic uses a header.
        // A public client (PKCE, no secret) still sends its client_id in the body.
        if (req.ClientAuth == OAuthClientAuth.Body)
        {
            Add(form, "client_id", req.ClientId);
            Add(form, "client_secret", req.ClientSecret);
        }
        else if (req.ClientId is { Length: > 0 } && string.IsNullOrEmpty(req.ClientSecret))
        {
            Add(form, "client_id", req.ClientId);
        }

        if (req.ExtraParams is { } extra)
            foreach (var kv in extra) form.Add(kv);

        var options = transport ?? new TransportOptions();
        if (ignoreServerCertificateErrors) options = options with { IgnoreServerCertificateErrors = true };
        var handler = StreamTransport.CreateHandler(clientCertificate, options, trustServerCertificate,
            Uri.TryCreate(req.TokenEndpoint, UriKind.Absolute, out var tokenDestination) ? tokenDestination : null);

        using var http = new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(100) };
        using var message = new HttpRequestMessage(HttpMethod.Post, req.TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(form)
        };
        message.Headers.TryAddWithoutValidation("Accept", "application/json");
        if (req.ClientAuth == OAuthClientAuth.Basic && req.ClientId is { Length: > 0 })
        {
            string basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{req.ClientId}:{req.ClientSecret}"));
            message.Headers.TryAddWithoutValidation("Authorization", "Basic " + basic);
        }

        string raw;
        try
        {
            using var response = await http.SendAsync(message, ct);
            raw = await response.Content.ReadAsStringAsync(ct);
            return Parse(raw);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return Failure("timeout", "The token request timed out.", "");
        }
        catch (HttpRequestException ex)
        {
            return Failure("request_failed", ex.Message, "");
        }
    }

    /// <summary>Runs the whole RFC 8628 device-authorization flow: asks the device endpoint for a
    /// code, hands the user instructions to <paramref name="onPrompt"/>, then polls the token
    /// endpoint at the server's stated interval until the user approves, refuses, or the code
    /// expires. `authorization_pending` keeps polling; `slow_down` adds five seconds per the RFC;
    /// anything else — a token or a terminal error — is the answer. The countdown is logical
    /// (expires_in minus the intervals actually waited), never a wall-clock race, and
    /// <paramref name="delay"/> exists so a test can run the flow with no waiting at all.</summary>
    public static async Task<OAuthTokenResult> RequestDeviceTokenAsync(
        OAuthRequest req,
        Action<OAuthDevicePrompt> onPrompt,
        X509Certificate2? clientCertificate = null,
        bool ignoreServerCertificateErrors = false,
        TransportOptions? transport = null,
        Func<X509Certificate2?, bool>? trustServerCertificate = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(req.DeviceAuthorizationEndpoint))
            return Failure("invalid_request", "The device grant needs a device-authorization endpoint.", "");
        delay ??= Task.Delay;

        var options = transport ?? new TransportOptions();
        if (ignoreServerCertificateErrors) options = options with { IgnoreServerCertificateErrors = true };
        var handler = StreamTransport.CreateHandler(clientCertificate, options, trustServerCertificate);
        using var http = new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(100) };

        // ---- step 1: the device-authorization request ------------------------------------
        var form = new List<KeyValuePair<string, string>>();
        if (req.ClientAuth == OAuthClientAuth.Body || string.IsNullOrEmpty(req.ClientSecret))
            Add(form, "client_id", req.ClientId);
        Add(form, "scope", req.Scope);
        if (req.ExtraParams is { } extra) foreach (var kv in extra) form.Add(kv);

        string raw;
        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, req.DeviceAuthorizationEndpoint)
            { Content = new FormUrlEncodedContent(form) };
            message.Headers.TryAddWithoutValidation("Accept", "application/json");
            if (req.ClientAuth == OAuthClientAuth.Basic && req.ClientId is { Length: > 0 })
                message.Headers.TryAddWithoutValidation("Authorization", "Basic " +
                    Convert.ToBase64String(Encoding.UTF8.GetBytes($"{req.ClientId}:{req.ClientSecret}")));
            using var response = await http.SendAsync(message, ct);
            raw = await response.Content.ReadAsStringAsync(ct);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return Failure("timeout", "The device-authorization request timed out.", "");
        }
        catch (HttpRequestException ex)
        {
            return Failure("request_failed", ex.Message, "");
        }

        JsonElement device;
        try { device = JsonDocument.Parse(raw).RootElement; }
        catch (JsonException) { return Failure("invalid_response", "The device endpoint did not return JSON.", raw); }
        if (device.ValueKind != JsonValueKind.Object)
            return Failure("invalid_response", "The device response was not a JSON object.", raw);
        if (Str(device, "error") is { } deviceError)
            return new OAuthTokenResult(false, null, null, null, null, null, null,
                deviceError, Str(device, "error_description"), raw);

        string? deviceCode = Str(device, "device_code");
        string? userCode = Str(device, "user_code");
        string? verificationUri = Str(device, "verification_uri");
        if (deviceCode is null || userCode is null || verificationUri is null)
            return Failure("invalid_response",
                "The device response was missing device_code, user_code, or verification_uri.", raw);

        int interval = Int(device, "interval") ?? 5;
        int remaining = Int(device, "expires_in") ?? 1800;
        onPrompt(new OAuthDevicePrompt(
            userCode, verificationUri, Str(device, "verification_uri_complete"), interval, remaining));

        // ---- step 2: poll the token endpoint until the user decides ----------------------
        var poll = req with { Grant = OAuthGrant.DeviceCode, DeviceCode = deviceCode, Scope = null };
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (remaining <= 0)
                return Failure("expired_token", "The device code expired before the user approved it.", "");
            await delay(TimeSpan.FromSeconds(interval), ct);
            remaining -= Math.Max(interval, 1);

            var result = await RequestTokenAsync(poll, clientCertificate, ignoreServerCertificateErrors,
                transport, trustServerCertificate, ct);
            switch (result.Error)
            {
                case "authorization_pending":
                    continue;
                case "slow_down":
                    interval += 5;   // RFC 8628 §3.5: the polling interval MUST increase by 5 seconds
                    continue;
                default:
                    return result;   // a token, or a terminal error — either way, the answer
            }
        }
    }

    private static OAuthTokenResult Parse(string raw)
    {
        JsonElement root;
        try { root = JsonDocument.Parse(raw).RootElement; }
        catch (JsonException) { return Failure("invalid_response", "The token endpoint did not return JSON.", raw); }

        if (root.ValueKind != JsonValueKind.Object)
            return Failure("invalid_response", "The token response was not a JSON object.", raw);

        if (Str(root, "error") is { } err)
            return new OAuthTokenResult(false, null, null, null, null, null, null,
                err, Str(root, "error_description"), raw);

        string? access = Str(root, "access_token");
        if (access is null)
            return Failure(null, null, raw);

        int? expiresIn = Int(root, "expires_in");
        DateTime? expiresUtc = expiresIn is { } s ? DateTime.UtcNow.AddSeconds(s) : null;

        return new OAuthTokenResult(
            true, access, Str(root, "token_type"), Str(root, "refresh_token"),
            Str(root, "scope"), expiresUtc, expiresIn, null, null, raw);
    }

    private static OAuthTokenResult Failure(string? error, string? desc, string raw) =>
        new(false, null, null, null, null, null, null, error, desc, raw);

    private static string GrantType(OAuthGrant g) => g switch
    {
        OAuthGrant.ClientCredentials => "client_credentials",
        OAuthGrant.Password => "password",
        OAuthGrant.RefreshToken => "refresh_token",
        OAuthGrant.AuthorizationCode => "authorization_code",
        OAuthGrant.DeviceCode => "urn:ietf:params:oauth:grant-type:device_code",
        _ => "client_credentials"
    };

    private static void Add(List<KeyValuePair<string, string>> form, string key, string? value)
    {
        if (!string.IsNullOrEmpty(value)) form.Add(new(key, value));
    }

    private static string? Str(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? Int(JsonElement o, string name)
    {
        if (!o.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)) return n;
        if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var s)) return s;
        return null;
    }
}

/// <summary>What the user must be told for a device-code flow to proceed: visit the URI, enter the
/// code (or just visit the complete form when the server offers one).</summary>
public sealed record OAuthDevicePrompt(
    string UserCode, string VerificationUri, string? VerificationUriComplete,
    int IntervalSeconds, int ExpiresInSeconds);

/// <summary>Helpers for the interactive authorization-code grant: PKCE (RFC 7636) and building the
/// authorization URL that a browser is sent to.</summary>
public static class OAuthAuthorization
{
    /// <summary>A fresh, high-entropy PKCE code verifier (RFC 7636 §4.1) — 43 base64url chars.</summary>
    public static string CreateCodeVerifier()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Base64Url(bytes);
    }

    /// <summary>The S256 code challenge for a verifier: base64url(SHA-256(verifier)).</summary>
    public static string CodeChallenge(string verifier) =>
        Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    /// <summary>An opaque state / nonce value for CSRF protection on the redirect.</summary>
    public static string CreateState() => Base64Url(RandomNumberGenerator.GetBytes(16));

    /// <summary>Build the authorization endpoint URL a browser is redirected to. When
    /// <paramref name="codeChallenge"/> is given, PKCE (S256) parameters are added.</summary>
    public static string BuildAuthorizationUrl(
        string authorizationEndpoint, string clientId, string redirectUri,
        string? scope, string state, string? codeChallenge)
    {
        var q = new List<KeyValuePair<string, string>>
        {
            new("response_type", "code"),
            new("client_id", clientId),
            new("redirect_uri", redirectUri),
            new("state", state)
        };
        if (!string.IsNullOrEmpty(scope)) q.Add(new("scope", scope));
        if (!string.IsNullOrEmpty(codeChallenge))
        {
            q.Add(new("code_challenge", codeChallenge));
            q.Add(new("code_challenge_method", "S256"));
        }
        string sep = authorizationEndpoint.Contains('?') ? "&" : "?";
        string query = string.Join("&", q.Select(kv =>
            $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
        return authorizationEndpoint + sep + query;
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>What came back on the OAuth redirect: the authorization code and state, or an error.</summary>
public sealed record OAuthRedirectResult(string? Code, string? State, string? Error, string? ErrorDescription);

/// <summary>A tiny loopback listener for the authorization-code redirect. It waits on
/// 127.0.0.1:&lt;port&gt; for the single browser callback, reads the <c>code</c>/<c>state</c> (or an
/// <c>error</c>) from the query, and serves a plain "you can close this tab" page.</summary>
public static class OAuthRedirect
{
    /// <summary>Reserve a free loopback port for a redirect URI before the browser is launched.</summary>
    public static int FreeLoopbackPort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    public static async Task<OAuthRedirectResult> WaitForCodeAsync(int port, CancellationToken ct = default)
    {
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        await using var reg = ct.Register(() => { try { listener.Stop(); } catch { /* ignore */ } });
        try
        {
            using var client = await listener.AcceptTcpClientAsync(ct);
            await using var stream = client.GetStream();

            var buffer = new byte[8192];
            var sb = new StringBuilder();
            while (sb.ToString().IndexOf("\r\n\r\n", StringComparison.Ordinal) < 0)
            {
                int n = await stream.ReadAsync(buffer, ct);
                if (n == 0) break;
                sb.Append(Encoding.ASCII.GetString(buffer, 0, n));
                if (sb.Length > 65536) break;   // a redirect line is small; guard against a flood
            }

            string requestLine = sb.ToString().Split("\r\n")[0];
            var result = ParseRedirect(requestLine);

            string body = result.Error is null
                ? "<!doctype html><html><body style='font-family:sans-serif;padding:2rem'>" +
                  "<h2>Authorization complete</h2><p>You can close this tab and return to certapi.</p></body></html>"
                : "<!doctype html><html><body style='font-family:sans-serif;padding:2rem'>" +
                  $"<h2>Authorization failed</h2><p>{WebUtility.HtmlEncode(result.ErrorDescription ?? result.Error)}</p></body></html>";
            var bytes = Encoding.UTF8.GetBytes(body);
            string head =
                "HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\n" +
                $"Content-Length: {bytes.Length}\r\nConnection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(head), ct);
            await stream.WriteAsync(bytes, ct);
            await stream.FlushAsync(ct);
            return result;
        }
        finally { try { listener.Stop(); } catch { /* ignore */ } }
    }

    private static OAuthRedirectResult ParseRedirect(string requestLine)
    {
        // e.g. "GET /callback?code=abc&state=xyz HTTP/1.1"
        var parts = requestLine.Split(' ');
        if (parts.Length < 2) return new(null, null, "invalid_request", "No request target on the redirect.");
        string target = parts[1];
        int q = target.IndexOf('?');
        if (q < 0) return new(null, null, "invalid_request", "The redirect had no query string.");

        string? code = null, state = null, error = null, desc = null;
        foreach (var pair in target[(q + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = pair.IndexOf('=');
            string key = Uri.UnescapeDataString((eq >= 0 ? pair[..eq] : pair).Replace('+', ' '));
            string val = eq >= 0 ? Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' ')) : "";
            switch (key)
            {
                case "code": code = val; break;
                case "state": state = val; break;
                case "error": error = val; break;
                case "error_description": desc = val; break;
            }
        }
        if (error is null && code is null) error = "invalid_request";
        return new(code, state, error, desc);
    }
}
