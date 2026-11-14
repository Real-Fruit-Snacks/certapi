using ApiTester.Core;

namespace ApiTester.App;

/// <summary>The request editor's controls, as a UI-free value. Every property is control-shaped — a
/// text box is a <see cref="string"/>, a check box is a <see cref="bool"/>, a combo box is the
/// selected <see cref="int"/> index — which is what turns the index-to-enum mapping (the exact thing
/// that had to be hand-verified by hand in both directions in v1.64.0, for the revocation combo) into
/// a plain value a test can assert on, rather than a side effect of opening a window.
/// <para>This lives in <c>ApiTester.App</c> rather than <c>ApiTester.Core</c> on purpose: the combo
/// indices it carries are this desktop editor's own XAML ordering, which has no business in the
/// shared core library the command line also uses.</para>
/// <para><see cref="From"/> and <see cref="ApplyTo"/> are the two halves of the round trip
/// <c>MainWindow.LoadIntoControls</c> and <c>CaptureControlsInto</c> used to perform inline. A field
/// loaded but not captured is silently discarded on save; a combo whose index-to-enum mapping differs
/// between the two silently rewrites the user's setting — this record, and the exhaustive tests
/// against it, are what makes either mistake fail loudly instead.</para>
/// <para>It is a <c>record</c> deliberately: the generated value equality lets one assertion cover
/// every property, including ones added in the future.</para></summary>
public sealed record RequestEditorState
{
    public string Method { get; init; } = "GET";
    public string BaseUrlText { get; init; } = "";
    public string UrlText { get; init; } = "";
    public string? CertThumbprint { get; init; }
    public string BodyText { get; init; } = "";
    public string ContentType { get; init; } = "application/json";
    public bool BodyModeForm { get; init; }
    public int AuthTypeIndex { get; init; }
    public string BearerTokenText { get; init; } = "";
    public string BasicUserText { get; init; } = "";
    public string BasicPassText { get; init; } = "";
    public bool WindowsDefault { get; init; } = true;
    public string WindowsUserText { get; init; } = "";
    public string WindowsPassText { get; init; } = "";
    public string AwsKeyText { get; init; } = "";
    public string AwsSecretText { get; init; } = "";
    public string AwsScopeText { get; init; } = "";
    public int HmacSchemeIndex { get; init; }
    public string HmacKeyText { get; init; } = "";
    public string HmacSecretText { get; init; } = "";
    public string HmacAlgText { get; init; } = "sha256";
    public string HmacSignText { get; init; } = "";
    public string HmacHeaderText { get; init; } = "X-Signature: {signature}";
    public string HmacEncodingText { get; init; } = "hex";
    public string HmacAlsoText { get; init; } = "";
    public string HmacComponentsText { get; init; } = "";
    public bool IgnoreServerCert { get; init; }
    public string TimeoutText { get; init; } = "100";
    public int ProxyIndex { get; init; }
    public string ProxyUrlText { get; init; } = "";
    public string ProxyUserText { get; init; } = "";
    public string ProxyPassText { get; init; } = "";
    public string NoProxyText { get; init; } = "";
    public int RevocationIndex { get; init; }
    public bool RevocationStrict { get; init; }
    public bool FollowRedirects { get; init; } = true;
    public string MaxRedirectsText { get; init; } = "20";
    public bool Decompress { get; init; } = true;
    public int VersionIndex { get; init; }
    public string RetriesText { get; init; } = "0";
    public string RetryOnText { get; init; } = "429,502,503,504";
    public string RetryDelayText { get; init; } = "500";
    public bool RetryUnsafeMethods { get; init; }
    public bool RetryOnTransportError { get; init; } = true;

    /// <summary>Everything <c>LoadIntoControls</c> used to put into the editor's controls, read out of
    /// a request model instead. Does not mutate <paramref name="m"/>.</summary>
    public static RequestEditorState From(RequestModel m)
    {
        var hmac = m.AuthType == "Hmac" ? HmacSettings.FromScope(m.AuthScope) : new HmacSettings();
        return FromModel(m) with
        {
            HmacSchemeIndex = (int)hmac.Scheme,
            HmacKeyText = m.AuthType == "Hmac" ? m.AuthUser ?? "" : "",
            HmacSecretText = m.AuthType == "Hmac" ? m.AuthSecret ?? "" : "",
            HmacAlgText = hmac.Algorithm,
            HmacSignText = (hmac.StringToSign ?? "").Replace("\r\n", "\n"),
            HmacHeaderText = hmac.Header,
            HmacEncodingText = hmac.Encoding,
            HmacAlsoText = string.Join("\n", hmac.AlsoSend),
            HmacComponentsText = hmac.Components ?? ""
        };
    }

    private static RequestEditorState FromModel(RequestModel m) => new()
    {
        Method = m.Method,
        BaseUrlText = m.BaseUrl ?? "",
        UrlText = m.Path,
        CertThumbprint = m.CertThumbprint,
        BodyText = m.Body ?? "",
        ContentType = m.ContentType,
        BodyModeForm = m.IsMultipart,
        AuthTypeIndex = m.AuthType switch { "Bearer" => 2, "Basic" => 3, "Windows" => 4, "Digest" => 5, "AwsSigV4" => 6, "Hmac" => 7, "None" => 1, _ => 0 },
        BearerTokenText = m.AuthType == "Bearer" ? m.AuthSecret ?? "" : "",
        // Digest takes a user name and password exactly as Basic does, in the same two boxes.
        BasicUserText = m.AuthType is "Basic" or "Digest" ? m.AuthUser ?? "" : "",
        BasicPassText = m.AuthType is "Basic" or "Digest" ? m.AuthSecret ?? "" : "",
        AwsKeyText = m.AuthType == "AwsSigV4" ? m.AuthUser ?? "" : "",
        AwsSecretText = m.AuthType == "AwsSigV4" ? m.AuthSecret ?? "" : "",
        AwsScopeText = m.AuthType == "AwsSigV4" ? m.AuthScope ?? "" : "",
        // Windows auth: an empty user means single sign-on; a value means explicit credentials.
        WindowsDefault = m.AuthType != "Windows" || string.IsNullOrEmpty(m.AuthUser),
        WindowsUserText = m.AuthType == "Windows" ? m.AuthUser ?? "" : "",
        WindowsPassText = m.AuthType == "Windows" ? m.AuthSecret ?? "" : "",
        IgnoreServerCert = m.IgnoreServerCert,
        TimeoutText = m.TimeoutSeconds.ToString(),
        ProxyIndex = m.Transport.Proxy switch { ProxyMode.None => 1, ProxyMode.Explicit => 2, _ => 0 },
        ProxyUrlText = m.Transport.ProxyUrl ?? "",
        ProxyUserText = m.Transport.ProxyUser ?? "",
        ProxyPassText = m.Transport.ProxyPassword ?? "",
        NoProxyText = m.Transport.NoProxy ?? "",
        RevocationIndex = m.Transport.Revocation switch { RevocationMode.Offline => 1, RevocationMode.Online => 2, _ => 0 },
        RevocationStrict = m.Transport.RevocationStrict,
        FollowRedirects = m.Transport.FollowRedirects,
        MaxRedirectsText = m.Transport.MaxRedirects.ToString(),
        Decompress = m.Transport.Decompress,
        VersionIndex = m.Transport.Version switch { HttpVersionMode.Http11 => 1, HttpVersionMode.Http2 => 2, HttpVersionMode.Http3 => 3, _ => 0 },
        RetriesText = m.Transport.Retries.ToString(),
        RetryOnText = m.Transport.RetryOn ?? "",
        RetryDelayText = m.Transport.RetryDelayMs.ToString(),
        RetryUnsafeMethods = m.Transport.RetryUnsafeMethods,
        RetryOnTransportError = m.Transport.RetryOnTransportError
    };

    /// <summary>Everything <c>CaptureControlsInto</c> used to read back out of the editor's controls,
    /// written onto a request model instead — in the same order <c>CaptureControlsInto</c> wrote them,
    /// because <c>RequestTab</c> subscribes to <see cref="RequestModel.PropertyChanged"/> for
    /// <see cref="RequestModel.Method"/> and <see cref="RequestModel.Path"/> to keep the tab title in
    /// step, so reordering these writes would change the sequence of notifications a live window
    /// sees.</summary>
    public void ApplyTo(RequestModel m)
    {
        m.Method = Method;
        m.BaseUrl = BaseUrlText;

        // Fold any query typed straight into the URL box into the parameter grid.
        var (path, typedQuery) = RequestUrl.SplitForEditing(UrlText.Trim());
        m.Path = path;
        if (typedQuery.Count > 0)
        {
            m.QueryParams.Clear();
            foreach (var kv in typedQuery) m.QueryParams.Add(new ParamRow { Key = kv.Key, Value = kv.Value });
        }

        m.Body = BodyText;
        m.ContentType = ContentType;
        m.IsMultipart = BodyModeForm;
        m.AuthType = AuthTypeIndex switch { 2 => "Bearer", 3 => "Basic", 4 => "Windows", 5 => "Digest", 6 => "AwsSigV4", 7 => "Hmac", 1 => "None", _ => "Auto" };
        bool winSso = WindowsDefault;
        m.AuthUser = AuthTypeIndex switch { 4 => winSso ? "" : WindowsUserText, 6 => AwsKeyText, 7 => HmacKeyText, _ => BasicUserText };
        m.AuthSecret = AuthTypeIndex switch
        {
            2 => BearerTokenText,
            4 => winSso ? "" : WindowsPassText,
            6 => AwsSecretText,
            7 => HmacSecretText,
            _ => BasicPassText
        };
        m.AuthScope = AuthTypeIndex switch
        {
            6 => AwsScopeText,
            7 => new HmacSettings
            {
                Scheme = HmacSchemeIndex is >= 0 and <= 2 ? (HmacScheme)HmacSchemeIndex : HmacScheme.Template,
                Algorithm = string.IsNullOrWhiteSpace(HmacAlgText) ? "sha256" : HmacAlgText.Trim(),
                StringToSign = string.IsNullOrEmpty(HmacSignText) ? null : HmacSignText.Replace("\r\n", "\n"),
                Header = string.IsNullOrWhiteSpace(HmacHeaderText) ? "X-Signature: {signature}" : HmacHeaderText.Trim(),
                Encoding = string.IsNullOrWhiteSpace(HmacEncodingText) ? "hex" : HmacEncodingText.Trim(),
                AlsoSend = HmacAlsoText.Replace("\r", "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList(),
                Components = string.IsNullOrWhiteSpace(HmacComponentsText) ? null : HmacComponentsText.Trim()
            }.ToScope(),
            _ => null
        };
        m.CertThumbprint = CertThumbprint;
        m.IgnoreServerCert = IgnoreServerCert;
        m.TimeoutSeconds = ParseTimeoutSeconds(TimeoutText);

        // Transport settings are edited in place so the model instance the tab holds keeps its identity.
        m.Transport.Proxy = ProxyIndex switch { 1 => ProxyMode.None, 2 => ProxyMode.Explicit, _ => ProxyMode.System };
        m.Transport.ProxyUrl = BlankToNull(ProxyUrlText);
        m.Transport.ProxyUser = BlankToNull(ProxyUserText);
        m.Transport.ProxyPassword = BlankToNull(ProxyPassText);
        m.Transport.NoProxy = BlankToNull(NoProxyText);
        m.Transport.Revocation = RevocationIndex switch { 1 => RevocationMode.Offline, 2 => RevocationMode.Online, _ => RevocationMode.None };
        m.Transport.RevocationStrict = RevocationStrict;
        m.Transport.FollowRedirects = FollowRedirects;
        m.Transport.MaxRedirects = ParseMaxRedirects(MaxRedirectsText);
        m.Transport.Decompress = Decompress;
        m.Transport.Version = VersionIndex switch { 1 => HttpVersionMode.Http11, 2 => HttpVersionMode.Http2, 3 => HttpVersionMode.Http3, _ => HttpVersionMode.Auto };
        m.Transport.Retries = ParseCount(RetriesText, fallback: 0, max: 20);
        // An emptied box has not said "never retry on status", it has said nothing — so fall back to
        // the default set, matching what TransportSettings.ToOptions() does for an unparseable string.
        m.Transport.RetryOn = string.IsNullOrWhiteSpace(RetryOnText) ? "429,502,503,504" : RetryOnText.Trim();
        m.Transport.RetryDelayMs = ParseCount(RetryDelayText, fallback: 500, max: 60000);
        m.Transport.RetryUnsafeMethods = RetryUnsafeMethods;
        m.Transport.RetryOnTransportError = RetryOnTransportError;
        // Headers, Captures, Assertions and FormParts are the model's own collections, bound straight to
        // the grids as ItemsSource — the grid edits them in place, so there is nothing to copy back.
    }

    /// <summary>The timeout as typed, falling back to the default while the box is empty, unparseable,
    /// zero, or negative — capped at an hour.</summary>
    public static int ParseTimeoutSeconds(string? text)
    {
        if (int.TryParse(text, out var s) && s > 0) return Math.Min(s, 3600);
        return 100;
    }

    /// <summary>The hop limit as typed, falling back to the default while the box is empty or nonsense.</summary>
    public static int ParseMaxRedirects(string? text)
    {
        if (int.TryParse(text, out var n) && n > 0) return Math.Min(n, 100);
        return 20;
    }

    /// <summary>A non-negative number from a text box, clamped. A typo in a settings box must not be a
    /// crash or a silent thirty-thousand-retry loop, so an unparseable value falls back to the default.</summary>
    public static int ParseCount(string? text, int fallback, int max) =>
        int.TryParse(text, out var n) && n >= 0 ? Math.Min(n, max) : fallback;

    /// <summary>An empty or whitespace-only box becomes null, so an untouched setting stays absent
    /// from the workspace JSON rather than being stored as "". A real value is trimmed.</summary>
    public static string? BlankToNull(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
