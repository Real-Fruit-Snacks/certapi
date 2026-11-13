using ApiTester.Cli.Mcp;

namespace ApiTester.Tests.Cli;

public class HostAllowlistTests
{
    [Theory]
    [InlineData("https://internal.corp/api", true)]
    [InlineData("https://INTERNAL.CORP/api", true)]           // case-insensitive
    [InlineData("https://api.internal.corp/v1", true)]         // subdomain
    [InlineData("https://internal.corp.evil.com/x", false)]    // look-alike suffix
    [InlineData("https://evil.com/x", false)]                  // different host
    [InlineData("https://notinternal.corp/x", false)]          // not a dotted subdomain
    [InlineData("http://internal.corp/x", true)]               // http allowed too
    [InlineData("ftp://internal.corp/x", false)]               // non-http scheme
    [InlineData("/relative/path", false)]                      // not absolute
    [InlineData("not a url", false)]
    public void Enforces_the_allowlist(string url, bool expected)
    {
        var allow = new HostAllowlist(new[] { "internal.corp", "api.other" });
        Assert.Equal(expected, allow.IsAllowed(url));
    }

    [Fact]
    public void Empty_allowlist_permits_any_http_host_but_not_other_schemes()
    {
        var allow = new HostAllowlist(Array.Empty<string>());
        Assert.True(allow.IsAllowed("https://anything.example/x"));
        Assert.True(allow.IsAllowed("http://anything.example/x"));
        Assert.False(allow.IsAllowed("ftp://anything.example/x"));
        Assert.False(allow.IsAllowed("/relative"));
    }
}

public class HostAllowlistEntryTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("http://api.example")]
    [InlineData("api.example:8443")]
    [InlineData("*.example")]
    [InlineData("api.example/")]
    [InlineData("a.example,b.example")]
    [InlineData("api.example.")]
    [InlineData("::1")]
    public void An_entry_that_is_not_a_bare_host_is_refused(string entry)
    {
        Assert.NotNull(HostAllowlist.EntryProblem(entry));
        Assert.Throws<ArgumentException>(() => new HostAllowlist(new[] { entry }));
    }

    [Theory]
    [InlineData("api.example")]
    [InlineData("127.0.0.1")]
    [InlineData("[::1]")]
    [InlineData("API.Example")]
    public void A_bare_host_or_address_is_accepted(string entry) =>
        Assert.Null(HostAllowlist.EntryProblem(entry));

    [Fact]
    public void A_bracketed_ipv6_entry_matches_its_own_address()
    {
        var allow = new HostAllowlist(new[] { "[::1]" });
        Assert.True(allow.IsAllowed("http://[::1]:8080/x"));
        Assert.False(allow.IsAllowed("http://127.0.0.1/x"));
    }
}
