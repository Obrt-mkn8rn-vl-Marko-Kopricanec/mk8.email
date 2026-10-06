using System.Text;
using System.Xml.Linq;
using mk8.email.Configuration;
using mk8.email.Gateway.Protocols.Autodiscover;

namespace mk8.email.Application.Tests;

[TestClass]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1812", Justification = "MSTest DiscoverInternals instantiates this class; focused executed discovery verifies all standards vectors.")]
internal sealed class GatewayAutodiscoverXmlTests
{
    [TestMethod]
    public async Task RequestUsesRegisteredNamespacesAndNormalizesTheMailboxAddress()
    {
        var request = Request("OWNER@EXAMPLE.TEST");
        using var body = new MemoryStream(Encoding.UTF8.GetBytes(request));
        var parsed = await GatewayAutodiscoverXml.ReadAsync(body, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(0, parsed.ErrorCode);
        Assert.AreEqual("owner@example.test", parsed.EmailAddress, StringComparer.Ordinal);
    }

    [TestMethod]
    [DataRow("<broken>", 600)]
    [DataRow("<Autodiscover><Request /></Autodiscover>", 600)]
    [DataRow("<!DOCTYPE Autodiscover SYSTEM 'file:///never-mk8-secret'><Autodiscover />", 600)]
    [DataRow("<!DOCTYPE Autodiscover [<!ENTITY a 'expanded'>]><Autodiscover>&a;</Autodiscover>", 600)]
    public async Task MalformedAndEntityDocumentsAreRejected(string xml, int expectedCode)
    {
        using var body = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        var parsed = await GatewayAutodiscoverXml.ReadAsync(body, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(expectedCode, parsed.ErrorCode);
        Assert.IsNull(parsed.EmailAddress);
    }

    [TestMethod]
    [DataRow("<EMailAddress>owner@example.test</EMailAddress><EMailAddress>other@example.test</EMailAddress>")]
    [DataRow("<EMailAddress><address>owner@example.test</address></EMailAddress>")]
    [DataRow("<EMailAddress type='mail'>owner@example.test</EMailAddress>")]
    [DataRow("<EMailAddress>owner@example.test</EMailAddress><Unknown />")]
    [DataRow("<EMailAddress xmlns='urn:wrong'>owner@example.test</EMailAddress>")]
    [DataRow("<EMailAddress>not-a-mailbox</EMailAddress>")]
    public async Task RegisteredFieldShapeIsNotAnOpaqueExtensionPoint(string fields)
    {
        var xml = $"<Autodiscover xmlns='{GatewayAutodiscoverXml.RequestNamespace}'><Request>{fields}"
            + $"<AcceptableResponseSchema>{GatewayAutodiscoverXml.ResponseNamespace}</AcceptableResponseSchema>"
            + "</Request></Autodiscover>";
        using var body = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        var parsed = await GatewayAutodiscoverXml.ReadAsync(body, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(600, parsed.ErrorCode);
    }

    [TestMethod]
    [DataRow("urn:unsupported:mobile", false)]
    [DataRow(GatewayAutodiscoverXml.ResponseNamespace, true)]
    public async Task UnsupportedSchemaAndLegacyDirectoryLookupUseProviderFailure(string schema, bool legacyOnly)
    {
        var fields = legacyOnly ? "<LegacyDN>/o=unimplemented</LegacyDN>" : "<EMailAddress>owner@example.test</EMailAddress>";
        var xml = $"<Autodiscover xmlns='{GatewayAutodiscoverXml.RequestNamespace}'><Request>{fields}"
            + $"<AcceptableResponseSchema>{schema}</AcceptableResponseSchema></Request></Autodiscover>";
        using var body = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        Assert.AreEqual(601, (await GatewayAutodiscoverXml.ReadAsync(body, CancellationToken.None).ConfigureAwait(false)).ErrorCode);
    }

    [TestMethod]
    [DataRow("<EMailAddress>owner@example.test</EMailAddress>")]
    [DataRow("text<EMailAddress>owner@example.test</EMailAddress><AcceptableResponseSchema>{schema}</AcceptableResponseSchema>")]
    [DataRow("<AcceptableResponseSchema>{schema}</AcceptableResponseSchema><EMailAddress>owner@example.test</EMailAddress>")]
    [DataRow("<LegacyDN>/o=unsupported</LegacyDN><EMailAddress>owner@example.test</EMailAddress><AcceptableResponseSchema>{schema}</AcceptableResponseSchema>")]
    public async Task MissingSchemaMixedTextAndOutOfOrderFieldsAreRejected(string fields)
    {
        var xml = $"<Autodiscover xmlns='{GatewayAutodiscoverXml.RequestNamespace}'><Request>"
            + fields.Replace("{schema}", GatewayAutodiscoverXml.ResponseNamespace, StringComparison.Ordinal)
            + "</Request></Autodiscover>";
        using var body = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        Assert.AreEqual(600, (await GatewayAutodiscoverXml.ReadAsync(body, CancellationToken.None).ConfigureAwait(false)).ErrorCode);
    }

    [TestMethod]
    public async Task NamespacePrefixIsNotTreatedAsTheNamespaceIdentity()
    {
        var xml = $"<a:Autodiscover xmlns:a='{GatewayAutodiscoverXml.RequestNamespace}'><a:Request>"
            + "<a:EMailAddress>owner@example.test</a:EMailAddress>"
            + $"<a:AcceptableResponseSchema>{GatewayAutodiscoverXml.ResponseNamespace}</a:AcceptableResponseSchema>"
            + "</a:Request></a:Autodiscover>";
        using var body = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        var parsed = await GatewayAutodiscoverXml.ReadAsync(body, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(0, parsed.ErrorCode);
        Assert.AreEqual("owner@example.test", parsed.EmailAddress, StringComparer.Ordinal);
    }

    [TestMethod]
    [DataRow("utf-16")]
    [DataRow("utf-32")]
    public async Task NonUtf8WireEncodingsAreRejected(string encoding)
    {
        var payload = Encoding.GetEncoding(encoding).GetBytes(Request());
        using var body = new MemoryStream(payload);
        Assert.AreEqual(600, (await GatewayAutodiscoverXml.ReadAsync(body, CancellationToken.None).ConfigureAwait(false)).ErrorCode);
    }

    [TestMethod]
    [DataRow("utf-16")]
    [DataRow("iso-8859-1")]
    public async Task IncorrectDeclarationCannotOverrideUtf8WireEncoding(string encoding)
    {
        var payload = Encoding.UTF8.GetBytes($"<?xml version='1.0' encoding='{encoding}'?>" + Request());
        using var body = new MemoryStream(payload);
        Assert.AreEqual(600, (await GatewayAutodiscoverXml.ReadAsync(body, CancellationToken.None).ConfigureAwait(false)).ErrorCode);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Utf8DeclarationAndOptionalBomAreSupported(bool bom)
    {
        var encoding = new UTF8Encoding(bom, true);
        var xml = "<?xml version='1.0' encoding='UTF-8'?>" + Request();
        using var body = new MemoryStream([.. encoding.GetPreamble(), .. encoding.GetBytes(xml)]);
        Assert.AreEqual(0, (await GatewayAutodiscoverXml.ReadAsync(body, CancellationToken.None).ConfigureAwait(false)).ErrorCode);
    }

    [TestMethod]
    public async Task InvalidUtf8IsAProtocolRequestError()
    {
        using var body = new MemoryStream([0xFF, 0xFE, .. Encoding.UTF8.GetBytes(Request())]);
        Assert.AreEqual(600, (await GatewayAutodiscoverXml.ReadAsync(body, CancellationToken.None).ConfigureAwait(false)).ErrorCode);
    }

    [TestMethod]
    public async Task XmlParserHasAnIndependentDocumentBudget()
    {
        var xml = Request().Replace("<Request>", "<Request>" + new string(' ', 65_537), StringComparison.Ordinal);
        using var body = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        Assert.AreEqual(600, (await GatewayAutodiscoverXml.ReadAsync(body, CancellationToken.None).ConfigureAwait(false)).ErrorCode);
    }

    [TestMethod]
    public void ErrorEnvelopeUsesGeneralResponseNamespaceAndEscapesMessage()
    {
        XNamespace ns = "http://schemas.microsoft.com/exchange/autodiscover/responseschema/2006";
        var document = XDocument.Parse(GatewayAutodiscoverXml.Error(603, "failure & <internal>"));
        Assert.AreEqual(ns + "Autodiscover", document.Root!.Name);
        var error = document.Root.Element(ns + "Response")!.Element(ns + "Error")!;
        Assert.AreEqual("603", error.Element(ns + "ErrorCode")!.Value, StringComparer.Ordinal);
        Assert.AreEqual("failure & <internal>", error.Element(ns + "Message")!.Value, StringComparer.Ordinal);
        Assert.IsNotNull(error.Attribute("Time"));
        Assert.IsNotNull(error.Attribute("Id"));
        Assert.AreEqual(string.Empty, error.Element(ns + "DebugData")!.Value, StringComparer.Ordinal);
    }

    [TestMethod]
    [DataRow(true, false, false, "IMAP")]
    [DataRow(false, true, false, "POP3")]
    [DataRow(false, false, true, "SMTP")]
    public void OnlyEnabledImplicitTlsListenersAreAdvertised(bool imap, bool pop3, bool smtp, string expectedType)
    {
        var environment = new EnvironmentConfig
        {
            Smtp = new SmtpConfig { Hostname = "mail.example.test", EnableImplicitTls = smtp, EnableSubmission = true, EnableStartTls = true },
            Imap = new ImapConfig { EnableImap = true, EnableImplicitTls = imap },
            Pop3 = new Pop3Config { EnablePop3 = true, EnableStartTls = true, EnableImplicitTls = pop3 },
        };
        var document = XDocument.Parse(GatewayAutodiscoverXml.Settings("owner@example.test", environment));
        XNamespace response = GatewayAutodiscoverXml.ResponseNamespace;
        var protocols = document.Descendants(response + "Protocol").ToArray();
        Assert.HasCount(1, protocols);
        Assert.AreEqual(expectedType, protocols[0].Element(response + "Type")!.Value, StringComparer.Ordinal);
        Assert.AreEqual("on", protocols[0].Element(response + "SSL")!.Value, StringComparer.Ordinal);
        Assert.AreEqual("on", protocols[0].Element(response + "AuthRequired")!.Value, StringComparer.Ordinal);
        Assert.AreEqual("off", protocols[0].Element(response + "SPA")!.Value, StringComparer.Ordinal);
        Assert.AreEqual("owner@example.test", protocols[0].Element(response + "LoginName")!.Value, StringComparer.Ordinal);
        CollectionAssert.AreEqual(ExpectedProtocolElements, protocols[0].Elements().Select(element => element.Name.LocalName).ToArray());
    }

    [TestMethod]
    public void NoImplicitTlsEndpointUsesBadAddressAndDoesNotInventExchangeServices()
    {
        var xml = GatewayAutodiscoverXml.Settings("owner@example.test", new EnvironmentConfig());
        var document = XDocument.Parse(xml);
        Assert.AreEqual("501", document.Descendants().Single(element => string.Equals(element.Name.LocalName, "ErrorCode", StringComparison.Ordinal)).Value,
            StringComparer.Ordinal);
        Assert.IsFalse(document.Descendants().Any(element => string.Equals(element.Name.LocalName, "Protocol", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void SettingsEscapeWorkerTextAsXmlData()
    {
        const string username = "owner&<tag>@example.test";
        var environment = new EnvironmentConfig { Smtp = new SmtpConfig { Hostname = "mail.example.test", EnableImplicitTls = true } };
        var xml = GatewayAutodiscoverXml.Settings(username, environment);
        var document = XDocument.Parse(xml);
        XNamespace response = GatewayAutodiscoverXml.ResponseNamespace;
        Assert.AreEqual(username, document.Descendants(response + "LoginName").Single().Value, StringComparer.Ordinal);
        Assert.IsFalse(xml.Contains("<tag>", StringComparison.Ordinal));
    }

    internal static string Request(string address = "owner@example.test")
    {
        XNamespace request = GatewayAutodiscoverXml.RequestNamespace;
        return new XElement(request + "Autodiscover", new XElement(request + "Request",
            new XElement(request + "EMailAddress", address),
            new XElement(request + "AcceptableResponseSchema", GatewayAutodiscoverXml.ResponseNamespace)))
            .ToString(SaveOptions.DisableFormatting);
    }

    private static readonly string[] ExpectedProtocolElements = ["Type", "Server", "Port", "LoginName", "DomainRequired", "SPA", "SSL", "AuthRequired"];
}
