using System.Text.Json;
using ApiTester.Core;

namespace ApiTester.Tests.Sweep;

/// <summary>A stored response's credential headers — a login's Set-Cookie, a token header — are
/// secrets like the captured token itself: encrypted at rest, stripped from an export.</summary>
public class StoredHeaderSecretTests
{
    private sealed class Rot : ISecretProtector
    {
        public bool IsAvailable => true;
        // Padded to the length real ciphertext has, which LooksProtected requires.
        public string? Protect(string plaintext) =>
            SecretProtection.Prefix + Convert.ToBase64String(new byte[32].Concat(System.Text.Encoding.UTF8.GetBytes(plaintext)).ToArray());
        public string? Unprotect(string stored) =>
            System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(stored[SecretProtection.Prefix.Length..])[32..]);
    }

    private static AppState State()
    {
        var state = new AppState();
        var h = new HistoryEntry { Method = "POST", BaseUrl = "https://api", Url = "/login" };
        h.Response = new HistorySnapshot { StatusCode = 200 };
        h.Response.Headers.Add(new HeaderRow { Name = "Set-Cookie", Value = "session=S3CRET" });
        h.Response.Headers.Add(new HeaderRow { Name = "X-Auth-Token", Value = "T0KEN" });
        h.Response.Headers.Add(new HeaderRow { Name = "Content-Type", Value = "application/json" });
        state.History.Add(h);
        var node = new CollectionNode { Name = "login", Request = new RequestModel { Method = "POST", BaseUrl = "https://api", Path = "/login" } };
        node.KnownGood = new ResponseSnapshot(200, new KeyValuePair<string, string>[] { new("Set-Cookie", "session=S3CRET"), new("Content-Type", "application/json") }, Array.Empty<byte>(), "application/json");
        state.Collections.Add(node);
        return state;
    }

    [Fact]
    public void Credential_headers_of_stored_responses_are_encrypted_and_come_back()
    {
        var state = State();
        Assert.True(StateSecrets.Protect(state, new Rot()));
        string json = JsonSerializer.Serialize(state);
        Assert.DoesNotContain("S3CRET", json);
        Assert.DoesNotContain("T0KEN", json);
        Assert.Contains("application/json", json);

        var warnings = new List<string>();
        StateSecrets.Unprotect(state, new Rot(), warnings);
        Assert.Empty(warnings);
        Assert.Equal("session=S3CRET", state.History[0].Response!.Headers[0].Value);
        Assert.Equal("T0KEN", state.History[0].Response!.Headers[1].Value);
        Assert.Equal("session=S3CRET", state.Collections[0].KnownGood!.Headers[0].Value);
    }

    [Fact]
    public void An_export_strips_them()
    {
        var state = State();
        var summary = StateSecrets.Strip(state);
        Assert.DoesNotContain("S3CRET", JsonSerializer.Serialize(state));
        Assert.Equal(2, summary.ResponseHeaders);
        Assert.Contains("credential header", summary.Describe());
    }

    [Theory]
    [InlineData("Authorization", true)]
    [InlineData("x-api-key", true)]
    [InlineData("X-Auth-Token", true)]
    [InlineData("X-CSRF-Token", true)]
    [InlineData("Set-Cookie", true)]
    [InlineData("WWW-Authenticate", false)]
    [InlineData("Content-Type", false)]
    [InlineData("X-Request-Id", false)]
    public void Which_headers_are_secrets(string name, bool secret) => Assert.Equal(secret, SecretHeaders.IsSecret(name));
}
