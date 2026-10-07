using System.Net;
using System.Text;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using mk8.email.Application.Services;
using mk8.email.Infrastructure.Data;
using mk8.email.Infrastructure.Models;
using mk8.email.Gateway.Protocols.Ews;
using CaptureFixture = mk8.email.Messaging.Tests.GatewayHttpCaptureBoundaryTests.CaptureFixture;

namespace mk8.email.Messaging.Tests;

internal sealed partial class GatewayEwsRouteTests
{
    private const string FindItemProperties = "<t:AdditionalProperties><t:FieldURI FieldURI='item:Subject'/><t:FieldURI FieldURI='item:ParentFolderId'/><t:FieldURI FieldURI='item:HasAttachments'/><t:FieldURI FieldURI='message:IsRead'/></t:AdditionalProperties>";
    private static readonly string[] FindAuthorityOutcomes = ["ErrorAccessDenied", "ErrorFolderNotFound", "ErrorAccessDenied", "ErrorInvalidIdMalformed", "ErrorFolderNotFound", "NoError", "NoError"];

    [TestMethod]
    [DataRow(CanonicalPath, false)]
    [DataRow(CanonicalPath, true)]
    [DataRow("/eWs/EXCHANGE.ASMX/", false)]
    [DataRow("/eWs/EXCHANGE.ASMX/", true)]
    public async Task FindItemPagesDiscoverAzureBackedIdsWhichGetItemCanRead(string path, bool ordinaryClient)
    {
        var fixture = await CaptureFixture.CreateAsync(ordinaryClient, mailFolders: true).ConfigureAwait(false);
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        var ids = await SeedFindItemsAsync(fixture, 5, GatewayEwsFixtureDomain.ChildId).ConfigureAwait(false);
        Authenticate(fixture);
        await fixture.AssertAdminBoundaryAsync(ordinaryClient).ConfigureAwait(false);
        var parent = $"<t:FolderId Id='{FolderId(GatewayEwsFixtureDomain.ChildId)}'/>";
        var first = await FindPageAsync(fixture, parent, 0, 2, path).ConfigureAwait(false);
        AssertFindPage(first, ids[..2], total: 5, next: 2, last: false);
        var bound = await ReadItemAsync(fixture, ids[0]).ConfigureAwait(false);
        Assert.AreEqual((string?)first.Descendants(Types + "ItemId").First().Attribute("Id"), (string?)bound.Element(Types + "ItemId")!.Attribute("Id"), StringComparer.Ordinal);
        AssertFindPage(await FindPageAsync(fixture, parent, 2, 2, path).ConfigureAwait(false), ids[2..4], 5, 4, false);
        AssertFindPage(await FindPageAsync(fixture, parent, 4, 2, path).ConfigureAwait(false), ids[4..], 5, 5, true);
        AssertFindPage(await FindPageAsync(fixture, parent, int.MaxValue, 2, path).ConfigureAwait(false), [], 5, 5, true);
        var database = Context(fixture);
        await using var databaseLifetime = database.ConfigureAwait(false);
        var rows = await database.Emails.Where(email => ids.Contains(email.Id)).ToListAsync().ConfigureAwait(false);
        Assert.IsTrue(rows.All(email => email.RawMessage is null && string.Equals(email.RawMessageObjectProvider, "azure-blob", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task FindItemParentOutcomesStayOrderedAndNeverGrantForeignOrAliasAuthority()
    {
        var fixture = await CaptureFixture.CreateAsync(mailFolders: true).ConfigureAwait(false);
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        var owned = await SeedFindItemsAsync(fixture, 1, GatewayEwsFixtureDomain.ChildId).ConfigureAwait(false);
        var foreign = await SeedFindItemsAsync(fixture, 1, GatewayEwsFixtureDomain.ForeignFolderId).ConfigureAwait(false);
        Authenticate(fixture);
        var refs = $"<t:FolderId Id='{GatewayEwsFolderIdCodec.Encode(GatewayEwsFixtureDomain.ForeignAccountId, GatewayEwsFixtureDomain.ForeignFolderId)}'/>"
            + $"<t:FolderId Id='{FolderId(GatewayEwsFixtureDomain.ForeignFolderId)}'/>"
            + "<t:DistinguishedFolderId Id='inbox'><t:Mailbox><t:EmailAddress>alias@example.test</t:EmailAddress></t:Mailbox></t:DistinguishedFolderId>"
            + $"<t:FolderId Id='{ItemId(owned[0])}'/><t:FolderId Id='{FolderId(Guid.CreateVersion7())}'/>"
            + $"<t:FolderId Id='{FolderId(GatewayEwsFixtureDomain.ChildId)}'/><t:FolderId Id='{FolderId(GatewayEwsFixtureDomain.ChildId)}'/>";
        var response = await FindPageAsync(fixture, refs).ConfigureAwait(false);
        CollectionAssert.AreEqual(FindAuthorityOutcomes, response.Descendants(Messages + "ResponseCode").Select(code => code.Value).ToArray());
        Assert.AreEqual(2, response.Descendants(Types + "ItemId").Count());
        Assert.IsTrue(response.Descendants(Types + "ItemId").All(item => string.Equals((string?)item.Attribute("Id"), ItemId(owned[0]), StringComparison.Ordinal)));
        Assert.IsFalse(response.ToString().Contains(ItemId(foreign[0]), StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task ShallowFoldersAndVirtualMailRootNeverBecomeAccountWideQueries()
    {
        var fixture = await CaptureFixture.CreateAsync(mailFolders: true).ConfigureAwait(false);
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        var child = await SeedFindItemsAsync(fixture, 1, GatewayEwsFixtureDomain.ChildId).ConfigureAwait(false);
        var grandchild = await SeedFindItemsAsync(fixture, 1, GatewayEwsFixtureDomain.GrandchildId).ConfigureAwait(false);
        Authenticate(fixture);
        var refs = "<t:DistinguishedFolderId Id='msgfolderroot'/>"
            + $"<t:FolderId Id='{FolderId(GatewayEwsFixtureDomain.ChildId)}'/><t:FolderId Id='{FolderId(GatewayEwsFixtureDomain.GrandchildId)}'/>";
        var response = await FindPageAsync(fixture, refs).ConfigureAwait(false);
        var roots = response.Descendants(Messages + "RootFolder").ToArray();
        Assert.HasCount(3, roots);
        Assert.AreEqual("0", (string?)roots[0].Attribute("TotalItemsInView"), StringComparer.Ordinal);
        Assert.IsEmpty(roots[0].Descendants(Types + "Message"));
        Assert.AreEqual(ItemId(child[0]), (string?)roots[1].Descendants(Types + "ItemId").Single().Attribute("Id"), StringComparer.Ordinal);
        Assert.AreEqual(ItemId(grandchild[0]), (string?)roots[2].Descendants(Types + "ItemId").Single().Attribute("Id"), StringComparer.Ordinal);
    }

    [TestMethod]
    public async Task MaximumPageSucceedsAndUnpagedOverflowIsAnExplicitRefusal()
    {
        var fixture = await CaptureFixture.CreateAsync(mailFolders: true).ConfigureAwait(false);
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        var ids = await SeedFindItemsAsync(fixture, 33, GatewayEwsFixtureDomain.ChildId).ConfigureAwait(false);
        Authenticate(fixture);
        var refs = $"<t:FolderId Id='{FolderId(GatewayEwsFixtureDomain.ChildId)}'/>";
        AssertFindPage(await FindPageAsync(fixture, refs, 0, 32).ConfigureAwait(false), ids[..32], 33, 32, false);
        using var content = XmlContent(FindRequest(refs, view: ""));
        using var response = await fixture.Client.PostAsync(new Uri(CanonicalPath, UriKind.Relative), content).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("ErrorExceededFindCountLimit", XDocument.Parse(body).Descendants(Messages + "ResponseCode").Single().Value, StringComparer.Ordinal);
        Assert.IsFalse(body.Contains("RootFolder", StringComparison.Ordinal));
        await fixture.AssertRecordedResponseAsync("ews", CanonicalPath, 200, body, rejection: false).ConfigureAwait(false);
    }

    [TestMethod]
    [DataRow("Deep", "", "", "ErrorInvalidTraversal")]
    [DataRow("SoftDeleted", "", "", "ErrorInvalidTraversal")]
    [DataRow("Shallow", "<m:Restriction/>", "", "ErrorInvalidRequest")]
    [DataRow("Shallow", "<m:SortOrder/>", "", "ErrorInvalidRequest")]
    [DataRow("Shallow", "", "<t:AdditionalProperties><t:FieldURI FieldURI='message:ToRecipients'/></t:AdditionalProperties>", "ErrorInvalidPropertyRequest")]
    [DataRow("Shallow", "<m:IndexedPageItemView MaxEntriesReturned='33' Offset='0' BasePoint='Beginning'/>", "", "ErrorExceededFindCountLimit")]
    public async Task UnsupportedFindItemInputIsDurablyRefusedBeforeWorkerDispatch(string traversal, string view, string fields, string code)
    {
        var fixture = await CaptureFixture.CreateAsync(mailFolders: true).ConfigureAwait(false);
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        Authenticate(fixture);
        using var content = XmlContent(FindRequest("<t:DistinguishedFolderId Id='inbox'/>", view, fields, traversal));
        using var response = await fixture.Client.PostAsync(new Uri(CanonicalPath, UriKind.Relative), content).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.InternalServerError, response.StatusCode);
        AssertFault(body, code);
        await fixture.AssertNoWorkerRequestsAsync().ConfigureAwait(false);
        await fixture.AssertRecordedResponseAsync("ews", CanonicalPath, 500, body, rejection: false).ConfigureAwait(false);
    }

    [TestMethod]
    [DataRow(0L)]
    [DataRow(1L)]
    public async Task FindItemJournalFailureWithholdsDiscoveredIdsAndMetadata(long sequence)
    {
        var fixture = await CaptureFixture.CreateAsync(failSequence: sequence, mailFolders: true).ConfigureAwait(false);
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        await SeedFindItemsAsync(fixture, 1, GatewayEwsFixtureDomain.ChildId).ConfigureAwait(false);
        Authenticate(fixture);
        using var content = XmlContent(FindRequest($"<t:FolderId Id='{FolderId(GatewayEwsFixtureDomain.ChildId)}'/>"));
        using var response = await fixture.Client.PostAsync(new Uri(CanonicalPath, UriKind.Relative), content).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        AssertFault(body, "ErrorServerBusy");
        Assert.IsFalse(body.Contains("ItemId", StringComparison.Ordinal));
        Assert.IsFalse(body.Contains("NoError", StringComparison.Ordinal));
        await fixture.AssertPresentationRecordCountAsync(sequence).ConfigureAwait(false);
    }

    private static async Task<XDocument> FindPageAsync(CaptureFixture fixture, string refs, int offset = 0, int limit = 2, string path = CanonicalPath)
    {
        var view = $"<m:IndexedPageItemView MaxEntriesReturned='{limit}' Offset='{offset}' BasePoint='Beginning'/>";
        using var content = XmlContent(FindRequest(refs, view));
        using var response = await fixture.Client.PostAsync(new Uri(path, UriKind.Relative), content).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.IsTrue(response.Headers.CacheControl?.NoStore);
        Assert.IsFalse(body.Contains("PRIVATE BODY", StringComparison.Ordinal));
        Assert.IsFalse(body.Contains("sender@example.test", StringComparison.Ordinal));
        Assert.IsFalse(body.Contains("ToRecipients", StringComparison.Ordinal));
        await fixture.AssertRecordedResponseAsync("ews", path, 200, body, rejection: false).ConfigureAwait(false);
        return XDocument.Parse(body);
    }

    private static void AssertFindPage(XDocument response, Guid[] ids, int total, int next, bool last)
    {
        Assert.AreEqual("NoError", response.Descendants(Messages + "ResponseCode").Single().Value, StringComparer.Ordinal);
        var root = response.Descendants(Messages + "RootFolder").Single();
        Assert.AreEqual(total.ToString(System.Globalization.CultureInfo.InvariantCulture), (string?)root.Attribute("TotalItemsInView"), StringComparer.Ordinal);
        Assert.AreEqual(next.ToString(System.Globalization.CultureInfo.InvariantCulture), (string?)root.Attribute("IndexedPagingOffset"), StringComparer.Ordinal);
        Assert.AreEqual(last ? "true" : "false", (string?)root.Attribute("IncludesLastItemInRange"), StringComparer.Ordinal);
        CollectionAssert.AreEqual(ids.Select(ItemId).ToArray(), root.Descendants(Types + "ItemId").Select(item => (string)item.Attribute("Id")!).ToArray());
        Assert.HasCount(1, root.Elements());
        Assert.AreEqual(Types + "Items", root.Elements().Single().Name);
    }

    private static async Task<Guid[]> SeedFindItemsAsync(CaptureFixture fixture, int count, Guid folder)
    {
        using var scope = fixture.DomainScopes.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
        var transaction = await database.Database.BeginTransactionAsync().ConfigureAwait(false);
        await using var transactionLifetime = transaction.ConfigureAwait(false);
        var emails = Enumerable.Range(0, count + 1).Select(index => new EmailDB
        {
            Id = Guid.CreateVersion7(),
            FolderId = folder,
            Uid = index + 100,
            IsDeleted = index == count,
            ReceivedAt = new DateTime(2026, 10, 7, 1, 0, 0, DateTimeKind.Utc).AddMinutes(index / 2),
        }).ToArray();
        await database.Emails.AddRangeAsync(emails).ConfigureAwait(false);
        foreach (var email in emails)
            await scope.ServiceProvider.GetRequiredService<MailboxMessageContentService>().SetAsync(email, Encoding.UTF8.GetBytes(ItemMime), CancellationToken.None).ConfigureAwait(false);
        await database.SaveChangesAsync().ConfigureAwait(false);
        await transaction.CommitAsync().ConfigureAwait(false);
        return emails.Where(email => !email.IsDeleted).OrderByDescending(email => email.ReceivedAt).ThenBy(email => email.Id).Select(email => email.Id).ToArray();
    }

    private static string FindRequest(string refs, string view = "<m:IndexedPageItemView MaxEntriesReturned='2' Offset='0' BasePoint='Beginning'/>",
        string fields = FindItemProperties, string traversal = "Shallow") =>
        $"<s:Envelope xmlns:s='{GatewayEwsSoap.Soap}' xmlns:m='{Messages}' xmlns:t='{Types}'><s:Body><m:FindItem Traversal='{traversal}'><m:ItemShape><t:BaseShape>IdOnly</t:BaseShape>{fields}</m:ItemShape>{view}<m:ParentFolderIds>{refs}</m:ParentFolderIds></m:FindItem></s:Body></s:Envelope>";
}
