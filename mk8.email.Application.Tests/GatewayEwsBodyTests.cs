using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using mk8.email.Configuration;
using mk8.email.Contracts.Messaging;
using mk8.email.Gateway.ApplicationBridge;
using mk8.email.Gateway.Protocols.Ews;

namespace mk8.email.Application.Tests;

[TestClass]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1812", Justification = "MSTest DiscoverInternals executes this body shape/projection fixture; discovery is retained.")]
internal sealed class GatewayEwsBodyTests
{
    private const string BodyProperty = "<t:AdditionalProperties><t:FieldURI FieldURI='item:Body'/></t:AdditionalProperties>";
    private static readonly Guid Account = new("1138f53b-3efa-4a59-8602-e215c0df1e35");
    private static readonly Guid Item = new("2238f53b-3efa-4a59-8602-e215c0df1e35");
    private static readonly Guid Folder = new("1338f53b-3efa-4a59-8602-e215c0df1e35");
    private static readonly JmapApplicationProfile Profile = new("owner@example.test",
        new(1024, 1, 65_536, 8, 64, 500, 500, 50, 100, 1024, [], []), [new($"A{Account:N}", "owner@example.test", true, false, true)]);
    private static readonly ProtocolAuthentication Authentication = new(ProtocolAuthenticationKinds.Password, "owner@example.test", "fixture-secret");

    [TestMethod]
    [DataRow("", "Best")]
    [DataRow("<t:BodyType>Best</t:BodyType>", "Best")]
    [DataRow("<t:BodyType>Text</t:BodyType>", "Text")]
    [DataRow("<t:IncludeMimeContent>0</t:IncludeMimeContent><t:BodyType>HTML</t:BodyType><t:FilterHtmlContent>false</t:FilterHtmlContent>", "HTML")]
    public async Task BodyShapeIsExplicitAndOrdered(string options, string expected)
    {
        var request = await ParseAsync(options + BodyProperty).ConfigureAwait(false);
        Assert.AreEqual(expected, request.BodyType, StringComparer.Ordinal);
        Assert.IsTrue(request.Properties.SetEquals(["ItemId", "Body"]));
    }

    [TestMethod]
    [DataRow("<t:BodyType>Text</t:BodyType><t:BodyType>HTML</t:BodyType>", "ErrorInvalidPropertyRequest")]
    [DataRow("<t:BodyType>text</t:BodyType>", "ErrorSchemaValidation")]
    [DataRow("<t:BodyType Encoding='utf-8'>Text</t:BodyType>", "ErrorSchemaValidation")]
    [DataRow("<t:BodyType><t:BodyType>Text</t:BodyType></t:BodyType>", "ErrorSchemaValidation")]
    [DataRow("<t:FilterHtmlContent>true</t:FilterHtmlContent>", "ErrorInvalidPropertyRequest")]
    [DataRow("<t:FilterHtmlContent>1</t:FilterHtmlContent>", "ErrorInvalidPropertyRequest")]
    [DataRow("<t:FilterHtmlContent>False</t:FilterHtmlContent>", "ErrorSchemaValidation")]
    [DataRow("<t:FilterHtmlContent>false</t:FilterHtmlContent><t:BodyType>Text</t:BodyType>", "ErrorInvalidPropertyRequest")]
    [DataRow("<t:ConvertHtmlCodePageToUTF8>true</t:ConvertHtmlCodePageToUTF8>", "ErrorInvalidPropertyRequest")]
    public async Task UnsupportedOptionsDoNotBecomeBodyReads(string options, string code)
    {
        var error = await Assert.ThrowsAsync<GatewayEwsRequestException>(() => ParseAsync(options + BodyProperty)).ConfigureAwait(false);
        Assert.AreEqual(code, error.Code, StringComparer.Ordinal);
    }

    [TestMethod]
    [DataRow("<t:BodyType>Text</t:BodyType>")]
    [DataRow(BodyProperty)]
    public async Task FindItemCannotAcquireBodyProjection(string fields)
    {
        await Assert.ThrowsAsync<GatewayEwsRequestException>(() => ParseAsync(fields, find: true)).ConfigureAwait(false);
    }

    [TestMethod]
    [DataRow("Best", "Text", "One <&> č😀\nTwo")]
    [DataRow("Text", "Text", "One <&> č😀\nTwo")]
    [DataRow("HTML", "HTML", "<pre>One &lt;&amp;&gt; č&#128512;\nTwo</pre>")]
    public void PlainBodyIsCompleteAndXmlEscaped(string requested, string type, string expected)
    {
        var body = GatewayEwsBodyCodec.Render(Snapshot(Part("1", "text/plain", "One <&> č😀\r\nTwo")), requested);
        Assert.AreEqual(type, (string?)body.Attribute("BodyType"), StringComparer.Ordinal);
        Assert.AreEqual("false", (string?)body.Attribute("IsTruncated"), StringComparer.Ordinal);
        Assert.AreEqual(expected, body.Value, StringComparer.Ordinal);
        Assert.IsEmpty(body.Elements());
        Assert.AreEqual(expected, XElement.Parse(body.ToString()).Value, StringComparer.Ordinal);
    }

    [TestMethod]
    [DataRow("Best", "HTML", "<p>Rich &amp; body</p>")]
    [DataRow("HTML", "HTML", "<p>Rich &amp; body</p>")]
    [DataRow("Text", "Text", "Plain alternative")]
    public void MultipartAlternativeSelectsRequestedNativeRepresentation(string requested, string type, string expected)
    {
        var snapshot = Snapshot(Part("1.1", "text/plain", "Plain alternative"), Part("1.2", "text/html", "<p>Rich &amp; body</p>"),
            Part(null, "multipart/alternative", null, children: [0, 1]));
        var body = GatewayEwsBodyCodec.Render(snapshot, requested);
        Assert.AreEqual(type, (string?)body.Attribute("BodyType"), StringComparer.Ordinal);
        Assert.AreEqual(expected, body.Value, StringComparer.Ordinal);
    }

    [TestMethod]
    public void HtmlToTextDecodesEntitiesSeparatesBlocksAndNeverFetchesOrExecutes()
    {
        var html = "<HTML><HEAD><title>HIDDEN TITLE</title></HEAD><body><p>Hello &amp; č😀</p><script>HIDDEN SCRIPT</script><style>HIDDEN STYLE</style><template><template>HIDDEN TEMPLATE</template>ALSO HIDDEN</template><div>Next<br>line</div><a href='http://127.0.0.1:1/never'>Link</a><img src='http://127.0.0.1:1/never'></body></HTML>";
        var body = GatewayEwsBodyCodec.Render(Snapshot(Part("1", "text/html", html)), "Text");
        Assert.AreEqual("Hello & č😀\nNext\nline\nLink", body.Value, StringComparer.Ordinal);
        Assert.IsFalse(body.Value.Contains("HIDDEN", StringComparison.Ordinal));
        Assert.IsFalse(body.Value.Contains("http", StringComparison.Ordinal));
    }

    [TestMethod]
    public void MixedAndRelatedBodiesExcludeAttachmentTextAndInlineBinary()
    {
        var snapshot = Snapshot(Part("1.1.1", "text/html", "<p>Visible</p>"), Part("1.1.2", "image/png", null),
            Part(null, "multipart/related", null, children: [0, 1]), Part("1.2", "text/plain", "PRIVATE ATTACHMENT", disposition: "attachment"),
            Part(null, "multipart/mixed", null, children: [2, 3]));
        var body = GatewayEwsBodyCodec.Render(snapshot, "Best");
        Assert.AreEqual("<p>Visible</p>", body.Value, StringComparer.Ordinal);
        Assert.IsFalse(body.ToString().Contains("PRIVATE", StringComparison.Ordinal));
    }

    [TestMethod]
    [DataRow("mixed", "visible", "Best")]
    [DataRow("mixed", "visible", "Text")]
    [DataRow("mixed", "visible", "HTML")]
    [DataRow("alternative", "visible", "Best")]
    [DataRow("alternative", "visible", "Text")]
    [DataRow("alternative", "visible", "HTML")]
    [DataRow("mixed", "visible-alternative", "Best")]
    [DataRow("mixed", "visible-alternative", "Text")]
    [DataRow("mixed", "visible-alternative", "HTML")]
    [DataRow("alternative", "visible-alternative", "Best")]
    [DataRow("alternative", "visible-alternative", "Text")]
    [DataRow("alternative", "visible-alternative", "HTML")]
    [DataRow("mixed", "nested-only", "Best")]
    [DataRow("mixed", "nested-only", "Text")]
    [DataRow("mixed", "nested-only", "HTML")]
    [DataRow("alternative", "nested-only", "Best")]
    [DataRow("alternative", "nested-only", "Text")]
    [DataRow("alternative", "nested-only", "HTML")]
    [DataRow("mixed", "root-only", "Best")]
    [DataRow("mixed", "root-only", "Text")]
    [DataRow("mixed", "root-only", "HTML")]
    [DataRow("alternative", "root-only", "Best")]
    [DataRow("alternative", "root-only", "Text")]
    [DataRow("alternative", "root-only", "HTML")]
    public void AttachedMultipartSubtreesCannotChooseOrLeakBody(string subtype, string layout, string requested)
    {
        var parts = new List<MailMimePartSnapshot>
        {
            Part("2.1", "text/plain", "PRIVATE ATTACHMENT PLAIN", disposition: "inline"),
            Part("2.2", "text/html", "<p>PRIVATE ATTACHMENT HTML</p>", disposition: "inline"),
            Part(null, "multipart/" + subtype, null, disposition: "AtTaChMeNt", children: [0, 1]),
        };
        if (layout is "nested-only")
            parts.Add(Part(null, "multipart/mixed", null, disposition: "inline", children: [2]));
        else if (layout is "visible")
        {
            parts.Add(Part("1", "text/plain", "Visible body"));
            parts.Add(Part(null, "multipart/mixed", null, children: [3, 2]));
        }
        else if (layout is "visible-alternative")
        {
            parts.Add(Part("1.1", "text/plain", "Visible plain"));
            parts.Add(Part("1.2", "text/html", "<p>Visible HTML</p>"));
            parts.Add(Part(null, "multipart/alternative", null, children: [3, 4]));
            parts.Add(Part(null, "multipart/mixed", null, children: [5, 2]));
        }
        var snapshot = Snapshot(parts.ToArray());
        var body = GatewayEwsBodyCodec.Render(snapshot, requested);
        var html = requested is "HTML" || requested is "Best" && layout is "visible-alternative";
        var expected = layout switch
        {
            "visible" => html ? "<pre>Visible body</pre>" : "Visible body",
            "visible-alternative" => html ? "<p>Visible HTML</p>" : "Visible plain",
            _ => "",
        };
        Assert.AreEqual(html ? "HTML" : "Text", (string?)body.Attribute("BodyType"), StringComparer.Ordinal);
        Assert.AreEqual(expected, body.Value, StringComparer.Ordinal);
        Assert.AreEqual("false", (string?)body.Attribute("IsTruncated"), StringComparer.Ordinal);
        Assert.IsFalse(body.ToString().Contains("PRIVATE", StringComparison.Ordinal));
        Assert.AreEqual("PRIVATE ATTACHMENT PLAIN", snapshot.Parts[0].Text, StringComparer.Ordinal);
        Assert.HasCount(2, snapshot.Parts[2].Children);
    }

    [TestMethod]
    [DataRow("Best")]
    [DataRow("Text")]
    [DataRow("HTML")]
    public void AttachedUnsupportedAndInvalidTextCannotTriggerBodyRefusal(string requested)
    {
        var snapshot = Snapshot(Part("1.1", "text/rtf", "PRIVATE unsupported"),
            Part("1.2", "text/html", "PRIVATE\0invalid") with { EncodingProblem = true },
            Part(null, "multipart/mixed", null, disposition: "attachment", children: [0, 1]),
            Part(null, "multipart/mixed", null, children: [2]));
        Assert.AreEqual("", GatewayEwsBodyCodec.Render(snapshot, requested).Value, StringComparer.Ordinal);
    }

    [TestMethod]
    [DataRow("Best")]
    [DataRow("Text")]
    [DataRow("HTML")]
    public void UnknownMultipartDispositionRequiresSeparatePresentation(string requested)
    {
        var snapshot = Snapshot(Part("1.1", "text/plain", "PRIVATE UNKNOWN DISPOSITION"),
            Part(null, "multipart/mixed", null, disposition: "x-restricted", children: [0]));
        Assert.AreEqual("", GatewayEwsBodyCodec.Render(snapshot, requested).Value, StringComparer.Ordinal);
    }

    [TestMethod]
    public void VisibilityProjectionRefusesInvalidAttachedGraphRatherThanRepairingIt()
    {
        var snapshot = Snapshot(Part(null, "multipart/mixed", null, disposition: "attachment", children: [0]));
        Assert.Throws<InvalidOperationException>(() => GatewayEwsBodyCodec.Render(snapshot, "Best"));
        Assert.Throws<InvalidOperationException>(() => GatewayEwsBodyCodec.Render(snapshot with { RootPart = 100 }, "Best"));
    }

    [TestMethod]
    [DataRow("Best", "Text")]
    [DataRow("Text", "Text")]
    [DataRow("HTML", "HTML")]
    public void AbsentBodyReturnsExplicitEmptyBody(string requested, string type)
    {
        var body = GatewayEwsBodyCodec.Render(Snapshot(), requested);
        Assert.AreEqual(type, (string?)body.Attribute("BodyType"), StringComparer.Ordinal);
        Assert.AreEqual("", body.Value, StringComparer.Ordinal);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void NativeAndConversionBudgetsRefuseWithoutTruncation(bool conversion)
    {
        var text = new string(conversion ? '&' : 'A', conversion ? 220_000 : GatewayEwsBodyCodec.MaximumBodyBytes + 1);
        var error = Assert.Throws<GatewayEwsRequestException>(() => GatewayEwsBodyCodec.Render(Snapshot(Part("1", "text/plain", text)), conversion ? "HTML" : "Text"));
        Assert.AreEqual("ErrorDataSizeLimitExceeded", error.Code, StringComparer.Ordinal);
    }

    [TestMethod]
    public void CombinedSourceBudgetAndUnsupportedInlineFormatAreExplicitRefusals()
    {
        var html = "<p>" + new string('A', 600_000) + "</p>";
        var snapshot = Snapshot(Part("1.1", "text/html", html), Part("1.2", "text/html", html), Part(null, "multipart/mixed", null, children: [0, 1]));
        Assert.AreEqual("ErrorDataSizeLimitExceeded", Assert.Throws<GatewayEwsRequestException>(() => GatewayEwsBodyCodec.Render(snapshot, "Text")).Code, StringComparer.Ordinal);
        Assert.AreEqual("ErrorInvalidPropertyRequest", Assert.Throws<GatewayEwsRequestException>(() => GatewayEwsBodyCodec.Render(Snapshot(Part("1", "text/rtf", "unsupported")), "Best")).Code, StringComparer.Ordinal);
    }

    [TestMethod]
    public void ExactUtf8BodyBoundarySucceedsWithoutClaimingTruncation()
    {
        var text = new string('č', GatewayEwsBodyCodec.MaximumBodyBytes / 2);
        Assert.AreEqual(text, GatewayEwsBodyCodec.Render(Snapshot(Part("1", "text/plain", text)), "Text").Value, StringComparer.Ordinal);
        Assert.AreEqual("ErrorDataSizeLimitExceeded", Assert.Throws<GatewayEwsRequestException>(() => GatewayEwsBodyCodec.Render(Snapshot(Part("1", "text/plain", text + "A")), "Text")).Code, StringComparer.Ordinal);
    }

    [TestMethod]
    public void InvalidXmlAndEncodingProblemsAreRefusedRatherThanSilentlyRepaired()
    {
        Assert.Throws<XmlException>(() => GatewayEwsBodyCodec.Render(Snapshot(Part("1", "text/plain", "invalid\0text")), "Text"));
        var part = Part("1", "text/plain", "replacement") with { EncodingProblem = true };
        Assert.AreEqual("ErrorInvalidPropertyRequest", Assert.Throws<GatewayEwsRequestException>(() => GatewayEwsBodyCodec.Render(Snapshot(part), "Text")).Code, StringComparer.Ordinal);
        Assert.Throws<InvalidOperationException>(() => GatewayEwsBodyCodec.Render(Snapshot(Part("1", "text/plain", null)), "Text"));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task TextIsRequestedOnlyForBodyAndRepeatedReferencesRetainTheirOutcomes(bool body)
    {
        var snapshot = Snapshot(Part("1", "text/plain", "BODY CONTENT"));
        var reply = new MailOperationResult(new(MailOperationKind.ReadMessages, ApplicationValueCodec.Encode(JsonSerializer.SerializeToNode(
            new MailMessageReadResult(MailMessageReadStatus.Ok, "s10", [new(Item, snapshot)]), JsonSerializerOptions.Web))),
            new Dictionary<string, string>(StringComparer.Ordinal), Profile);
        var transport = new Transport(new(JmapApplicationOutcomes.Ok, OperationResult: reply));
        var client = new GatewayEwsClient(transport, new EnvironmentConfig());
        var parsed = await ParseAsync(body ? BodyProperty : "<t:BodyType>HTML</t:BodyType>").ConfigureAwait(false);
        var xml = await GatewayEwsItemResponse.ExecuteAsync(client, Authentication, Profile, Account, parsed, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(body, transport.Command!.IncludeText);
        Assert.AreEqual(2, XDocument.Parse(xml).Descendants(GatewayEwsSoap.Types + "Message").Count());
        Assert.AreEqual(body ? 2 : 0, XDocument.Parse(xml).Descendants(GatewayEwsSoap.Types + "Body").Count());
        Assert.AreEqual(body, xml.Contains("BODY CONTENT", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task BodySerializationIsStoppedDuringAccumulationNotOnlyAtFinalHttpWrite()
    {
        var snapshot = Snapshot(Part("1", "text/plain", new string('&', 200_000)));
        var reply = new MailOperationResult(new(MailOperationKind.ReadMessages, ApplicationValueCodec.Encode(JsonSerializer.SerializeToNode(
            new MailMessageReadResult(MailMessageReadStatus.Ok, "s10", [new(Item, snapshot)]), JsonSerializerOptions.Web))),
            new Dictionary<string, string>(StringComparer.Ordinal), Profile);
        var transport = new Transport(new(JmapApplicationOutcomes.Ok, OperationResult: reply));
        var environment = new EnvironmentConfig { Messaging = new MessagingConfig { MaxPayloadBytes = 2 * 1024 * 1024 } };
        var client = new GatewayEwsClient(transport, environment);
        var request = await ParseAsync(BodyProperty).ConfigureAwait(false);
        var error = await Assert.ThrowsAsync<GatewayEwsRequestException>(() => GatewayEwsItemResponse.ExecuteAsync(client, Authentication, Profile,
            Account, request, CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual("ErrorDataSizeLimitExceeded", error.Code, StringComparer.Ordinal);
    }

    private static Task<GatewayEwsRequest> ParseAsync(string fields, bool find = false)
    {
        var id = GatewayEwsItemIdCodec.Encode(Account, Item);
        var operation = find ? $"<m:FindItem Traversal='Shallow'><m:ItemShape><t:BaseShape>IdOnly</t:BaseShape>{fields}</m:ItemShape><m:ParentFolderIds><t:DistinguishedFolderId Id='inbox'/></m:ParentFolderIds></m:FindItem>"
            : $"<m:GetItem><m:ItemShape><t:BaseShape>IdOnly</t:BaseShape>{fields}</m:ItemShape><m:ItemIds><t:ItemId Id='{id}'/><t:ItemId Id='{id}'/></m:ItemIds></m:GetItem>";
        return ReadAsync($"<s:Envelope xmlns:s='{GatewayEwsSoap.Soap}' xmlns:m='{GatewayEwsSoap.Messages}' xmlns:t='{GatewayEwsSoap.Types}'><s:Body>{operation}</s:Body></s:Envelope>");
    }

    private static async Task<GatewayEwsRequest> ReadAsync(string xml)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        return await GatewayEwsRequestParser.ReadAsync(stream, CancellationToken.None).ConfigureAwait(false);
    }

    private static MailMessageSnapshot Snapshot(params MailMimePartSnapshot[] parts) => new(Item, null, null, 100,
        new(Item, Folder, "thread", [], 100, new DateTime(2026, 10, 7, 0, 0, 0, DateTimeKind.Utc)), [], parts.Length == 0 ? null : parts.Length - 1, parts);
    private static MailMimePartSnapshot Part(string? path, string type, string? text, string? disposition = null, IReadOnlyList<int>? children = null) =>
        new(path, text is null ? 0 : Encoding.UTF8.GetByteCount(text), [], null, type, "utf-8", disposition, null, null, null, text, false, children ?? []);

    private sealed class Transport(JmapApplicationResult reply) : IGatewayApplicationTransport
    {
        internal MailMessageReadCommand? Command { get; private set; }
        public Task<TResponse> SendAsync<TRequest, TResponse>(string protocol, string operation, TRequest request, CancellationToken cancellationToken = default)
        {
            var input = request as MailOperationApplicationRequest;
            Assert.IsNotNull(input);
            Command = input.Command.Arguments.Deserialize<MailMessageReadCommand>(JsonSerializerOptions.Web);
            return Task.FromResult((TResponse)(object)reply);
        }
    }
}
