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
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1812", Justification = "MSTest DiscoverInternals executes this strict native-MIME CreateItem/upload/import fixture; discovered outcomes are retained.")]
internal sealed class GatewayEwsItemCreateTests
{
    private static readonly Guid Account = new("1138f53b-3efa-4a59-8602-e215c0df1e35");
    private static readonly Guid Target = new("1538f53b-3efa-4a59-8602-e215c0df1e35");
    private static readonly Guid Created = new("2438f53b-3efa-4a59-8602-e215c0df1e35");
    private static readonly Guid Second = new("3438f53b-3efa-4a59-8602-e215c0df1e35");
    private static readonly byte[] Native = Encoding.ASCII.GetBytes("Subject: create\r\n\r\nNative body\r\n");
    private static readonly string[] DraftKeywords = ["$draft"];
    private static readonly ProtocolAuthentication Authentication = new(ProtocolAuthenticationKinds.Password, "owner@example.test", "fixture-secret");
    private static readonly JmapApplicationProfile Profile = new("owner@example.test",
        new(4096, 1, 65_536, 8, 64, 500, 500, 50, 100, 4096, [], []), [new($"A{Account:N}", "owner@example.test", true, false, true)]);
    private static readonly MailImportCommand Command = new(Account, "s10", [Import("ewsCreate0"), Import("ewsCreate1")]);

    [TestMethod]
    [DataRow("")]
    [DataRow("CharacterSet='UTF-8'")]
    [DataRow("CharacterSet='ignored-label'")]
    public async Task ParserPreservesAsciiMimeAndIgnoresOnlyRegisteredCharset(string attributes)
    {
        using var stream = Body(Message(Convert.ToBase64String(Native), attributes));
        var request = await GatewayEwsRequestParser.ReadAsync(stream, CancellationToken.None).ConfigureAwait(false);
        Assert.IsTrue(request.IsMutation);
        Assert.AreEqual("CreateItem", request.Operation, StringComparer.Ordinal);
        Assert.HasCount(1, request.MimeCreates!);
        CollectionAssert.AreEqual(Native, request.MimeCreates![0]);
        Assert.IsNull(request.Items);
    }

    [TestMethod]
    public async Task MimeBase64WhitespaceDoesNotAlterNativeBytes()
    {
        var encoded = Convert.ToBase64String(Native);
        using var body = Body(Message(" \t" + encoded[..8] + "\r\n" + encoded[8..] + " "));
        CollectionAssert.AreEqual(Native, (await GatewayEwsRequestParser.ReadAsync(body, CancellationToken.None).ConfigureAwait(false)).MimeCreates![0]);
    }

    [TestMethod]
    [DataRow("", "ErrorInvalidMimeContent")]
    [DataRow("!", "ErrorInvalidMimeContent")]
    [DataRow("Zh==", "ErrorInvalidMimeContent")]
    [DataRow("/w==", "ErrorInvalidMimeContent")]
    public async Task InvalidEmptyNoncanonicalAndNonAsciiMimeRefuse(string value, string code)
    {
        using var stream = Body(Message(value));
        Assert.AreEqual(code, (await Assert.ThrowsAsync<GatewayEwsRequestException>(() => GatewayEwsRequestParser.ReadAsync(stream, CancellationToken.None)).ConfigureAwait(false)).Code, StringComparer.Ordinal);
    }

    [TestMethod]
    [DataRow("", "ErrorInvalidRequest")]
    [DataRow("MessageDisposition='SendOnly'", "ErrorInvalidRequest")]
    [DataRow("MessageDisposition='SendAndSaveCopy'", "ErrorInvalidRequest")]
    [DataRow("MessageDisposition='SaveOnly' SendMeetingInvitations='SendToNone'", "ErrorSchemaValidation")]
    public async Task SendingAndCalendarSemanticsCannotEnterCreation(string attributes, string code)
    {
        using var stream = Body(Message(Convert.ToBase64String(Native)), operationAttributes: attributes);
        Assert.AreEqual(code, (await Assert.ThrowsAsync<GatewayEwsRequestException>(() => GatewayEwsRequestParser.ReadAsync(stream, CancellationToken.None)).ConfigureAwait(false)).Code, StringComparer.Ordinal);
    }

    [TestMethod]
    [DataRow("<t:DistinguishedFolderId Id='drafts'/>", "ErrorInvalidRequest")]
    [DataRow("<t:FolderId Id='target' ChangeKey='czEw'/>", "ErrorInvalidRequest")]
    [DataRow("<t:FolderId Id='target' Extra='value'/>", "ErrorSchemaValidation")]
    [DataRow("<t:FolderId Id='a'/><t:FolderId Id='b'/>", "ErrorSchemaValidation")]
    public async Task OnlyOneExplicitUnversionedPhysicalDestinationIsAdmitted(string folder, string code)
    {
        using var stream = Body(Message(Convert.ToBase64String(Native)), folder);
        Assert.AreEqual(code, (await Assert.ThrowsAsync<GatewayEwsRequestException>(() => GatewayEwsRequestParser.ReadAsync(stream, CancellationToken.None)).ConfigureAwait(false)).Code, StringComparer.Ordinal);
    }

    [TestMethod]
    [DataRow("<t:CalendarItem/>", "ErrorInvalidRequest")]
    [DataRow("<t:Message><t:Body BodyType='Text'>body</t:Body></t:Message>", "ErrorInvalidPropertySet")]
    [DataRow("<t:Message><t:MimeContent>TQ==</t:MimeContent><t:Subject>override</t:Subject></t:Message>", "ErrorInvalidPropertySet")]
    [DataRow("<t:Message><t:MimeContent>TQ==</t:MimeContent><t:MimeContent>TQ==</t:MimeContent></t:Message>", "ErrorInvalidPropertySet")]
    [DataRow("<t:Message><t:MimeContent Other='value'>TQ==</t:MimeContent></t:Message>", "ErrorSchemaValidation")]
    [DataRow("<t:Message><t:MimeContent><t:Text>TQ==</t:Text></t:MimeContent></t:Message>", "ErrorSchemaValidation")]
    public async Task NonmailPropertyOverridesAndNestedContentCannotDispatch(string item, string code)
    {
        using var stream = Body(item);
        Assert.AreEqual(code, (await Assert.ThrowsAsync<GatewayEwsRequestException>(() => GatewayEwsRequestParser.ReadAsync(stream, CancellationToken.None)).ConfigureAwait(false)).Code, StringComparer.Ordinal);
    }

    [TestMethod]
    [DataRow(32_768, true)]
    [DataRow(32_769, false)]
    public async Task DecodedMimeLimitIsInclusiveBeforeAnyApplicationWork(int count, bool valid)
    {
        using var stream = Body(Message(Convert.ToBase64String(Enumerable.Repeat((byte)'x', count).ToArray())));
        if (valid) Assert.HasCount(count, (await GatewayEwsRequestParser.ReadAsync(stream, CancellationToken.None).ConfigureAwait(false)).MimeCreates![0]);
        else Assert.AreEqual("ErrorDataSizeLimitExceeded", (await Assert.ThrowsAsync<GatewayEwsRequestException>(() => GatewayEwsRequestParser.ReadAsync(stream, CancellationToken.None)).ConfigureAwait(false)).Code, StringComparer.Ordinal);
    }

    [TestMethod]
    public async Task ThirtyThirdCreationIsRejectedByTheRegisteredObjectBound()
    {
        using var stream = Body(string.Concat(Enumerable.Repeat(Message(Convert.ToBase64String(Native)), 33)));
        Assert.AreEqual("ErrorExceededFindCountLimit", (await Assert.ThrowsAsync<GatewayEwsRequestException>(() => GatewayEwsRequestParser.ReadAsync(stream, CancellationToken.None)).ConfigureAwait(false)).Code, StringComparer.Ordinal);
    }

    [TestMethod]
    public async Task CreationUsesEmptyStateReadAccountScopedUploadAndGuardedDraftImport()
    {
        var transport = new Transport([State(), Upload(), Result(Success() with { Items = [Success().Items[0]] })]);
        var xml = await GatewayEwsItemCreate.ExecuteAsync(new(transport, new()), Authentication, Profile, Account, Request([Native]), CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("NoError", XDocument.Parse(xml).Descendants(GatewayEwsSoap.Messages + "ResponseCode").Single().Value, StringComparer.Ordinal);
        var read = transport.Commands[0].Arguments.Deserialize<MailMessageReadCommand>(JsonSerializerOptions.Web)!;
        Assert.IsNotNull(read.MessageIds);
        Assert.IsEmpty(read.MessageIds);
        Assert.IsFalse(read.IncludeText);
        Assert.HasCount(1, transport.Uploads);
        var upload = transport.Uploads[0];
        Assert.AreEqual($"A{Account:N}", upload.AccountId, StringComparer.Ordinal);
        Assert.AreEqual("message/rfc822", upload.ContentType, StringComparer.Ordinal);
        CollectionAssert.AreEqual(Native, upload.Content);
        var import = transport.Commands[1].Arguments.Deserialize<MailImportCommand>(JsonSerializerOptions.Web)!;
        Assert.AreEqual(MailOperationKind.ImportMessages, transport.Commands[1].Operation);
        Assert.AreEqual(Account, import.AccountId);
        Assert.AreEqual("s10", import.IfInState, StringComparer.Ordinal);
        Assert.HasCount(1, import.Items);
        Assert.AreEqual(Target, import.Items[0].MailboxId);
        CollectionAssert.AreEqual(DraftKeywords, import.Items[0].Keywords.ToArray());
        Assert.IsNull(import.Items[0].ReceivedAt);
        Assert.IsFalse(import.Items[0].InvalidInitialProperties || import.Items[0].InvalidReceivedAt);
    }

    [TestMethod]
    [DataRow("foreign", "ErrorAccessDenied")]
    [DataRow("root", "ErrorAccessDenied")]
    [DataRow("malformed", "ErrorInvalidIdMalformed")]
    public async Task UnadmittedDestinationNeverReadsStateOrUploads(string mode, string code)
    {
        var id = mode is "foreign" ? GatewayEwsFolderIdCodec.Encode(Second, Target)
            : mode is "root" ? GatewayEwsFolderIdCodec.Encode(Account, Guid.Empty) : "bad";
        var transport = new Transport([]);
        var request = Request([Native]) with { Folders = [new(id, false, null, null)] };
        var xml = await GatewayEwsItemCreate.ExecuteAsync(new(transport, new()), Authentication, Profile, Account, request, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(code, XDocument.Parse(xml).Descendants(GatewayEwsSoap.Messages + "ResponseCode").Single().Value, StringComparer.Ordinal);
        Assert.IsEmpty(transport.Commands);
        Assert.IsEmpty(transport.Uploads);
    }

    [TestMethod]
    public async Task ConfiguredSetAndUploadLimitsPrecedeTransport()
    {
        var transport = new Transport([]);
        var client = new GatewayEwsClient(transport, new());
        var set = Profile with { Limits = Profile.Limits with { MaxObjectsInSet = 1 } };
        Assert.AreEqual("ErrorExceededFindCountLimit", (await Assert.ThrowsAsync<GatewayEwsRequestException>(() => GatewayEwsItemCreate.ExecuteAsync(client, Authentication, set, Account, Request([Native, Native]), CancellationToken.None)).ConfigureAwait(false)).Code, StringComparer.Ordinal);
        var small = Profile with { Limits = Profile.Limits with { MaxUploadSizeBytes = Native.Length - 1 } };
        var xml = await GatewayEwsItemCreate.ExecuteAsync(client, Authentication, small, Account, Request([Native]), CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("ErrorDataSizeLimitExceeded", XDocument.Parse(xml).Descendants(GatewayEwsSoap.Messages + "ResponseCode").Single().Value, StringComparer.Ordinal);
        Assert.IsEmpty(transport.Commands);
        Assert.IsEmpty(transport.Uploads);
    }

    [TestMethod]
    [DataRow("null")]
    [DataRow("empty")]
    [DataRow("control")]
    [DataRow("long")]
    [DataRow("status")]
    [DataRow("account")]
    public async Task StatePreflightCannotDispatchUploadWithoutValidatedCurrentAuthority(string mode)
    {
        var state = mode is "null" ? null : mode is "empty" ? "" : mode is "control" ? "s\n10"
            : mode is "long" ? new string('x', 257) : "s10";
        var read = new MailMessageReadResult(mode is "account" ? MailMessageReadStatus.AccountNotFound
            : mode is "status" ? MailMessageReadStatus.RequestTooLarge : MailMessageReadStatus.Ok,
            mode is "account" or "status" ? null : state, []);
        var transport = new Transport([new(JmapApplicationOutcomes.Ok, OperationResult: Operation(MailOperationKind.ReadMessages,
            JsonSerializer.SerializeToNode(read, JsonSerializerOptions.Web)!))]);
        if (mode is "account")
        {
            var xml = await GatewayEwsItemCreate.ExecuteAsync(new(transport, new()), Authentication, Profile, Account, Request([Native]), CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual("ErrorFolderNotFound", XDocument.Parse(xml).Descendants(GatewayEwsSoap.Messages + "ResponseCode").Single().Value, StringComparer.Ordinal);
        }
        else await Assert.ThrowsAsync<InvalidOperationException>(() => GatewayEwsItemCreate.ExecuteAsync(new(transport, new()), Authentication,
            Profile, Account, Request([Native]), CancellationToken.None)).ConfigureAwait(false);
        Assert.HasCount(1, transport.Commands);
        Assert.IsEmpty(transport.Uploads);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task MessageAndEncodedTransportBudgetsRejectBeforeStateOrBlobDispatch(bool transportBudget)
    {
        var transport = new Transport([]);
        var environment = transportBudget
            ? new EnvironmentConfig { Messaging = new() { MaxPayloadBytes = GatewayHttpPayloadBudget.MetadataBytes + 4 } }
            : new EnvironmentConfig();
        var profile = Profile;
        if (!transportBudget) profile = profile with { Limits = profile.Limits with { MaxMessageSizeBytes = Native.Length - 1 } };
        var xml = await GatewayEwsItemCreate.ExecuteAsync(new(transport, environment), Authentication, profile, Account, Request([Native]), CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("ErrorDataSizeLimitExceeded", XDocument.Parse(xml).Descendants(GatewayEwsSoap.Messages + "ResponseCode").Single().Value, StringComparer.Ordinal);
        Assert.IsEmpty(transport.Commands);
        Assert.IsEmpty(transport.Uploads);
    }

    [TestMethod]
    [DataRow("valid")]
    [DataRow("wrong-type")]
    [DataRow("wrong-size")]
    [DataRow("empty-id")]
    [DataRow("raw-id")]
    [DataRow("unknown-outcome")]
    [DataRow("success-failure")]
    [DataRow("content")]
    [DataRow("profile")]
    public void UploadRepliesCannotSupplyUncorrelatedContentOrAuthority(string mode)
    {
        var result = Upload();
        if (mode is "wrong-type") result = result with { ContentType = "text/plain" };
        if (mode is "wrong-size") result = result with { Size = Native.Length + 1 };
        if (mode is "empty-id") result = result with { BlobId = $"U{Guid.Empty:N}" };
        if (mode is "raw-id") result = result with { BlobId = $"B{Created:N}" };
        if (mode is "unknown-outcome") result = result with { Outcome = "other" };
        if (mode is "success-failure") result = result with { Failure = new(MailFailureKind.InvalidSelection) };
        if (mode is "content") result = result with { Content = Native };
        if (mode is "profile") result = result with { Profile = Profile };
        if (mode is "valid") Assert.AreEqual($"U{Created:N}", GatewayEwsMimeUploadReply.Decode(result, Native.Length), StringComparer.Ordinal);
        else Assert.Throws<InvalidOperationException>(() => GatewayEwsMimeUploadReply.Decode(result, Native.Length));
    }

    [TestMethod]
    [DataRow("valid")]
    [DataRow("missing-field")]
    [DataRow("extra-field")]
    [DataRow("null-row")]
    [DataRow("missing-row")]
    [DataRow("wrong-token")]
    [DataRow("empty-id")]
    [DataRow("duplicate-id")]
    [DataRow("wrong-size")]
    [DataRow("thread")]
    [DataRow("old-state")]
    [DataRow("new-state")]
    [DataRow("unchanged")]
    [DataRow("status")]
    [DataRow("error")]
    [DataRow("failure-data")]
    [DataRow("known-missing")]
    [DataRow("known-wrong")]
    public void ImportRepliesRequireExactOrderedCreationStateSizeAndKnownMap(string mode)
    {
        var data = (JsonObject)JsonSerializer.SerializeToNode(Success(), JsonSerializerOptions.Web)!;
        Corrupt(mode, data);
        var known = Known(Success());
        if (mode is "known-missing") known.Clear();
        if (mode is "known-wrong") known["ewsCreate0"] = $"E{Second:N}";
        var reply = Operation(MailOperationKind.ImportMessages, data, known);
        if (mode is "valid") Assert.HasCount(2, GatewayEwsItemCreateReply.Decode(reply, Command, Sizes()).Items);
        else Assert.Throws<InvalidOperationException>(() => GatewayEwsItemCreateReply.Decode(reply, Command, Sizes()));
    }

    private static void Corrupt(string mode, JsonObject data)
    {
        var row = (JsonObject)data["items"]![0]!;
        if (mode is "missing-field") row.Remove("size");
        if (mode is "extra-field") row["other"] = true;
        if (mode is "null-row") data["items"]![0] = null;
        if (mode is "missing-row") ((JsonArray)data["items"]!).RemoveAt(1);
        if (mode is "wrong-token") row["creationId"] = "other";
        if (mode is "empty-id") row["emailId"] = Guid.Empty;
        if (mode is "duplicate-id") data["items"]![1]!["emailId"] = Created;
        if (mode is "wrong-size") row["size"] = Native.Length + 1;
        if (mode is "thread") row["storedThreadId"] = "bad\nthread";
        if (mode is "old-state") data["oldState"] = "other";
        if (mode is "new-state") data["newState"] = "";
        if (mode is "unchanged") data["newState"] = "s10";
        if (mode is "status") data["status"] = 99;
        if (mode is "error") row["error"] = 99;
        if (mode is "failure-data") data["status"] = (int)MailImportStatus.StateMismatch;
    }

    [TestMethod]
    [DataRow(MailImportStatus.AccountNotFound)]
    [DataRow(MailImportStatus.StateMismatch)]
    [DataRow(MailImportStatus.RequestTooLarge)]
    public void WholeRefusalsCannotCarryCreatedData(MailImportStatus status)
    {
        var result = new MailImportResult(status, null, null, []);
        Assert.AreEqual(status, GatewayEwsItemCreateReply.Decode(Operation(MailOperationKind.ImportMessages, JsonSerializer.SerializeToNode(result, JsonSerializerOptions.Web)!), Command, Sizes()).Status);
    }

    [TestMethod]
    [DataRow(MailImportItemError.MissingBlob, "ErrorServerBusy")]
    [DataRow(MailImportItemError.InvalidMailbox, "ErrorFolderNotFound")]
    [DataRow(MailImportItemError.TooLarge, "ErrorDataSizeLimitExceeded")]
    [DataRow(MailImportItemError.OverQuota, "ErrorQuotaExceeded")]
    [DataRow(MailImportItemError.InvalidEmail, "ErrorInvalidMimeContent")]
    public void LegalNativeFailuresPreserveOrderedSuccessfulCreation(MailImportItemError error, string code)
    {
        var success = Success();
        var result = success with { Items = [success.Items[0], new("ewsCreate1", error, null, null, null)] };
        var decoded = GatewayEwsItemCreateReply.Decode(Operation(MailOperationKind.ImportMessages, JsonSerializer.SerializeToNode(result, JsonSerializerOptions.Web)!, Known(result)), Command, Sizes());
        var document = XDocument.Parse(GatewayEwsItemCreateResponse.Render(Account, [new("ewsCreate0", Native, null), new("ewsCreate1", Native, null)], decoded));
        CollectionAssert.AreEqual(new[] { "NoError", code }, document.Descendants(GatewayEwsSoap.Messages + "ResponseCode").Select(item => item.Value).ToArray());
        Assert.HasCount(1, document.Descendants(GatewayEwsSoap.Types + "ItemId").ToArray());
    }

    private static MailImportItem Import(string token) => new(token, $"U{Created:N}", false, Target, MailMessageMailboxIssue.None, ["$draft"], MailMessageKeywordIssue.None, null, false);
    private static Dictionary<string, int> Sizes() => new(StringComparer.Ordinal) { ["ewsCreate0"] = Native.Length, ["ewsCreate1"] = Native.Length };
    private static MailImportResult Success() => new(MailImportStatus.Ok, "s10", "s11", [new("ewsCreate0", MailImportItemError.None, Created, "thread", Native.Length), new("ewsCreate1", MailImportItemError.None, Second, "thread", Native.Length)]);
    private static Dictionary<string, string> Known(MailImportResult result) => result.Items.Where(item => item.Error == MailImportItemError.None).ToDictionary(item => item.CreationId, item => $"E{item.EmailId:N}", StringComparer.Ordinal);
    private static JmapApplicationResult Result(MailImportResult result) => new(JmapApplicationOutcomes.Ok, OperationResult: Operation(MailOperationKind.ImportMessages, JsonSerializer.SerializeToNode(result, JsonSerializerOptions.Web)!, Known(result)));
    private static JmapApplicationResult State() => new(JmapApplicationOutcomes.Ok, OperationResult: Operation(MailOperationKind.ReadMessages, JsonSerializer.SerializeToNode(new MailMessageReadResult(MailMessageReadStatus.Ok, "s10", []), JsonSerializerOptions.Web)!));
    private static JmapApplicationResult Upload() => new(JmapApplicationOutcomes.Ok, ContentType: "message/rfc822", BlobId: $"U{Created:N}", Size: Native.Length);
    private static MailOperationResult Operation(MailOperationKind operation, JsonNode data, Dictionary<string, string>? known = null) => new(new(operation, ApplicationValueCodec.Encode(data)), known ?? new(StringComparer.Ordinal), Profile);
    private static GatewayEwsRequest Request(byte[][] contents) => new("CreateItem", new HashSet<string>(StringComparer.Ordinal), [new(GatewayEwsFolderIdCodec.Encode(Account, Target), false, null, null)], false, 0, 0, false, MimeCreates: contents);
    private static string Message(string encoded, string attributes = "") => $"<t:Message><t:MimeContent {attributes}>{encoded}</t:MimeContent></t:Message>";
    private static MemoryStream Body(string messages, string folder = "<t:FolderId Id='target'/>", string operationAttributes = "MessageDisposition='SaveOnly'") => new(Encoding.UTF8.GetBytes($"<s:Envelope xmlns:s='{GatewayEwsSoap.Soap}' xmlns:m='{GatewayEwsSoap.Messages}' xmlns:t='{GatewayEwsSoap.Types}'><s:Body><m:CreateItem {operationAttributes}><m:SavedItemFolderId>{folder}</m:SavedItemFolderId><m:Items>{messages}</m:Items></m:CreateItem></s:Body></s:Envelope>"));

    private sealed class Transport(IEnumerable<JmapApplicationResult> responses) : IGatewayApplicationTransport
    {
        private readonly Queue<JmapApplicationResult> _responses = new(responses);
        public List<MailOperationCommand> Commands { get; } = [];
        public List<JmapUploadApplicationRequest> Uploads { get; } = [];
        public Task<TResponse> SendAsync<TRequest, TResponse>(string protocol, string operation, TRequest request, CancellationToken cancellationToken = default)
        {
            Assert.AreEqual("ews", protocol, StringComparer.Ordinal);
            if (request is JmapUploadApplicationRequest upload)
            {
                Assert.AreEqual(ApplicationOperations.JmapUpload, operation, StringComparer.Ordinal);
                Uploads.Add(upload);
            }
            else
            {
                Assert.AreEqual(ApplicationOperations.MailOperationExecute, operation, StringComparer.Ordinal);
                Commands.Add(((MailOperationApplicationRequest)(object)request!).Command);
            }
            return Task.FromResult((TResponse)(object)_responses.Dequeue());
        }
    }
}
