using ApiTester.Core;

namespace ApiTester.Tests;

/// <summary>Pins the Postman Collection v2.x mapping table: folders, both URL forms, disabled
/// rows, every body mode, the auth ladder (request beats folder beats collection), variables
/// becoming an environment, and the named-warning contract for anything that cannot carry
/// across.</summary>
public class PostmanImportTests
{
    [Fact]
    public void A_nested_collection_maps_folders_and_requests_with_both_url_forms()
    {
        var result = PostmanImport.Parse("""
            {
              "info": { "name": "Orders API", "schema": "https://schema.getpostman.com/json/collection/v2.1.0/collection.json" },
              "item": [
                { "name": "Auth", "item": [
                    { "name": "login", "request": {
                        "method": "POST",
                        "url": "https://api.example/{{env}}/login",
                        "header": [ { "key": "X-Trace", "value": "1" },
                                    { "key": "X-Off", "value": "no", "disabled": true } ],
                        "body": { "mode": "raw", "raw": "{\"user\":\"u\"}",
                                  "options": { "raw": { "language": "json" } } } } }
                ] },
                { "name": "list orders", "request": {
                    "method": "GET",
                    "url": { "raw": "https://api.example/orders?limit=10",
                             "protocol": "https", "host": ["api","example"], "path": ["orders"],
                             "query": [ { "key": "limit", "value": "10" },
                                        { "key": "debug", "value": "1", "disabled": true } ] } } }
              ]
            }
            """);

        Assert.Equal("Orders API", result.Root.Name);
        Assert.Empty(result.Warnings);

        var auth = result.Root.Children[0];
        Assert.True(auth.IsFolder);
        var login = auth.Children[0].Request!;
        Assert.Equal("POST", login.Method);
        Assert.Equal("https://api.example/{{env}}/login", login.Path);   // {{variables}} untouched
        Assert.Equal(2, login.Headers.Count);
        Assert.True(login.Headers[0].Enabled);
        Assert.False(login.Headers[1].Enabled);                          // disabled stays disabled
        Assert.Equal("application/json", login.ContentType);
        Assert.Equal("{\"user\":\"u\"}", login.Body);

        var orders = result.Root.Children[1].Request!;
        Assert.Equal("https://api.example/orders", orders.Path);         // query moved to rows
        Assert.Equal(2, orders.QueryParams.Count);
        Assert.True(orders.QueryParams[0].Enabled);
        Assert.False(orders.QueryParams[1].Enabled);
    }

    [Fact]
    public void Request_auth_beats_folder_auth_which_beats_collection_auth()
    {
        var result = PostmanImport.Parse("""
            {
              "info": { "name": "C" },
              "auth": { "type": "bearer", "bearer": [ { "key": "token", "value": "collection-tok" } ] },
              "item": [
                { "name": "plain", "request": { "method": "GET", "url": "https://a/1" } },
                { "name": "F",
                  "auth": { "type": "basic", "basic": [ { "key": "username", "value": "fu" }, { "key": "password", "value": "fp" } ] },
                  "item": [
                    { "name": "inherits folder", "request": { "method": "GET", "url": "https://a/2" } },
                    { "name": "own auth", "request": { "method": "GET", "url": "https://a/3",
                        "auth": { "type": "bearer", "bearer": [ { "key": "token", "value": "own-tok" } ] } } }
                ] }
              ]
            }
            """);

        var plain = result.Root.Children[0].Request!;
        Assert.Equal("Bearer", plain.AuthType);
        Assert.Equal("collection-tok", plain.AuthSecret);

        var folder = result.Root.Children[1];
        var inherited = folder.Children[0].Request!;
        Assert.Equal("Basic", inherited.AuthType);
        Assert.Equal("fu", inherited.AuthUser);
        Assert.Equal("fp", inherited.AuthSecret);

        var own = folder.Children[1].Request!;
        Assert.Equal("Bearer", own.AuthType);
        Assert.Equal("own-tok", own.AuthSecret);
    }

    [Fact]
    public void Urlencoded_and_formdata_bodies_map_and_a_file_part_imports_disabled_with_a_warning()
    {
        var result = PostmanImport.Parse("""
            {
              "info": { "name": "Bodies" },
              "item": [
                { "name": "form", "request": { "method": "POST", "url": "https://a/f",
                    "body": { "mode": "urlencoded", "urlencoded": [
                        { "key": "a", "value": "1 2" },
                        { "key": "skip", "value": "x", "disabled": true } ] } } },
                { "name": "upload", "request": { "method": "POST", "url": "https://a/u",
                    "body": { "mode": "formdata", "formdata": [
                        { "key": "note", "value": "hi", "type": "text" },
                        { "key": "doc", "src": "C:/them/report.pdf", "type": "file" } ] } } }
              ]
            }
            """);

        var form = result.Root.Children[0].Request!;
        Assert.Equal("application/x-www-form-urlencoded", form.ContentType);
        Assert.Equal("a=1%202", form.Body);                       // disabled field dropped, value escaped

        var upload = result.Root.Children[1].Request!;
        Assert.True(upload.IsMultipart);
        Assert.Equal(2, upload.FormParts.Count);
        Assert.True(upload.FormParts[0].Enabled);
        Assert.False(upload.FormParts[1].Enabled);                // a foreign file path never auto-sends
        Assert.True(upload.FormParts[1].IsFile);
        Assert.Contains(result.Warnings, w => w.Contains("doc") && w.Contains("disabled"));
    }

    [Fact]
    public void Apikey_auth_becomes_a_header_or_query_row_per_its_in()
    {
        var result = PostmanImport.Parse("""
            {
              "info": { "name": "Keys" },
              "item": [
                { "name": "hdr", "request": { "method": "GET", "url": "https://a/h",
                    "auth": { "type": "apikey", "apikey": [
                        { "key": "key", "value": "X-Key" }, { "key": "value", "value": "k1" } ] } } },
                { "name": "qry", "request": { "method": "GET", "url": "https://a/q",
                    "auth": { "type": "apikey", "apikey": [
                        { "key": "key", "value": "api_key" }, { "key": "value", "value": "k2" },
                        { "key": "in", "value": "query" } ] } } }
              ]
            }
            """);

        var hdr = result.Root.Children[0].Request!;
        Assert.Contains(hdr.Headers, h => h.Name == "X-Key" && h.Value == "k1");
        var qry = result.Root.Children[1].Request!;
        Assert.Contains(qry.QueryParams, p => p.Key == "api_key" && p.Value == "k2");
    }

    [Fact]
    public void Collection_variables_become_an_environment_and_secret_stays_secret()
    {
        var result = PostmanImport.Parse("""
            {
              "info": { "name": "Vars" },
              "item": [],
              "variable": [
                { "key": "baseUrl", "value": "https://api.example" },
                { "key": "apiKey", "value": "shh", "type": "secret" }
              ]
            }
            """);

        Assert.NotNull(result.Variables);
        Assert.Equal("Vars", result.Variables!.Name);
        Assert.Equal(2, result.Variables.Variables.Count);
        Assert.False(result.Variables.Variables[0].Secret);
        Assert.True(result.Variables.Variables[1].Secret);
    }

    [Fact]
    public void Unsupported_auth_and_body_modes_are_named_warnings_not_silent_drops()
    {
        var result = PostmanImport.Parse("""
            {
              "info": { "name": "Edge" },
              "item": [
                { "name": "oauth", "request": { "method": "GET", "url": "https://a/x",
                    "auth": { "type": "oauth2" } } },
                { "name": "gql", "request": { "method": "POST", "url": "https://a/g",
                    "body": { "mode": "graphql" } } }
              ]
            }
            """);

        Assert.Contains(result.Warnings, w => w.Contains("oauth") && w.Contains("oauth2"));
        Assert.Contains(result.Warnings, w => w.Contains("gql") && w.Contains("graphql"));
        Assert.Equal("None", result.Root.Children[0].Request!.AuthType);
    }

    [Fact]
    public void Not_a_postman_collection_is_a_format_error_naming_what_was_expected()
    {
        var ex = Assert.Throws<FormatException>(() => PostmanImport.Parse("""{"openapi":"3.0.0"}"""));
        Assert.Contains("Postman", ex.Message);

        Assert.Throws<FormatException>(() => PostmanImport.Parse("not json at all"));
    }
}
