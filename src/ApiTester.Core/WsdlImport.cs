using System.Xml.Linq;

namespace ApiTester.Core;

/// <summary>What a WSDL document parsed to: one saved request per operation, and warnings for
/// anything that could not be carried across faithfully.</summary>
public sealed record WsdlImportResult(CollectionNode Root, IReadOnlyList<string> Warnings);

/// <summary>Reads a WSDL 1.1 document (and the SOAP 1.2 binding variant) into saved requests — one
/// POST per operation, addressed at the port's endpoint, with the right content type and a SOAP
/// envelope skeleton naming the operation and its message parts.
/// <para><b>Deliberately minimal.</b> This gets a working SOAP request about ninety percent
/// written; it is not a WSDL toolchain. Types are NOT expanded from the schema: a part becomes a
/// commented placeholder naming its element or type, because generating a full instance document
/// from XML Schema — with its imports, restrictions, choices, and substitution groups — is a
/// different product. An external <c>xsd:import</c> or <c>wsdl:import</c> is named in a warning
/// rather than fetched: this parser never touches the network or the file system.</para></summary>
public static class WsdlImport
{
    private static readonly XNamespace Wsdl = "http://schemas.xmlsoap.org/wsdl/";
    private static readonly XNamespace Soap11 = "http://schemas.xmlsoap.org/wsdl/soap/";
    private static readonly XNamespace Soap12 = "http://schemas.xmlsoap.org/wsdl/soap12/";
    private static readonly XNamespace Xsd = "http://www.w3.org/2001/XMLSchema";

    /// <summary>Throws <see cref="FormatException"/> when the text is not a WSDL document at all;
    /// anything less total is a warning on the result instead.</summary>
    public static WsdlImportResult Parse(string xml)
    {
        XDocument doc;
        try { doc = XDocument.Parse(xml, LoadOptions.None); }
        catch (System.Xml.XmlException ex) { throw new FormatException("Not XML: " + ex.Message); }

        var definitions = doc.Root;
        if (definitions is null || definitions.Name != Wsdl + "definitions")
            throw new FormatException(
                "Not a WSDL document: expected a <definitions> root in the WSDL 1.1 namespace " +
                "(http://schemas.xmlsoap.org/wsdl/).");

        var warnings = new List<string>();
        string targetNamespace = (string?)definitions.Attribute("targetNamespace") ?? "";

        // Anything the document points at but does not contain is named, never fetched: this parser
        // has no network and no file access, and silently importing half a contract would be worse
        // than saying which half is missing.
        foreach (var import in definitions.Descendants(Wsdl + "import"))
            if ((string?)import.Attribute("location") is { Length: > 0 } location)
                warnings.Add($"the document imports '{location}', which is not read — import that file too if its operations are missing.");
        foreach (var import in definitions.Descendants(Xsd + "import").Concat(definitions.Descendants(Xsd + "include")))
            if ((string?)import.Attribute("schemaLocation") is { Length: > 0 } location)
                warnings.Add($"the schema imports '{location}', which is not read — body placeholders for its types name the type only.");

        // portType operations carry the message names; bindings carry the SOAP action and style;
        // services carry the address. All three are needed, and each is keyed by qualified name.
        var portTypes = definitions.Elements(Wsdl + "portType")
            .ToDictionary(p => (string?)p.Attribute("name") ?? "", p => p, StringComparer.Ordinal);
        var messages = definitions.Elements(Wsdl + "message")
            .ToDictionary(m => (string?)m.Attribute("name") ?? "", m => m, StringComparer.Ordinal);
        var bindings = definitions.Elements(Wsdl + "binding").ToList();

        string serviceName = (string?)definitions.Elements(Wsdl + "service").FirstOrDefault()?.Attribute("name")
            ?? (string?)definitions.Attribute("name") ?? "WSDL import";
        var root = new CollectionNode { Name = serviceName, IsFolder = true };

        foreach (var service in definitions.Elements(Wsdl + "service"))
        foreach (var port in service.Elements(Wsdl + "port"))
        {
            string? bindingRef = Local((string?)port.Attribute("binding"));
            var binding = bindings.FirstOrDefault(b => (string?)b.Attribute("name") == bindingRef);
            if (binding is null) continue;

            // SOAP 1.1 and 1.2 differ only in the namespace of the binding elements and in how the
            // action travels — a header for 1.1, a content-type parameter for 1.2.
            bool soap12 = binding.Elements(Soap12 + "binding").Any();
            var soapNs = soap12 ? Soap12 : Soap11;
            string? address = (string?)port.Elements(soapNs + "address").FirstOrDefault()?.Attribute("location");
            if (address is null)
            {
                warnings.Add($"port '{(string?)port.Attribute("name")}' has no SOAP address and was skipped.");
                continue;
            }

            string? portTypeRef = Local((string?)binding.Attribute("type"));
            if (portTypeRef is null || !portTypes.TryGetValue(portTypeRef, out var portType))
            {
                warnings.Add($"binding '{bindingRef}' names a portType that is not in this document and was skipped.");
                continue;
            }

            var portFolder = new CollectionNode
            {
                Name = (string?)port.Attribute("name") ?? bindingRef ?? "port",
                IsFolder = true
            };

            foreach (var operation in portType.Elements(Wsdl + "operation"))
            {
                string operationName = (string?)operation.Attribute("name") ?? "operation";
                var bindingOperation = binding.Elements(Wsdl + "operation")
                    .FirstOrDefault(o => (string?)o.Attribute("name") == operationName);
                string soapAction = (string?)bindingOperation?.Elements(soapNs + "operation").FirstOrDefault()
                    ?.Attribute("soapAction") ?? "";

                var model = new RequestModel
                {
                    Method = "POST",
                    Path = address,
                    Body = BuildEnvelope(operation, messages, targetNamespace, soap12, operationName)
                };

                if (soap12)
                {
                    // SOAP 1.2 carries the action as a content-type parameter, not a header.
                    model.ContentType = soapAction.Length > 0
                        ? $"application/soap+xml;charset=UTF-8;action=\"{soapAction}\""
                        : "application/soap+xml;charset=UTF-8";
                }
                else
                {
                    model.ContentType = "text/xml;charset=UTF-8";
                    model.Headers.Add(new HeaderRow { Name = "SOAPAction", Value = $"\"{soapAction}\"" });
                }

                portFolder.Children.Add(new CollectionNode
                {
                    Name = operationName, IsFolder = false, Request = model
                });
            }

            if (portFolder.Children.Count > 0) root.Children.Add(portFolder);
        }

        if (root.Children.Count == 0)
            warnings.Add("no SOAP operations were found — the document may describe only HTTP bindings, or its service may be in an imported file.");

        return new WsdlImportResult(root, warnings);
    }

    /// <summary>A SOAP envelope skeleton for one operation: the right envelope namespace, the
    /// operation element in the service's target namespace, and one commented placeholder per
    /// message part. The comment names the part's element or type rather than inventing a value,
    /// because the schema is not expanded — saying "fill this in, it is a PurchaseOrder" is honest,
    /// where a fabricated instance would look authoritative and be wrong.</summary>
    private static string BuildEnvelope(
        XElement operation, IReadOnlyDictionary<string, XElement> messages,
        string targetNamespace, bool soap12, string operationName)
    {
        string envelopeNs = soap12
            ? "http://www.w3.org/2003/05/soap-envelope"
            : "http://schemas.xmlsoap.org/soap/envelope/";

        var parts = new List<string>();
        bool allElements = true;
        string? inputRef = Local((string?)operation.Elements(Wsdl + "input").FirstOrDefault()?.Attribute("message"));
        if (inputRef is not null && messages.TryGetValue(inputRef, out var message))
        {
            foreach (var part in message.Elements(Wsdl + "part"))
            {
                string partName = (string?)part.Attribute("name") ?? "part";
                string? element = Local((string?)part.Attribute("element"));
                string? type = Local((string?)part.Attribute("type"));
                // Document/literal names an element; RPC/encoded names a type. Either way the
                // placeholder says which, so the person filling it in knows what to look up.
                if (element is null) allElements = false;
                // The element lives in its schema's namespace, which is often not the WSDL's own
                // (StockQuote's xsd1: beside tns:): it is resolved through the prefix the part used.
                string qualified = "tns:" + element;
                if (element is not null && (string?)part.Attribute("element") is { } raw)
                {
                    int colon = raw.IndexOf(':');
                    var ns = colon > 0 ? part.GetNamespaceOfPrefix(raw[..colon]) : part.GetDefaultNamespace();
                    if (ns is not null && ns != XNamespace.None && ns.NamespaceName != targetNamespace)
                    {
                        string prefix = "ns" + parts.Count;
                        qualified = $"{prefix}:{element}";
                        parts.Add($"      <!-- {partName}: element {element} — fill in from the schema -->\n      <{qualified} xmlns:{prefix}=\"{ns.NamespaceName}\"></{qualified}>");
                        continue;
                    }
                }
                parts.Add(element is not null
                    ? $"      <!-- {partName}: element {element} — fill in from the schema -->\n      <{qualified}></{qualified}>"
                    : $"      <!-- {partName}: type {type ?? "unknown"} — fill in from the schema -->\n      <{partName}></{partName}>");
            }
        }

        string body = parts.Count > 0
            ? string.Join("\n", parts)
            : "      <!-- this operation takes no parameters -->";

        // Document/literal: the part's element IS the body's content (in the wrapped style, an
        // element named after the operation) — wrapping it in the operation name again gives
        // <tns:GetQuote><GetQuote/></tns:GetQuote>, which the service refuses. RPC style names
        // types, and the operation element is the wrapper the parts go in.
        if (parts.Count > 0 && allElements)
            return $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <soap:Envelope xmlns:soap="{envelopeNs}" xmlns:tns="{targetNamespace}">
              <soap:Header/>
              <soap:Body>
            {body.Replace("\n      ", "\n    ").Replace("      <!--", "    <!--")}
              </soap:Body>
            </soap:Envelope>
            """;

        return $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <soap:Envelope xmlns:soap="{envelopeNs}" xmlns:tns="{targetNamespace}">
              <soap:Header/>
              <soap:Body>
                <tns:{operationName}>
            {body}
                </tns:{operationName}>
              </soap:Body>
            </soap:Envelope>
            """;
    }

    /// <summary>The local part of a possibly-prefixed qualified name: <c>tns:Foo</c> is
    /// <c>Foo</c>. Prefix resolution is unnecessary here because every reference this parser
    /// follows is within one document.</summary>
    private static string? Local(string? qualifiedName)
    {
        if (qualifiedName is null) return null;
        int colon = qualifiedName.IndexOf(':');
        return colon < 0 ? qualifiedName : qualifiedName[(colon + 1)..];
    }
}
