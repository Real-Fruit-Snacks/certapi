using System.IO;
using System.Text;
using System.Text.Json;
using ApiTester.Core;

namespace ApiTester.Tests;

/// <summary>Proves the v1.62.0 encrypt-at-rest contract: <see cref="StateSecrets"/> and the
/// <see cref="AppState.SaveTo(string)"/>/<see cref="AppState.LoadFrom(string)"/> integration built on
/// it — round trip, migration from a legacy plaintext file, backup-before-first-rewrite, and
/// graceful degradation when a secret cannot be decrypted. "Never lose a user's data" is the whole
/// point of this release, so several of these tests are deliberately exhaustive rather than
/// representative.</summary>
public class SecretsAtRestTests
{
    private const string PlainToken = "session-token-plaintext-abc123";
    private const string PlainCookieValue = "cookie-plaintext-value-xyz789";
    private const string PlainTabSecret = "tab-auth-secret-plaintext";
    private const string PlainHistorySecret = "history-auth-secret-plaintext";
    private const string PlainRequestSecret = "collection-request-secret-plaintext";
    private const string PlainVariableValue = "secret-variable-plaintext-value";
    private const string PlainKnownGoodBody = "{\"ok\":true,\"stage\":\"known-good\"}";
    private const string PlainHistoryResponseBody = "{\"status\":\"history-body-plaintext\"}";

    private static string TempPath(string tag) =>
        Path.Combine(Path.GetTempPath(), $"certapi-secrets-{tag}-{Guid.NewGuid():N}.json");

    // Deletes the workspace file itself, any ".tmp" the atomic write might have left behind on a
    // failure, and every "<path>.<timestamp>.bak" a migrating save produced.
    private static void DeleteWithBackups(string path)
    {
        if (File.Exists(path)) File.Delete(path);
        if (File.Exists(path + ".tmp")) File.Delete(path + ".tmp");
        var dir = Path.GetDirectoryName(path)!;
        var name = Path.GetFileName(path);
        foreach (var f in Directory.GetFiles(dir, name + ".*.bak")) File.Delete(f);
    }

    /// <summary>A state carrying all six secret-bearing kinds, each holding a distinct known
    /// plaintext value.</summary>
    private static AppState BuildStateWithAllSecretKinds()
    {
        var state = new AppState();
        state.SessionTokens.Add(new SessionToken
        {
            Origin = "https://api.example.com:443", Token = PlainToken, Source = "oauth",
            CapturedUtc = DateTime.UtcNow
        });
        state.SessionCookies.Add(new SessionCookie
        {
            Origin = "https://intranet.corp:443", Name = "SESSIONID", Value = PlainCookieValue,
            Path = "/", Domain = "intranet.corp"
        });
        state.Tabs.Add(new RequestModel
        {
            Method = "POST", BaseUrl = "https://api.example.com", Path = "/users",
            AuthType = "Bearer", AuthSecret = PlainTabSecret, AuthUser = "tab-user"
        });
        state.History.Add(new HistoryEntry
        {
            Method = "GET", Url = "/status", AuthType = "Bearer", AuthSecret = PlainHistorySecret
        });
        var folder = new CollectionNode { IsFolder = true, Name = "api" };
        folder.Children.Add(new CollectionNode
        {
            Name = "Create user",
            Request = new RequestModel
            {
                Method = "POST", Path = "/users", AuthType = "Bearer", AuthSecret = PlainRequestSecret
            }
        });
        state.Collections.Add(folder);
        var env = new ApiEnvironment { Id = "e1", Name = "Dev" };
        env.Variables.Add(new Variable { Key = "access_token", Value = PlainVariableValue, Secret = true });
        env.Variables.Add(new Variable { Key = "host", Value = "dev.local", Secret = false });
        state.Environments.Add(env);
        return state;
    }

    /// <summary>A realistic v1.61.1 workspace: secrets stored in the clear (SchemaVersion 1) plus a
    /// broad spread of non-secret content, so the "never lose a user's data" tests are exhaustive.</summary>
    private static AppState BuildLegacyWorkspace()
    {
        var state = new AppState
        {
            SchemaVersion = 1,
            Theme = "Light",
            TimeoutSeconds = 45,
            AutoTokens = false,
            AutoCookies = false,
            ActiveEnvironmentId = "e1"
        };
        state.SavedBaseUrls.Add("https://api.example.com");

        state.SessionTokens.Add(new SessionToken
        {
            Origin = "https://api.example.com:443", Token = PlainToken, Source = "oauth",
            CapturedUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        });
        state.SessionCookies.Add(new SessionCookie
        {
            Origin = "https://intranet.corp:443", Name = "SESSIONID", Value = PlainCookieValue,
            Path = "/", Domain = "intranet.corp", Secure = true, HttpOnly = true
        });

        var tab = new RequestModel
        {
            Method = "POST", BaseUrl = "https://api.example.com", Path = "/users",
            AuthType = "Bearer", AuthSecret = PlainTabSecret, AuthUser = "tab-user",
            Headers = { new HeaderRow { Name = "X-T", Value = "1" } },
            QueryParams = { new ParamRow { Key = "q", Value = "2" } },
            Captures = { new CaptureRule { Enabled = true, Variable = "id", Source = CaptureSource.Body, Path = "data.id" } },
            Assertions = { new AssertionRule { Enabled = true, Target = AssertTarget.Status, Op = AssertOp.Equals, Value = "201" } }
        };
        state.Tabs.Add(tab);

        var folder = new CollectionNode { Id = "f1", IsFolder = true, Name = "api" };
        var leaf = new CollectionNode
        {
            Id = "n1", IsFolder = false, Name = "Create user",
            Request = new RequestModel
            {
                Method = "POST", Path = "/users", AuthType = "Bearer", AuthSecret = PlainRequestSecret
            }
        };
        leaf.RecordResult(201, new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc),
            new ResponseSnapshot(201, new List<KeyValuePair<string, string>>(),
                Encoding.UTF8.GetBytes("{\"ok\":true}"), "application/json"));
        folder.Children.Add(leaf);
        state.Collections.Add(folder);

        var env = new ApiEnvironment { Id = "e1", Name = "Dev" };
        env.Variables.Add(new Variable { Key = "access_token", Value = PlainVariableValue, Secret = true });
        env.Variables.Add(new Variable { Key = "host", Value = "dev.local", Secret = false });
        state.Environments.Add(env);

        state.History.Add(new HistoryEntry
        {
            Method = "GET", Url = "/status", StatusCode = 200,
            AuthType = "Bearer", AuthSecret = PlainHistorySecret
        });

        state.Chains.Add(new RequestChain
        {
            Id = "c1", Name = "login then browse", EnvironmentName = "Dev",
            Steps =
            {
                new ChainStep { RequestId = "n1" },
                new ChainStep { RequestId = "n2", StopOnFailure = false }
            }
        });

        return state;
    }

    /// <summary>A realistic v1.64.0 workspace: the six secret fields already encrypted per Windows
    /// user (SchemaVersion 2, this release's predecessor), but stored response bodies still in the
    /// clear — exactly the gap D2 closes. Written by hand with <see cref="JsonSerializer"/>, never
    /// through <see cref="AppState.SaveTo(string)"/>, which would already write today's format.</summary>
    private static AppState BuildV1_64Workspace()
    {
        var protector = SecretProtection.Default;
        var state = new AppState
        {
            SchemaVersion = 2,
            Theme = "Light",
            TimeoutSeconds = 45,
            AutoTokens = false,
            AutoCookies = false,
            ActiveEnvironmentId = "e1"
        };
        state.SavedBaseUrls.Add("https://api.example.com");

        state.SessionTokens.Add(new SessionToken
        {
            Origin = "https://api.example.com:443", Token = protector.Protect(PlainToken)!, Source = "oauth",
            CapturedUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        });
        state.SessionCookies.Add(new SessionCookie
        {
            Origin = "https://intranet.corp:443", Name = "SESSIONID", Value = protector.Protect(PlainCookieValue)!,
            Path = "/", Domain = "intranet.corp", Secure = true, HttpOnly = true
        });

        var tab = new RequestModel
        {
            Method = "POST", BaseUrl = "https://api.example.com", Path = "/users",
            AuthType = "Bearer", AuthSecret = protector.Protect(PlainTabSecret)!, AuthUser = "tab-user",
            Headers = { new HeaderRow { Name = "X-T", Value = "1" } },
            QueryParams = { new ParamRow { Key = "q", Value = "2" } },
            Captures = { new CaptureRule { Enabled = true, Variable = "id", Source = CaptureSource.Body, Path = "data.id" } },
            Assertions = { new AssertionRule { Enabled = true, Target = AssertTarget.Status, Op = AssertOp.Equals, Value = "201" } }
        };
        state.Tabs.Add(tab);

        var folder = new CollectionNode { Id = "f1", IsFolder = true, Name = "api" };
        var leaf = new CollectionNode
        {
            Id = "n1", IsFolder = false, Name = "Create user",
            Request = new RequestModel
            {
                Method = "POST", Path = "/users", AuthType = "Bearer", AuthSecret = protector.Protect(PlainRequestSecret)!
            }
        };
        leaf.RecordResult(201, new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc),
            new ResponseSnapshot(201, new List<KeyValuePair<string, string>>(),
                Encoding.UTF8.GetBytes(PlainKnownGoodBody), "application/json"));   // body still PLAINTEXT — v1.64.0 doesn't encrypt it
        folder.Children.Add(leaf);
        state.Collections.Add(folder);

        var env = new ApiEnvironment { Id = "e1", Name = "Dev" };
        env.Variables.Add(new Variable { Key = "access_token", Value = protector.Protect(PlainVariableValue)!, Secret = true });
        env.Variables.Add(new Variable { Key = "host", Value = "dev.local", Secret = false });
        state.Environments.Add(env);

        state.History.Add(new HistoryEntry
        {
            Method = "GET", Url = "/status", StatusCode = 200,
            AuthType = "Bearer", AuthSecret = protector.Protect(PlainHistorySecret)!,
            Response = new HistorySnapshot
            {
                StatusCode = 200, ContentType = "application/json",
                Body = Encoding.UTF8.GetBytes(PlainHistoryResponseBody)   // still PLAINTEXT — v1.64.0 doesn't encrypt it
            }
        });

        state.Chains.Add(new RequestChain
        {
            Id = "c1", Name = "login then browse", EnvironmentName = "Dev",
            Steps =
            {
                new ChainStep { RequestId = "n1" },
                new ChainStep { RequestId = "n2", StopOnFailure = false }
            }
        });

        return state;
    }

    private static byte[] DeterministicNoise(int seed)
    {
        var bytes = new byte[64];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    [Fact]
    public void A_stored_response_body_round_trips_through_save_and_load()
    {
        var path = TempPath("body-roundtrip");
        try
        {
            var textBody = Encoding.UTF8.GetBytes("{\"ok\":true,\"id\":42}");
            // Deliberately invalid UTF-8 (0xC3 with no continuation byte, plus a lone high byte),
            // so this proves arbitrary binary survives, not just text that happens to be bytes.
            var binaryBody = new byte[] { 0xFF, 0xFE, 0x00, 0x01, 0x80, 0x81, 0x7F, 0x00, 0xC3, 0x28 };

            var state = new AppState();
            state.History.Add(new HistoryEntry
            {
                Method = "GET",
                Url = "/status",
                Response = new HistorySnapshot
                {
                    StatusCode = 200,
                    ContentType = "application/json",
                    Body = textBody,
                    Headers = { new HeaderRow { Name = "X-Trace", Value = "abc" } }
                }
            });

            var folder = new CollectionNode { IsFolder = true, Name = "api" };
            var leaf = new CollectionNode { Name = "Create user", Request = new RequestModel { Method = "POST", Path = "/users" } };
            leaf.RecordResult(201, DateTime.UtcNow,
                new ResponseSnapshot(201,
                    new List<KeyValuePair<string, string>> { new("Content-Type", "application/octet-stream") },
                    binaryBody, "application/octet-stream"));
            folder.Children.Add(leaf);
            state.Collections.Add(folder);

            state.SaveTo(path);

            // The round trip alone would "pass" trivially today, since bodies are stored in the
            // clear — this is what makes the test genuinely red before the feature exists. A body
            // is a byte[], which System.Text.Json always Base64-encodes regardless of encryption,
            // so a literal substring search for the marker in the raw file text would never find
            // it either way; decoding the persisted bytes is what actually proves encryption ran.
            using (var doc = JsonDocument.Parse(File.ReadAllText(path)))
            {
                var storedHistoryBody = doc.RootElement.GetProperty("History")[0].GetProperty("Response")
                    .GetProperty("Body").GetBytesFromBase64();
                Assert.True(SecretProtection.LooksProtected(Encoding.UTF8.GetString(storedHistoryBody)));

                var storedKnownGoodBody = doc.RootElement.GetProperty("Collections")[0].GetProperty("Children")[0]
                    .GetProperty("KnownGood").GetProperty("Body").GetBytesFromBase64();
                Assert.True(SecretProtection.LooksProtected(Encoding.UTF8.GetString(storedKnownGoodBody)));
            }

            var back = AppState.LoadFrom(path);

            var h = Assert.Single(back.History);
            Assert.NotNull(h.Response);
            Assert.Equal(textBody, h.Response!.Body);
            Assert.Equal(200, h.Response.StatusCode);
            Assert.Equal("application/json", h.Response.ContentType);
            Assert.Equal("X-Trace", Assert.Single(h.Response.Headers).Name);

            var backLeaf = Assert.Single(Assert.Single(back.Collections).Children);
            Assert.NotNull(backLeaf.KnownGood);
            Assert.Equal(binaryBody, backLeaf.KnownGood!.Body);
            Assert.Equal(201, backLeaf.KnownGood.StatusCode);
            Assert.Equal("application/octet-stream", backLeaf.KnownGood.ContentType);
            Assert.Equal("Content-Type", Assert.Single(backLeaf.KnownGood.Headers).Key);

            Assert.Empty(back.SecretWarnings);
        }
        finally { DeleteWithBackups(path); }
    }

    [Fact]
    public void Saved_file_never_contains_a_stored_response_body_in_the_clear()
    {
        var path = TempPath("body-plaintext");
        try
        {
            const string plainBody = "{\"access_token\":\"body-token-plaintext-abc\"}";
            var plainBodyBytes = Encoding.UTF8.GetBytes(plainBody);

            var folder = new CollectionNode { IsFolder = true, Name = "api" };
            var leaf = new CollectionNode { Name = "login", Request = new RequestModel { Method = "POST", Path = "/login" } };
            leaf.RecordResult(200, DateTime.UtcNow,
                new ResponseSnapshot(200, new List<KeyValuePair<string, string>>(), plainBodyBytes, "application/json"));
            folder.Children.Add(leaf);
            var state = new AppState();
            state.Collections.Add(folder);

            state.SaveTo(path);

            var raw = File.ReadAllText(path);
            Assert.DoesNotContain(plainBody, raw);

            // A body is a byte[], which System.Text.Json always Base64-encodes regardless of
            // encryption, so the raw file's "Body" string never literally contains
            // SecretProtection.Prefix — that marker is nested inside this outer Base64 layer.
            // Decoding the persisted bytes is what actually proves the body is no longer the
            // plaintext, and that what replaced it looks like ciphertext.
            using var doc = JsonDocument.Parse(raw);
            var storedBody = doc.RootElement.GetProperty("Collections")[0].GetProperty("Children")[0]
                .GetProperty("KnownGood").GetProperty("Body").GetBytesFromBase64();
            Assert.NotEqual(plainBodyBytes, storedBody);
            Assert.True(SecretProtection.LooksProtected(Encoding.UTF8.GetString(storedBody)));
        }
        finally { DeleteWithBackups(path); }
    }

    [Fact]
    public void All_six_secret_kinds_round_trip_through_save_and_load()
    {
        var path = TempPath("roundtrip");
        try
        {
            BuildStateWithAllSecretKinds().SaveTo(path);

            var back = AppState.LoadFrom(path);

            Assert.Equal(PlainToken, Assert.Single(back.SessionTokens).Token);
            Assert.Equal(PlainCookieValue, Assert.Single(back.SessionCookies).Value);
            Assert.Equal(PlainTabSecret, Assert.Single(back.Tabs).AuthSecret);
            Assert.Equal(PlainHistorySecret, Assert.Single(back.History).AuthSecret);
            var leaf = Assert.Single(Assert.Single(back.Collections).Children);
            Assert.Equal(PlainRequestSecret, leaf.Request!.AuthSecret);
            var secretVar = Assert.Single(back.Environments).Variables.Single(v => v.Secret);
            Assert.Equal(PlainVariableValue, secretVar.Value);
            Assert.Empty(back.SecretWarnings);
        }
        finally { DeleteWithBackups(path); }
    }

    [Fact]
    public void Saved_file_never_contains_secret_plaintext_and_carries_the_encryption_marker()
    {
        var path = TempPath("encrypted");
        try
        {
            BuildStateWithAllSecretKinds().SaveTo(path);

            var raw = File.ReadAllText(path);
            Assert.DoesNotContain(PlainToken, raw);
            Assert.DoesNotContain(PlainCookieValue, raw);
            Assert.DoesNotContain(PlainTabSecret, raw);
            Assert.DoesNotContain(PlainHistorySecret, raw);
            Assert.DoesNotContain(PlainRequestSecret, raw);
            Assert.DoesNotContain(PlainVariableValue, raw);
            Assert.Contains(SecretProtection.Prefix, raw);
        }
        finally { DeleteWithBackups(path); }
    }

    [Fact]
    public void Non_secret_fields_stay_in_the_clear_alongside_the_encrypted_secrets()
    {
        var path = TempPath("plaintext-fields");
        try
        {
            BuildStateWithAllSecretKinds().SaveTo(path);

            var raw = File.ReadAllText(path);
            Assert.Contains("access_token", raw);              // the secret variable's Key
            Assert.Contains("host", raw);                      // the non-secret variable's Key
            Assert.Contains("dev.local", raw);                 // the non-secret variable's value
            Assert.Contains("tab-user", raw);                  // AuthUser
            Assert.Contains("https://api.example.com:443", raw); // the token's Origin
            Assert.Contains("SESSIONID", raw);                 // the cookie's Name
            Assert.Contains("/users", raw);                    // a request URL
        }
        finally { DeleteWithBackups(path); }
    }

    [Fact]
    public void A_legacy_plaintext_workspace_loads_with_every_field_intact()
    {
        var path = TempPath("legacy");
        try
        {
            File.WriteAllText(path,
                JsonSerializer.Serialize(BuildLegacyWorkspace(), new JsonSerializerOptions { WriteIndented = true }));

            var back = AppState.LoadFrom(path);

            Assert.Equal("Light", back.Theme);
            Assert.Equal(45, back.TimeoutSeconds);
            Assert.False(back.AutoTokens);
            Assert.False(back.AutoCookies);
            Assert.Equal("https://api.example.com", Assert.Single(back.SavedBaseUrls));
            Assert.Equal("e1", back.ActiveEnvironmentId);

            var token = Assert.Single(back.SessionTokens);
            Assert.Equal(PlainToken, token.Token);
            Assert.Equal("oauth", token.Source);
            Assert.Equal("https://api.example.com:443", token.Origin);

            var cookie = Assert.Single(back.SessionCookies);
            Assert.Equal(PlainCookieValue, cookie.Value);
            Assert.Equal("SESSIONID", cookie.Name);
            Assert.True(cookie.Secure);
            Assert.True(cookie.HttpOnly);

            var tab = Assert.Single(back.Tabs);
            Assert.Equal(PlainTabSecret, tab.AuthSecret);
            Assert.Equal("tab-user", tab.AuthUser);
            Assert.Equal("X-T", Assert.Single(tab.Headers).Name);
            Assert.Equal("q", Assert.Single(tab.QueryParams).Key);
            Assert.Equal("id", Assert.Single(tab.Captures).Variable);
            Assert.Equal("201", Assert.Single(tab.Assertions).Value);

            var backFolder = Assert.Single(back.Collections);
            Assert.Equal("api", backFolder.Name);
            var backLeaf = Assert.Single(backFolder.Children);
            Assert.Equal(PlainRequestSecret, backLeaf.Request!.AuthSecret);
            Assert.Equal(201, backLeaf.LastStatusCode);
            Assert.True(backLeaf.IsKnownGood);
            Assert.NotNull(backLeaf.KnownGood);

            var backEnv = Assert.Single(back.Environments);
            var secretVar = backEnv.Variables.Single(v => v.Key == "access_token");
            Assert.Equal(PlainVariableValue, secretVar.Value);
            Assert.True(secretVar.Secret);
            var plainVar = backEnv.Variables.Single(v => v.Key == "host");
            Assert.Equal("dev.local", plainVar.Value);
            Assert.False(plainVar.Secret);

            var h = Assert.Single(back.History);
            Assert.Equal(200, h.StatusCode);
            Assert.Equal(PlainHistorySecret, h.AuthSecret);

            var chain = Assert.Single(back.Chains);
            Assert.Equal("login then browse", chain.Name);
            Assert.Equal("Dev", chain.EnvironmentName);
            Assert.Equal(2, chain.Steps.Count);
            Assert.Equal("n1", chain.Steps[0].RequestId);
            Assert.True(chain.Steps[0].StopOnFailure);
            Assert.Equal("n2", chain.Steps[1].RequestId);
            Assert.False(chain.Steps[1].StopOnFailure);

            Assert.Empty(back.SecretWarnings);
        }
        finally { DeleteWithBackups(path); }
    }

    [Fact]
    public void SaveTo_backs_up_the_legacy_file_once_before_the_first_encrypted_rewrite()
    {
        var path = TempPath("backup");
        try
        {
            File.WriteAllText(path,
                JsonSerializer.Serialize(BuildLegacyWorkspace(), new JsonSerializerOptions { WriteIndented = true }));
            var originalBytes = File.ReadAllBytes(path);

            var loaded = AppState.LoadFrom(path);
            var result = loaded.SaveTo(path);

            Assert.NotNull(result.BackupPath);
            Assert.True(result.SecretsEncrypted);
            Assert.Equal(Path.GetDirectoryName(path), Path.GetDirectoryName(result.BackupPath));
            Assert.EndsWith(".bak", result.BackupPath);
            Assert.Equal(originalBytes, File.ReadAllBytes(result.BackupPath!));

            using (var doc = JsonDocument.Parse(File.ReadAllText(path)))
                Assert.Equal(AppState.CurrentSchemaVersion, doc.RootElement.GetProperty("SchemaVersion").GetInt32());
            Assert.Contains(SecretProtection.Prefix, File.ReadAllText(path));

            var reloaded = AppState.LoadFrom(path);
            Assert.Equal(PlainToken, Assert.Single(reloaded.SessionTokens).Token);
            Assert.Equal(PlainCookieValue, Assert.Single(reloaded.SessionCookies).Value);
            Assert.Equal(PlainTabSecret, Assert.Single(reloaded.Tabs).AuthSecret);
            Assert.Equal(PlainHistorySecret, Assert.Single(reloaded.History).AuthSecret);
            Assert.Equal(PlainRequestSecret,
                Assert.Single(Assert.Single(reloaded.Collections).Children).Request!.AuthSecret);
            Assert.Equal(PlainVariableValue,
                Assert.Single(reloaded.Environments).Variables.Single(v => v.Secret).Value);

            var second = reloaded.SaveTo(path);
            Assert.Null(second.BackupPath);
        }
        finally { DeleteWithBackups(path); }
    }

    [Fact]
    public void A_v1_64_0_workspace_loads_with_every_field_intact_and_upgrades_on_the_next_save()
    {
        var path = TempPath("migration-v1-64");
        try
        {
            var legacy = BuildV1_64Workspace();
            File.WriteAllText(path, JsonSerializer.Serialize(legacy, new JsonSerializerOptions { WriteIndented = true }));
            var originalBytes = File.ReadAllBytes(path);

            Assert.True(AppState.NeedsSecretUpgrade(path));

            var back = AppState.LoadFrom(path);

            Assert.Equal("Light", back.Theme);
            Assert.Equal(45, back.TimeoutSeconds);
            Assert.False(back.AutoTokens);
            Assert.False(back.AutoCookies);
            Assert.Equal("https://api.example.com", Assert.Single(back.SavedBaseUrls));
            Assert.Equal("e1", back.ActiveEnvironmentId);

            var token = Assert.Single(back.SessionTokens);
            Assert.Equal(PlainToken, token.Token);
            Assert.Equal("oauth", token.Source);
            Assert.Equal("https://api.example.com:443", token.Origin);

            var cookie = Assert.Single(back.SessionCookies);
            Assert.Equal(PlainCookieValue, cookie.Value);
            Assert.Equal("SESSIONID", cookie.Name);
            Assert.True(cookie.Secure);
            Assert.True(cookie.HttpOnly);

            var tab = Assert.Single(back.Tabs);
            Assert.Equal(PlainTabSecret, tab.AuthSecret);
            Assert.Equal("tab-user", tab.AuthUser);
            Assert.Equal("X-T", Assert.Single(tab.Headers).Name);
            Assert.Equal("q", Assert.Single(tab.QueryParams).Key);
            Assert.Equal("id", Assert.Single(tab.Captures).Variable);
            Assert.Equal("201", Assert.Single(tab.Assertions).Value);

            var backFolder = Assert.Single(back.Collections);
            Assert.Equal("api", backFolder.Name);
            var backLeaf = Assert.Single(backFolder.Children);
            Assert.Equal(PlainRequestSecret, backLeaf.Request!.AuthSecret);
            Assert.Equal(201, backLeaf.LastStatusCode);
            Assert.True(backLeaf.IsKnownGood);
            Assert.NotNull(backLeaf.KnownGood);
            Assert.Equal(Encoding.UTF8.GetBytes(PlainKnownGoodBody), backLeaf.KnownGood!.Body);
            Assert.Equal(201, backLeaf.KnownGood.StatusCode);
            Assert.Equal("application/json", backLeaf.KnownGood.ContentType);

            var backEnv = Assert.Single(back.Environments);
            var secretVar = backEnv.Variables.Single(v => v.Key == "access_token");
            Assert.Equal(PlainVariableValue, secretVar.Value);
            Assert.True(secretVar.Secret);
            var plainVar = backEnv.Variables.Single(v => v.Key == "host");
            Assert.Equal("dev.local", plainVar.Value);
            Assert.False(plainVar.Secret);

            var h = Assert.Single(back.History);
            Assert.Equal(200, h.StatusCode);
            Assert.Equal(PlainHistorySecret, h.AuthSecret);
            Assert.NotNull(h.Response);
            Assert.Equal(Encoding.UTF8.GetBytes(PlainHistoryResponseBody), h.Response!.Body);
            Assert.Equal(200, h.Response.StatusCode);
            Assert.Equal("application/json", h.Response.ContentType);

            var chain = Assert.Single(back.Chains);
            Assert.Equal("login then browse", chain.Name);
            Assert.Equal("Dev", chain.EnvironmentName);
            Assert.Equal(2, chain.Steps.Count);
            Assert.Equal("n1", chain.Steps[0].RequestId);
            Assert.True(chain.Steps[0].StopOnFailure);
            Assert.Equal("n2", chain.Steps[1].RequestId);
            Assert.False(chain.Steps[1].StopOnFailure);

            Assert.Empty(back.SecretWarnings);

            var result = back.SaveTo(path);
            Assert.NotNull(result.BackupPath);
            Assert.Equal(originalBytes, File.ReadAllBytes(result.BackupPath!));

            var rewritten = File.ReadAllText(path);
            using (var doc = JsonDocument.Parse(rewritten))
                Assert.Equal(AppState.CurrentSchemaVersion, doc.RootElement.GetProperty("SchemaVersion").GetInt32());

            // The bodies are byte[] (always Base64-encoded by System.Text.Json regardless of
            // encryption), so a raw substring search for the plaintext is a weak guard at best —
            // decoding what is actually stored is what proves the rewrite really encrypted them.
            using (var doc = JsonDocument.Parse(rewritten))
            {
                var storedKnownGoodBody = doc.RootElement.GetProperty("Collections")[0].GetProperty("Children")[0]
                    .GetProperty("KnownGood").GetProperty("Body").GetBytesFromBase64();
                Assert.NotEqual(Encoding.UTF8.GetBytes(PlainKnownGoodBody), storedKnownGoodBody);
                Assert.True(SecretProtection.LooksProtected(Encoding.UTF8.GetString(storedKnownGoodBody)));

                var storedHistoryBody = doc.RootElement.GetProperty("History")[0].GetProperty("Response")
                    .GetProperty("Body").GetBytesFromBase64();
                Assert.NotEqual(Encoding.UTF8.GetBytes(PlainHistoryResponseBody), storedHistoryBody);
                Assert.True(SecretProtection.LooksProtected(Encoding.UTF8.GetString(storedHistoryBody)));
            }

            var reloaded = AppState.LoadFrom(path);
            Assert.Equal(Encoding.UTF8.GetBytes(PlainKnownGoodBody),
                Assert.Single(Assert.Single(reloaded.Collections).Children).KnownGood!.Body);
            Assert.Equal(Encoding.UTF8.GetBytes(PlainHistoryResponseBody), Assert.Single(reloaded.History).Response!.Body);
            Assert.Equal(PlainToken, Assert.Single(reloaded.SessionTokens).Token);
            Assert.Empty(reloaded.SecretWarnings);

            var second = reloaded.SaveTo(path);
            Assert.Null(second.BackupPath);
        }
        finally { DeleteWithBackups(path); }
    }

    [Fact]
    public void A_failed_backup_aborts_the_save_and_leaves_the_only_copy_untouched()
    {
        var path = TempPath("backup-fails");
        try
        {
            var legacy = BuildV1_64Workspace();
            File.WriteAllText(path, JsonSerializer.Serialize(legacy, new JsonSerializerOptions { WriteIndented = true }));
            var originalBytes = File.ReadAllBytes(path);

            var loaded = AppState.LoadFrom(path);

            using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Assert.ThrowsAny<IOException>(() => loaded.SaveTo(path));
            }

            Assert.Equal(originalBytes, File.ReadAllBytes(path));
            Assert.False(File.Exists(path + ".tmp"));
        }
        finally { DeleteWithBackups(path); }
    }

    [Fact]
    public void NeedsSecretUpgrade_reflects_schema_version_and_file_state()
    {
        var path = TempPath("needs-upgrade");
        try
        {
            File.WriteAllText(path,
                JsonSerializer.Serialize(BuildLegacyWorkspace(), new JsonSerializerOptions { WriteIndented = true }));
            Assert.True(AppState.NeedsSecretUpgrade(path));

            var loaded = AppState.LoadFrom(path);
            loaded.SaveTo(path);
            Assert.False(AppState.NeedsSecretUpgrade(path));

            File.WriteAllText(path, "{ not json");
            Assert.True(AppState.NeedsSecretUpgrade(path));

            Assert.False(AppState.NeedsSecretUpgrade(path + "-does-not-exist"));
        }
        finally { DeleteWithBackups(path); }
    }

    [Fact]
    public void Undecryptable_secrets_are_dropped_or_emptied_without_throwing_and_everything_else_survives()
    {
        var path = TempPath("undecryptable");
        try
        {
            var noise = SecretProtection.Prefix + Convert.ToBase64String(DeterministicNoise(20260727));

            var state = new AppState { SchemaVersion = 2 };
            state.SessionTokens.Add(new SessionToken { Origin = "https://api.example.com:443", Token = noise, Source = "oauth" });
            state.SessionCookies.Add(new SessionCookie { Origin = "https://intranet.corp:443", Name = "SESSIONID", Value = noise });

            state.Tabs.Add(new RequestModel { Method = "POST", Path = "/broken", AuthType = "Bearer", AuthSecret = noise });
            state.Tabs.Add(new RequestModel { Method = "GET", Path = "/fine", AuthType = "Bearer", AuthSecret = "still-plaintext-until-next-save" });

            var folder = new CollectionNode { IsFolder = true, Name = "api" };
            folder.Children.Add(new CollectionNode { Name = "Create user", Request = new RequestModel { Method = "POST", Path = "/users" } });
            state.Collections.Add(folder);

            state.History.Add(new HistoryEntry { Method = "GET", Url = "/ok", StatusCode = 200 });

            var env = new ApiEnvironment { Id = "e1", Name = "Dev" };
            env.Variables.Add(new Variable { Key = "broken_secret", Value = noise, Secret = true });
            env.Variables.Add(new Variable { Key = "host", Value = "dev.local", Secret = false });
            state.Environments.Add(env);

            File.WriteAllText(path, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));

            var back = AppState.LoadFrom(path);

            Assert.Empty(back.SessionTokens);
            Assert.Empty(back.SessionCookies);
            Assert.Null(back.Tabs[0].AuthSecret);
            Assert.Equal("still-plaintext-until-next-save", back.Tabs[1].AuthSecret);

            var backEnv = Assert.Single(back.Environments);
            var brokenVar = backEnv.Variables.Single(v => v.Key == "broken_secret");
            Assert.Equal("", brokenVar.Value);
            Assert.True(brokenVar.Secret);
            var hostVar = backEnv.Variables.Single(v => v.Key == "host");
            Assert.Equal("dev.local", hostVar.Value);
            Assert.False(hostVar.Secret);

            Assert.Equal("api", Assert.Single(back.Collections).Name);
            Assert.Equal("Create user", Assert.Single(Assert.Single(back.Collections).Children).Name);
            Assert.Equal(200, Assert.Single(back.History).StatusCode);

            Assert.Equal(5, back.SecretWarnings.Count);
            Assert.Contains(back.SecretWarnings, w => w.Contains("captured token for https://api.example.com:443") && w.Contains("dropped"));
            Assert.Contains(back.SecretWarnings, w => w.Contains("captured cookie \"SESSIONID\"") && w.Contains("dropped"));
            Assert.Contains(back.SecretWarnings, w => w.Contains("open tab 1") && w.Contains("POST /broken"));
            Assert.Contains(back.SecretWarnings, w => w.Contains("environment variable \"broken_secret\"") && w.Contains("Dev"));
            Assert.Equal(
                "Secrets are encrypted for your Windows user — a workspace file copied from another user or machine cannot be read.",
                back.SecretWarnings[^1]);
        }
        finally { DeleteWithBackups(path); }
    }

    [Fact]
    public void An_undecryptable_stored_body_is_left_empty_with_a_named_warning_and_the_rest_of_the_workspace_opens()
    {
        var path = TempPath("undecryptable-body");
        try
        {
            // Valid Base64 of 64 random bytes, so it reads as ciphertext-shaped, but not a real
            // DPAPI blob, so decryption genuinely fails on this machine — same approach as the
            // undecryptable-secret test above, applied to a stored body.
            var noiseBody = Encoding.UTF8.GetBytes(
                SecretProtection.Prefix + Convert.ToBase64String(DeterministicNoise(20260728)));

            var state = new AppState { SchemaVersion = AppState.CurrentSchemaVersion };

            var folder = new CollectionNode { IsFolder = true, Name = "api" };
            var leaf = new CollectionNode { Name = "Create user", Request = new RequestModel { Method = "POST", Path = "/users" } };
            leaf.RecordResult(201, DateTime.UtcNow,
                new ResponseSnapshot(201, new List<KeyValuePair<string, string>>(), noiseBody, "application/json"));
            folder.Children.Add(leaf);
            state.Collections.Add(folder);

            state.History.Add(new HistoryEntry
            {
                Method = "GET", Url = "/status", StatusCode = 200,
                Response = new HistorySnapshot { StatusCode = 200, ContentType = "application/json", Body = noiseBody }
            });

            var env = new ApiEnvironment { Id = "e1", Name = "Dev" };
            env.Variables.Add(new Variable { Key = "host", Value = "dev.local", Secret = false });
            state.Environments.Add(env);

            File.WriteAllText(path, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));

            var back = AppState.LoadFrom(path);

            var backLeaf = Assert.Single(Assert.Single(back.Collections).Children);
            Assert.NotNull(backLeaf.KnownGood);
            Assert.Empty(backLeaf.KnownGood!.Body);
            Assert.Equal(201, backLeaf.KnownGood.StatusCode);
            Assert.Equal("application/json", backLeaf.KnownGood.ContentType);
            Assert.Equal("api", Assert.Single(back.Collections).Name);
            Assert.Equal("Create user", backLeaf.Name);

            var h = Assert.Single(back.History);
            Assert.NotNull(h.Response);
            Assert.Empty(h.Response!.Body);
            Assert.Equal(200, h.Response.StatusCode);
            Assert.Equal("application/json", h.Response.ContentType);
            Assert.Equal(200, h.StatusCode);

            var backEnv = Assert.Single(back.Environments);
            Assert.Equal("dev.local", backEnv.Variables.Single(v => v.Key == "host").Value);

            Assert.Equal(3, back.SecretWarnings.Count);
            Assert.Contains(back.SecretWarnings,
                w => w.Contains("stored response body on history entry 1") && w.Contains("GET /status"));
            Assert.Contains(back.SecretWarnings,
                w => w.Contains("stored known-good response on saved request \"api/Create user\""));
            Assert.Equal(
                "Secrets are encrypted for your Windows user — a workspace file copied from another user or machine cannot be read.",
                back.SecretWarnings[^1]);
        }
        finally { DeleteWithBackups(path); }
    }

    [Fact]
    public void When_the_protector_is_unavailable_secrets_are_saved_in_the_clear_without_data_loss()
    {
        var path = TempPath("unavailable");
        try
        {
            var protector = new UnavailableSecretProtector();

            var result = BuildStateWithAllSecretKinds().SaveTo(path, protector);

            Assert.False(result.SecretsEncrypted);
            var raw = File.ReadAllText(path);
            Assert.Contains(PlainToken, raw);
            Assert.Contains(PlainCookieValue, raw);
            Assert.Contains(PlainTabSecret, raw);
            Assert.Contains(PlainHistorySecret, raw);
            Assert.Contains(PlainRequestSecret, raw);
            Assert.Contains(PlainVariableValue, raw);

            var backSameProtector = AppState.LoadFrom(path, protector);
            Assert.Equal(PlainToken, Assert.Single(backSameProtector.SessionTokens).Token);
            Assert.Equal(PlainVariableValue, Assert.Single(backSameProtector.Environments).Variables.Single(v => v.Secret).Value);
            Assert.Empty(backSameProtector.SecretWarnings);

            var backDefaultProtector = AppState.LoadFrom(path);
            Assert.Equal(PlainToken, Assert.Single(backDefaultProtector.SessionTokens).Token);
            Assert.Equal(PlainRequestSecret,
                Assert.Single(Assert.Single(backDefaultProtector.Collections).Children).Request!.AuthSecret);
            Assert.Empty(backDefaultProtector.SecretWarnings);
        }
        finally { DeleteWithBackups(path); }
    }

    [Fact]
    public void Schema_version_one_does_not_rerun_the_auth_none_to_auto_migration()
    {
        var path = TempPath("guard-v1");
        try
        {
            var state = new AppState { SchemaVersion = 1 };
            state.Tabs.Add(new RequestModel { AuthType = "None" });
            File.WriteAllText(path, JsonSerializer.Serialize(state));

            var back = AppState.LoadFrom(path);
            Assert.Equal("None", back.Tabs[0].AuthType);
        }
        finally { DeleteWithBackups(path); }
    }

    [Fact]
    public void Schema_version_zero_still_migrates_none_auth_to_auto()
    {
        var path = TempPath("guard-v0");
        try
        {
            var state = new AppState { SchemaVersion = 0 };
            state.Tabs.Add(new RequestModel { AuthType = "None" });
            File.WriteAllText(path, JsonSerializer.Serialize(state));

            var back = AppState.LoadFrom(path);
            Assert.Equal("Auto", back.Tabs[0].AuthType);
        }
        finally { DeleteWithBackups(path); }
    }

    [Fact]
    public void PeekTheme_reads_theme_streaming_including_bom_missing_and_corrupt_files()
    {
        var path = TempPath("peek-theme");
        try
        {
            Assert.Null(AppState.PeekTheme(path));

            // Large nested collections before Theme to force multi-chunk scanning.
            var state = BuildLegacyWorkspace();
            state.Theme = "Light";
            for (int i = 0; i < 40; i++)
                state.History.Add(new HistoryEntry { Method = "GET", Url = $"/items/{i}", Body = new string('a', 512) });
            var json = JsonSerializer.Serialize(state);
            File.WriteAllBytes(path, Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(json)).ToArray());

            Assert.Equal("Light", AppState.PeekTheme(path));

            // Corrupt JSON falls back cleanly to null without throwing.
            File.WriteAllText(path, "{ \"Theme\": ");
            Assert.Null(AppState.PeekTheme(path));
        }
        finally { DeleteWithBackups(path); }
    }

    [Fact]
    public void NeedsSecretUpgrade_handles_empty_non_object_and_trailing_whitespace()
    {
        var path = TempPath("upgrade-edge");
        try
        {
            Assert.False(AppState.NeedsSecretUpgrade(path));

            File.WriteAllText(path, "");
            Assert.True(AppState.NeedsSecretUpgrade(path));

            File.WriteAllText(path, "[]");
            Assert.True(AppState.NeedsSecretUpgrade(path));

            File.WriteAllText(path, $"{{ \"SchemaVersion\": {AppState.CurrentSchemaVersion} }}   \r\n");
            Assert.False(AppState.NeedsSecretUpgrade(path));

            File.WriteAllText(path, $"{{ \"SchemaVersion\": {AppState.CurrentSchemaVersion}, \"Theme\": \"Dark\" }} trailing-garbage");
            Assert.True(AppState.NeedsSecretUpgrade(path));

            File.WriteAllText(path, "{ \"SchemaVersion\": 2 }");
            Assert.True(AppState.NeedsSecretUpgrade(path));
        }
        finally { DeleteWithBackups(path); }
    }

    [Fact]
    public void SaveTo_with_stripWindowGeometry_preserves_live_geometry_and_reuses_protector()
    {
        var path1 = TempPath("geom-1");
        var path2 = TempPath("geom-2");
        try
        {
            var state = BuildStateWithAllSecretKinds();
            state.WindowWidth = 1400;
            state.WindowHeight = 900;
            state.WindowLeft = 100;
            state.WindowTop = 50;
            state.WindowMaximized = true;

            state.SaveTo(path1);
            var loaded = AppState.LoadFrom(path1);
            var firstRaw = File.ReadAllText(path1);

            loaded.SaveTo(path2, SecretProtection.Default, stripWindowGeometry: true);

            // Live state keeps its window geometry intact.
            Assert.Equal(1400, loaded.WindowWidth);
            Assert.Equal(900, loaded.WindowHeight);
            Assert.Equal(100, loaded.WindowLeft);
            Assert.Equal(50, loaded.WindowTop);
            Assert.True(loaded.WindowMaximized);

            // Exported file stripped geometry and reused the stored ciphertext for unchanged secrets.
            var exported = AppState.LoadFrom(path2);
            Assert.Null(exported.WindowWidth);
            Assert.Null(exported.WindowHeight);
            Assert.Null(exported.WindowLeft);
            Assert.Null(exported.WindowTop);
            Assert.False(exported.WindowMaximized);
            Assert.Equal(PlainToken, Assert.Single(exported.SessionTokens).Token);

            using var doc1 = JsonDocument.Parse(firstRaw);
            using var doc2 = JsonDocument.Parse(File.ReadAllText(path2));
            Assert.Equal(
                doc1.RootElement.GetProperty("SessionTokens")[0].GetProperty("Token").GetString(),
                doc2.RootElement.GetProperty("SessionTokens")[0].GetProperty("Token").GetString());
        }
        finally
        {
            DeleteWithBackups(path1);
            DeleteWithBackups(path2);
        }
    }
}
