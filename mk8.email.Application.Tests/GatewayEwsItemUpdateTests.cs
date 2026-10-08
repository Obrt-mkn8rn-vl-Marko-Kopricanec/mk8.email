using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using mk8.email.Contracts.Messaging;
using mk8.email.Gateway.ApplicationBridge;
using mk8.email.Gateway.Protocols.Ews;

namespace mk8.email.Application.Tests;

[TestClass]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1812", Justification = "MSTest DiscoverInternals executes this strict UpdateItem/one-key mutation fixture; discovered outcomes are retained.")]
internal sealed class GatewayEwsItemUpdateTests
{
    private static readonly Guid Account = new("1138f53b-3efa-4a59-8602-e215c0df1e35");
    private static readonly Guid Item = new("2238f53b-3efa-4a59-8602-e215c0df1e35");
    private static readonly ProtocolAuthentication Authentication = new(ProtocolAuthenticationKinds.Password, "owner@example.test", "fixture-secret");
    private static readonly JmapApplicationProfile Profile = new("owner@example.test",
        new(1024, 1, 65_536, 8, 64, 500, 500, 50, 100, 1024, [], []), [new($"A{Account:N}", "owner@example.test", true, false, true)]);
    private const string Attributes = "ConflictResolution='NeverOverwrite' MessageDisposition='SaveOnly' SuppressReadReceipts='true'";
    private static readonly string[] UnseenKeywords = ["$flagged", "custom"];
    private static readonly string[] SeenKeywords = ["$seen", "$flagged", "custom"];
    private static readonly MailMessageMutationCommand Command = new(Account, "s10", [], [new($"E{Item:N}",
        new([new(MailMessageFlagField.Keywords, MailMessageFlagChangeKind.Set, [new("$seen", MailMessageFlagValue.Enabled)], false)], [], [], 0, false, null))], []);

    [TestMethod]
    [DataRow("true", true)]
    [DataRow("1", true)]
    [DataRow("false", false)]
    [DataRow("0", false)]
    public async Task ParserAdmitsExactlyOneRegisteredReadStateChange(string value, bool read)
    {
        using var body = Body(Change("opaque", value));
        var request = await GatewayEwsRequestParser.ReadAsync(body, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("UpdateItem", request.Operation, StringComparer.Ordinal);
        Assert.IsTrue(request.IsMutation);
        Assert.HasCount(1, request.Items!);
        Assert.HasCount(1, request.ReadStates!);
        Assert.AreEqual(read, request.ReadStates![0]);
    }

    [TestMethod]
    [DataRow("MessageDisposition='SaveOnly' SuppressReadReceipts='true'", "ErrorInvalidRequest")]
    [DataRow("ConflictResolution='AutoResolve' MessageDisposition='SaveOnly' SuppressReadReceipts='true'", "ErrorInvalidRequest")]
    [DataRow("ConflictResolution='AlwaysOverwrite' MessageDisposition='SaveOnly' SuppressReadReceipts='true'", "ErrorInvalidRequest")]
    [DataRow("ConflictResolution='NeverOverwrite' MessageDisposition='SendOnly' SuppressReadReceipts='true'", "ErrorInvalidRequest")]
    [DataRow("ConflictResolution='NeverOverwrite' MessageDisposition='SendAndSaveCopy' SuppressReadReceipts='true'", "ErrorInvalidRequest")]
    [DataRow("ConflictResolution='NeverOverwrite' MessageDisposition='SaveOnly'", "ErrorInvalidRequest")]
    [DataRow("ConflictResolution='NeverOverwrite' MessageDisposition='SaveOnly' SuppressReadReceipts='false'", "ErrorInvalidRequest")]
    [DataRow("ConflictResolution='NeverOverwrite' MessageDisposition='SaveOnly' SuppressReadReceipts='true' SendMeetingInvitationsOrCancellations='SendToNone'", "ErrorSchemaValidation")]
    public async Task UnsupportedConflictSendAndReceiptSemanticsCannotDispatch(string attributes, string code)
    {
        using var body = Body(Change("item", "true"), attributes);
        Assert.AreEqual(code, (await Assert.ThrowsAsync<GatewayEwsRequestException>(() => GatewayEwsRequestParser.ReadAsync(body, CancellationToken.None)).ConfigureAwait(false)).Code, StringComparer.Ordinal);
    }

    [TestMethod]
    [DataRow("true", "item:Subject", "SetItemField", "IsRead", "ErrorInvalidPropertySet")]
    [DataRow("true", "message:IsRead", "AppendToItemField", "IsRead", "ErrorInvalidPropertySet")]
    [DataRow("true", "message:IsRead", "DeleteItemField", "IsRead", "ErrorInvalidPropertySet")]
    [DataRow("true", "message:IsRead", "SetItemField", "IsDraft", "ErrorInvalidPropertySet")]
    [DataRow("True", "message:IsRead", "SetItemField", "IsRead", "ErrorSchemaValidation")]
    [DataRow("", "message:IsRead", "SetItemField", "IsRead", "ErrorSchemaValidation")]
    public async Task InvalidFieldRepresentationsAndBooleansAreRefused(string value, string uri, string action, string field, string code)
    {
        using var body = Body(Change("item", value, uri, action, field));
        Assert.AreEqual(code, (await Assert.ThrowsAsync<GatewayEwsRequestException>(() => GatewayEwsRequestParser.ReadAsync(body, CancellationToken.None)).ConfigureAwait(false)).Code, StringComparer.Ordinal);
    }

    [TestMethod]
    public async Task UpdateCountAndExactItemChangeShapeAreBounded()
    {
        using var oversized = Body(string.Concat(Enumerable.Repeat(Change("item", "true"), 33)));
        Assert.AreEqual("ErrorExceededFindCountLimit", (await Assert.ThrowsAsync<GatewayEwsRequestException>(() => GatewayEwsRequestParser.ReadAsync(oversized, CancellationToken.None)).ConfigureAwait(false)).Code, StringComparer.Ordinal);
        using var malformed = Body(Change("item", "true").Replace("<t:ItemId Id='item'/>", "<t:OccurrenceItemId RecurringMasterId='item' InstanceIndex='1'/>", StringComparison.Ordinal));
        Assert.AreEqual("ErrorSchemaValidation", (await Assert.ThrowsAsync<GatewayEwsRequestException>(() => GatewayEwsRequestParser.ReadAsync(malformed, CancellationToken.None)).ConfigureAwait(false)).Code, StringComparer.Ordinal);
    }

    [TestMethod]
    [DataRow("valid")]
    [DataRow("missing-field")]
    [DataRow("extra-field")]
    [DataRow("null-row")]
    [DataRow("missing-row")]
    [DataRow("extra-row")]
    [DataRow("wrong-reference")]
    [DataRow("wrong-id")]
    [DataRow("empty-id")]
    [DataRow("old-state")]
    [DataRow("new-state")]
    [DataRow("unchanged")]
    [DataRow("status")]
    [DataRow("both")]
    [DataRow("neither")]
    [DataRow("failure-data")]
    [DataRow("known-entity")]
    [DataRow("create")]
    [DataRow("destroy")]
    public void RepliesRequireExactUpdateIdentityStateAndExclusiveOutcomes(string mode)
    {
        var data = (JsonObject)JsonSerializer.SerializeToNode(Success(), JsonSerializerOptions.Web)!;
        CorruptRows(mode, data);
        CorruptState(mode, data);
        var reply = Operation(data);
        if (mode is "known-entity") reply = reply with { KnownEntities = new Dictionary<string, string>(StringComparer.Ordinal) { ["bad"] = "unexpected" } };
        if (mode is "valid") Assert.AreEqual(Item, GatewayEwsItemUpdateReply.Decode(reply, Command, Expected(true)).Updated[0].MessageId);
        else Assert.Throws<InvalidOperationException>(() => GatewayEwsItemUpdateReply.Decode(reply, Command, Expected(true)));
    }

    private static void CorruptRows(string mode, JsonObject data)
    {
        var item = (JsonObject)data["updated"]![0]!;
        if (mode is "missing-field") item.Remove("messageId");
        if (mode is "extra-field") item["other"] = true;
        if (mode is "null-row") data["updated"]![0] = null;
        if (mode is "missing-row") ((JsonArray)data["updated"]!).Clear();
        if (mode is "extra-row") ((JsonArray)data["updated"]!).Add(item.DeepClone());
        if (mode is "wrong-reference") item["requestedId"] = $"E{Account:N}";
        if (mode is "wrong-id") item["messageId"] = Account;
        if (mode is "empty-id") item["messageId"] = Guid.Empty;
        if (mode is "both") item["failure"] = JsonSerializer.SerializeToNode(new MailMessageMutationFailure(MailMessageMutationError.NotFound, null, null, null), JsonSerializerOptions.Web);
        if (mode is "neither") item["messageId"] = null;
    }

    private static void CorruptState(string mode, JsonObject data)
    {
        if (mode is "old-state") data["oldState"] = "other";
        if (mode is "new-state") data["newState"] = "";
        if (mode is "unchanged") data["newState"] = "s10";
        if (mode is "status") data["status"] = 99;
        if (mode is "failure-data") data["status"] = (int)MailMessageMutationStatus.StateMismatch;
        if (mode is "create") ((JsonArray)data["created"]!).Add(new JsonObject());
        if (mode is "destroy") ((JsonArray)data["destroyed"]!).Add(new JsonObject());
    }

    [TestMethod]
    [DataRow(MailMessageMutationStatus.AccountNotFound)]
    [DataRow(MailMessageMutationStatus.StateMismatch)]
    public void WholeRefusalsCannotHaveUpdateData(MailMessageMutationStatus status)
    {
        var result = new MailMessageMutationResult(status, null, null, [], [], []);
        Assert.AreEqual(status, GatewayEwsItemUpdateReply.Decode(Operation(JsonSerializer.SerializeToNode(result, JsonSerializerOptions.Web)!), Command, Expected(true)).Status);
    }

    [TestMethod]
    public void AValidNoOpCanRetainItsOriginalState()
    {
        var data = JsonSerializer.SerializeToNode(Success() with { NewState = "s10" }, JsonSerializerOptions.Web)!;
        var result = GatewayEwsItemUpdateReply.Decode(Operation(data), Command, Expected(false));
        var xml = GatewayEwsItemUpdateResponse.Render(Account, [new(Item, true, null)], result);
        Assert.AreEqual("czEw", (string?)XDocument.Parse(xml).Descendants(GatewayEwsSoap.Types + "ItemId").Single().Attribute("ChangeKey"), StringComparer.Ordinal);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task UpdatesUseOnlyASeenDeltaAndAlwaysGuardTheAdmittedState(bool read)
    {
        var snapshot = new MailMessageSnapshot(Item, null, null, 100, new(Item, Account, "thread", read ? UnseenKeywords : SeenKeywords, 100, DateTime.UtcNow), [], null, []);
        var nativeRead = new MailMessageReadResult(MailMessageReadStatus.Ok, "s10", [new(Item, snapshot)]);
        var transport = new Transport([new(JmapApplicationOutcomes.Ok, OperationResult: Operation(JsonSerializer.SerializeToNode(nativeRead, JsonSerializerOptions.Web)!) with
            { Response = new(MailOperationKind.ReadMessages, ApplicationValueCodec.Encode(JsonSerializer.SerializeToNode(nativeRead, JsonSerializerOptions.Web))) }),
            new(JmapApplicationOutcomes.Ok, OperationResult: Operation(JsonSerializer.SerializeToNode(Success(), JsonSerializerOptions.Web)!))]);
        var xml = await GatewayEwsItemUpdate.ExecuteAsync(new(transport, new()), Authentication, Profile, Account,
            Request([new(GatewayEwsItemIdCodec.Encode(Account, Item), null)], [read]), CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("NoError", XDocument.Parse(xml).Descendants(GatewayEwsSoap.Messages + "ResponseCode").Single().Value, StringComparer.Ordinal);
        var command = transport.Commands[1].Arguments.Deserialize<MailMessageMutationCommand>(JsonSerializerOptions.Web)!;
        Assert.AreEqual(Account, command.AccountId);
        Assert.AreEqual("s10", command.IfInState, StringComparer.Ordinal);
        Assert.IsEmpty(command.Creates);
        Assert.IsEmpty(command.Destroys);
        Assert.HasCount(1, command.Updates);
        var patch = command.Updates[0].Patch;
        Assert.IsFalse(patch.RequiresMime);
        Assert.IsEmpty(patch.Assertions);
        Assert.IsEmpty(patch.PartFields);
        Assert.AreEqual(MailMessageFlagField.Keywords, patch.Flags[0].Field);
        Assert.AreEqual(read ? MailMessageFlagChangeKind.Set : MailMessageFlagChangeKind.Remove, patch.Flags[0].Kind);
        Assert.HasCount(1, patch.Flags[0].Entries);
        Assert.AreEqual("$seen", patch.Flags[0].Entries[0].Key, StringComparer.Ordinal);
    }

    [TestMethod]
    [DataRow(1, 500)]
    [DataRow(500, 1)]
    public async Task BothGetAndSetLimitsPrecedePreflightTransport(int get, int set)
    {
        var transport = new Transport([]);
        var refs = new GatewayEwsItemReference[] { new(GatewayEwsItemIdCodec.Encode(Account, Item), null), new(GatewayEwsItemIdCodec.Encode(Account, Account), null) };
        var profile = Profile with { Limits = Profile.Limits with { MaxObjectsInGet = get, MaxObjectsInSet = set } };
        Assert.AreEqual("ErrorExceededFindCountLimit", (await Assert.ThrowsAsync<GatewayEwsRequestException>(() => GatewayEwsItemUpdate.ExecuteAsync(new(transport, new()), Authentication,
            profile, Account, Request(refs, [true, false]), CancellationToken.None)).ConfigureAwait(false)).Code, StringComparer.Ordinal);
        Assert.IsEmpty(transport.Commands);
    }

    private static Dictionary<string, bool> Expected(bool changed) => new(StringComparer.Ordinal) { [$"E{Item:N}"] = changed };
    [TestMethod]
    [DataRow("null")]
    [DataRow("duplicate")]
    [DataRow("empty")]
    [DataRow("control")]
    public async Task InvalidPreflightKeywordsCannotDispatchAnyMutation(string mode)
    {
        IReadOnlyList<string> keywords = mode switch
        {
            "null" => null!,
            "duplicate" => ["$seen", "$seen"],
            "empty" => [""],
            _ => ["bad\n"],
        };
        var snapshot = new MailMessageSnapshot(Item, null, null, 100, new(Item, Account, "thread", keywords, 100, DateTime.UtcNow), [], null, []);
        var read = new MailMessageReadResult(MailMessageReadStatus.Ok, "s10", [new(Item, snapshot)]);
        var transport = new Transport([new(JmapApplicationOutcomes.Ok, OperationResult: Operation(JsonSerializer.SerializeToNode(read, JsonSerializerOptions.Web)!) with
            { Response = new(MailOperationKind.ReadMessages, ApplicationValueCodec.Encode(JsonSerializer.SerializeToNode(read, JsonSerializerOptions.Web))) })]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => GatewayEwsItemUpdate.ExecuteAsync(new(transport, new()), Authentication, Profile, Account,
            Request([new(GatewayEwsItemIdCodec.Encode(Account, Item), null)], [true]), CancellationToken.None)).ConfigureAwait(false);
        Assert.HasCount(1, transport.Commands);
        Assert.AreEqual(MailOperationKind.ReadMessages, transport.Commands[0].Operation);
    }

    [TestMethod]
    [DataRow(MailMessageMutationError.NotFound, "ErrorItemNotFound")]
    [DataRow(MailMessageMutationError.InvalidProperties, "ErrorInvalidPropertySet")]
    [DataRow(MailMessageMutationError.TooManyKeywords, "ErrorInvalidPropertySet")]
    public void ValidFailureResultsCannotInventUpdatedIdentity(MailMessageMutationError error, string code)
    {
        var value = new MailMessageMutationResult(MailMessageMutationStatus.Ok, "s10", "s10", [],
            [new($"E{Item:N}", null, new(error, null, null, null))], []);
        var result = GatewayEwsItemUpdateReply.Decode(Operation(JsonSerializer.SerializeToNode(value, JsonSerializerOptions.Web)!), Command, Expected(true));
        var document = XDocument.Parse(GatewayEwsItemUpdateResponse.Render(Account, [new(Item, true, null)], result));
        Assert.AreEqual(code, document.Descendants(GatewayEwsSoap.Messages + "ResponseCode").Single().Value, StringComparer.Ordinal);
        Assert.IsFalse(document.Descendants(GatewayEwsSoap.Types + "ItemId").Any());
    }

    private static MailMessageMutationResult Success() => new(MailMessageMutationStatus.Ok, "s10", "s11", [], [new($"E{Item:N}", Item, null)], []);
    private static MailOperationResult Operation(JsonNode value) => new(new(MailOperationKind.MutateMessages, ApplicationValueCodec.Encode(value)), new Dictionary<string, string>(StringComparer.Ordinal), Profile);
    private static GatewayEwsRequest Request(IReadOnlyList<GatewayEwsItemReference> items, IReadOnlyList<bool> reads) => new("UpdateItem", new HashSet<string>(StringComparer.Ordinal), [], false, 0, 0, false, Items: items, ReadStates: reads);
    private static MemoryStream Body(string changes, string attributes = Attributes) => new(Encoding.UTF8.GetBytes(
        $"<s:Envelope xmlns:s='{GatewayEwsSoap.Soap}' xmlns:m='{GatewayEwsSoap.Messages}' xmlns:t='{GatewayEwsSoap.Types}'><s:Body><m:UpdateItem {attributes}><m:ItemChanges>{changes}</m:ItemChanges></m:UpdateItem></s:Body></s:Envelope>"));
    private static string Change(string id, string value, string uri = "message:IsRead", string action = "SetItemField", string field = "IsRead") =>
        $"<t:ItemChange><t:ItemId Id='{id}'/><t:Updates><t:{action}><t:FieldURI FieldURI='{uri}'/><t:Message><t:{field}>{value}</t:{field}></t:Message></t:{action}></t:Updates></t:ItemChange>";

    private sealed class Transport(IEnumerable<JmapApplicationResult> results) : IGatewayApplicationTransport
    {
        private readonly Queue<JmapApplicationResult> _results = new(results);
        public List<MailOperationCommand> Commands { get; } = [];
        public Task<TResponse> SendAsync<TRequest, TResponse>(string protocol, string operation, TRequest request, CancellationToken cancellationToken = default)
        {
            Assert.AreEqual("ews", protocol, StringComparer.Ordinal);
            Assert.AreEqual(ApplicationOperations.MailOperationExecute, operation, StringComparer.Ordinal);
            Commands.Add(((MailOperationApplicationRequest)(object)request!).Command);
            return Task.FromResult((TResponse)(object)_results.Dequeue());
        }
    }
}
