using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using mk8.email.Contracts.Messaging;
using mk8.email.Gateway.ApplicationBridge;
using mk8.email.Gateway.Protocols.Ews;

namespace mk8.email.Application.Tests;

[TestClass]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1812", Justification = "MSTest DiscoverInternals executes this bounded folder synchronization/parser/cursor/reply fixture; discovered outcomes are retained.")]
internal sealed class GatewayEwsFolderSyncTests
{
    private static readonly Guid Account = new("1138f53b-3efa-4a59-8602-e215c0df1e35");
    private static readonly Guid Parent = new("1538f53b-3efa-4a59-8602-e215c0df1e35");
    private static readonly Guid Child = new("1438f53b-3efa-4a59-8602-e215c0df1e35");
    private static readonly Guid Deleted = new("2438f53b-3efa-4a59-8602-e215c0df1e35");
    private static readonly Guid Foreign = new("3438f53b-3efa-4a59-8602-e215c0df1e35");
    private static readonly Guid[] Current = [Parent, Child];
    private static readonly Guid[] Previous = [Parent, Deleted];
    private static readonly string[] ChangeKinds = ["Create", "Update", "Delete"];
    private static readonly ProtocolAuthentication Authentication = new(ProtocolAuthenticationKinds.Password, "owner@example.test", "fixture-secret");
    private static readonly JmapApplicationProfile Profile = new("owner@example.test",
        new(4096, 1, 65_536, 8, 64, 500, 500, 50, 100, 4096, [], []), [new($"A{Account:N}", "owner@example.test", true, false, true)]);

    [TestMethod]
    [DataRow("")]
    [DataRow("<m:SyncFolderId><t:DistinguishedFolderId Id='msgfolderroot'/></m:SyncFolderId>")]
    public async Task InitialSyncHasOneRootIdOnlyAndNoMutationAuthority(string scope)
    {
        using var body = Body(scope);
        var request = await GatewayEwsRequestParser.ReadAsync(body, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("SyncFolderHierarchy", request.Operation, StringComparer.Ordinal);
        Assert.IsFalse(request.IsMutation);
        Assert.IsNull(request.SyncState);
        Assert.AreEqual(scope.Length != 0, request.SyncScopeSpecified);
        Assert.HasCount(1, request.Folders);
        Assert.IsTrue(request.Properties.SetEquals(new HashSet<string>(StringComparer.Ordinal) { "FolderId" }));
    }

    [TestMethod]
    public async Task RegisteredMetadataAndCanonicalStateRetainTheWatermark()
    {
        var state = GatewayEwsFolderSyncState.Encode(Account, "s10", Parents(Current));
        using var body = Body($"<m:SyncState>{state}</m:SyncState>", "<t:BaseShape>IdOnly</t:BaseShape><t:AdditionalProperties><t:FieldURI FieldURI='folder:ParentFolderId'/><t:FieldURI FieldURI='folder:DisplayName'/><t:FieldURI FieldURI='folder:UnreadCount'/></t:AdditionalProperties>");
        var request = await GatewayEwsRequestParser.ReadAsync(body, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(state, request.SyncState, StringComparer.Ordinal);
        Assert.HasCount(4, request.Properties);
    }

    [TestMethod]
    public async Task DefaultShapeAdmitsDerivedChildCountsAlongsideExistingScalarMetadata()
    {
        using var body = Body("", "<t:BaseShape>Default</t:BaseShape>");
        var request = await GatewayEwsRequestParser.ReadAsync(body, CancellationToken.None).ConfigureAwait(false);
        Assert.IsTrue(request.Properties.Contains("ChildFolderCount"));
        Assert.HasCount(5, request.Properties);
    }

    [TestMethod]
    [DataRow("", "<t:BaseShape>AllProperties</t:BaseShape>", "ErrorInvalidPropertyRequest")]
    [DataRow("<m:SyncState/>", "<t:BaseShape>IdOnly</t:BaseShape>", "ErrorInvalidSyncStateData")]
    [DataRow("<m:SyncState>!</m:SyncState>", "<t:BaseShape>IdOnly</t:BaseShape>", "ErrorInvalidSyncStateData")]
    [DataRow("<m:SyncFolderId/>", "<t:BaseShape>IdOnly</t:BaseShape>", "ErrorSchemaValidation")]
    [DataRow("<m:SyncFolderId><t:FolderId Id='a'/><t:FolderId Id='b'/></m:SyncFolderId>", "<t:BaseShape>IdOnly</t:BaseShape>", "ErrorSchemaValidation")]
    [DataRow("<m:SyncFolderId><t:FolderId Id='a' ChangeKey='key'/></m:SyncFolderId>", "<t:BaseShape>IdOnly</t:BaseShape>", "ErrorInvalidRequest")]
    [DataRow("<m:Unknown/>", "<t:BaseShape>IdOnly</t:BaseShape>", "ErrorSchemaValidation")]
    [DataRow("<m:MaxChangesReturned>1</m:MaxChangesReturned>", "<t:BaseShape>IdOnly</t:BaseShape>", "ErrorSchemaValidation")]
    public async Task UnsupportedShapeScopeAndStateRefuseBeforeTransport(string fields, string shape, string code)
    {
        using var body = Body(fields, shape);
        Assert.AreEqual(code, (await Assert.ThrowsAsync<GatewayEwsRequestException>(() => GatewayEwsRequestParser.ReadAsync(body, CancellationToken.None)).ConfigureAwait(false)).Code, StringComparer.Ordinal);
    }

    [TestMethod]
    [DataRow("s0")]
    [DataRow("s10")]
    [DataRow("s9223372036854775807")]
    public void WatermarksBindVersionAccountStateRootModeAndCanonicalParentGraph(string state)
    {
        var token = GatewayEwsFolderSyncState.Encode(Account, state, Parents(Current));
        Assert.IsTrue(GatewayEwsFolderSyncState.TryDecode(token, out var cursor));
        Assert.AreEqual(Account, cursor!.Account);
        Assert.AreEqual(state, cursor.State, StringComparer.Ordinal);
        Assert.HasCount(2, cursor.Parents);
        Assert.AreEqual(Parent, cursor.Parents[Child]);
        Assert.IsNull(cursor.Parents[Parent]);
    }

    [TestMethod]
    [DataRow("empty")]
    [DataRow("whitespace")]
    [DataRow("version")]
    [DataRow("zero-account")]
    [DataRow("negative-state")]
    [DataRow("large-count")]
    [DataRow("wrong-count")]
    [DataRow("flag")]
    [DataRow("zero-id")]
    [DataRow("duplicate-id")]
    [DataRow("reverse-order")]
    [DataRow("missing-parent")]
    [DataRow("cycle")]
    [DataRow("truncated")]
    [DataRow("trailing")]
    public void MalformedUnorderedOrCyclicStateCannotBeAdmitted(string mode)
    {
        var token = GatewayEwsFolderSyncState.Encode(Account, "s10", Parents(Current));
        var bytes = Convert.FromBase64String(token);
        if (mode is "empty") token = "";
        else if (mode is "whitespace") token = " " + token;
        else
        {
            if (mode is "version") bytes[7] = 2;
            if (mode is "zero-account") bytes.AsSpan(8, 16).Clear();
            if (mode is "negative-state") bytes[24] = 0x80;
            if (mode is "large-count") BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(33, 2), 501);
            if (mode is "wrong-count") BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(33, 2), 1);
            if (mode is "flag") bytes[32] = 2;
            if (mode is "zero-id") bytes.AsSpan(35, 16).Clear();
            if (mode is "duplicate-id") bytes.AsSpan(35, 16).CopyTo(bytes.AsSpan(67, 16));
            if (mode is "reverse-order")
            {
                var first = bytes.AsSpan(35, 32).ToArray();
                bytes.AsSpan(67, 32).CopyTo(bytes.AsSpan(35, 32));
                first.CopyTo(bytes, 67);
            }
            if (mode is "missing-parent") Foreign.ToByteArray().CopyTo(bytes, 51);
            if (mode is "cycle") bytes.AsSpan(35, 16).CopyTo(bytes.AsSpan(83, 16));
            if (mode is "truncated") bytes = bytes[..^1];
            if (mode is "trailing") bytes = [.. bytes, 0];
            token = Convert.ToBase64String(bytes);
        }
        Assert.IsFalse(GatewayEwsFolderSyncState.TryDecode(token, out _));
    }

    [TestMethod]
    [DataRow("valid")]
    [DataRow("extra")]
    [DataRow("missing")]
    [DataRow("null-keys")]
    [DataRow("status")]
    [DataRow("old-state")]
    [DataRow("new-state")]
    [DataRow("backward")]
    [DataRow("unchanged")]
    [DataRow("foreign-type")]
    [DataRow("zero-id")]
    [DataRow("noncanonical-id")]
    [DataRow("duplicate")]
    [DataRow("over-limit")]
    [DataRow("failure-data")]
    public void DeltaRepliesRequireExactBoundedExclusiveFolderIdentitiesAndMonotonicState(string mode)
    {
        var data = (JsonObject)JsonSerializer.SerializeToNode(Delta(), JsonSerializerOptions.Web)!;
        if (mode is "extra") data["other"] = true;
        if (mode is "missing") data.Remove("newState");
        if (mode is "null-keys") data["createdKeys"] = null;
        if (mode is "status") data["status"] = 99;
        if (mode is "old-state") data["oldState"] = "s9";
        if (mode is "new-state") data["newState"] = "s011";
        if (mode is "backward") data["newState"] = "s9";
        if (mode is "unchanged") data["newState"] = "s10";
        if (mode is "foreign-type") data["createdKeys"]![0] = $"E{Child:N}";
        if (mode is "zero-id") data["createdKeys"]![0] = $"M{Guid.Empty:N}";
        if (mode is "noncanonical-id") data["createdKeys"]![0] = $"M{Child:N}".ToUpperInvariant();
        if (mode is "duplicate") data["updatedKeys"]![0] = $"M{Child:N}";
        if (mode is "failure-data") data["status"] = (int)MailChangesStatus.CannotCalculateChanges;
        if (mode is "valid") Assert.AreEqual("s11", GatewayEwsFolderSyncReply.Decode(data, "s10", 3).NewState, StringComparer.Ordinal);
        else Assert.Throws<InvalidOperationException>(() => GatewayEwsFolderSyncReply.Decode(data, "s10", mode is "over-limit" ? 2 : 3));
    }

    [TestMethod]
    public async Task InitialCreatesAreParentFirstAndUseAValidatedFinalGraph()
    {
        var transport = new Transport([.. Graph("s10"), .. Graph("s10")]);
        var xml = XDocument.Parse(await ExecuteAsync(transport, Request()).ConfigureAwait(false));
        AssertCode(xml, "NoError");
        var folders = xml.Descendants(GatewayEwsSoap.Types + "FolderId").ToArray();
        Assert.AreEqual(GatewayEwsFolderIdCodec.Encode(Account, Parent), (string?)folders[0].Attribute("Id"), StringComparer.Ordinal);
        Assert.AreEqual(GatewayEwsFolderIdCodec.Encode(Account, Child), (string?)folders[1].Attribute("Id"), StringComparer.Ordinal);
        Assert.HasCount(2, xml.Descendants(GatewayEwsSoap.Types + "Create"));
        Assert.AreEqual("true", xml.Descendants(GatewayEwsSoap.Messages + "IncludesLastFolderInRange").Single().Value, StringComparer.Ordinal);
        Assert.IsTrue(GatewayEwsFolderSyncState.TryDecode(xml.Descendants(GatewayEwsSoap.Messages + "SyncState").Single().Value, out var cursor));
        Assert.AreEqual("s10", cursor!.State, StringComparer.Ordinal);
        Assert.HasCount(4, transport.Commands);
    }

    [TestMethod]
    public async Task IncrementalSynchronizationReturnsOneCompleteCreateUpdateDeletePage()
    {
        var transport = new Transport([.. Graph("s11"), Result(MailOperationKind.ReadFolderChanges, Delta()), .. Graph("s11")]);
        var xml = XDocument.Parse(await ExecuteAsync(transport, Request(GatewayEwsFolderSyncState.Encode(Account, "s10", Parents(Previous)))).ConfigureAwait(false));
        AssertCode(xml, "NoError");
        CollectionAssert.AreEqual(ChangeKinds, xml.Descendants(GatewayEwsSoap.Messages + "Changes").Single().Elements().Select(item => item.Name.LocalName).ToArray());
        var command = transport.Commands.Single(item => item.Operation == MailOperationKind.ReadFolderChanges).Arguments.Deserialize<MailChangesCommand>(JsonSerializerOptions.Web)!;
        Assert.AreEqual(Account, command.AccountId);
        Assert.AreEqual("s10", command.SinceState, StringComparer.Ordinal);
        Assert.AreEqual(500L, command.MaxChanges);
        Assert.IsTrue(command.AccountReferenceEligible);
    }

    [TestMethod]
    [DataRow("partial", "ErrorExceededFindCountLimit")]
    [DataRow("race", "ErrorServerBusy")]
    [DataRow("lost-membership", "ErrorInvalidSyncStateData")]
    [DataRow("future", "ErrorInvalidSyncStateData")]
    [DataRow("lost-account", "ErrorFolderNotFound")]
    public async Task IncompleteRacedOrUnverifiableDeltasNeverAdvanceTheClientCursor(string mode, string code)
    {
        var delta = mode is "future" or "lost-account" ? new MailChangesResult(mode is "future" ? MailChangesStatus.CannotCalculateChanges : MailChangesStatus.AccountNotFound, null, null, false, [], [], [])
            : Delta() with { HasMoreChanges = mode is "partial", NewState = mode is "race" ? "s12" : "s11" };
        var prior = mode is "lost-membership" ? Current : Previous;
        var transport = new Transport([.. Graph("s11"), Result(MailOperationKind.ReadFolderChanges, delta)]);
        var xml = XDocument.Parse(await ExecuteAsync(transport, Request(GatewayEwsFolderSyncState.Encode(Account, "s10", Parents(prior)))).ConfigureAwait(false));
        AssertCode(xml, code);
        Assert.IsFalse(xml.Descendants(GatewayEwsSoap.Messages + "SyncState").Any() || xml.Descendants(GatewayEwsSoap.Messages + "Changes").Any());
    }

    [TestMethod]
    public async Task FinalCommittedWriteWithholdsAllInitialCreates()
    {
        var transport = new Transport([.. Graph("s10"), .. Graph("s11")]);
        var xml = XDocument.Parse(await ExecuteAsync(transport, Request()).ConfigureAwait(false));
        AssertCode(xml, "ErrorServerBusy");
        Assert.IsFalse(xml.Descendants(GatewayEwsSoap.Types + "FolderId").Any());
    }

    [TestMethod]
    public async Task DerivedParentAndChildCountsNotifySurvivorsEvenWithoutTheirNativeUpdateKeys()
    {
        var old = new Dictionary<Guid, Guid?> { [Parent] = null, [Child] = null };
        var delta = new MailChangesResult(MailChangesStatus.Ok, "s10", "s11", false, [], [], []);
        var transport = new Transport([.. Graph("s11"), Result(MailOperationKind.ReadFolderChanges, delta), .. Graph("s11")]);
        var request = Request(GatewayEwsFolderSyncState.Encode(Account, "s10", old)) with
        {
            Properties = new HashSet<string>(StringComparer.Ordinal) { "FolderId", "ParentFolderId", "ChildFolderCount" },
        };
        var xml = XDocument.Parse(await ExecuteAsync(transport, request).ConfigureAwait(false));
        AssertCode(xml, "NoError");
        Assert.HasCount(2, xml.Descendants(GatewayEwsSoap.Types + "Update"));
        var child = xml.Descendants(GatewayEwsSoap.Types + "Folder").Single(folder => string.Equals((string?)folder.Element(GatewayEwsSoap.Types + "FolderId")!.Attribute("Id"), GatewayEwsFolderIdCodec.Encode(Account, Child), StringComparison.Ordinal));
        Assert.AreEqual(GatewayEwsFolderIdCodec.Encode(Account, Parent), (string?)child.Element(GatewayEwsSoap.Types + "ParentFolderId")!.Attribute("Id"), StringComparer.Ordinal);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task OmittedAndExplicitRootModesCannotShareAnIncrementalWatermark(bool supplied)
    {
        var transport = new Transport([]);
        var request = Request(GatewayEwsFolderSyncState.Encode(Account, "s10", Parents(Current), !supplied)) with { SyncScopeSpecified = supplied };
        AssertCode(XDocument.Parse(await ExecuteAsync(transport, request).ConfigureAwait(false)), "ErrorInvalidSyncStateData");
        Assert.IsEmpty(transport.Commands);
    }

    [TestMethod]
    [DataRow("foreign", "ErrorAccessDenied")]
    [DataRow("physical", "ErrorInvalidRequest")]
    [DataRow("distinguished", "ErrorInvalidRequest")]
    [DataRow("alias", "ErrorAccessDenied")]
    [DataRow("token", "ErrorInvalidSyncStateData")]
    public async Task UnsupportedScopesAndForeignTokensCannotDispatchOwnedReads(string mode, string code)
    {
        var request = Request(mode is "token" ? GatewayEwsFolderSyncState.Encode(Foreign, "s10", Parents(Current)) : null);
        if (mode is "foreign") request = request with { Folders = [new(GatewayEwsFolderIdCodec.Encode(Foreign, Guid.Empty), false, null, null)] };
        if (mode is "physical") request = request with { Folders = [new(GatewayEwsFolderIdCodec.Encode(Account, Parent), false, null, null)] };
        if (mode is "distinguished") request = request with { Folders = [new("root", true, null, null)] };
        if (mode is "alias") request = request with { Folders = [new("msgfolderroot", true, "alias@example.test", null)] };
        var transport = new Transport([]);
        AssertCode(XDocument.Parse(await ExecuteAsync(transport, request).ConfigureAwait(false)), code);
        Assert.IsEmpty(transport.Commands);
    }

    private static Dictionary<Guid, Guid?> Parents(IEnumerable<Guid> ids) => ids.ToDictionary(id => id, id => id == Child || id == Deleted ? (Guid?)Parent : null);
    private static MailChangesResult Delta() => new(MailChangesStatus.Ok, "s10", "s11", false, [$"M{Child:N}"], [$"M{Parent:N}"], [$"M{Deleted:N}"]);
    private static MailFolderSnapshot Folder(Guid id, Guid? parent = null) => new(id, "Folder", parent, null, 0, true, 0, 0, 0, 0, false);
    private static JmapApplicationResult[] Graph(string state) => [Result(MailOperationKind.FindFolders, new MailFolderQueryResult(MailFolderQueryStatus.Ok, state, 0, [], 2)),
        Result(MailOperationKind.ReadFolders, new MailFolderReadResult(MailFolderReadStatus.Ok, state, [Folder(Parent), Folder(Child, Parent)]))];
    private static JmapApplicationResult Result<T>(MailOperationKind operation, T value) => new(JmapApplicationOutcomes.Ok,
        OperationResult: new(new(operation, ApplicationValueCodec.Encode(JsonSerializer.SerializeToNode(value, JsonSerializerOptions.Web)!)), new Dictionary<string, string>(StringComparer.Ordinal), Profile));
    private static GatewayEwsRequest Request(string? state = null) => new("SyncFolderHierarchy", new HashSet<string>(StringComparer.Ordinal) { "FolderId" }, [new("msgfolderroot", true, null, null)], true, 0, 500, false, SyncState: state);
    private static Task<string> ExecuteAsync(Transport transport, GatewayEwsRequest request) => GatewayEwsFolderSync.ExecuteAsync(new(transport, new()), Authentication, Profile, Account, request, CancellationToken.None);
    private static void AssertCode(XDocument document, string code) => Assert.AreEqual(code, document.Descendants(GatewayEwsSoap.Messages + "ResponseCode").Single().Value, StringComparer.Ordinal);
    private static MemoryStream Body(string fields, string shape = "<t:BaseShape>IdOnly</t:BaseShape>") => new(Encoding.UTF8.GetBytes($"<s:Envelope xmlns:s='{GatewayEwsSoap.Soap}' xmlns:m='{GatewayEwsSoap.Messages}' xmlns:t='{GatewayEwsSoap.Types}'><s:Body><m:SyncFolderHierarchy><m:FolderShape>{shape}</m:FolderShape>{fields}</m:SyncFolderHierarchy></s:Body></s:Envelope>"));

    private sealed class Transport(IEnumerable<JmapApplicationResult> responses) : IGatewayApplicationTransport
    {
        private readonly Queue<JmapApplicationResult> _responses = new(responses);
        public List<MailOperationCommand> Commands { get; } = [];
        public Task<TResponse> SendAsync<TRequest, TResponse>(string protocol, string operation, TRequest request, CancellationToken cancellationToken = default)
        {
            Assert.AreEqual("ews", protocol, StringComparer.Ordinal);
            Assert.AreEqual(ApplicationOperations.MailOperationExecute, operation, StringComparer.Ordinal);
            Commands.Add(((MailOperationApplicationRequest)(object)request!).Command);
            return Task.FromResult((TResponse)(object)_responses.Dequeue());
        }
    }
}
