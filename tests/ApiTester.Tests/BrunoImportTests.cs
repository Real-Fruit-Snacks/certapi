using System.IO;
using ApiTester.Core;

namespace ApiTester.Tests;

public class BrunoImportTests
{
    private const string GetUser = """
        meta {
          name: Get user
          type: http
          seq: 2
        }

        get {
          url: {{baseUrl}}/users/:id?verbose=1
          body: none
          auth: inherit
        }

        params:query {
          verbose: 1
          ~debug: true
        }

        params:path {
          id: 42
        }

        headers {
          Accept: application/json
          ~X-Off: no
        }

        assert {
          res.status: eq 200
          res.body.id: isDefined
          res.body.items: length 3
        }

        vars:post-response {
          userId: res.body.id
          computed: res.body.a + 1
        }

        tests {
          test("ok", () => expect(res.status).to.equal(200));
        }
        """;

    private const string CreateUser = """
        meta {
          name: Create user
          type: http
          seq: 1
        }

        post {
          url: {{baseUrl}}/users
          body: json
          auth: awsv4
        }

        auth:awsv4 {
          accessKeyId: AKID
          secretAccessKey: {{secret}}
          sessionToken:
          service: execute-api
          region: eu-west-1
          profileName:
        }

        body:json {
          {
            "name": "Ann",
            "nested": { "a": 1 }
          }
        }
        """;

    private const string FolderBru = """
        meta {
          name: Users
          seq: 1
        }

        auth {
          mode: bearer
        }

        auth:bearer {
          token: {{token}}
        }
        """;

    private const string Env = """
        vars {
          baseUrl: https://api.test
          ~old: x
        }
        vars:secret [
          token,
          secret
        ]
        """;

    private static BrunoImportResult Collection() => BrunoImport.ParseFolder(new[]
    {
        ("bruno.json", """{ "version": "1", "name": "People API", "type": "collection" }"""),
        ("users/folder.bru", FolderBru),
        ("users/get-user.bru", GetUser),
        ("users/create-user.bru", CreateUser),
        ("environments/Local.bru", Env),
        ("health.bru", """
            meta {
              name: Health
              type: http
              seq: 5
            }

            get {
              url: https://api.test/health
            }
            """)
    }, "folder-name");

    [Fact]
    public void The_folder_tree_names_and_order_come_across()
    {
        var r = Collection();
        Assert.Equal("People API", r.Root.Name);
        var users = r.Root.Children.Single(c => c.IsFolder);
        Assert.Equal("Users", users.Name);
        Assert.Equal(new[] { "Create user", "Get user" }, users.Children.Select(c => c.Name));   // seq order
        Assert.Equal("Health", r.Root.Children.Single(c => !c.IsFolder).Name);
    }

    [Fact]
    public void A_request_keeps_its_params_headers_and_path_values()
    {
        var get = Collection().Root.Children.Single(c => c.IsFolder).Children.Single(c => c.Name == "Get user").Request!;
        Assert.Equal("GET", get.Method);
        Assert.Equal("{{baseUrl}}/users/42", get.Path);
        Assert.Equal(2, get.QueryParams.Count);
        Assert.False(get.QueryParams.Single(p => p.Key == "debug").Enabled);
        Assert.False(get.Headers.Single(h => h.Name == "X-Off").Enabled);
    }

    [Fact]
    public void Inherit_takes_the_folders_auth()
    {
        var get = Collection().Root.Children.Single(c => c.IsFolder).Children.Single(c => c.Name == "Get user").Request!;
        Assert.Equal(("Bearer", "{{token}}"), (get.AuthType, get.AuthSecret));
    }

    [Fact]
    public void Aws_auth_and_a_json_body_come_across()
    {
        var post = Collection().Root.Children.Single(c => c.IsFolder).Children.Single(c => c.Name == "Create user").Request!;
        Assert.Equal(("AwsSigV4", "AKID", "{{secret}}", "eu-west-1/execute-api"), (post.AuthType, post.AuthUser, post.AuthSecret, post.AuthScope));
        Assert.Equal("application/json", post.ContentType);
        Assert.StartsWith("{\n  \"name\": \"Ann\",", post.Body);
        Assert.EndsWith("}", post.Body);
        Assert.DoesNotContain(post.Headers, h => h.Name == "X-Amz-Security-Token");   // empty token: no header
    }

    [Fact]
    public void Asserts_and_post_response_vars_map_where_they_can_and_say_where_they_cannot()
    {
        var r = Collection();
        var get = r.Root.Children.Single(c => c.IsFolder).Children.Single(c => c.Name == "Get user").Request!;
        Assert.Contains(get.Assertions, a => a.Target == AssertTarget.Status && a.Op == AssertOp.Equals && a.Value == "200");
        Assert.Contains(get.Assertions, a => a.Target == AssertTarget.Body && a.Op == AssertOp.Exists && a.Path == "id");
        Assert.Equal(2, get.Assertions.Count);
        var capture = Assert.Single(get.Captures);
        Assert.Equal(("userId", CaptureSource.Body, "id"), (capture.Variable, capture.Source, capture.Path));

        Assert.Contains(r.Warnings, w => w.Contains("length 3"));
        Assert.Contains(r.Warnings, w => w.Contains("'computed'"));
        Assert.Contains(r.Warnings, w => w.Contains("tests block is JavaScript"));
    }

    [Fact]
    public void Environments_come_across_with_secrets_empty_and_named()
    {
        var r = Collection();
        var env = Assert.Single(r.Environments);
        Assert.Equal("Local", env.Name);
        Assert.Equal("https://api.test", env.Variables.Single(v => v.Key == "baseUrl").Value);
        Assert.DoesNotContain(env.Variables, v => v.Key == "old");
        Assert.True(env.Variables.Single(v => v.Key == "token").Secret);
        Assert.Contains(r.Warnings, w => w.Contains("2 secret variable"));
    }

    [Fact]
    public void A_request_with_no_auth_line_keeps_the_default()
    {
        var health = Collection().Root.Children.Single(c => c.Name == "Health").Request!;
        Assert.Equal("Auto", health.AuthType);
    }

    [Fact]
    public void Graphql_and_form_bodies_are_rebuilt()
    {
        var r = BrunoImport.ParseFile("""
            meta {
              name: Query
              type: graphql
            }

            post {
              url: https://api.test/graphql
              body: graphql
              auth: none
            }

            body:graphql {
              query { me { id } }
            }

            body:graphql:vars {
              { "a": 1 }
            }
            """, "q");
        var q = r.Root.Children.Single().Request!;
        Assert.Equal("None", q.AuthType);
        Assert.Contains("\"query\":\"query { me { id } }\"", q.Body);
        Assert.Contains("\"variables\":{\"a\":1}", q.Body);

        var form = BrunoImport.ParseFile("""
            post {
              url: https://api.test/login
              body: formUrlEncoded
            }

            body:form-urlencoded {
              user: ann b
              ~skip: 1
            }
            """, "login").Root.Children.Single().Request!;
        Assert.Equal("user=ann%20b", form.Body);
        Assert.Equal("application/x-www-form-urlencoded", form.ContentType);
    }

    [Fact]
    public void The_json_export_maps_the_same_way()
    {
        var r = BrunoImport.ParseJson("""
            { "name": "Exported", "version": "1",
              "root": { "request": { "auth": { "mode": "basic", "basic": { "username": "u", "password": "p" } } } },
              "items": [
                { "type": "folder", "name": "F", "items": [
                  { "type": "http-request", "name": "two", "seq": 2, "request": { "method": "GET", "url": "https://x.test/2",
                    "auth": { "mode": "inherit" } } },
                  { "type": "http-request", "name": "one", "seq": 1, "request": { "method": "POST", "url": "https://x.test/1",
                    "headers": [ { "name": "A", "value": "b", "enabled": false } ],
                    "body": { "mode": "json", "json": "{\"x\":1}" },
                    "auth": { "mode": "digest", "digest": { "username": "d", "password": "q" } },
                    "assertions": [ { "name": "res.status", "value": "eq 201", "enabled": true } ] } }
                ] }
              ],
              "environments": [ { "name": "Dev", "variables": [ { "name": "k", "value": "v", "enabled": true, "secret": true } ] } ] }
            """);
        var folder = r.Root.Children.Single();
        Assert.Equal(new[] { "one", "two" }, folder.Children.Select(c => c.Name));
        var one = folder.Children[0].Request!;
        Assert.Equal(("Digest", "d", "{\"x\":1}"), (one.AuthType, one.AuthUser, one.Body));
        Assert.False(one.Headers.Single().Enabled);
        Assert.Equal("201", one.Assertions.Single().Value);
        Assert.Equal(("Basic", "u"), (folder.Children[1].Request!.AuthType, folder.Children[1].Request!.AuthUser));
        Assert.True(r.Environments.Single().Variables.Single().Secret);
    }

    [Fact]
    public void Not_a_bruno_export_is_a_format_error() =>
        Assert.Throws<FormatException>(() => BrunoImport.ParseJson("""{ "info": {} }"""));

    [Fact]
    public void The_cli_reads_a_collection_folder()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"certapi-bruno-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(dir, "users"));
        Directory.CreateDirectory(Path.Combine(dir, "node_modules", "x"));
        try
        {
            File.WriteAllText(Path.Combine(dir, "bruno.json"), """{ "name": "On disk" }""");
            File.WriteAllText(Path.Combine(dir, "users", "get-user.bru"), GetUser);
            File.WriteAllText(Path.Combine(dir, "node_modules", "x", "ignored.bru"), GetUser);
            string workspace = Path.Combine(dir, "ws.json");
            var err = new StringWriter();
            int code = ApiTester.Cli.CliApp.Run(["import", "bruno", dir, "--workspace", workspace], new StringReader(""), new StringWriter(), err,
                null, new ApiTester.Cli.CliServices());
            Assert.True(code == 0, err.ToString());
            Assert.Contains("Imported 1 request (On disk)", err.ToString());
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
