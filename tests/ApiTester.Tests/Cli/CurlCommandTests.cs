using System.IO;
using System.Text;
using System.Text.Json;
using ApiTester.Cli;
using ApiTester.Cli.Commands;
using ApiTester.Core;

namespace ApiTester.Tests.Cli;

/// <summary>curl's own spellings on send (bundles, attached values, -G, -T, -O, --data-raw, -E…)
/// and `certapi curl`, which runs a pasted curl command with curl's meaning.</summary>
public class CurlCommandTests
{
    private static string TempState() => Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");

    private static (int Code, string Out, string Err, string Body) Run(params string[] args)
    {
        var so = new StringWriter();
        var se = new StringWriter();
        var body = new MemoryStream();
        int code = CliApp.Run(args.Append("--no-config").ToArray(), so, se, body,
            new CliServices { LiveStatePath = TempState(), UserConfigPath = null });
        return (code, so.ToString(), se.ToString(), Encoding.UTF8.GetString(body.ToArray()));
    }

    private static JsonElement Echo(string body) => JsonDocument.Parse(body).RootElement;
    private static string? Header(JsonElement echo, string name) =>
        echo.GetProperty("headers").EnumerateObject()
            .FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Value is { ValueKind: JsonValueKind.String } v
            ? v.GetString() : null;

    // ---- send: bundles and attached values ----

    [Fact]
    public void Bundled_switches_are_read_one_by_one()
    {
        var a = new Args(new[] { "-sSkL" });
        Assert.True(a.Flag("--insecure"));
        Assert.True(a.Flag("-q", "--quiet"));
        Assert.True(a.Flag("-S", "--show-error"));
        Assert.True(a.Flag("-L", "--location"));
        Assert.Empty(a.Positionals());
    }

    [Fact]
    public void A_long_options_value_that_looks_like_a_bundle_is_its_value()
    {
        // --user-agent takes a value on send: "-ok" is that value, not -o k.
        var a = new Args(new[] { "--user-agent", "-ok", "--query", "-debug=1", "--include", "-sS" }, "send");
        Assert.Equal("-ok", a.Value("--user-agent"));
        Assert.Equal("-debug=1", a.Value("--query"));
        // --include is a switch on send, so the bundle after it is still a bundle.
        Assert.True(a.Flag("--include"));
        Assert.True(a.Flag("-q", "--quiet"));
        Assert.True(a.Flag("-S", "--show-error"));
        Assert.Empty(a.Positionals());
    }

    [Fact]
    public async Task A_bundle_shaped_value_reaches_the_server_as_typed()
    {
        await using var srv = MockServer.Start(0, MockTlsMode.Http);
        var r = Run("send", srv.BaseUrl.TrimEnd('/') + "/echo", "--user-agent", "-ok");
        Assert.Equal(0, r.Code);
        Assert.Equal("-ok", Header(Echo(r.Body), "User-Agent"));
        var c = Run("curl", srv.BaseUrl.TrimEnd('/') + "/echo", "--user-agent", "-download", "-s");
        Assert.Equal(0, c.Code);
        Assert.Equal("-download", Header(Echo(c.Body), "User-Agent"));

        // After an option whose value is optional, a bundle is still a bundle.
        var m = new Args(new[] { "--cors", "-sS" }, "mock");
        Assert.True(m.OptionalValue(out var origins, "--cors"));
        Assert.Null(origins);
        Assert.Contains("-S", m.ExpandedTokens);
    }

    [Fact]
    public async Task Data_and_urlencoded_parts_keep_curls_order()
    {
        var t = CurlCommand.Translate(new[] { "http://x/", "--data-urlencode", "a=b c", "-d", "z=1", "--data-urlencode", "q=&" });
        Assert.Contains("--data-raw=a=b%20c&z=1&q=%26", t);
        Assert.DoesNotContain(t, x => x.StartsWith("--data-urlencode", StringComparison.Ordinal));

        await using var srv = MockServer.Start(0, MockTlsMode.Http);
        var r = Run("curl", srv.BaseUrl.TrimEnd('/') + "/echo", "-s", "--data-urlencode", "a=b c", "-d", "z=1", "--data-urlencode", "q=&");
        Assert.Equal(0, r.Code);
        var e = Echo(r.Body);
        Assert.Equal("a=b%20c&z=1&q=%26", e.GetProperty("body").GetString());
        Assert.Equal("application/x-www-form-urlencoded", Header(e, "Content-Type"));

        var g = Run("curl", "-G", srv.BaseUrl.TrimEnd('/') + "/echo", "-s", "--data-urlencode", "a=b c", "-d", "z=1");
        Assert.Equal(0, g.Code);
        Assert.Equal("a=b%20c&z=1", Echo(g.Body).GetProperty("query").GetString());

        // Beside binary data they are left to send, which refuses the mix rather than reading the
        // file's bytes as text.
        var binary = CurlCommand.Translate(new[] { "http://x/", "--data-binary", "@f.bin", "--data-urlencode", "x=y" });
        Assert.Contains("--data-file=f.bin", binary);
        Assert.Contains("--data-urlencode=x=y", binary);

        // On their own they still go to send to encode, a file's text included.
        var only = CurlCommand.Translate(new[] { "http://x/", "--data-urlencode", "a=b c" });
        Assert.Contains("--data-urlencode=a=b c", only);
    }

    [Fact]
    public void A_value_attached_to_its_letter_is_the_value()
    {
        var a = new Args(new[] { "-XPOST", "-HAccept:x", "-sSXPUT" });
        Assert.Equal(new[] { "POST", "PUT" }, a.Values("-X", "--method"));
        Assert.Equal("Accept:x", a.Value("-H", "--header"));
        Assert.True(a.Flag("-q", "--quiet"));
    }

    [Fact]
    public void A_value_that_looks_like_a_bundle_stays_a_value()
    {
        var a = new Args(new[] { "-H", "-kv", "-d", "-1" });
        Assert.Equal("-kv", a.Value("-H", "--header"));
        Assert.Equal("-1", a.Value("-d", "--data"));
        Assert.False(a.Flag("--insecure"));
    }

    [Fact]
    public void A_bundle_with_a_letter_nobody_has_is_unknown()
    {
        var r = Run("send", "http://127.0.0.1:9/", "-sZq");
        Assert.Equal(2, r.Code);
        Assert.Contains("Unknown option", r.Err);
    }

    [Theory]
    [InlineData("--fail-with-body", "--fail")]
    [InlineData("-m", "--timeout")]
    [InlineData("--oauth2-bearer", "--bearer")]
    public void Curl_names_are_aliases(string alias, string option)
    {
        var a = new Args(new[] { alias, "v" });
        if (option == "--fail") Assert.True(a.Flag("-f", option));
        else Assert.Equal("v", a.Value(option));
    }

    // ---- send: curl's request options ----

    [Fact]
    public async Task Bundled_method_and_attached_body_reach_the_server()
    {
        await using var srv = MockServer.Start(0, MockTlsMode.Http);
        var r = Run("send", srv.BaseUrl.TrimEnd('/') + "/echo", "-sSXPUT", "-d{}", "-HX-Test:1", "-e", "https://from.example/");
        Assert.Equal(0, r.Code);
        var e = Echo(r.Body);
        Assert.Equal("PUT", e.GetProperty("method").GetString());
        Assert.Equal("{}", e.GetProperty("body").GetString());
        Assert.Equal("1", Header(e, "X-Test"));
        Assert.Equal("https://from.example/", Header(e, "Referer"));
    }

    [Fact]
    public async Task Dash_G_moves_the_data_into_the_query()
    {
        await using var srv = MockServer.Start(0, MockTlsMode.Http);
        var r = Run("send", srv.BaseUrl.TrimEnd('/') + "/echo", "-G", "-d", "a=1", "--data-urlencode", "b=x y");
        Assert.Equal(0, r.Code);
        var e = Echo(r.Body);
        Assert.Equal("GET", e.GetProperty("method").GetString());
        Assert.Equal("a=1&b=x%20y", e.GetProperty("query").GetString());
        Assert.Equal(JsonValueKind.Null, e.GetProperty("body").ValueKind);
    }

    [Fact]
    public async Task Upload_file_is_a_PUT_of_the_file_and_data_raw_is_literal()
    {
        await using var srv = MockServer.Start(0, MockTlsMode.Http);
        var file = Path.GetTempFileName();
        File.WriteAllText(file, "uploaded");
        try
        {
            var put = Echo(Run("send", srv.BaseUrl.TrimEnd('/') + "/echo", "-T", file).Body);
            Assert.Equal("PUT", put.GetProperty("method").GetString());
            Assert.Equal("uploaded", put.GetProperty("body").GetString());

            var raw = Echo(Run("send", srv.BaseUrl.TrimEnd('/') + "/echo", "--data-raw", "@" + file).Body);
            Assert.Equal("@" + file, raw.GetProperty("body").GetString());
        }
        finally { File.Delete(file); }
    }

    [Theory]
    [InlineData("-L", "--no-redirect")]
    [InlineData("--compressed", "--no-decompress")]
    [InlineData("-O", "-o")]
    [InlineData("-d", "--data-raw")]
    public void A_curl_spelling_and_its_opposite_conflict(string curl, string other)
    {
        var args = new List<string> { "send", "http://127.0.0.1:9/x" };
        args.Add(curl);
        if (curl == "-d") args.Add("a");
        args.Add(other);
        if (other is "-o" or "--data-raw") args.Add("b");
        var r = Run(args.ToArray());
        Assert.Equal(2, r.Code);
    }

    [Fact]
    public void Curls_json_with_data_explains_the_difference()
    {
        var r = Run("send", "http://127.0.0.1:9/", "--json", "{\"a\":1}");
        Assert.Equal(2, r.Code);
        Assert.Contains("certapi curl", r.Err);
    }

    [Fact]
    public void Url_option_stands_in_for_the_positional()
    {
        var r = Run("send", "--url", "http://127.0.0.1:9/", "http://127.0.0.1:9/");
        Assert.Equal(2, r.Code);
    }

    [Theory]
    [InlineData(@"CurrentUser\My\0123456789abcdef0123456789abcdef01234567", "0123456789abcdef0123456789abcdef01234567", null, null, "CurrentUser")]
    [InlineData(@"LocalMachine\MY\0123456789ABCDEF0123456789ABCDEF01234567", "0123456789ABCDEF0123456789ABCDEF01234567", null, null, "LocalMachine")]
    [InlineData(@"C:\certs\me.pfx:s3cret", null, @"C:\certs\me.pfx", "s3cret", null)]
    [InlineData(@"C:\certs\me.pfx", null, @"C:\certs\me.pfx", null, null)]
    [InlineData("me.pem", null, "me.pem", null, null)]
    public void Dash_E_reads_curls_forms(string value, string? query, string? file, string? password, string? store)
    {
        var parsed = CliCert.FromCurlCert(value);
        Assert.Equal(query, parsed.Query);
        Assert.Equal(file, parsed.File);
        Assert.Equal(password, parsed.Password);
        Assert.Equal(store, parsed.Store);
    }

    // ---- certapi curl: the translation ----

    private static List<string> T(params string[] curl) => CurlCommand.Translate(curl);

    [Fact]
    public void Redirects_are_followed_only_with_L()
    {
        Assert.Contains("--no-redirect", T("https://x/"));
        Assert.DoesNotContain("--no-redirect", T("-sSL", "https://x/"));
        Assert.DoesNotContain("--no-redirect", T("--location", "https://x/"));
    }

    [Fact]
    public void Data_parts_are_joined_and_sent_as_a_form()
    {
        var t = T("-d", "a=1", "--data", "b=2", "https://x/");
        Assert.Contains("--data-raw=a=1&b=2", t);
        Assert.Contains("--content-type=application/x-www-form-urlencoded", t);
        Assert.DoesNotContain("--content-type=application/x-www-form-urlencoded",
            T("-H", "Content-Type: application/json", "-d", "{}", "https://x/"));
        Assert.DoesNotContain("--content-type=application/x-www-form-urlencoded", T("-G", "-d", "a=1", "https://x/"));
    }

    [Fact]
    public void Json_takes_data_and_sets_both_headers()
    {
        var t = T("--json", "{\"a\":1}", "https://x/");
        Assert.Contains("--data-raw={\"a\":1}", t);
        Assert.Contains("--content-type=application/json", t);
        Assert.Contains("Accept: application/json", t);
        Assert.DoesNotContain("Accept: application/json", T("--json", "{}", "-H", "Accept: */*", "https://x/"));
    }

    [Fact]
    public void Data_at_file_drops_line_breaks_and_data_binary_keeps_the_bytes()
    {
        var file = Path.GetTempFileName();
        File.WriteAllText(file, "a=1\r\n&b=2\n");
        try
        {
            Assert.Contains("--data-raw=a=1&b=2", T("-d", "@" + file, "https://x/"));
            Assert.Contains("--data-file=" + file, T("--data-binary", "@" + file, "https://x/"));
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void Curl_only_options_become_their_certapi_names()
    {
        var t = T("-v", "-c", "jar.txt", "--pass", "pw", "--cert-type", "P12", "https://x/");
        Assert.Contains("--debug", t);
        Assert.Contains("--cookie-jar=jar.txt", t);
        Assert.Contains("--cert-password=pw", t);
        Assert.DoesNotContain("--cert-type", t);
        Assert.DoesNotContain("P12", t);
    }

    [Fact]
    public void Cert_names_a_file_a_store_path_or_a_store_query()
    {
        var store = T("-E", @"LocalMachine\MY\0123456789abcdef0123456789abcdef01234567", "https://x/");
        Assert.Contains("--cert=0123456789abcdef0123456789abcdef01234567", store);
        Assert.Contains("--store=LocalMachine", store);

        Assert.Contains("--cert=CN=me", T("--cert", "CN=me", "https://x/"));
        Assert.Contains("--cert-file=client.pfx", T("--cert", "client.pfx", "https://x/"));

        var file = Path.GetTempFileName();
        try
        {
            var withPassword = T("--cert", file + ":pw", "https://x/");
            Assert.Contains("--cert-file=" + file, withPassword);
            Assert.Contains("--cert-password=pw", withPassword);
        }
        finally { File.Delete(file); }
    }

    // ---- certapi curl: end to end ----

    [Fact]
    public async Task A_pasted_command_line_runs_with_curls_meaning()
    {
        await using var srv = MockServer.Start(0, MockTlsMode.Http);
        string url = srv.BaseUrl.TrimEnd('/') + "/echo";
        var r = Run("curl", $"curl -sS -X POST '{url}' \\\n  -d user=me -d 'note=a b' -H 'X-From: paste'", "--assert", "status == 200");
        Assert.Equal(0, r.Code);
        var e = Echo(r.Body);
        Assert.Equal("POST", e.GetProperty("method").GetString());
        Assert.Equal("user=me&note=a b", e.GetProperty("body").GetString());
        Assert.Equal("application/x-www-form-urlencoded", Header(e, "Content-Type"));
        Assert.Equal("paste", Header(e, "X-From"));
    }

    [Fact]
    public async Task Separate_arguments_work_and_certapi_options_can_be_added()
    {
        await using var srv = MockServer.Start(0, MockTlsMode.Http);
        var r = Run("curl", "curl", "--json", "{\"a\":1}", srv.BaseUrl.TrimEnd('/') + "/echo", "--assert", "status == 200");
        Assert.Equal(0, r.Code);
        var e = Echo(r.Body);
        Assert.Equal("POST", e.GetProperty("method").GetString());
        Assert.Equal("{\"a\":1}", e.GetProperty("body").GetString());
        Assert.Equal("application/json", Header(e, "Accept"));
    }

    [Fact]
    public void Nothing_to_run_prints_the_help_as_a_usage_error()
    {
        var r = Run("curl", "curl");
        Assert.Equal(2, r.Code);
        Assert.Contains("certapi curl", r.Err);
    }

    [Fact]
    public void An_option_neither_curl_nor_send_has_is_reported()
    {
        var r = Run("curl", "curl --frobnicate http://127.0.0.1:9/");
        Assert.Equal(2, r.Code);
        Assert.Contains("--frobnicate", r.Err);
    }
}
