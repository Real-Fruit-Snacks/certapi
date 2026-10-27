using System.IO;
using System.Net;
using ApiTester.Core;

namespace ApiTester.Tests.Sweep;

/// <summary>A cookie jar file keeps each cookie's scope: a host-only cookie stays host-only.</summary>
public class CookieJarScopeTests
{
    [Fact]
    public void A_round_trip_through_the_jar_file_does_not_widen_a_host_only_cookie()
    {
        var jar = new CookieContainer();
        var api = new Uri("https://api.example.com/");
        jar.SetCookies(api, "sid=1; Secure");
        jar.SetCookies(api, "pref=2; Domain=example.com");
        Assert.Contains("sid=1", jar.GetCookieHeader(api));
        Assert.DoesNotContain("sid=1", jar.GetCookieHeader(new Uri("https://evil.api.example.com/")));

        var loaded = new CookieContainer();
        Assert.Equal(2, CookieJarFile.Deserialize(CookieJarFile.Serialize(jar), loaded));

        Assert.Contains("sid=1", loaded.GetCookieHeader(api));
        Assert.Contains("pref=2", loaded.GetCookieHeader(api));
        string sub = loaded.GetCookieHeader(new Uri("https://evil.api.example.com/"));
        Assert.DoesNotContain("sid=1", sub);
        Assert.Contains("pref=2", sub);   // a Domain= cookie still covers subdomains
    }
}
