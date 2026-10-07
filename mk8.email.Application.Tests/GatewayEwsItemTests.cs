using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using mk8.email.Configuration;
using mk8.email.Contracts.Messaging;
using mk8.email.Gateway.ApplicationBridge;
using mk8.email.Gateway.Protocols.Ews;

namespace mk8.email.Application.Tests;

[TestClass]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1812", Justification = "MSTest DiscoverInternals executes this parser/typed-mail projection fixture; discovery is retained.")]
internal sealed class GatewayEwsItemTests
{
    private static readonly Guid Account = new("1138f53b-3efa-4a59-8602-e215c0df1e35");
    private static readonly Guid Item = new("2238f53b-3efa-4a59-8602-e215c0df1e35");
    private static readonly Guid Folder = new("1338f53b-3efa-4a59-8602-e215c0df1e35");
    private static readonly string[] ShapeProperties = ["ItemId", "Subject", "IsRead"];
    private static readonly string[] OrderedProperties = ["ItemId", "ParentFolderId", "ItemClass", "Subject", "DateTimeReceived", "Size", "IsDraft", "HasAttachments", "From", "InternetMessageId", "IsRead"];
    private static readonly ProtocolAuthentication Authentication = new(ProtocolAuthenticationKinds.Password, "owner@example.test", "fixture-secret");
    private static readonly JmapApplicationProfile Profile = new("owner@example.test",
        new(1024, 1, 65_536, 8, 64, 500, 500, 50, 100, 1024, [], []), [new($"A{Account:N}", "owner@example.test", true, false, true)]);

    [TestMethod]
    public async Task ReferenceCountIsBoundedBeforeAnyItemRead()
    {
        using var body = new MemoryStream(Encoding.UTF8.GetBytes(Request(string.Concat(Enumerable.Repeat("<t:ItemId Id='opaque'/>", 33)))));
        var error = await Assert.ThrowsAsync<GatewayEwsRequestException>(() => GatewayEwsRequestParser.ReadAsync(body, CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual("ErrorExceededFindCountLimit", error.Code, StringComparer.Ordinal);
    }

    [TestMethod]
    public async Task WorkerObjectLimitIsRespectedBeforeTransportDispatch()
    {
        var transport = new Transport(new(JmapApplicationOutcomes.Unauthorized));
        var client = new GatewayEwsClient(transport, new EnvironmentConfig());
        var profile = Profile with { Limits = Profile.Limits with { MaxObjectsInGet = 1 } };
        var error = await Assert.ThrowsAsync<GatewayEwsRequestException>(() => client.ReadItemsAsync(Authentication, profile, Account, [Item, Folder], CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual("ErrorExceededFindCountLimit", error.Code, StringComparer.Ordinal);
        Assert.IsNull(transport.Command);
    }

    [TestMethod]
    public async Task IdOnlyAndAdditionalPropertiesKeepOrderedRepeatedItemReferences()
    {
        var id = GatewayEwsItemIdCodec.Encode(Account, Item);
        var fields = "<t:AdditionalProperties><t:FieldURI FieldURI='item:Subject'/><t:FieldURI FieldURI='message:IsRead'/></t:AdditionalProperties>";
        using var body = new MemoryStream(Encoding.UTF8.GetBytes(Request($"<t:ItemId Id='{id}'/><t:ItemId Id='{id}' ChangeKey='b2xk'/>", fields)));
        var parsed = await GatewayEwsRequestParser.ReadAsync(body, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("GetItem", parsed.Operation, StringComparer.Ordinal);
        Assert.IsFalse(parsed.IsMutation);
        Assert.IsEmpty(parsed.Folders);
        CollectionAssert.AreEquivalent(ShapeProperties, parsed.Properties.ToArray());
        Assert.AreEqual(2, parsed.Items!.Count);
        Assert.AreEqual("b2xk", parsed.Items[1].ChangeKey, StringComparer.Ordinal);
    }

    [TestMethod]
    [DataRow("Default", "", "ErrorInvalidPropertyRequest")]
    [DataRow("AllProperties", "", "ErrorInvalidPropertyRequest")]
    [DataRow("IdOnly", "<t:IncludeMimeContent>true</t:IncludeMimeContent>", "ErrorInvalidPropertyRequest")]
    [DataRow("IdOnly", "<t:BodyType>Text</t:BodyType>", "ErrorInvalidPropertyRequest")]
    [DataRow("IdOnly", "<t:AdditionalProperties><t:FieldURI FieldURI='item:Body'/></t:AdditionalProperties>", "ErrorInvalidPropertyRequest")]
    [DataRow("IdOnly", "<t:AdditionalProperties><t:ExtendedFieldURI PropertyTag='0x3001' PropertyType='String'/></t:AdditionalProperties>", "ErrorInvalidPropertyRequest")]
    [DataRow("IdOnly", "<t:AdditionalProperties/><t:IncludeMimeContent>false</t:IncludeMimeContent>", "ErrorInvalidPropertyRequest")]
    [DataRow("IdOnly", "<t:IncludeMimeContent>False</t:IncludeMimeContent>", "ErrorSchemaValidation")]
    public async Task UnsupportedShapesAndInvalidOrderNeverBecomeReads(string shape, string fields, string expected)
    {
        using var body = new MemoryStream(Encoding.UTF8.GetBytes(Request("<t:ItemId Id='opaque'/>", fields, shape)));
        var error = await Assert.ThrowsAsync<GatewayEwsRequestException>(() => GatewayEwsRequestParser.ReadAsync(body, CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(expected, error.Code, StringComparer.Ordinal);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("<t:FolderId Id='folder'/>")]
    [DataRow("<t:ItemId Id='opaque'><t:ItemId Id='nested'/></t:ItemId>")]
    [DataRow("<t:RecurringMasterItemId OccurrenceId='item'/>")]
    [DataRow("<t:ItemId Id='opaque' Mailbox='foreign'/>")]
    public async Task UnsupportedReferenceShapesAreRefusedBeforeWorker(string references)
    {
        using var body = new MemoryStream(Encoding.UTF8.GetBytes(Request(references)));
        Assert.AreEqual("ErrorSchemaValidation", (await Assert.ThrowsAsync<GatewayEwsRequestException>(() => GatewayEwsRequestParser.ReadAsync(body, CancellationToken.None)).ConfigureAwait(false)).Code, StringComparer.Ordinal);
    }

    [TestMethod]
    public void CanonicalItemIdsCannotBecomeFolderIdsOrVirtualRootIds()
    {
        var value = GatewayEwsItemIdCodec.Encode(Account, Item);
        Assert.IsTrue(GatewayEwsItemIdCodec.TryDecode(value, out var account, out var item));
        Assert.AreEqual(Account, account);
        Assert.AreEqual(Item, item);
        Assert.IsFalse(GatewayEwsFolderIdCodec.TryDecode(value, out _, out _));
        Assert.IsFalse(GatewayEwsItemIdCodec.TryDecode(GatewayEwsFolderIdCodec.Encode(Account, Folder), out _, out _));
        Assert.IsFalse(GatewayEwsItemIdCodec.TryDecode(GatewayEwsItemIdCodec.Encode(Account, Guid.Empty), out _, out _));
        Assert.IsFalse(GatewayEwsItemIdCodec.TryDecode(value + " ", out _, out _));
    }

    [TestMethod]
    public void MailPropertiesFollowMessageSchemaOrderAndDoNotInventFolderChangeKeys()
    {
        var properties = new HashSet<string>(OrderedProperties, StringComparer.Ordinal);
        var message = GatewayEwsItemResponse.Message(Account, "s10", Snapshot(), properties);
        CollectionAssert.AreEqual(OrderedProperties, message.Elements().Select(field => field.Name.LocalName).ToArray());
        Assert.AreEqual("A <&> B", message.Element(GatewayEwsSoap.Types + "Subject")!.Value, StringComparer.Ordinal);
        Assert.AreEqual("<fixture@example.test>", message.Element(GatewayEwsSoap.Types + "InternetMessageId")!.Value, StringComparer.Ordinal);
        Assert.AreEqual("true", message.Element(GatewayEwsSoap.Types + "IsRead")!.Value, StringComparer.Ordinal);
        Assert.IsNull(message.Element(GatewayEwsSoap.Types + "ParentFolderId")!.Attribute("ChangeKey"));
        Assert.IsTrue(message.ToString().Contains("&lt;&amp;&gt;", StringComparison.Ordinal));
    }

    [TestMethod]
    [DataRow("Subject", "ErrorDataSizeLimitExceeded")]
    [DataRow("From", "ErrorInvalidPropertyRequest")]
    public void UnrepresentableRequestedMetadataIsRefusedWithoutTruncationOrAuthorInvention(string property, string expected)
    {
        var header = property is "Subject" ? new MailMimeHeaderSnapshot("Subject"u8.ToArray(), Encoding.ASCII.GetBytes(" " + new string('A', 256) + "\r\n"))
            : new("From"u8.ToArray(), " One <one@example.test>, Two <two@example.test>\r\n"u8.ToArray());
        var snapshot = Snapshot() with { Headers = [header] };
        var error = Assert.Throws<GatewayEwsRequestException>(() => GatewayEwsItemResponse.Message(Account, "s10", snapshot,
            new HashSet<string>(["ItemId", property], StringComparer.Ordinal)));
        Assert.AreEqual(expected, error.Code, StringComparer.Ordinal);
    }

    [TestMethod]
    [DataRow("valid")]
    [DataRow("duplicate")]
    [DataRow("unexpected")]
    [DataRow("source")]
    [DataRow("stored")]
    [DataRow("state")]
    [DataRow("failure-with-data")]
    [DataRow("known-entity")]
    public async Task TypedItemRepliesRequireCorrelatedAccountReadOutcomes(string mode)
    {
        var snapshot = Snapshot();
        if (mode is "source") snapshot = snapshot with { ContentSourceId = Folder };
        if (mode is "stored") snapshot = snapshot with { Stored = null };
        var item = new MailMessageProjectedItem(mode is "unexpected" ? Folder : Item, snapshot);
        var value = new MailMessageReadResult(mode is "failure-with-data" ? MailMessageReadStatus.AccountNotFound : MailMessageReadStatus.Ok,
            mode is "state" ? null : "s10", mode is "duplicate" ? [item, item] : [item]);
        var reply = new MailOperationResult(new(MailOperationKind.ReadMessages, ApplicationValueCodec.Encode(JsonSerializer.SerializeToNode(value, JsonSerializerOptions.Web))),
            mode is "known-entity" ? new Dictionary<string, string>(StringComparer.Ordinal) { ["opaque"] = "unexpected" } : new Dictionary<string, string>(StringComparer.Ordinal), Profile);
        var transport = new Transport(new(JmapApplicationOutcomes.Ok, OperationResult: reply));
        var client = new GatewayEwsClient(transport, new EnvironmentConfig());
        if (mode is "valid") Assert.AreEqual(Item, (await client.ReadItemsAsync(Authentication, Profile, Account, [Item], CancellationToken.None).ConfigureAwait(false)).Messages.Single().MessageId);
        else await Assert.ThrowsAsync<InvalidOperationException>(() => client.ReadItemsAsync(Authentication, Profile, Account, [Item], CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(Account, transport.Command!.AccountId);
        Assert.IsFalse(transport.Command.IncludeText);
    }

    private static string Request(string references, string fields = "", string shape = "IdOnly") =>
        $"<s:Envelope xmlns:s='{GatewayEwsSoap.Soap}' xmlns:m='{GatewayEwsSoap.Messages}' xmlns:t='{GatewayEwsSoap.Types}'><s:Body><m:GetItem><m:ItemShape><t:BaseShape>{shape}</t:BaseShape>{fields}</m:ItemShape><m:ItemIds>{references}</m:ItemIds></m:GetItem></s:Body></s:Envelope>";

    private static MailMessageSnapshot Snapshot() => new(Item, null, null, 123,
        new(Item, Folder, "thread", ["$seen"], 123, new DateTime(2026, 10, 7, 0, 0, 0, DateTimeKind.Utc)),
        [new("Subject"u8.ToArray(), " A <&> B\r\n"u8.ToArray()), new("Message-ID"u8.ToArray(), " <fixture@example.test>\r\n"u8.ToArray()),
            new("From"u8.ToArray(), " Sender <sender@example.test>\r\n"u8.ToArray())], null, []);

    private sealed class Transport(JmapApplicationResult result) : IGatewayApplicationTransport
    {
        internal MailMessageReadCommand? Command { get; private set; }
        public Task<TResponse> SendAsync<TRequest, TResponse>(string protocol, string operation, TRequest request, CancellationToken cancellationToken = default)
        {
            Assert.AreEqual("ews", protocol, StringComparer.Ordinal);
            Assert.AreEqual(ApplicationOperations.MailOperationExecute, operation, StringComparer.Ordinal);
            var input = request as MailOperationApplicationRequest;
            Assert.IsNotNull(input);
            Assert.AreEqual(Authentication, input.Authentication);
            Assert.AreEqual(MailOperationKind.ReadMessages, input.Command.Operation);
            Command = input.Command.Arguments.Deserialize<MailMessageReadCommand>(JsonSerializerOptions.Web);
            return Task.FromResult((TResponse)(object)result);
        }
    }
}
