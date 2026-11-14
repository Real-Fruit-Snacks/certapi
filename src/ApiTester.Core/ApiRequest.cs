using System.Net.Http;

namespace ApiTester.Core;

public sealed record ApiRequest
{
    public required HttpMethod Method { get; init; }
    public required string Url { get; init; }
    public IReadOnlyList<KeyValuePair<string, string>> Headers { get; init; }
        = Array.Empty<KeyValuePair<string, string>>();
    public string? Body { get; init; }

    /// <summary>A body sent as these exact bytes — a file that is not text. Takes the place of
    /// <see cref="Body"/> when set; <see cref="Parts"/> still wins over both.</summary>
    public byte[]? BodyBytes { get; init; }
    public string? ContentType { get; init; }

    /// <summary>When set (and non-empty), the request is sent as multipart/form-data built from
    /// these parts, and <see cref="Body"/> is ignored.</summary>
    public IReadOnlyList<MultipartPart>? Parts { get; init; }

    /// <summary>When set, the request authenticates with Windows Integrated Auth (Negotiate/NTLM)
    /// using either the signed-in account or explicit credentials.</summary>
    public WindowsAuthOptions? WindowsAuth { get; init; }

    /// <summary>When set, the request is signed with AWS Signature Version 4 (on every hop to the
    /// origin it was made to; never on a hop elsewhere).</summary>
    public AwsSigV4Options? AwsSigV4 { get; init; }

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(100);
}

/// <summary>One part of a multipart/form-data body: a text field (<see cref="Value"/> set) or a
/// file field (<see cref="FilePath"/> set, read at send time).</summary>
public sealed record MultipartPart(string Name, string? Value, string? FilePath, string? ContentType = null);

/// <summary>Windows Integrated Authentication settings. <see cref="UseDefaultCredentials"/> uses the
/// signed-in Windows account (single sign-on); otherwise the explicit domain/user/password are used.
/// The handler negotiates Kerberos or NTLM with the server automatically.</summary>
public sealed record WindowsAuthOptions(
    bool UseDefaultCredentials, string? Domain = null, string? Username = null, string? Password = null)
{
    /// <summary>Answer an HTTP Digest challenge (RFC 7616: MD5 or SHA-256, <c>qop=auth</c>) with
    /// <see cref="Username"/> and <see cref="Password"/> instead of Negotiate/NTLM. It rides the
    /// same challenge/response machinery, and the same rule: only the origin the request was made
    /// to is ever answered. A Basic challenge is never answered with these credentials.</summary>
    public bool Digest { get; init; }

    /// <summary>Digest credentials for a request.</summary>
    public static WindowsAuthOptions ForDigest(string username, string? password) =>
        new(false, null, username, password ?? "") { Digest = true };

    /// <summary>Build options from a saved request's fields: an empty user means "use the signed-in
    /// account" (SSO); otherwise the value is parsed as <c>DOMAIN\user</c> (or a bare <c>user</c>)
    /// with the given password.</summary>
    public static WindowsAuthOptions FromCredentials(string? userField, string? password)
    {
        if (string.IsNullOrWhiteSpace(userField)) return new WindowsAuthOptions(true);
        string u = userField.Trim();
        int slash = u.IndexOf('\\');
        string? domain = slash > 0 ? u[..slash] : null;
        string user = slash >= 0 ? u[(slash + 1)..] : u;
        return new WindowsAuthOptions(false, domain, user, string.IsNullOrEmpty(password) ? null : password);
    }
}
