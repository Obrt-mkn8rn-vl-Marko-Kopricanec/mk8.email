using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using mk8.email.Configuration;
using mk8.email.Contracts.Messaging;
using mk8.email.Gateway.ApplicationBridge;
using mk8.email.Gateway.Protocols.Ews;

namespace mk8.email.Application.Tests;

[TestClass]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1812", Justification = "MSTest DiscoverInternals executes this attachment grammar/catalog/typed-transport fixture; discovery is retained.")]
internal sealed class GatewayEwsAttachmentTests
{
    private static readonly Guid Account = new("1138f53b-3efa-4a59-8602-e215c0df1e35");
    private static readonly Guid Item = new("2238f53b-3efa-4a59-8602-e215c0df1e35");
    private static readonly Guid Folder = new("1338f53b-3efa-4a59-8602-e215c0df1e35");
    private static readonly byte[] Bytes = [0, 1, 2, 3, 255, 254];
    private static readonly string[] FileFields = ["AttachmentId", "Name", "ContentType", "ContentId", "ContentLocation", "Size", "IsInline", "Content"];
    private static readonly string[] Outcomes = ["ErrorInvalidAttachmentId", "ErrorAccessDenied", "NoError", "NoError"];
    private static readonly string[] ReferenceOrder = ["one", "two", "one"];
    private static readonly Guid[] ParentIds = [Item];
    private const string Headers = "From: Sender <sender@example.test>\r\nSubject: Attachment fixture\r\nMIME-Version: 1.0\r\n";
    private const string FileHeaders = "Content-Type: application/octet-stream\r\nContent-Disposition: attachment; filename*=utf-8''r%C3%A9sum%C3%A9%3C%3E%26.bin\r\n"
        + "Content-ID: <file@example.test>\r\nContent-Location: files/private.bin\r\nContent-Transfer-Encoding: base64\r\n\r\nAAECA//+\r\n";
    internal const string Mime = Headers + "Content-Type: multipart/mixed; boundary=files\r\n\r\n--files\r\nContent-Type: text/plain\r\n\r\nVisible body\r\n--files\r\n" + FileHeaders + "--files--\r\n";
    private static readonly ProtocolAuthentication Authentication = new(ProtocolAuthenticationKinds.Password, "owner@example.test", "fixture-secret");
    private static readonly JmapApplicationProfile Profile = new("owner@example.test",
        new(1024, 1, 65_536, 8, 64, 500, 500, 50, 100, 2_097_152, [], []), [new($"A{Account:N}", "owner@example.test", true, false, true)]);

    [TestMethod]
    [DataRow("")]
    [DataRow("<m:AttachmentShape/>")]
    [DataRow("<m:AttachmentShape><t:IncludeMimeContent>false</t:IncludeMimeContent></m:AttachmentShape>")]
    [DataRow("<m:AttachmentShape><t:IncludeMimeContent>0</t:IncludeMimeContent></m:AttachmentShape>")]
    public async Task AttachmentShapeIsOptionalAndReferencesRemainOrdered(string shape)
    {
        var parsed = await ParseAsync(Request("<t:AttachmentId Id='one'/><t:AttachmentId Id='two'/><t:AttachmentId Id='one'/>", shape)).ConfigureAwait(false);
        Assert.AreEqual("GetAttachment", parsed.Operation, StringComparer.Ordinal);
        CollectionAssert.AreEqual(ReferenceOrder, parsed.Attachments!.ToArray());
        Assert.IsNull(parsed.Items);
        Assert.IsFalse(parsed.IsMutation);
    }

    [TestMethod]
    [DataRow("<m:AttachmentShape><t:IncludeMimeContent>true</t:IncludeMimeContent></m:AttachmentShape>")]
    [DataRow("<m:AttachmentShape><t:BodyType>Best</t:BodyType></m:AttachmentShape>")]
    [DataRow("<m:AttachmentShape><t:FilterHtmlContent>false</t:FilterHtmlContent></m:AttachmentShape>")]
    [DataRow("<m:AttachmentShape><t:AdditionalProperties/></m:AttachmentShape>")]
    [DataRow("<m:AttachmentShape><t:IncludeMimeContent>false</t:IncludeMimeContent><t:IncludeMimeContent>false</t:IncludeMimeContent></m:AttachmentShape>")]
    public async Task UnsupportedAttachmentShapesDoNotAcquireContent(string shape)
    {
        Assert.AreEqual("ErrorInvalidPropertyRequest", (await Assert.ThrowsAsync<GatewayEwsRequestException>(() => ParseAsync(Request("<t:AttachmentId Id='one'/>", shape))).ConfigureAwait(false)).Code, StringComparer.Ordinal);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("<t:AttachmentId/>")]
    [DataRow("<t:ItemId Id='item'/>")]
    [DataRow("<t:AttachmentId Id='one' RootItemId='authority'/>")]
    [DataRow("<t:AttachmentId Id='one'><t:AttachmentId Id='child'/></t:AttachmentId>")]
    public async Task InvalidAttachmentReferencesAreRefused(string references)
    {
        Assert.AreEqual("ErrorSchemaValidation", (await Assert.ThrowsAsync<GatewayEwsRequestException>(() => ParseAsync(Request(references))).ConfigureAwait(false)).Code, StringComparer.Ordinal);
    }

    [TestMethod]
    public async Task AttachmentReferenceCountIsBounded()
    {
        var xml = Request(string.Concat(Enumerable.Repeat("<t:AttachmentId Id='one'/>", 33)));
        Assert.AreEqual("ErrorExceededFindCountLimit", (await Assert.ThrowsAsync<GatewayEwsRequestException>(() => ParseAsync(xml)).ConfigureAwait(false)).Code, StringComparer.Ordinal);
    }

    [TestMethod]
    public void AttachmentIdsAreCanonicalAndBoundParentContentNotFlags()
    {
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(Mime));
        var id = GatewayEwsAttachmentIdCodec.Encode(Account, Item, hash, 3);
        Assert.IsTrue(GatewayEwsAttachmentIdCodec.TryDecode(id, out var account, out var parent, out var digest, out var position));
        Assert.AreEqual(Account, account);
        Assert.AreEqual(Item, parent);
        Assert.AreEqual(Convert.ToHexStringLower(hash), digest, StringComparer.Ordinal);
        Assert.AreEqual(3, position);
        Assert.IsFalse(GatewayEwsItemIdCodec.TryDecode(id, out _, out _));
        Assert.IsFalse(GatewayEwsFolderIdCodec.TryDecode(id, out _, out _));
        Assert.IsFalse(GatewayEwsAttachmentIdCodec.TryDecode(id + " ", out _, out _, out _, out _));
        Assert.IsFalse(GatewayEwsAttachmentIdCodec.TryDecode(GatewayEwsItemIdCodec.Encode(Account, Item), out _, out _, out _, out _));
    }

    [TestMethod]
    public void FileMetadataAndContentAreSeparateAndFollowSchemaOrder()
    {
        var raw = Encoding.ASCII.GetBytes(Mime);
        using var catalog = GatewayEwsAttachmentCatalog.Load(raw);
        var metadata = catalog.Metadata(Account, Item).Elements().Single();
        Assert.IsNull(metadata.Element(GatewayEwsSoap.Types + "Content"));
        var id = (string)metadata.Element(GatewayEwsSoap.Types + "AttachmentId")!.Attribute("Id")!;
        Assert.IsTrue(GatewayEwsAttachmentIdCodec.TryDecode(id, out _, out _, out var hash, out var position));
        var file = catalog.Get(Account, Item, hash, position);
        CollectionAssert.AreEqual(FileFields, file.Elements().Select(element => element.Name.LocalName).ToArray());
        Assert.AreEqual("résumé<>&.bin", file.Element(GatewayEwsSoap.Types + "Name")!.Value, StringComparer.Ordinal);
        Assert.AreEqual("file@example.test", file.Element(GatewayEwsSoap.Types + "ContentId")!.Value, StringComparer.Ordinal);
        Assert.AreEqual("files/private.bin", file.Element(GatewayEwsSoap.Types + "ContentLocation")!.Value, StringComparer.Ordinal);
        Assert.AreEqual("6", file.Element(GatewayEwsSoap.Types + "Size")!.Value, StringComparer.Ordinal);
        Assert.AreEqual("false", file.Element(GatewayEwsSoap.Types + "IsInline")!.Value, StringComparer.Ordinal);
        CollectionAssert.AreEqual(Bytes, Convert.FromBase64String(file.Element(GatewayEwsSoap.Types + "Content")!.Value));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void RelatedResourcesAreInlineEvenWhenStartSelectsTheSecondPart(bool reverse)
    {
        var html = "Content-Type: text/html\r\nContent-ID: <root>\r\n\r\n<img src='cid:photo'>\r\n";
        var image = "Content-Type: image/png\r\nContent-ID: <photo>\r\nContent-Transfer-Encoding: base64\r\n\r\nAAECA//+\r\n";
        var raw = Headers + "Content-Type: multipart/related; boundary=related; start=\"<root>\"\r\n\r\n--related\r\n"
            + (reverse ? image : html) + "--related\r\n" + (reverse ? html : image) + "--related--\r\n";
        using var catalog = GatewayEwsAttachmentCatalog.Load(Encoding.ASCII.GetBytes(raw));
        var file = catalog.Metadata(Account, Item).Elements().Single();
        Assert.AreEqual("true", file.Element(GatewayEwsSoap.Types + "IsInline")!.Value, StringComparer.Ordinal);
        Assert.AreEqual("image/png", file.Element(GatewayEwsSoap.Types + "ContentType")!.Value, StringComparer.Ordinal);
    }

    [TestMethod]
    [DataRow("mixed")]
    [DataRow("alternative")]
    public void AttachedMultipartChildrenNeverBecomeInventedFileAttachments(string subtype)
    {
        var raw = Headers + $"Content-Type: multipart/{subtype}; boundary=private\r\nContent-Disposition: attachment\r\n\r\n--private\r\n" + FileHeaders + "--private--\r\n";
        Assert.AreEqual("ErrorInvalidPropertyRequest", Assert.Throws<GatewayEwsRequestException>(() =>
        {
            using var catalog = GatewayEwsAttachmentCatalog.Load(Encoding.ASCII.GetBytes(raw));
            catalog.Metadata(Account, Item);
        }).Code, StringComparer.Ordinal);
    }

    [TestMethod]
    public void ReplacedMimeInvalidatesTheOldAttachmentIdentity()
    {
        using var original = GatewayEwsAttachmentCatalog.Load(Encoding.ASCII.GetBytes(Mime));
        var id = (string)original.Metadata(Account, Item).Descendants(GatewayEwsSoap.Types + "AttachmentId").Single().Attribute("Id")!;
        Assert.IsTrue(GatewayEwsAttachmentIdCodec.TryDecode(id, out _, out _, out var hash, out var index));
        using var replaced = GatewayEwsAttachmentCatalog.Load(Encoding.ASCII.GetBytes(Mime.Replace("AAECA//+", "BAUGBwgJ", StringComparison.Ordinal)));
        Assert.AreEqual("ErrorInvalidAttachmentId", Assert.Throws<GatewayEwsRequestException>(() => replaced.Get(Account, Item, hash, index)).Code, StringComparer.Ordinal);
    }

    [TestMethod]
    public async Task OrderedForgedAndRepeatedAttachmentReferencesKeepOneAuthorizedParentRead()
    {
        var raw = Encoding.ASCII.GetBytes(Mime);
        var hash = SHA256.HashData(raw);
        var id = GatewayEwsAttachmentIdCodec.Encode(Account, Item, hash, 0);
        var foreign = GatewayEwsAttachmentIdCodec.Encode(Folder, Item, hash, 0);
        var request = await ParseAsync(Request($"<t:AttachmentId Id='opaque'/><t:AttachmentId Id='{foreign}'/><t:AttachmentId Id='{id}'/><t:AttachmentId Id='{id}'/>")).ConfigureAwait(false);
        var transport = new Transport(raw);
        var xml = await GatewayEwsAttachmentResponse.ExecuteAsync(new(transport, new EnvironmentConfig()), Authentication, Profile, Account, request, CancellationToken.None).ConfigureAwait(false);
        var document = XDocument.Parse(xml);
        CollectionAssert.AreEqual(Outcomes, document.Descendants(GatewayEwsSoap.Messages + "ResponseCode").Select(element => element.Value).ToArray());
        Assert.HasCount(2, document.Descendants(GatewayEwsSoap.Types + "Content").ToArray());
        Assert.AreEqual(1, transport.Calls);
    }

    [TestMethod]
    [DataRow("attachment", false, true)]
    [DataRow("inline", false, false)]
    [DataRow(null, false, true)]
    [DataRow(null, true, false)]
    public void AttachmentIndicatorExcludesRelatedAndExplicitInlineResources(string? disposition, bool related, bool expected)
    {
        var part = new MailMimePartSnapshot("1", 6, [], "photo.png", "image/png", null, disposition, "photo", null, null, null, false, []);
        var parts = new List<MailMimePartSnapshot> { part };
        if (related) parts.Add(new MailMimePartSnapshot(null, 0, [], null, "multipart/related", null, null, null, null, null, null, false,
            [0]) with
        { Headers = [new("Content-Type"u8.ToArray(), " multipart/related; start=\"<missing-body>\"\r\n"u8.ToArray())] });
        var snapshot = new MailMessageSnapshot(Item, null, null, 6, null, [], parts.Count - 1, parts);
        Assert.AreEqual(expected, GatewayEwsAttachmentCatalog.HasNonInlineAttachments(snapshot));
    }

    [TestMethod]
    public void FileCatalogCountAndMissingPartPositionsAreBounded()
    {
        var raw = Headers + "Content-Type: multipart/mixed; boundary=many\r\n\r\n"
            + string.Concat(Enumerable.Repeat("--many\r\n" + FileHeaders, GatewayEwsAttachmentIdCodec.MaximumAttachments + 1)) + "--many--\r\n";
        Assert.AreEqual("ErrorDataSizeLimitExceeded", Assert.Throws<GatewayEwsRequestException>(() =>
        {
            using var catalog = GatewayEwsAttachmentCatalog.Load(Encoding.ASCII.GetBytes(raw));
            catalog.Metadata(Account, Item);
        }).Code, StringComparer.Ordinal);
        using var valid = GatewayEwsAttachmentCatalog.Load(Encoding.ASCII.GetBytes(Mime));
        Assert.AreEqual("ErrorInvalidAttachmentId", Assert.Throws<GatewayEwsRequestException>(() =>
            valid.Get(Account, Item, Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes(Mime))), 1)).Code, StringComparer.Ordinal);
    }

    [TestMethod]
    public void AttachedMessagesAndUnknownDispositionsAreNotSilentlyFlattened()
    {
        foreach (var raw in new[] { Headers + "Content-Type: message/rfc822\r\nContent-Disposition: attachment\r\n\r\nSubject: Private child\r\n\r\nPrivate child",
            Mime.Replace("Content-Disposition: attachment;", "Content-Disposition: x-private;", StringComparison.Ordinal) })
        {
            Assert.AreEqual("ErrorInvalidPropertyRequest", Assert.Throws<GatewayEwsRequestException>(() =>
            {
                using var catalog = GatewayEwsAttachmentCatalog.Load(Encoding.ASCII.GetBytes(raw));
                catalog.Metadata(Account, Item);
            }).Code, StringComparer.Ordinal);
        }
    }

    private static string Request(string references, string shape = "") =>
        $"<s:Envelope xmlns:s='{GatewayEwsSoap.Soap}' xmlns:m='{GatewayEwsSoap.Messages}' xmlns:t='{GatewayEwsSoap.Types}'><s:Body><m:GetAttachment>{shape}<m:AttachmentIds>{references}</m:AttachmentIds></m:GetAttachment></s:Body></s:Envelope>";
    private static async Task<GatewayEwsRequest> ParseAsync(string xml)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        return await GatewayEwsRequestParser.ReadAsync(stream, CancellationToken.None).ConfigureAwait(false);
    }

    private sealed class Transport(byte[] raw) : IGatewayApplicationTransport
    {
        internal int Calls { get; private set; }
        public Task<TResponse> SendAsync<TRequest, TResponse>(string protocol, string operation, TRequest request, CancellationToken cancellationToken = default)
        {
            Assert.AreEqual("ews", protocol, StringComparer.Ordinal);
            Assert.AreEqual(ApplicationOperations.MailOperationExecute, operation, StringComparer.Ordinal);
            var input = request as MailOperationApplicationRequest;
            Assert.IsNotNull(input);
            Assert.AreEqual(Authentication, input.Authentication);
            Assert.AreEqual(MailOperationKind.ReadMessageContent, input.Command.Operation);
            var command = input.Command.Arguments.Deserialize<MailMessageContentCommand>(JsonSerializerOptions.Web)!;
            Assert.AreEqual(Account, command.AccountId);
            CollectionAssert.AreEqual(ParentIds, command.MessageIds.ToArray());
            Assert.IsFalse(command.IncludeText);
            Calls++;
            var snapshot = new MailMessageSnapshot(Item, null, null, raw.Length, new(Item, Folder, "thread", [], raw.Length, DateTime.UtcNow), [], null, []);
            var result = new MailMessageContentResult(MailMessageReadStatus.Ok, "s1", [new(Item, MailMessageContentStatus.Ok, snapshot, raw)]);
            var reply = new JmapApplicationResult(JmapApplicationOutcomes.Ok, OperationResult: new(new(MailOperationKind.ReadMessageContent,
                ApplicationValueCodec.Encode(JsonSerializer.SerializeToNode(result, JsonSerializerOptions.Web))), new Dictionary<string, string>(StringComparer.Ordinal), Profile));
            return Task.FromResult((TResponse)(object)reply);
        }
    }
}
