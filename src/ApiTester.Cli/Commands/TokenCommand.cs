using System.Text.Json;
using ApiTester.Core;

namespace ApiTester.Cli.Commands;

public static class TokenCommand
{
    public const string Help = """
        Usage: certapi token [options]

        Fetch an OAuth 2.0 access token from a token endpoint. By default the access token is
        printed to stdout; --save stores it so later `certapi send` calls attach it automatically.

        Grant (default client_credentials):
          --grant client_credentials   Machine-to-machine (client id + secret)
          --grant password             Resource-owner password (--username / --password)
          --grant refresh              Exchange a --refresh-token for a new access token
          --grant device               Device-code flow (RFC 8628): prints a verification URL and
                                       code, then polls until you approve in a browser anywhere.
                                       Needs --device-url and --client-id; Ctrl+C abandons it.

        Endpoint & client:
          --token-url <url>            The token endpoint (required)
          --device-url <url>           The device-authorization endpoint (device grant only)
          --client-id <id>
          --client-secret <secret>
          --client-auth <body|basic>   Send client creds in the body (default) or a Basic header
          --scope "<a b c>"            Space-separated scopes
          --username <u> / --password <p>   For the password grant
          --refresh-token <t>          For the refresh grant
          --param k=v                  Extra form parameter (audience, resource, …); repeatable

        Reuse:
          --save                       Store the access token for reuse by `certapi send`
          --for <api-url>              API origin the saved token applies to (repeatable; required
                                       with --save). e.g. --for https://api.example.com
          --workspace <file>           Save into a workspace file instead of the live state

        TLS / certificates (the token endpoint itself may require mTLS):
          --cert <thumb|subject>  --cert-file <path>  --cert-password <pw>  --key-file <path>
          --cert-issuer <substr>  --cert-serial <hex>  --cert-pin <pin> (or CERTAPI_CERT_PIN)
          --store <location>      --insecure
        A token endpoint pinned with `certapi trust add` needs no --insecure.

        """ + "\n" + TransportFlags.StreamHelp + "\n" + """

        Output:
          --json                       Print the full token result (access/refresh/expiry/scope)
          -q, --quiet                  No notes on stderr

        Global: --debug and --log-file <path> work here too.

        Examples:
          # Client-credentials, print the token
          certapi token --token-url https://auth.example.com/token \
              --client-id app --client-secret s3cret --scope "api.read api.write"

          # Fetch and store it so subsequent sends to the API attach it automatically
          certapi token --token-url https://auth.example.com/token --client-id app \
              --client-secret s3cret --save --for https://api.example.com
          certapi send https://api.example.com/orders

        Exit 0 on a token, 1 if the endpoint returned an error, 2/3 on usage/data errors.
        """;

    public static int Run(Args args, TextWriter stdout, TextWriter stderr, CliServices services)
    {
        var grant = ParseGrant(args.Value("--grant"));
        string? tokenUrl = args.Value("--token-url");
        string? clientId = args.Value("--client-id");
        string? clientSecret = args.Value("--client-secret");
        var clientAuth = string.Equals(args.Value("--client-auth"), "basic", StringComparison.OrdinalIgnoreCase)
            ? OAuthClientAuth.Basic : OAuthClientAuth.Body;
        string? scope = args.Value("--scope");
        string? username = args.Value("--username");
        string? password = args.Value("--password");
        string? refreshToken = args.Value("--refresh-token");
        var paramSpecs = args.Values("--param");
        bool save = args.Flag("--save");
        var forUrls = args.Values("--for");
        string? workspace = args.Value("--workspace");
        string store = args.Value("--store") ?? services.Profile?.Store ?? "CurrentUser";
        bool insecure = args.Switch("--insecure", services.Profile?.Insecure);
        bool json = args.Flag("--json");
        bool quiet = args.Flag("-q", "--quiet");
        string? deviceUrl = args.Value("--device-url");
        var transport = TransportFlags.ParseStreamSubset(args, insecure, environment: null, services.Profile);
        // Resolve the certificate before Positionals()/validation so its options aren't leftovers.
        var cert = CliCert.Resolve(args, store, services, stderr);

        if (args.Positionals().Count != 0)
            throw new CliUsageException("token takes no positional arguments — pass the endpoint with --token-url.\n" + Help);
        if (string.IsNullOrWhiteSpace(tokenUrl)) throw new CliUsageException("--token-url is required.");

        // Per-grant required fields.
        switch (grant)
        {
            case OAuthGrant.Password when string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password):
                throw new CliUsageException("the password grant needs --username and --password.");
            case OAuthGrant.RefreshToken when string.IsNullOrEmpty(refreshToken):
                throw new CliUsageException("the refresh grant needs --refresh-token.");
            case OAuthGrant.DeviceCode when string.IsNullOrWhiteSpace(deviceUrl) || string.IsNullOrEmpty(clientId):
                throw new CliUsageException("the device grant needs --device-url and --client-id.");
        }
        if (save && forUrls.Count == 0)
            throw new CliUsageException("--save needs at least one --for <api-url> so the token can be scoped to an origin.");

        var extra = new List<KeyValuePair<string, string>>();
        foreach (var raw in paramSpecs)
        {
            int eq = raw.IndexOf('=');
            if (eq <= 0) throw new CliUsageException($"--param expects k=v, got '{raw}'.");
            extra.Add(new(raw[..eq], raw[(eq + 1)..]));
        }

        var request = new OAuthRequest
        {
            Grant = grant,
            TokenEndpoint = tokenUrl,
            DeviceAuthorizationEndpoint = deviceUrl,
            ClientId = clientId,
            ClientSecret = clientSecret,
            ClientAuth = clientAuth,
            Scope = scope,
            Username = username,
            Password = password,
            RefreshToken = refreshToken,
            ExtraParams = extra.Count > 0 ? extra : null
        };

        // Pins come from the same state file --save writes to; read here so an internal auth
        // server pinned with `trust add` is reachable without --insecure, like everywhere else.
        // --save may be about to CREATE the named workspace, and a file that does not exist yet
        // has no pins in it — that must stay exit 0, not become a data error.
        var trustState = workspace is not null && !System.IO.File.Exists(workspace)
            ? new AppState()
            : CliWorkspace.Load(workspace, services.LiveStatePath, stderr);
        Func<System.Security.Cryptography.X509Certificates.X509Certificate2?, bool> trust = c =>
            c is not null &&
            (TrustService.IsTrusted(trustState, TokenService.HostOf(tokenUrl), c, TrustContext.CurrentChain) ||
             (deviceUrl is not null && TrustService.IsTrusted(trustState, TokenService.HostOf(deviceUrl), c, TrustContext.CurrentChain)));

        services.Log.Debug($"OAuth {grant} → {MarkdownSecrets.RedactUrl(tokenUrl)} · client {clientId ?? "—"} · auth {clientAuth} · cert {(cert is null ? "none" : cert.Subject)}");
        if (!quiet) stderr.WriteLine($"requesting a token ({GrantLabel(grant)}) from {tokenUrl} …");

        var result = grant == OAuthGrant.DeviceCode
            ? OAuthClient.RequestDeviceTokenAsync(request,
                // The prompt is the flow: it goes to stderr even under -q, or the user could
                // never approve anything.
                prompt => stderr.WriteLine(prompt.VerificationUriComplete is { } complete
                    ? $"To sign in, visit: {complete}  (code {prompt.UserCode}, expires in {prompt.ExpiresInSeconds}s)"
                    : $"To sign in, visit {prompt.VerificationUri} and enter code: {prompt.UserCode}  (expires in {prompt.ExpiresInSeconds}s)"),
                cert, insecure, transport, trust, delay: null, services.Cancel)
                .GetAwaiter().GetResult()
            : OAuthClient.RequestTokenAsync(request, cert, insecure, transport, trust, services.Cancel)
                .GetAwaiter().GetResult();

        if (!result.Success)
        {
            stderr.WriteLine("error: " + result.FailureMessage);
            services.Log.Debug("token endpoint response: " + result.RawResponse);
            return ExitCodes.Failure;
        }

        if (!quiet)
        {
            string exp = result.ExpiresInSeconds is { } s ? $", expires in {s / 60} min" : "";
            stderr.WriteLine($"got an access token ({result.TokenType ?? "Bearer"}{exp})" +
                             (result.RefreshToken is not null ? " with a refresh token" : ""));
        }

        if (save)
        {
            SaveToken(result, forUrls, workspace, services, stderr);
        }

        if (json)
            stdout.WriteLine(JsonSerializer.Serialize(new
            {
                access_token = result.AccessToken,
                token_type = result.TokenType,
                refresh_token = result.RefreshToken,
                scope = result.Scope,
                expires_in = result.ExpiresInSeconds,
                expires_utc = result.ExpiresUtc
            }, new JsonSerializerOptions { WriteIndented = true }));
        else
            stdout.WriteLine(result.AccessToken);

        return ExitCodes.Ok;
    }

    private static void SaveToken(OAuthTokenResult result, IReadOnlyList<string> forUrls,
        string? workspace, CliServices services, TextWriter stderr)
    {
        AppState state;
        // A --workspace file that doesn't exist yet is created fresh (like `send --capture`); a
        // missing live state is already handled by CliWorkspace.Load returning an empty state.
        if (workspace is not null && !File.Exists(workspace))
        {
            state = new AppState();
        }
        else
        {
            try { state = CliWorkspace.Load(workspace, services.LiveStatePath, stderr); }
            catch (CliDataException ex) { stderr.WriteLine($"warning: could not load state to save the token ({ex.Message})"); return; }
        }

        var saved = new List<string>();
        foreach (var url in forUrls)
        {
            if (TokenService.OriginOf(url) is not { } origin)
            {
                stderr.WriteLine($"warning: --for '{url}' is not an http(s) URL — skipped");
                continue;
            }
            state.SessionTokens.RemoveAll(t => t.Origin == origin);
            state.SessionTokens.Add(new SessionToken
            {
                Origin = origin,
                Token = result.AccessToken!,
                Source = "oauth",
                CapturedUtc = DateTime.UtcNow,
                ExpiresUtc = result.ExpiresUtc
            });
            saved.Add(origin);
        }

        if (saved.Count == 0) return;
        if (workspace is null && services.IsGuiRunning())
        {
            stderr.WriteLine("note: the GUI is running — the token was not saved (it would overwrite it on close).");
            return;
        }
        string path = workspace ?? services.LiveStatePath;
        try
        {
            CliWorkspace.ReportSaveResult(state.SaveTo(path), path, stderr);
            stderr.WriteLine("saved the token for " + string.Join(", ", saved));
        }
        catch (Exception ex) { stderr.WriteLine($"warning: could not save the token: {ex.Message}"); }
    }

    private static OAuthGrant ParseGrant(string? raw) => raw?.ToLowerInvariant() switch
    {
        null or "" or "client_credentials" or "client-credentials" or "cc" => OAuthGrant.ClientCredentials,
        "password" or "pw" => OAuthGrant.Password,
        "refresh" or "refresh_token" or "refresh-token" => OAuthGrant.RefreshToken,
        "device" or "device_code" or "device-code" => OAuthGrant.DeviceCode,
        _ => throw new CliUsageException($"unknown --grant '{raw}'. Use client_credentials, password, refresh, or device.")
    };

    private static string GrantLabel(OAuthGrant g) => g switch
    {
        OAuthGrant.ClientCredentials => "client credentials",
        OAuthGrant.Password => "password",
        OAuthGrant.RefreshToken => "refresh token",
        OAuthGrant.DeviceCode => "device code",
        _ => g.ToString()
    };
}
