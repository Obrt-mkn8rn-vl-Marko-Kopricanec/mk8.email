using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using mk8.email.Contracts.Messaging;
using mk8.email.Gateway.Protocols.Ews;
using mk8.email.Infrastructure.Models;
using CaptureFixture = mk8.email.Messaging.Tests.GatewayHttpCaptureBoundaryTests.CaptureFixture;

namespace mk8.email.Messaging.Tests;

internal sealed partial class GatewayEwsRouteTests
{
    [TestMethod]
    [DataRow(CanonicalPath, false)]
    [DataRow(CanonicalPath, true)]
    [DataRow("/eWs/EXCHANGE.ASMX/", false)]
    [DataRow("/eWs/EXCHANGE.ASMX/", true)]
    public async Task FolderSyncInitialAndEmptyDeltaUseRealOwnedSnapshotsAndOriginalSessions(string path, bool disabledJmap)
    {
        var fixture = await CaptureFixture.CreateAsync(ordinaryClient: true, mailFolders: true, disableJmap: disabledJmap).ConfigureAwait(false);
        await using var lifetime = fixture.ConfigureAwait(false);
        Authenticate(fixture);
        var initial = await SyncHierarchyAsync(fixture, path: path).ConfigureAwait(false);
        AssertCode(initial, "NoError");
        Assert.HasCount(7, initial.Descendants(Types + "Create"));
        Assert.IsFalse(initial.ToString().Contains("PRIVATE FOREIGN NAME", StringComparison.Ordinal));
        var initialState = SyncCursor(initial);
        Assert.IsTrue(GatewayEwsFolderSyncState.TryDecode(initialState, out var cursor));
        Assert.AreEqual(GatewayEwsFixtureDomain.AccountId, cursor!.Account);
        Assert.HasCount(7, cursor.Parents);
        var empty = await SyncHierarchyAsync(fixture, initialState, path: path).ConfigureAwait(false);
        AssertCode(empty, "NoError");
        Assert.IsFalse(empty.Descendants(Messages + "Changes").Single().HasElements);
        Assert.AreEqual(initialState, SyncCursor(empty), StringComparer.Ordinal);
        Assert.AreEqual("true", empty.Descendants(Messages + "IncludesLastFolderInRange").Single().Value, StringComparer.Ordinal);
    }

    [TestMethod]
    public async Task FolderSyncNativeCreateRenameAndDeleteConvergeWithClientCache()
    {
        var fixture = await CaptureFixture.CreateAsync(mailFolders: true).ConfigureAwait(false);
        await using var lifetime = fixture.ConfigureAwait(false);
        Authenticate(fixture);
        var initial = await SyncHierarchyAsync(fixture, metadata: true).ConfigureAwait(false);
        var cache = initial.Descendants(Types + "Create").Select(item => item.Element(Types + "Folder")!)
            .ToDictionary(item => (string)item.Element(Types + "FolderId")!.Attribute("Id")!, item => item.Element(Types + "DisplayName")!.Value, StringComparer.Ordinal);
        var created = await WriteAsync(fixture, "CreateFolder", CreateFields("<t:DistinguishedFolderId Id='msgfolderroot'/>", "Sync &amp; new")).ConfigureAwait(false);
        AssertCode(created, "NoError");
        var addedId = (string)created.Descendants(Types + "FolderId").Single().Attribute("Id")!;
        AssertCode(await WriteAsync(fixture, "UpdateFolder", UpdateFields(FolderId(GatewayEwsFixtureDomain.ChildId), "Renamed &lt;&amp;&gt;", null)).ConfigureAwait(false), "NoError");
        AssertCode(await WriteAsync(fixture, "DeleteFolder", $"<m:FolderIds><t:FolderId Id='{FolderId(GatewayEwsFixtureDomain.GrandchildId)}'/></m:FolderIds>").ConfigureAwait(false), "NoError");
        var delta = await SyncHierarchyAsync(fixture, SyncCursor(initial), metadata: true).ConfigureAwait(false);
        AssertCode(delta, "NoError");
        Assert.HasCount(1, delta.Descendants(Types + "Create"));
        Assert.HasCount(1, delta.Descendants(Types + "Delete"));
        Assert.IsTrue(delta.Descendants(Types + "Update").Any());
        foreach (var change in delta.Descendants(Messages + "Changes").Single().Elements())
        {
            if (change.Name == Types + "Delete") cache.Remove((string)change.Element(Types + "FolderId")!.Attribute("Id")!);
            else
            {
                var folder = change.Element(Types + "Folder")!;
                cache[(string)folder.Element(Types + "FolderId")!.Attribute("Id")!] = folder.Element(Types + "DisplayName")!.Value;
            }
        }
        Assert.AreEqual("Sync & new", cache[addedId], StringComparer.Ordinal);
        Assert.AreEqual("Renamed <&>", cache[FolderId(GatewayEwsFixtureDomain.ChildId)], StringComparer.Ordinal);
        Assert.IsFalse(cache.ContainsKey(FolderId(GatewayEwsFixtureDomain.GrandchildId)));
        Assert.HasCount(7, cache);
        var empty = await SyncHierarchyAsync(fixture, SyncCursor(delta), metadata: true).ConfigureAwait(false);
        AssertCode(empty, "NoError");
        Assert.IsFalse(empty.Descendants(Messages + "Changes").Single().HasElements);
    }

    [TestMethod]
    public async Task FolderSyncReadFlagChangesProjectCurrentUnreadCountsWithoutBodies()
    {
        var fixture = await CaptureFixture.CreateAsync(mailFolders: true).ConfigureAwait(false);
        await using var lifetime = fixture.ConfigureAwait(false);
        Authenticate(fixture);
        var initial = await SyncHierarchyAsync(fixture, metadata: true).ConfigureAwait(false);
        var database = Context(fixture);
        await using var databaseLifetime = database.ConfigureAwait(false);
        var email = await database.Emails.FirstAsync(item => item.FolderId == GatewayEwsFixtureDomain.InboxId && !item.IsRead && !item.IsDeleted).ConfigureAwait(false);
        email.IsRead = true;
        await database.SaveChangesAsync().ConfigureAwait(false);
        var delta = await SyncHierarchyAsync(fixture, SyncCursor(initial), metadata: true).ConfigureAwait(false);
        AssertCode(delta, "NoError");
        var folder = delta.Descendants(Types + "Update").Single().Element(Types + "Folder")!;
        Assert.AreEqual(FolderId(GatewayEwsFixtureDomain.InboxId), (string?)folder.Element(Types + "FolderId")!.Attribute("Id"), StringComparer.Ordinal);
        Assert.AreEqual("0", folder.Element(Types + "UnreadCount")!.Value, StringComparer.Ordinal);
        Assert.IsFalse(delta.Descendants(Types + "Body").Any() || delta.Descendants(Types + "MimeContent").Any());
    }

    [TestMethod]
    public async Task FolderSyncMissingPathParentCreationAndDeletionReconcileDerivedAncestryAndCounts()
    {
        var fixture = await CaptureFixture.CreateAsync(mailFolders: true).ConfigureAwait(false);
        await using var lifetime = fixture.ConfigureAwait(false);
        Authenticate(fixture);
        var database = Context(fixture);
        await using var databaseLifetime = database.ConfigureAwait(false);
        var orphan = Guid.CreateVersion7();
        await database.Folders.AddAsync(new FolderDB { Id = orphan, InboxId = GatewayEwsFixtureDomain.AccountId, Name = "INBOX/Missing/Child" }).ConfigureAwait(false);
        await database.SaveChangesAsync().ConfigureAwait(false);
        var initial = await SyncHierarchyAsync(fixture, metadata: true).ConfigureAwait(false);
        var created = await WriteAsync(fixture, "CreateFolder", CreateFields("<t:DistinguishedFolderId Id='inbox'/>", "Missing")).ConfigureAwait(false);
        AssertCode(created, "NoError");
        var parentId = (string)created.Descendants(Types + "FolderId").Single().Attribute("Id")!;
        Assert.IsTrue(GatewayEwsFolderIdCodec.TryDecode(parentId, out _, out var parent));
        var added = await SyncHierarchyAsync(fixture, SyncCursor(initial), metadata: true).ConfigureAwait(false);
        AssertCode(added, "NoError");
        Assert.AreEqual(parentId, (string?)SyncedFolder(added, orphan).Element(Types + "ParentFolderId")!.Attribute("Id"), StringComparer.Ordinal);
        Assert.AreEqual("2", SyncedFolder(added, GatewayEwsFixtureDomain.InboxId).Element(Types + "ChildFolderCount")!.Value, StringComparer.Ordinal);
        database.Folders.Remove(await database.Folders.SingleAsync(item => item.Id == parent).ConfigureAwait(false));
        await database.SaveChangesAsync().ConfigureAwait(false);
        var removed = await SyncHierarchyAsync(fixture, SyncCursor(added), metadata: true).ConfigureAwait(false);
        AssertCode(removed, "NoError");
        Assert.AreEqual(FolderId(Guid.Empty), (string?)SyncedFolder(removed, orphan).Element(Types + "ParentFolderId")!.Attribute("Id"), StringComparer.Ordinal);
        Assert.AreEqual("1", SyncedFolder(removed, GatewayEwsFixtureDomain.InboxId).Element(Types + "ChildFolderCount")!.Value, StringComparer.Ordinal);
        Assert.AreEqual(parentId, (string?)removed.Descendants(Types + "Delete").Single().Element(Types + "FolderId")!.Attribute("Id"), StringComparer.Ordinal);
    }

    [TestMethod]
    [DataRow("foreign", "ErrorAccessDenied")]
    [DataRow("alias", "ErrorAccessDenied")]
    [DataRow("physical", "ErrorInvalidRequest")]
    [DataRow("root", "ErrorInvalidRequest")]
    [DataRow("foreign-token", "ErrorInvalidSyncStateData")]
    public async Task FolderSyncScopeReferencesAndForeignCursorsCannotSupplyAuthority(string mode, string code)
    {
        var fixture = await CaptureFixture.CreateAsync(mailFolders: true).ConfigureAwait(false);
        await using var lifetime = fixture.ConfigureAwait(false);
        Authenticate(fixture);
        var scope = mode is "foreign" ? $"<t:FolderId Id='{GatewayEwsFolderIdCodec.Encode(GatewayEwsFixtureDomain.ForeignAccountId, Guid.Empty)}'/>"
            : mode is "alias" ? "<t:DistinguishedFolderId Id='msgfolderroot'><t:Mailbox><t:EmailAddress>alias@example.test</t:EmailAddress></t:Mailbox></t:DistinguishedFolderId>"
            : mode is "physical" ? $"<t:FolderId Id='{FolderId(GatewayEwsFixtureDomain.InboxId)}'/>"
            : mode is "root" ? "<t:DistinguishedFolderId Id='root'/>" : null;
        var state = mode is "foreign-token" ? GatewayEwsFolderSyncState.Encode(GatewayEwsFixtureDomain.ForeignAccountId, "s0", new Dictionary<Guid, Guid?>()) : null;
        var result = await SyncHierarchyAsync(fixture, state, scope).ConfigureAwait(false);
        AssertCode(result, code);
        Assert.IsFalse(result.Descendants(Types + "FolderId").Any() || result.Descendants(Messages + "SyncState").Any());
        await fixture.AssertWorkerOperationsAsync(ApplicationOperations.JmapProfileGet).ConfigureAwait(false);
    }

    [TestMethod]
    [DataRow("<m:SyncState>!</m:SyncState>", false, "ErrorInvalidSyncStateData")]
    [DataRow("<m:SyncState/>", false, "ErrorInvalidSyncStateData")]
    [DataRow("<m:SyncState><?forbidden data?>!</m:SyncState>", false, "ErrorSchemaValidation")]
    [DataRow("<m:MaxChangesReturned>1</m:MaxChangesReturned>", false, "ErrorSchemaValidation")]
    [DataRow("", true, "ErrorInvalidPropertyRequest")]
    public async Task FolderSyncUnsupportedShapesAndMalformedTokensAreRecordedBeforeAnyWorkerDispatch(string fields, bool derivedCount, string code)
    {
        var fixture = await CaptureFixture.CreateAsync(mailFolders: true).ConfigureAwait(false);
        await using var lifetime = fixture.ConfigureAwait(false);
        Authenticate(fixture);
        var shape = derivedCount ? "<t:BaseShape>AllProperties</t:BaseShape>"
            : "<t:BaseShape>IdOnly</t:BaseShape>";
        using var content = XmlContent(SyncRequest(fields, shape));
        using var response = await fixture.Client.PostAsync(new Uri(CanonicalPath, UriKind.Relative), content).ConfigureAwait(false);
        var xml = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.InternalServerError, response.StatusCode);
        AssertFault(xml, code);
        await fixture.AssertNoWorkerRequestsAsync().ConfigureAwait(false);
        await fixture.AssertRecordedResponseAsync("ews", CanonicalPath, 500, xml, rejection: false).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task FolderSyncFutureStateRefusesWithoutAnAdvancedCursor()
    {
        var fixture = await CaptureFixture.CreateAsync(mailFolders: true).ConfigureAwait(false);
        await using var lifetime = fixture.ConfigureAwait(false);
        Authenticate(fixture);
        var initial = await SyncHierarchyAsync(fixture).ConfigureAwait(false);
        Assert.IsTrue(GatewayEwsFolderSyncState.TryDecode(SyncCursor(initial), out var prior));
        var result = await SyncHierarchyAsync(fixture, GatewayEwsFolderSyncState.Encode(prior!.Account, "s9223372036854775807", prior.Parents)).ConfigureAwait(false);
        AssertCode(result, "ErrorInvalidSyncStateData");
        Assert.IsFalse(result.Descendants(Messages + "SyncState").Any());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task FolderSyncOmittedVersusExplicitRootIsBoundAcrossSuccessfulRequests(bool supplied)
    {
        var fixture = await CaptureFixture.CreateAsync(mailFolders: true).ConfigureAwait(false);
        await using var lifetime = fixture.ConfigureAwait(false);
        Authenticate(fixture);
        const string root = "<t:DistinguishedFolderId Id='msgfolderroot'/>";
        var initial = await SyncHierarchyAsync(fixture, scope: supplied ? root : null).ConfigureAwait(false);
        var state = SyncCursor(initial);
        var same = await SyncHierarchyAsync(fixture, state, supplied ? root : null).ConfigureAwait(false);
        AssertCode(same, "NoError");
        Assert.IsFalse(same.Descendants(Messages + "Changes").Single().HasElements);
        var mismatched = await SyncHierarchyAsync(fixture, state, supplied ? null : root).ConfigureAwait(false);
        AssertCode(mismatched, "ErrorInvalidSyncStateData");
        Assert.IsFalse(mismatched.Descendants(Messages + "SyncState").Any());
    }

    [TestMethod]
    public async Task FolderSyncUnjournaledAccountMoveCannotSilentlyLeaveAStaleClientId()
    {
        var fixture = await CaptureFixture.CreateAsync(mailFolders: true).ConfigureAwait(false);
        await using var lifetime = fixture.ConfigureAwait(false);
        Authenticate(fixture);
        var initial = await SyncHierarchyAsync(fixture).ConfigureAwait(false);
        var database = Context(fixture);
        await using var databaseLifetime = database.ConfigureAwait(false);
        var folder = await database.Folders.SingleAsync(item => item.Id == GatewayEwsFixtureDomain.GrandchildId).ConfigureAwait(false);
        folder.InboxId = GatewayEwsFixtureDomain.ForeignAccountId;
        folder.Name = "Moved-private";
        await database.SaveChangesAsync().ConfigureAwait(false);
        var result = await SyncHierarchyAsync(fixture, SyncCursor(initial)).ConfigureAwait(false);
        AssertCode(result, "ErrorInvalidSyncStateData");
        Assert.IsFalse(result.Descendants(Types + "FolderId").Any() || result.Descendants(Messages + "SyncState").Any());
    }

    [TestMethod]
    public async Task FolderSyncMoreThanOneCompleteNativeDeltaPageRefusesRatherThanFabricates()
    {
        var fixture = await CaptureFixture.CreateAsync(mailFolders: true, additionalFolders: 493).ConfigureAwait(false);
        await using var lifetime = fixture.ConfigureAwait(false);
        Authenticate(fixture);
        var initial = await SyncHierarchyAsync(fixture).ConfigureAwait(false);
        Assert.HasCount(500, initial.Descendants(Types + "Create"));
        var database = Context(fixture);
        await using var databaseLifetime = database.ConfigureAwait(false);
        database.Folders.RemoveRange(await database.Folders.Where(item => item.InboxId == GatewayEwsFixtureDomain.AccountId && item.Name.StartsWith("Extra-")).ToArrayAsync().ConfigureAwait(false));
        for (var index = 0; index < 20; index++)
            await database.Folders.AddAsync(new FolderDB { Id = Guid.CreateVersion7(), InboxId = GatewayEwsFixtureDomain.AccountId, Name = $"New-{index:D2}" }).ConfigureAwait(false);
        await database.SaveChangesAsync().ConfigureAwait(false);
        var result = await SyncHierarchyAsync(fixture, SyncCursor(initial)).ConfigureAwait(false);
        AssertCode(result, "ErrorExceededFindCountLimit");
        Assert.IsFalse(result.Descendants(Messages + "SyncState").Any() || result.Descendants(Messages + "Changes").Any());
    }

    [TestMethod]
    [DataRow(493, true)]
    [DataRow(494, false)]
    public async Task FolderSyncInitialHierarchyLimitIsCompleteAndInclusive(int additional, bool accepted)
    {
        var fixture = await CaptureFixture.CreateAsync(mailFolders: true, additionalFolders: additional).ConfigureAwait(false);
        await using var lifetime = fixture.ConfigureAwait(false);
        Authenticate(fixture);
        var result = await SyncHierarchyAsync(fixture).ConfigureAwait(false);
        AssertCode(result, accepted ? "NoError" : "ErrorExceededFindCountLimit");
        Assert.AreEqual(accepted ? 500 : 0, result.Descendants(Types + "Create").Count());
        Assert.AreEqual(accepted, result.Descendants(Messages + "SyncState").Any());
        if (accepted)
        {
            var token = SyncCursor(result);
            Assert.AreEqual(GatewayEwsFolderSyncState.MaximumEncodedLength, token.Length);
            var text = SyncRequest($"<m:SyncState>{token}</m:SyncState>");
            using var content = new ByteArrayContent([.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes(text)]);
            content.Headers.ContentType = new MediaTypeHeaderValue("text/xml") { CharSet = "utf-16" };
            using var response = await fixture.Client.PostAsync(new Uri(CanonicalPath, UriKind.Relative), content).ConfigureAwait(false);
            var xml = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, xml);
            var empty = XDocument.Parse(xml);
            AssertCode(empty, "NoError");
            Assert.IsFalse(empty.Descendants(Messages + "Changes").Single().HasElements);
            await fixture.AssertRecordedResponseAsync("ews", CanonicalPath, 200, xml, rejection: false).ConfigureAwait(false);
        }
    }

    [TestMethod]
    [DataRow(MailOperationKind.ReadFolders)]
    [DataRow(MailOperationKind.ReadFolderChanges)]
    public async Task FolderSyncCommittedRacesWithholdMixedSnapshots(MailOperationKind trigger)
    {
        CaptureFixture? fixture = null;
        // For a change-reader race, the initial cursor must be issued before the
        // decorator can fire. ReadFolders instead races the first initial graph.
        fixture = await CaptureFixture.CreateAsync(mailFolders: true, decorateDispatcher: inner => new FindRaceDispatcher(inner, trigger, async token =>
        {
            var database = Context(fixture!);
            await using var databaseLifetime = database.ConfigureAwait(false);
            var folder = await database.Folders.SingleAsync(item => item.Id == GatewayEwsFixtureDomain.GrandchildId, token).ConfigureAwait(false);
            folder.Name = "INBOX/A & B/Committed race";
            await database.SaveChangesAsync(token).ConfigureAwait(false);
        })).ConfigureAwait(false);
        await using var lifetime = fixture.ConfigureAwait(false);
        Authenticate(fixture);
        var state = trigger == MailOperationKind.ReadFolderChanges ? SyncCursor(await SyncHierarchyAsync(fixture).ConfigureAwait(false)) : null;
        var result = await SyncHierarchyAsync(fixture, state).ConfigureAwait(false);
        AssertCode(result, "ErrorServerBusy");
        Assert.IsFalse(result.Descendants(Messages + "SyncState").Any() || result.Descendants(Messages + "Changes").Any());
    }

    [TestMethod]
    [DataRow(0L)]
    [DataRow(1L)]
    public async Task FolderSyncJournalFailureWithholdsEveryFolderAndCursor(long sequence)
    {
        var fixture = await CaptureFixture.CreateAsync(failSequence: sequence, mailFolders: true).ConfigureAwait(false);
        await using var lifetime = fixture.ConfigureAwait(false);
        Authenticate(fixture);
        using var content = XmlContent(SyncRequest(""));
        using var response = await fixture.Client.PostAsync(new Uri(CanonicalPath, UriKind.Relative), content).ConfigureAwait(false);
        var xml = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        AssertFault(xml, "ErrorServerBusy");
        Assert.IsFalse(XDocument.Parse(xml).Descendants(Types + "FolderId").Any() || xml.Contains("SyncState", StringComparison.Ordinal));
        await fixture.AssertPresentationRecordCountAsync(sequence).ConfigureAwait(false);
    }

    private static string SyncCursor(XDocument document) => document.Descendants(Messages + "SyncState").Single().Value;
    private static XElement SyncedFolder(XDocument document, Guid id) => document.Descendants(Types + "Update").Select(item => item.Element(Types + "Folder")!)
        .Single(folder => string.Equals((string?)folder.Element(Types + "FolderId")!.Attribute("Id"), FolderId(id), StringComparison.Ordinal));
    private static string SyncRequest(string fields, string shape = "<t:BaseShape>IdOnly</t:BaseShape>") => $"<s:Envelope xmlns:s='{GatewayEwsSoap.Soap}' xmlns:m='{Messages}' xmlns:t='{Types}'><s:Body><m:SyncFolderHierarchy><m:FolderShape>{shape}</m:FolderShape>{fields}</m:SyncFolderHierarchy></s:Body></s:Envelope>";
    private static async Task<XDocument> SyncHierarchyAsync(CaptureFixture fixture, string? state = null, string? scope = null, bool metadata = false, string path = CanonicalPath)
    {
        var fields = scope is null ? "" : $"<m:SyncFolderId>{scope}</m:SyncFolderId>";
        if (state is not null) fields += $"<m:SyncState>{state}</m:SyncState>";
        var shape = "<t:BaseShape>IdOnly</t:BaseShape>";
        if (metadata) shape += "<t:AdditionalProperties><t:FieldURI FieldURI='folder:ParentFolderId'/><t:FieldURI FieldURI='folder:DisplayName'/><t:FieldURI FieldURI='folder:TotalCount'/><t:FieldURI FieldURI='folder:ChildFolderCount'/><t:FieldURI FieldURI='folder:UnreadCount'/></t:AdditionalProperties>";
        using var content = XmlContent(SyncRequest(fields, shape));
        using var response = await fixture.Client.PostAsync(new Uri(path, UriKind.Relative), content).ConfigureAwait(false);
        var xml = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, xml);
        await fixture.AssertRecordedResponseAsync("ews", path, 200, xml, rejection: false).ConfigureAwait(false);
        return XDocument.Parse(xml);
    }
}
