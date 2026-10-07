using System.Net;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using mk8.email.Application.Interfaces;
using mk8.email.Application.Services;
using mk8.email.Contracts.Messaging;
using mk8.email.Contracts.Storage;
using mk8.email.Gateway.Protocols.Ews;
using mk8.email.Infrastructure.Data;
using CaptureFixture = mk8.email.Messaging.Tests.GatewayHttpCaptureBoundaryTests.CaptureFixture;

namespace mk8.email.Messaging.Tests;

internal sealed partial class GatewayEwsRouteTests
{
    private static readonly string[] CopyCodes = ["ErrorAccessDenied", "ErrorItemNotFound", "ErrorItemNotFound", "ErrorInvalidIdMalformed",
        "ErrorInvalidChangeKey", "NoError", "NoError"];

    [TestMethod]
    [DataRow(CanonicalPath, false)]
    [DataRow(CanonicalPath, true)]
    [DataRow("/EwS/EXCHANGE.ASMX/", false)]
    [DataRow("/EwS/EXCHANGE.ASMX/", true)]
    public async Task CopyItemPreservesOriginalAzureMimeAndInheritedValuesInIndependentOwnedItem(string path, bool disabledJmap)
    {
        var fixture = await CaptureFixture.CreateAsync(ordinaryClient: true, mailFolders: true, disableJmap: disabledJmap).ConfigureAwait(false);
        await using var lifetime = fixture.ConfigureAwait(false);
        var source = await SeedItemAsync(fixture, GatewayEwsFixtureDomain.ChildId, NativeMime).ConfigureAwait(false);
        await SetCopySourceFlagsAsync(fixture, source).ConfigureAwait(false);
        var original = await ItemBlobReferenceAsync(fixture, source).ConfigureAwait(false);
        Authenticate(fixture);
        var read = await ReadItemAsync(fixture, source).ConfigureAwait(false);
        var key = (string)read.Element(Types + "ItemId")!.Attribute("ChangeKey")!;
        var xml = await CopyResponseAsync(fixture, $"<t:ItemId Id='{ItemId(source)}' ChangeKey='{key}'/>", path: path).ConfigureAwait(false);
        AssertCode(XDocument.Parse(xml), "NoError");
        var reference = XDocument.Parse(xml).Descendants(Types + "ItemId").Single();
        Assert.IsTrue(GatewayEwsItemIdCodec.TryDecode((string)reference.Attribute("Id")!, out var account, out var copied));
        Assert.AreEqual(GatewayEwsFixtureDomain.AccountId, account);
        Assert.AreNotEqual(source, copied);
        Assert.AreNotEqual(key, (string?)reference.Attribute("ChangeKey"), StringComparer.Ordinal);
        await AssertCopiedValuesAsync(fixture, source, copied, original).ConfigureAwait(false);
        var native = await MimeResponseAsync(fixture, $"<t:ItemId Id='{ItemId(copied)}'/>").ConfigureAwait(false);
        CollectionAssert.AreEqual(Encoding.ASCII.GetBytes(NativeMime), Convert.FromBase64String(XDocument.Parse(native).Descendants(Types + "MimeContent").Single().Value));
        if (!disabledJmap) await AssertJmapCopiedItemAsync(fixture, source, copied).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task CopyItemRepeatedSourcesCreateSeparateItemsAndKeepOrderedRefusals()
    {
        var fixture = await CaptureFixture.CreateAsync(mailFolders: true).ConfigureAwait(false);
        await using var lifetime = fixture.ConfigureAwait(false);
        var source = await SeedItemAsync(fixture, GatewayEwsFixtureDomain.ChildId).ConfigureAwait(false);
        var foreign = await SeedItemAsync(fixture, GatewayEwsFixtureDomain.ForeignFolderId).ConfigureAwait(false);
        Authenticate(fixture);
        var refs = $"<t:ItemId Id='{GatewayEwsItemIdCodec.Encode(GatewayEwsFixtureDomain.ForeignAccountId, foreign)}'/>"
            + $"<t:ItemId Id='{ItemId(foreign)}'/><t:ItemId Id='{ItemId(Guid.CreateVersion7())}'/>"
            + $"<t:ItemId Id='{FolderId(GatewayEwsFixtureDomain.ChildId)}'/><t:ItemId Id='{ItemId(source)}' ChangeKey='bad'/>"
            + $"<t:ItemId Id='{ItemId(source)}'/><t:ItemId Id='{ItemId(source)}'/>";
        var xml = await CopyResponseAsync(fixture, refs).ConfigureAwait(false);
        var document = XDocument.Parse(xml);
        CollectionAssert.AreEqual(CopyCodes, document.Descendants(Messages + "ResponseCode").Select(code => code.Value).ToArray());
        var ids = document.Descendants(Types + "ItemId").Select(item => (string)item.Attribute("Id")!).ToArray();
        Assert.HasCount(2, ids);
        Assert.AreNotEqual(ids[0], ids[1], StringComparer.Ordinal);
        var database = Context(fixture);
        await using var databaseLifetime = database.ConfigureAwait(false);
        Assert.AreEqual(2, await database.Emails.CountAsync(email => email.FolderId == GatewayEwsFixtureDomain.GrandchildId).ConfigureAwait(false));
        Assert.IsTrue(await database.Emails.AnyAsync(email => email.Id == source).ConfigureAwait(false));
        Assert.IsTrue(await database.Emails.AnyAsync(email => email.Id == foreign).ConfigureAwait(false));
        Assert.AreEqual(0, await database.ExpungedUids.CountAsync().ConfigureAwait(false));
        await fixture.AssertWorkerOperationsAsync(ApplicationOperations.JmapProfileGet, ApplicationOperations.MailOperationExecute,
            ApplicationOperations.MailOperationExecute).ConfigureAwait(false);
    }

    [TestMethod]
    [DataRow("foreign", "ErrorAccessDenied", false)]
    [DataRow("forged", "ErrorFolderNotFound", true)]
    [DataRow("missing", "ErrorFolderNotFound", true)]
    [DataRow("root", "ErrorAccessDenied", false)]
    [DataRow("malformed", "ErrorInvalidIdMalformed", false)]
    public async Task CopyItemDestinationReferencesCannotGrantForeignOrVirtualFolderAuthority(string kind, string code, bool dispatch)
    {
        var fixture = await CaptureFixture.CreateAsync(mailFolders: true).ConfigureAwait(false);
        await using var lifetime = fixture.ConfigureAwait(false);
        var source = await SeedItemAsync(fixture, GatewayEwsFixtureDomain.ChildId).ConfigureAwait(false);
        Authenticate(fixture);
        var id = kind switch
        {
            "foreign" => GatewayEwsFolderIdCodec.Encode(GatewayEwsFixtureDomain.ForeignAccountId, GatewayEwsFixtureDomain.ForeignFolderId),
            "forged" => FolderId(GatewayEwsFixtureDomain.ForeignFolderId),
            "missing" => FolderId(Guid.CreateVersion7()),
            "root" => FolderId(Guid.Empty),
            _ => "malformed",
        };
        AssertCode(XDocument.Parse(await CopyResponseAsync(fixture, $"<t:ItemId Id='{ItemId(source)}'/>", target: $"<t:FolderId Id='{id}'/>").ConfigureAwait(false)), code);
        var database = Context(fixture);
        await using var databaseLifetime = database.ConfigureAwait(false);
        Assert.IsTrue(await database.Emails.AnyAsync(email => email.Id == source).ConfigureAwait(false));
        Assert.AreEqual(0, await database.Emails.CountAsync(email => email.FolderId == GatewayEwsFixtureDomain.GrandchildId).ConfigureAwait(false));
        if (dispatch) await fixture.AssertWorkerOperationsAsync(ApplicationOperations.JmapProfileGet, ApplicationOperations.MailOperationExecute,
            ApplicationOperations.MailOperationExecute).ConfigureAwait(false);
        else await fixture.AssertWorkerOperationsAsync(ApplicationOperations.JmapProfileGet).ConfigureAwait(false);
    }

    [TestMethod]
    [DataRow("false")]
    [DataRow("0")]
    public async Task CopyItemCanSuppressReturnedIdsWithoutSuppressingTheActualCopy(string boolean)
    {
        var fixture = await CaptureFixture.CreateAsync(mailFolders: true).ConfigureAwait(false);
        await using var lifetime = fixture.ConfigureAwait(false);
        var source = await SeedItemAsync(fixture, GatewayEwsFixtureDomain.ChildId).ConfigureAwait(false);
        Authenticate(fixture);
        var xml = await CopyResponseAsync(fixture, $"<t:ItemId Id='{ItemId(source)}'/>", option: $"<m:ReturnNewItemIds>{boolean}</m:ReturnNewItemIds>").ConfigureAwait(false);
        AssertCode(XDocument.Parse(xml), "NoError");
        Assert.IsFalse(XDocument.Parse(xml).Descendants(Types + "ItemId").Any());
        Assert.IsEmpty(XDocument.Parse(xml).Descendants(Messages + "Items").Single().Elements());
        var database = Context(fixture);
        await using var databaseLifetime = database.ConfigureAwait(false);
        Assert.AreEqual(1, await database.Emails.CountAsync(email => email.FolderId == GatewayEwsFixtureDomain.GrandchildId).ConfigureAwait(false));
        Assert.IsTrue(await database.Emails.AnyAsync(email => email.Id == source).ConfigureAwait(false));
    }

    [TestMethod]
    [DataRow("<t:DistinguishedFolderId Id='inbox'/>", "<t:ItemId Id='opaque'/>", "", "ErrorInvalidRequest")]
    [DataRow("<t:FolderId Id='folder' ChangeKey='czEw'/>", "<t:ItemId Id='opaque'/>", "", "ErrorInvalidRequest")]
    [DataRow("<t:FolderId Id='folder'/>", "<t:RecurringMasterItemId OccurrenceId='opaque'/>", "", "ErrorSchemaValidation")]
    [DataRow("<t:FolderId Id='folder'/>", "<t:ItemId Id='opaque'/>", "<m:ReturnNewItemIds>True</m:ReturnNewItemIds>", "ErrorSchemaValidation")]
    public async Task CopyItemUnsupportedProfilesAreJournaledBeforeWorker(string target, string refs, string option, string code)
    {
        var fixture = await CaptureFixture.CreateAsync(mailFolders: true).ConfigureAwait(false);
        await using var lifetime = fixture.ConfigureAwait(false);
        Authenticate(fixture);
        using var content = XmlContent(CopyRequest(target, refs, option));
        using var response = await fixture.Client.PostAsync(new Uri(CanonicalPath, UriKind.Relative), content).ConfigureAwait(false);
        var xml = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.InternalServerError, response.StatusCode);
        AssertFault(xml, code);
        await fixture.AssertNoWorkerRequestsAsync().ConfigureAwait(false);
        await fixture.AssertRecordedResponseAsync("ews", CanonicalPath, 500, xml, rejection: false).ConfigureAwait(false);
    }

    [TestMethod]
    [DataRow("bad", "ErrorInvalidChangeKey", false)]
    [DataRow("c3RhbGU=", "ErrorIrresolvableConflict", true)]
    public async Task CopyItemInvalidOrStaleSourceKeysCannotCopy(string key, string code, bool read)
    {
        var fixture = await CaptureFixture.CreateAsync(mailFolders: true).ConfigureAwait(false);
        await using var lifetime = fixture.ConfigureAwait(false);
        var source = await SeedItemAsync(fixture, GatewayEwsFixtureDomain.ChildId).ConfigureAwait(false);
        Authenticate(fixture);
        AssertCode(XDocument.Parse(await CopyResponseAsync(fixture, $"<t:ItemId Id='{ItemId(source)}' ChangeKey='{key}'/>").ConfigureAwait(false)), code);
        var database = Context(fixture);
        await using var databaseLifetime = database.ConfigureAwait(false);
        Assert.AreEqual(0, await database.Emails.CountAsync(email => email.FolderId == GatewayEwsFixtureDomain.GrandchildId).ConfigureAwait(false));
        if (read) await fixture.AssertWorkerOperationsAsync(ApplicationOperations.JmapProfileGet, ApplicationOperations.MailOperationExecute).ConfigureAwait(false);
        else await fixture.AssertWorkerOperationsAsync(ApplicationOperations.JmapProfileGet).ConfigureAwait(false);
    }

    [TestMethod]
    [DataRow("flags")]
    [DataRow("scope")]
    [DataRow("deleted")]
    public async Task CopyItemCommittedSourceChangeRejectsBothAdmittedStatesWithoutCreatingAnything(string change)
    {
        CaptureFixture? fixture = null;
        var source = Guid.Empty;
        var count = 0;
        fixture = await CaptureFixture.CreateAsync(mailFolders: true, decorateDispatcher: inner => new FindRaceDispatcher(inner,
            MailOperationKind.ReadMessages, async token =>
            {
                if (Interlocked.CompareExchange(ref count, 1, 0) != 0) return;
                var database = Context(fixture!);
                await using var databaseLifetime = database.ConfigureAwait(false);
                var email = await database.Emails.SingleAsync(email => email.Id == source, token).ConfigureAwait(false);
                if (change is "scope") email.FolderId = GatewayEwsFixtureDomain.ForeignFolderId;
                else if (change is "deleted") email.IsDeleted = true;
                else email.IsRead = true;
                await database.SaveChangesAsync(token).ConfigureAwait(false);
            })).ConfigureAwait(false);
        await using var lifetime = fixture.ConfigureAwait(false);
        source = await SeedItemAsync(fixture, GatewayEwsFixtureDomain.ChildId).ConfigureAwait(false);
        var original = await ItemBlobReferenceAsync(fixture, source).ConfigureAwait(false);
        Authenticate(fixture);
        AssertCode(XDocument.Parse(await CopyResponseAsync(fixture, $"<t:ItemId Id='{ItemId(source)}'/>").ConfigureAwait(false)), "ErrorIrresolvableConflict");
        Assert.AreEqual(1, count);
        var verify = Context(fixture);
        await using var verifyLifetime = verify.ConfigureAwait(false);
        Assert.AreEqual(0, await verify.Emails.CountAsync(email => email.FolderId == GatewayEwsFixtureDomain.GrandchildId).ConfigureAwait(false));
        Assert.IsTrue(await verify.Emails.AnyAsync(email => email.Id == source).ConfigureAwait(false));
        Assert.AreEqual(0, await verify.ExpungedUids.CountAsync().ConfigureAwait(false));
        Assert.IsTrue(await fixture.BlobExistsAsync(original.ObjectName).ConfigureAwait(false));
    }

    [TestMethod]
    [DataRow(0L)]
    [DataRow(1L)]
    public async Task CopyItemJournalFailureWithholdsAcknowledgementButNeverDeletesTheSource(long sequence)
    {
        var fixture = await CaptureFixture.CreateAsync(failSequence: sequence, mailFolders: true).ConfigureAwait(false);
        await using var lifetime = fixture.ConfigureAwait(false);
        var source = await SeedItemAsync(fixture, GatewayEwsFixtureDomain.ChildId).ConfigureAwait(false);
        Authenticate(fixture);
        using var content = XmlContent(CopyRequest($"<t:FolderId Id='{FolderId(GatewayEwsFixtureDomain.GrandchildId)}'/>", $"<t:ItemId Id='{ItemId(source)}'/>"));
        using var response = await fixture.Client.PostAsync(new Uri(CanonicalPath, UriKind.Relative), content).ConfigureAwait(false);
        var xml = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        AssertFault(xml, "ErrorServerBusy");
        Assert.IsFalse(xml.Contains("NoError", StringComparison.Ordinal));
        Assert.IsFalse(XDocument.Parse(xml).Descendants(Types + "ItemId").Any());
        await fixture.AssertPresentationRecordCountAsync(sequence).ConfigureAwait(false);
        var database = Context(fixture);
        await using var databaseLifetime = database.ConfigureAwait(false);
        Assert.IsTrue(await database.Emails.AnyAsync(email => email.Id == source).ConfigureAwait(false));
        Assert.AreEqual((int)sequence, await database.Emails.CountAsync(email => email.FolderId == GatewayEwsFixtureDomain.GrandchildId).ConfigureAwait(false));
        if (sequence == 0) await fixture.AssertNoWorkerRequestsAsync().ConfigureAwait(false);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CopyItemWorkerRechecksDestinationScopeAfterCommittedPreflight(bool delete)
    {
        CaptureFixture? fixture = null;
        var count = 0;
        fixture = await CaptureFixture.CreateAsync(mailFolders: true, decorateDispatcher: inner => new FindRaceDispatcher(inner,
            MailOperationKind.ReadMessages, async token =>
            {
                if (Interlocked.CompareExchange(ref count, 1, 0) != 0) return;
                var database = Context(fixture!);
                await using var databaseLifetime = database.ConfigureAwait(false);
                var folder = await database.Folders.SingleAsync(folder => folder.Id == GatewayEwsFixtureDomain.GrandchildId, token).ConfigureAwait(false);
                if (delete) database.Folders.Remove(folder);
                else folder.InboxId = GatewayEwsFixtureDomain.ForeignAccountId;
                await database.SaveChangesAsync(token).ConfigureAwait(false);
            })).ConfigureAwait(false);
        await using var lifetime = fixture.ConfigureAwait(false);
        var source = await SeedItemAsync(fixture, GatewayEwsFixtureDomain.ChildId).ConfigureAwait(false);
        Authenticate(fixture);
        var xml = await CopyResponseAsync(fixture, $"<t:ItemId Id='{ItemId(source)}'/>").ConfigureAwait(false);
        var code = XDocument.Parse(xml).Descendants(Messages + "ResponseCode").Single().Value;
        Assert.IsTrue(code is "ErrorFolderNotFound" or "ErrorIrresolvableConflict");
        Assert.AreEqual(1, count);
        Assert.IsFalse(XDocument.Parse(xml).Descendants(Types + "ItemId").Any());
        var verify = Context(fixture);
        await using var verifyLifetime = verify.ConfigureAwait(false);
        Assert.IsTrue(await verify.Emails.AnyAsync(email => email.Id == source).ConfigureAwait(false));
        Assert.AreEqual(0, await verify.Emails.CountAsync(email => email.FolderId == GatewayEwsFixtureDomain.GrandchildId).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task CopyItemExactWorkerReplayReturnsOneCreatedIdentityWithoutRepeatingCopy()
    {
        var replay = new CopyReplay();
        var fixture = await CaptureFixture.CreateAsync(mailFolders: true, decorateDispatcher: inner => new CopyReplayDispatcher(inner, replay)).ConfigureAwait(false);
        await using var lifetime = fixture.ConfigureAwait(false);
        var source = await SeedItemAsync(fixture, GatewayEwsFixtureDomain.ChildId).ConfigureAwait(false);
        Authenticate(fixture);
        var xml = await CopyResponseAsync(fixture, $"<t:ItemId Id='{ItemId(source)}'/>").ConfigureAwait(false);
        AssertCode(XDocument.Parse(xml), "NoError");
        Assert.AreEqual(1, replay.Count);
        var database = Context(fixture);
        await using var databaseLifetime = database.ConfigureAwait(false);
        Assert.AreEqual(1, await database.Emails.CountAsync(email => email.FolderId == GatewayEwsFixtureDomain.GrandchildId).ConfigureAwait(false));
        Assert.IsTrue(await database.Emails.AnyAsync(email => email.Id == source).ConfigureAwait(false));
        Assert.AreEqual(0, await database.ExpungedUids.CountAsync().ConfigureAwait(false));
    }

    private static async Task SetCopySourceFlagsAsync(CaptureFixture fixture, Guid source)
    {
        var database = Context(fixture);
        await using var lifetime = database.ConfigureAwait(false);
        var email = await database.Emails.SingleAsync(email => email.Id == source).ConfigureAwait(false);
        email.IsRead = email.IsDraft = email.IsFlagged = email.IsAnswered = true;
        email.Keywords = ["custom-keyword"];
        await database.SaveChangesAsync().ConfigureAwait(false);
    }

    private static async Task AssertCopiedValuesAsync(CaptureFixture fixture, Guid source, Guid copied, LargeObjectReference original)
    {
        using var scope = fixture.DomainScopes.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
        var first = await database.Emails.SingleAsync(email => email.Id == source).ConfigureAwait(false);
        var next = await database.Emails.SingleAsync(email => email.Id == copied).ConfigureAwait(false);
        Assert.AreEqual(GatewayEwsFixtureDomain.GrandchildId, next.FolderId);
        Assert.IsTrue(next.IsRead && next.IsDraft && next.IsFlagged && next.IsAnswered);
        CollectionAssert.AreEqual(first.Keywords!, next.Keywords!);
        Assert.AreEqual(first.ReceivedAt, next.ReceivedAt);
        Assert.AreEqual(first.SizeBytes, next.SizeBytes);
        Assert.IsNull(next.RawMessage);
        Assert.AreEqual("azure-blob", next.RawMessageObjectProvider, StringComparer.Ordinal);
        Assert.AreNotEqual(first.RawMessageObjectName, next.RawMessageObjectName, StringComparer.Ordinal);
        Assert.IsTrue(await fixture.BlobExistsAsync(original.ObjectName).ConfigureAwait(false));
        var content = scope.ServiceProvider.GetRequiredService<MailboxMessageContentService>();
        CollectionAssert.AreEqual(Encoding.ASCII.GetBytes(NativeMime), await content.ReadAsync(first, CancellationToken.None).ConfigureAwait(false));
        CollectionAssert.AreEqual(Encoding.ASCII.GetBytes(NativeMime), await content.ReadAsync(next, CancellationToken.None).ConfigureAwait(false));
        Assert.AreEqual(0, await database.ExpungedUids.CountAsync().ConfigureAwait(false));
    }

    private static async Task AssertJmapCopiedItemAsync(CaptureFixture fixture, Guid source, Guid copied)
    {
        using var content = new StringContent($"{{\"using\":[\"urn:ietf:params:jmap:core\",\"urn:ietf:params:jmap:mail\"],\"methodCalls\":[[\"Email/get\",{{\"accountId\":\"A{GatewayEwsFixtureDomain.AccountId:N}\",\"ids\":[\"E{source:N}\",\"E{copied:N}\"],\"properties\":[\"id\",\"mailboxIds\"]}},\"copied\"]]}}", Encoding.UTF8, "application/json");
        using var response = await fixture.Client.PostAsync(new Uri("/jmap/api", UriKind.Relative), content).ConfigureAwait(false);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var args = document.RootElement.GetProperty("methodResponses")[0][1];
        Assert.AreEqual(0, args.GetProperty("notFound").GetArrayLength());
        Assert.AreEqual(2, args.GetProperty("list").GetArrayLength());
        var copy = args.GetProperty("list").EnumerateArray().Single(item => string.Equals(item.GetProperty("id").GetString(), $"E{copied:N}", StringComparison.Ordinal));
        Assert.IsTrue(copy.GetProperty("mailboxIds").GetProperty($"M{GatewayEwsFixtureDomain.GrandchildId:N}").GetBoolean());
    }

    private static async Task<string> CopyResponseAsync(CaptureFixture fixture, string refs, string? target = null, string option = "", string path = CanonicalPath)
    {
        using var content = XmlContent(CopyRequest(target ?? $"<t:FolderId Id='{FolderId(GatewayEwsFixtureDomain.GrandchildId)}'/>", refs, option));
        using var response = await fixture.Client.PostAsync(new Uri(path, UriKind.Relative), content).ConfigureAwait(false);
        var xml = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, xml);
        Assert.IsTrue(response.Headers.CacheControl?.NoStore);
        await fixture.AssertRecordedResponseAsync("ews", path, 200, xml, rejection: false).ConfigureAwait(false);
        return xml;
    }

    private static string CopyRequest(string target, string refs, string option = "") =>
        $"<s:Envelope xmlns:s='{GatewayEwsSoap.Soap}' xmlns:m='{Messages}' xmlns:t='{Types}'><s:Body><m:CopyItem><m:ToFolderId>{target}</m:ToFolderId><m:ItemIds>{refs}</m:ItemIds>{option}</m:CopyItem></s:Body></s:Envelope>";

    private sealed class CopyReplay { public int Count { get; set; } }

    private sealed class CopyReplayDispatcher(IApplicationRequestDispatcher inner, CopyReplay replay) : IApplicationRequestDispatcher
    {
        public async Task<ApplicationResponse> DispatchAsync(ApplicationRequest request, CancellationToken cancellationToken = default)
        {
            var response = await inner.DispatchAsync(request, cancellationToken).ConfigureAwait(false);
            if (string.Equals(request.Operation, ApplicationOperations.MailOperationExecute, StringComparison.Ordinal)
                && JsonSerializer.Deserialize<MailOperationApplicationRequest>(request.Payload, JsonSerializerOptions.Web)!.Command.Operation == MailOperationKind.CopyMessages)
            {
                var again = await inner.DispatchAsync(request, cancellationToken).ConfigureAwait(false);
                CollectionAssert.AreEqual(response.Payload.ToArray(), again.Payload.ToArray());
                replay.Count++;
            }
            return response;
        }
    }
}
