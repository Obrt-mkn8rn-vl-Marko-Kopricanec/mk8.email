using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using mk8.email.Contracts.Messaging;
using mk8.email.Gateway.Protocols.Ews;
using CaptureFixture = mk8.email.Messaging.Tests.GatewayHttpCaptureBoundaryTests.CaptureFixture;

namespace mk8.email.Messaging.Tests;

internal sealed partial class GatewayEwsRouteTests
{
    [TestMethod]
    [DataRow(CanonicalPath, false)]
    [DataRow(CanonicalPath, true)]
    [DataRow("/eWs/EXCHANGE.ASMX/", false)]
    [DataRow("/eWs/EXCHANGE.ASMX/", true)]
    public async Task ItemSyncInitialEmptyDeltaAndGetItemBindRealFolderOwnedAzureSnapshots(string path, bool disabledJmap)
    {
        var fixture = await CaptureFixture.CreateAsync(ordinaryClient: true, mailFolders: true, disableJmap: disabledJmap).ConfigureAwait(false);
        await using var lifetime = fixture.ConfigureAwait(false);
        var ids = await SeedFindItemsAsync(fixture, 2, GatewayEwsFixtureDomain.ChildId).ConfigureAwait(false);
        await SeedFindItemsAsync(fixture, 1, GatewayEwsFixtureDomain.GrandchildId).ConfigureAwait(false);
        await SeedFindItemsAsync(fixture, 1, GatewayEwsFixtureDomain.ForeignFolderId).ConfigureAwait(false);
        Authenticate(fixture);
        var initial = await SyncItemsAsync(fixture, path: path).ConfigureAwait(false);
        AssertCode(initial, "NoError");
        CollectionAssert.AreEquivalent(ids.Select(ItemId).ToArray(), initial.Descendants(Types + "Create").Select(change => (string)change.Descendants(Types + "ItemId").Single().Attribute("Id")!).ToArray());
        var token = ItemSyncCursor(initial);
        Assert.IsTrue(GatewayEwsItemSyncState.TryDecode(token, out var cursor));
        Assert.AreEqual(GatewayEwsFixtureDomain.ChildId, cursor!.Folder);
        Assert.HasCount(2, cursor.ReadStates);
        var empty = await SyncItemsAsync(fixture, token, path: path).ConfigureAwait(false);
        AssertCode(empty, "NoError");
        Assert.IsFalse(empty.Descendants(Messages + "Changes").Single().HasElements);
        Assert.AreEqual(token, ItemSyncCursor(empty), StringComparer.Ordinal);
        Assert.AreEqual("true", empty.Descendants(Messages + "IncludesLastItemInRange").Single().Value, StringComparer.Ordinal);
        var bound = await ReadItemAsync(fixture, ids[0]).ConfigureAwait(false);
        Assert.AreEqual(ItemId(ids[0]), (string?)bound.Element(Types + "ItemId")!.Attribute("Id"), StringComparer.Ordinal);
    }

    [TestMethod]
    public async Task ItemSyncNativeReadDeleteAndSameAccountFolderMovesConvergeWithoutBodies()
    {
        var fixture = await CaptureFixture.CreateAsync(mailFolders: true).ConfigureAwait(false);
        await using var lifetime = fixture.ConfigureAwait(false);
        var ids = await SeedFindItemsAsync(fixture, 3, GatewayEwsFixtureDomain.ChildId).ConfigureAwait(false);
        var inbound = (await SeedFindItemsAsync(fixture, 1, GatewayEwsFixtureDomain.GrandchildId).ConfigureAwait(false)).Single();
        Authenticate(fixture);
        var initial = await SyncItemsAsync(fixture).ConfigureAwait(false);
        var database = Context(fixture);
        await using var databaseLifetime = database.ConfigureAwait(false);
        var rows = await database.Emails.Where(email => ids.Contains(email.Id) || email.Id == inbound).ToDictionaryAsync(email => email.Id).ConfigureAwait(false);
        rows[ids[0]].IsRead = true;
        rows[ids[1]].IsDeleted = true;
        rows[ids[2]].FolderId = GatewayEwsFixtureDomain.GrandchildId;
        rows[ids[2]].Uid = 201;
        rows[inbound].FolderId = GatewayEwsFixtureDomain.ChildId;
        rows[inbound].Uid = 202;
        await database.SaveChangesAsync().ConfigureAwait(false);
        var delta = await SyncItemsAsync(fixture, ItemSyncCursor(initial)).ConfigureAwait(false);
        AssertCode(delta, "NoError");
        Assert.HasCount(1, delta.Descendants(Types + "Create"));
        Assert.HasCount(1, delta.Descendants(Types + "Update"));
        Assert.HasCount(2, delta.Descendants(Types + "Delete"));
        Assert.HasCount(1, delta.Descendants(Types + "ReadFlagChange"));
        Assert.AreEqual(ItemId(inbound), (string?)delta.Descendants(Types + "Create").Single().Descendants(Types + "ItemId").Single().Attribute("Id"), StringComparer.Ordinal);
        Assert.AreEqual("true", delta.Descendants(Types + "ReadFlagChange").Single().Element(Types + "IsRead")!.Value, StringComparer.Ordinal);
        var empty = await SyncItemsAsync(fixture, ItemSyncCursor(delta)).ConfigureAwait(false);
        AssertCode(empty, "NoError");
        Assert.IsFalse(empty.Descendants(Messages + "Changes").Single().HasElements);
        Assert.IsTrue(GatewayEwsItemSyncState.TryDecode(ItemSyncCursor(delta), out var cursor));
        Assert.IsTrue(cursor!.ReadStates.Keys.ToHashSet().SetEquals([ids[0], inbound]));
    }

    [TestMethod]
    public async Task ItemSyncActualNativeImportAndHardDeleteAreCreatesThenDeletes()
    {
        var fixture = await CaptureFixture.CreateAsync(mailFolders: true).ConfigureAwait(false);
        await using var lifetime = fixture.ConfigureAwait(false);
        Authenticate(fixture);
        var initial = await SyncItemsAsync(fixture).ConfigureAwait(false);
        AssertCode(initial, "NoError");
        Assert.IsFalse(initial.Descendants(Messages + "Changes").Single().HasElements);
        var created = XDocument.Parse(await CreateResponseAsync(fixture, CreateMessage(NativeMime), $"<t:FolderId Id='{FolderId(GatewayEwsFixtureDomain.ChildId)}'/>").ConfigureAwait(false));
        AssertCode(created, "NoError");
        var reference = created.Descendants(Types + "ItemId").Single();
        var delta = await SyncItemsAsync(fixture, ItemSyncCursor(initial)).ConfigureAwait(false);
        AssertCode(delta, "NoError");
        Assert.AreEqual((string?)reference.Attribute("Id"), (string?)delta.Descendants(Types + "Create").Single().Descendants(Types + "ItemId").Single().Attribute("Id"), StringComparer.Ordinal);
        AssertCode(XDocument.Parse(await DeleteResponseAsync(fixture, reference.ToString(SaveOptions.DisableFormatting)).ConfigureAwait(false)), "NoError");
        var removed = await SyncItemsAsync(fixture, ItemSyncCursor(delta)).ConfigureAwait(false);
        AssertCode(removed, "NoError");
        Assert.HasCount(1, removed.Descendants(Types + "Delete"));
        Assert.AreEqual((string?)reference.Attribute("Id"), (string?)removed.Descendants(Types + "Delete").Single().Element(Types + "ItemId")!.Attribute("Id"), StringComparer.Ordinal);
    }

    [TestMethod]
    [DataRow("foreign", "ErrorAccessDenied")]
    [DataRow("alias", "ErrorAccessDenied")]
    [DataRow("root", "ErrorInvalidRequest")]
    [DataRow("missing", "ErrorFolderNotFound")]
    public async Task ItemSyncUntrustedFolderReferencesCannotGrantMessageAuthority(string mode, string code)
    {
        var fixture = await CaptureFixture.CreateAsync(mailFolders: true).ConfigureAwait(false);
        await using var lifetime = fixture.ConfigureAwait(false);
        Authenticate(fixture);
        var reference = mode is "foreign" ? $"<t:FolderId Id='{GatewayEwsFolderIdCodec.Encode(GatewayEwsFixtureDomain.ForeignAccountId, GatewayEwsFixtureDomain.ForeignFolderId)}'/>"
            : mode is "alias" ? "<t:DistinguishedFolderId Id='inbox'><t:Mailbox><t:EmailAddress>alias@example.test</t:EmailAddress></t:Mailbox></t:DistinguishedFolderId>"
            : mode is "root" ? "<t:DistinguishedFolderId Id='msgfolderroot'/>" : $"<t:FolderId Id='{FolderId(Guid.CreateVersion7())}'/>";
        var result = await SyncItemsAsync(fixture, scope: reference).ConfigureAwait(false);
        AssertCode(result, code);
        Assert.IsFalse(result.Descendants(Types + "ItemId").Any() || result.Descendants(Messages + "SyncState").Any());
    }

    [TestMethod]
    [DataRow("foreign-account")]
    [DataRow("other-folder")]
    [DataRow("shape")]
    [DataRow("foreign-entry")]
    [DataRow("future")]
    public async Task ItemSyncForeignFutureTamperedMembershipOrChangedShapeCursorCannotAdvanceCache(string mode)
    {
        var fixture = await CaptureFixture.CreateAsync(mailFolders: true).ConfigureAwait(false);
        await using var lifetime = fixture.ConfigureAwait(false);
        await SeedFindItemsAsync(fixture, 1, GatewayEwsFixtureDomain.ChildId).ConfigureAwait(false);
        Authenticate(fixture);
        var initial = await SyncItemsAsync(fixture).ConfigureAwait(false);
        Assert.IsTrue(GatewayEwsItemSyncState.TryDecode(ItemSyncCursor(initial), out var prior));
        var rows = prior!.ReadStates.ToDictionary(entry => entry.Key, entry => entry.Value);
        if (mode is "foreign-entry") rows.Add(Guid.CreateVersion7(), false);
        var token = GatewayEwsItemSyncState.Encode(mode is "foreign-account" ? GatewayEwsFixtureDomain.ForeignAccountId : prior.Account,
            mode is "other-folder" ? GatewayEwsFixtureDomain.GrandchildId : prior.Folder,
            mode is "future" ? "s9223372036854775807" : prior.State,
            mode is "shape" ? GatewayEwsItemSyncState.Shape(new HashSet<string>(StringComparer.Ordinal) { "ItemId" }) : prior.Shape, rows);
        var result = await SyncItemsAsync(fixture, token).ConfigureAwait(false);
        AssertCode(result, "ErrorInvalidSyncStateData");
        Assert.IsFalse(result.Descendants(Messages + "SyncState").Any() || result.Descendants(Types + "ItemId").Any());
    }

    [TestMethod]
    [DataRow(32, 512, true)]
    [DataRow(33, 512, false)]
    [DataRow(2, 1, false)]
    public async Task ItemSyncCompleteFolderAndRequestedChangeLimitsNeverPublishPartialSuccess(int count, int maximum, bool accepted)
    {
        var fixture = await CaptureFixture.CreateAsync(mailFolders: true).ConfigureAwait(false);
        await using var lifetime = fixture.ConfigureAwait(false);
        await SeedFindItemsAsync(fixture, count, GatewayEwsFixtureDomain.ChildId).ConfigureAwait(false);
        Authenticate(fixture);
        var initial = await SyncItemsAsync(fixture, maximum: maximum).ConfigureAwait(false);
        AssertCode(initial, accepted ? "NoError" : "ErrorExceededFindCountLimit");
        Assert.AreEqual(accepted ? count : 0, initial.Descendants(Types + "Create").Count());
        Assert.AreEqual(accepted, initial.Descendants(Messages + "SyncState").Any());
        if (accepted)
        {
            var token = ItemSyncCursor(initial);
            Assert.AreEqual(GatewayEwsItemSyncState.MaximumEncodedLength, token.Length);
            var request = ItemSyncRequest($"<m:SyncState>{token}</m:SyncState><m:MaxChangesReturned>512</m:MaxChangesReturned>");
            using var content = new ByteArrayContent([.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes(request)]);
            content.Headers.ContentType = new MediaTypeHeaderValue("text/xml") { CharSet = "utf-16" };
            using var response = await fixture.Client.PostAsync(new Uri(CanonicalPath, UriKind.Relative), content).ConfigureAwait(false);
            var xml = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, xml);
            AssertCode(XDocument.Parse(xml), "NoError");
            await fixture.AssertRecordedResponseAsync("ews", CanonicalPath, 200, xml, rejection: false).ConfigureAwait(false);
        }
    }

    [TestMethod]
    [DataRow(MailOperationKind.FindMessages)]
    [DataRow(MailOperationKind.ReadMessages)]
    [DataRow(MailOperationKind.ReadMessageChanges)]
    public async Task ItemSyncCommittedQueryReadOrDeltaRacesWithholdCompositeSnapshots(MailOperationKind trigger)
    {
        CaptureFixture? fixture = null;
        var enabled = false;
        Guid id = default;
        fixture = await CaptureFixture.CreateAsync(mailFolders: true, decorateDispatcher: inner => new FindRaceDispatcher(inner, trigger, async token =>
        {
            if (!enabled) return;
            var database = Context(fixture!);
            await using var databaseLifetime = database.ConfigureAwait(false);
            var email = await database.Emails.SingleAsync(item => item.Id == id, token).ConfigureAwait(false);
            email.IsRead = !email.IsRead;
            await database.SaveChangesAsync(token).ConfigureAwait(false);
        })).ConfigureAwait(false);
        await using var lifetime = fixture.ConfigureAwait(false);
        id = (await SeedFindItemsAsync(fixture, 1, GatewayEwsFixtureDomain.ChildId).ConfigureAwait(false)).Single();
        Authenticate(fixture);
        var initial = await SyncItemsAsync(fixture).ConfigureAwait(false);
        enabled = true;
        using var content = XmlContent(ItemSyncRequest($"<m:SyncState>{ItemSyncCursor(initial)}</m:SyncState><m:MaxChangesReturned>512</m:MaxChangesReturned>"));
        using var response = await fixture.Client.PostAsync(new Uri(CanonicalPath, UriKind.Relative), content).ConfigureAwait(false);
        var xml = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.IsTrue(xml.Contains("ErrorServerBusy", StringComparison.Ordinal), xml);
        Assert.IsFalse(xml.Contains("SyncState", StringComparison.Ordinal) || xml.Contains("<t:Create", StringComparison.Ordinal));
        await fixture.AssertRecordedResponseAsync("ews", CanonicalPath, (int)response.StatusCode, xml, rejection: false).ConfigureAwait(false);
    }

    [TestMethod]
    [DataRow("<m:Ignore/><m:MaxChangesReturned>32</m:MaxChangesReturned>", "ErrorInvalidRequest")]
    [DataRow("<m:MaxChangesReturned>32</m:MaxChangesReturned><m:SyncScope>NormalAndAssociatedItems</m:SyncScope>", "ErrorInvalidRequest")]
    [DataRow("<m:SyncState>!</m:SyncState><m:MaxChangesReturned>32</m:MaxChangesReturned>", "ErrorInvalidSyncStateData")]
    [DataRow("<m:MaxChangesReturned><?invalid data?>32</m:MaxChangesReturned>", "ErrorSchemaValidation")]
    public async Task ItemSyncMalformedAndUnsupportedRequestsAreJournaledWithoutWorkerDispatch(string fields, string code)
    {
        var fixture = await CaptureFixture.CreateAsync(mailFolders: true).ConfigureAwait(false);
        await using var lifetime = fixture.ConfigureAwait(false);
        Authenticate(fixture);
        using var content = XmlContent(ItemSyncRequest(fields));
        using var response = await fixture.Client.PostAsync(new Uri(CanonicalPath, UriKind.Relative), content).ConfigureAwait(false);
        var xml = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.InternalServerError, response.StatusCode);
        AssertFault(xml, code);
        await fixture.AssertNoWorkerRequestsAsync().ConfigureAwait(false);
        await fixture.AssertRecordedResponseAsync("ews", CanonicalPath, 500, xml, rejection: false).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task ItemSyncRecipientPropertiesCannotExpandTheScalarProfileBeforeDispatch()
    {
        var fixture = await CaptureFixture.CreateAsync(mailFolders: true).ConfigureAwait(false);
        await using var lifetime = fixture.ConfigureAwait(false);
        Authenticate(fixture);
        var request = ItemSyncRequest("<m:MaxChangesReturned>32</m:MaxChangesReturned>").Replace("item:Subject", "message:ToRecipients", StringComparison.Ordinal);
        using var content = XmlContent(request);
        using var response = await fixture.Client.PostAsync(new Uri(CanonicalPath, UriKind.Relative), content).ConfigureAwait(false);
        var xml = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.InternalServerError, response.StatusCode);
        AssertFault(xml, "ErrorInvalidPropertyRequest");
        await fixture.AssertNoWorkerRequestsAsync().ConfigureAwait(false);
        await fixture.AssertRecordedResponseAsync("ews", CanonicalPath, 500, xml, rejection: false).ConfigureAwait(false);
    }

    [TestMethod]
    [DataRow(0L)]
    [DataRow(1L)]
    public async Task ItemSyncJournalFailureWithholdsItemsAndCursor(long sequence)
    {
        var fixture = await CaptureFixture.CreateAsync(failSequence: sequence, mailFolders: true).ConfigureAwait(false);
        await using var lifetime = fixture.ConfigureAwait(false);
        await SeedFindItemsAsync(fixture, 1, GatewayEwsFixtureDomain.ChildId).ConfigureAwait(false);
        Authenticate(fixture);
        using var content = XmlContent(ItemSyncRequest("<m:MaxChangesReturned>512</m:MaxChangesReturned>"));
        using var response = await fixture.Client.PostAsync(new Uri(CanonicalPath, UriKind.Relative), content).ConfigureAwait(false);
        var xml = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        AssertFault(xml, "ErrorServerBusy");
        Assert.IsFalse(xml.Contains("SyncState", StringComparison.Ordinal) || xml.Contains("ItemId", StringComparison.Ordinal));
        await fixture.AssertPresentationRecordCountAsync(sequence).ConfigureAwait(false);
    }

    private static string ItemSyncCursor(XDocument xml) => xml.Descendants(Messages + "SyncState").Single().Value;
    private static string ItemSyncRequest(string fields, string? scope = null) =>
        $"<s:Envelope xmlns:s='{GatewayEwsSoap.Soap}' xmlns:m='{Messages}' xmlns:t='{Types}'><s:Body><m:SyncFolderItems><m:ItemShape><t:BaseShape>IdOnly</t:BaseShape>{FindItemProperties}</m:ItemShape><m:SyncFolderId>{scope ?? $"<t:FolderId Id='{FolderId(GatewayEwsFixtureDomain.ChildId)}'/>"}</m:SyncFolderId>{fields}</m:SyncFolderItems></s:Body></s:Envelope>";
    private static async Task<XDocument> SyncItemsAsync(CaptureFixture fixture, string? state = null, string? scope = null, int maximum = 512, string path = CanonicalPath)
    {
        var fields = state is null ? "" : $"<m:SyncState>{state}</m:SyncState>";
        fields += $"<m:MaxChangesReturned>{maximum}</m:MaxChangesReturned>";
        using var content = XmlContent(ItemSyncRequest(fields, scope));
        using var response = await fixture.Client.PostAsync(new Uri(path, UriKind.Relative), content).ConfigureAwait(false);
        var xml = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, xml);
        Assert.IsFalse(xml.Contains("PRIVATE BODY", StringComparison.Ordinal) || xml.Contains("MimeContent", StringComparison.Ordinal));
        await fixture.AssertRecordedResponseAsync("ews", path, 200, xml, rejection: false).ConfigureAwait(false);
        return XDocument.Parse(xml);
    }
}
