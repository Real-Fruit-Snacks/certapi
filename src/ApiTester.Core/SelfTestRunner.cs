using System.Net.Http;
using System.Text;

namespace ApiTester.Core;

public sealed record SelfTestResult(bool Passed, string Detail);

public sealed class SelfTestRunner
{
    public async Task<SelfTestResult> RunAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            // Three RSA keys, made side by side rather than one after another (each is 70-120 ms).
            var keys = SelfSignedCertificateFactory.NewRsaKeys(3);
            using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("ApiTester Self-Test CA", await keys[0]);
            using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate(
                "localhost", ca, serverAuth: true, clientAuth: false, dnsNames: new[] { "localhost" }, key: await keys[1]);
            using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate(
                "ApiTester Self-Test Client", ca, serverAuth: false, clientAuth: true, key: await keys[2]);

            await using var server = await LoopbackMtlsServer.StartAsync(
                serverCert, clientCert.Thumbprint!, "{\"selfTest\":\"ok\"}");

            using var client = new ApiClient();
            var response = await client.SendAsync(
                new ApiRequest { Method = HttpMethod.Get, Url = server.BaseUrl },
                clientCert,
                trustServerCertificate: c => c is not null && c.Thumbprint == serverCert.Thumbprint,
                cancellationToken: cancellationToken);

            if (response.IsSuccess && Encoding.UTF8.GetString(response.Body).Contains("selfTest"))
                return new SelfTestResult(true,
                    $"mTLS round-trip succeeded in {response.Elapsed.TotalMilliseconds:F0} ms.");

            return new SelfTestResult(false,
                response.Error?.Message ?? $"Unexpected response: {response.StatusCode}");
        }
        catch (Exception ex)
        {
            return new SelfTestResult(false, ex.Message);
        }
    }
}
