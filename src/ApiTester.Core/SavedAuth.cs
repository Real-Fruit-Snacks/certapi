namespace ApiTester.Core;

/// <summary>The connection- and signature-level auth a saved request carries — Windows, Digest and
/// AWS Signature v4 — turned into what <see cref="ApiRequest"/> needs. Bearer and Basic are plain
/// headers and are added where the headers are built. One place, so the app, <c>run</c>,
/// <c>bench</c> and MCP treat a saved request the same way.</summary>
public static class SavedAuth
{
    /// <summary>The auth types a saved request may name. Anything else loads as Auto.</summary>
    public static readonly IReadOnlyList<string> Types = ["Auto", "None", "Bearer", "Basic", "Windows", "Digest", "AwsSigV4"];

    public static string Normalize(string? type) =>
        Types.FirstOrDefault(t => t.Equals(type, StringComparison.Ordinal)) ?? "Auto";

    /// <summary>What to set on the request for <paramref name="m"/>'s auth, with variables already
    /// resolved by <paramref name="resolve"/>. <paramref name="problem"/> is set when the saved auth
    /// cannot be used as it stands (AWS without a region it can work out).</summary>
    public static (WindowsAuthOptions? Windows, AwsSigV4Options? Aws) For(
        RequestModel m, Func<string, string> resolve, string url, out string? problem)
    {
        problem = null;
        switch (m.AuthType)
        {
            case "Windows":
                return (WindowsAuthOptions.FromCredentials(resolve(m.AuthUser ?? ""), resolve(m.AuthSecret ?? "")), null);
            case "Digest":
                return (WindowsAuthOptions.ForDigest(resolve(m.AuthUser ?? ""), resolve(m.AuthSecret ?? "")), null);
            case "AwsSigV4":
            {
                var aws = Aws(resolve(m.AuthUser ?? ""), resolve(m.AuthSecret ?? ""), resolve(m.AuthScope ?? ""), url, out problem);
                return (null, aws);
            }
            default:
                return (null, null);
        }
    }

    /// <summary>AWS options from a key id, a secret and a "region/service" scope (also written
    /// "region:service"); either half left empty is read from an AWS host name.</summary>
    public static AwsSigV4Options? Aws(string keyId, string secret, string scope, string url, out string? problem)
    {
        problem = null;
        if (string.IsNullOrWhiteSpace(keyId) || string.IsNullOrEmpty(secret))
        {
            problem = "AWS Signature v4 needs an access key id and a secret access key.";
            return null;
        }
        string region = "", service = "";
        var parts = scope.Split('/', ':');
        if (parts.Length >= 1) region = parts[0].Trim();
        if (parts.Length >= 2) service = parts[1].Trim();
        if ((region.Length == 0 || service.Length == 0) &&
            Uri.TryCreate(url, UriKind.Absolute, out var uri) && AwsSigV4Options.ScopeFromHost(uri.Host) is { } fromHost)
        {
            if (region.Length == 0) region = fromHost.Region;
            if (service.Length == 0) service = fromHost.Service;
        }
        if (region.Length == 0 || service.Length == 0)
        {
            problem = "AWS Signature v4 needs a region and a service (e.g. us-east-1/execute-api); the host does not say.";
            return null;
        }
        // A session token is typed as an X-Amz-Security-Token header, which is signed like any
        // other x-amz-* header: nothing further to carry here.
        return new AwsSigV4Options(keyId.Trim(), secret, region, service);
    }
}
