using System.IO;
using System.Security.Cryptography.X509Certificates;
using ApiTester.Cli;
using ApiTester.Core;

namespace ApiTester.Tests.Cli;

/// <summary>e2e coverage for <c>certapi trust list|add|remove</c> — the CLI surface over
/// <see cref="TrustService"/>'s pinned-server-certificate store. Uses a real <see cref="ApiClient"/>
/// against a real <see cref="LoopbackMtlsServer"/> for the <c>--from-url</c> capture, never a mock
/// of ApiClient or TrustService.</summary>
public class TrustCliTests
{
    private static string TempWorkspace()
    {
        var path = Path.Combine(Path.GetTempPath(), $"certapi-trust-{Guid.NewGuid():N}.json");
        new AppState().SaveTo(path);   // trust add/remove load via CliWorkspace.Load, which requires the file to exist
        return path;
    }

    private static CliServices Services(string live) =>
        new() { LiveStatePath = live, IsGuiRunning = () => false, Client = new ApiClient(), Cancel = default };

    [Fact]
    public void Add_with_thumbprint_persists_the_pin_and_list_shows_it()
    {
        string live = Path.Combine(Path.GetTempPath(), $"certapi-trust-live-{Guid.NewGuid():N}.json");
        string workspace = TempWorkspace();
        try
        {
            int addCode = CliApp.Run(
                new[] { "trust", "add", "internal.corp", "--thumbprint", "0123456789ABCDEF0123456789ABCDEF01234567", "--workspace", workspace },
                new StringWriter(), new StringWriter(), services: Services(live));
            Assert.Equal(0, addCode);

            var saved = AppState.LoadFrom(workspace);
            var pin = Assert.Single(saved.TrustedServerCerts);
            Assert.Equal("internal.corp", pin.Host);   // lowercased by TrustService
            Assert.Equal("0123456789ABCDEF0123456789ABCDEF01234567", pin.Thumbprint);

            var so = new StringWriter();
            int listCode = CliApp.Run(
                new[] { "trust", "list", "--workspace", workspace },
                so, new StringWriter(), services: Services(live));
            Assert.Equal(0, listCode);
            Assert.Contains("internal.corp", so.ToString());
            Assert.Contains("0123456789ABCDEF0123456789ABCDEF01234567", so.ToString());
        }
        finally
        {
            if (File.Exists(workspace)) File.Delete(workspace);
            if (File.Exists(live)) File.Delete(live);
        }
    }

    [Fact]
    public void Remove_deletes_a_previously_added_pin()
    {
        string live = Path.Combine(Path.GetTempPath(), $"certapi-trust-live-{Guid.NewGuid():N}.json");
        string workspace = TempWorkspace();
        try
        {
            Assert.Equal(0, CliApp.Run(
                new[] { "trust", "add", "internal.corp", "--thumbprint", "0123456789ABCDEF0123456789ABCDEF01234567", "--workspace", workspace },
                new StringWriter(), new StringWriter(), services: Services(live)));

            int removeCode = CliApp.Run(
                new[] { "trust", "remove", "internal.corp", "--thumbprint", "0123456789ABCDEF0123456789ABCDEF01234567", "--workspace", workspace },
                new StringWriter(), new StringWriter(), services: Services(live));
            Assert.Equal(0, removeCode);

            var saved = AppState.LoadFrom(workspace);
            Assert.Empty(saved.TrustedServerCerts);
        }
        finally
        {
            if (File.Exists(workspace)) File.Delete(workspace);
            if (File.Exists(live)) File.Delete(live);
        }
    }

    [Fact]
    public void Add_with_neither_thumbprint_nor_from_url_is_a_usage_error()
    {
        string live = Path.Combine(Path.GetTempPath(), $"certapi-trust-live-{Guid.NewGuid():N}.json");
        string workspace = TempWorkspace();
        try
        {
            var se = new StringWriter();
            int code = CliApp.Run(
                new[] { "trust", "add", "internal.corp", "--workspace", workspace },
                new StringWriter(), se, services: Services(live));

            Assert.Equal(2, code);
            Assert.Contains("--thumbprint", se.ToString());
            Assert.Contains("--from-url", se.ToString());

            // Nothing was written.
            Assert.Empty(AppState.LoadFrom(workspace).TrustedServerCerts);
        }
        finally
        {
            if (File.Exists(workspace)) File.Delete(workspace);
            if (File.Exists(live)) File.Delete(live);
        }
    }

    [Fact]
    public async Task Add_from_url_pins_the_thumbprint_of_the_certificate_actually_presented()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("TrustClient", ca, false, true);
        await using var server = await LoopbackMtlsServer.StartAsync(serverCert, clientCert.Thumbprint!);

        string live = Path.Combine(Path.GetTempPath(), $"certapi-trust-live-{Guid.NewGuid():N}.json");
        string workspace = TempWorkspace();
        var pfx = Path.Combine(Path.GetTempPath(), $"certapi-trust-client-{Guid.NewGuid():N}.pfx");
        File.WriteAllBytes(pfx, clientCert.Export(X509ContentType.Pkcs12, "pw"));
        try
        {
            int code = CliApp.Run(
                new[]
                {
                    "trust", "add", "127.0.0.1", "--from-url", server.BaseUrl,
                    "--cert-file", pfx, "--cert-password", "pw", "--workspace", workspace
                },
                new StringWriter(), new StringWriter(), services: Services(live));

            Assert.Equal(0, code);
            var saved = AppState.LoadFrom(workspace);
            var pin = Assert.Single(saved.TrustedServerCerts);
            Assert.Equal("127.0.0.1", pin.Host);
            Assert.Equal(serverCert.Thumbprint, pin.Thumbprint);
        }
        finally
        {
            File.Delete(pfx);
            if (File.Exists(workspace)) File.Delete(workspace);
            if (File.Exists(live)) File.Delete(live);
        }
    }

    [Fact]
    public void Add_from_url_that_cannot_connect_is_a_transport_error()
    {
        string live = Path.Combine(Path.GetTempPath(), $"certapi-trust-live-{Guid.NewGuid():N}.json");
        string workspace = TempWorkspace();
        try
        {
            var se = new StringWriter();
            int code = CliApp.Run(
                new[] { "trust", "add", "127.0.0.1", "--from-url", "https://127.0.0.1:1/", "--workspace", workspace },
                new StringWriter(), se, services: Services(live));

            Assert.Equal(1, code);
            Assert.NotEmpty(se.ToString());
            Assert.Empty(AppState.LoadFrom(workspace).TrustedServerCerts);
        }
        finally
        {
            if (File.Exists(workspace)) File.Delete(workspace);
            if (File.Exists(live)) File.Delete(live);
        }
    }
}
