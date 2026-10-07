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
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1812", Justification = "MSTest DiscoverInternals executes this strict DeleteItem parser/typed-reply fixture; discovery is retained.")]
internal sealed class GatewayEwsItemDeleteTests
{
    private static readonly Guid Account = new("1138f53b-3efa-4a59-8602-e215c0df1e35");
    private static readonly Guid Item = new("2238f53b-3efa-4a59-8602-e215c0df1e35");
    private static readonly ProtocolAuthentication Authentication = new(ProtocolAuthenticationKinds.Password, "owner@example.test", "fixture-secret");
    private static readonly JmapApplicationProfile Profile = new("owner@example.test",
        new(1024, 1, 65_536, 8, 64, 500, 500, 50, 100, 1024, [], []), [new($"A{Account:N}", "owner@example.test", true, false, true)]);
    private static readonly MailMessageMutationCommand Command = new(Account, "s10", [], [], [new($"E{Item:N}")]);
    private static readonly string[] MissingPrimaryCodes = ["ErrorItemNotFound", "ErrorInvalidIdMalformed"];
    private static readonly string[] SuccessCodes = ["NoError"];

    [TestMethod]
    [DataRow("true")]
    [DataRow("1")]
    public async Task DeleteParserRetainsRegisteredOrdinaryItemReferences(string suppress)
    {
        using var stream = Body("<t:ItemId Id='opaque' ChangeKey='czEw'/><t:ItemId Id='opaque'/>", $"DeleteType='HardDelete' SuppressReadReceipts='{suppress}'");
        var request = await GatewayEwsRequestParser.ReadAsync(stream, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("DeleteItem", request.Operation, StringComparer.Ordinal);
        Assert.IsTrue(request.IsMutation);
        Assert.HasCount(2, request.Items!);
        Assert.AreEqual("czEw", request.Items![0].ChangeKey, StringComparer.Ordinal);
        Assert.IsEmpty(request.Properties);
        Assert.IsEmpty(request.Folders);
    }

    [TestMethod]
    [DataRow("DeleteType='SoftDelete' SuppressReadReceipts='true'", "<t:ItemId Id='x'/>", "ErrorInvalidRequest")]
    [DataRow("DeleteType='MoveToDeletedItems' SuppressReadReceipts='true'", "<t:ItemId Id='x'/>", "ErrorInvalidRequest")]
    [DataRow("SuppressReadReceipts='true'", "<t:ItemId Id='x'/>", "ErrorInvalidRequest")]
    [DataRow("DeleteType='HardDelete'", "<t:ItemId Id='x'/>", "ErrorInvalidRequest")]
    [DataRow("DeleteType='HardDelete' SuppressReadReceipts='false'", "<t:ItemId Id='x'/>", "ErrorInvalidRequest")]
    [DataRow("DeleteType='HardDelete' SuppressReadReceipts='True'", "<t:ItemId Id='x'/>", "ErrorInvalidRequest")]
    [DataRow("DeleteType='HardDelete' SuppressReadReceipts='true' SendMeetingCancellations='SendToNone'", "<t:ItemId Id='x'/>", "ErrorSchemaValidation")]
    [DataRow("DeleteType='HardDelete' SuppressReadReceipts='true' AffectedTaskOccurrences='AllOccurrences'", "<t:ItemId Id='x'/>", "ErrorSchemaValidation")]
    [DataRow("DeleteType='HardDelete' SuppressReadReceipts='true'", "", "ErrorSchemaValidation")]
    [DataRow("DeleteType='HardDelete' SuppressReadReceipts='true'", "<t:OccurrenceItemId RecurringMasterId='x' InstanceIndex='1'/>", "ErrorSchemaValidation")]
    [DataRow("DeleteType='HardDelete' SuppressReadReceipts='true'", "<t:ItemId Id='x' Mailbox='foreign'/>", "ErrorSchemaValidation")]
    [DataRow("DeleteType='HardDelete' SuppressReadReceipts='true'", "<t:FolderId Id='x'/>", "ErrorSchemaValidation")]
    public async Task UnsupportedDeletionSemanticsAndShapesAreExplicitlyRefused(string attributes, string references, string code)
    {
        using var stream = Body(references, attributes);
        var error = await Assert.ThrowsAsync<GatewayEwsRequestException>(() => GatewayEwsRequestParser.ReadAsync(stream, CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(code, error.Code, StringComparer.Ordinal);
    }

    [TestMethod]
    public async Task DeleteReferenceCountIsBoundedBeforeDispatch()
    {
        using var stream = Body(string.Concat(Enumerable.Repeat("<t:ItemId Id='opaque'/>", 33)));
        Assert.AreEqual("ErrorExceededFindCountLimit", (await Assert.ThrowsAsync<GatewayEwsRequestException>(() =>
            GatewayEwsRequestParser.ReadAsync(stream, CancellationToken.None)).ConfigureAwait(false)).Code, StringComparer.Ordinal);
    }

    [TestMethod]
    [DataRow("valid")]
    [DataRow("missing-field")]
    [DataRow("unknown-field")]
    [DataRow("null-row")]
    [DataRow("wrong-reference")]
    [DataRow("wrong-id")]
    [DataRow("empty-id")]
    [DataRow("duplicate")]
    [DataRow("missing-row")]
    [DataRow("wrong-old-state")]
    [DataRow("empty-new-state")]
    [DataRow("unchanged-state")]
    [DataRow("invalid-status")]
    [DataRow("both-outcomes")]
    [DataRow("neither-outcome")]
    [DataRow("invalid-failure")]
    [DataRow("failure-with-state")]
    [DataRow("known-entity")]
    [DataRow("create")]
    public void DeleteRepliesRequireExactCorrelatedOutcomes(string mode)
    {
        var data = (JsonObject)JsonSerializer.SerializeToNode(Success(), JsonSerializerOptions.Web)!;
        var item = (JsonObject)data["destroyed"]![0]!;
        Corrupt(mode, data, item);
        var reply = new MailOperationResult(new(MailOperationKind.MutateMessages, ApplicationValueCodec.Encode(data)),
            mode is "known-entity" ? new Dictionary<string, string>(StringComparer.Ordinal) { ["x"] = "other" } : new Dictionary<string, string>(StringComparer.Ordinal), Profile);
        if (mode is "valid") Assert.AreEqual(Item, GatewayEwsItemDeleteReply.Decode(reply, Command).Destroyed[0].MessageId);
        else Assert.Throws<InvalidOperationException>(() => GatewayEwsItemDeleteReply.Decode(reply, Command));
    }

    private static void Corrupt(string mode, JsonObject data, JsonObject item)
    {
        if (mode is "missing-field") item.Remove("messageId");
        if (mode is "unknown-field") item["other"] = "unexpected";
        if (mode is "null-row") data["destroyed"]![0] = null;
        if (mode is "wrong-reference") item["requestedId"] = $"E{Account:N}";
        if (mode is "wrong-id") item["messageId"] = Account;
        if (mode is "empty-id") item["messageId"] = Guid.Empty;
        if (mode is "duplicate") ((JsonArray)data["destroyed"]!).Add(item.DeepClone());
        if (mode is "missing-row") ((JsonArray)data["destroyed"]!).Clear();
        if (mode is "wrong-old-state") data["oldState"] = "other";
        if (mode is "empty-new-state") data["newState"] = "";
        if (mode is "unchanged-state") data["newState"] = "s10";
        if (mode is "invalid-status") data["status"] = 99;
        if (mode is "both-outcomes" or "invalid-failure")
            item["failure"] = JsonSerializer.SerializeToNode(new MailMessageMutationFailure(
                mode is "invalid-failure" ? MailMessageMutationError.InvalidProperties : MailMessageMutationError.NotFound, null, null, null), JsonSerializerOptions.Web);
        if (mode is "invalid-failure" or "neither-outcome") item["messageId"] = null;
        if (mode is "failure-with-state") data["status"] = (int)MailMessageMutationStatus.StateMismatch;
        if (mode is "create") ((JsonArray)data["created"]!).Add(new JsonObject());
    }

    [TestMethod]
    [DataRow(MailMessageMutationStatus.AccountNotFound)]
    [DataRow(MailMessageMutationStatus.StateMismatch)]
    public void WholeOperationRefusalsCannotCarryMutationData(MailMessageMutationStatus status)
    {
        var value = new MailMessageMutationResult(status, null, null, [], [], []);
        Assert.AreEqual(status, GatewayEwsItemDeleteReply.Decode(Operation(value), Command).Status);
    }

    [TestMethod]
    [DataRow(1, 500)]
    [DataRow(500, 1)]
    public async Task BothWorkerReadAndWriteLimitsAreAppliedBeforeTransport(int get, int set)
    {
        var transport = new Transport([]);
        var request = Request([new(GatewayEwsItemIdCodec.Encode(Account, Item), null), new(GatewayEwsItemIdCodec.Encode(Account, Account), null)]);
        var profile = Profile with { Limits = Profile.Limits with { MaxObjectsInGet = get, MaxObjectsInSet = set } };
        var error = await Assert.ThrowsAsync<GatewayEwsRequestException>(() => GatewayEwsItemDelete.ExecuteAsync(
            new(transport, new()), Authentication, profile, Account, request, CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual("ErrorExceededFindCountLimit", error.Code, StringComparer.Ordinal);
        Assert.IsEmpty(transport.Commands);
    }

    [TestMethod]
    public async Task MissingPrimaryAndMalformedKeysNeverAcquireMutationAuthority()
    {
        var transport = new Transport([]);
        var references = new GatewayEwsItemReference[] { new(GatewayEwsItemIdCodec.Encode(Account, Item), null), new("bad", null) };
        var xml = await GatewayEwsItemDelete.ExecuteAsync(new(transport, new()), Authentication, Profile, Guid.Empty,
            Request(references), CancellationToken.None).ConfigureAwait(false);
        CollectionAssert.AreEqual(MissingPrimaryCodes, Codes(xml));
        Assert.IsEmpty(transport.Commands);
    }

    [TestMethod]
    public async Task DeleteAlwaysSendsAdmittedStateWithoutTextOrCreationAuthority()
    {
        var snapshot = new MailMessageSnapshot(Item, null, null, 100,
            new(Item, Account, "thread", [], 100, DateTime.UtcNow), [], null, []);
        var read = new MailMessageReadResult(MailMessageReadStatus.Ok, "s10", [new(Item, snapshot)]);
        var transport = new Transport([Reply(MailOperationKind.ReadMessages, read), new(JmapApplicationOutcomes.Ok, OperationResult: Operation(Success()))]);
        var xml = await GatewayEwsItemDelete.ExecuteAsync(new(transport, new()), Authentication, Profile, Account,
            Request([new(GatewayEwsItemIdCodec.Encode(Account, Item), null)]), CancellationToken.None).ConfigureAwait(false);
        CollectionAssert.AreEqual(SuccessCodes, Codes(xml));
        var first = transport.Commands[0].Arguments.Deserialize<MailMessageReadCommand>(JsonSerializerOptions.Web)!;
        Assert.IsFalse(first.IncludeText);
        var mutation = transport.Commands[1].Arguments.Deserialize<MailMessageMutationCommand>(JsonSerializerOptions.Web)!;
        Assert.AreEqual(Account, mutation.AccountId);
        Assert.AreEqual("s10", mutation.IfInState, StringComparer.Ordinal);
        Assert.IsEmpty(mutation.Creates);
        Assert.IsEmpty(mutation.Updates);
        Assert.AreEqual($"E{Item:N}", mutation.Destroys.Single().RequestedId, StringComparer.Ordinal);
        Assert.IsFalse(XDocument.Parse(xml).Descendants(GatewayEwsSoap.Messages + "Items").Any());
    }

    private static MailMessageMutationResult Success() => new(MailMessageMutationStatus.Ok, "s10", "s11", [], [], [new($"E{Item:N}", Item, null)]);
    private static MailOperationResult Operation<T>(T value) => new(new(MailOperationKind.MutateMessages,
        ApplicationValueCodec.Encode(JsonSerializer.SerializeToNode(value, JsonSerializerOptions.Web))), new Dictionary<string, string>(StringComparer.Ordinal), Profile);
    private static JmapApplicationResult Reply<T>(MailOperationKind kind, T value) => new(JmapApplicationOutcomes.Ok,
        OperationResult: Operation(value) with { Response = new(kind, ApplicationValueCodec.Encode(JsonSerializer.SerializeToNode(value, JsonSerializerOptions.Web))) });
    private static GatewayEwsRequest Request(IReadOnlyList<GatewayEwsItemReference> items) => new("DeleteItem", new HashSet<string>(StringComparer.Ordinal), [], false, 0, 0, false, Items: items);
    private static string[] Codes(string xml) => XDocument.Parse(xml).Descendants(GatewayEwsSoap.Messages + "ResponseCode").Select(code => code.Value).ToArray();
    private static MemoryStream Body(string refs, string attributes = "DeleteType='HardDelete' SuppressReadReceipts='true'") => new(Encoding.UTF8.GetBytes(
        $"<s:Envelope xmlns:s='{GatewayEwsSoap.Soap}' xmlns:m='{GatewayEwsSoap.Messages}' xmlns:t='{GatewayEwsSoap.Types}'><s:Body><m:DeleteItem {attributes}><m:ItemIds>{refs}</m:ItemIds></m:DeleteItem></s:Body></s:Envelope>"));

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
