using System.IO;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using ApiTester.Core;

namespace ApiTester.Tests;

/// <summary>JSON Schema assertions, the ones an OpenAPI import writes, and the JUnit run report.</summary>
public class JsonSchemaCheckTests
{
    private static string? Check(string schema, string json)
    {
        using var doc = JsonDocument.Parse(json);
        return JsonSchemaCheck.FirstError(schema, doc.RootElement);
    }

    [Theory]
    [InlineData("""{"type":"object","required":["id"],"properties":{"id":{"type":"integer"}}}""", """{"id":4}""")]
    [InlineData("""{"type":"array","items":{"type":"string"},"minItems":1,"uniqueItems":true}""", """["a","b"]""")]
    [InlineData("""{"type":["string","null"]}""", "null")]
    [InlineData("""{"type":"string","nullable":true}""", "null")]
    [InlineData("""{"enum":["red","green"]}""", "\"green\"")]
    [InlineData("""{"const":3}""", "3.0")]
    [InlineData("""{"type":"number","minimum":1,"exclusiveMaximum":10,"multipleOf":0.5}""", "9.5")]
    [InlineData("""{"type":"string","minLength":2,"maxLength":3,"pattern":"^[a-z]+$"}""", "\"abc\"")]
    [InlineData("""{"oneOf":[{"type":"string"},{"type":"integer"}]}""", "7")]
    [InlineData("""{"anyOf":[{"type":"string"},{"minimum":5}]}""", "6")]
    [InlineData("""{"not":{"type":"string"}}""", "1")]
    [InlineData("""{"if":{"properties":{"kind":{"const":"a"}}},"then":{"required":["a"]},"else":{"required":["b"]}}""", """{"kind":"z","b":1}""")]
    [InlineData("""{"$ref":"#/$defs/id","$defs":{"id":{"type":"integer","minimum":1}}}""", "3")]
    [InlineData("""{"additionalProperties":false,"properties":{"a":{}},"patternProperties":{"^x-":{}}}""", """{"a":1,"x-trace":2}""")]
    [InlineData("""{"prefixItems":[{"type":"string"},{"type":"integer"}],"items":false}""", """["a",1]""")]
    [InlineData("""{"contains":{"type":"integer"}}""", """["a",2]""")]
    [InlineData("true", """{"anything":1}""")]
    public void A_value_that_fits_passes(string schema, string json) => Assert.Null(Check(schema, json));

    [Theory]
    [InlineData("""{"type":"object","required":["id"]}""", """{"name":"x"}""", "$", "id")]
    [InlineData("""{"properties":{"id":{"type":"integer"}}}""", """{"id":"7"}""", "$.id", "integer")]
    [InlineData("""{"items":{"type":"integer"}}""", """[1,"2"]""", "$[1]", "integer")]
    [InlineData("""{"additionalProperties":false,"properties":{"a":{}}}""", """{"a":1,"b":2}""", "$", "b")]
    [InlineData("""{"type":"string","pattern":"^\\d+$"}""", "\"12a\"", "$", "pattern")]
    [InlineData("""{"minimum":5}""", "4", "$", "5")]
    [InlineData("""{"exclusiveMinimum":true,"minimum":5}""", "5", "$", "5")]
    [InlineData("""{"oneOf":[{"type":"number"},{"type":"integer"}]}""", "3", "$", "one")]
    [InlineData("""{"uniqueItems":true}""", "[1,1.0]", "$", "unique")]
    [InlineData("""{"type":"integer"}""", "1.5", "$", "integer")]
    [InlineData("""{"$ref":"#/definitions/x","definitions":{"x":{"type":"string"}}}""", "1", "$", "string")]
    [InlineData("false", "1", "$", "")]
    public void A_value_that_does_not_fit_names_where_and_why(string schema, string json, string where, string why)
    {
        string? error = Check(schema, json);
        Assert.NotNull(error);
        Assert.StartsWith("at " + where + ":", error);
        Assert.Contains(why, error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_self_referencing_schema_does_not_recurse_forever()
    {
        string? error = Check("""{"$ref":"#"}""", "1");
        Assert.NotNull(error);
        Assert.Contains("deep", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Lengths_count_characters_not_utf16_units()
    {
        // One emoji is one character to JSON Schema, though .NET holds it as two chars.
        Assert.Null(Check("""{"maxLength":1}""", "\"😀\""));
    }
}

public class SchemaAssertionTests
{
    private static ApiResponse Json(string body) => new()
    {
        StatusCode = 200,
        Body = Encoding.UTF8.GetBytes(body),
        Elapsed = TimeSpan.FromMilliseconds(5),
        Headers = new List<KeyValuePair<string, string>> { new("Content-Type", "application/json") }
    };

    [Fact]
    public void The_parser_takes_the_rest_of_the_line_as_the_schema()
    {
        var rule = AssertionParser.Parse("""body $ schema {"type":"object","required":["id"]}""");
        Assert.Equal(AssertTarget.Body, rule.Target);
        Assert.Equal(AssertOp.MatchesSchema, rule.Op);
        Assert.Equal("""{"type":"object","required":["id"]}""", rule.Value);
    }

    [Fact]
    public void The_parser_reads_a_schema_file()
    {
        string file = Path.Combine(Path.GetTempPath(), $"certapi-schema-{Guid.NewGuid():N}.json");
        File.WriteAllText(file, """{"type":"array"}""");
        try
        {
            var rule = AssertionParser.Parse($"body data.items matches-schema @{file}");
            Assert.Equal("data.items", rule.Path);
            Assert.Equal("""{"type":"array"}""", rule.Value);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void A_schema_that_is_not_json_is_a_parse_error()
    {
        Assert.Throws<FormatException>(() => AssertionParser.Parse("body $ schema {not json"));
    }

    [Fact]
    public void The_whole_body_or_a_path_is_checked_and_the_failure_says_where()
    {
        var whole = AssertionParser.Parse("""body $ schema {"required":["id"]}""");
        var nested = AssertionParser.Parse("""body data schema {"type":"array"}""");
        var results = AssertionEvaluator.Evaluate(new[] { whole, nested }, Json("""{"data":{"x":1}}"""));

        Assert.False(results[0].Passed);
        Assert.Contains("id", results[0].Actual);
        Assert.False(results[1].Passed);
        Assert.Contains("array", results[1].Actual);
        Assert.Contains("schema", results[0].Description);

        var pass = AssertionEvaluator.Evaluate(new[] { whole }, Json("""{"id":1}"""));
        Assert.True(pass[0].Passed);
    }

    [Fact]
    public void A_body_that_is_not_json_fails_rather_than_throwing()
    {
        var rule = AssertionParser.Parse("""body $ schema {"type":"object"}""");
        var result = AssertionEvaluator.Evaluate(new[] { rule }, Json("<html>"))[0];
        Assert.False(result.Passed);
    }
}

public class OpenApiResponseAssertionTests
{
    private const string Spec = """
    {
      "openapi": "3.0.3",
      "info": { "title": "Pets" },
      "servers": [ { "url": "https://pets.example" } ],
      "paths": {
        "/pets/{id}": {
          "get": {
            "summary": "Get pet",
            "responses": {
              "404": { "description": "nope" },
              "200": { "description": "ok", "content": { "application/json": { "schema": { "$ref": "#/components/schemas/Pet" } } } }
            }
          }
        },
        "/pets": {
          "post": { "summary": "Add pet", "responses": { "201": { "description": "made" } } }
        }
      },
      "components": { "schemas": {
        "Pet": { "type": "object", "required": ["id"], "properties": { "id": { "type": "integer" }, "owner": { "$ref": "#/components/schemas/Owner" } } },
        "Owner": { "type": "object", "properties": { "name": { "type": "string" } } },
        "Unused": { "type": "string" }
      } }
    }
    """;

    [Fact]
    public void Each_operation_asserts_its_documented_success_status_and_body_schema()
    {
        var c = OpenApiImporter.Parse(Spec);
        var get = c.Folders.SelectMany(f => f.Requests).Single(r => r.Name == "Get pet");

        var status = Assert.Single(get.Assertions, a => a.Target == AssertTarget.Status);
        Assert.Equal("200", status.Value);
        var schema = Assert.Single(get.Assertions, a => a.Op == AssertOp.MatchesSchema);
        Assert.Equal("$", schema.Path);
        // Only what the response reaches is embedded, so the rule stays readable.
        Assert.Contains("Owner", schema.Value);
        Assert.DoesNotContain("Unused", schema.Value);

        var add = c.Folders.SelectMany(f => f.Requests).Single(r => r.Name == "Add pet");
        Assert.Equal("201", Assert.Single(add.Assertions).Value);
    }

    [Fact]
    public void The_generated_schema_accepts_a_good_body_and_refuses_a_bad_one()
    {
        var c = OpenApiImporter.Parse(Spec);
        var rule = c.Folders.SelectMany(f => f.Requests).Single(r => r.Name == "Get pet")
                    .Assertions.Single(a => a.Op == AssertOp.MatchesSchema);
        using var good = JsonDocument.Parse("""{"id":1,"owner":{"name":"Ann"}}""");
        using var bad = JsonDocument.Parse("""{"id":1,"owner":{"name":5}}""");
        Assert.Null(JsonSchemaCheck.FirstError(rule.Value, good.RootElement));
        Assert.Contains("$.owner.name", JsonSchemaCheck.FirstError(rule.Value, bad.RootElement));
    }
}

public class RunReportJunitTests
{
    private static readonly DateTimeOffset When = new(2026, 7, 28, 9, 14, 0, TimeSpan.Zero);

    private static RunReportRow Row(string label, bool passed, IReadOnlyList<AssertionResult>? assertions = null,
                                    string? error = null, string url = "https://api.internal/orders?api_key=s3cret") =>
        new(label, "GET", url, error is null ? 200 : null, TimeSpan.FromMilliseconds(142), 512,
            passed, assertions ?? Array.Empty<AssertionResult>(), error, Array.Empty<(string, bool, string?)>());

    [Fact]
    public void Counts_cases_failures_errors_and_skips()
    {
        var report = new RunReport("checkout", new[]
        {
            Row("orders/list", true),
            Row("orders/create", false, new[] { new AssertionResult(false, "Status == 201", "500") }),
            Row("orders/pay", false, error: "connection refused")
        }, new[] { "orders/ship" }, TimeSpan.FromSeconds(1.5));

        var xml = XDocument.Parse(RunReportJunit.Render(report, When));
        var suite = xml.Root!.Element("testsuite")!;
        Assert.Equal("4", suite.Attribute("tests")!.Value);
        Assert.Equal("1", suite.Attribute("failures")!.Value);
        Assert.Equal("1", suite.Attribute("errors")!.Value);
        Assert.Equal("1", suite.Attribute("skipped")!.Value);
        Assert.Equal("1.5", suite.Attribute("time")!.Value);
        Assert.Equal("2026-07-28T09:14:00", suite.Attribute("timestamp")!.Value);

        var cases = suite.Elements("testcase").ToList();
        Assert.Equal("certapi.orders", cases[0].Attribute("classname")!.Value);
        var failure = cases[1].Element("failure")!;
        Assert.Equal("Status == 201", failure.Attribute("message")!.Value);
        Assert.Contains("got 500", failure.Value);
        Assert.NotNull(cases[2].Element("error"));
        Assert.NotNull(cases[3].Element("skipped"));
    }

    [Fact]
    public void Urls_are_redacted_unless_asked()
    {
        var report = new RunReport(null, new[] { Row("a", true) }, Array.Empty<string>(), TimeSpan.Zero);
        Assert.DoesNotContain("s3cret", RunReportJunit.Render(report, When));
        Assert.Contains("s3cret", RunReportJunit.Render(report, When, includeSecrets: true));
    }

    [Fact]
    public void Characters_xml_cannot_hold_are_dropped_not_fatal()
    {
        var report = new RunReport(null, new[] { Row("bell\u0007", false, error: "bad\u0001byte 😀") },
                                   Array.Empty<string>(), TimeSpan.Zero);
        string text = RunReportJunit.Render(report, When);
        var xml = XDocument.Parse(text);
        Assert.Equal("bell", xml.Descendants("testcase").Single().Attribute("name")!.Value);
        Assert.Contains("😀", text);
        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"utf-8\"?>", text);
    }
}
