using System.Net;
using System.Text;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using mk8.email.Application.Services;
using mk8.email.Contracts.Messaging;
using CaptureFixture = mk8.email.Messaging.Tests.GatewayHttpCaptureBoundaryTests.CaptureFixture;

namespace mk8.email.Messaging.Tests;

internal sealed partial class GatewayEwsRouteTests
{
    [TestMethod]
    [DataRow("CopyItem", "inbox", "inbox", false)]
    [DataRow("CopyItem", "drafts", "drafts", true)]
    [DataRow("CopyItem", "sentitems", "sent", false)]
    [DataRow("CopyItem", "deleteditems", "trash", true)]
    [DataRow("CopyItem", "junkemail", "junk", false)]
    [DataRow("MoveItem", "inbox", "inbox", true)]
    [DataRow("MoveItem", "drafts", "drafts", false)]
    [DataRow("MoveItem", "sentitems", "sent", true)]
    [DataRow("MoveItem", "deleteditems", "trash", false)]
    [DataRow("MoveItem", "junkemail", "junk", true)]
    [DataRow("CreateItem", "inbox", "inbox", false)]
    [DataRow("CreateItem", "drafts", "drafts", true)]
    [DataRow("CreateItem", "sentitems", "sent", false)]
    [DataRow("CreateItem", "deleteditems", "trash", true)]
    [DataRow("CreateItem", "junkemail", "junk", false)]
    public async Task DistinguishedItemDestinationsBindActualOwnedRolesAndPreserveNativeContent(string operation, string name, string role, bool explicitMailbox)
    {
        var fixture = await CaptureFixture.CreateAsync(ordinaryClient: true, mailFolders: true, disableJmap: true).ConfigureAwait(false);
        await using var lifetime = fixture.ConfigureAwait(false);
        var source = operation is "CreateItem" ? Guid.Empty : await SeedItemAsync(fixture, GatewayEwsFixtureDomain.ChildId, NativeMime).ConfigureAwait(false);
        var database = Context(fixture);
        await using var databaseLifetime = database.ConfigureAwait(false);
        var target = await database.Folders.SingleAsync(folder => folder.InboxId == GatewayEwsFixtureDomain.AccountId && folder.JmapRole == role).ConfigureAwait(false);
        // The shared read fixture seeds Inbox UIDs directly. This write control
        // must arrange a valid allocator without rewriting those stored rows.
        var maximumUid = await database.Emails.Where(email => email.FolderId == target.Id)
            .Select(email => (int?)email.Uid).MaxAsync().ConfigureAwait(false) ?? 0;
        target.NextUid = Math.Max(target.NextUid, maximumUid + 1);
        await database.SaveChangesAsync().ConfigureAwait(false);
        var before = await database.Emails.CountAsync().ConfigureAwait(false);
        Authenticate(fixture);
        var mailbox = explicitMailbox ? "<t:Mailbox><t:EmailAddress>OWNER@example.test</t:EmailAddress></t:Mailbox>" : "";
        var destination = $"<t:DistinguishedFolderId Id='{name}'>{mailbox}</t:DistinguishedFolderId>";
        var xml = await DestinationResponseAsync(fixture, operation, source, destination).ConfigureAwait(false);
        AssertCode(XDocument.Parse(xml), "NoError");
        var created = CreatedId(XDocument.Parse(xml).Descendants(Types + "ItemId").Single());
        var stored = await database.Emails.AsNoTracking().SingleAsync(email => email.Id == created).ConfigureAwait(false);
        Assert.AreEqual(target.Id, stored.FolderId);
        Assert.AreEqual("azure-blob", stored.RawMessageObjectProvider, StringComparer.Ordinal);
        if (operation is "CreateItem") Assert.IsTrue(stored.IsDraft);
        Assert.AreEqual(before + (operation is "MoveItem" ? 0 : 1), await database.Emails.CountAsync().ConfigureAwait(false));
        if (operation is not "CreateItem") Assert.AreEqual(operation is "CopyItem", await database.Emails.AnyAsync(email => email.Id == source).ConfigureAwait(false));
        Assert.AreEqual(operation is "MoveItem" ? 1 : 0, await database.ExpungedUids.CountAsync().ConfigureAwait(false));
        Assert.AreEqual(0, await database.MailQueueMessages.CountAsync().ConfigureAwait(false));
        var scope = fixture.DomainScopes.CreateAsyncScope();
        await using var scopeLifetime = scope.ConfigureAwait(false);
        CollectionAssert.AreEqual(Encoding.ASCII.GetBytes(NativeMime),
            await scope.ServiceProvider.GetRequiredService<MailboxMessageContentService>().ReadAsync(stored, CancellationToken.None).ConfigureAwait(false));
        if (operation is "CreateItem") await fixture.AssertWorkerOperationsAsync(ApplicationOperations.JmapProfileGet,
            ApplicationOperations.MailOperationExecute, ApplicationOperations.MailOperationExecute, ApplicationOperations.MailOperationExecute,
            ApplicationOperations.JmapUpload, ApplicationOperations.MailOperationExecute).ConfigureAwait(false);
        else await fixture.AssertWorkerOperationsAsync(ApplicationOperations.JmapProfileGet, ApplicationOperations.MailOperationExecute,
            ApplicationOperations.MailOperationExecute, ApplicationOperations.MailOperationExecute, ApplicationOperations.MailOperationExecute).ConfigureAwait(false);
    }

    [TestMethod]
    [DataRow("CopyItem", "alias@example.test")]
    [DataRow("MoveItem", "alias@example.test")]
    [DataRow("CreateItem", "alias@example.test")]
    [DataRow("CopyItem", "foreign@example.test")]
    [DataRow("MoveItem", "foreign@example.test")]
    [DataRow("CreateItem", "foreign@example.test")]
    public async Task DistinguishedItemDestinationsDenyAliasAndForeignMailboxBeforeGraphOrMutation(string operation, string mailbox)
    {
        var fixture = await CaptureFixture.CreateAsync(mailFolders: true).ConfigureAwait(false);
        await using var lifetime = fixture.ConfigureAwait(false);
        var source = operation is "CreateItem" ? Guid.Empty : await SeedItemAsync(fixture, GatewayEwsFixtureDomain.ChildId).ConfigureAwait(false);
        var database = Context(fixture);
        await using var databaseLifetime = database.ConfigureAwait(false);
        var before = await database.Emails.CountAsync().ConfigureAwait(false);
        Authenticate(fixture);
        var destination = $"<t:DistinguishedFolderId Id='drafts'><t:Mailbox><t:EmailAddress>{mailbox}</t:EmailAddress></t:Mailbox></t:DistinguishedFolderId>";
        AssertCode(XDocument.Parse(await DestinationResponseAsync(fixture, operation, source, destination).ConfigureAwait(false)), "ErrorAccessDenied");
        await fixture.AssertWorkerOperationsAsync(ApplicationOperations.JmapProfileGet).ConfigureAwait(false);
        Assert.AreEqual(before, await database.Emails.CountAsync().ConfigureAwait(false));
        Assert.AreEqual(0, await database.ExpungedUids.CountAsync().ConfigureAwait(false));
    }

    [TestMethod]
    [DataRow("CopyItem", "calendar")]
    [DataRow("MoveItem", "calendar")]
    [DataRow("CreateItem", "calendar")]
    [DataRow("CopyItem", "msgfolderroot")]
    [DataRow("MoveItem", "msgfolderroot")]
    [DataRow("CreateItem", "msgfolderroot")]
    public async Task DistinguishedItemDestinationsRefuseUnimplementedOrVirtualProfilesInsideJournal(string operation, string name)
    {
        var fixture = await CaptureFixture.CreateAsync(mailFolders: true).ConfigureAwait(false);
        await using var lifetime = fixture.ConfigureAwait(false);
        Authenticate(fixture);
        var target = $"<t:DistinguishedFolderId Id='{name}'/>";
        var request = operation is "CreateItem" ? CreateRequest(CreateMessage(NativeMime), target)
            : operation is "MoveItem" ? MoveRequest(target, "<t:ItemId Id='opaque'/>") : CopyRequest(target, "<t:ItemId Id='opaque'/>");
        using var content = XmlContent(request);
        using var response = await fixture.Client.PostAsync(new Uri(CanonicalPath, UriKind.Relative), content).ConfigureAwait(false);
        var xml = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.InternalServerError, response.StatusCode);
        AssertFault(xml, "ErrorInvalidRequest");
        await fixture.AssertNoWorkerRequestsAsync().ConfigureAwait(false);
        await fixture.AssertRecordedResponseAsync("ews", CanonicalPath, 500, xml, rejection: false).ConfigureAwait(false);
    }

    [TestMethod]
    [DataRow("CopyItem")]
    [DataRow("MoveItem")]
    [DataRow("CreateItem")]
    public async Task DistinguishedItemDestinationsRefuseAMissingRoleWithoutUploadOrMutation(string operation)
    {
        var fixture = await CaptureFixture.CreateAsync(mailFolders: true).ConfigureAwait(false);
        await using var lifetime = fixture.ConfigureAwait(false);
        var source = operation is "CreateItem" ? Guid.Empty : await SeedItemAsync(fixture, GatewayEwsFixtureDomain.ChildId).ConfigureAwait(false);
        await ChangeDestinationRoleAsync(fixture, CancellationToken.None).ConfigureAwait(false);
        Authenticate(fixture);
        AssertCode(XDocument.Parse(await DestinationResponseAsync(fixture, operation, source, "<t:DistinguishedFolderId Id='drafts'/>").ConfigureAwait(false)), "ErrorFolderNotFound");
        await fixture.AssertWorkerOperationsAsync(ApplicationOperations.JmapProfileGet, ApplicationOperations.MailOperationExecute,
            ApplicationOperations.MailOperationExecute).ConfigureAwait(false);
    }

    [TestMethod]
    [DataRow("CopyItem")]
    [DataRow("MoveItem")]
    [DataRow("CreateItem")]
    public async Task DistinguishedItemDestinationsCompareMailboxStateInsideRealNativeTransaction(string operation)
    {
        CaptureFixture? fixture = null;
        fixture = await CaptureFixture.CreateAsync(mailFolders: true, decorateDispatcher: inner => operation is "CreateItem"
            ? new CreateUploadDispatcher(inner, (_, token) => ChangeDestinationRoleAsync(fixture!, token))
            : new FindRaceDispatcher(inner, MailOperationKind.ReadMessages, token => ChangeDestinationRoleAsync(fixture!, token))).ConfigureAwait(false);
        await using var lifetime = fixture.ConfigureAwait(false);
        var source = operation is "CreateItem" ? Guid.Empty : await SeedItemAsync(fixture, GatewayEwsFixtureDomain.ChildId).ConfigureAwait(false);
        var database = Context(fixture);
        await using var databaseLifetime = database.ConfigureAwait(false);
        var before = await database.Emails.CountAsync().ConfigureAwait(false);
        Authenticate(fixture);
        var xml = await DestinationResponseAsync(fixture, operation, source, "<t:DistinguishedFolderId Id='drafts'/>").ConfigureAwait(false);
        AssertCode(XDocument.Parse(xml), "ErrorIrresolvableConflict");
        Assert.IsFalse(XDocument.Parse(xml).Descendants(Types + "ItemId").Any());
        Assert.AreEqual(before, await database.Emails.CountAsync().ConfigureAwait(false));
        Assert.AreEqual(0, await database.ExpungedUids.CountAsync().ConfigureAwait(false));
        if (source != Guid.Empty) Assert.IsTrue(await database.Emails.AnyAsync(email => email.Id == source).ConfigureAwait(false));
    }

    private static async Task ChangeDestinationRoleAsync(CaptureFixture fixture, CancellationToken cancellationToken)
    {
        var database = Context(fixture);
        await using var lifetime = database.ConfigureAwait(false);
        var emailBefore = await DestinationSequenceAsync(database, "Email", cancellationToken).ConfigureAwait(false);
        var mailboxBefore = await DestinationSequenceAsync(database, "Mailbox", cancellationToken).ConfigureAwait(false);
        var folder = await database.Folders.SingleAsync(item => item.InboxId == GatewayEwsFixtureDomain.AccountId && item.JmapRole == "drafts", cancellationToken).ConfigureAwait(false);
        folder.JmapRole = "archive";
        await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        Assert.AreEqual(emailBefore, await DestinationSequenceAsync(database, "Email", cancellationToken).ConfigureAwait(false));
        Assert.IsGreaterThan(mailboxBefore, await DestinationSequenceAsync(database, "Mailbox", cancellationToken).ConfigureAwait(false));
    }

    private static Task<long> DestinationSequenceAsync(mk8.email.Infrastructure.Data.EmailDbContext database, string type, CancellationToken token) =>
        database.JmapChanges.Where(change => change.AccountId == GatewayEwsFixtureDomain.AccountId && change.DataType == type)
            .Select(change => change.Sequence).DefaultIfEmpty().MaxAsync(token);

    private static Task<string> DestinationResponseAsync(CaptureFixture fixture, string operation, Guid source, string destination) => operation switch
    {
        "CreateItem" => CreateResponseAsync(fixture, CreateMessage(NativeMime), destination),
        "MoveItem" => MoveResponseAsync(fixture, $"<t:ItemId Id='{ItemId(source)}'/>", destination),
        _ => CopyResponseAsync(fixture, $"<t:ItemId Id='{ItemId(source)}'/>", destination),
    };
}
