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
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1812", Justification = "MSTest DiscoverInternals executes this strict CopyItem/typed-creation fixture; discovered outcomes are retained.")]
internal sealed class GatewayEwsItemCopyTests
{
    private static readonly Guid Account = new("1138f53b-3efa-4a59-8602-e215c0df1e35");
    private static readonly Guid Source = new("2238f53b-3efa-4a59-8602-e215c0df1e35");
    private static readonly Guid Target = new("1538f53b-3efa-4a59-8602-e215c0df1e35");
    private static readonly Guid Created = new("2438f53b-3efa-4a59-8602-e215c0df1e35");
    private static readonly ProtocolAuthentication Authentication = new(ProtocolAuthenticationKinds.Password, "owner@example.test", "fixture-secret");
    private static readonly JmapApplicationProfile Profile = new("owner@example.test",
        new(1024, 1, 65_536, 8, 64, 500, 500, 50, 100, 1024, [], []), [new($"A{Account:N}", "owner@example.test", true, false, true)]);
    private static readonly MailCopyCommand Command = new(Account, Account, "s10", "s10", false, null,
        [new("ewsCopy0", Source, false, Target, MailMessageMailboxIssue.None, null, MailMessageKeywordIssue.None, null, false)]);

    [TestMethod]
    [DataRow("", true)]
    [DataRow("<m:ReturnNewItemIds>true</m:ReturnNewItemIds>", true)]
    [DataRow("<m:ReturnNewItemIds>1</m:ReturnNewItemIds>", true)]
    [DataRow("<m:ReturnNewItemIds>false</m:ReturnNewItemIds>", false)]
    [DataRow("<m:ReturnNewItemIds>0</m:ReturnNewItemIds>", false)]
    public async Task CopyParserAdmitsRegisteredOrderingAndReturnIdentitySemantics(string option, bool returns)
    {
        using var body = Body("<t:FolderId Id='folder'/>", "<t:ItemId Id='item' ChangeKey='czEw'/><t:ItemId Id='item'/>", option);
        var request = await GatewayEwsRequestParser.ReadAsync(body, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("CopyItem", request.Operation, StringComparer.Ordinal);
        Assert.IsTrue(request.IsMutation);
        Assert.AreEqual(returns, request.ReturnNewItemIds);
        Assert.HasCount(1, request.Folders);
        Assert.HasCount(2, request.Items!);
        Assert.AreEqual("czEw", request.Items![0].ChangeKey, StringComparer.Ordinal);
    }

    [TestMethod]
    [DataRow("<t:DistinguishedFolderId Id='inbox'/>", "<t:ItemId Id='item'/>", "", "ErrorInvalidRequest")]
    [DataRow("<t:FolderId Id='folder' ChangeKey='czEw'/>", "<t:ItemId Id='item'/>", "", "ErrorInvalidRequest")]
    [DataRow("", "<t:ItemId Id='item'/>", "", "ErrorSchemaValidation")]
    [DataRow("<t:FolderId Id='a'/><t:FolderId Id='b'/>", "<t:ItemId Id='item'/>", "", "ErrorSchemaValidation")]
    [DataRow("<t:FolderId Id='folder'/>", "", "", "ErrorSchemaValidation")]
    [DataRow("<t:FolderId Id='folder'/>", "<t:OccurrenceItemId RecurringMasterId='item' InstanceIndex='1'/>", "", "ErrorSchemaValidation")]
    [DataRow("<t:FolderId Id='folder' Other='opaque'/>", "<t:ItemId Id='item'/>", "", "ErrorSchemaValidation")]
    [DataRow("<t:FolderId Id='folder'/>", "<t:ItemId Id='item'/>", "<m:ReturnNewItemIds>True</m:ReturnNewItemIds>", "ErrorSchemaValidation")]
    [DataRow("<t:FolderId Id='folder'/>", "<t:ItemId Id='item'/>", "<m:ReturnNewItemIds/>", "ErrorSchemaValidation")]
    [DataRow("<t:FolderId Id='folder'/>", "<t:ItemId Id='item'/>", "<t:ReturnNewItemIds>true</t:ReturnNewItemIds>", "ErrorSchemaValidation")]
    [DataRow("<t:FolderId Id='folder'/>", "<t:ItemId Id='item' Mailbox='foreign'/>", "", "ErrorSchemaValidation")]
    public async Task UnsupportedDestinationAndItemShapesCannotBecomeCopies(string target, string refs, string option, string code)
    {
        using var body = Body(target, refs, option);
        var error = await Assert.ThrowsAsync<GatewayEwsRequestException>(() => GatewayEwsRequestParser.ReadAsync(body, CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(code, error.Code, StringComparer.Ordinal);
    }

    [TestMethod]
    public async Task CopyReferenceCountAndElementOrderAreBoundedBeforeDispatch()
    {
        using var body = Body("<t:FolderId Id='target'/>", string.Concat(Enumerable.Repeat("<t:ItemId Id='item'/>", 33)));
        Assert.AreEqual("ErrorExceededFindCountLimit", (await Assert.ThrowsAsync<GatewayEwsRequestException>(() =>
            GatewayEwsRequestParser.ReadAsync(body, CancellationToken.None)).ConfigureAwait(false)).Code, StringComparer.Ordinal);
        using var reversed = new MemoryStream(Encoding.UTF8.GetBytes(Envelope("<m:ItemIds/><m:ToFolderId/>")));
        Assert.AreEqual("ErrorSchemaValidation", (await Assert.ThrowsAsync<GatewayEwsRequestException>(() =>
            GatewayEwsRequestParser.ReadAsync(reversed, CancellationToken.None)).ConfigureAwait(false)).Code, StringComparer.Ordinal);
    }

    [TestMethod]
    [DataRow("valid")]
    [DataRow("missing-field")]
    [DataRow("unknown-field")]
    [DataRow("null-row")]
    [DataRow("wrong-token")]
    [DataRow("duplicate-row")]
    [DataRow("missing-row")]
    [DataRow("empty-id")]
    [DataRow("source-id")]
    [DataRow("wrong-size")]
    [DataRow("missing-thread")]
    [DataRow("unchanged-state")]
    [DataRow("wrong-old-state")]
    [DataRow("invalid-status")]
    [DataRow("failure-with-data")]
    [DataRow("missing-map")]
    [DataRow("wrong-map")]
    [DataRow("extra-map")]
    [DataRow("destroy")]
    public void CopyRepliesRequireFreshCorrelatedCreationIdentitiesAndExactShape(string mode)
    {
        var data = (JsonObject)JsonSerializer.SerializeToNode(Success(), JsonSerializerOptions.Web)!;
        var item = (JsonObject)data["items"]![0]!;
        Corrupt(mode, data, item);
        var known = new Dictionary<string, string>(StringComparer.Ordinal);
        if (mode is not "missing-map") known.Add("ewsCopy0", $"E{(mode is "wrong-map" ? Source : Created):N}");
        if (mode is "extra-map") known.Add("other", $"E{Created:N}");
        var reply = Operation(data, known);
        if (mode is "valid") Assert.AreEqual(Created, GatewayEwsItemCopyReply.Decode(reply, Command, Sources()).Items[0].EmailId);
        else Assert.Throws<InvalidOperationException>(() => GatewayEwsItemCopyReply.Decode(reply, Command, Sources()));
    }

    private static void Corrupt(string mode, JsonObject data, JsonObject item)
    {
        if (mode is "missing-field") item.Remove("size");
        if (mode is "unknown-field") item["other"] = "unexpected";
        if (mode is "null-row") data["items"]![0] = null;
        if (mode is "wrong-token") item["creationId"] = "other";
        if (mode is "duplicate-row") ((JsonArray)data["items"]!).Add(item.DeepClone());
        if (mode is "missing-row") ((JsonArray)data["items"]!).Clear();
        if (mode is "empty-id") item["emailId"] = Guid.Empty;
        if (mode is "source-id") item["emailId"] = Source;
        if (mode is "wrong-size") item["size"] = 101;
        if (mode is "missing-thread") item["storedThreadId"] = null;
        if (mode is "unchanged-state") data["newTargetState"] = "s10";
        if (mode is "wrong-old-state") data["oldTargetState"] = "other";
        if (mode is "invalid-status") data["status"] = 99;
        if (mode is "failure-with-data") item["error"] = (int)MailCopyItemError.NotFound;
        if (mode is "destroy") data["destroy"] = new JsonObject();
    }

    [TestMethod]
    [DataRow(MailCopyStatus.SourceAccountNotFound)]
    [DataRow(MailCopyStatus.TargetAccountNotFound)]
    [DataRow(MailCopyStatus.StateMismatch)]
    [DataRow(MailCopyStatus.RequestTooLarge)]
    public void WholeCopyRefusalsCannotCarryStateOrCreatedEntities(MailCopyStatus status)
    {
        var data = JsonSerializer.SerializeToNode(new MailCopyResult(status, null, null, [], null), JsonSerializerOptions.Web)!;
        Assert.AreEqual(status, GatewayEwsItemCopyReply.Decode(Operation(data, new Dictionary<string, string>(StringComparer.Ordinal)), Command, Sources()).Status);
    }

    [TestMethod]
    [DataRow(MailCopyItemError.NotFound, "ErrorItemNotFound")]
    [DataRow(MailCopyItemError.InvalidMailbox, "ErrorFolderNotFound")]
    [DataRow(MailCopyItemError.TooLarge, "ErrorDataSizeLimitExceeded")]
    [DataRow(MailCopyItemError.OverQuota, "ErrorQuotaExceeded")]
    [DataRow(MailCopyItemError.InvalidEmail, "ErrorInvalidPropertyRequest")]
    public void ValidPerItemFailuresCannotInventCreatedIdentity(MailCopyItemError error, string code)
    {
        var value = new MailCopyResult(MailCopyStatus.Ok, "s10", "s10", [new("ewsCopy0", error, null, null, null)], null);
        var data = JsonSerializer.SerializeToNode(value, JsonSerializerOptions.Web)!;
        var result = GatewayEwsItemCopyReply.Decode(Operation(data, new Dictionary<string, string>(StringComparer.Ordinal)), Command, Sources());
        var xml = GatewayEwsItemCopyResponse.Render(Request([]), Account, [new(Source, "ewsCopy0", null)], result);
        Assert.AreEqual(code, XDocument.Parse(xml).Descendants(GatewayEwsSoap.Messages + "ResponseCode").Single().Value, StringComparer.Ordinal);
        Assert.IsFalse(XDocument.Parse(xml).Descendants(GatewayEwsSoap.Types + "ItemId").Any());
    }

    [TestMethod]
    public void MultipleSuccessesCannotReuseOneNativeIdentity()
    {
        var second = Command.Items[0] with { CreationId = "ewsCopy1" };
        var command = Command with { Items = [Command.Items[0], second] };
        var result = Success() with { Items = [Success().Items[0], Success().Items[0] with { CreationId = "ewsCopy1" }] };
        var known = new Dictionary<string, string>(StringComparer.Ordinal) { ["ewsCopy0"] = $"E{Created:N}", ["ewsCopy1"] = $"E{Created:N}" };
        Assert.Throws<InvalidOperationException>(() => GatewayEwsItemCopyReply.Decode(
            Operation(JsonSerializer.SerializeToNode(result, JsonSerializerOptions.Web)!, known), command, Sources()));
    }

    [TestMethod]
    [DataRow("root", "ErrorAccessDenied")]
    [DataRow("foreign", "ErrorAccessDenied")]
    [DataRow("malformed", "ErrorInvalidIdMalformed")]
    public async Task RefusedDestinationNeverUsesTransportAuthority(string kind, string code)
    {
        var transport = new Transport([]);
        var id = kind is "root" ? GatewayEwsFolderIdCodec.Encode(Account, Guid.Empty)
            : kind is "foreign" ? GatewayEwsFolderIdCodec.Encode(Created, Target) : "malformed";
        var request = Request([new(GatewayEwsItemIdCodec.Encode(Account, Source), null)]) with { Folders = [new(id, false, null, null)] };
        var xml = await GatewayEwsItemCopy.ExecuteAsync(new(transport, new()), Authentication, Profile, Account, request, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(code, XDocument.Parse(xml).Descendants(GatewayEwsSoap.Messages + "ResponseCode").Single().Value, StringComparer.Ordinal);
        Assert.IsEmpty(transport.Commands);
    }

    [TestMethod]
    [DataRow(1, 500)]
    [DataRow(500, 1)]
    public async Task CopyGetAndSetLimitsApplyToDistinctReadsAndEveryCreation(int get, int set)
    {
        var transport = new Transport([]);
        var ids = new[] { Source, get == 1 ? Created : Source };
        var request = Request(ids.Select(id => new GatewayEwsItemReference(GatewayEwsItemIdCodec.Encode(Account, id), null)).ToArray());
        var profile = Profile with { Limits = Profile.Limits with { MaxObjectsInGet = get, MaxObjectsInSet = set } };
        var error = await Assert.ThrowsAsync<GatewayEwsRequestException>(() => GatewayEwsItemCopy.ExecuteAsync(
            new(transport, new()), Authentication, profile, Account, request, CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual("ErrorExceededFindCountLimit", error.Code, StringComparer.Ordinal);
        Assert.IsEmpty(transport.Commands);
    }

    [TestMethod]
    public async Task CopyUsesSameAccountStateAndNeverAuthorizesSourceDeletionOrDataReplacement()
    {
        var read = new MailMessageReadResult(MailMessageReadStatus.Ok, "s10", [new(Source, Sources()[Source])]);
        var copy = JsonSerializer.SerializeToNode(Success(), JsonSerializerOptions.Web)!;
        var transport = new Transport([Reply(MailOperationKind.ReadMessages, read), new(JmapApplicationOutcomes.Ok,
            OperationResult: Operation(copy, new Dictionary<string, string>(StringComparer.Ordinal) { ["ewsCopy0"] = $"E{Created:N}" }))]);
        var xml = await GatewayEwsItemCopy.ExecuteAsync(new(transport, new()), Authentication, Profile, Account,
            Request([new(GatewayEwsItemIdCodec.Encode(Account, Source), null)]), CancellationToken.None).ConfigureAwait(false);
        var id = XDocument.Parse(xml).Descendants(GatewayEwsSoap.Types + "ItemId").Single();
        Assert.AreEqual(GatewayEwsItemIdCodec.Encode(Account, Created), (string?)id.Attribute("Id"), StringComparer.Ordinal);
        Assert.IsFalse(transport.Commands[0].Arguments.Deserialize<MailMessageReadCommand>(JsonSerializerOptions.Web)!.IncludeText);
        var command = transport.Commands[1].Arguments.Deserialize<MailCopyCommand>(JsonSerializerOptions.Web)!;
        Assert.AreEqual(Account, command.SourceAccountId);
        Assert.AreEqual(Account, command.TargetAccountId);
        Assert.AreEqual("s10", command.IfFromInState, StringComparer.Ordinal);
        Assert.AreEqual("s10", command.IfInState, StringComparer.Ordinal);
        Assert.IsFalse(command.DestroyOriginal);
        Assert.IsNull(command.DestroyFromIfInState);
        Assert.IsNull(command.Items[0].Keywords);
        Assert.IsNull(command.Items[0].ReceivedAt);
        Assert.AreEqual(Target, command.Items[0].MailboxId);
    }

    private static MailCopyResult Success() => new(MailCopyStatus.Ok, "s10", "s11", [new("ewsCopy0", MailCopyItemError.None, Created, "thread", 100)], null);
    private static Dictionary<Guid, MailMessageSnapshot> Sources() => new() { [Source] = new(Source, null, null, 100, new(Source, Target, "thread", [], 100, DateTime.UtcNow), [], null, []) };
    private static MailOperationResult Operation(JsonNode data, IReadOnlyDictionary<string, string> known) => new(new(MailOperationKind.CopyMessages, ApplicationValueCodec.Encode(data)), known, Profile);
    private static JmapApplicationResult Reply<T>(MailOperationKind operation, T value) => new(JmapApplicationOutcomes.Ok,
        OperationResult: new(new(operation, ApplicationValueCodec.Encode(JsonSerializer.SerializeToNode(value, JsonSerializerOptions.Web))), new Dictionary<string, string>(StringComparer.Ordinal), Profile));
    private static GatewayEwsRequest Request(IReadOnlyList<GatewayEwsItemReference> items) => new("CopyItem", new HashSet<string>(StringComparer.Ordinal),
        [new(GatewayEwsFolderIdCodec.Encode(Account, Target), false, null, null)], false, 0, 0, false, Items: items);
    private static MemoryStream Body(string target, string refs, string option = "") => new(Encoding.UTF8.GetBytes(Envelope(
        $"<m:ToFolderId>{target}</m:ToFolderId><m:ItemIds>{refs}</m:ItemIds>{option}")));
    private static string Envelope(string fields) => $"<s:Envelope xmlns:s='{GatewayEwsSoap.Soap}' xmlns:m='{GatewayEwsSoap.Messages}' xmlns:t='{GatewayEwsSoap.Types}'><s:Body><m:CopyItem>{fields}</m:CopyItem></s:Body></s:Envelope>";

    private sealed class Transport(IEnumerable<JmapApplicationResult> replies) : IGatewayApplicationTransport
    {
        private readonly Queue<JmapApplicationResult> _replies = new(replies);
        public List<MailOperationCommand> Commands { get; } = [];
        public Task<TResponse> SendAsync<TRequest, TResponse>(string protocol, string operation, TRequest request, CancellationToken cancellationToken = default)
        {
            Assert.AreEqual("ews", protocol, StringComparer.Ordinal);
            Assert.AreEqual(ApplicationOperations.MailOperationExecute, operation, StringComparer.Ordinal);
            Commands.Add(((MailOperationApplicationRequest)(object)request!).Command);
            return Task.FromResult((TResponse)(object)_replies.Dequeue());
        }
    }
}
