using System.Linq;
using System.Reflection;
using ApiTester.App;
using ApiTester.Core;

namespace ApiTester.Tests;

/// <summary>The characterisation baseline for A2 (v1.66.0). <see cref="RequestEditorState"/> extracts
/// the mapping <c>MainWindow.LoadIntoControls</c> and <c>CaptureControlsInto</c> used to do inline, so
/// the load/capture round trip — where silent data loss lives — becomes something a test can see.
/// A field loaded but not captured is silently discarded on save; a combo box whose index-to-enum
/// mapping differs between load and capture silently rewrites the user's setting. That is not
/// hypothetical: during v1.64.0 the revocation combo's mapping had to be hand-verified in both
/// directions, because nothing here could catch a mismatch. These tests exist so that never has to
/// happen again — every field a <see cref="RequestModel"/> carries, and every enum's full range, is
/// exercised in both directions. This file is the safety net for the rest of the release: later
/// slices must not edit it.</summary>
public class RequestEditorStateTests
{
    // ---------- (a) field-coverage gate ----------

    [Fact]
    public void RequestModel_field_coverage_is_fully_accounted_for()
    {
        // A field added to RequestModel in the future fails this test until somebody decides which
        // bucket it belongs in. That is the entire point of the gate: an unmapped field is a silent
        // data-loss bug waiting to happen, and this makes it a compile-time-adjacent, first-run
        // failure instead.
        var roundTripped = new[]
        {
            "Method", "BaseUrl", "Path", "Body", "ContentType", "AuthType", "AuthUser", "AuthSecret",
            "AuthScope", "CertThumbprint", "IgnoreServerCert", "TimeoutSeconds", "IsMultipart", "Transport"
        };
        // Bound straight to a grid as ItemsSource; the grid edits these in place, so ApplyTo never
        // copies them back.
        var gridBound = new[] { "Headers", "Captures", "Assertions", "FormParts" };
        // Rewritten only when the URL box carries a query typed straight into it.
        var queryFold = new[] { "QueryParams" };
        // SourceCollectionId: identity of the collection entry a tab was opened from, so a send can
        // record its result back on that entry. Not an editable field — no control shows it.
        var unmapped = new[] { "SourceCollectionId" };

        AssertBucketsDisjointAndComplete(typeof(RequestModel), roundTripped, gridBound, queryFold, unmapped);
    }

    [Fact]
    public void TransportSettings_field_coverage_is_fully_accounted_for()
    {
        var roundTripped = new[]
        {
            "Proxy", "ProxyUrl", "ProxyUser", "ProxyPassword", "FollowRedirects", "MaxRedirects",
            "Decompress", "Version", "Retries", "RetryOn", "RetryDelayMs", "RetryOnTransportError",
            "RetryUnsafeMethods", "NoProxy", "Revocation", "RevocationStrict"
        };
        // "HonorRetryAfter deliberately has no control: it defaults to on, the command line does not
        // expose it either, and honoring the server's own Retry-After is not a preference to argue
        // with." — MainWindow.xaml, the Transport tab, immediately above the retry settings.
        var unmapped = new[] { "HonorRetryAfter" };

        AssertBucketsDisjointAndComplete(typeof(TransportSettings), roundTripped, unmapped);
    }

    private static void AssertBucketsDisjointAndComplete(System.Type type, params string[][] buckets)
    {
        for (int i = 0; i < buckets.Length; i++)
            for (int j = i + 1; j < buckets.Length; j++)
                Assert.Empty(buckets[i].Intersect(buckets[j]));   // a name cannot be listed in two buckets

        var expected = buckets.SelectMany(b => b).ToHashSet();
        var actual = type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => p.Name).ToHashSet();
        Assert.Equal(expected, actual);
    }

    // ---------- fully-populated model builder, shared by the exhaustive round-trip tests ----------

    /// <summary>A RequestModel with every round-tripped field set to an in-domain, normalised,
    /// non-default value, so a dropped field or a wrong index shows up immediately. Only the auth
    /// fields vary between callers — everything else stays identical across the theory so the same
    /// assertion covers every field regardless of which auth type is under test.</summary>
    private static RequestModel BuildPopulatedModel(string authType, string? authUser, string? authSecret, string? authScope = null)
    {
        var m = new RequestModel
        {
            Method = "POST",
            BaseUrl = "https://api.example.com",
            Path = "/orders/123",              // no query string
            Body = "{\"a\":1}",
            ContentType = "application/xml",
            AuthType = authType,
            AuthUser = authUser,
            AuthSecret = authSecret,
            AuthScope = authScope,
            CertThumbprint = "AA:BB:CC:DD:EE:FF",
            IgnoreServerCert = true,
            TimeoutSeconds = 45,
            IsMultipart = true
        };
        m.Transport.Proxy = ProxyMode.Explicit;
        m.Transport.ProxyUrl = "http://proxy.corp:8080";
        m.Transport.ProxyUser = "proxyuser";
        m.Transport.ProxyPassword = "proxypass";
        m.Transport.NoProxy = "internal.corp";
        m.Transport.Revocation = RevocationMode.Online;
        m.Transport.RevocationStrict = true;
        m.Transport.FollowRedirects = false;
        m.Transport.MaxRedirects = 7;
        m.Transport.Decompress = false;
        m.Transport.Version = HttpVersionMode.Http2;
        m.Transport.Retries = 3;
        m.Transport.RetryOn = "429,503";
        m.Transport.RetryDelayMs = 250;
        m.Transport.RetryUnsafeMethods = true;
        m.Transport.RetryOnTransportError = false;
        return m;
    }

    // ---------- (b) the exhaustive round trip, via record equality ----------

    [Theory]
    [InlineData("Auto", null, null)]
    [InlineData("None", null, null)]
    [InlineData("Bearer", null, "tok-abc")]
    [InlineData("Basic", "alice", "pw1")]
    [InlineData("Windows", "DOMAIN\\bob", "pw2")]      // explicit creds, not single sign-on
    [InlineData("Digest", "carol", "pw3")]
    [InlineData("AwsSigV4", "AKIDEXAMPLE", "secret-key", "eu-west-1/execute-api")]
    public void Round_trip_preserves_every_property_the_state_record_has(string authType, string? authUser, string? authSecret, string? authScope = null)
    {
        // Record equality is what makes this cover every property RequestEditorState has today AND
        // every one it gains later, with no maintenance: a field dropped on either side of the round
        // trip changes one of the two states without changing the other, and Assert.Equal catches it.
        var model = BuildPopulatedModel(authType, authUser, authSecret, authScope);

        var loaded = RequestEditorState.From(model);
        var captured = new RequestModel();
        loaded.ApplyTo(captured);

        Assert.Equal(loaded, RequestEditorState.From(captured));
    }

    // ---------- (c) model-level survival, field by field ----------

    [Fact]
    public void Round_trip_preserves_every_mapped_model_field_explicitly()
    {
        // The human-readable half of the theory above: when that one goes red, this says which field.
        var model = BuildPopulatedModel("Basic", "alice", "pw1");
        var fresh = new RequestModel();
        RequestEditorState.From(model).ApplyTo(fresh);

        Assert.Equal("POST", fresh.Method);
        Assert.Equal("https://api.example.com", fresh.BaseUrl);
        Assert.Equal("/orders/123", fresh.Path);
        Assert.Equal("{\"a\":1}", fresh.Body);
        Assert.Equal("application/xml", fresh.ContentType);
        Assert.Equal("Basic", fresh.AuthType);
        Assert.Equal("alice", fresh.AuthUser);
        Assert.Equal("pw1", fresh.AuthSecret);
        Assert.Equal("AA:BB:CC:DD:EE:FF", fresh.CertThumbprint);
        Assert.True(fresh.IgnoreServerCert);
        Assert.Equal(45, fresh.TimeoutSeconds);
        Assert.True(fresh.IsMultipart);

        Assert.Equal(ProxyMode.Explicit, fresh.Transport.Proxy);
        Assert.Equal("http://proxy.corp:8080", fresh.Transport.ProxyUrl);
        Assert.Equal("proxyuser", fresh.Transport.ProxyUser);
        Assert.Equal("proxypass", fresh.Transport.ProxyPassword);
        Assert.Equal("internal.corp", fresh.Transport.NoProxy);
        Assert.Equal(RevocationMode.Online, fresh.Transport.Revocation);
        Assert.True(fresh.Transport.RevocationStrict);
        Assert.False(fresh.Transport.FollowRedirects);
        Assert.Equal(7, fresh.Transport.MaxRedirects);
        Assert.False(fresh.Transport.Decompress);
        Assert.Equal(HttpVersionMode.Http2, fresh.Transport.Version);
        Assert.Equal(3, fresh.Transport.Retries);
        Assert.Equal("429,503", fresh.Transport.RetryOn);
        Assert.Equal(250, fresh.Transport.RetryDelayMs);
        Assert.True(fresh.Transport.RetryUnsafeMethods);
        Assert.False(fresh.Transport.RetryOnTransportError);
    }

    // ---------- (d) every enum's full range, both directions ----------

    [Theory]
    [InlineData(ProxyMode.System, 0)]
    [InlineData(ProxyMode.None, 1)]
    [InlineData(ProxyMode.Explicit, 2)]
    public void ProxyMode_round_trips_through_its_combo_index(ProxyMode mode, int expectedIndex)
    {
        var model = new RequestModel();
        model.Transport.Proxy = mode;
        var state = RequestEditorState.From(model);
        Assert.Equal(expectedIndex, state.ProxyIndex);

        var captured = new RequestModel();
        state.ApplyTo(captured);
        Assert.Equal(mode, captured.Transport.Proxy);
    }

    [Fact]
    public void Every_ProxyMode_member_is_wired_to_a_combo_index()
    {
        // This is the v1.64.0 incident made impossible to repeat silently: a fourth ProxyMode value
        // fails this test the moment it is added, until its combo index is decided in both
        // directions — instead of quietly falling through the `_ =>` arm on one side only.
        Assert.Equal(3, Enum.GetValues<ProxyMode>().Length);
    }

    [Fact]
    public void An_out_of_range_proxy_index_falls_back_to_System()
    {
        var state = new RequestEditorState { ProxyIndex = 99 };
        var m = new RequestModel();
        state.ApplyTo(m);
        Assert.Equal(ProxyMode.System, m.Transport.Proxy);
    }

    [Theory]
    [InlineData(RevocationMode.None, 0)]
    [InlineData(RevocationMode.Offline, 1)]
    [InlineData(RevocationMode.Online, 2)]
    public void RevocationMode_round_trips_through_its_combo_index(RevocationMode mode, int expectedIndex)
    {
        var model = new RequestModel();
        model.Transport.Revocation = mode;
        var state = RequestEditorState.From(model);
        Assert.Equal(expectedIndex, state.RevocationIndex);

        var captured = new RequestModel();
        state.ApplyTo(captured);
        Assert.Equal(mode, captured.Transport.Revocation);
    }

    [Fact]
    public void Every_RevocationMode_member_is_wired_to_a_combo_index()
    {
        // The exact incident this release fixes: the revocation combo's mapping had to be
        // hand-verified in both directions in v1.64.0 because nothing could test it. Now a fifth
        // member fails loudly instead.
        Assert.Equal(3, Enum.GetValues<RevocationMode>().Length);
    }

    [Fact]
    public void An_out_of_range_revocation_index_falls_back_to_None()
    {
        var state = new RequestEditorState { RevocationIndex = 99 };
        var m = new RequestModel();
        state.ApplyTo(m);
        Assert.Equal(RevocationMode.None, m.Transport.Revocation);
    }

    [Theory]
    [InlineData(HttpVersionMode.Auto, 0)]
    [InlineData(HttpVersionMode.Http11, 1)]
    [InlineData(HttpVersionMode.Http2, 2)]
    [InlineData(HttpVersionMode.Http3, 3)]
    public void HttpVersionMode_round_trips_through_its_combo_index(HttpVersionMode mode, int expectedIndex)
    {
        var model = new RequestModel();
        model.Transport.Version = mode;
        var state = RequestEditorState.From(model);
        Assert.Equal(expectedIndex, state.VersionIndex);

        var captured = new RequestModel();
        state.ApplyTo(captured);
        Assert.Equal(mode, captured.Transport.Version);
    }

    [Fact]
    public void Every_HttpVersionMode_member_is_wired_to_a_combo_index()
    {
        // Grew to 4 when HTTP/3 landed (v1.73.0) — the theory above gained its row and the
        // window's combo gained its item in the same change, which is the whole point of this gate.
        Assert.Equal(4, Enum.GetValues<HttpVersionMode>().Length);
    }

    [Fact]
    public void An_out_of_range_version_index_falls_back_to_Auto()
    {
        var state = new RequestEditorState { VersionIndex = 99 };
        var m = new RequestModel();
        state.ApplyTo(m);
        Assert.Equal(HttpVersionMode.Auto, m.Transport.Version);
    }

    // ---------- (e) auth, all five types, both directions ----------

    [Theory]
    [InlineData("Auto", 0)]
    [InlineData("None", 1)]
    [InlineData("Bearer", 2)]
    [InlineData("Basic", 3)]
    [InlineData("Windows", 4)]
    [InlineData("Digest", 5)]
    [InlineData("AwsSigV4", 6)]
    public void AuthType_round_trips_through_its_combo_index(string authType, int expectedIndex)
    {
        var model = new RequestModel { AuthType = authType };
        var state = RequestEditorState.From(model);
        Assert.Equal(expectedIndex, state.AuthTypeIndex);

        var captured = new RequestModel();
        state.ApplyTo(captured);
        Assert.Equal(authType, captured.AuthType);
    }

    [Fact]
    public void Aws_and_digest_fields_survive_the_editor()
    {
        var aws = new RequestModel { AuthType = "AwsSigV4", AuthUser = "AK", AuthSecret = "SK", AuthScope = "us-east-1/sqs" };
        var captured = new RequestModel();
        RequestEditorState.From(aws).ApplyTo(captured);
        Assert.Equal(("AwsSigV4", "AK", "SK", "us-east-1/sqs"), (captured.AuthType, captured.AuthUser, captured.AuthSecret, captured.AuthScope));

        var digest = new RequestModel { AuthType = "Digest", AuthUser = "ann", AuthSecret = "pw", AuthScope = "stale" };
        captured = new RequestModel();
        RequestEditorState.From(digest).ApplyTo(captured);
        Assert.Equal(("Digest", "ann", "pw", (string?)null), (captured.AuthType, captured.AuthUser, captured.AuthSecret, captured.AuthScope));
    }

    [Fact]
    public void Bearer_auth_users_are_blanked_on_capture_but_the_secret_survives()
    {
        // Deliberately characterised, not endorsed: CaptureControlsInto has always read
        // BasicUserBox.Text for every non-Windows auth type, and LoadIntoControls never puts a
        // Bearer request's AuthUser into any box — so it comes back empty rather than preserved.
        var model = new RequestModel { AuthType = "Bearer", AuthUser = "should-not-survive", AuthSecret = "tok-abc" };
        var state = RequestEditorState.From(model);
        var captured = new RequestModel();
        state.ApplyTo(captured);

        Assert.Equal("", captured.AuthUser);
        Assert.Equal("tok-abc", captured.AuthSecret);
    }

    [Fact]
    public void Windows_single_sign_on_clears_a_stored_secret_on_capture()
    {
        // Deliberately characterised, not endorsed: an empty AuthUser means single sign-on, so
        // WindowsDefaultCheck comes back checked on load — and a checked box blanks both the user
        // and secret boxes on capture, regardless of what secret the model held before.
        var model = new RequestModel { AuthType = "Windows", AuthUser = "", AuthSecret = "leftover-secret" };
        var state = RequestEditorState.From(model);
        var captured = new RequestModel();
        state.ApplyTo(captured);

        Assert.Equal("", captured.AuthUser);
        Assert.Equal("", captured.AuthSecret);
    }

    [Fact]
    public void Windows_auth_with_an_explicit_user_keeps_both_user_and_secret()
    {
        var model = new RequestModel { AuthType = "Windows", AuthUser = "DOMAIN\\bob", AuthSecret = "pw2" };
        var state = RequestEditorState.From(model);
        var captured = new RequestModel();
        state.ApplyTo(captured);

        Assert.Equal("DOMAIN\\bob", captured.AuthUser);
        Assert.Equal("pw2", captured.AuthSecret);
    }

    // ---------- (f) parsing and clamping ----------

    [Theory]
    [InlineData("45", 45)]
    [InlineData("0", 100)]
    [InlineData("-1", 100)]
    [InlineData("abc", 100)]
    [InlineData("", 100)]
    [InlineData("99999", 3600)]
    public void ParseTimeoutSeconds_clamps_and_falls_back(string text, int expected) =>
        Assert.Equal(expected, RequestEditorState.ParseTimeoutSeconds(text));

    [Theory]
    [InlineData("7", 7)]
    [InlineData("0", 20)]
    [InlineData("abc", 20)]
    [InlineData("500", 100)]
    public void ParseMaxRedirects_clamps_and_falls_back(string text, int expected) =>
        Assert.Equal(expected, RequestEditorState.ParseMaxRedirects(text));

    [Theory]
    [InlineData("3", 3)]
    [InlineData("-1", 0)]
    [InlineData("abc", 0)]
    [InlineData("99", 20)]
    public void ParseCount_clamps_a_retry_count_and_falls_back(string text, int expected) =>
        Assert.Equal(expected, RequestEditorState.ParseCount(text, fallback: 0, max: 20));

    [Theory]
    [InlineData("250", 250)]
    [InlineData("abc", 500)]
    [InlineData("999999", 60000)]
    public void ParseCount_clamps_a_retry_delay_and_falls_back(string text, int expected) =>
        Assert.Equal(expected, RequestEditorState.ParseCount(text, fallback: 500, max: 60000));

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("  x  ", "x")]
    public void BlankToNull_blanks_whitespace_to_null_and_trims_a_value(string? input, string? expected) =>
        Assert.Equal(expected, RequestEditorState.BlankToNull(input));

    [Fact]
    public void ApplyTo_uses_the_timeout_clamp()
    {
        // Proves the seam actually calls the helper above, rather than the helper merely existing.
        var state = new RequestEditorState { TimeoutText = "99999" };
        var m = new RequestModel();
        state.ApplyTo(m);
        Assert.Equal(3600, m.TimeoutSeconds);
    }

    [Fact]
    public void ApplyTo_uses_the_max_redirects_clamp()
    {
        var state = new RequestEditorState { MaxRedirectsText = "500" };
        var m = new RequestModel();
        state.ApplyTo(m);
        Assert.Equal(100, m.Transport.MaxRedirects);
    }

    [Fact]
    public void ApplyTo_uses_the_retries_clamp()
    {
        var state = new RequestEditorState { RetriesText = "99" };
        var m = new RequestModel();
        state.ApplyTo(m);
        Assert.Equal(20, m.Transport.Retries);
    }

    [Fact]
    public void ApplyTo_uses_the_retry_delay_clamp()
    {
        var state = new RequestEditorState { RetryDelayText = "999999" };
        var m = new RequestModel();
        state.ApplyTo(m);
        Assert.Equal(60000, m.Transport.RetryDelayMs);
    }

    // ---------- (g) blank-to-null on the proxy fields ----------

    [Fact]
    public void Whitespace_only_proxy_boxes_land_as_null_so_an_untouched_setting_stays_absent()
    {
        var state = new RequestEditorState
        {
            ProxyUrlText = "   ",
            ProxyUserText = "",
            ProxyPassText = "   ",
            NoProxyText = "  "
        };
        var m = new RequestModel();
        state.ApplyTo(m);

        Assert.Null(m.Transport.ProxyUrl);
        Assert.Null(m.Transport.ProxyUser);
        Assert.Null(m.Transport.ProxyPassword);
        Assert.Null(m.Transport.NoProxy);
    }

    [Fact]
    public void Padded_proxy_boxes_are_trimmed()
    {
        var state = new RequestEditorState
        {
            ProxyUrlText = "  http://proxy.corp:8080  ",
            ProxyUserText = "  user  ",
            ProxyPassText = "  pass  ",
            NoProxyText = "  internal.corp  "
        };
        var m = new RequestModel();
        state.ApplyTo(m);

        Assert.Equal("http://proxy.corp:8080", m.Transport.ProxyUrl);
        Assert.Equal("user", m.Transport.ProxyUser);
        Assert.Equal("pass", m.Transport.ProxyPassword);
        Assert.Equal("internal.corp", m.Transport.NoProxy);
    }

    // ---------- (h) retry-on fallback ----------

    [Fact]
    public void Blank_retry_on_falls_back_to_the_default_status_set()
    {
        var state = new RequestEditorState { RetryOnText = "   " };
        var m = new RequestModel();
        state.ApplyTo(m);
        Assert.Equal("429,502,503,504", m.Transport.RetryOn);
    }

    [Fact]
    public void A_real_retry_on_list_is_trimmed_and_kept()
    {
        var state = new RequestEditorState { RetryOnText = "  429,503  " };
        var m = new RequestModel();
        state.ApplyTo(m);
        Assert.Equal("429,503", m.Transport.RetryOn);
    }

    // ---------- (i) the URL box query fold ----------

    [Fact]
    public void A_url_box_query_replaces_query_params_and_leaves_the_bare_path()
    {
        var state = new RequestEditorState { UrlText = "/orders?a=1&b=2" };
        var m = new RequestModel();
        state.ApplyTo(m);

        Assert.Equal("/orders", m.Path);
        Assert.Equal(2, m.QueryParams.Count);
        Assert.Equal("a", m.QueryParams[0].Key);
        Assert.Equal("1", m.QueryParams[0].Value);
        Assert.Equal("b", m.QueryParams[1].Key);
        Assert.Equal("2", m.QueryParams[1].Value);
    }

    [Fact]
    public void A_url_box_with_no_query_leaves_existing_query_params_untouched()
    {
        // The case that matters most: a user's parameter grid must not be silently emptied just
        // because the URL box they typed into happened not to contain a "?".
        var m = new RequestModel();
        var existing = m.QueryParams;
        existing.Add(new ParamRow { Key = "keep", Value = "me" });

        var state = new RequestEditorState { UrlText = "/orders/123" };
        state.ApplyTo(m);

        Assert.Same(existing, m.QueryParams);
        var row = Assert.Single(m.QueryParams);
        Assert.Equal("keep", row.Key);
        Assert.Equal("me", row.Value);
    }

    [Fact]
    public void Leading_and_trailing_whitespace_in_the_url_box_is_trimmed_off_the_path()
    {
        var state = new RequestEditorState { UrlText = "  /orders/123  " };
        var m = new RequestModel();
        state.ApplyTo(m);
        Assert.Equal("/orders/123", m.Path);
    }

    // ---------- (j) the grid-bound collections survive ----------

    [Fact]
    public void ApplyTo_leaves_grid_bound_collections_as_the_same_instances_with_contents_intact()
    {
        // The grids bind these collections as ItemsSource and edit them in place; a round trip that
        // copied them instead would throw the user's in-progress edits away.
        var m = new RequestModel();
        var headers = m.Headers; headers.Add(new HeaderRow { Name = "X-T", Value = "1" });
        var captures = m.Captures; captures.Add(new CaptureRule { Variable = "v", Path = "data.id" });
        var assertions = m.Assertions; assertions.Add(new AssertionRule { Target = AssertTarget.Status, Value = "200" });
        var formParts = m.FormParts; formParts.Add(new FormPart { Name = "file", IsFile = true, Value = "x" });

        new RequestEditorState().ApplyTo(m);

        Assert.Same(headers, m.Headers);
        Assert.Same(captures, m.Captures);
        Assert.Same(assertions, m.Assertions);
        Assert.Same(formParts, m.FormParts);
        Assert.Equal("X-T", Assert.Single(m.Headers).Name);
        Assert.Equal("v", Assert.Single(m.Captures).Variable);
        Assert.Equal("200", Assert.Single(m.Assertions).Value);
        Assert.Equal("file", Assert.Single(m.FormParts).Name);
    }

    // ---------- (k) null-to-empty pins ----------

    [Fact]
    public void A_null_base_url_and_body_come_back_as_empty_strings()
    {
        // Today's behavior, pinned rather than fixed: LoadIntoControls does `m.BaseUrl ?? ""` /
        // `m.Body ?? ""`, and CaptureControlsInto writes the box back verbatim — so a null becomes
        // "" rather than staying null.
        var model = new RequestModel { BaseUrl = null, Body = null };
        var captured = new RequestModel();
        RequestEditorState.From(model).ApplyTo(captured);

        Assert.Equal("", captured.BaseUrl);
        Assert.Equal("", captured.Body);
    }

    // ---------- (l) WPF containment ----------

    [Fact]
    public void RequestEditorState_lives_in_ApiTester_App_and_carries_no_WPF_types()
    {
        // This is what keeps the seam testable without opening a window: if a future edit lets a
        // WPF type (Visibility, a control type, anything from PresentationFramework/Core or
        // WindowsBase) leak onto a public property, this fails instead of quietly requiring an STA
        // thread and a live Window to run the round-trip tests above.
        var asm = typeof(RequestEditorState).Assembly;
        Assert.Equal("ApiTester.App", asm.GetName().Name);

        var forbidden = new[] { "PresentationFramework", "PresentationCore", "WindowsBase" };
        foreach (var prop in typeof(RequestEditorState).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var propAssemblyName = prop.PropertyType.Assembly.GetName().Name;
            Assert.DoesNotContain(propAssemblyName, forbidden);
        }
    }
}
