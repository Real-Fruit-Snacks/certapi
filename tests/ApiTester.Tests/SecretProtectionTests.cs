using ApiTester.Core;

namespace ApiTester.Tests;

public class SecretProtectionTests
{
    [Fact]
    public void Protect_then_unprotect_returns_the_original_string()
    {
        var protector = new DpapiSecretProtector();
        const string plaintext = "a plain secret with Unicode ☺, spaces, and more";

        var stored = protector.Protect(plaintext);
        Assert.NotNull(stored);
        Assert.Equal(plaintext, protector.Unprotect(stored!));
    }

    [Fact]
    public void Protect_then_unprotect_round_trips_a_very_long_value()
    {
        var protector = new DpapiSecretProtector();
        var plaintext = new string('x', 50_000) + "☺" + new string('y', 50_000);

        var stored = protector.Protect(plaintext);
        Assert.NotNull(stored);
        Assert.Equal(plaintext, protector.Unprotect(stored!));
    }

    [Fact]
    public void Protected_form_starts_with_the_marker_and_never_contains_the_plaintext()
    {
        var protector = new DpapiSecretProtector();
        const string plaintext = "super-secret-value-do-not-leak";

        var stored = protector.Protect(plaintext);

        Assert.NotNull(stored);
        Assert.StartsWith(SecretProtection.Prefix, stored, StringComparison.Ordinal);
        Assert.DoesNotContain(plaintext, stored);
    }

    [Fact]
    public void Protecting_the_same_plaintext_twice_produces_different_stored_strings_that_both_decrypt()
    {
        var protector = new DpapiSecretProtector();
        const string plaintext = "same-plaintext-each-time";

        var first = protector.Protect(plaintext);
        var second = protector.Protect(plaintext);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotEqual(first, second);
        Assert.Equal(plaintext, protector.Unprotect(first!));
        Assert.Equal(plaintext, protector.Unprotect(second!));
    }

    [Fact]
    public void Protect_then_unprotect_round_trips_the_empty_string()
    {
        // An empty secret is still a secret: it must come back as "", not null, or a caller
        // would mistake it for an undecryptable value and drop it.
        var protector = new DpapiSecretProtector();

        var stored = protector.Protect("");

        Assert.NotNull(stored);
        Assert.Equal("", protector.Unprotect(stored!));
    }

    [Fact]
    public void LooksProtected_accepts_exactly_the_documented_32_byte_minimum()
    {
        // The boundary itself, pinned so it cannot drift: 32 decoded bytes looks protected,
        // 31 does not.
        Assert.True(SecretProtection.LooksProtected(
            SecretProtection.Prefix + Convert.ToBase64String(new byte[32])));
        Assert.False(SecretProtection.LooksProtected(
            SecretProtection.Prefix + Convert.ToBase64String(new byte[31])));
    }

    [Theory]
    [InlineData("this is legacy plaintext with no marker at all")]
    [InlineData("enc:v1:")]
    [InlineData("enc:v1:not-valid-base64!!!")]
    public void Unprotect_returns_null_without_throwing_for_unusable_input(string stored)
    {
        var protector = new DpapiSecretProtector();
        Assert.Null(protector.Unprotect(stored));
    }

    [Fact]
    public void Unprotect_returns_null_for_base64_of_random_bytes_that_are_not_a_real_blob()
    {
        var protector = new DpapiSecretProtector();
        var randomBytes = new byte[64];
        new Random(12345).NextBytes(randomBytes);
        var stored = SecretProtection.Prefix + Convert.ToBase64String(randomBytes);

        Assert.Null(protector.Unprotect(stored));
    }

    [Fact]
    public void LooksProtected_is_true_only_for_real_protected_output()
    {
        var protector = new DpapiSecretProtector();
        var stored = protector.Protect("something to protect");

        Assert.NotNull(stored);
        Assert.True(SecretProtection.LooksProtected(stored));

        Assert.False(SecretProtection.LooksProtected(null));
        Assert.False(SecretProtection.LooksProtected(""));
        Assert.False(SecretProtection.LooksProtected("hello"));
        Assert.False(SecretProtection.LooksProtected("enc:v1:"));
        Assert.False(SecretProtection.LooksProtected("enc:v1:AAAA")); // valid base64, far too short
    }

    [Fact]
    public void Unavailable_protector_reports_unavailable_and_returns_null_from_both_operations()
    {
        var protector = new UnavailableSecretProtector();

        Assert.False(protector.IsAvailable);
        Assert.Null(protector.Protect("anything"));
        Assert.Null(protector.Unprotect("enc:v1:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA="));
    }

    [Fact]
    public void Default_protector_is_available_and_round_trips_on_this_windows_test_machine()
    {
        // The test suite targets net10.0-windows and runs on Windows CI/dev machines only, so
        // asserting IsAvailable directly (rather than skipping) is safe here.
        Assert.True(SecretProtection.Default.IsAvailable);

        const string plaintext = "default-protector-round-trip";
        var stored = SecretProtection.Default.Protect(plaintext);

        Assert.NotNull(stored);
        Assert.Equal(plaintext, SecretProtection.Default.Unprotect(stored!));
    }
}
