using ApiTester.Core;

namespace ApiTester.Tests;

public class CurlParserTests
{
    [Fact]
    public void Simple_get()
    {
        var r = CurlParser.Parse("curl https://api.example.com/users");
        Assert.Equal("GET", r.Method);
        Assert.Equal("https://api.example.com/users", r.Url);
    }

    [Fact]
    public void Post_with_json_body_and_content_type()
    {
        var r = CurlParser.Parse("curl -X POST https://h/api -H \"Content-Type: application/json\" -d '{\"a\":1}'");
        Assert.Equal("POST", r.Method);
        Assert.Equal("https://h/api", r.Url);
        Assert.Equal("application/json", r.ContentType);
        Assert.Equal("{\"a\":1}", r.Body);
        Assert.Empty(r.Headers); // content-type is lifted out of headers
    }

    [Fact]
    public void Data_without_method_implies_post()
    {
        var r = CurlParser.Parse("curl https://h/f -d name=bob");
        Assert.Equal("POST", r.Method);
        Assert.Equal("name=bob", r.Body);
    }

    [Fact]
    public void Multiple_headers_are_kept()
    {
        var r = CurlParser.Parse("curl -H 'Accept: application/json' -H 'X-Key: abc' https://h");
        Assert.Equal(2, r.Headers.Count);
        Assert.Equal(new("Accept", "application/json"), r.Headers[0]);
        Assert.Equal(new("X-Key", "abc"), r.Headers[1]);
    }

    [Fact]
    public void Basic_user_is_split()
    {
        var r = CurlParser.Parse("curl -u alice:s3cret https://h");
        Assert.Equal("alice", r.BasicUser);
        Assert.Equal("s3cret", r.BasicPassword);
    }

    [Fact]
    public void Insecure_flag()
    {
        var r = CurlParser.Parse("curl -k https://self-signed.example");
        Assert.True(r.InsecureSkipVerify);
    }

    [Fact]
    public void Bearer_authorization_becomes_token()
    {
        var r = CurlParser.Parse("curl https://h -H \"Authorization: Bearer tok123\"");
        Assert.Equal("tok123", r.BearerToken);
        Assert.Empty(r.Headers);
    }

    [Fact]
    public void Line_continuations_and_long_options()
    {
        var curl = "curl --request PUT \\\n  --url https://h/thing \\\n  --header 'Accept: */*'";
        var r = CurlParser.Parse(curl);
        Assert.Equal("PUT", r.Method);
        Assert.Equal("https://h/thing", r.Url);
        Assert.Single(r.Headers);
        Assert.Equal(new("Accept", "*/*"), r.Headers[0]);
    }

    [Theory]
    [InlineData("curl https://api.example.com/orders --cert client.pem --key client.key")]
    [InlineData("curl https://api.example.com/orders -o out.json -m 5 -w '%{http_code}'")]
    [InlineData("curl -E client.pem --cacert ca.pem --connect-timeout 3 https://api.example.com/orders")]
    public void Values_of_options_this_importer_does_not_use_are_not_the_url(string curl)
    {
        Assert.Equal("https://api.example.com/orders", CurlParser.Parse(curl).Url);
    }

    [Fact]
    public void Short_options_with_their_value_attached_and_in_clusters()
    {
        var r = CurlParser.Parse("curl -sSLkXPUT -HAccept:x -uuser:pw https://h/o/1");
        Assert.Equal("PUT", r.Method);
        Assert.True(r.InsecureSkipVerify);
        Assert.Equal(new("Accept", "x"), r.Headers[0]);
        Assert.Equal("user", r.BasicUser);
        Assert.Equal("pw", r.BasicPassword);
        Assert.Equal("https://h/o/1", r.Url);
    }

    [Fact]
    public void Get_puts_the_data_on_the_query_string()
    {
        var r = CurlParser.Parse("curl -G https://h/search -d q=1 --data-urlencode 'name=a b&c'");
        Assert.Equal("GET", r.Method);
        Assert.Equal("https://h/search?q=1&name=a%20b%26c", r.Url);
        Assert.Null(r.Body);
    }

    [Fact]
    public void Json_sets_body_content_type_and_accept()
    {
        var r = CurlParser.Parse("curl --json '{\"a\":1}' https://h/x");
        Assert.Equal("POST", r.Method);
        Assert.Equal("{\"a\":1}", r.Body);
        Assert.Equal("application/json", r.ContentType);
        Assert.Contains(new KeyValuePair<string, string>("Accept", "application/json"), r.Headers);
    }

    [Fact]
    public void Data_without_a_content_type_is_form_encoded_as_curl_sends_it()
    {
        Assert.Equal("application/x-www-form-urlencoded", CurlParser.Parse("curl https://h -d a=1").ContentType);
    }

    [Fact]
    public void Head_and_form_parts()
    {
        Assert.Equal("HEAD", CurlParser.Parse("curl -I https://h").Method);
        var r = CurlParser.Parse("curl https://h/up -F name=bob -F 'file=@C:\\scans\\a.pdf;type=application/pdf'");
        Assert.Equal("POST", r.Method);
        Assert.Equal(new ParsedFormPart("name", "bob", false), r.FormParts[0]);
        Assert.Equal(new ParsedFormPart("file", "C:\\scans\\a.pdf", true), r.FormParts[1]);
        var m = RequestModel.FromParsed(r);
        Assert.True(m.IsMultipart);
        Assert.Equal(2, m.FormParts.Count);
    }

    [Fact]
    public void Double_quotes_keep_backslashes_bash_does_not_treat_as_escapes()
    {
        var r = CurlParser.Parse("curl https://h -d \"{\\\"m\\\":\\\"a\\nb\\\",\\\"p\\\":\\\"C:\\temp\\\"}\"");
        Assert.Equal("{\"m\":\"a\\nb\",\"p\":\"C:\\temp\"}", r.Body);
    }

    [Fact]
    public void Ansi_c_quoting_from_copy_as_curl_bash()
    {
        var r = CurlParser.Parse("curl 'https://h/x' --data-raw $'{\"q\":\"it\\'s\\u00e9\",\"n\":\"a\\nb\",\"x\":\"\\xc3\\xa9\"}'");
        Assert.Equal("{\"q\":\"it's\u00e9\",\"n\":\"a\nb\",\"x\":\"\u00e9\"}", r.Body);
    }

    [Fact]
    public void Copy_as_curl_cmd_from_a_windows_browser()
    {
        var curl = "curl ^\"https://h/api?a=1^&b=2^\" ^\r\n  -H ^\"content-type: application/json^\" ^\r\n  --data-raw ^\"^{^\\^\"a^\\^\":^\\^\"C:\\\\x^\\^\"^}^\"";
        var r = CurlParser.Parse(curl);
        Assert.Equal("https://h/api?a=1&b=2", r.Url);
        Assert.Equal("application/json", r.ContentType);
        Assert.Equal("{\"a\":\"C:\\\\x\"}", r.Body);
    }

    [Fact]
    public void Curl_exe_and_a_path_to_curl_are_the_program_not_the_url()
    {
        Assert.Equal("https://h/", CurlParser.Parse("curl.exe https://h/").Url);
        Assert.Equal("https://h/", CurlParser.Parse("/usr/bin/curl https://h/ -s").Url);
    }

    [Fact]
    public void A_bash_command_with_a_caret_quote_in_its_body_is_still_bash()
    {
        var r = CurlParser.Parse("curl https://x/y -H 'Content-Type: application/json' -d '{\"sep\":\"^\"}'");
        Assert.Equal("https://x/y", r.Url);
        Assert.Equal("{\"sep\":\"^\"}", r.Body);
    }
}
