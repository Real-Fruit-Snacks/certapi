using ApiTester.Core;

namespace ApiTester.Tests;

/// <summary>Pins the WSDL mapping: one POST per operation at the port's address, the SOAP 1.1 vs
/// 1.2 differences (action in a header vs a content-type parameter, and the envelope namespace),
/// placeholders that name a part's element or type rather than inventing an instance, and the
/// "named, never fetched" rule for imports.</summary>
public class WsdlImportTests
{
    private const string Soap11Wsdl = """
        <?xml version="1.0"?>
        <definitions xmlns="http://schemas.xmlsoap.org/wsdl/"
                     xmlns:soap="http://schemas.xmlsoap.org/wsdl/soap/"
                     xmlns:tns="http://example.test/orders"
                     targetNamespace="http://example.test/orders">
          <message name="GetOrderRequest">
            <part name="body" element="tns:GetOrder"/>
          </message>
          <message name="GetOrderResponse">
            <part name="body" element="tns:GetOrderResponse"/>
          </message>
          <message name="PingRequest"/>
          <portType name="OrdersPort">
            <operation name="GetOrder">
              <input message="tns:GetOrderRequest"/>
              <output message="tns:GetOrderResponse"/>
            </operation>
            <operation name="Ping">
              <input message="tns:PingRequest"/>
            </operation>
          </portType>
          <binding name="OrdersBinding" type="tns:OrdersPort">
            <soap:binding style="document" transport="http://schemas.xmlsoap.org/soap/http"/>
            <operation name="GetOrder">
              <soap:operation soapAction="http://example.test/orders/GetOrder"/>
            </operation>
            <operation name="Ping">
              <soap:operation soapAction=""/>
            </operation>
          </binding>
          <service name="OrdersService">
            <port name="OrdersHttp" binding="tns:OrdersBinding">
              <soap:address location="https://api.example.test/orders.svc"/>
            </port>
          </service>
        </definitions>
        """;

    [Fact]
    public void Each_operation_becomes_a_post_at_the_ports_address()
    {
        var result = WsdlImport.Parse(Soap11Wsdl);

        Assert.Equal("OrdersService", result.Root.Name);
        var port = Assert.Single(result.Root.Children);
        Assert.Equal("OrdersHttp", port.Name);
        Assert.Equal(2, port.Children.Count);

        var getOrder = port.Children.Single(c => c.Name == "GetOrder").Request!;
        Assert.Equal("POST", getOrder.Method);
        Assert.Equal("https://api.example.test/orders.svc", getOrder.Path);
    }

    [Fact]
    public void Soap_11_carries_the_action_in_a_header_and_uses_text_xml()
    {
        var result = WsdlImport.Parse(Soap11Wsdl);
        var m = result.Root.Children.Single().Children.Single(c => c.Name == "GetOrder").Request!;

        Assert.Equal("text/xml;charset=UTF-8", m.ContentType);
        var action = Assert.Single(m.Headers, h => h.Name == "SOAPAction");
        Assert.Equal("\"http://example.test/orders/GetOrder\"", action.Value);
    }

    [Fact]
    public void The_envelope_names_the_operation_and_its_parts_without_inventing_values()
    {
        var result = WsdlImport.Parse(Soap11Wsdl);
        string body = result.Root.Children.Single().Children.Single(c => c.Name == "GetOrder").Request!.Body!;

        Assert.Contains("http://schemas.xmlsoap.org/soap/envelope/", body);   // 1.1 envelope namespace
        Assert.Contains("<tns:GetOrder>", body);                              // the operation element
        Assert.Contains("http://example.test/orders", body);                  // the target namespace
        // The part is named as a placeholder, not fabricated: the schema is deliberately not
        // expanded, and a made-up instance would look authoritative while being wrong.
        Assert.Contains("element GetOrder", body);
        Assert.Contains("fill in from the schema", body, StringComparison.OrdinalIgnoreCase);
        // Document/literal: the element is the body's content, not wrapped in the operation again.
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(body, "<tns:GetOrder>"));
        Assert.DoesNotContain("<GetOrder>", body);
    }

    [Fact]
    public void An_operation_with_no_parameters_says_so()
    {
        var result = WsdlImport.Parse(Soap11Wsdl);
        string body = result.Root.Children.Single().Children.Single(c => c.Name == "Ping").Request!.Body!;

        Assert.Contains("takes no parameters", body);
    }

    [Fact]
    public void Soap_12_puts_the_action_in_the_content_type_and_uses_the_2003_envelope()
    {
        var result = WsdlImport.Parse("""
            <?xml version="1.0"?>
            <definitions xmlns="http://schemas.xmlsoap.org/wsdl/"
                         xmlns:soap12="http://schemas.xmlsoap.org/wsdl/soap12/"
                         xmlns:tns="http://example.test/v2"
                         targetNamespace="http://example.test/v2">
              <message name="DoRequest"><part name="p" type="xsd:string"/></message>
              <portType name="P"><operation name="Do"><input message="tns:DoRequest"/></operation></portType>
              <binding name="B" type="tns:P">
                <soap12:binding style="document" transport="http://schemas.xmlsoap.org/soap/http"/>
                <operation name="Do"><soap12:operation soapAction="urn:Do"/></operation>
              </binding>
              <service name="S">
                <port name="Http" binding="tns:B"><soap12:address location="https://v2.example.test/svc"/></port>
              </service>
            </definitions>
            """);

        var m = result.Root.Children.Single().Children.Single().Request!;
        Assert.StartsWith("application/soap+xml", m.ContentType);
        Assert.Contains("action=\"urn:Do\"", m.ContentType);
        Assert.DoesNotContain(m.Headers, h => h.Name == "SOAPAction");
        Assert.Contains("http://www.w3.org/2003/05/soap-envelope", m.Body);
        // An RPC-style part names a type rather than an element; the placeholder says which.
        Assert.Contains("type string", m.Body);
    }

    [Fact]
    public void An_imported_document_or_schema_is_named_in_a_warning_and_never_fetched()
    {
        var result = WsdlImport.Parse("""
            <?xml version="1.0"?>
            <definitions xmlns="http://schemas.xmlsoap.org/wsdl/"
                         xmlns:xsd="http://www.w3.org/2001/XMLSchema"
                         xmlns:tns="http://example.test/x" targetNamespace="http://example.test/x">
              <import namespace="http://example.test/other" location="other.wsdl"/>
              <types><xsd:schema><xsd:import namespace="http://example.test/t" schemaLocation="types.xsd"/></xsd:schema></types>
            </definitions>
            """);

        Assert.Contains(result.Warnings, w => w.Contains("other.wsdl") && w.Contains("not read"));
        Assert.Contains(result.Warnings, w => w.Contains("types.xsd") && w.Contains("not read"));
    }

    [Fact]
    public void A_document_with_no_soap_service_says_so_rather_than_importing_nothing_silently()
    {
        var result = WsdlImport.Parse("""
            <?xml version="1.0"?>
            <definitions xmlns="http://schemas.xmlsoap.org/wsdl/"
                         xmlns:tns="http://example.test/x" targetNamespace="http://example.test/x">
              <portType name="P"><operation name="Do"/></portType>
            </definitions>
            """);

        Assert.Empty(result.Root.Children);
        Assert.Contains(result.Warnings, w => w.Contains("no SOAP operations"));
    }

    [Fact]
    public void A_port_without_an_address_is_skipped_with_a_warning_rather_than_crashing()
    {
        var result = WsdlImport.Parse("""
            <?xml version="1.0"?>
            <definitions xmlns="http://schemas.xmlsoap.org/wsdl/"
                         xmlns:soap="http://schemas.xmlsoap.org/wsdl/soap/"
                         xmlns:tns="http://example.test/x" targetNamespace="http://example.test/x">
              <portType name="P"><operation name="Do"/></portType>
              <binding name="B" type="tns:P"><soap:binding style="document" transport="t"/></binding>
              <service name="S"><port name="NoAddress" binding="tns:B"/></service>
            </definitions>
            """);

        Assert.Contains(result.Warnings, w => w.Contains("NoAddress") && w.Contains("no SOAP address"));
    }

    [Fact]
    public void Not_a_wsdl_document_is_a_format_error_naming_what_was_expected()
    {
        var ex = Assert.Throws<FormatException>(() =>
            WsdlImport.Parse("<html><body>not a contract</body></html>"));
        Assert.Contains("WSDL", ex.Message);

        Assert.Throws<FormatException>(() => WsdlImport.Parse("this is not xml at all <<<"));
    }
}
