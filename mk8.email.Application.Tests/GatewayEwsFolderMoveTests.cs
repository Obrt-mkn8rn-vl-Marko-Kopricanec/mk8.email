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
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1812", Justification = "MSTest DiscoverInternals executes this MoveFolder parser/planning/typed-result fixture; discovery is retained.")]
internal sealed class GatewayEwsFolderMoveTests
{
    private static readonly Guid Account = new("1138f53b-3efa-4a59-8602-e215c0df1e35");
    private static readonly Guid Source = new("2238f53b-3efa-4a59-8602-e215c0df1e35");
    private static readonly Guid Child = new("2338f53b-3efa-4a59-8602-e215c0df1e35");
    private static readonly Guid Target = new("2438f53b-3efa-4a59-8602-e215c0df1e35");
    private static readonly Guid Other = new("2538f53b-3efa-4a59-8602-e215c0df1e35");
    private static readonly ProtocolAuthentication Authentication = new(ProtocolAuthenticationKinds.Password, "owner@example.test", "fixture-secret");
    private static readonly JmapApplicationProfile Profile = new("owner@example.test",
        new(1024, 1, 65_536, 8, 64, 500, 500, 50, 100, 1024, [], []), [new($"A{Account:N}", "owner@example.test", true, false, true)]);

    [TestMethod]
    [DataRow("<t:FolderId Id='target'/>")]
    [DataRow("<t:DistinguishedFolderId Id='inbox'/>")]
    [DataRow("<t:DistinguishedFolderId Id='msgfolderroot'/>")]
    public async Task FolderMoveParserRetainsSourceKeysAndRegisteredDestination(string target)
    {
        using var body = Body(target, "<t:FolderId Id='source' ChangeKey='czEw'/><t:FolderId Id='source'/>");
        var request = await GatewayEwsRequestParser.ReadAsync(body, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("MoveFolder", request.Operation, StringComparer.Ordinal);
        Assert.IsTrue(request.IsMutation);
        Assert.HasCount(2, request.Folders);
        Assert.AreEqual("czEw", request.Folders[0].ChangeKey, StringComparer.Ordinal);
        Assert.IsNotNull(request.Destination);
        Assert.IsNull(request.Destination.ChangeKey);
    }

    [TestMethod]
    [DataRow("", "<t:FolderId Id='a'/>", "ErrorSchemaValidation")]
    [DataRow("<t:FolderId Id='a'/><t:FolderId Id='b'/>", "<t:FolderId Id='a'/>", "ErrorSchemaValidation")]
    [DataRow("<t:FolderId Id='a' ChangeKey='czEw'/>", "<t:FolderId Id='a'/>", "ErrorInvalidRequest")]
    [DataRow("<t:DistinguishedFolderId Id='inbox' ChangeKey='czEw'/>", "<t:FolderId Id='a'/>", "ErrorInvalidRequest")]
    [DataRow("<t:FolderId Id='a'/>", "", "ErrorSchemaValidation")]
    [DataRow("<t:FolderId Id='a'/>", "<t:ItemId Id='a'/>", "ErrorSchemaValidation")]
    [DataRow("<t:FolderId Id='a'/>", "<t:FolderId Id='a' Mailbox='foreign'/>", "ErrorSchemaValidation")]
    [DataRow("<t:FolderId Id='a'/>", "<t:FolderId Id=''/>", "ErrorSchemaValidation")]
    [DataRow("<t:FolderId Id='a'/>", "<?forbidden x?><t:FolderId Id='a'/>", "ErrorSchemaValidation")]
    public async Task FolderMoveMalformedOrUnsupportedShapesCannotDispatch(string target, string refs, string code)
    {
        using var body = Body(target, refs);
        var error = await Assert.ThrowsAsync<GatewayEwsRequestException>(() => GatewayEwsRequestParser.ReadAsync(body, CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(code, error.Code, StringComparer.Ordinal);
    }

    [TestMethod]
    public async Task FolderMoveReferencesAndOrderAreBounded()
    {
        using var body = Body("<t:FolderId Id='target'/>", string.Concat(Enumerable.Repeat("<t:FolderId Id='source'/>", 33)));
        Assert.AreEqual("ErrorExceededFindCountLimit", (await Assert.ThrowsAsync<GatewayEwsRequestException>(() =>
            GatewayEwsRequestParser.ReadAsync(body, CancellationToken.None)).ConfigureAwait(false)).Code, StringComparer.Ordinal);
        using var reversed = new MemoryStream(Encoding.UTF8.GetBytes(Envelope("<m:FolderIds><t:FolderId Id='a'/></m:FolderIds><m:ToFolderId><t:FolderId Id='b'/></m:ToFolderId>")));
        await Assert.ThrowsAsync<GatewayEwsRequestException>(() => GatewayEwsRequestParser.ReadAsync(reversed, CancellationToken.None)).ConfigureAwait(false);
    }

    [TestMethod]
    [DataRow("self", "ErrorInvalidRequest")]
    [DataRow("descendant", "ErrorInvalidRequest")]
    [DataRow("same-parent", "ErrorInvalidRequest")]
    [DataRow("protected", "ErrorMoveDistinguishedFolder")]
    [DataRow("distinguished", "ErrorMoveDistinguishedFolder")]
    [DataRow("root-source", "ErrorMoveDistinguishedFolder")]
    [DataRow("protected-child", "ErrorAccessDenied")]
    [DataRow("collision", "ErrorFolderExists")]
    [DataRow("stale", "ErrorIrresolvableConflict")]
    [DataRow("bad-key", "ErrorInvalidChangeKey")]
    [DataRow("foreign-source", "ErrorAccessDenied")]
    [DataRow("forged-source", "ErrorFolderNotFound")]
    [DataRow("foreign-target", "ErrorAccessDenied")]
    [DataRow("forged-target", "ErrorFolderNotFound")]
    [DataRow("alias-target", "ErrorAccessDenied")]
    public void FolderMovePlansNeverTreatReferencesAsAuthority(string scenario, string code)
    {
        var folders = Folders();
        var source = Ref(Source);
        var target = Ref(Target);
        if (scenario is "self") target = Ref(Source);
        if (scenario is "descendant") target = Ref(Child);
        if (scenario is "same-parent") target = Ref(Guid.Empty);
        if (scenario is "protected") folders[0] = folders[0] with { IsProtected = true };
        if (scenario is "protected-child") folders[1] = folders[1] with { IsProtected = true };
        if (scenario is "distinguished") source = new("inbox", true, null, null);
        if (scenario is "distinguished") folders[0] = folders[0] with { Role = "inbox", IsProtected = true };
        if (scenario is "root-source") source = Ref(Guid.Empty);
        if (scenario is "collision") folders[3] = folders[3] with { Name = "SOURCE", ParentId = Target };
        if (scenario is "stale") source = source with { ChangeKey = "b3RoZXI=" };
        if (scenario is "bad-key") source = source with { ChangeKey = "!bad" };
        if (scenario is "foreign-source") source = new(GatewayEwsFolderIdCodec.Encode(Other, Source), false, null, null);
        if (scenario is "forged-source") source = Ref(Account);
        if (scenario is "foreign-target") target = new(GatewayEwsFolderIdCodec.Encode(Other, Target), false, null, null);
        if (scenario is "forged-target") target = Ref(Account);
        if (scenario is "alias-target") target = new("inbox", true, "alias@example.test", null);
        var plans = GatewayEwsFolderMovePlan.Build(Request([source], target), new(Account, "s10", folders), Profile.Username,
            "ErrorFolderNotFound", out _);
        Assert.AreEqual(code, plans[0].Code, StringComparer.Ordinal);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void FolderMoveOverlappingSubtreesRefuseOnlyTheLaterAdmittedSource(bool reverse)
    {
        var refs = reverse ? new[] { Ref(Child), Ref(Source) } : [Ref(Source), Ref(Child)];
        var plans = GatewayEwsFolderMovePlan.Build(Request(refs, Ref(Target)), new(Account, "s10", Folders()), Profile.Username,
            "ErrorFolderNotFound", out _);
        Assert.IsNull(plans[0].Code);
        Assert.AreEqual("ErrorInvalidRequest", plans[1].Code, StringComparer.Ordinal);
    }

    [TestMethod]
    public void FolderMoveDuplicatesAndIncomingNameCollisionsKeepOrderedSlots()
    {
        var folders = Folders();
        folders[3] = folders[3] with { Name = "SOURCE" };
        var plans = GatewayEwsFolderMovePlan.Build(Request([Ref(Source), Ref(Source), Ref(Other)], Ref(Target)),
            new(Account, "s10", folders), Profile.Username, "ErrorFolderNotFound", out var target);
        Assert.AreEqual(Target, target);
        Assert.IsNull(plans[0].Code);
        Assert.AreEqual("ErrorInvalidRequest", plans[1].Code, StringComparer.Ordinal);
        Assert.AreEqual("ErrorFolderExists", plans[2].Code, StringComparer.Ordinal);
    }

    [TestMethod]
    [DataRow("leaf")]
    [DataRow("depth")]
    public void FolderMoveCannotBypassNativeHierarchyBounds(string bound)
    {
        var folders = Folders().ToList();
        if (bound is "leaf") folders[0] = folders[0] with { Name = new string('x', 101) };
        else
        {
            var parent = Target;
            for (var index = 0; index < 48; index++)
            {
                var id = Guid.NewGuid();
                folders.Add(new(id, "Level", parent, null, 0, false, 0, 0, 0, 0, false));
                parent = id;
            }
            // Moving source plus child below a 49-level target exceeds 50.
            var plans = GatewayEwsFolderMovePlan.Build(Request([Ref(Source)], Ref(parent)), new(Account, "s10", folders),
                Profile.Username, "ErrorFolderNotFound", out _);
            Assert.AreEqual("ErrorInvalidFolderName", plans[0].Code, StringComparer.Ordinal);
            return;
        }
        Assert.AreEqual("ErrorInvalidFolderName", GatewayEwsFolderMovePlan.Build(Request([Ref(Source)], Ref(Target)),
            new(Account, "s10", folders), Profile.Username, "ErrorFolderNotFound", out _)[0].Code, StringComparer.Ordinal);
    }

    [TestMethod]
    [DataRow("valid")]
    [DataRow("unchanged-state")]
    [DataRow("wrong-state")]
    [DataRow("reordered")]
    [DataRow("wrong-id")]
    [DataRow("missing-row")]
    [DataRow("extra-row")]
    [DataRow("extra-field")]
    [DataRow("known-entity")]
    [DataRow("both-outcomes")]
    [DataRow("invalid-failure")]
    public void FolderMoveTypedRepliesRequireExactOrderedTargetsAndAdvancingSuccess(string mode)
    {
        var command = Command();
        var result = new MailFolderMutationResult(MailFolderMutationStatus.Ok, "s10", "s11", [],
            [new($"M{Source:N}", Source, null), new($"M{Other:N}", Other, null)], []);
        var data = (JsonObject)JsonSerializer.SerializeToNode(result, JsonSerializerOptions.Web)!;
        var rows = (JsonArray)data["updated"]!;
        if (mode is "unchanged-state") data["newState"] = "s10";
        if (mode is "wrong-state") data["oldState"] = "s9";
        if (mode is "reordered") { var first = rows[0]!.DeepClone(); rows[0] = rows[1]!.DeepClone(); rows[1] = first; }
        if (mode is "wrong-id") rows[0]!["folderId"] = Target;
        if (mode is "missing-row") rows.RemoveAt(0);
        if (mode is "extra-row") rows.Add(rows[0]!.DeepClone());
        if (mode is "extra-field") rows[0]!["extra"] = true;
        if (mode is "both-outcomes" or "invalid-failure") rows[0]!["failure"] = JsonSerializer.SerializeToNode(new MailFolderMutationFailure(
            mode is "invalid-failure" ? (MailFolderMutationError)99 : MailFolderMutationError.Forbidden), JsonSerializerOptions.Web);
        var known = new Dictionary<string, string>(StringComparer.Ordinal);
        if (mode is "known-entity") known.Add("unexpected", $"M{Target:N}");
        var reply = new MailOperationResult(new(MailOperationKind.MutateFolders, ApplicationValueCodec.Encode(data)), known, Profile);
        if (mode is "valid") Assert.AreEqual(Source, GatewayEwsFolderMove.Decode(reply, command).Updated[0].FolderId);
        else Assert.Throws<InvalidOperationException>(() => GatewayEwsFolderMove.Decode(reply, command));
    }

    [TestMethod]
    [DataRow(MailFolderMutationStatus.AccountNotFound, "ErrorFolderNotFound")]
    [DataRow(MailFolderMutationStatus.StateMismatch, "ErrorIrresolvableConflict")]
    public async Task FolderMoveWholeOperationRefusalsCannotInventFolders(MailFolderMutationStatus status, string code)
    {
        var transport = new Transport(new(status, null, null, [], [], []));
        var xml = await GatewayEwsFolderMove.ExecuteAsync(new(transport, new()), Authentication, Profile, Account,
            Request([Ref(Source)], Ref(Target)), new(Account, "s10", Folders()), "ErrorFolderNotFound", CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(code, XDocument.Parse(xml).Descendants(GatewayEwsSoap.Messages + "ResponseCode").Single().Value, StringComparer.Ordinal);
        Assert.IsEmpty(XDocument.Parse(xml).Descendants(GatewayEwsSoap.Types + "FolderId"));
        Assert.AreEqual("s10", transport.Command!.IfInState, StringComparer.Ordinal);
        Assert.IsFalse(transport.Command.RemoveEmailsOnDestroy);
        Assert.IsEmpty(transport.Command.Creates);
        Assert.IsEmpty(transport.Command.Destroys);
        Assert.AreEqual(MailFolderFields.Parent, transport.Command.Updates[0].Patch.Fields);
        Assert.AreEqual($"M{Target:N}", transport.Command.Updates[0].Patch.Values!.ParentReference, StringComparer.Ordinal);
    }

    [TestMethod]
    public async Task FolderMoveSetLimitRefusesBeforeMutationTransport()
    {
        var transport = new Transport(new(MailFolderMutationStatus.Ok, "s10", "s11", [], [], []));
        var profile = Profile with { Limits = Profile.Limits with { MaxObjectsInSet = 1 } };
        var error = await Assert.ThrowsAsync<GatewayEwsRequestException>(() => GatewayEwsFolderMove.ExecuteAsync(new(transport, new()), Authentication,
            profile, Account, Request([Ref(Source), Ref(Other)], Ref(Target)), new(Account, "s10", Folders()), "ErrorFolderNotFound", CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual("ErrorExceededFindCountLimit", error.Code, StringComparer.Ordinal);
        Assert.IsNull(transport.Command);
    }

    [TestMethod]
    [DataRow(mk8.email.Infrastructure.Models.FolderDB.MaximumLeafNameOctets,
        mk8.email.Infrastructure.Models.FolderDB.MaximumHierarchyDepth, mk8.email.Infrastructure.Models.FolderDB.MaximumStoredNameLength)]
    public void FolderMoveHierarchyLimitsMatchTheUnchangedNativeModel(int leafOctets, int depth, int pathLength)
    {
        Assert.AreEqual(100, leafOctets);
        Assert.AreEqual(50, depth);
        Assert.AreEqual(5049, pathLength);
        var folders = Folders();
        folders[0] = folders[0] with { Name = new string('x', leafOctets) };
        var plans = GatewayEwsFolderMovePlan.Build(Request([Ref(Source)], Ref(Target)), new(Account, "s10", folders),
            Profile.Username, "ErrorFolderNotFound", out _);
        Assert.IsNull(plans[0].Code);
    }

    private static MailFolderSnapshot[] Folders() =>
    [new(Source, "Source", null, null, 0, false, 0, 0, 0, 0, false), new(Child, "Child", Source, null, 0, false, 0, 0, 0, 0, false),
        new(Target, "Target", null, null, 0, false, 0, 0, 0, 0, false), new(Other, "Other", null, null, 0, false, 0, 0, 0, 0, false)];
    private static GatewayEwsFolderReference Ref(Guid id) => new(GatewayEwsFolderIdCodec.Encode(Account, id), false, null, null);
    private static GatewayEwsRequest Request(IReadOnlyList<GatewayEwsFolderReference> refs, GatewayEwsFolderReference target) =>
        new("MoveFolder", new HashSet<string>(StringComparer.Ordinal), refs, false, 0, 0, false, Destination: target);
    private static MailFolderMutationCommand Command() => new(Account, "s10", false, [],
        [new($"M{Source:N}", new(MailFolderFields.Parent, new(string.Empty, $"M{Target:N}", null, 0, false), [], null)),
            new($"M{Other:N}", new(MailFolderFields.Parent, new(string.Empty, $"M{Target:N}", null, 0, false), [], null))], []);
    private static MemoryStream Body(string target, string refs) => new(Encoding.UTF8.GetBytes(Envelope($"<m:ToFolderId>{target}</m:ToFolderId><m:FolderIds>{refs}</m:FolderIds>")));
    private static string Envelope(string fields) => $"<s:Envelope xmlns:s='{GatewayEwsSoap.Soap}' xmlns:m='{GatewayEwsSoap.Messages}' xmlns:t='{GatewayEwsSoap.Types}'><s:Body><m:MoveFolder>{fields}</m:MoveFolder></s:Body></s:Envelope>";

    private sealed class Transport(MailFolderMutationResult result) : IGatewayApplicationTransport
    {
        public MailFolderMutationCommand? Command { get; private set; }
        public Task<TResponse> SendAsync<TRequest, TResponse>(string protocol, string operation, TRequest request, CancellationToken cancellationToken = default)
        {
            Assert.AreEqual("ews", protocol, StringComparer.Ordinal);
            Assert.AreEqual(ApplicationOperations.MailOperationExecute, operation, StringComparer.Ordinal);
            var message = (MailOperationApplicationRequest)(object)request!;
            Assert.AreEqual(MailOperationKind.MutateFolders, message.Command.Operation);
            Command = message.Command.Arguments.Deserialize<MailFolderMutationCommand>(JsonSerializerOptions.Web)!;
            var reply = new JmapApplicationResult(JmapApplicationOutcomes.Ok, OperationResult: new(new(MailOperationKind.MutateFolders,
                ApplicationValueCodec.Encode(JsonSerializer.SerializeToNode(result, JsonSerializerOptions.Web))), new Dictionary<string, string>(StringComparer.Ordinal), Profile));
            return Task.FromResult((TResponse)(object)reply);
        }
    }
}
