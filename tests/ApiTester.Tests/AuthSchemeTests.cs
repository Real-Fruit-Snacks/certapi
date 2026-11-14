using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using ApiTester.Cli;
using ApiTester.Cli.Commands;
using ApiTester.Core;

namespace ApiTester.Tests;

/// <summary>AWS Signature v4 against AWS's own published test vectors, and HTTP Digest end to end.</summary>
public class AwsSigV4Tests
{
    // The credentials and clock every case in AWS's Signature v4 test suite uses.
    private static readonly AwsSigV4Options Suite = new("AKIDEXAMPLE", "wJalrXUtnFEMI/K7MDENG+bPxRfiCYEXAMPLEKEY", "us-east-1", "service");
    private static readonly DateTimeOffset SuiteTime = new(2015, 8, 30, 12, 36, 0, TimeSpan.Zero);

    private static string Authorization(string method, string url, byte[]? body = null, params (string, string)[] headers) =>
        AwsSigV4.Sign(method, new Uri(url), headers.Select(h => new KeyValuePair<string, string>(h.Item1, h.Item2)),
            body ?? Array.Empty<byte>(), Suite, SuiteTime).Single(h => h.Key == "Authorization").Value;

    [Fact]
    public void Get_vanilla()
    {
        Assert.Equal(
            "AWS4-HMAC-SHA256 Credential=AKIDEXAMPLE/20150830/us-east-1/service/aws4_request, SignedHeaders=host;x-amz-date, " +
            "Signature=5fa00fa31553b73ebf1942676e86291e8372ff2a2260956d9b8aae1d763fbf31",
            Authorization("GET", "https://example.amazonaws.com/"));
    }

    [Fact]
    public void Get_vanilla_query_order_key_case()
    {
        Assert.EndsWith("Signature=b97d918cfa904a5beff61c982a1b6f458b799221646efd99d3219ec94cdf2500",
            Authorization("GET", "https://example.amazonaws.com/?Param2=value2&Param1=value1"));
    }

    [Fact]
    public void The_date_header_and_a_session_token_are_added_and_signed()
    {
        var added = AwsSigV4.Sign("GET", new Uri("https://example.amazonaws.com/"), [], [],
            Suite with { SessionToken = "tok" }, SuiteTime);
        Assert.Contains(added, h => h.Key == "X-Amz-Date" && h.Value == "20150830T123600Z");
        Assert.Contains(added, h => h.Key == "X-Amz-Security-Token" && h.Value == "tok");
        Assert.Contains("SignedHeaders=host;x-amz-date;x-amz-security-token,", added.Single(h => h.Key == "Authorization").Value);
        Assert.DoesNotContain(added, h => h.Key == "X-Amz-Content-Sha256");   // S3 only
    }

    [Fact]
    public void S3_carries_the_payload_hash()
    {
        var added = AwsSigV4.Sign("PUT", new Uri("https://bucket.s3.amazonaws.com/a b.txt"), [], Encoding.UTF8.GetBytes("x"),
            Suite with { Service = "s3" }, SuiteTime);
        Assert.Contains(added, h => h.Key == "X-Amz-Content-Sha256" && h.Value == Convert.ToHexStringLower(SHA256.HashData("x"u8)));
    }

    [Fact]
    public void Unsigned_headers_do_not_change_the_signature()
    {
        Assert.Equal(Authorization("GET", "https://example.amazonaws.com/"),
            Authorization("GET", "https://example.amazonaws.com/", null, ("User-Agent", "certapi"), ("Accept", "*/*")));
        Assert.NotEqual(Authorization("GET", "https://example.amazonaws.com/"),
            Authorization("GET", "https://example.amazonaws.com/", null, ("X-Amz-Target", "a")));
    }

    [Theory]
    [InlineData("execute-api.eu-west-1.amazonaws.com", "eu-west-1", "execute-api")]
    [InlineData("abc123.execute-api.ap-south-1.amazonaws.com", "ap-south-1", "execute-api")]
    [InlineData("iam.amazonaws.com", "us-east-1", "iam")]
    [InlineData("sqs.cn-north-1.amazonaws.com.cn", "cn-north-1", "sqs")]
    public void Region_and_service_come_from_an_aws_host(string host, string region, string service) =>
        Assert.Equal((region, service), AwsSigV4Options.ScopeFromHost(host));

    [Fact]
    public void A_non_aws_host_names_no_scope() => Assert.Null(AwsSigV4Options.ScopeFromHost("api.internal.corp"));

    [Fact]
    public async Task The_client_signs_the_request_it_sends()
    {
        await using var server = new RawHttpServer(_ => "HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        var response = await new ApiClient().SendAsync(new ApiRequest
        {
            Method = HttpMethod.Post,
            Url = server.BaseUrl + "/items?b=2&a=1",
            Body = "{\"x\":1}",
            ContentType = "application/json",
            AwsSigV4 = Suite
        }, null);
        Assert.Equal(200, response.StatusCode);
        var seen = Assert.Single(server.Requests);
        string auth = seen.Header("Authorization")!;
        Assert.StartsWith("AWS4-HMAC-SHA256 Credential=AKIDEXAMPLE/", auth);
        Assert.Contains("SignedHeaders=content-type;host;x-amz-date,", auth);
        Assert.NotNull(seen.Header("X-Amz-Date"));
    }

    [Fact]
    public void Send_reads_curls_spelling_and_the_environment()
    {
        var env = new Dictionary<string, string?> { ["AWS_ACCESS_KEY_ID"] = "AKID", ["AWS_SECRET_ACCESS_KEY"] = "s", ["AWS_SESSION_TOKEN"] = "t" };
        var fromEnv = SendCommand.SigV4Options("aws:amz", null, "https://sqs.eu-west-2.amazonaws.com/", k => env.GetValueOrDefault(k));
        Assert.Equal(("AKID", "eu-west-2", "sqs", "t"), (fromEnv.AccessKeyId, fromEnv.Region, fromEnv.Service, fromEnv.SessionToken));

        var typed = SendCommand.SigV4Options("aws:amz:us-west-2:execute-api", "KEY:SECRET", "https://api.corp/x", _ => null);
        Assert.Equal(("KEY", "SECRET", "us-west-2", "execute-api", (string?)null),
            (typed.AccessKeyId, typed.SecretAccessKey, typed.Region, typed.Service, typed.SessionToken));

        Assert.Throws<CliUsageException>(() => SendCommand.SigV4Options("osc:osc", "K:S", "https://api.corp/", _ => null));
        Assert.Throws<CliUsageException>(() => SendCommand.SigV4Options("aws:amz", "K:S", "https://api.corp/", _ => null));
    }
}

public class DigestAuthTests
{
    private const string Realm = "certapi-test";
    private const string Nonce = "dcd98b7102dd2f0e8b11d0f600bfb0c093";

    private static string Md5(string s) => Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(s)));

    private static Dictionary<string, string> Params(string header)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(
                     header["Digest ".Length..], "(\\w+)=(\"([^\"]*)\"|[^,\\s]+)"))
            d[m.Groups[1].Value] = m.Groups[3].Success ? m.Groups[3].Value : m.Groups[2].Value;
        return d;
    }

    private static RawHttpServer Server(string user, string password) => new(req =>
    {
        string? auth = req.Header("Authorization");
        if (auth is not null && auth.StartsWith("Digest ", StringComparison.Ordinal))
        {
            var p = Params(auth);
            string ha1 = Md5($"{user}:{Realm}:{password}");
            string ha2 = Md5($"{req.RequestLine.Split(' ')[0]}:{p["uri"]}");
            string expected = Md5($"{ha1}:{p["nonce"]}:{p["nc"]}:{p["cnonce"]}:{p["qop"]}:{ha2}");
            if (p["username"] == user && p["response"] == expected)
                return "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok";
        }
        return "HTTP/1.1 401 Unauthorized\r\n" +
               $"WWW-Authenticate: Digest realm=\"{Realm}\", qop=\"auth\", nonce=\"{Nonce}\", opaque=\"5ccc069c403ebaf9f0171e9517f40e41\"\r\n" +
               "Content-Length: 0\r\nConnection: close\r\n\r\n";
    });

    [Fact]
    public async Task A_digest_challenge_is_answered_and_the_password_never_travels()
    {
        await using var server = Server("ann", "s3cret");
        var response = await new ApiClient().SendAsync(new ApiRequest
        {
            Method = HttpMethod.Get,
            Url = server.BaseUrl + "/private",
            WindowsAuth = WindowsAuthOptions.ForDigest("ann", "s3cret")
        }, null);

        Assert.Equal(200, response.StatusCode);
        Assert.All(server.Requests, r => Assert.DoesNotContain("s3cret", string.Join("\n", r.Headers.Select(h => h.Value))));
    }

    [Fact]
    public async Task A_wrong_password_ends_in_the_servers_401()
    {
        await using var server = Server("ann", "s3cret");
        var response = await new ApiClient().SendAsync(new ApiRequest
        {
            Method = HttpMethod.Get,
            Url = server.BaseUrl + "/private",
            WindowsAuth = WindowsAuthOptions.ForDigest("ann", "wrong")
        }, null);
        Assert.Equal(401, response.StatusCode);
    }

    [Fact]
    public async Task Digest_credentials_never_answer_a_basic_challenge()
    {
        await using var server = new RawHttpServer(_ =>
            "HTTP/1.1 401 Unauthorized\r\nWWW-Authenticate: Basic realm=\"x\"\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        var response = await new ApiClient().SendAsync(new ApiRequest
        {
            Method = HttpMethod.Get,
            Url = server.BaseUrl + "/",
            WindowsAuth = WindowsAuthOptions.ForDigest("ann", "s3cret")
        }, null);
        Assert.Equal(401, response.StatusCode);
        Assert.All(server.Requests, r => Assert.Null(r.Header("Authorization")));
    }

    [Fact]
    public async Task Send_digest_uses_the_basic_pair()
    {
        await using var server = Server("ann", "s3cret");
        var o = new StringWriter();
        var e = new StringWriter();
        int code = CliApp.Run(["send", server.BaseUrl + "/private", "--digest", "-u", "ann:s3cret"], new StringReader(""), o, e, new MemoryStream(),
            new CliServices { LiveStatePath = Path.Combine(Path.GetTempPath(), $"certapi-none-{Guid.NewGuid():N}.json") });
        Assert.True(code == 0, e.ToString());
        Assert.DoesNotContain(server.Requests, r => (r.Header("Authorization") ?? "").StartsWith("Basic", StringComparison.Ordinal));

        int usage = CliApp.Run(["send", server.BaseUrl, "--digest"], new StringReader(""), new StringWriter(), e, new MemoryStream(), new CliServices());
        Assert.Equal(2, usage);
    }
}

public class ImportedAuthTests
{
    [Fact]
    public void Postman_digest_and_awsv4_map_to_their_own_types()
    {
        var result = PostmanImport.Parse("""
            { "info": { "name": "c", "schema": "https://schema.getpostman.com/json/collection/v2.1.0/collection.json" },
              "item": [
                { "name": "d", "request": { "method": "GET", "url": "https://x.test/",
                  "auth": { "type": "digest", "digest": [ { "key": "username", "value": "ann" }, { "key": "password", "value": "pw" } ] } } },
                { "name": "a", "request": { "method": "GET", "url": "https://x.test/",
                  "auth": { "type": "awsv4", "awsv4": [ { "key": "accessKey", "value": "AK" }, { "key": "secretKey", "value": "SK" },
                    { "key": "region", "value": "eu-west-1" }, { "key": "service", "value": "execute-api" }, { "key": "sessionToken", "value": "T" } ] } } }
              ] }
            """);
        Assert.Empty(result.Warnings);
        var d = result.Root.Children[0].Request!;
        Assert.Equal(("Digest", "ann", "pw"), (d.AuthType, d.AuthUser, d.AuthSecret));
        var a = result.Root.Children[1].Request!;
        Assert.Equal(("AwsSigV4", "AK", "SK", "eu-west-1/execute-api"), (a.AuthType, a.AuthUser, a.AuthSecret, a.AuthScope));
        Assert.Contains(a.Headers, h => h.Name == "X-Amz-Security-Token" && h.Value == "T");
    }

    [Fact]
    public void Insomnia_digest_and_iam_map_to_their_own_types()
    {
        var result = InsomniaImport.Parse("""
            { "_type": "export", "__export_format": 4, "resources": [
                { "_id": "wrk_1", "_type": "workspace", "name": "w" },
                { "_id": "req_1", "_type": "request", "parentId": "wrk_1", "name": "d", "method": "GET", "url": "https://x.test/",
                  "authentication": { "type": "digest", "username": "ann", "password": "pw" } },
                { "_id": "req_2", "_type": "request", "parentId": "wrk_1", "name": "a", "method": "GET", "url": "https://x.test/",
                  "authentication": { "type": "iam", "accessKeyId": "AK", "secretAccessKey": "SK", "region": "us-east-2", "service": "sqs" } }
            ] }
            """);
        Assert.Empty(result.Warnings);
        var byName = result.Root.Children.ToDictionary(c => c.Name, c => c.Request!);
        Assert.Equal("Digest", byName["d"].AuthType);
        Assert.Equal(("AwsSigV4", "us-east-2/sqs"), (byName["a"].AuthType, byName["a"].AuthScope));
    }

    [Fact]
    public void A_saved_aws_request_without_a_readable_region_says_so()
    {
        var m = new RequestModel { AuthType = "AwsSigV4", AuthUser = "AK", AuthSecret = "SK" };
        var (_, aws, _) = SavedAuth.For(m, s => s, "https://api.internal.corp/", out var problem);
        Assert.Null(aws);
        Assert.Contains("region", problem);

        var (_, fromHost, _) = SavedAuth.For(m, s => s, "https://lambda.eu-central-1.amazonaws.com/", out problem);
        Assert.Null(problem);
        Assert.Equal(("eu-central-1", "lambda"), (fromHost!.Region, fromHost.Service));
    }
}
