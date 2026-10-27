using System.Net.Http;
using ApiTester.Core;

namespace ApiTester.Tests.Sweep;

public class ExportSecretTests
{
    [Theory]
    [InlineData("{\"user\":\"alice\",\"password\":\"HUNTER2\",\"nested\":{\"client_secret\":\"CS\"}}", "application/json")]
    [InlineData("grant_type=password&username=alice&password=HUNTER2&client_secret=CS", "application/x-www-form-urlencoded")]
    public void Credential_fields_in_a_body_are_redacted(string body, string type)
    {
        string redacted = MarkdownSecrets.RedactBody(body, type);
        Assert.DoesNotContain("HUNTER2", redacted);
        Assert.DoesNotContain("\"CS\"", redacted);
        Assert.DoesNotContain("=CS", redacted);
        Assert.Contains("alice", redacted);
        Assert.Equal(body, MarkdownSecrets.RedactBody(body, type, includeSecrets: true));
    }

    [Fact]
    public void A_variable_reference_is_not_a_secret_and_a_plain_body_is_untouched()
    {
        Assert.Equal("{\"password\":\"{{pw}}\"}", MarkdownSecrets.RedactBody("{\"password\":\"{{pw}}\"}", "application/json"));
        Assert.Equal("hello password", MarkdownSecrets.RedactBody("hello password", "text/plain"));
    }

    [Fact]
    public void An_openapi_export_carries_no_credential_examples()
    {
        var collection = new ParsedCollection { Name = "c" };
        collection.Requests.Add(new ParsedRequest
        {
            Method = "POST",
            Url = "https://api.example/login?api_key=QUERYSECRET&page=2",
            Headers = { new("X-Api-Key", "HEADERSECRET"), new("Cookie", "sid=COOKIESECRET"), new("X-Trace", "keep") },
            Body = "{\"password\":\"BODYSECRET\"}",
            ContentType = "application/json",
        });
        string json = OpenApiExporter.ToJson(collection);
        foreach (var secret in new[] { "QUERYSECRET", "HEADERSECRET", "COOKIESECRET", "BODYSECRET" })
            Assert.DoesNotContain(secret, json);
        Assert.Contains("keep", json);
    }

    [Fact]
    public void A_doctor_note_for_an_unparseable_url_does_not_put_its_credentials_in_the_title()
    {
        var report = new DoctorReport("https://svc:hunter2@api.example.com:44x/orders?api_key=SECRET", Array.Empty<DoctorStage>());
        string note = DoctorMarkdown.Render(report, DateTimeOffset.UtcNow);
        Assert.DoesNotContain("hunter2", note);
        Assert.DoesNotContain("SECRET", note);
    }
}
