using System.IO;
using System.Net.Http;
using ApiTester.Core;

namespace ApiTester.Tests;

/// <summary>What a followed redirect may carry to another origin: neither the user's Windows
/// credentials nor a Cookie / Proxy-Authorization header they typed for the first host.</summary>
public class RedirectCredentialTests
{
    private static string Challenge(RawRequest r) =>
        r.Header("Authorization") is null
            ? RawHttpServer.Response(401, "Unauthorized", "", "WWW-Authenticate: Basic realm=\"x\"")
            : RawHttpServer.Response(200, "OK", "in");

    [Fact]
    public async Task Windows_credentials_never_answer_a_basic_challenge_even_from_the_origin()
    {
        // Windows auth is Negotiate/NTLM; answering Basic would hand over the domain password.
        await using var a = new RawHttpServer(Challenge);
        var resp = await new ApiClient().SendAsync(new ApiRequest
        {
            Method = HttpMethod.Get,
            Url = a.BaseUrl + "/login",
            WindowsAuth = new WindowsAuthOptions(false, "CORP", "alice", "P@ssw0rd"),
        }, null);
        Assert.Equal(401, resp.StatusCode);
        Assert.All(a.Requests, r => Assert.Null(r.Header("Authorization")));
    }

    [Fact]
    public async Task Windows_credentials_are_not_offered_to_a_host_a_redirect_points_at()
    {
        await using var b = new RawHttpServer(Challenge);
        await using var a = new RawHttpServer(_ => RawHttpServer.Response(302, "Found", "", $"Location: {b.BaseUrl}/steal"));
        var resp = await new ApiClient().SendAsync(new ApiRequest
        {
            Method = HttpMethod.Get,
            Url = a.BaseUrl + "/start",
            WindowsAuth = new WindowsAuthOptions(false, "CORP", "alice", "P@ssw0rd"),
        }, null);
        Assert.Equal(401, resp.StatusCode);
        Assert.NotEmpty(b.Requests);
        Assert.All(b.Requests, r => Assert.Null(r.Header("Authorization")));
    }

    [Fact]
    public async Task A_typed_cookie_and_proxy_authorization_stay_with_the_host_they_were_typed_for()
    {
        await using var b = new RawHttpServer(_ => RawHttpServer.Response(200, "OK", "b"));
        await using var a = new RawHttpServer(r => r.Path == "/start"
            ? RawHttpServer.Response(302, "Found", "", "Location: /same")
            : r.Path == "/same"
                ? RawHttpServer.Response(302, "Found", "", $"Location: {b.BaseUrl}/other")
                : RawHttpServer.Response(404, "Not Found"));
        var resp = await new ApiClient().SendAsync(new ApiRequest
        {
            Method = HttpMethod.Get,
            Url = a.BaseUrl + "/start",
            Headers = new KeyValuePair<string, string>[] { new("Cookie", "session=SECRET"), new("Proxy-Authorization", "Basic eDp5") },
        }, null);
        Assert.Equal(200, resp.StatusCode);
        Assert.All(a.Requests, r => Assert.Equal("session=SECRET", r.Header("Cookie")));   // same origin: kept
        var other = Assert.Single(b.Requests);
        Assert.Null(other.Header("Cookie"));
        Assert.Null(other.Header("Proxy-Authorization"));
    }
}

public class OAuthRedirectTests
{
    [Fact]
    public async Task A_token_request_is_not_replayed_to_where_a_redirect_points()
    {
        await using var b = new RawHttpServer(_ => RawHttpServer.Response(200, "OK", "{\"access_token\":\"x\"}", "Content-Type: application/json"));
        await using var a = new RawHttpServer(_ => RawHttpServer.Response(307, "Temporary Redirect", "", $"Location: {b.BaseUrl}/steal"));
        var result = await OAuthClient.RequestTokenAsync(new OAuthRequest
        {
            Grant = OAuthGrant.Password, TokenEndpoint = a.BaseUrl + "/token",
            ClientId = "cid", ClientSecret = "CLIENTSECRET", Username = "alice", Password = "pw",
        });
        Assert.False(result.Success);
        Assert.Equal("redirected", result.Error);
        Assert.Empty(b.Requests);
    }
}

public class ContentHeaderForwardingTests
{
    [Fact]
    public async Task Body_headers_given_as_headers_reach_the_server()
    {
        await using var server = new RawHttpServer(_ => RawHttpServer.Response(200, "OK", "ok"));
        var resp = await new ApiClient().SendAsync(new ApiRequest
        {
            Method = HttpMethod.Post,
            Url = server.BaseUrl + "/x",
            Body = "<a/>",
            Headers = new KeyValuePair<string, string>[] { new("Content-Type", "application/xml"), new("Content-Language", "de") },
        }, null);
        Assert.Equal(200, resp.StatusCode);
        var got = Assert.Single(server.Requests);
        Assert.StartsWith("application/xml", got.Header("Content-Type"));
        Assert.Equal("de", got.Header("Content-Language"));
    }

    [Fact]
    public async Task The_gateway_forwards_content_encoding_with_the_body()
    {
        await using var upstream = new RawHttpServer(_ => RawHttpServer.Response(200, "OK", "ok"));
        using var gateway = new MtlsGateway(new Uri(upstream.BaseUrl + "/"), null, false, TimeSpan.FromSeconds(10));
        var body = new MemoryStream(new byte[] { 0x1f, 0x8b, 1, 2, 3 });
        var resp = await gateway.ForwardAsync(new GatewayRequest("POST", "/x",
            new KeyValuePair<string, string>[] { new("Content-Encoding", "gzip"), new("X-Custom", "1") }, body, "application/json") { ContentLength = 5 }, default);
        Assert.Equal(200, resp.StatusCode);
        resp.Lifetime.Dispose();
        var got = Assert.Single(upstream.Requests);
        Assert.Equal("gzip", got.Header("Content-Encoding"));
        Assert.Equal("1", got.Header("X-Custom"));
    }
}
