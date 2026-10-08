using System.Net;
using System.Text;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using mk8.email.Application.Services;
using mk8.email.Gateway.Protocols.Ews;
using mk8.email.Infrastructure.Data;
using CaptureFixture = mk8.email.Messaging.Tests.GatewayHttpCaptureBoundaryTests.CaptureFixture;

namespace mk8.email.Messaging.Tests;

internal sealed partial class GatewayEwsRouteTests
{
    private static readonly string[] MoveCodes = ["ErrorAccessDenied", "ErrorItemNotFound", "NoError", "ErrorItemNotFound"];
    private static readonly string[] MoveKeywords = ["custom-keyword"];

    [TestMethod]
    [DataRow(CanonicalPath, false)]
    [DataRow(CanonicalPath, true)]
    [DataRow("/eWs/EXCHANGE.ASMX/", false)]
    [DataRow("/eWs/EXCHANGE.ASMX/", true)]
    public async Task MoveItemPreservesNativeAzureContentButExpungesOriginal(string path, bool disabledJmap)
    {
        var fixture = await CaptureFixture.CreateAsync(ordinaryClient: true, mailFolders: true, disableJmap: disabledJmap).ConfigureAwait(false);
        await using var lifetime = fixture.ConfigureAwait(false);
        var source = await SeedItemAsync(fixture, GatewayEwsFixtureDomain.ChildId, NativeMime).ConfigureAwait(false);
        await SetCopySourceFlagsAsync(fixture, source).ConfigureAwait(false);
        var original = await ItemBlobReferenceAsync(fixture, source).ConfigureAwait(false);
        var before = Context(fixture);
        await using var beforeLifetime = before.ConfigureAwait(false);
        var received = (await before.Emails.AsNoTracking().SingleAsync(email => email.Id == source).ConfigureAwait(false)).ReceivedAt;
        Authenticate(fixture);
        var old = await ReadItemAsync(fixture, source).ConfigureAwait(false);
        var key = (string)old.Element(Types + "ItemId")!.Attribute("ChangeKey")!;
        var xml = await MoveResponseAsync(fixture, $"<t:ItemId Id='{ItemId(source)}' ChangeKey='{key}'/>", path: path).ConfigureAwait(false);
        AssertCode(XDocument.Parse(xml), "NoError");
        var reference = XDocument.Parse(xml).Descendants(Types + "ItemId").Single();
        Assert.IsTrue(GatewayEwsItemIdCodec.TryDecode((string)reference.Attribute("Id")!, out var account, out var moved));
        Assert.AreEqual(GatewayEwsFixtureDomain.AccountId, account);
        Assert.AreNotEqual(source, moved);
        await AssertItemRemovedAsync(fixture, source, original).ConfigureAwait(false);
        await AssertMovedValuesAsync(fixture, moved, received, original.ObjectName).ConfigureAwait(false);
        var current = await ReadItemAsync(fixture, moved).ConfigureAwait(false);
        Assert.AreEqual((string?)reference.Attribute("ChangeKey"), (string?)current.Element(Types + "ItemId")!.Attribute("ChangeKey"), StringComparer.Ordinal);
        await AssertDeletedItemNotReadableAsync(fixture, source).ConfigureAwait(false);
        if (!disabledJmap) await AssertJmapItemRemovedAsync(fixture, source).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task MoveItemRefusesLaterDuplicatesAndPreservesOrderedAuthorityOutcomes()
    {
        var fixture = await CaptureFixture.CreateAsync(mailFolders: true).ConfigureAwait(false);
        await using var lifetime = fixture.ConfigureAwait(false);
        var source = await SeedItemAsync(fixture, GatewayEwsFixtureDomain.ChildId).ConfigureAwait(false);
        var foreign = await SeedItemAsync(fixture, GatewayEwsFixtureDomain.ForeignFolderId).ConfigureAwait(false);
        Authenticate(fixture);
        var refs = $"<t:ItemId Id='{GatewayEwsItemIdCodec.Encode(GatewayEwsFixtureDomain.ForeignAccountId, foreign)}'/>"
            + $"<t:ItemId Id='{ItemId(foreign)}'/><t:ItemId Id='{ItemId(source)}'/><t:ItemId Id='{ItemId(source)}'/>";
        var xml = await MoveResponseAsync(fixture, refs).ConfigureAwait(false);
        CollectionAssert.AreEqual(MoveCodes, XDocument.Parse(xml).Descendants(Messages + "ResponseCode").Select(code => code.Value).ToArray());
        Assert.HasCount(1, XDocument.Parse(xml).Descendants(Types + "ItemId").ToArray());
        await AssertMoveCountsAsync(fixture, source, 1).ConfigureAwait(false);
        var database = Context(fixture);
        await using var databaseLifetime = database.ConfigureAwait(false);
        Assert.IsTrue(await database.Emails.AnyAsync(email => email.Id == foreign).ConfigureAwait(false));
    }

    [TestMethod]
    [DataRow("foreign", "ErrorAccessDenied")]
    [DataRow("forged", "ErrorFolderNotFound")]
    [DataRow("missing", "ErrorFolderNotFound")]
    [DataRow("root", "ErrorAccessDenied")]
    [DataRow("malformed", "ErrorInvalidIdMalformed")]
    [DataRow("same", "ErrorInvalidRequest")]
    public async Task MoveItemRefusedDestinationNeverDestroysOriginal(string kind, string code)
    {
        var fixture = await CaptureFixture.CreateAsync(mailFolders: true).ConfigureAwait(false);
        await using var lifetime = fixture.ConfigureAwait(false);
        var source = await SeedItemAsync(fixture, GatewayEwsFixtureDomain.ChildId).ConfigureAwait(false);
        Authenticate(fixture);
        var target = kind switch
        {
            "foreign" => GatewayEwsFolderIdCodec.Encode(GatewayEwsFixtureDomain.ForeignAccountId, GatewayEwsFixtureDomain.ForeignFolderId),
            "forged" => FolderId(GatewayEwsFixtureDomain.ForeignFolderId),
            "missing" => FolderId(Guid.CreateVersion7()),
            "root" => FolderId(Guid.Empty),
            "same" => FolderId(GatewayEwsFixtureDomain.ChildId),
            _ => "malformed",
        };
        AssertCode(XDocument.Parse(await MoveResponseAsync(fixture, $"<t:ItemId Id='{ItemId(source)}'/>", target: $"<t:FolderId Id='{target}'/>").ConfigureAwait(false)), code);
        await AssertMoveCountsAsync(fixture, source, 0).ConfigureAwait(false);
    }

    [TestMethod]
    [DataRow("false")]
    [DataRow("0")]
    public async Task MoveItemIdentitySuppressionStillCommitsMoveAndExpunge(string boolean)
    {
        var fixture = await CaptureFixture.CreateAsync(mailFolders: true).ConfigureAwait(false);
        await using var lifetime = fixture.ConfigureAwait(false);
        var source = await SeedItemAsync(fixture, GatewayEwsFixtureDomain.ChildId).ConfigureAwait(false);
        Authenticate(fixture);
        var xml = await MoveResponseAsync(fixture, $"<t:ItemId Id='{ItemId(source)}'/>", option: $"<m:ReturnNewItemIds>{boolean}</m:ReturnNewItemIds>").ConfigureAwait(false);
        AssertCode(XDocument.Parse(xml), "NoError");
        Assert.IsFalse(XDocument.Parse(xml).Descendants(Types + "ItemId").Any());
        await AssertMoveCountsAsync(fixture, source, 1).ConfigureAwait(false);
    }

    [TestMethod]
    [DataRow("bad", "ErrorInvalidChangeKey")]
    [DataRow("c3RhbGU=", "ErrorIrresolvableConflict")]
    public async Task MoveItemInvalidOrStaleKeysNeverDestroySources(string key, string code)
    {
        var fixture = await CaptureFixture.CreateAsync(mailFolders: true).ConfigureAwait(false);
        await using var lifetime = fixture.ConfigureAwait(false);
        var source = await SeedItemAsync(fixture, GatewayEwsFixtureDomain.ChildId).ConfigureAwait(false);
        Authenticate(fixture);
        AssertCode(XDocument.Parse(await MoveResponseAsync(fixture, $"<t:ItemId Id='{ItemId(source)}' ChangeKey='{key}'/>").ConfigureAwait(false)), code);
        await AssertMoveCountsAsync(fixture, source, 0).ConfigureAwait(false);
    }

    [TestMethod]
    [DataRow("<t:DistinguishedFolderId Id='inbox'/>")]
    [DataRow("<t:FolderId Id='folder' ChangeKey='czEw'/>")]
    public async Task MoveItemUnsupportedProfileIsJournaledBeforeWorker(string target)
    {
        var fixture = await CaptureFixture.CreateAsync(mailFolders: true).ConfigureAwait(false);
        await using var lifetime = fixture.ConfigureAwait(false);
        Authenticate(fixture);
        using var content = XmlContent(MoveRequest(target, "<t:ItemId Id='opaque'/>"));
        using var response = await fixture.Client.PostAsync(new Uri(CanonicalPath, UriKind.Relative), content).ConfigureAwait(false);
        var xml = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.InternalServerError, response.StatusCode);
        AssertFault(xml, "ErrorInvalidRequest");
        await fixture.AssertNoWorkerRequestsAsync().ConfigureAwait(false);
        await fixture.AssertRecordedResponseAsync("ews", CanonicalPath, 500, xml, rejection: false).ConfigureAwait(false);
    }

    [TestMethod]
    [DataRow("flags")]
    [DataRow("scope")]
    [DataRow("deleted")]
    [DataRow("target-deleted")]
    [DataRow("target-moved")]
    public async Task MoveItemCommittedScopeRacesNeverDestroySources(string change)
    {
        CaptureFixture? fixture = null;
        var source = Guid.Empty;
        fixture = await CaptureFixture.CreateAsync(mailFolders: true, decorateDispatcher: inner => new FindRaceDispatcher(inner,
            mk8.email.Contracts.Messaging.MailOperationKind.ReadMessages, async token =>
            {
                var database = Context(fixture!);
                await using var databaseLifetime = database.ConfigureAwait(false);
                if (change.StartsWith("target-", StringComparison.Ordinal))
                {
                    var folder = await database.Folders.SingleAsync(folder => folder.Id == GatewayEwsFixtureDomain.GrandchildId, token).ConfigureAwait(false);
                    if (change is "target-deleted") database.Folders.Remove(folder);
                    else folder.InboxId = GatewayEwsFixtureDomain.ForeignAccountId;
                }
                else
                {
                    var email = await database.Emails.SingleAsync(email => email.Id == source, token).ConfigureAwait(false);
                    if (change is "scope") email.FolderId = GatewayEwsFixtureDomain.ForeignFolderId;
                    else if (change is "deleted") email.IsDeleted = true;
                    else email.IsRead = true;
                }
                await database.SaveChangesAsync(token).ConfigureAwait(false);
            })).ConfigureAwait(false);
        await using var lifetime = fixture.ConfigureAwait(false);
        source = await SeedItemAsync(fixture, GatewayEwsFixtureDomain.ChildId).ConfigureAwait(false);
        Authenticate(fixture);
        var document = XDocument.Parse(await MoveResponseAsync(fixture, $"<t:ItemId Id='{ItemId(source)}'/>").ConfigureAwait(false));
        Assert.IsTrue(document.Descendants(Messages + "ResponseCode").Single().Value is "ErrorIrresolvableConflict" or "ErrorFolderNotFound");
        await AssertMoveCountsAsync(fixture, source, 0).ConfigureAwait(false);
    }

    [TestMethod]
    [DataRow(0L)]
    [DataRow(1L)]
    public async Task MoveItemJournalFailureWithholdsSuccessEvenAfterCommit(long sequence)
    {
        var fixture = await CaptureFixture.CreateAsync(failSequence: sequence, mailFolders: true).ConfigureAwait(false);
        await using var lifetime = fixture.ConfigureAwait(false);
        var source = await SeedItemAsync(fixture, GatewayEwsFixtureDomain.ChildId).ConfigureAwait(false);
        Authenticate(fixture);
        using var content = XmlContent(MoveRequest($"<t:FolderId Id='{FolderId(GatewayEwsFixtureDomain.GrandchildId)}'/>", $"<t:ItemId Id='{ItemId(source)}'/>"));
        using var response = await fixture.Client.PostAsync(new Uri(CanonicalPath, UriKind.Relative), content).ConfigureAwait(false);
        var xml = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        AssertFault(xml, "ErrorServerBusy");
        Assert.IsFalse(xml.Contains("NoError", StringComparison.Ordinal));
        await fixture.AssertPresentationRecordCountAsync(sequence).ConfigureAwait(false);
        await AssertMoveCountsAsync(fixture, source, (int)sequence).ConfigureAwait(false);
        if (sequence == 0) await fixture.AssertNoWorkerRequestsAsync().ConfigureAwait(false);
    }

    [TestMethod]
    public async Task MoveItemExactWorkerReplayDoesNotRepeatReplacementOrExpunge()
    {
        var replay = new CopyReplay();
        var fixture = await CaptureFixture.CreateAsync(mailFolders: true, decorateDispatcher: inner => new CopyReplayDispatcher(inner, replay)).ConfigureAwait(false);
        await using var lifetime = fixture.ConfigureAwait(false);
        var source = await SeedItemAsync(fixture, GatewayEwsFixtureDomain.ChildId).ConfigureAwait(false);
        Authenticate(fixture);
        AssertCode(XDocument.Parse(await MoveResponseAsync(fixture, $"<t:ItemId Id='{ItemId(source)}'/>").ConfigureAwait(false)), "NoError");
        Assert.AreEqual(1, replay.Count);
        await AssertMoveCountsAsync(fixture, source, 1).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task MoveItemDeleteStageFailureRollsBackBothRowChanges()
    {
        var fixture = await CaptureFixture.CreateAsync(mailFolders: true).ConfigureAwait(false);
        await using var lifetime = fixture.ConfigureAwait(false);
        var source = await SeedItemAsync(fixture, GatewayEwsFixtureDomain.ChildId).ConfigureAwait(false);
        var database = Context(fixture);
        await using var databaseLifetime = database.ConfigureAwait(false);
        await database.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION ews_move_fail_delete() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'owned move deletion fault'; END; $$;
            CREATE TRIGGER ews_move_delete_fault BEFORE DELETE ON emails FOR EACH ROW EXECUTE FUNCTION ews_move_fail_delete();
            """).ConfigureAwait(false);
        Authenticate(fixture);
        using var content = XmlContent(MoveRequest($"<t:FolderId Id='{FolderId(GatewayEwsFixtureDomain.GrandchildId)}'/>", $"<t:ItemId Id='{ItemId(source)}'/>"));
        using var response = await fixture.Client.PostAsync(new Uri(CanonicalPath, UriKind.Relative), content).ConfigureAwait(false);
        var xml = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.InternalServerError, response.StatusCode);
        AssertFault(xml, "ErrorInternalServerError");
        await fixture.AssertRecordedResponseAsync("ews", CanonicalPath, 500, xml, rejection: false).ConfigureAwait(false);
        await AssertMoveCountsAsync(fixture, source, 0).ConfigureAwait(false);
    }

    private static async Task AssertMoveCountsAsync(CaptureFixture fixture, Guid source, int count)
    {
        var database = Context(fixture);
        await using var databaseLifetime = database.ConfigureAwait(false);
        Assert.AreEqual(count == 0, await database.Emails.AnyAsync(email => email.Id == source).ConfigureAwait(false));
        Assert.AreEqual(count, await database.Emails.CountAsync(email => email.FolderId == GatewayEwsFixtureDomain.GrandchildId).ConfigureAwait(false));
        Assert.AreEqual(count, await database.ExpungedUids.CountAsync().ConfigureAwait(false));
    }

    private static async Task AssertMovedValuesAsync(CaptureFixture fixture, Guid moved, DateTime received, string originalName)
    {
        using var scope = fixture.DomainScopes.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
        var item = await database.Emails.SingleAsync(email => email.Id == moved).ConfigureAwait(false);
        Assert.AreEqual(GatewayEwsFixtureDomain.GrandchildId, item.FolderId);
        Assert.IsTrue(item.IsRead && item.IsDraft && item.IsFlagged && item.IsAnswered);
        CollectionAssert.AreEqual(MoveKeywords, item.Keywords!);
        Assert.AreEqual(received, item.ReceivedAt);
        Assert.AreEqual("azure-blob", item.RawMessageObjectProvider, StringComparer.Ordinal);
        Assert.AreNotEqual(originalName, item.RawMessageObjectName, StringComparer.Ordinal);
        var content = scope.ServiceProvider.GetRequiredService<MailboxMessageContentService>();
        CollectionAssert.AreEqual(Encoding.ASCII.GetBytes(NativeMime), await content.ReadAsync(item, CancellationToken.None).ConfigureAwait(false));
    }

    private static async Task<string> MoveResponseAsync(CaptureFixture fixture, string refs, string? target = null, string option = "", string path = CanonicalPath)
    {
        using var content = XmlContent(MoveRequest(target ?? $"<t:FolderId Id='{FolderId(GatewayEwsFixtureDomain.GrandchildId)}'/>", refs, option));
        using var response = await fixture.Client.PostAsync(new Uri(path, UriKind.Relative), content).ConfigureAwait(false);
        var xml = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, xml);
        Assert.IsTrue(response.Headers.CacheControl?.NoStore);
        await fixture.AssertRecordedResponseAsync("ews", path, 200, xml, rejection: false).ConfigureAwait(false);
        return xml;
    }

    private static string MoveRequest(string target, string refs, string option = "") =>
        $"<s:Envelope xmlns:s='{GatewayEwsSoap.Soap}' xmlns:m='{Messages}' xmlns:t='{Types}'><s:Body><m:MoveItem><m:ToFolderId>{target}</m:ToFolderId><m:ItemIds>{refs}</m:ItemIds>{option}</m:MoveItem></s:Body></s:Envelope>";
}
