using System.Net;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using mk8.email.Contracts.Messaging;
using mk8.email.Gateway.Protocols.Ews;
using CaptureFixture = mk8.email.Messaging.Tests.GatewayHttpCaptureBoundaryTests.CaptureFixture;

namespace mk8.email.Messaging.Tests;

internal sealed partial class GatewayEwsRouteTests
{
    private const string BodyProperty = "<t:AdditionalProperties><t:FieldURI FieldURI='item:Subject'/><t:FieldURI FieldURI='item:Body'/><t:FieldURI FieldURI='item:HasAttachments'/></t:AdditionalProperties>";
    private const string BodyHeaders = "From: Sender <sender@example.test>\r\nSubject: Body fixture\r\nMIME-Version: 1.0\r\n";
    private static readonly string[] BodyFields = ["ItemId", "Subject", "Body", "HasAttachments"];
    private static readonly string[] BodyReferenceOutcomes = ["ErrorAccessDenied", "ErrorItemNotFound", "NoError", "NoError"];
    private const string PlainBodyMime = BodyHeaders + "Content-Type: text/plain; charset=utf-8\r\n\r\nVisible č😀 <&> body\r\n";
    private const string HtmlBodyMime = BodyHeaders + "Content-Type: text/html; charset=utf-8\r\n\r\n<p>Rich &amp; č😀 body</p><script>DO NOT EXECUTE</script>\r\n";
    private const string AlternativeBodyMime = BodyHeaders + "Content-Type: multipart/alternative; boundary=alternative\r\n\r\n"
        + "--alternative\r\nContent-Type: text/plain; charset=utf-8\r\n\r\nPlain alternative\r\n"
        + "--alternative\r\nContent-Type: text/html; charset=utf-8\r\n\r\n<p>Rich alternative</p>\r\n--alternative--\r\n";
    private const string MixedBodyMime = BodyHeaders + "Content-Type: multipart/mixed; boundary=mixed\r\n\r\n"
        + "--mixed\r\nContent-Type: text/plain; charset=utf-8\r\n\r\nVisible body\r\n"
        + "--mixed\r\nContent-Type: text/plain; charset=utf-8; name=secret.txt\r\nContent-Disposition: attachment; filename=secret.txt\r\n\r\nPRIVATE ATTACHMENT\r\n--mixed--\r\n";

    [TestMethod]
    [DataRow(CanonicalPath, false)]
    [DataRow(CanonicalPath, true)]
    [DataRow("/eWs/EXCHANGE.ASMX/", false)]
    [DataRow("/eWs/EXCHANGE.ASMX/", true)]
    public async Task GetItemBodyUsesAzureWorkerAndOriginalDurableSession(string path, bool ordinaryClient)
    {
        var fixture = await CaptureFixture.CreateAsync(ordinaryClient, mailFolders: true).ConfigureAwait(false);
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        await fixture.AssertAdminBoundaryAsync(ordinaryClient).ConfigureAwait(false);
        var item = await SeedItemAsync(fixture, GatewayEwsFixtureDomain.InboxId, PlainBodyMime).ConfigureAwait(false);
        Authenticate(fixture);
        var xml = await BodyResponseAsync(fixture, item, "Best", path).ConfigureAwait(false);
        var message = XDocument.Parse(xml).Descendants(Types + "Message").Single();
        var body = message.Element(Types + "Body")!;
        Assert.AreEqual("Visible č😀 <&> body\n", body.Value, StringComparer.Ordinal);
        Assert.AreEqual("Text", (string?)body.Attribute("BodyType"), StringComparer.Ordinal);
        Assert.AreEqual("false", (string?)body.Attribute("IsTruncated"), StringComparer.Ordinal);
        Assert.IsEmpty(body.Elements());
        CollectionAssert.AreEqual(BodyFields, message.Elements().Select(element => element.Name.LocalName).ToArray());
        await fixture.AssertWorkerOperationsAsync(ApplicationOperations.JmapProfileGet, ApplicationOperations.MailOperationExecute).ConfigureAwait(false);
        var database = Context(fixture);
        await using var databaseLifetime = database.ConfigureAwait(false);
        var stored = await database.Emails.SingleAsync(email => email.Id == item).ConfigureAwait(false);
        Assert.IsNull(stored.RawMessage);
        Assert.AreEqual("azure-blob", stored.RawMessageObjectProvider, StringComparer.Ordinal);
    }

    [TestMethod]
    [DataRow("plain", "HTML", "HTML", "<pre>Visible č&#128512; &lt;&amp;&gt; body\n</pre>")]
    [DataRow("html", "Best", "HTML", "<p>Rich &amp; č😀 body</p><script>DO NOT EXECUTE</script>\n")]
    [DataRow("html", "Text", "Text", "Rich & č😀 body\n\n")]
    [DataRow("alternative", "Text", "Text", "Plain alternative")]
    [DataRow("alternative", "Best", "HTML", "<p>Rich alternative</p>")]
    [DataRow("mixed", "Best", "Text", "Visible body")]
    [DataRow("latin", "Text", "Text", "café")]
    public async Task NativeAlternativesCharsetAndConversionAreCompleteWithoutAttachmentDisclosure(string source, string requested, string type, string expected)
    {
        var fixture = await CaptureFixture.CreateAsync(mailFolders: true).ConfigureAwait(false);
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        var mime = source switch
        {
            "plain" => PlainBodyMime,
            "html" => HtmlBodyMime,
            "alternative" => AlternativeBodyMime,
            "mixed" => MixedBodyMime,
            _ => BodyHeaders + "Content-Type: text/plain; charset=iso-8859-1\r\nContent-Transfer-Encoding: base64\r\n\r\nY2Fm6Q==\r\n",
        };
        var item = await SeedItemAsync(fixture, GatewayEwsFixtureDomain.InboxId, mime).ConfigureAwait(false);
        Authenticate(fixture);
        var xml = await BodyResponseAsync(fixture, item, requested).ConfigureAwait(false);
        var body = XDocument.Parse(xml).Descendants(Types + "Body").Single();
        Assert.AreEqual(type, (string?)body.Attribute("BodyType"), StringComparer.Ordinal);
        Assert.AreEqual(expected, body.Value, StringComparer.Ordinal);
        Assert.IsFalse(xml.Contains("PRIVATE ATTACHMENT", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task BodyReferenceForgeryAndDuplicatesRetainOrderedOutcomes()
    {
        var fixture = await CaptureFixture.CreateAsync(mailFolders: true).ConfigureAwait(false);
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        var owned = await SeedItemAsync(fixture, GatewayEwsFixtureDomain.InboxId, PlainBodyMime).ConfigureAwait(false);
        var foreign = await SeedItemAsync(fixture, GatewayEwsFixtureDomain.ForeignFolderId, BodyHeaders + "Content-Type: text/plain\r\n\r\nPRIVATE FOREIGN BODY").ConfigureAwait(false);
        Authenticate(fixture);
        var references = $"<t:ItemId Id='{GatewayEwsItemIdCodec.Encode(GatewayEwsFixtureDomain.ForeignAccountId, foreign)}'/><t:ItemId Id='{ItemId(foreign)}'/>"
            + $"<t:ItemId Id='{ItemId(owned)}'/><t:ItemId Id='{ItemId(owned)}'/>";
        using var content = XmlContent(ItemRequest(references, BodyProperty));
        using var response = await fixture.Client.PostAsync(new Uri(CanonicalPath, UriKind.Relative), content).ConfigureAwait(false);
        var xml = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var document = XDocument.Parse(xml);
        CollectionAssert.AreEqual(BodyReferenceOutcomes, document.Descendants(Messages + "ResponseCode").Select(element => element.Value).ToArray());
        Assert.AreEqual(2, document.Descendants(Types + "Body").Count());
        Assert.IsFalse(xml.Contains("PRIVATE FOREIGN", StringComparison.Ordinal));
        await fixture.AssertRecordedResponseAsync("ews", CanonicalPath, 200, xml, rejection: false).ConfigureAwait(false);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task BodyLimitAndInvalidXmlArePerItemRefusalsWithoutPartialContent(bool invalidXml)
    {
        var fixture = await CaptureFixture.CreateAsync(mailFolders: true).ConfigureAwait(false);
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        var mime = BodyHeaders + "Content-Type: text/plain; charset=utf-8\r\n\r\n" + (invalidXml ? "PRIVATE\u0001INVALID" : new string('A', GatewayEwsBodyCodec.MaximumBodyBytes + 1));
        var item = await SeedItemAsync(fixture, GatewayEwsFixtureDomain.InboxId, mime).ConfigureAwait(false);
        Authenticate(fixture);
        var xml = await BodyResponseAsync(fixture, item, "Text").ConfigureAwait(false);
        Assert.AreEqual(invalidXml ? "ErrorInvalidPropertyRequest" : "ErrorDataSizeLimitExceeded", XDocument.Parse(xml).Descendants(Messages + "ResponseCode").Single().Value, StringComparer.Ordinal);
        Assert.IsFalse(xml.Contains("NoError", StringComparison.Ordinal));
        Assert.IsFalse(xml.Contains("PRIVATE", StringComparison.Ordinal));
        Assert.IsEmpty(XDocument.Parse(xml).Descendants(Types + "Body"));
    }

    [TestMethod]
    public async Task ExactBodyLimitCrossesActualJournalAndWorkerWithoutTruncation()
    {
        var fixture = await CaptureFixture.CreateAsync(mailFolders: true).ConfigureAwait(false);
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        var expected = new string('A', GatewayEwsBodyCodec.MaximumBodyBytes);
        var item = await SeedItemAsync(fixture, GatewayEwsFixtureDomain.InboxId, BodyHeaders + "Content-Type: text/plain; charset=utf-8\r\n\r\n" + expected).ConfigureAwait(false);
        Authenticate(fixture);
        var xml = await BodyResponseAsync(fixture, item, "Text").ConfigureAwait(false);
        var body = XDocument.Parse(xml).Descendants(Types + "Body").Single();
        Assert.AreEqual(expected, body.Value, StringComparer.Ordinal);
        Assert.AreEqual("false", (string?)body.Attribute("IsTruncated"), StringComparer.Ordinal);
    }

    [TestMethod]
    public async Task RepeatedBodyReferencesCannotEscapeFinalEncodedReplyBudget()
    {
        var fixture = await CaptureFixture.CreateAsync(mailFolders: true).ConfigureAwait(false);
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        var item = await SeedItemAsync(fixture, GatewayEwsFixtureDomain.InboxId, BodyHeaders + "Content-Type: text/plain; charset=utf-8\r\n\r\n" + new string('A', GatewayEwsBodyCodec.MaximumBodyBytes)).ConfigureAwait(false);
        Authenticate(fixture);
        using var content = XmlContent(ItemRequest(string.Concat(Enumerable.Repeat($"<t:ItemId Id='{ItemId(item)}'/>", 3)), BodyProperty));
        using var response = await fixture.Client.PostAsync(new Uri(CanonicalPath, UriKind.Relative), content).ConfigureAwait(false);
        var xml = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.InternalServerError, response.StatusCode);
        AssertFault(xml, "ErrorDataSizeLimitExceeded");
        Assert.IsFalse(xml.Contains("NoError", StringComparison.Ordinal));
        Assert.IsEmpty(XDocument.Parse(xml).Descendants(Types + "Body"));
        await fixture.AssertRecordedResponseAsync("ews", CanonicalPath, 500, xml, rejection: false).ConfigureAwait(false);
    }

    [TestMethod]
    [DataRow("text/plain; charset=unsupported-fixture-charset")]
    [DataRow("text/rtf")]
    public async Task InvalidEncodingAndUnsupportedBodyFormatsNeverBecomeInventedText(string contentType)
    {
        var fixture = await CaptureFixture.CreateAsync(mailFolders: true).ConfigureAwait(false);
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        var item = await SeedItemAsync(fixture, GatewayEwsFixtureDomain.InboxId, BodyHeaders + $"Content-Type: {contentType}\r\n\r\nPRIVATE UNREPRESENTABLE").ConfigureAwait(false);
        Authenticate(fixture);
        var xml = await BodyResponseAsync(fixture, item, "Best").ConfigureAwait(false);
        Assert.AreEqual("ErrorInvalidPropertyRequest", XDocument.Parse(xml).Descendants(Messages + "ResponseCode").Single().Value, StringComparer.Ordinal);
        Assert.IsFalse(xml.Contains("PRIVATE", StringComparison.Ordinal));
        Assert.IsEmpty(XDocument.Parse(xml).Descendants(Types + "Body"));
    }

    [TestMethod]
    [DataRow("<t:BodyType>Text</t:BodyType>")]
    [DataRow(BodyProperty)]
    public async Task FindItemBodyOptionsAreRefusedBeforeWorkerDispatch(string fields)
    {
        var fixture = await CaptureFixture.CreateAsync(mailFolders: true).ConfigureAwait(false);
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        Authenticate(fixture);
        using var content = XmlContent(FindRequest("<t:DistinguishedFolderId Id='inbox'/>", fields: fields));
        using var response = await fixture.Client.PostAsync(new Uri(CanonicalPath, UriKind.Relative), content).ConfigureAwait(false);
        var xml = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.InternalServerError, response.StatusCode);
        AssertFault(xml, "ErrorInvalidPropertyRequest");
        await fixture.AssertNoWorkerRequestsAsync().ConfigureAwait(false);
        await fixture.AssertRecordedResponseAsync("ews", CanonicalPath, 500, xml, rejection: false).ConfigureAwait(false);
    }

    [TestMethod]
    [DataRow(0L)]
    [DataRow(1L)]
    public async Task BodyJournalFailureWithholdsBodyAndSuccess(long sequence)
    {
        var fixture = await CaptureFixture.CreateAsync(failSequence: sequence, mailFolders: true).ConfigureAwait(false);
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        var item = await SeedItemAsync(fixture, GatewayEwsFixtureDomain.InboxId, PlainBodyMime).ConfigureAwait(false);
        Authenticate(fixture);
        using var content = XmlContent(ItemRequest($"<t:ItemId Id='{ItemId(item)}'/>", BodyProperty));
        using var response = await fixture.Client.PostAsync(new Uri(CanonicalPath, UriKind.Relative), content).ConfigureAwait(false);
        var xml = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        AssertFault(xml, "ErrorServerBusy");
        Assert.IsFalse(xml.Contains("Visible", StringComparison.Ordinal));
        Assert.IsFalse(xml.Contains("NoError", StringComparison.Ordinal));
        await fixture.AssertPresentationRecordCountAsync(sequence).ConfigureAwait(false);
    }

    [TestMethod]
    [DataRow("<t:BodyType>text</t:BodyType>", "ErrorSchemaValidation")]
    [DataRow("<t:BodyType>HTML</t:BodyType><t:FilterHtmlContent>true</t:FilterHtmlContent>", "ErrorInvalidPropertyRequest")]
    [DataRow("<t:BodyType>HTML</t:BodyType><t:BodyType>Text</t:BodyType>", "ErrorInvalidPropertyRequest")]
    public async Task InvalidBodyOptionsHaveBoundedJournaledRefusalAndNoDispatch(string fields, string code)
    {
        var fixture = await CaptureFixture.CreateAsync(mailFolders: true).ConfigureAwait(false);
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        Authenticate(fixture);
        using var content = XmlContent(ItemRequest("<t:ItemId Id='opaque'/>", fields + BodyProperty));
        using var response = await fixture.Client.PostAsync(new Uri(CanonicalPath, UriKind.Relative), content).ConfigureAwait(false);
        var xml = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.InternalServerError, response.StatusCode);
        AssertFault(xml, code);
        await fixture.AssertNoWorkerRequestsAsync().ConfigureAwait(false);
        await fixture.AssertRecordedResponseAsync("ews", CanonicalPath, 500, xml, rejection: false).ConfigureAwait(false);
    }

    [TestMethod]
    [DataRow("plain-http", 403)]
    [DataRow("missing-password", 401)]
    [DataRow("bearer", 401)]
    public async Task BodyShapeCannotRelaxTlsOrPasswordAuthority(string mode, int status)
    {
        var fixture = await CaptureFixture.CreateAsync(mailFolders: true).ConfigureAwait(false);
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        if (mode is "plain-http")
        {
            Authenticate(fixture);
            fixture.Client.DefaultRequestHeaders.Remove("X-Forwarded-Proto");
        }
        else if (mode is "bearer") fixture.Client.DefaultRequestHeaders.Authorization = new("Bearer", "not-ews-authority");
        else fixture.Client.DefaultRequestHeaders.Authorization = null;
        using var content = XmlContent(ItemRequest("<t:ItemId Id='opaque'/>", BodyProperty));
        using var response = await fixture.Client.PostAsync(new Uri(CanonicalPath, UriKind.Relative), content).ConfigureAwait(false);
        var xml = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.AreEqual((HttpStatusCode)status, response.StatusCode);
        AssertFault(xml, "ErrorAccessDenied");
        Assert.IsFalse(xml.Contains("NoError", StringComparison.Ordinal));
        await fixture.AssertNoWorkerRequestsAsync().ConfigureAwait(false);
        await fixture.AssertRecordedResponseAsync("ews", CanonicalPath, status, xml, rejection: false).ConfigureAwait(false);
    }

    private static async Task<string> BodyResponseAsync(CaptureFixture fixture, Guid item, string bodyType, string path = CanonicalPath)
    {
        using var content = XmlContent(ItemRequest($"<t:ItemId Id='{ItemId(item)}'/>", $"<t:BodyType>{bodyType}</t:BodyType>" + BodyProperty));
        using var response = await fixture.Client.PostAsync(new Uri(path, UriKind.Relative), content).ConfigureAwait(false);
        var xml = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.IsTrue(response.Headers.CacheControl?.NoStore);
        Assert.AreEqual("text/xml", response.Content.Headers.ContentType?.MediaType, StringComparer.Ordinal);
        await fixture.AssertRecordedResponseAsync("ews", path, 200, xml, rejection: false).ConfigureAwait(false);
        return xml;
    }
}
