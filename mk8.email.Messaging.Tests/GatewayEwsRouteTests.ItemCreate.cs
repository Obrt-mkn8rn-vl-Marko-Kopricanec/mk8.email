using System.Net;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using mk8.email.Application.Interfaces;
using mk8.email.Application.Services;
using mk8.email.Contracts.Messaging;
using mk8.email.Gateway.Protocols.Ews;
using mk8.email.Infrastructure.Data;
using CaptureFixture = mk8.email.Messaging.Tests.GatewayHttpCaptureBoundaryTests.CaptureFixture;

namespace mk8.email.Messaging.Tests;

internal sealed partial class GatewayEwsRouteTests
{
    private static readonly string[] CreateSuccessCodes = ["NoError", "NoError"];
    [TestMethod]
    [DataRow(CanonicalPath, false)]
    [DataRow(CanonicalPath, true)]
    [DataRow("/eWs/EXCHANGE.ASMX/", false)]
    [DataRow("/eWs/EXCHANGE.ASMX/", true)]
    public async Task CreateItemSavesDistinctNativeDraftsThroughAzureWithoutSending(string path, bool disabledJmap)
    {
        var fixture = await CaptureFixture.CreateAsync(ordinaryClient: true, mailFolders: true, disableJmap: disabledJmap).ConfigureAwait(false);
        await using var lifetime = fixture.ConfigureAwait(false);
        Authenticate(fixture);
        var xml = await CreateResponseAsync(fixture, CreateMessage(NativeMime) + CreateMessage(NativeMime), path: path).ConfigureAwait(false);
        await fixture.AssertWorkerOperationsAsync(ApplicationOperations.JmapProfileGet, ApplicationOperations.MailOperationExecute,
            ApplicationOperations.JmapUpload, ApplicationOperations.JmapUpload, ApplicationOperations.MailOperationExecute).ConfigureAwait(false);
        var document = XDocument.Parse(xml);
        CollectionAssert.AreEqual(CreateSuccessCodes, document.Descendants(Messages + "ResponseCode").Select(item => item.Value).ToArray());
        var identities = document.Descendants(Types + "ItemId").ToArray();
        Assert.HasCount(2, identities);
        var ids = identities.Select(element => CreatedId(element)).ToArray();
        Assert.AreNotEqual(ids[0], ids[1]);
        Assert.AreEqual((string?)identities[0].Attribute("ChangeKey"), (string?)identities[1].Attribute("ChangeKey"), StringComparer.Ordinal);
        await AssertCreatedDraftsAsync(fixture, ids, NativeMime).ConfigureAwait(false);
        var native = await MimeResponseAsync(fixture, $"<t:ItemId Id='{ItemId(ids[0])}'/>").ConfigureAwait(false);
        CollectionAssert.AreEqual(Encoding.ASCII.GetBytes(NativeMime), Convert.FromBase64String(XDocument.Parse(native).Descendants(Types + "MimeContent").Single().Value));
        var metadata = await ReadItemAsync(fixture, ids[0]).ConfigureAwait(false);
        Assert.AreEqual("true", metadata.Element(Types + "IsDraft")!.Value, StringComparer.Ordinal);
        Assert.AreEqual("false", metadata.Element(Types + "IsRead")!.Value, StringComparer.Ordinal);
        if (!disabledJmap) await AssertJmapCreatedDraftAsync(fixture, ids[0]).ConfigureAwait(false);
    }

    [TestMethod]
    [DataRow("foreign", "ErrorAccessDenied")]
    [DataRow("forged", "ErrorFolderNotFound")]
    [DataRow("missing", "ErrorFolderNotFound")]
    [DataRow("root", "ErrorAccessDenied")]
    [DataRow("malformed", "ErrorInvalidIdMalformed")]
    public async Task CreateItemDestinationReferencesNeverGrantForeignOrVirtualAuthority(string mode, string code)
    {
        var fixture = await CaptureFixture.CreateAsync(mailFolders: true).ConfigureAwait(false);
        await using var lifetime = fixture.ConfigureAwait(false);
        Authenticate(fixture);
        var target = mode is "foreign" ? GatewayEwsFolderIdCodec.Encode(GatewayEwsFixtureDomain.ForeignAccountId, GatewayEwsFixtureDomain.ForeignFolderId)
            : mode is "forged" ? FolderId(GatewayEwsFixtureDomain.ForeignFolderId)
            : mode is "missing" ? FolderId(Guid.CreateVersion7()) : mode is "root" ? FolderId(Guid.Empty) : "bad";
        var document = XDocument.Parse(await CreateResponseAsync(fixture, CreateMessage(NativeMime), $"<t:FolderId Id='{target}'/>").ConfigureAwait(false));
        AssertCode(document, code);
        Assert.IsFalse(document.Descendants(Types + "ItemId").Any());
        await AssertNoCreatedDraftsAsync(fixture).ConfigureAwait(false);
    }

    [TestMethod]
    [DataRow("MessageDisposition='SendOnly'", "<t:FolderId Id='opaque'/>", "<t:Message><t:MimeContent>TQ==</t:MimeContent></t:Message>", "ErrorInvalidRequest")]
    [DataRow("MessageDisposition='SendAndSaveCopy'", "<t:FolderId Id='opaque'/>", "<t:Message><t:MimeContent>TQ==</t:MimeContent></t:Message>", "ErrorInvalidRequest")]
    [DataRow("MessageDisposition='SaveOnly'", "<t:DistinguishedFolderId Id='drafts' ChangeKey='czEw'/>", "<t:Message><t:MimeContent>TQ==</t:MimeContent></t:Message>", "ErrorInvalidRequest")]
    [DataRow("MessageDisposition='SaveOnly'", "<t:FolderId Id='opaque' ChangeKey='czEw'/>", "<t:Message><t:MimeContent>TQ==</t:MimeContent></t:Message>", "ErrorInvalidRequest")]
    [DataRow("MessageDisposition='SaveOnly'", "<t:FolderId Id='opaque'/>", "<t:CalendarItem/>", "ErrorInvalidRequest")]
    [DataRow("MessageDisposition='SaveOnly'", "<t:FolderId Id='opaque'/>", "<t:Message><t:MimeContent>/w==</t:MimeContent></t:Message>", "ErrorInvalidMimeContent")]
    [DataRow("MessageDisposition='SaveOnly'", "<t:FolderId Id='opaque'/>", "<t:Message><t:MimeContent><?forbidden data?>TQ==</t:MimeContent></t:Message>", "ErrorSchemaValidation")]
    public async Task CreateItemUnsupportedSendingStoresAndMimeHaveRecordedNoDispatchRefusals(string attributes, string folder, string items, string code)
    {
        var fixture = await CaptureFixture.CreateAsync(mailFolders: true).ConfigureAwait(false);
        await using var lifetime = fixture.ConfigureAwait(false);
        Authenticate(fixture);
        using var content = XmlContent(CreateRequest(items, folder, attributes));
        using var response = await fixture.Client.PostAsync(new Uri(CanonicalPath, UriKind.Relative), content).ConfigureAwait(false);
        var xml = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.InternalServerError, response.StatusCode);
        AssertFault(xml, code);
        await fixture.AssertNoWorkerRequestsAsync().ConfigureAwait(false);
        await fixture.AssertRecordedResponseAsync("ews", CanonicalPath, 500, xml, rejection: false).ConfigureAwait(false);
        await AssertNoCreatedDraftsAsync(fixture).ConfigureAwait(false);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CreateItemDecodedMimeBoundaryIsInclusiveAndNextByteCannotDispatch(bool above)
    {
        var fixture = await CaptureFixture.CreateAsync(mailFolders: true).ConfigureAwait(false);
        await using var lifetime = fixture.ConfigureAwait(false);
        Authenticate(fixture);
        const string header = "Subject: EWS create boundary\r\nContent-Type: text/plain\r\n\r\n";
        var mime = header + new string('x', GatewayEwsItemCreateParser.MaximumMimeBytes + (above ? 1 : 0) - header.Length);
        using var content = XmlContent(CreateRequest(CreateMessage(mime)));
        using var response = await fixture.Client.PostAsync(new Uri(CanonicalPath, UriKind.Relative), content).ConfigureAwait(false);
        var xml = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (above)
        {
            Assert.AreEqual(HttpStatusCode.InternalServerError, response.StatusCode);
            AssertFault(xml, "ErrorDataSizeLimitExceeded");
            await fixture.AssertNoWorkerRequestsAsync().ConfigureAwait(false);
            await AssertNoCreatedDraftsAsync(fixture).ConfigureAwait(false);
        }
        else
        {
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, xml);
            var created = CreatedId(XDocument.Parse(xml).Descendants(Types + "ItemId").Single());
            await AssertCreatedDraftsAsync(fixture, [created], mime).ConfigureAwait(false);
        }
        await fixture.AssertRecordedResponseAsync("ews", CanonicalPath, (int)response.StatusCode, xml, rejection: false).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task CreateItemCommittedEmailWriteRejectsTheRequiredSnapshot()
    {
        CaptureFixture? fixture = null;
        fixture = await CaptureFixture.CreateAsync(mailFolders: true, decorateDispatcher: inner => new FindRaceDispatcher(inner,
            MailOperationKind.ReadMessages, async token =>
            {
                var database = Context(fixture!);
                await using var databaseLifetime = database.ConfigureAwait(false);
                var item = await database.Emails.FirstAsync(email => email.FolderId == GatewayEwsFixtureDomain.InboxId, token).ConfigureAwait(false);
                item.IsFlagged = !item.IsFlagged;
                await database.SaveChangesAsync(token).ConfigureAwait(false);
            })).ConfigureAwait(false);
        await using var lifetime = fixture.ConfigureAwait(false);
        Authenticate(fixture);
        AssertCode(XDocument.Parse(await CreateResponseAsync(fixture, CreateMessage(NativeMime)).ConfigureAwait(false)), "ErrorIrresolvableConflict");
        await AssertNoCreatedDraftsAsync(fixture).ConfigureAwait(false);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CreateItemImportReauthorizesMovedOrDeletedDestination(bool deleted)
    {
        CaptureFixture? fixture = null;
        fixture = await CaptureFixture.CreateAsync(mailFolders: true, decorateDispatcher: inner => new FindRaceDispatcher(inner,
            MailOperationKind.ReadMessages, async token =>
            {
                var database = Context(fixture!);
                await using var databaseLifetime = database.ConfigureAwait(false);
                var folder = await database.Folders.SingleAsync(item => item.Id == GatewayEwsFixtureDomain.ChildId, token).ConfigureAwait(false);
                if (deleted) database.Folders.Remove(folder);
                else folder.InboxId = GatewayEwsFixtureDomain.ForeignAccountId;
                await database.SaveChangesAsync(token).ConfigureAwait(false);
            })).ConfigureAwait(false);
        await using var lifetime = fixture.ConfigureAwait(false);
        Authenticate(fixture);
        var xml = await CreateResponseAsync(fixture, CreateMessage(NativeMime)).ConfigureAwait(false);
        var code = XDocument.Parse(xml).Descendants(Messages + "ResponseCode").Single().Value;
        Assert.IsTrue(code is "ErrorFolderNotFound" or "ErrorIrresolvableConflict", xml);
        await AssertNoCreatedDraftsAsync(fixture).ConfigureAwait(false);
    }

    [TestMethod]
    [DataRow("missing")]
    [DataRow("foreign")]
    [DataRow("expired")]
    public async Task CreateItemImportReauthorizesTheActualUploadedBlob(string condition)
    {
        CaptureFixture? fixture = null;
        fixture = await CaptureFixture.CreateAsync(mailFolders: true, decorateDispatcher: inner => new CreateUploadDispatcher(inner, async (blob, token) =>
        {
            var database = Context(fixture!);
            await using var databaseLifetime = database.ConfigureAwait(false);
            var uploaded = await database.JmapBlobs.SingleAsync(item => item.BlobId == blob, token).ConfigureAwait(false);
            if (condition is "missing") database.JmapBlobs.Remove(uploaded);
            else if (condition is "foreign") uploaded.AccountId = GatewayEwsFixtureDomain.ForeignAccountId;
            else uploaded.ExpiresAt = DateTime.UtcNow.AddHours(-1);
            await database.SaveChangesAsync(token).ConfigureAwait(false);
        })).ConfigureAwait(false);
        await using var lifetime = fixture.ConfigureAwait(false);
        Authenticate(fixture);
        AssertCode(XDocument.Parse(await CreateResponseAsync(fixture, CreateMessage(NativeMime)).ConfigureAwait(false)), "ErrorServerBusy");
        await AssertNoCreatedDraftsAsync(fixture).ConfigureAwait(false);
    }

    [TestMethod]
    [DataRow(0L)]
    [DataRow(1L)]
    public async Task CreateItemJournalFailureWithholdsTheIdEvenAfterImportCommit(long sequence)
    {
        var fixture = await CaptureFixture.CreateAsync(failSequence: sequence, mailFolders: true).ConfigureAwait(false);
        await using var lifetime = fixture.ConfigureAwait(false);
        Authenticate(fixture);
        using var content = XmlContent(CreateRequest(CreateMessage(NativeMime)));
        using var response = await fixture.Client.PostAsync(new Uri(CanonicalPath, UriKind.Relative), content).ConfigureAwait(false);
        var xml = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        AssertFault(xml, "ErrorServerBusy");
        Assert.IsFalse(xml.Contains("NoError", StringComparison.Ordinal) || XDocument.Parse(xml).Descendants(Types + "ItemId").Any());
        await fixture.AssertPresentationRecordCountAsync(sequence).ConfigureAwait(false);
        var database = Context(fixture);
        await using var databaseLifetime = database.ConfigureAwait(false);
        Assert.AreEqual(sequence == 0 ? 1 : 2, await database.Emails.CountAsync(item => item.IsDraft).ConfigureAwait(false));
        Assert.AreEqual(sequence == 0 ? 4 : 5, await database.Emails.CountAsync().ConfigureAwait(false));
    }

    [TestMethod]
    public async Task CreateItemImportReceiptReplayDoesNotCreateAnotherDraft()
    {
        var fixture = await CaptureFixture.CreateAsync(mailFolders: true, decorateDispatcher: inner => new CreateReplayDispatcher(inner)).ConfigureAwait(false);
        await using var lifetime = fixture.ConfigureAwait(false);
        Authenticate(fixture);
        var xml = await CreateResponseAsync(fixture, CreateMessage(NativeMime)).ConfigureAwait(false);
        var created = CreatedId(XDocument.Parse(xml).Descendants(Types + "ItemId").Single());
        await AssertCreatedDraftsAsync(fixture, [created], NativeMime).ConfigureAwait(false);
        var database = Context(fixture);
        await using var databaseLifetime = database.ConfigureAwait(false);
        Assert.AreEqual(2, await database.Emails.CountAsync(item => item.IsDraft).ConfigureAwait(false));
        Assert.AreEqual(5, await database.Emails.CountAsync().ConfigureAwait(false));
    }

    private static async Task AssertCreatedDraftsAsync(CaptureFixture fixture, Guid[] ids, string mime)
    {
        using var scope = fixture.DomainScopes.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
        var content = scope.ServiceProvider.GetRequiredService<MailboxMessageContentService>();
        var references = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in ids)
        {
            var email = await database.Emails.SingleAsync(item => item.Id == id).ConfigureAwait(false);
            Assert.AreEqual(GatewayEwsFixtureDomain.ChildId, email.FolderId);
            Assert.IsTrue(email.IsDraft);
            Assert.IsFalse(email.IsRead || email.IsFlagged || email.IsAnswered || email.IsDeleted);
            Assert.IsNull(email.RawMessage);
            Assert.AreEqual("azure-blob", email.RawMessageObjectProvider, StringComparer.Ordinal);
            var reference = content.TryGetReference(email)!;
            Assert.IsNotNull(reference);
            Assert.IsTrue(references.Add(reference.ObjectName));
            Assert.IsTrue(await fixture.BlobExistsAsync(reference.ObjectName).ConfigureAwait(false));
            CollectionAssert.AreEqual(Encoding.ASCII.GetBytes(mime), await content.ReadAsync(email, CancellationToken.None).ConfigureAwait(false));
        }
        Assert.AreEqual(ids.Length, await database.JmapBlobs.CountAsync().ConfigureAwait(false));
        Assert.AreEqual(4 + ids.Length, await database.Emails.CountAsync().ConfigureAwait(false));
        Assert.AreEqual(1 + ids.Length, await database.Emails.CountAsync(item => item.IsDraft).ConfigureAwait(false));
        Assert.IsFalse(await database.JmapBlobs.AnyAsync(blob => blob.Content != null || blob.ObjectProvider != "azure-blob").ConfigureAwait(false));
        Assert.AreEqual(0, await database.ExpungedUids.CountAsync().ConfigureAwait(false));
    }

    private static async Task AssertNoCreatedDraftsAsync(CaptureFixture fixture)
    {
        var database = Context(fixture);
        await using var databaseLifetime = database.ConfigureAwait(false);
        // The shared domain seeds four Inbox rows, including one draft.
        // Refusals must leave that complete baseline and all scopes intact.
        Assert.AreEqual(4, await database.Emails.CountAsync().ConfigureAwait(false));
        Assert.AreEqual(1, await database.Emails.CountAsync(item => item.IsDraft).ConfigureAwait(false));
        Assert.IsFalse(await database.Emails.AnyAsync(item => item.FolderId != GatewayEwsFixtureDomain.InboxId).ConfigureAwait(false));
    }

    private static async Task AssertJmapCreatedDraftAsync(CaptureFixture fixture, Guid id)
    {
        using var content = new StringContent($"{{\"using\":[\"urn:ietf:params:jmap:core\",\"urn:ietf:params:jmap:mail\"],\"methodCalls\":[[\"Email/get\",{{\"accountId\":\"A{GatewayEwsFixtureDomain.AccountId:N}\",\"ids\":[\"E{id:N}\"],\"properties\":[\"id\",\"mailboxIds\",\"keywords\"]}},\"created\"]]}}", Encoding.UTF8, "application/json");
        using var response = await fixture.Client.PostAsync(new Uri("/jmap/api", UriKind.Relative), content).ConfigureAwait(false);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        var item = document.RootElement.GetProperty("methodResponses")[0][1].GetProperty("list")[0];
        Assert.IsTrue(item.GetProperty("keywords").GetProperty("$draft").GetBoolean());
        Assert.IsTrue(item.GetProperty("mailboxIds").GetProperty($"M{GatewayEwsFixtureDomain.ChildId:N}").GetBoolean());
    }

    private static Guid CreatedId(XElement reference)
    {
        Assert.IsTrue(GatewayEwsItemIdCodec.TryDecode((string)reference.Attribute("Id")!, out var account, out var id));
        Assert.AreEqual(GatewayEwsFixtureDomain.AccountId, account);
        return id;
    }

    private static async Task<string> CreateResponseAsync(CaptureFixture fixture, string items, string? folder = null, string path = CanonicalPath)
    {
        using var content = XmlContent(CreateRequest(items, folder));
        using var response = await fixture.Client.PostAsync(new Uri(path, UriKind.Relative), content).ConfigureAwait(false);
        var xml = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, xml);
        Assert.IsTrue(response.Headers.CacheControl?.NoStore);
        await fixture.AssertRecordedResponseAsync("ews", path, 200, xml, rejection: false).ConfigureAwait(false);
        return xml;
    }

    private static string CreateMessage(string mime) => $"<t:Message><t:MimeContent CharacterSet='UTF-8'>{Convert.ToBase64String(Encoding.ASCII.GetBytes(mime))}</t:MimeContent></t:Message>";
    private static string CreateRequest(string items, string? folder = null, string attributes = "MessageDisposition='SaveOnly'") =>
        $"<s:Envelope xmlns:s='{GatewayEwsSoap.Soap}' xmlns:m='{Messages}' xmlns:t='{Types}'><s:Body><m:CreateItem {attributes}><m:SavedItemFolderId>{folder ?? $"<t:FolderId Id='{FolderId(GatewayEwsFixtureDomain.ChildId)}'/>"}</m:SavedItemFolderId><m:Items>{items}</m:Items></m:CreateItem></s:Body></s:Envelope>";

    private sealed class CreateReplayDispatcher(IApplicationRequestDispatcher inner) : IApplicationRequestDispatcher
    {
        public async Task<ApplicationResponse> DispatchAsync(ApplicationRequest request, CancellationToken cancellationToken = default)
        {
            var response = await inner.DispatchAsync(request, cancellationToken).ConfigureAwait(false);
            if (string.Equals(request.Operation, ApplicationOperations.MailOperationExecute, StringComparison.Ordinal)
                && JsonSerializer.Deserialize<MailOperationApplicationRequest>(request.Payload, JsonSerializerOptions.Web)!.Command.Operation == MailOperationKind.ImportMessages)
                CollectionAssert.AreEqual(response.Payload.ToArray(), (await inner.DispatchAsync(request, cancellationToken).ConfigureAwait(false)).Payload.ToArray());
            return response;
        }
    }

    private sealed class CreateUploadDispatcher(IApplicationRequestDispatcher inner, Func<string, CancellationToken, Task> after) : IApplicationRequestDispatcher
    {
        public async Task<ApplicationResponse> DispatchAsync(ApplicationRequest request, CancellationToken cancellationToken = default)
        {
            var response = await inner.DispatchAsync(request, cancellationToken).ConfigureAwait(false);
            if (string.Equals(request.Operation, ApplicationOperations.JmapUpload, StringComparison.Ordinal))
                await after(JsonSerializer.Deserialize<JmapApplicationResult>(response.Payload, JsonSerializerOptions.Web)!.BlobId!, cancellationToken).ConfigureAwait(false);
            return response;
        }
    }
}
