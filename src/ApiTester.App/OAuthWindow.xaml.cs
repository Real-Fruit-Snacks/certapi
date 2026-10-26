using System;
using System.Diagnostics;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using ApiTester.Core;

namespace ApiTester.App;

/// <summary>
/// Fetches an OAuth 2.0 token for the current request. Runs the client-credentials, password, and
/// refresh grants directly against the token endpoint, and the authorization-code grant
/// interactively — opening the browser and catching the redirect on a loopback port with PKCE. On
/// success the caller reads <see cref="Result"/> and <see cref="ApplyToUrl"/> to store/use the token.
/// </summary>
public partial class OAuthWindow : Window
{
    private readonly X509Certificate2? _cert;
    private readonly bool _insecure;
    private CancellationTokenSource? _cts;

    public OAuthTokenResult? Result { get; private set; }
    public string ApplyToUrl { get; private set; } = "";

    public OAuthWindow(X509Certificate2? cert, bool insecure, string? defaultApiUrl)
    {
        InitializeComponent();
        _cert = cert;
        _insecure = insecure;
        ApplyToBox.Text = OriginOrEmpty(defaultApiUrl);
        Loaded += (_, _) => TokenUrlBox.Focus();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        NativeTheme.ApplyTitleBar(this);
    }

    protected override void OnClosed(EventArgs e)
    {
        try { _cts?.Cancel(); } catch { /* ignore */ }
        base.OnClosed(e);
    }

    private void Header_Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private OAuthGrant Grant => GrantCombo.SelectedIndex switch
    {
        1 => OAuthGrant.Password,
        2 => OAuthGrant.RefreshToken,
        3 => OAuthGrant.AuthorizationCode,
        _ => OAuthGrant.ClientCredentials
    };

    private void GrantCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (PasswordPanel is null) return;   // fires during InitializeComponent
        PasswordPanel.Visibility = Grant == OAuthGrant.Password ? Visibility.Visible : Visibility.Collapsed;
        RefreshPanel.Visibility = Grant == OAuthGrant.RefreshToken ? Visibility.Visible : Visibility.Collapsed;
        AuthUrlPanel.Visibility = Grant == OAuthGrant.AuthorizationCode ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void Fetch_Click(object sender, RoutedEventArgs e)
    {
        string tokenUrl = TokenUrlBox.Text.Trim();
        if (tokenUrl.Length == 0) { SetStatus("Enter the token endpoint URL.", error: true); return; }

        _cts?.Dispose();
        _cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        FetchButton.IsEnabled = false;
        try
        {
            OAuthTokenResult result = Grant == OAuthGrant.AuthorizationCode
                ? await RunAuthorizationCodeAsync(tokenUrl, _cts.Token)
                : await RunDirectGrantAsync(tokenUrl, _cts.Token);

            if (result.Success)
            {
                Result = result;
                ApplyToUrl = ApplyToBox.Text.Trim();
                DialogResult = true;   // closes the dialog; the caller stores/uses the token
            }
            else
            {
                SetStatus("Token request failed — " + result.FailureMessage, error: true);
            }
        }
        catch (OperationCanceledException) { SetStatus("Cancelled.", error: true); }
        catch (Exception ex) { SetStatus(ex.Message, error: true); }
        finally { FetchButton.IsEnabled = true; }
    }

    private Task<OAuthTokenResult> RunDirectGrantAsync(string tokenUrl, CancellationToken ct)
    {
        var req = new OAuthRequest
        {
            Grant = Grant,
            TokenEndpoint = tokenUrl,
            ClientId = Blank(ClientIdBox.Text),
            ClientSecret = Blank(ClientSecretBox.Text),
            ClientAuth = ClientAuthCombo.SelectedIndex == 1 ? OAuthClientAuth.Basic : OAuthClientAuth.Body,
            Scope = Blank(ScopeBox.Text),
            Username = Blank(UsernameBox.Text),
            Password = Blank(PasswordBox.Text),
            RefreshToken = Blank(RefreshTokenBox.Text)
        };
        SetStatus("Requesting a token…", error: false);
        return OAuthClient.RequestTokenAsync(req, _cert, _insecure, ct: ct);
    }

    private async Task<OAuthTokenResult> RunAuthorizationCodeAsync(string tokenUrl, CancellationToken ct)
    {
        string clientId = ClientIdBox.Text.Trim();
        string authUrl = AuthUrlBox.Text.Trim();
        if (clientId.Length == 0) return Fail("Enter the client id for the authorization-code grant.");
        if (authUrl.Length == 0) return Fail("Enter the authorize URL for the authorization-code grant.");

        int port = OAuthRedirect.FreeLoopbackPort();
        string redirectUri = $"http://127.0.0.1:{port}/callback";
        string verifier = OAuthAuthorization.CreateCodeVerifier();
        string challenge = OAuthAuthorization.CodeChallenge(verifier);
        string state = OAuthAuthorization.CreateState();
        string url = OAuthAuthorization.BuildAuthorizationUrl(authUrl, clientId, redirectUri, Blank(ScopeBox.Text), state, challenge);

        SetStatus("Waiting for the browser sign-in…", error: false);
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { return Fail("Could not open the browser: " + ex.Message); }

        var redirect = await OAuthRedirect.WaitForCodeAsync(port, ct);
        if (redirect.Error is not null)
            return Fail($"Authorization was declined ({redirect.ErrorDescription ?? redirect.Error}).");
        if (!string.Equals(redirect.State, state, StringComparison.Ordinal))
            return Fail("The redirect state did not match — ignoring the response for safety.");
        if (redirect.Code is null)
            return Fail("The redirect did not include an authorization code.");

        SetStatus("Exchanging the code for a token…", error: false);
        var req = new OAuthRequest
        {
            Grant = OAuthGrant.AuthorizationCode,
            TokenEndpoint = tokenUrl,
            ClientId = clientId,
            ClientSecret = Blank(ClientSecretBox.Text),
            ClientAuth = ClientAuthCombo.SelectedIndex == 1 ? OAuthClientAuth.Basic : OAuthClientAuth.Body,
            Scope = Blank(ScopeBox.Text),
            Code = redirect.Code,
            RedirectUri = redirectUri,
            CodeVerifier = verifier
        };
        return await OAuthClient.RequestTokenAsync(req, _cert, _insecure, ct: ct);
    }

    private static OAuthTokenResult Fail(string message) =>
        new(false, null, null, null, null, null, null, "error", message, "");

    private void SetStatus(string message, bool error)
    {
        StatusBar.Visibility = Visibility.Visible;
        StatusText.Text = message;
        StatusText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, error ? "Red" : "Signal");
    }

    private static string? Blank(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static string OriginOrEmpty(string? url) =>
        url is not null && Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme is "http" or "https"
            ? $"{u.Scheme}://{u.Host}" + (u.IsDefaultPort ? "" : $":{u.Port}")
            : "";
}
