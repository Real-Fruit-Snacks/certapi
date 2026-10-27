namespace ApiTester.Core;

/// <summary>A request parsed from an external source (cURL command or OpenAPI operation),
/// in UI-free terms the App maps onto its own request model.</summary>
public sealed class ParsedRequest
{
    public string Method { get; set; } = "GET";
    public string? BaseUrl { get; set; }              // set for OpenAPI ops; null for a full cURL URL
    public string Url { get; set; } = "";             // full URL (cURL) or path (OpenAPI)
    public string? Name { get; set; }                 // display name (OpenAPI summary/operationId)
    public string? Description { get; set; }          // OpenAPI operation description
    public List<KeyValuePair<string, string>> Headers { get; set; } = new();
    public string? Body { get; set; }
    public string? ContentType { get; set; }
    public string? BasicUser { get; set; }
    public string? BasicPassword { get; set; }
    public string? BearerToken { get; set; }
    public bool InsecureSkipVerify { get; set; }
    /// <summary>multipart/form-data fields (cURL <c>-F</c>); a file part's value is its path.</summary>
    public List<ParsedFormPart> FormParts { get; set; } = new();
}

/// <summary>One multipart field from an import: text, or a file to upload when <paramref name="IsFile"/>.</summary>
public sealed record ParsedFormPart(string Name, string Value, bool IsFile);

/// <summary>A parsed collection: a named group with an optional base URL, child folders,
/// and requests. Produced by the OpenAPI importer.</summary>
public sealed class ParsedCollection
{
    public string Name { get; set; } = "";
    public string? BaseUrl { get; set; }
    public List<ParsedCollection> Folders { get; set; } = new();
    public List<ParsedRequest> Requests { get; set; } = new();
}
