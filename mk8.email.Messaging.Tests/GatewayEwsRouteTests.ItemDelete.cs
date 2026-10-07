using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Azure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using mk8.email.Application.Interfaces;
using mk8.email.Application.Services;
using mk8.email.Contracts.Messaging;
using mk8.email.Contracts.Storage;
using mk8.email.Infrastructure.Data;
using mk8.email.Gateway.Protocols.Ews;
using CaptureFixture = mk8.email.Messaging.Tests.GatewayHttpCaptureBoundaryTests.CaptureFixture;

namespace mk8.email.Messaging.Tests;

internal sealed partial class GatewayEwsRouteTests
{
    private static readonly string[] DeleteOutcomes = ["ErrorAccessDenied", "ErrorItemNotFound", "ErrorInvalidIdMalformed",
        "ErrorInvalidChangeKey", "ErrorItemNotFound", "NoError", "ErrorInvalidRequest"];

    [TestMethod]
    [DataRow(CanonicalPath, false)]
    [DataRow(CanonicalPath, true)]
    [DataRow("/eWs/EXCHANGE.ASMX/", false)]
    [DataRow("/eWs/EXCHANGE.ASMX/", true)]
    public async Task DeleteItemCommitsAzureRemovalAndOriginalSessionWithoutPublicJmap(string path, bool disabledJmap)
    {
        var fixture = await CaptureFixture.CreateAsync(ordinaryClient: true, mailFolders: true, disableJmap: disabledJmap).ConfigureAwait(false);
        await using var lifetime = fixture.ConfigureAwait(false);
        var item = await SeedItemAsync(fixture, GatewayEwsFixtureDomain.ChildId, NativeMime).ConfigureAwait(false);
        var reference = await ItemBlobReferenceAsync(fixture, item).ConfigureAwait(false);
        Assert.IsTrue(await fixture.BlobExistsAsync(reference.ObjectName).ConfigureAwait(false));
        Authenticate(fixture);
        var before = await ReadItemAsync(fixture, item).ConfigureAwait(false);
        var key = (string)before.Element(Types + "ItemId")!.Attribute("ChangeKey")!;
        var xml = await DeleteResponseAsync(fixture, $"<t:ItemId Id='{ItemId(item)}' ChangeKey='{key}'/>", path).ConfigureAwait(false);
        AssertCode(XDocument.Parse(xml), "NoError");
        Assert.IsFalse(xml.Contains("PRIVATE", StringComparison.Ordinal));
        Assert.IsFalse(XDocument.Parse(xml).Descendants(Types + "ItemId").Any());
        await AssertItemRemovedAsync(fixture, item, reference).ConfigureAwait(false);
        await AssertDeletedItemNotReadableAsync(fixture, item).ConfigureAwait(false);
        if (!disabledJmap) await AssertJmapItemRemovedAsync(fixture, item).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task DeleteItemPreservesOrderedForeignForgedMissingMalformedAndDuplicateOutcomes()
    {
        var fixture = await CaptureFixture.CreateAsync(mailFolders: true).ConfigureAwait(false);
        await using var lifetime = fixture.ConfigureAwait(false);
        var item = await SeedItemAsync(fixture, GatewayEwsFixtureDomain.ChildId).ConfigureAwait(false);
        var foreign = await SeedItemAsync(fixture, GatewayEwsFixtureDomain.ForeignFolderId).ConfigureAwait(false);
        Authenticate(fixture);
        var references = $"<t:ItemId Id='{GatewayEwsItemIdCodec.Encode(GatewayEwsFixtureDomain.ForeignAccountId, foreign)}'/>"
            + $"<t:ItemId Id='{ItemId(foreign)}'/><t:ItemId Id='{FolderId(GatewayEwsFixtureDomain.ChildId)}'/>"
            + $"<t:ItemId Id='{ItemId(item)}' ChangeKey='bad'/><t:ItemId Id='{ItemId(Guid.CreateVersion7())}'/>"
            + $"<t:ItemId Id='{ItemId(item)}'/><t:ItemId Id='{ItemId(item)}'/>";
        var xml = await DeleteResponseAsync(fixture, references).ConfigureAwait(false);
        CollectionAssert.AreEqual(DeleteOutcomes, XDocument.Parse(xml).Descendants(Messages + "ResponseCode").Select(code => code.Value).ToArray());
        var database = Context(fixture);
        await using var databaseLifetime = database.ConfigureAwait(false);
        Assert.IsTrue(await database.Emails.AnyAsync(email => email.Id == foreign).ConfigureAwait(false));
        Assert.IsFalse(await database.Emails.AnyAsync(email => email.Id == item).ConfigureAwait(false));
        Assert.HasCount(1, await database.ExpungedUids.Where(uid => uid.FolderId == GatewayEwsFixtureDomain.ChildId).ToArrayAsync().ConfigureAwait(false));
        await fixture.AssertWorkerOperationsAsync(ApplicationOperations.JmapProfileGet, ApplicationOperations.MailOperationExecute,
            ApplicationOperations.MailOperationExecute).ConfigureAwait(false);
    }

    [TestMethod]
    [DataRow("!bad", "ErrorInvalidChangeKey", false)]
    [DataRow("c3RhbGU=", "ErrorIrresolvableConflict", true)]
    public async Task DeleteItemRefusesMalformedAndStaleChangeKeysWithoutMutation(string key, string code, bool read)
    {
        var fixture = await CaptureFixture.CreateAsync(mailFolders: true).ConfigureAwait(false);
        await using var lifetime = fixture.ConfigureAwait(false);
        var item = await SeedItemAsync(fixture, GatewayEwsFixtureDomain.ChildId).ConfigureAwait(false);
        Authenticate(fixture);
        AssertCode(XDocument.Parse(await DeleteResponseAsync(fixture, $"<t:ItemId Id='{ItemId(item)}' ChangeKey='{key}'/>").ConfigureAwait(false)), code);
        var database = Context(fixture);
        await using var databaseLifetime = database.ConfigureAwait(false);
        Assert.IsTrue(await database.Emails.AnyAsync(email => email.Id == item).ConfigureAwait(false));
        Assert.AreEqual(0, await database.ExpungedUids.CountAsync().ConfigureAwait(false));
        if (read) await fixture.AssertWorkerOperationsAsync(ApplicationOperations.JmapProfileGet, ApplicationOperations.MailOperationExecute).ConfigureAwait(false);
        else await fixture.AssertWorkerOperationsAsync(ApplicationOperations.JmapProfileGet).ConfigureAwait(false);
    }

    [TestMethod]
    [DataRow("DeleteType='SoftDelete' SuppressReadReceipts='true'", "ErrorInvalidRequest")]
    [DataRow("DeleteType='MoveToDeletedItems' SuppressReadReceipts='true'", "ErrorInvalidRequest")]
    [DataRow("DeleteType='HardDelete'", "ErrorInvalidRequest")]
    [DataRow("DeleteType='HardDelete' SuppressReadReceipts='false'", "ErrorInvalidRequest")]
    [DataRow("DeleteType='HardDelete' SuppressReadReceipts='true' AffectedTaskOccurrences='AllOccurrences'", "ErrorSchemaValidation")]
    public async Task DeleteItemUnsupportedSemanticsAreJournaledBeforeAnyWorker(string attributes, string code)
    {
        var fixture = await CaptureFixture.CreateAsync(mailFolders: true).ConfigureAwait(false);
        await using var lifetime = fixture.ConfigureAwait(false);
        Authenticate(fixture);
        using var content = XmlContent(DeleteRequest("<t:ItemId Id='opaque'/>", attributes));
        using var response = await fixture.Client.PostAsync(new Uri(CanonicalPath, UriKind.Relative), content).ConfigureAwait(false);
        var xml = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.InternalServerError, response.StatusCode);
        AssertFault(xml, code);
        await fixture.AssertNoWorkerRequestsAsync().ConfigureAwait(false);
        await fixture.AssertRecordedResponseAsync("ews", CanonicalPath, 500, xml, rejection: false).ConfigureAwait(false);
    }

    [TestMethod]
    [DataRow("flags")]
    [DataRow("scope")]
    [DataRow("deleted")]
    public async Task DeleteItemRealCommittedWriterInvalidatesAdmittedStateBeforeMutation(string change)
    {
        CaptureFixture? fixture = null;
        var item = Guid.Empty;
        var injections = 0;
        fixture = await CaptureFixture.CreateAsync(mailFolders: true, decorateDispatcher: inner => new FindRaceDispatcher(inner,
            MailOperationKind.ReadMessages, async token =>
            {
                if (Interlocked.CompareExchange(ref injections, 1, 0) != 0) return;
                var database = Context(fixture!);
                await using var databaseLifetime = database.ConfigureAwait(false);
                var email = await database.Emails.SingleAsync(email => email.Id == item, token).ConfigureAwait(false);
                if (change is "flags") email.IsRead = true;
                else if (change is "scope") email.FolderId = GatewayEwsFixtureDomain.ForeignFolderId;
                else email.IsDeleted = true;
                await database.SaveChangesAsync(token).ConfigureAwait(false);
            })).ConfigureAwait(false);
        await using var lifetime = fixture.ConfigureAwait(false);
        item = await SeedItemAsync(fixture, GatewayEwsFixtureDomain.ChildId).ConfigureAwait(false);
        var reference = await ItemBlobReferenceAsync(fixture, item).ConfigureAwait(false);
        Authenticate(fixture);
        AssertCode(XDocument.Parse(await DeleteResponseAsync(fixture, $"<t:ItemId Id='{ItemId(item)}'/>").ConfigureAwait(false)), "ErrorIrresolvableConflict");
        Assert.AreEqual(1, injections);
        var verify = Context(fixture);
        await using var verifyLifetime = verify.ConfigureAwait(false);
        Assert.IsTrue(await verify.Emails.AnyAsync(email => email.Id == item).ConfigureAwait(false));
        Assert.AreEqual(0, await verify.ExpungedUids.CountAsync().ConfigureAwait(false));
        using var scope = fixture.DomainScopes.CreateScope();
        using var bytes = new MemoryStream();
        await scope.ServiceProvider.GetRequiredService<ILargeObjectStore>().CopyToAsync(reference, bytes).ConfigureAwait(false);
        CollectionAssert.AreEqual(Encoding.UTF8.GetBytes(ItemMime), bytes.ToArray());
    }

    [TestMethod]
    [DataRow("missing")]
    [DataRow("wrong")]
    [DataRow("plaintext")]
    public async Task DeleteItemRequiresTlsAndWorkerPasswordAuthentication(string mode)
    {
        var fixture = await CaptureFixture.CreateAsync(mailFolders: true).ConfigureAwait(false);
        await using var lifetime = fixture.ConfigureAwait(false);
        var item = await SeedItemAsync(fixture, GatewayEwsFixtureDomain.ChildId).ConfigureAwait(false);
        Authenticate(fixture);
        if (mode is "missing") fixture.Client.DefaultRequestHeaders.Authorization = null;
        if (mode is "wrong") fixture.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("owner@example.test:wrong")));
        if (mode is "plaintext")
        {
            fixture.Client.DefaultRequestHeaders.Remove("X-Forwarded-Proto");
            fixture.Client.DefaultRequestHeaders.Add("X-Forwarded-Proto", "http");
        }
        using var content = XmlContent(DeleteRequest($"<t:ItemId Id='{ItemId(item)}'/>"));
        using var response = await fixture.Client.PostAsync(new Uri(CanonicalPath, UriKind.Relative), content).ConfigureAwait(false);
        var xml = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        var status = mode is "plaintext" ? HttpStatusCode.Forbidden : HttpStatusCode.Unauthorized;
        Assert.AreEqual(status, response.StatusCode);
        AssertFault(xml, "ErrorAccessDenied");
        await fixture.AssertRecordedResponseAsync("ews", CanonicalPath, (int)status, xml, rejection: false).ConfigureAwait(false);
        if (mode is "wrong") await fixture.AssertWorkerOperationsAsync(ApplicationOperations.JmapProfileGet).ConfigureAwait(false);
        else await fixture.AssertNoWorkerRequestsAsync().ConfigureAwait(false);
        var database = Context(fixture);
        await using var databaseLifetime = database.ConfigureAwait(false);
        Assert.IsTrue(await database.Emails.AnyAsync(email => email.Id == item).ConfigureAwait(false));
    }

    [TestMethod]
    [DataRow(0L)]
    [DataRow(1L)]
    public async Task DeleteItemJournalFailureWithholdsAcknowledgementNotCommittedDomainDeletion(long sequence)
    {
        var fixture = await CaptureFixture.CreateAsync(failSequence: sequence, mailFolders: true).ConfigureAwait(false);
        await using var lifetime = fixture.ConfigureAwait(false);
        var item = await SeedItemAsync(fixture, GatewayEwsFixtureDomain.ChildId).ConfigureAwait(false);
        Authenticate(fixture);
        using var content = XmlContent(DeleteRequest($"<t:ItemId Id='{ItemId(item)}'/>"));
        using var response = await fixture.Client.PostAsync(new Uri(CanonicalPath, UriKind.Relative), content).ConfigureAwait(false);
        var xml = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        AssertFault(xml, "ErrorServerBusy");
        Assert.IsFalse(xml.Contains("NoError", StringComparison.Ordinal));
        await fixture.AssertPresentationRecordCountAsync(sequence).ConfigureAwait(false);
        var database = Context(fixture);
        await using var databaseLifetime = database.ConfigureAwait(false);
        Assert.AreEqual(sequence == 0, await database.Emails.AnyAsync(email => email.Id == item).ConfigureAwait(false));
        if (sequence == 0) await fixture.AssertNoWorkerRequestsAsync().ConfigureAwait(false);
    }

    [TestMethod]
    public async Task DeleteItemDurableWorkerReplayDoesNotRepeatExpunge()
    {
        var replay = new DeleteReplay();
        var fixture = await CaptureFixture.CreateAsync(mailFolders: true, decorateDispatcher: inner => new DeleteReplayDispatcher(inner, replay)).ConfigureAwait(false);
        await using var lifetime = fixture.ConfigureAwait(false);
        var item = await SeedItemAsync(fixture, GatewayEwsFixtureDomain.ChildId).ConfigureAwait(false);
        Authenticate(fixture);
        AssertCode(XDocument.Parse(await DeleteResponseAsync(fixture, $"<t:ItemId Id='{ItemId(item)}'/>").ConfigureAwait(false)), "NoError");
        Assert.AreEqual(1, replay.Count);
        var database = Context(fixture);
        await using var databaseLifetime = database.ConfigureAwait(false);
        Assert.HasCount(1, await database.ExpungedUids.Where(uid => uid.FolderId == GatewayEwsFixtureDomain.ChildId).ToArrayAsync().ConfigureAwait(false));
        Assert.IsFalse(await database.Emails.AnyAsync(email => email.Id == item).ConfigureAwait(false));
    }

    private static async Task<LargeObjectReference> ItemBlobReferenceAsync(CaptureFixture fixture, Guid item)
    {
        using var scope = fixture.DomainScopes.CreateScope();
        var email = await scope.ServiceProvider.GetRequiredService<EmailDbContext>().Emails.SingleAsync(email => email.Id == item).ConfigureAwait(false);
        Assert.IsNull(email.RawMessage);
        return scope.ServiceProvider.GetRequiredService<MailboxMessageContentService>().TryGetReference(email)!;
    }

    private static async Task AssertItemRemovedAsync(CaptureFixture fixture, Guid item, LargeObjectReference reference)
    {
        var database = Context(fixture);
        await using var lifetime = database.ConfigureAwait(false);
        Assert.IsFalse(await database.Emails.AnyAsync(email => email.Id == item).ConfigureAwait(false));
        var expunged = await database.ExpungedUids.SingleAsync(uid => uid.FolderId == GatewayEwsFixtureDomain.ChildId).ConfigureAwait(false);
        Assert.AreEqual(100, expunged.Uid);
        Assert.IsGreaterThan(0L, expunged.ModSeq);
        using var scope = fixture.DomainScopes.CreateScope();
        using var destination = new MemoryStream();
        Assert.IsFalse(await fixture.BlobExistsAsync(reference.ObjectName).ConfigureAwait(false));
        var error = await Assert.ThrowsAsync<RequestFailedException>(() => scope.ServiceProvider.GetRequiredService<ILargeObjectStore>()
            .CopyToAsync(reference, destination)).ConfigureAwait(false);
        Assert.IsTrue(error.Status is 404 or 412);
    }

    private static async Task AssertDeletedItemNotReadableAsync(CaptureFixture fixture, Guid item)
    {
        using var content = XmlContent(ItemRequest($"<t:ItemId Id='{ItemId(item)}'/>", MimeOption));
        using var response = await fixture.Client.PostAsync(new Uri(CanonicalPath, UriKind.Relative), content).ConfigureAwait(false);
        var xml = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        AssertCode(XDocument.Parse(xml), "ErrorItemNotFound");
        Assert.IsFalse(XDocument.Parse(xml).Descendants(Types + "MimeContent").Any());
        await fixture.AssertRecordedResponseAsync("ews", CanonicalPath, 200, xml, rejection: false).ConfigureAwait(false);
    }

    private static async Task AssertJmapItemRemovedAsync(CaptureFixture fixture, Guid item)
    {
        using var content = new StringContent($"{{\"using\":[\"urn:ietf:params:jmap:core\",\"urn:ietf:params:jmap:mail\"],\"methodCalls\":[[\"Email/get\",{{\"accountId\":\"A{GatewayEwsFixtureDomain.AccountId:N}\",\"ids\":[\"E{item:N}\"],\"properties\":[\"id\"]}},\"deleted\"]]}}", Encoding.UTF8, "application/json");
        using var response = await fixture.Client.PostAsync(new Uri("/jmap/api", UriKind.Relative), content).ConfigureAwait(false);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var arguments = document.RootElement.GetProperty("methodResponses")[0][1];
        Assert.AreEqual(0, arguments.GetProperty("list").GetArrayLength());
        Assert.AreEqual($"E{item:N}", arguments.GetProperty("notFound")[0].GetString(), StringComparer.Ordinal);
    }

    private static async Task<string> DeleteResponseAsync(CaptureFixture fixture, string refs, string path = CanonicalPath)
    {
        using var content = XmlContent(DeleteRequest(refs));
        using var response = await fixture.Client.PostAsync(new Uri(path, UriKind.Relative), content).ConfigureAwait(false);
        var xml = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, xml);
        Assert.IsTrue(response.Headers.CacheControl?.NoStore);
        Assert.AreEqual("text/xml", response.Content.Headers.ContentType?.MediaType, StringComparer.Ordinal);
        await fixture.AssertRecordedResponseAsync("ews", path, 200, xml, rejection: false).ConfigureAwait(false);
        return xml;
    }

    private static string DeleteRequest(string refs, string attributes = "DeleteType='HardDelete' SuppressReadReceipts='true'") =>
        $"<s:Envelope xmlns:s='{GatewayEwsSoap.Soap}' xmlns:m='{Messages}' xmlns:t='{Types}'><s:Body><m:DeleteItem {attributes}><m:ItemIds>{refs}</m:ItemIds></m:DeleteItem></s:Body></s:Envelope>";

    private sealed class DeleteReplay { public int Count { get; set; } }

    private sealed class DeleteReplayDispatcher(IApplicationRequestDispatcher inner, DeleteReplay replay) : IApplicationRequestDispatcher
    {
        public async Task<ApplicationResponse> DispatchAsync(ApplicationRequest request, CancellationToken cancellationToken = default)
        {
            var response = await inner.DispatchAsync(request, cancellationToken).ConfigureAwait(false);
            if (string.Equals(request.Operation, ApplicationOperations.MailOperationExecute, StringComparison.Ordinal)
                && JsonSerializer.Deserialize<MailOperationApplicationRequest>(request.Payload, JsonSerializerOptions.Web)!.Command.Operation == MailOperationKind.MutateMessages)
            {
                var again = await inner.DispatchAsync(request, cancellationToken).ConfigureAwait(false);
                CollectionAssert.AreEqual(response.Payload.ToArray(), again.Payload.ToArray());
                replay.Count++;
            }
            return response;
        }
    }
}
