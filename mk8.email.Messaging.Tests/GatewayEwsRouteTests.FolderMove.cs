using System.Net;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using mk8.email.Application.Interfaces;
using mk8.email.Contracts.Messaging;
using mk8.email.Gateway.Protocols.Ews;
using CaptureFixture = mk8.email.Messaging.Tests.GatewayHttpCaptureBoundaryTests.CaptureFixture;

namespace mk8.email.Messaging.Tests;

internal sealed partial class GatewayEwsRouteTests
{
    private static readonly string[] MoveFolderMixedCodes = ["ErrorAccessDenied", "NoError", "ErrorInvalidRequest", "ErrorInvalidRequest"];
    private static readonly string[] MoveFolderIncomingCollisionCodes = ["NoError", "ErrorFolderExists"];
    private static readonly string[] MoveFolderExistingCollisionCodes = ["ErrorFolderExists"];
    private static readonly string[] MoveFolderBatchCodes = ["NoError", "NoError"];
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task FolderMoveRelocatesAnEntireSubtreeWithoutChangingItemsOrStableIdentities(bool disabledJmap)
    {
        var fixture = await CaptureFixture.CreateAsync(ordinaryClient: true, mailFolders: true, disableJmap: disabledJmap).ConfigureAwait(false);
        await using var lifetime = fixture.ConfigureAwait(false);
        var message = await SeedItemAsync(fixture, GatewayEwsFixtureDomain.GrandchildId).ConfigureAwait(false);
        var blob = await ItemBlobReferenceAsync(fixture, message).ConfigureAwait(false);
        Authenticate(fixture);
        var before = await ReadKeyAsync(fixture, FolderId(GatewayEwsFixtureDomain.ChildId)).ConfigureAwait(false);
        var reply = await WriteAsync(fixture, "MoveFolder", MoveFolderFields("<t:DistinguishedFolderId Id='drafts'/>",
            $"<t:FolderId Id='{FolderId(GatewayEwsFixtureDomain.ChildId)}' ChangeKey='{before}'/>")).ConfigureAwait(false);
        AssertCode(reply, "NoError");
        var returned = reply.Descendants(Types + "FolderId").Single();
        Assert.AreEqual(FolderId(GatewayEwsFixtureDomain.ChildId), (string?)returned.Attribute("Id"), StringComparer.Ordinal);
        Assert.AreNotEqual(before, (string?)returned.Attribute("ChangeKey"), StringComparer.Ordinal);
        var database = Context(fixture);
        await using var databaseLifetime = database.ConfigureAwait(false);
        Assert.AreEqual("Drafts/A & B", (await database.Folders.SingleAsync(row => row.Id == GatewayEwsFixtureDomain.ChildId).ConfigureAwait(false)).Name, StringComparer.Ordinal);
        Assert.AreEqual("Drafts/A & B/Deep", (await database.Folders.SingleAsync(row => row.Id == GatewayEwsFixtureDomain.GrandchildId).ConfigureAwait(false)).Name, StringComparer.Ordinal);
        Assert.AreEqual(GatewayEwsFixtureDomain.GrandchildId, (await database.Emails.SingleAsync(row => row.Id == message).ConfigureAwait(false)).FolderId);
        Assert.AreEqual(0, await database.ExpungedUids.CountAsync().ConfigureAwait(false));
        Assert.AreEqual(blob, await ItemBlobReferenceAsync(fixture, message).ConfigureAwait(false));
        Assert.IsTrue(await fixture.BlobExistsAsync(blob.ObjectName).ConfigureAwait(false));
        await AssertFolderAsync(fixture, FolderId(GatewayEwsFixtureDomain.GrandchildId), "Deep", (string)returned.Attribute("ChangeKey")!).ConfigureAwait(false);
        if (!disabledJmap) await AssertJmapFolderAsync(fixture, GatewayEwsFixtureDomain.ChildId, "A & B").ConfigureAwait(false);
    }

    [TestMethod]
    [DataRow("<t:DistinguishedFolderId Id='msgfolderroot'/>")]
    [DataRow("opaque-root")]
    public async Task FolderMoveAdmitsOwnedRootDestinations(string target)
    {
        var fixture = await CaptureFixture.CreateAsync(mailFolders: true).ConfigureAwait(false);
        await using var lifetime = fixture.ConfigureAwait(false);
        Authenticate(fixture);
        if (target is "opaque-root") target = $"<t:FolderId Id='{FolderId(Guid.Empty)}'/>";
        AssertCode(await WriteAsync(fixture, "MoveFolder", MoveFolderFields(target,
            $"<t:FolderId Id='{FolderId(GatewayEwsFixtureDomain.GrandchildId)}'/>")).ConfigureAwait(false), "NoError");
        var database = Context(fixture);
        await using var databaseLifetime = database.ConfigureAwait(false);
        Assert.AreEqual("Deep", (await database.Folders.SingleAsync(row => row.Id == GatewayEwsFixtureDomain.GrandchildId).ConfigureAwait(false)).Name, StringComparer.Ordinal);
    }

    [TestMethod]
    [DataRow("self", "ErrorInvalidRequest")]
    [DataRow("descendant", "ErrorInvalidRequest")]
    [DataRow("same-parent", "ErrorInvalidRequest")]
    [DataRow("protected", "ErrorMoveDistinguishedFolder")]
    [DataRow("root", "ErrorMoveDistinguishedFolder")]
    [DataRow("foreign-source", "ErrorAccessDenied")]
    [DataRow("forged-source", "ErrorFolderNotFound")]
    [DataRow("foreign-target", "ErrorAccessDenied")]
    [DataRow("forged-target", "ErrorFolderNotFound")]
    [DataRow("alias-target", "ErrorAccessDenied")]
    [DataRow("bad-key", "ErrorInvalidChangeKey")]
    [DataRow("stale-key", "ErrorIrresolvableConflict")]
    public async Task FolderMoveInvalidAuthorityHierarchyOrVersionNeverDispatchesAMutation(string kind, string code)
    {
        var fixture = await CaptureFixture.CreateAsync(mailFolders: true).ConfigureAwait(false);
        await using var lifetime = fixture.ConfigureAwait(false);
        Authenticate(fixture);
        var source = $"<t:FolderId Id='{FolderId(GatewayEwsFixtureDomain.ChildId)}'/>";
        var target = "<t:DistinguishedFolderId Id='drafts'/>";
        if (kind is "self") target = source;
        if (kind is "descendant") target = $"<t:FolderId Id='{FolderId(GatewayEwsFixtureDomain.GrandchildId)}'/>";
        if (kind is "same-parent") target = "<t:DistinguishedFolderId Id='inbox'/>";
        if (kind is "protected") source = "<t:DistinguishedFolderId Id='inbox'/>";
        if (kind is "root") source = $"<t:FolderId Id='{FolderId(Guid.Empty)}'/>";
        var foreign = $"<t:FolderId Id='{GatewayEwsFolderIdCodec.Encode(GatewayEwsFixtureDomain.ForeignAccountId, GatewayEwsFixtureDomain.ForeignFolderId)}'/>";
        if (kind is "foreign-source") source = foreign;
        if (kind is "forged-source") source = $"<t:FolderId Id='{FolderId(GatewayEwsFixtureDomain.ForeignFolderId)}'/>";
        if (kind is "foreign-target") target = foreign;
        if (kind is "forged-target") target = $"<t:FolderId Id='{FolderId(GatewayEwsFixtureDomain.ForeignFolderId)}'/>";
        if (kind is "alias-target") target = "<t:DistinguishedFolderId Id='inbox'><t:Mailbox><t:EmailAddress>alias@example.test</t:EmailAddress></t:Mailbox></t:DistinguishedFolderId>";
        if (kind is "bad-key" or "stale-key") source = $"<t:FolderId Id='{FolderId(GatewayEwsFixtureDomain.ChildId)}' ChangeKey='{(kind is "bad-key" ? "!bad" : "c3RhbGU=")}'/>";
        AssertCode(await WriteAsync(fixture, "MoveFolder", MoveFolderFields(target, source)).ConfigureAwait(false), code);
        await AssertReadOperationsAsync(fixture).ConfigureAwait(false);
        var database = Context(fixture);
        await using var databaseLifetime = database.ConfigureAwait(false);
        Assert.AreEqual("INBOX/A & B", (await database.Folders.SingleAsync(row => row.Id == GatewayEwsFixtureDomain.ChildId).ConfigureAwait(false)).Name, StringComparer.Ordinal);
    }

    [TestMethod]
    public async Task FolderMoveMixedDuplicatesAndOverlapsKeepOriginalResultOrder()
    {
        var fixture = await CaptureFixture.CreateAsync(mailFolders: true).ConfigureAwait(false);
        await using var lifetime = fixture.ConfigureAwait(false);
        Authenticate(fixture);
        var owned = $"<t:FolderId Id='{FolderId(GatewayEwsFixtureDomain.ChildId)}'/>";
        var source = $"<t:FolderId Id='{GatewayEwsFolderIdCodec.Encode(GatewayEwsFixtureDomain.ForeignAccountId, GatewayEwsFixtureDomain.ForeignFolderId)}'/>"
            + owned + owned + $"<t:FolderId Id='{FolderId(GatewayEwsFixtureDomain.GrandchildId)}'/>";
        var reply = await WriteAsync(fixture, "MoveFolder", MoveFolderFields("<t:DistinguishedFolderId Id='drafts'/>", source)).ConfigureAwait(false);
        CollectionAssert.AreEqual(MoveFolderMixedCodes,
            reply.Descendants(Messages + "ResponseCode").Select(row => row.Value).ToArray());
        Assert.HasCount(1, reply.Descendants(Types + "FolderId"));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task FolderMoveSiblingCollisionIsCaseInsensitiveAndNoOpOnRefusal(bool incoming)
    {
        var fixture = await CaptureFixture.CreateAsync(mailFolders: true).ConfigureAwait(false);
        await using var lifetime = fixture.ConfigureAwait(false);
        Authenticate(fixture);
        var parent = incoming ? "<t:DistinguishedFolderId Id='msgfolderroot'/>" : "<t:DistinguishedFolderId Id='drafts'/>";
        var created = await WriteAsync(fixture, "CreateFolder", CreateFields(parent, "A &amp; B")).ConfigureAwait(false);
        var second = (string)created.Descendants(Types + "FolderId").Single().Attribute("Id")!;
        var refs = $"<t:FolderId Id='{FolderId(GatewayEwsFixtureDomain.ChildId)}'/>" + (incoming ? $"<t:FolderId Id='{second}'/>" : "");
        var reply = await WriteAsync(fixture, "MoveFolder", MoveFolderFields("<t:DistinguishedFolderId Id='drafts'/>", refs)).ConfigureAwait(false);
        CollectionAssert.AreEqual(incoming ? MoveFolderIncomingCollisionCodes : MoveFolderExistingCollisionCodes,
            reply.Descendants(Messages + "ResponseCode").Select(row => row.Value).ToArray());
    }

    [TestMethod]
    [DataRow("<t:FolderId Id='target' ChangeKey='czEw'/>", "<t:FolderId Id='source'/>", "ErrorInvalidRequest")]
    [DataRow("<t:FolderId Id='target'/>", "<t:ItemId Id='source'/>", "ErrorSchemaValidation")]
    [DataRow("<t:FolderId Id='target'/>", "<?forbidden x?><t:FolderId Id='source'/>", "ErrorSchemaValidation")]
    public async Task FolderMoveUnsupportedShapesAreRecordedWithoutWorkerDispatch(string target, string sources, string code)
    {
        var fixture = await CaptureFixture.CreateAsync(mailFolders: true).ConfigureAwait(false);
        await using var lifetime = fixture.ConfigureAwait(false);
        Authenticate(fixture);
        using var content = XmlContent(MutationRequest("MoveFolder", MoveFolderFields(target, sources)));
        using var response = await fixture.Client.PostAsync(new Uri(CanonicalPath, UriKind.Relative), content).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.InternalServerError, response.StatusCode);
        AssertFault(body, code);
        await fixture.AssertNoWorkerRequestsAsync().ConfigureAwait(false);
        await fixture.AssertRecordedResponseAsync("ews", CanonicalPath, 500, body, rejection: false).ConfigureAwait(false);
    }

    [TestMethod]
    [DataRow("rename")]
    [DataRow("scope")]
    [DataRow("deleted-target")]
    public async Task FolderMoveCommittedRaceRefusesStaleStateOrLostCurrentOwnership(string change)
    {
        CaptureFixture? fixture = null;
        var injections = 0;
        var targetId = Guid.Empty;
        fixture = await CaptureFixture.CreateAsync(mailFolders: true, decorateDispatcher: inner => new FindRaceDispatcher(inner, MailOperationKind.ReadFolders,
            async token =>
            {
                if (Interlocked.CompareExchange(ref injections, 1, 0) != 0) return;
                var database = Context(fixture!);
                await using var databaseLifetime = database.ConfigureAwait(false);
                var changedId = change is "deleted-target" ? targetId : GatewayEwsFixtureDomain.GrandchildId;
                var row = await database.Folders.SingleAsync(row => row.Id == changedId, token).ConfigureAwait(false);
                if (change is "scope") row.InboxId = GatewayEwsFixtureDomain.ForeignAccountId;
                else if (change is "deleted-target") database.Folders.Remove(row);
                else row.Name = "INBOX/A & B/Raced";
                await database.SaveChangesAsync(token).ConfigureAwait(false);
            })).ConfigureAwait(false);
        await using var lifetime = fixture.ConfigureAwait(false);
        if (change is "deleted-target")
        {
            var database = Context(fixture);
            await using var databaseLifetime = database.ConfigureAwait(false);
            // Seed an ordinary independent target; protected Drafts must survive.
            var seededTarget = new mk8.email.Infrastructure.Models.FolderDB { Id = Guid.CreateVersion7(), InboxId = GatewayEwsFixtureDomain.AccountId, Name = "RaceTarget" };
            await database.Folders.AddAsync(seededTarget).ConfigureAwait(false);
            await database.SaveChangesAsync().ConfigureAwait(false);
            targetId = seededTarget.Id;
        }
        Authenticate(fixture);
        var target = change is "deleted-target" ? $"<t:FolderId Id='{FolderId(targetId)}'/>" : "<t:DistinguishedFolderId Id='drafts'/>";
        var source = GatewayEwsFixtureDomain.GrandchildId;
        var reply = await WriteAsync(fixture, "MoveFolder", MoveFolderFields(target, $"<t:FolderId Id='{FolderId(source)}'/>")).ConfigureAwait(false);
        Assert.AreEqual(1, injections);
        // Direct scope transfer need not advance the old account's cache state:
        // the independent CURRENT-account folder lookup must still refuse it.
        AssertCode(reply, change is "scope" ? "ErrorFolderNotFound" : "ErrorIrresolvableConflict");
        var verify = Context(fixture);
        await using var verifyLifetime = verify.ConfigureAwait(false);
        var current = await verify.Folders.SingleAsync(row => row.Id == source).ConfigureAwait(false);
        Assert.AreEqual(change is "scope" ? GatewayEwsFixtureDomain.ForeignAccountId : GatewayEwsFixtureDomain.AccountId, current.InboxId);
        Assert.AreEqual(change is "rename" ? "INBOX/A & B/Raced" : "INBOX/A & B/Deep", current.Name, StringComparer.Ordinal);
    }

    [TestMethod]
    [DataRow(0L)]
    [DataRow(1L)]
    public async Task FolderMoveJournalFailureWithholdsSuccessWithoutInventingRollback(long sequence)
    {
        var fixture = await CaptureFixture.CreateAsync(failSequence: sequence, mailFolders: true).ConfigureAwait(false);
        await using var lifetime = fixture.ConfigureAwait(false);
        Authenticate(fixture);
        using var content = XmlContent(MutationRequest("MoveFolder", MoveFolderFields("<t:DistinguishedFolderId Id='drafts'/>",
            $"<t:FolderId Id='{FolderId(GatewayEwsFixtureDomain.ChildId)}'/>")));
        using var response = await fixture.Client.PostAsync(new Uri(CanonicalPath, UriKind.Relative), content).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        AssertFault(body, "ErrorServerBusy");
        Assert.IsFalse(body.Contains("NoError", StringComparison.Ordinal));
        var database = Context(fixture);
        await using var databaseLifetime = database.ConfigureAwait(false);
        Assert.AreEqual(sequence == 0 ? "INBOX/A & B" : "Drafts/A & B", (await database.Folders.SingleAsync(row => row.Id == GatewayEwsFixtureDomain.ChildId).ConfigureAwait(false)).Name, StringComparer.Ordinal);
        await fixture.AssertPresentationRecordCountAsync(sequence).ConfigureAwait(false);
        if (sequence == 0) await fixture.AssertNoWorkerRequestsAsync().ConfigureAwait(false);
    }

    [TestMethod]
    public async Task FolderMoveExactReceiptReplayDoesNotRelocateTwice()
    {
        var replay = new FolderMoveReplay();
        var fixture = await CaptureFixture.CreateAsync(mailFolders: true, decorateDispatcher: inner => new FolderMoveReplayDispatcher(inner, replay)).ConfigureAwait(false);
        await using var lifetime = fixture.ConfigureAwait(false);
        Authenticate(fixture);
        AssertCode(await WriteAsync(fixture, "MoveFolder", MoveFolderFields("<t:DistinguishedFolderId Id='drafts'/>",
            $"<t:FolderId Id='{FolderId(GatewayEwsFixtureDomain.ChildId)}'/>")).ConfigureAwait(false), "NoError");
        Assert.AreEqual(1, replay.Count);
        var database = Context(fixture);
        await using var databaseLifetime = database.ConfigureAwait(false);
        Assert.AreEqual("Drafts/A & B/Deep", (await database.Folders.SingleAsync(row => row.Id == GatewayEwsFixtureDomain.GrandchildId).ConfigureAwait(false)).Name, StringComparer.Ordinal);
    }

    [TestMethod]
    public async Task FolderMoveTwoDisjointSubtreesUseOneGuardedDomainBatch()
    {
        var fixture = await CaptureFixture.CreateAsync(mailFolders: true, additionalFolders: 1).ConfigureAwait(false);
        await using var lifetime = fixture.ConfigureAwait(false);
        var database = Context(fixture);
        await using var databaseLifetime = database.ConfigureAwait(false);
        var other = await database.Folders.Where(row => row.Name == "Extra-0000").Select(row => row.Id).SingleAsync().ConfigureAwait(false);
        Authenticate(fixture);
        var reply = await WriteAsync(fixture, "MoveFolder", MoveFolderFields("<t:DistinguishedFolderId Id='drafts'/>",
            $"<t:FolderId Id='{FolderId(other)}'/><t:FolderId Id='{FolderId(GatewayEwsFixtureDomain.ChildId)}'/>")).ConfigureAwait(false);
        CollectionAssert.AreEqual(MoveFolderBatchCodes, reply.Descendants(Messages + "ResponseCode").Select(row => row.Value).ToArray());
        CollectionAssert.AreEqual(new[] { FolderId(other), FolderId(GatewayEwsFixtureDomain.ChildId) },
            reply.Descendants(Types + "FolderId").Select(row => (string)row.Attribute("Id")!).ToArray());
        Assert.HasCount(1, reply.Descendants(Types + "FolderId").Select(row => (string)row.Attribute("ChangeKey")!).Distinct(StringComparer.Ordinal));
        database.ChangeTracker.Clear();
        Assert.AreEqual("Drafts/Extra-0000", (await database.Folders.SingleAsync(row => row.Id == other).ConfigureAwait(false)).Name, StringComparer.Ordinal);
        Assert.AreEqual("Drafts/A & B/Deep", (await database.Folders.SingleAsync(row => row.Id == GatewayEwsFixtureDomain.GrandchildId).ConfigureAwait(false)).Name, StringComparer.Ordinal);
        await fixture.AssertWorkerOperationsAsync(ApplicationOperations.JmapProfileGet, ApplicationOperations.MailOperationExecute,
            ApplicationOperations.MailOperationExecute, ApplicationOperations.MailOperationExecute).ConfigureAwait(false);
    }

    [TestMethod]
    [DataRow("none", 401)]
    [DataRow("bearer", 401)]
    [DataRow("wrong", 401)]
    [DataRow("plaintext", 403)]
    public async Task FolderMoveRefusesUnsupportedOrWrongCredentials(string mode, int status)
    {
        var fixture = await CaptureFixture.CreateAsync(ordinaryClient: true, mailFolders: true).ConfigureAwait(false);
        await using var lifetime = fixture.ConfigureAwait(false);
        Authenticate(fixture);
        if (mode is "none") fixture.Client.DefaultRequestHeaders.Authorization = null;
        if (mode is "bearer") fixture.Client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "test-only-token");
        if (mode is "wrong") Authenticate(fixture, "wrong");
        if (mode is "plaintext") fixture.Client.DefaultRequestHeaders.Remove("X-Forwarded-Proto");
        using var content = XmlContent(MutationRequest("MoveFolder", MoveFolderFields("<t:DistinguishedFolderId Id='drafts'/>",
            $"<t:FolderId Id='{FolderId(GatewayEwsFixtureDomain.ChildId)}'/>")));
        using var response = await fixture.Client.PostAsync(new Uri(CanonicalPath, UriKind.Relative), content).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.AreEqual(status, (int)response.StatusCode);
        AssertFault(body, "ErrorAccessDenied");
        if (mode is "wrong") await fixture.AssertWorkerOperationsAsync(ApplicationOperations.JmapProfileGet).ConfigureAwait(false);
        else await fixture.AssertNoWorkerRequestsAsync().ConfigureAwait(false);
        await fixture.AssertRecordedResponseAsync("ews", CanonicalPath, status, body, rejection: false).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task FolderMoveTooManyReferencesAreJournaledBeforeWorker()
    {
        var fixture = await CaptureFixture.CreateAsync(mailFolders: true).ConfigureAwait(false);
        await using var lifetime = fixture.ConfigureAwait(false);
        Authenticate(fixture);
        using var content = XmlContent(MutationRequest("MoveFolder", MoveFolderFields("<t:DistinguishedFolderId Id='drafts'/>",
            string.Concat(Enumerable.Repeat($"<t:FolderId Id='{FolderId(GatewayEwsFixtureDomain.ChildId)}'/>", 33)))));
        using var response = await fixture.Client.PostAsync(new Uri(CanonicalPath, UriKind.Relative), content).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.InternalServerError, response.StatusCode);
        AssertFault(body, "ErrorExceededFindCountLimit");
        await fixture.AssertNoWorkerRequestsAsync().ConfigureAwait(false);
        await fixture.AssertRecordedResponseAsync("ews", CanonicalPath, 500, body, rejection: false).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task FolderMovePhysicalDestinationIsResolvedFromTheOwnedGraph()
    {
        var fixture = await CaptureFixture.CreateAsync(mailFolders: true).ConfigureAwait(false);
        await using var lifetime = fixture.ConfigureAwait(false);
        var database = Context(fixture);
        await using var databaseLifetime = database.ConfigureAwait(false);
        var target = await database.Folders.Where(row => row.Name == "Drafts").Select(row => row.Id).SingleAsync().ConfigureAwait(false);
        Authenticate(fixture);
        AssertCode(await WriteAsync(fixture, "MoveFolder", MoveFolderFields($"<t:FolderId Id='{FolderId(target)}'/>",
            $"<t:FolderId Id='{FolderId(GatewayEwsFixtureDomain.ChildId)}'/>")).ConfigureAwait(false), "NoError");
        database.ChangeTracker.Clear();
        Assert.AreEqual("Drafts/A & B/Deep", (await database.Folders.SingleAsync(row => row.Id == GatewayEwsFixtureDomain.GrandchildId).ConfigureAwait(false)).Name, StringComparer.Ordinal);
    }

    [TestMethod]
    public async Task FolderMoveProtectedDescendantCannotBeMovedThroughAnOrdinaryAncestor()
    {
        var fixture = await CaptureFixture.CreateAsync(mailFolders: true).ConfigureAwait(false);
        await using var lifetime = fixture.ConfigureAwait(false);
        var database = Context(fixture);
        await using var databaseLifetime = database.ConfigureAwait(false);
        var protectedFolder = await database.Folders.SingleAsync(row => row.Name == "Sent").ConfigureAwait(false);
        protectedFolder.Name = "INBOX/A & B/Sent";
        await database.SaveChangesAsync().ConfigureAwait(false);
        Authenticate(fixture);
        AssertCode(await WriteAsync(fixture, "MoveFolder", MoveFolderFields("<t:DistinguishedFolderId Id='drafts'/>",
            $"<t:FolderId Id='{FolderId(GatewayEwsFixtureDomain.ChildId)}'/>")).ConfigureAwait(false), "ErrorAccessDenied");
        await AssertReadOperationsAsync(fixture).ConfigureAwait(false);
        database.ChangeTracker.Clear();
        Assert.AreEqual("INBOX/A & B/Sent", (await database.Folders.SingleAsync(row => row.Id == protectedFolder.Id).ConfigureAwait(false)).Name, StringComparer.Ordinal);
    }

    private static string MoveFolderFields(string target, string sources) => $"<m:ToFolderId>{target}</m:ToFolderId><m:FolderIds>{sources}</m:FolderIds>";
    private sealed class FolderMoveReplay { internal int Count { get; set; } }
    private sealed class FolderMoveReplayDispatcher(IApplicationRequestDispatcher inner, FolderMoveReplay replay) : IApplicationRequestDispatcher
    {
        public async Task<ApplicationResponse> DispatchAsync(ApplicationRequest request, CancellationToken cancellationToken = default)
        {
            var response = await inner.DispatchAsync(request, cancellationToken).ConfigureAwait(false);
            if (string.Equals(request.Operation, ApplicationOperations.MailOperationExecute, StringComparison.Ordinal)
                && JsonSerializer.Deserialize<MailOperationApplicationRequest>(request.Payload, JsonSerializerOptions.Web)!.Command.Operation == MailOperationKind.MutateFolders)
            {
                var again = await inner.DispatchAsync(request, cancellationToken).ConfigureAwait(false);
                CollectionAssert.AreEqual(response.Payload.ToArray(), again.Payload.ToArray());
                replay.Count++;
            }
            return response;
        }
    }
}
