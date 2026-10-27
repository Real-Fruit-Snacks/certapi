using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography.X509Certificates;
using ApiTester.Cli;
using ApiTester.Core;

namespace ApiTester.Tests.Cli;

/// <summary>The shared rig for end-to-end `certapi serve` tests. It lives here rather than inside
/// one suite because how `serve` is started — the fixed flags, the stubbed certificate store, the
/// bounded shutdown — is a contract every serve suite depends on, and a copy per suite means a
/// change to that contract can be made in one place and silently missed in another.</summary>
internal static class ServeFixture
{
    /// <summary>Every network wait is bounded, so a gateway that never answers fails the test rather
    /// than hanging the suite.</summary>
    public static readonly TimeSpan Limit = TimeSpan.FromSeconds(20);

    public static int FreePort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    public static (X509Certificate2 ca, X509Certificate2 server, X509Certificate2 client) Certs()
    {
        var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        var server = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        var client = SelfSignedCertificateFactory.CreateSignedCertificate("GatewayClient", ca, false, true);
        return (ca, server, client);
    }

    /// <summary>A client that leaves the gateway's answer alone: redirects are not followed and
    /// cookies are not swallowed by a container, so Location and Set-Cookie can be asserted as sent.</summary>
    public static HttpClient NewClient() =>
        new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
        { Timeout = Limit };

    /// <summary>Every value of a response header, in order — the count matters as much as the value
    /// when a duplicate is the browser-breaking failure.</summary>
    public static string[] HeaderValues(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? values.ToArray() : Array.Empty<string>();

    /// <summary>Retry while the listener finishes binding.</summary>
    public static async Task<T> Poll<T>(Func<Task<T>> action)
    {
        Exception? last = null;
        for (int i = 0; i < 50; i++)
        {
            try { return await action(); }
            catch (Exception ex) { last = ex; await Task.Delay(100); }
        }
        throw last!;
    }
}

/// <summary>`serve` running on a background thread with its real gateway, so the routes, browser
/// options and header rules under test are the ones the command built from its own flags.</summary>
internal sealed class ServeHost : IAsyncDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly Task<int> _run;

    public int Port { get; }
    public string Origin => $"http://127.0.0.1:{Port}";

    private ServeHost(X509Certificate2 clientCert, IEnumerable<string> extraArgs, int port)
    {
        Port = port;
        var services = new CliServices
        {
            Cancel = _cts.Token,
            ListCertificates = _ => new[]
            {
                new CertificateInfo
                {
                    Subject = "CN=GatewayClient", Issuer = "CN=CA", Thumbprint = clientCert.Thumbprint!,
                    NotBefore = DateTime.Now.AddDays(-1), NotAfter = DateTime.Now.AddDays(30),
                    HasClientAuthEku = true, Certificate = clientCert
                }
            }
        };
        var args = new List<string> { "serve", "--port", Port.ToString(),
                                      "--cert", "GatewayClient", "--insecure", "-q" };
        args.AddRange(extraArgs);
        _run = Task.Run(() => CliApp.Run(args.ToArray(), TextWriter.Null, TextWriter.Null, services: services));
    }

    /// <summary>Start the gateway, retrying on a fresh port if the chosen one was taken between
    /// being probed and being bound.
    ///
    /// FreePort binds port 0, reads what the operating system assigned, then releases it — so the
    /// port is free at the moment it is returned and anyone may take it before the gateway binds.
    /// xUnit runs test classes in parallel, so the likeliest thief is another serve test in this
    /// same assembly. Losing that race made `serve` exit with a bind error while the test polled a
    /// port nothing was listening on, failing five seconds later with a bare socket error and a
    /// different test name each run. It reproduced under Debug, whose slower startup widens the
    /// window; Release simply won the race more often, which is the worst way for a flake to hide.</summary>
    public static ServeHost Start(X509Certificate2 clientCert, params string[] extraArgs)
    {
        ServeHost? host = null;
        for (int attempt = 0; attempt < 5; attempt++)
        {
            host = new ServeHost(clientCert, extraArgs, ServeFixture.FreePort());

            // A failed bind is not a hang: ServeCommand turns it into a data error and returns
            // immediately, so a run task that has already finished means this port was taken.
            if (!host._run.Wait(TimeSpan.FromMilliseconds(750)) || host._run.Result == ExitCodes.Ok)
                return host;

            if (attempt < 4) host.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        // Five starts that all exited before listening are not a port race: say so now, rather than
        // let the test poll a port nothing listens on until its request times out.
        if (host!._run.IsCompleted && host._run.Result != ExitCodes.Ok)
            throw new InvalidOperationException(
                $"serve exited with code {host._run.Result} before listening ({string.Join(" ", extraArgs)}). " +
                "Under --vhost or --listen on Windows that is usually a missing URL reservation: run the tests elevated.");
        return host;   // the last attempt, undisposed; the caller's assertions are the verdict
    }

    private bool _disposed;

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        try { await _run.WaitAsync(ServeFixture.Limit); } catch { /* the test's assertions are the verdict */ }
        _cts.Dispose();
    }
}

/// <summary>A test that needs to bind every interface (<c>http://+:port/</c>), as <c>serve --vhost</c>
/// does. On Windows that needs an elevated prompt or a URL reservation for the port; a developer
/// machine usually has neither, and the test is skipped there with that reason instead of failing
/// after a long wait. On CI (<c>CI=true</c>, set by GitHub Actions) it always runs.</summary>
public sealed class WildcardBindingFactAttribute : FactAttribute
{
    public WildcardBindingFactAttribute()
    {
        if (string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase)) return;
        if (!WildcardBinding.Available)
            Skip = "Needs to bind http://+:<port>/ (serve --vhost), which this Windows session may not do: " +
                   "run the tests from an elevated prompt to include it. CI always runs it.";
    }
}

internal static class WildcardBinding
{
    /// <summary>Probed once per test run, on a fresh port.</summary>
    public static readonly bool Available = Probe();

    private static bool Probe()
    {
        try
        {
            using var listener = new HttpListener();
            listener.Prefixes.Add($"http://+:{ServeFixture.FreePort()}/");
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (HttpListenerException) { return false; }
        catch (PlatformNotSupportedException) { return false; }
    }
}
