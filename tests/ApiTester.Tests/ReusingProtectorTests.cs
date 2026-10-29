using System.IO;
using System.Text;
using ApiTester.Core;

namespace ApiTester.Tests;

/// <summary>Saving a workspace encrypts only what changed: every DPAPI call is a call into lsass,
/// and a workspace holds tokens, cookies, credential headers and known-good bodies.</summary>
public class ReusingProtectorTests
{
    /// <summary>Counts its calls; each Protect gives a different stored form, as DPAPI does.</summary>
    private sealed class Counting : ISecretProtector
    {
        public int Protects, Unprotects;
        public bool IsAvailable => true;
        public string? Protect(string plaintext)
        {
            int n = Interlocked.Increment(ref Protects);
            var salt = BitConverter.GetBytes(n).Concat(new byte[28]).ToArray();
            return SecretProtection.Prefix + Convert.ToBase64String(salt.Concat(Encoding.UTF8.GetBytes(plaintext)).ToArray());
        }
        public string? Unprotect(string stored)
        {
            Interlocked.Increment(ref Unprotects);
            return Encoding.UTF8.GetString(Convert.FromBase64String(stored[SecretProtection.Prefix.Length..])[32..]);
        }
    }

    private static AppState Workspace()
    {
        var state = new AppState();
        state.SessionTokens.Add(new SessionToken { Origin = "https://api.corp", Token = "tok-1" });
        state.SessionCookies.Add(new SessionCookie { Origin = "https://api.corp", Name = "sid", Value = "cookie-1" });
        var node = new CollectionNode { Name = "orders", Request = new RequestModel { Method = "GET", BaseUrl = "https://api.corp", Path = "/orders" } };
        node.KnownGood = new ResponseSnapshot(200, Array.Empty<KeyValuePair<string, string>>(), Encoding.UTF8.GetBytes(new string('b', 100_000)), "application/json");
        state.Collections.Add(node);
        return state;
    }

    [Fact]
    public void A_save_after_a_load_encrypts_only_what_changed()
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        try
        {
            var dpapi = new Counting();
            Workspace().SaveTo(path, dpapi);
            Assert.Equal(3, dpapi.Protects);   // token, cookie, body
            string first = File.ReadAllText(path);

            var loaded = AppState.LoadFrom(path, dpapi);
            Assert.Equal("tok-1", loaded.SessionTokens[0].Token);
            dpapi.Protects = 0;

            loaded.SaveTo(path, dpapi);
            Assert.Equal(0, dpapi.Protects);    // nothing changed: every stored form is the one read
            Assert.Equal(first, File.ReadAllText(path));

            loaded.SessionTokens[0].Token = "tok-2";
            loaded.SaveTo(path, dpapi);
            Assert.Equal(1, dpapi.Protects);    // only the new token
            var again = AppState.LoadFrom(path, dpapi);
            Assert.Equal("tok-2", again.SessionTokens[0].Token);
            Assert.Equal("cookie-1", again.SessionCookies[0].Value);
            Assert.Equal(100_000, again.Collections[0].KnownGood!.Body.Length);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Two_fields_holding_one_value_keep_different_stored_forms()
    {
        // As DPAPI gives them: the file must not show which secrets are equal.
        var inner = new Counting();
        var reusing = new ReusingProtector(inner);
        reusing.BeginPass();
        string first = reusing.Protect("same")!, second = reusing.Protect("same")!;
        reusing.EndPass();
        Assert.NotEqual(first, second);

        reusing.BeginPass();
        var again = new[] { reusing.Protect("same")!, reusing.Protect("same")! };
        reusing.EndPass();
        Assert.Equal(new[] { first, second }.OrderBy(s => s), again.OrderBy(s => s));   // both reused, neither twice
        Assert.Equal(2, inner.Protects);
    }

    [Fact]
    public void A_value_no_longer_saved_is_forgotten_and_a_huge_one_is_never_kept()
    {
        var inner = new Counting();
        var reusing = new ReusingProtector(inner);
        reusing.BeginPass();
        reusing.Protect("kept");
        reusing.Protect("dropped");
        reusing.Protect(new string('x', ReusingProtector.LargestRemembered));   // its stored form is longer still
        reusing.EndPass();
        Assert.Equal(2, reusing.Remembered);

        reusing.BeginPass();
        reusing.Protect("kept");
        reusing.EndPass();
        Assert.Equal(1, reusing.Remembered);   // "dropped" was in no field this save
    }

    [Fact]
    public void The_reuse_is_by_value_and_the_value_itself_is_not_kept()
    {
        var inner = new Counting();
        var reusing = new ReusingProtector(inner);
        string stored = inner.Protect("secret")!;
        Assert.Equal("secret", reusing.Unprotect(stored));
        Assert.Equal(stored, reusing.Protect("secret"));
        Assert.NotEqual(stored, reusing.Protect("another"));
        Assert.Equal(2, inner.Protects);
    }
}
