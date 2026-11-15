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
            // The factory owns a key once it is handed one; any not handed over when something
            // throws are disposed here.
            var keys = await Task.WhenAll(SelfSignedCertificateFactory.NewRsaKeys(3));
            int handed = 0;
            System.Security.Cryptography.RSA Hand() => keys[handed++];
            try
            {
                using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("ApiTester Self-Test CA", Hand());
                using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate(
                    "localhost", ca, serverAuth: true, clientAuth: false, dnsNames: new[] { "localhost" }, key: Hand());
                using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate(
                    "ApiTester Self-Test Client", ca, serverAuth: false, clientAuth: true, key: Hand());

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
            finally
            {
                for (int i = handed; i < keys.Length; i++) keys[i].Dispose();
            }
        }
        catch (Exception ex)
        {
            return new SelfTestResult(false, ex.Message);
        }
    }
}
