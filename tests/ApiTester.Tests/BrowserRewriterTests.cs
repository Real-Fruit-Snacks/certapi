using ApiTester.Core;

namespace ApiTester.Tests;

public class BrowserRewriterTests
{
    private const string Local = "http://127.0.0.1:8080";

    private static BrowserOptions Options(
        bool cors = false,
        IReadOnlyList<string>? allowedOrigins = null,
        bool rewriteCookies = false,
        bool rewriteLocation = false,
        bool allowUpgrade = false,
        string localOrigin = Local,
        bool secureOrigin = false,
        int corsMaxAge = 600) =>
        new(cors, allowedOrigins, rewriteCookies, rewriteLocation, allowUpgrade, new Uri(localOrigin))
        { SecureOrigin = secureOrigin, CorsMaxAgeSeconds = corsMaxAge };

    private static GatewayRequest Request(string method, params (string Name, string Value)[] headers) =>
        new(method, "/orders",
            headers.Select(h => new KeyValuePair<string, string>(h.Name, h.Value)).ToList(),
            null, null);

    private static GatewayRoute Route(string prefix = "/", string upstream = "https://api.internal/") =>
        new(prefix, new Uri(upstream));

    private static IReadOnlyList<KeyValuePair<string, string>> Headers(
        params (string Name, string Value)[] headers) =>
        headers.Select(h => new KeyValuePair<string, string>(h.Name, h.Value)).ToList();

    private static IReadOnlyList<string> Values(
        IReadOnlyList<KeyValuePair<string, string>> headers, string name) =>
        headers.Where(h => h.Key.Equals(name, StringComparison.OrdinalIgnoreCase))
               .Select(h => h.Value).ToList();

    private static string? Value(IReadOnlyList<KeyValuePair<string, string>> headers, string name) =>
        Values(headers, name).SingleOrDefault();

    [Fact]
    public void Preflight_is_answered_locally_with_204_and_the_exact_origin_echoed()
    {
        var request = Request("OPTIONS",
            ("Origin", "https://app.example"),
            ("Access-Control-Request-Method", "PUT"),
            ("Access-Control-Request-Headers", "authorization, x-trace"));

        var response = BrowserRewriter.TryPreflight(request, Options(cors: true));

        Assert.NotNull(response);
        Assert.Equal(204, response!.StatusCode);
        Assert.Equal("https://app.example", Value(response.Headers, "Access-Control-Allow-Origin"));
        Assert.Equal("PUT", Value(response.Headers, "Access-Control-Allow-Methods"));
        Assert.Equal("authorization, x-trace", Value(response.Headers, "Access-Control-Allow-Headers"));
        Assert.Equal("true", Value(response.Headers, "Access-Control-Allow-Credentials"));
        Assert.Equal("600", Value(response.Headers, "Access-Control-Max-Age"));
        Assert.Equal("Origin", Value(response.Headers, "Vary"));
    }

    [Fact]
    public void A_preflight_that_asks_for_private_network_access_in_echo_mode_is_granted_it()
    {
        var request = Request("OPTIONS",
            ("Origin", "https://app.example"),
            ("Access-Control-Request-Method", "PUT"),
            ("Access-Control-Request-Private-Network", "true"));

        var response = BrowserRewriter.TryPreflight(request, Options(cors: true));

        Assert.NotNull(response);
        Assert.Equal(204, response!.StatusCode);
        Assert.Equal("https://app.example", Value(response.Headers, "Access-Control-Allow-Origin"));
        Assert.Equal("PUT", Value(response.Headers, "Access-Control-Allow-Methods"));
        Assert.Equal("true", Value(response.Headers, "Access-Control-Allow-Credentials"));
        Assert.Equal("true", Value(response.Headers, "Access-Control-Allow-Private-Network"));
    }

    [Fact]
    public void A_private_network_request_from_an_origin_inside_an_explicit_allowlist_is_granted_it()
    {
        var request = Request("OPTIONS",
            ("Origin", "https://app.example"),
            ("Access-Control-Request-Method", "PUT"),
            ("Access-Control-Request-Private-Network", "true"));

        var response = BrowserRewriter.TryPreflight(
            request, Options(cors: true, allowedOrigins: new[] { "https://app.example" }));

        Assert.NotNull(response);
        Assert.Equal(204, response!.StatusCode);
        Assert.Equal("true", Value(response.Headers, "Access-Control-Allow-Private-Network"));
    }

    [Fact]
    public void A_private_network_request_from_an_origin_outside_an_explicit_allowlist_is_refused_with_no_headers()
    {
        var request = Request("OPTIONS",
            ("Origin", "https://evil.example"),
            ("Access-Control-Request-Method", "PUT"),
            ("Access-Control-Request-Private-Network", "true"));

        var response = BrowserRewriter.TryPreflight(
            request, Options(cors: true, allowedOrigins: new[] { "https://app.example" }));

        Assert.NotNull(response);
        Assert.Equal(403, response!.StatusCode);
        Assert.Empty(response.Headers);
        Assert.Null(Value(response.Headers, "Access-Control-Allow-Private-Network"));
    }

    [Fact]
    public void An_origin_matching_one_entry_of_a_multi_entry_allowlist_is_allowed()
    {
        // Two entries where only one matches: the shape that distinguishes any-entry-matches
        // from every-entry-matches.
        var request = Request("OPTIONS",
            ("Origin", "https://second.example"),
            ("Access-Control-Request-Method", "PUT"));

        var response = BrowserRewriter.TryPreflight(request,
            Options(cors: true, allowedOrigins: new[] { "https://first.example", "https://second.example" }));

        Assert.NotNull(response);
        Assert.Equal(204, response!.StatusCode);
        Assert.Equal("https://second.example", Value(response.Headers, "Access-Control-Allow-Origin"));
    }

    [Fact]
    public void An_empty_allowlist_allows_no_origin_at_all()
    {
        // Explicitly empty is not null: null means "echo whatever asked", empty means the
        // operator listed nothing, so nothing may pass.
        var request = Request("OPTIONS",
            ("Origin", "https://app.example"),
            ("Access-Control-Request-Method", "PUT"));

        var response = BrowserRewriter.TryPreflight(request,
            Options(cors: true, allowedOrigins: Array.Empty<string>()));

        Assert.NotNull(response);
        Assert.Equal(403, response!.StatusCode);
        Assert.Empty(response.Headers);
    }

    [Fact]
    public void A_preflight_that_does_not_ask_for_private_network_access_gets_no_such_header()
    {
        var request = Request("OPTIONS",
            ("Origin", "https://app.example"),
            ("Access-Control-Request-Method", "PUT"));

        var response = BrowserRewriter.TryPreflight(request, Options(cors: true));

        Assert.NotNull(response);
        Assert.Null(Value(response!.Headers, "Access-Control-Allow-Private-Network"));
    }

    [Theory]
    [InlineData("false")]
    [InlineData("")]
    [InlineData("1")]
    public void A_private_network_request_header_with_any_value_other_than_true_grants_nothing(
        string value)
    {
        var request = Request("OPTIONS",
            ("Origin", "https://app.example"),
            ("Access-Control-Request-Method", "PUT"),
            ("Access-Control-Request-Private-Network", value));

        var response = BrowserRewriter.TryPreflight(request, Options(cors: true));

        Assert.NotNull(response);
        Assert.Null(Value(response!.Headers, "Access-Control-Allow-Private-Network"));
    }

    [Theory]
    [InlineData("access-control-request-private-network", "TRUE")]
    [InlineData("Access-Control-Request-Private-Network", " true ")]
    public void The_private_network_request_header_is_matched_case_insensitively_by_name_and_value(
        string headerName, string headerValue)
    {
        var request = Request("OPTIONS",
            ("Origin", "https://app.example"),
            ("Access-Control-Request-Method", "PUT"),
            (headerName, headerValue));

        var response = BrowserRewriter.TryPreflight(request, Options(cors: true));

        Assert.NotNull(response);
        Assert.Equal("true", Value(response!.Headers, "Access-Control-Allow-Private-Network"));
    }

    [Fact]
    public void A_private_network_preflight_is_still_forwarded_when_cors_handling_is_off()
    {
        var request = Request("OPTIONS",
            ("Origin", "https://app.example"),
            ("Access-Control-Request-Method", "PUT"),
            ("Access-Control-Request-Private-Network", "true"));

        Assert.Null(BrowserRewriter.TryPreflight(request, Options(cors: false)));
    }

    [Theory]
    [InlineData(3600, "3600")]
    [InlineData(0, "0")]
    public void Access_control_max_age_reflects_the_configured_value(int seconds, string expected)
    {
        var request = Request("OPTIONS",
            ("Origin", "https://app.example"),
            ("Access-Control-Request-Method", "PUT"));

        var response = BrowserRewriter.TryPreflight(
            request, Options(cors: true, corsMaxAge: seconds));

        Assert.NotNull(response);
        Assert.Equal(expected, Value(response!.Headers, "Access-Control-Max-Age"));
    }

    [Fact]
    public void Preflight_omits_allow_headers_when_the_request_asked_for_none()
    {
        var request = Request("OPTIONS",
            ("Origin", "https://app.example"),
            ("Access-Control-Request-Method", "GET"));

        var response = BrowserRewriter.TryPreflight(request, Options(cors: true));

        Assert.NotNull(response);
        Assert.Equal(204, response!.StatusCode);
        Assert.Empty(Values(response.Headers, "Access-Control-Allow-Headers"));
    }

    [Theory]
    [InlineData("GET", "https://app.example", "PUT")]     // not an OPTIONS at all
    [InlineData("OPTIONS", null, "PUT")]                  // no Origin: not a cross-origin request
    [InlineData("OPTIONS", "https://app.example", null)]  // a plain OPTIONS the upstream should answer
    [InlineData("OPTIONS", null, null)]
    public void A_request_that_is_not_a_preflight_is_left_to_be_forwarded(
        string method, string? origin, string? requestMethod)
    {
        var headers = new List<(string, string)>();
        if (origin is not null) headers.Add(("Origin", origin));
        if (requestMethod is not null) headers.Add(("Access-Control-Request-Method", requestMethod));

        Assert.Null(BrowserRewriter.TryPreflight(
            Request(method, headers.ToArray()), Options(cors: true)));
    }

    [Fact]
    public void A_preflight_is_forwarded_untouched_when_cors_handling_is_off()
    {
        var request = Request("OPTIONS",
            ("Origin", "https://app.example"),
            ("Access-Control-Request-Method", "PUT"));

        Assert.Null(BrowserRewriter.TryPreflight(request, Options(cors: false)));
    }

    [Fact]
    public void A_preflight_from_an_origin_outside_the_allowlist_is_refused_rather_than_forwarded()
    {
        var request = Request("OPTIONS",
            ("Origin", "https://evil.example"),
            ("Access-Control-Request-Method", "POST"));

        var response = BrowserRewriter.TryPreflight(
            request, Options(cors: true, allowedOrigins: new[] { "https://app.example" }));

        Assert.NotNull(response);
        Assert.Equal(403, response!.StatusCode);
        Assert.Empty(response.Headers);
    }

    [Theory]
    [InlineData("https://app.example")]
    [InlineData("https://APP.example")]   // an origin comparison is case-insensitive
    public void A_preflight_from_an_origin_inside_the_allowlist_is_answered(string origin)
    {
        var request = Request("OPTIONS",
            ("Origin", origin),
            ("Access-Control-Request-Method", "POST"));

        var response = BrowserRewriter.TryPreflight(
            request, Options(cors: true, allowedOrigins: new[] { "https://app.example" }));

        Assert.NotNull(response);
        Assert.Equal(204, response!.StatusCode);
        Assert.Equal(origin, Value(response.Headers, "Access-Control-Allow-Origin"));
    }

    [Fact]
    public void An_upstreams_own_cors_headers_are_replaced_by_exactly_one_allow_origin()
    {
        var upstream = Headers(
            ("Content-Type", "application/json"),
            ("Access-Control-Allow-Origin", "https://someone-else.example"),
            ("Access-Control-Allow-Methods", "GET, POST"),
            ("access-control-allow-credentials", "false"));

        var result = BrowserRewriter.RewriteResponseHeaders(
            upstream, "https://app.example", Route(), Options(cors: true), out var warnings);

        // A duplicate Access-Control-Allow-Origin is a hard browser failure, so the count matters
        // as much as the value.
        Assert.Equal(new[] { "https://app.example" }, Values(result, "Access-Control-Allow-Origin"));
        Assert.Equal(new[] { "true" }, Values(result, "Access-Control-Allow-Credentials"));
        Assert.Empty(Values(result, "Access-Control-Allow-Methods"));
        Assert.Equal("Origin", Value(result, "Vary"));
        Assert.Equal("application/json", Value(result, "Content-Type"));
        Assert.Empty(warnings);
    }

    [Fact]
    public void Expose_headers_names_the_non_safelisted_response_headers_instead_of_a_wildcard()
    {
        var upstream = Headers(
            ("Content-Type", "application/json"),
            ("X-Request-Id", "abc"),
            ("Cache-Control", "no-store"),
            ("ETag", "\"v1\""),
            ("x-request-id", "def"));   // a repeated header is still one name

        var result = BrowserRewriter.RewriteResponseHeaders(
            upstream, "https://app.example", Route(), Options(cors: true), out _);

        // A wildcard is not honored under Access-Control-Allow-Credentials: true, so the names are
        // spelled out — in the order they appear, de-duplicated case-insensitively.
        Assert.Equal("X-Request-Id, ETag", Value(result, "Access-Control-Expose-Headers"));
    }

    [Theory]
    [InlineData("Cache-Control", "no-store")]
    [InlineData("Content-Language", "en")]
    [InlineData("Content-Length", "12")]
    [InlineData("Content-Type", "text/plain")]
    [InlineData("Expires", "0")]
    [InlineData("Last-Modified", "Wed, 21 Oct 2015 07:28:00 GMT")]
    [InlineData("Pragma", "no-cache")]
    public void Expose_headers_is_omitted_when_only_safelisted_headers_came_back(
        string name, string value)
    {
        var result = BrowserRewriter.RewriteResponseHeaders(
            Headers((name, value)), "https://app.example", Route(), Options(cors: true), out _);

        Assert.Empty(Values(result, "Access-Control-Expose-Headers"));
    }

    [Fact]
    public void A_disallowed_origin_gets_no_cors_headers_at_all_and_is_warned_about()
    {
        var upstream = Headers(
            ("Content-Type", "application/json"),
            ("Access-Control-Allow-Origin", "https://evil.example"));

        var result = BrowserRewriter.RewriteResponseHeaders(
            upstream, "https://evil.example", Route(),
            Options(cors: true, allowedOrigins: new[] { "https://app.example" }), out var warnings);

        // The upstream's own policy is stripped even though ours is not injected: the browser must
        // not be handed a permission the operator did not grant.
        Assert.Empty(Values(result, "Access-Control-Allow-Origin"));
        Assert.Empty(Values(result, "Access-Control-Allow-Credentials"));
        Assert.Equal(new[] { new KeyValuePair<string, string>("Content-Type", "application/json") }, result);
        Assert.Contains(warnings, w => w.Contains("https://evil.example"));
    }

    [Fact]
    public void A_same_origin_response_with_no_origin_header_is_neither_injected_into_nor_warned_about()
    {
        var result = BrowserRewriter.RewriteResponseHeaders(
            Headers(("Content-Type", "application/json")), null, Route(),
            Options(cors: true), out var warnings);

        Assert.Equal(new[] { new KeyValuePair<string, string>("Content-Type", "application/json") }, result);
        Assert.Empty(warnings);
    }

    [Fact]
    public void Upstream_cors_headers_are_stripped_even_when_nothing_is_injected_in_their_place()
    {
        var upstream = Headers(
            ("Access-Control-Allow-Origin", "*"),
            ("Access-Control-Expose-Headers", "*"),
            ("Content-Type", "application/json"));

        var result = BrowserRewriter.RewriteResponseHeaders(
            upstream, null, Route(), Options(cors: true), out _);

        // The gateway owns the CORS policy once it is turned on; the upstream's is never relayed.
        Assert.Equal(new[] { new KeyValuePair<string, string>("Content-Type", "application/json") }, result);
    }

    [Fact]
    public void Upstream_cors_headers_are_left_alone_when_cors_handling_is_off()
    {
        var upstream = Headers(
            ("Access-Control-Allow-Origin", "https://someone-else.example"),
            ("Access-Control-Max-Age", "60"),
            ("Content-Type", "application/json"));

        var result = BrowserRewriter.RewriteResponseHeaders(
            upstream, "https://app.example", Route(), Options(cors: false), out var warnings);

        Assert.Equal(upstream, result);
        Assert.Empty(warnings);
    }

    [Theory]
    // Domain= would scope the cookie to the upstream host the browser is not talking to.
    [InlineData("id=7; Domain=api.internal; Path=/", "id=7; Path=/")]
    // Secure over http://127.0.0.1 means the browser stores nothing at all.
    [InlineData("id=7; Path=/; Secure", "id=7; Path=/")]
    [InlineData("id=7; secure; HttpOnly", "id=7; HttpOnly")]
    // SameSite=None without Secure is rejected outright, so it is downgraded rather than dropped.
    [InlineData("id=7; Path=/; SameSite=None; Secure", "id=7; Path=/; SameSite=Lax")]
    [InlineData("id=7; samesite=none", "id=7; SameSite=Lax")]
    [InlineData("id=7; SameSite=Strict", "id=7; SameSite=Strict")]
    [InlineData("id=7; SameSite=Lax", "id=7; SameSite=Lax")]
    // Everything else survives, in the order it arrived — an Expires date carries a comma, and a
    // cookie value may carry '=', neither of which may confuse the parse.
    [InlineData("id=7; Path=/app; Max-Age=3600; HttpOnly", "id=7; Path=/app; Max-Age=3600; HttpOnly")]
    [InlineData("id=7; Expires=Wed, 21 Oct 2015 07:28:00 GMT; HttpOnly",
                "id=7; Expires=Wed, 21 Oct 2015 07:28:00 GMT; HttpOnly")]
    [InlineData("token=YWJj=; Domain=api.internal; Secure; HttpOnly", "token=YWJj=; HttpOnly")]
    [InlineData("state=a=b=c; Path=/", "state=a=b=c; Path=/")]
    public void Cookie_attributes_are_rewritten_for_a_plaintext_loopback_origin(
        string upstream, string expected)
    {
        var result = BrowserRewriter.RewriteResponseHeaders(
            Headers(("Set-Cookie", upstream)), null, Route(),
            Options(rewriteCookies: true), out var warnings);

        Assert.Equal(expected, Value(result, "Set-Cookie"));
        Assert.Empty(warnings);
    }

    [Fact]
    public void Every_set_cookie_header_is_rewritten_and_they_stay_separate_headers()
    {
        var upstream = Headers(
            ("Set-Cookie", "session=abc; Domain=api.internal; Secure; HttpOnly"),
            ("Content-Type", "application/json"),
            ("Set-Cookie", "csrf=xyz; Path=/; SameSite=None; Secure"));

        var result = BrowserRewriter.RewriteResponseHeaders(
            upstream, null, Route(), Options(rewriteCookies: true), out _);

        Assert.Equal(
            new[] { "session=abc; HttpOnly", "csrf=xyz; Path=/; SameSite=Lax" },
            Values(result, "Set-Cookie"));
        Assert.Equal("application/json", Value(result, "Content-Type"));
    }

    [Theory]
    [InlineData("__Host-session=abc; Path=/; Secure", "__Host-session=abc; Path=/", "__Host-session")]
    [InlineData("__Secure-token=abc; Domain=api.internal; Secure", "__Secure-token=abc", "__Secure-token")]
    public void A_prefixed_cookie_is_still_emitted_and_its_limitation_warned_about(
        string upstream, string expected, string name)
    {
        var result = BrowserRewriter.RewriteResponseHeaders(
            Headers(("Set-Cookie", upstream)), null, Route(),
            Options(rewriteCookies: true), out var warnings);

        // The prefix requires Secure by specification, so this cookie cannot work over a plaintext
        // loopback origin. Dropping it silently would look like an unrelated bug to the operator.
        Assert.Equal(expected, Value(result, "Set-Cookie"));
        Assert.Contains(warnings, w => w.Contains(name));
    }

    [Fact]
    public void Empty_cookie_attributes_from_doubled_or_trailing_semicolons_are_dropped()
    {
        var result = BrowserRewriter.RewriteResponseHeaders(
            Headers(("Set-Cookie", "id=7;; Path=/;")), null, Route(),
            Options(rewriteCookies: true), out var warnings);

        Assert.Equal("id=7; Path=/", Value(result, "Set-Cookie"));
        Assert.Empty(warnings);
    }

    [Fact]
    public void A_degenerate_cookie_with_no_equals_sign_passes_through_unharmed()
    {
        // Not a legal Set-Cookie, but a server can send one; the rewriter must relay it, not crash.
        var result = BrowserRewriter.RewriteResponseHeaders(
            Headers(("Set-Cookie", "flag; Path=/")), null, Route(),
            Options(rewriteCookies: true), out var warnings);

        Assert.Equal("flag; Path=/", Value(result, "Set-Cookie"));
        Assert.Empty(warnings);
    }

    [Fact]
    public void An_ordinary_cookie_produces_no_prefix_warning()
    {
        BrowserRewriter.RewriteResponseHeaders(
            Headers(("Set-Cookie", "__Hostess=abc; Secure"), ("Set-Cookie", "session=abc; Secure")),
            null, Route(), Options(rewriteCookies: true), out var warnings);

        Assert.Empty(warnings);
    }

    [Fact]
    public void Set_cookie_passes_through_untouched_when_cookie_rewriting_is_off()
    {
        var upstream = Headers(
            ("Set-Cookie", "__Host-session=abc; Domain=api.internal; Secure; SameSite=None"),
            ("Set-Cookie", "csrf=xyz; Secure"));

        var result = BrowserRewriter.RewriteResponseHeaders(
            upstream, null, Route(), Options(rewriteCookies: false), out var warnings);

        Assert.Equal(upstream, result);
        Assert.Empty(warnings);
    }

    [Fact]
    public void A_redirect_to_the_matched_upstream_is_pointed_back_at_the_gateway()
    {
        var result = BrowserRewriter.RewriteResponseHeaders(
            Headers(("Location", "https://api.internal/orders/7?view=full")), null,
            Route("/api", "https://api.internal/"), Options(rewriteLocation: true), out var warnings);

        Assert.Equal("http://127.0.0.1:8080/api/orders/7?view=full", Value(result, "Location"));
        Assert.Empty(warnings);
    }

    [Theory]
    [InlineData("/orders/7")]
    [InlineData("orders/7?view=full")]
    public void A_relative_redirect_is_left_alone_because_it_already_resolves_locally(string location)
    {
        var result = BrowserRewriter.RewriteResponseHeaders(
            Headers(("Location", location)), null,
            Route("/api", "https://api.internal/"), Options(rewriteLocation: true), out var warnings);

        Assert.Equal(location, Value(result, "Location"));
        Assert.Empty(warnings);
    }

    [Theory]
    [InlineData("https://login.internal/sso?next=/orders")]  // a different host
    [InlineData("http://api.internal/orders")]               // the same host, a different scheme
    [InlineData("https://api.internal:8443/orders")]         // the same host, a different port
    public void A_redirect_off_the_matched_upstream_is_left_alone_and_warned_about(string location)
    {
        var result = BrowserRewriter.RewriteResponseHeaders(
            Headers(("Location", location)), null,
            Route("/api", "https://api.internal/"), Options(rewriteLocation: true), out var warnings);

        // Following it takes the browser off the gateway, and the client certificate with it.
        Assert.Equal(location, Value(result, "Location"));
        Assert.Contains(warnings, w => w.Contains(location));
    }

    [Fact]
    public void A_protocol_relative_redirect_to_the_matched_upstream_is_pointed_back_at_the_gateway()
    {
        var result = BrowserRewriter.RewriteResponseHeaders(
            Headers(("Location", "//api.internal/orders/7?view=full")), null,
            Route("/api", "https://api.internal/"), Options(rewriteLocation: true), out var warnings);

        // The same destination as the absolute form, merely written without a scheme: it is the
        // route's own upstream, so it is rewritten, and warning about a departure would be wrong.
        Assert.Equal("http://127.0.0.1:8080/api/orders/7?view=full", Value(result, "Location"));
        Assert.Empty(warnings);
    }

    [Theory]
    [InlineData("//evil.example/sso?next=/orders")]  // a different host
    [InlineData("//api.internal:8443/orders")]       // the same host, a different port
    public void A_protocol_relative_redirect_off_the_matched_upstream_is_left_alone_and_warned_about(
        string location)
    {
        var result = BrowserRewriter.RewriteResponseHeaders(
            Headers(("Location", location)), null,
            Route("/api", "https://api.internal/"), Options(rewriteLocation: true), out var warnings);

        // The browser resolves this against the gateway's own origin into an authority of its own,
        // so it leaves the gateway — and the client certificate — just as an absolute URI would.
        Assert.Equal(location, Value(result, "Location"));
        Assert.Contains(warnings, w => w.Contains(location));
    }

    [Theory]
    [InlineData("http://127.0.0.1:8080")]
    [InlineData("http://127.0.0.1:8080/")]   // however the local origin is spelled
    public void A_root_mounted_upstream_does_not_double_the_slash_in_a_rewritten_redirect(string local)
    {
        var result = BrowserRewriter.RewriteResponseHeaders(
            Headers(("Location", "https://api.internal/orders?page=2")), null,
            Route("/", "https://api.internal/"),
            Options(rewriteLocation: true, localOrigin: local), out _);

        Assert.Equal("http://127.0.0.1:8080/orders?page=2", Value(result, "Location"));
    }

    [Fact]
    public void Location_passes_through_untouched_when_location_rewriting_is_off()
    {
        var upstream = Headers(("Location", "https://api.internal/orders/7"));

        var result = BrowserRewriter.RewriteResponseHeaders(
            upstream, null, Route("/api", "https://api.internal/"),
            Options(rewriteLocation: false), out var warnings);

        Assert.Equal(upstream, result);
        Assert.Empty(warnings);
    }

    [Fact]
    public void Over_a_secure_origin_a_prefixed_cookie_keeps_secure_and_samesite_none_and_loses_only_domain()
    {
        var result = BrowserRewriter.RewriteResponseHeaders(
            Headers(("Set-Cookie",
                "__Host-sid=abc; Path=/; Secure; HttpOnly; SameSite=None; Domain=api.internal")),
            null, Route(), Options(rewriteCookies: true, secureOrigin: true), out var warnings);

        Assert.Equal("__Host-sid=abc; Path=/; Secure; HttpOnly; SameSite=None", Value(result, "Set-Cookie"));
        Assert.Empty(warnings);
    }

    [Fact]
    public void Over_a_plaintext_origin_the_same_prefixed_cookie_is_still_warned_about()
    {
        BrowserRewriter.RewriteResponseHeaders(
            Headers(("Set-Cookie",
                "__Host-sid=abc; Path=/; Secure; HttpOnly; SameSite=None; Domain=api.internal")),
            null, Route(), Options(rewriteCookies: true, secureOrigin: false), out var warnings);

        Assert.Contains(warnings, w => w.Contains("__Host-sid"));
    }

    [Fact]
    public void Over_a_secure_origin_an_ordinary_cookie_keeps_secure()
    {
        var result = BrowserRewriter.RewriteResponseHeaders(
            Headers(("Set-Cookie", "sid=1; Secure")), null, Route(),
            Options(rewriteCookies: true, secureOrigin: true), out var warnings);

        Assert.Equal("sid=1; Secure", Value(result, "Set-Cookie"));
        Assert.Empty(warnings);
    }

    [Fact]
    public void Over_a_secure_origin_a_rewritten_redirect_targets_the_https_local_origin()
    {
        var result = BrowserRewriter.RewriteResponseHeaders(
            Headers(("Location", "https://api.internal/orders/7?view=full")), null,
            Route("/api", "https://api.internal/"),
            Options(rewriteLocation: true, localOrigin: "https://127.0.0.1:8443", secureOrigin: true),
            out var warnings);

        Assert.Equal("https://127.0.0.1:8443/api/orders/7?view=full", Value(result, "Location"));
        Assert.Empty(warnings);
    }

    [Fact]
    public void The_whole_browser_bundle_applies_to_one_response_without_disturbing_the_input()
    {
        var upstream = Headers(
            ("Content-Type", "application/json"),
            ("Access-Control-Allow-Origin", "https://api.internal"),
            ("Set-Cookie", "__Host-session=abc; Domain=api.internal; Path=/; Secure; SameSite=None"),
            ("Location", "https://api.internal/orders/7"));
        var before = upstream.ToList();

        var result = BrowserRewriter.RewriteResponseHeaders(
            upstream, "https://app.example", Route("/api", "https://api.internal/"),
            Options(cors: true, rewriteCookies: true, rewriteLocation: true), out var warnings);

        Assert.Equal(new[] { "https://app.example" }, Values(result, "Access-Control-Allow-Origin"));
        Assert.Equal("Set-Cookie, Location", Value(result, "Access-Control-Expose-Headers"));
        // Path re-rooted under the /api mount, so the cookie comes back for that upstream only.
        Assert.Equal("__Host-session=abc; Path=/api; SameSite=Lax", Value(result, "Set-Cookie"));
        Assert.Equal("http://127.0.0.1:8080/api/orders/7", Value(result, "Location"));
        Assert.Contains(warnings, w => w.Contains("__Host-session"));
        Assert.Equal(before, upstream);   // the caller's list is never rewritten in place
    }
}
