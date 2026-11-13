using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography.X509Certificates;
using ApiTester.Cli;
using ApiTester.Core;
using static ApiTester.Tests.Cli.ServeFixture;

namespace ApiTester.Tests.Cli;

/// <summary>End-to-end coverage of `certapi serve`'s four header-rule flags, through a real listener
/// talking to real loopback mTLS upstreams. A request-rule assertion is on what the upstream's echo
/// reports it received; a response-rule assertion is on what the client actually received.</summary>
public class ServeHeaderRulesTests
{
    [Fact]
    public async Task A_request_header_reaches_the_upstream_with_no_browser_flags_at_all()
    {
        var (ca, server, client) = Certs();
        using (ca) using (server) using (client)
        {
            await using var api = await LoopbackMtlsServer.StartEchoAsync(server, client.Thumbprint!);
            await using var serve = ServeHost.Start(client,
                "--upstream", $"/={api.BaseUrl}", "--request-header", "X-Api-Key: s3cret");

            using var http = NewClient();
            string echoed = await Poll(() => http.GetStringAsync($"{serve.Origin}/orders"));

            // The echo body is the raw request the upstream received: this is what proves the header
            // actually reached the upstream, not merely that the gateway composed it.
            Assert.Contains("X-Api-Key: s3cret", echoed);
        }
    }

    [Fact]
    public async Task A_request_header_rule_overrides_the_value_the_caller_sent()
    {
        var (ca, server, client) = Certs();
        using (ca) using (server) using (client)
        {
            await using var api = await LoopbackMtlsServer.StartEchoAsync(server, client.Thumbprint!);
            await using var serve = ServeHost.Start(client,
                "--upstream", $"/={api.BaseUrl}", "--request-header", "X-Tenant: gold");

            using var http = NewClient();
            async Task<string> Send()
            {
                var req = new HttpRequestMessage(HttpMethod.Get, $"{serve.Origin}/orders");
                req.Headers.Add("X-Tenant", "bronze");
                var resp = await http.SendAsync(req);
                return await resp.Content.ReadAsStringAsync();
            }
            string echoed = await Poll(Send);

            Assert.Contains("X-Tenant: gold", echoed);
            Assert.DoesNotContain("bronze", echoed);
        }
    }

    [Fact]
    public async Task A_remove_request_header_rule_strips_a_header_the_caller_sent()
    {
        var (ca, server, client) = Certs();
        using (ca) using (server) using (client)
        {
            await using var api = await LoopbackMtlsServer.StartEchoAsync(server, client.Thumbprint!);
            await using var serve = ServeHost.Start(client,
                "--upstream", $"/={api.BaseUrl}", "--remove-request-header", "X-Debug");

            using var http = NewClient();
            async Task<string> Send()
            {
                var req = new HttpRequestMessage(HttpMethod.Get, $"{serve.Origin}/orders");
                req.Headers.Add("X-Debug", "1");
                var resp = await http.SendAsync(req);
                return await resp.Content.ReadAsStringAsync();
            }
            string echoed = await Poll(Send);

            Assert.DoesNotContain("X-Debug", echoed);
        }
    }

    [Fact]
    public async Task Removing_a_request_header_wins_over_setting_the_same_name()
    {
        var (ca, server, client) = Certs();
        using (ca) using (server) using (client)
        {
            await using var api = await LoopbackMtlsServer.StartEchoAsync(server, client.Thumbprint!);
            await using var serve = ServeHost.Start(client,
                "--upstream", $"/={api.BaseUrl}",
                "--request-header", "X-Both: v", "--remove-request-header", "X-Both");

            using var http = NewClient();
            string echoed = await Poll(() => http.GetStringAsync($"{serve.Origin}/orders"));

            Assert.DoesNotContain("X-Both", echoed);
        }
    }

    [Fact]
    public async Task Two_request_header_flags_both_reach_the_upstream()
    {
        var (ca, server, client) = Certs();
        using (ca) using (server) using (client)
        {
            await using var api = await LoopbackMtlsServer.StartEchoAsync(server, client.Thumbprint!);
            await using var serve = ServeHost.Start(client,
                "--upstream", $"/={api.BaseUrl}",
                "--request-header", "X-One: 1", "--request-header", "X-Two: 2");

            using var http = NewClient();
            string echoed = await Poll(() => http.GetStringAsync($"{serve.Origin}/orders"));

            Assert.Contains("X-One: 1", echoed);
            Assert.Contains("X-Two: 2", echoed);
        }
    }

    [Fact]
    public async Task A_response_header_rule_adds_a_header_the_client_receives_with_no_browser_flags()
    {
        var (ca, server, client) = Certs();
        using (ca) using (server) using (client)
        {
            await using var api = await LoopbackMtlsServer.StartWithHeadersAsync(
                server, client.Thumbprint!, Array.Empty<KeyValuePair<string, string>>(),
                responseBody: "ok", responseContentType: "text/plain");
            await using var serve = ServeHost.Start(client,
                "--upstream", $"/={api.BaseUrl}", "--response-header", "X-Gateway: certapi");

            using var http = NewClient();
            var resp = await Poll(() => http.GetAsync($"{serve.Origin}/orders"));

            Assert.Equal(new[] { "certapi" }, HeaderValues(resp, "X-Gateway"));
        }
    }

    [Fact]
    public async Task A_remove_response_header_rule_strips_a_header_the_upstream_sent()
    {
        var (ca, server, client) = Certs();
        using (ca) using (server) using (client)
        {
            await using var api = await LoopbackMtlsServer.StartWithHeadersAsync(
                server, client.Thumbprint!,
                new[]
                {
                    new KeyValuePair<string, string>("X-Upstream", "yes"),
                    new KeyValuePair<string, string>("X-Keep", "present")
                },
                responseBody: "ok", responseContentType: "text/plain");
            await using var serve = ServeHost.Start(client,
                "--upstream", $"/={api.BaseUrl}", "--remove-response-header", "X-Upstream");

            using var http = NewClient();
            var resp = await Poll(() => http.GetAsync($"{serve.Origin}/orders"));

            Assert.Empty(HeaderValues(resp, "X-Upstream"));
            Assert.Equal(new[] { "present" }, HeaderValues(resp, "X-Keep"));
        }
    }

    [Fact]
    public async Task A_response_header_rule_applies_after_the_browser_cors_rewrite_so_the_explicit_value_wins()
    {
        var (ca, server, client) = Certs();
        using (ca) using (server) using (client)
        {
            await using var api = await LoopbackMtlsServer.StartWithHeadersAsync(
                server, client.Thumbprint!, Array.Empty<KeyValuePair<string, string>>(),
                responseBody: "ok", responseContentType: "text/plain");
            await using var serve = ServeHost.Start(client,
                "--upstream", $"/={api.BaseUrl}", "--cors",
                "--response-header", "Access-Control-Allow-Origin: https://chosen.example");

            using var http = NewClient();
            HttpRequestMessage FromApp()
            {
                var m = new HttpRequestMessage(HttpMethod.Get, $"{serve.Origin}/orders");
                m.Headers.Add("Origin", "http://localhost:3000");
                return m;
            }
            var resp = await Poll(() => http.SendAsync(FromApp()));

            // Exactly one value, and it is the operator's explicit override — not the CORS
            // accommodation's own choice of the caller's Origin.
            Assert.Equal(new[] { "https://chosen.example" },
                         HeaderValues(resp, "Access-Control-Allow-Origin"));
        }
    }

    // Characterization test: with none of the four flags, the relay must stay exactly what it was
    // before this release. This exists to prove the default path did not change.
    [Fact]
    public async Task With_no_header_rules_the_relay_stays_byte_faithful()
    {
        var (ca, server, client) = Certs();
        using (ca) using (server) using (client)
        {
            await using var api = await LoopbackMtlsServer.StartEchoAsync(server, client.Thumbprint!);
            await using var serve = ServeHost.Start(client, "--upstream", $"/={api.BaseUrl}");

            using var http = NewClient();
            async Task<string> Send()
            {
                var req = new HttpRequestMessage(HttpMethod.Get, $"{serve.Origin}/orders");
                req.Headers.Add("X-Caller", "own-value");
                var resp = await http.SendAsync(req);
                return await resp.Content.ReadAsStringAsync();
            }
            string echoed = await Poll(Send);

            // The upstream saw the caller's own header and nothing injected.
            Assert.Contains("X-Caller: own-value", echoed);
            Assert.DoesNotContain("X-Gateway", echoed);
        }

        var (ca2, server2, client2) = Certs();
        using (ca2) using (server2) using (client2)
        {
            await using var api = await LoopbackMtlsServer.StartWithHeadersAsync(
                server2, client2.Thumbprint!,
                new[] { new KeyValuePair<string, string>("X-Upstream-Only", "as-sent") },
                responseBody: "ok", responseContentType: "text/plain");
            await using var serve = ServeHost.Start(client2, "--upstream", $"/={api.BaseUrl}");

            using var http = NewClient();
            var resp = await Poll(() => http.GetAsync($"{serve.Origin}/orders"));

            Assert.Equal(new[] { "as-sent" }, HeaderValues(resp, "X-Upstream-Only"));
        }
    }

    public static IEnumerable<object[]> FramingHeaderNamesAndFlags()
    {
        string[] names =
        {
            "Connection", "Keep-Alive", "Transfer-Encoding", "Content-Length", "TE", "Trailer",
            "Upgrade", "Proxy-Authenticate", "Proxy-Authorization"
        };
        string[] setFlags = { "--request-header", "--response-header" };
        string[] removeFlags = { "--remove-request-header", "--remove-response-header" };

        foreach (var name in names)
        {
            foreach (var flag in setFlags) yield return new object[] { flag, $"{name}: v", name };
            foreach (var flag in removeFlags) yield return new object[] { flag, name, name };
        }
    }

    [Theory]
    [MemberData(nameof(FramingHeaderNamesAndFlags))]
    public void A_framing_header_is_a_usage_error_on_every_header_rule_flag(
        string flag, string argument, string name)
    {
        var stderr = new StringWriter();
        int code = CliApp.Run(
            new[] { "serve", "--port", FreePort().ToString(),
                    "--upstream", "/=https://api.internal", flag, argument },
            TextWriter.Null, stderr, services: new CliServices());

        Assert.Equal(2, code);
        Assert.Contains(name, stderr.ToString());
    }

    public static IEnumerable<object[]> HostFlags()
    {
        yield return new object[] { "--request-header", "Host: v" };
        yield return new object[] { "--remove-request-header", "Host" };
        yield return new object[] { "--response-header", "Host: v" };
        yield return new object[] { "--remove-response-header", "Host" };
    }

    [Theory]
    [MemberData(nameof(HostFlags))]
    public void Naming_host_is_a_usage_error_distinct_from_a_framing_header(string flag, string argument)
    {
        var stderr = new StringWriter();
        int code = CliApp.Run(
            new[] { "serve", "--port", FreePort().ToString(),
                    "--upstream", "/=https://api.internal", flag, argument },
            TextWriter.Null, stderr, services: new CliServices());

        Assert.Equal(2, code);
        // Host's refusal is about the upstream URI, not about framing the message — that
        // distinguishes it from the other nine refused headers.
        Assert.Contains("upstream", stderr.ToString());
    }

    [Fact]
    public void A_request_header_with_no_colon_is_a_usage_error_quoting_the_argument()
    {
        var stderr = new StringWriter();
        int code = CliApp.Run(
            new[] { "serve", "--port", FreePort().ToString(),
                    "--upstream", "/=https://api.internal", "--request-header", "NoColonHere" },
            TextWriter.Null, stderr, services: new CliServices());

        Assert.Equal(2, code);
        Assert.Contains("NoColonHere", stderr.ToString());
    }

    [Fact]
    public void A_response_header_with_no_colon_is_a_usage_error_quoting_the_argument()
    {
        var stderr = new StringWriter();
        int code = CliApp.Run(
            new[] { "serve", "--port", FreePort().ToString(),
                    "--upstream", "/=https://api.internal", "--response-header", "NoColonHere" },
            TextWriter.Null, stderr, services: new CliServices());

        Assert.Equal(2, code);
        Assert.Contains("NoColonHere", stderr.ToString());
    }

    [Fact]
    public void An_empty_remove_request_header_name_is_a_usage_error()
    {
        var stderr = new StringWriter();
        int code = CliApp.Run(
            new[] { "serve", "--port", FreePort().ToString(),
                    "--upstream", "/=https://api.internal", "--remove-request-header", "" },
            TextWriter.Null, stderr, services: new CliServices());

        Assert.Equal(2, code);
    }

    public static IEnumerable<object[]> EmptyOrWhitespaceHeaderNameFlags()
    {
        yield return new object[] { "--request-header", " : v" };
        yield return new object[] { "--response-header", " : v" };
        yield return new object[] { "--remove-request-header", "   " };
        yield return new object[] { "--remove-response-header", "   " };
    }

    [Theory]
    [MemberData(nameof(EmptyOrWhitespaceHeaderNameFlags))]
    public void An_empty_or_whitespace_only_header_name_is_a_usage_error_on_every_header_rule_flag(
        string flag, string argument)
    {
        var stderr = new StringWriter();
        int code = CliApp.Run(
            new[] { "serve", "--port", FreePort().ToString(),
                    "--upstream", "/=https://api.internal", flag, argument },
            TextWriter.Null, stderr, services: new CliServices());

        Assert.Equal(2, code);
        Assert.Contains(flag, stderr.ToString());
    }

    [Fact]
    public void The_live_reported_defect_is_a_usage_error_instead_of_a_listener_that_drops_the_rule()
    {
        // This exact command line used to start a listener and log "listening" while the name parsed
        // to the empty string: HeaderRules.TryCreate accepted it (empty was not in Refused) and at
        // forward time TryAddWithoutValidation("", "v") silently dropped it, so the upstream never
        // saw the header and the operator had no way to know the rule never applied.
        var stderr = new StringWriter();
        int code = CliApp.Run(
            new[] { "serve", "--port", FreePort().ToString(),
                    "--upstream", "/=https://api.internal", "--request-header", " : v" },
            TextWriter.Null, stderr, services: new CliServices());

        Assert.Equal(2, code);
    }

    [Fact]
    public void A_header_name_with_a_character_illegal_in_an_http_field_name_is_a_usage_error()
    {
        var stderr = new StringWriter();
        int code = CliApp.Run(
            new[] { "serve", "--port", FreePort().ToString(),
                    "--upstream", "/=https://api.internal", "--request-header", "X Y: v" },
            TextWriter.Null, stderr, services: new CliServices());

        Assert.Equal(2, code);
        Assert.Contains("X Y", stderr.ToString());
    }
}
