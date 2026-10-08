using System.Net;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using mk8.email.Application.Services;
using mk8.email.Contracts.Messaging;
using mk8.email.Gateway.Protocols.Ews;
using mk8.email.Infrastructure.Data;
using CaptureFixture = mk8.email.Messaging.Tests.GatewayHttpCaptureBoundaryTests.CaptureFixture;

namespace mk8.email.Messaging.Tests;

internal sealed partial class GatewayEwsRouteTests
{
    private const string UpdateAttributes = "ConflictResolution='NeverOverwrite' MessageDisposition='SaveOnly' SuppressReadReceipts='true'";
    private static readonly string[] UpdateCodes = ["ErrorAccessDenied", "ErrorItemNotFound", "ErrorItemNotFound", "ErrorInvalidIdMalformed", "ErrorInvalidChangeKey", "NoError", "ErrorInvalidRequest"];

    [TestMethod]
    [DataRow(CanonicalPath, false, false)]
    [DataRow(CanonicalPath, true, false)]
    [DataRow("/eWs/EXCHANGE.ASMX/", false, true)]
    [DataRow("/eWs/EXCHANGE.ASMX/", true, true)]
    public async Task UpdateItemChangesOnlyReadStateWithoutReplacingNativeContent(string path, bool read, bool disabledJmap)
    {
        var fixture = await CaptureFixture.CreateAsync(ordinaryClient: true, mailFolders: true, disableJmap: disabledJmap).ConfigureAwait(false);
        await using var lifetime = fixture.ConfigureAwait(false);
        var source = await SeedItemAsync(fixture, GatewayEwsFixtureDomain.ChildId, NativeMime).ConfigureAwait(false);
        await SetCopySourceFlagsAsync(fixture, source).ConfigureAwait(false);
        var database = Context(fixture);
        await using var databaseLifetime = database.ConfigureAwait(false);
        var before = await database.Emails.SingleAsync(email => email.Id == source).ConfigureAwait(false);
        before.IsRead = !read;
        await database.SaveChangesAsync().ConfigureAwait(false);
        var modseq = before.ModSeq;
        var uid = before.Uid;
        var received = before.ReceivedAt;
        var blob = await ItemBlobReferenceAsync(fixture, source).ConfigureAwait(false);
        Authenticate(fixture);
        var old = await ReadItemAsync(fixture, source).ConfigureAwait(false);
        var key = (string)old.Element(Types + "ItemId")!.Attribute("ChangeKey")!;
        var xml = await UpdateResponseAsync(fixture, UpdateChange(ItemId(source), read, key), path).ConfigureAwait(false);
        AssertCode(XDocument.Parse(xml), "NoError");
        var result = XDocument.Parse(xml).Descendants(Types + "ItemId").Single();
        Assert.AreEqual(ItemId(source), (string?)result.Attribute("Id"), StringComparer.Ordinal);
        Assert.AreNotEqual(key, (string?)result.Attribute("ChangeKey"), StringComparer.Ordinal);
        Assert.AreEqual(blob, await ItemBlobReferenceAsync(fixture, source).ConfigureAwait(false));
        await AssertUpdatedValuesAsync(fixture, source, read, uid, received, modseq).ConfigureAwait(false);
        var current = await ReadItemAsync(fixture, source).ConfigureAwait(false);
        Assert.AreEqual(read ? "true" : "false", current.Element(Types + "IsRead")!.Value, StringComparer.Ordinal);
        Assert.AreEqual((string?)result.Attribute("ChangeKey"), (string?)current.Element(Types + "ItemId")!.Attribute("ChangeKey"), StringComparer.Ordinal);
        if (!disabledJmap) await AssertJmapReadStateAsync(fixture, source, read).ConfigureAwait(false);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task UpdateItemNoOpPreservesChangeKeyUidAndModseq(bool read)
    {
        var fixture = await CaptureFixture.CreateAsync(mailFolders: true).ConfigureAwait(false);
        await using var lifetime = fixture.ConfigureAwait(false);
        var source = await SeedItemAsync(fixture, GatewayEwsFixtureDomain.ChildId).ConfigureAwait(false);
        var database = Context(fixture);
        await using var databaseLifetime = database.ConfigureAwait(false);
        var email = await database.Emails.SingleAsync(email => email.Id == source).ConfigureAwait(false);
        email.IsRead = read;
        await database.SaveChangesAsync().ConfigureAwait(false);
        var modseq = email.ModSeq;
        Authenticate(fixture);
        var before = await ReadItemAsync(fixture, source).ConfigureAwait(false);
        var key = (string)before.Element(Types + "ItemId")!.Attribute("ChangeKey")!;
        var xml = await UpdateResponseAsync(fixture, UpdateChange(ItemId(source), read, key)).ConfigureAwait(false);
        AssertCode(XDocument.Parse(xml), "NoError");
        Assert.AreEqual(key, (string?)XDocument.Parse(xml).Descendants(Types + "ItemId").Single().Attribute("ChangeKey"), StringComparer.Ordinal);
        var after = Context(fixture);
        await using var afterLifetime = after.ConfigureAwait(false);
        Assert.AreEqual(modseq, (await after.Emails.SingleAsync(item => item.Id == source).ConfigureAwait(false)).ModSeq);
        Assert.AreEqual(0, await after.ExpungedUids.CountAsync().ConfigureAwait(false));
    }

    [TestMethod]
    public async Task UpdateItemMaintainsOrderedForeignMissingMalformedAndLaterDuplicateRefusals()
    {
        var fixture = await CaptureFixture.CreateAsync(mailFolders: true).ConfigureAwait(false);
        await using var lifetime = fixture.ConfigureAwait(false);
        var source = await SeedItemAsync(fixture, GatewayEwsFixtureDomain.ChildId).ConfigureAwait(false);
        var foreign = await SeedItemAsync(fixture, GatewayEwsFixtureDomain.ForeignFolderId).ConfigureAwait(false);
        Authenticate(fixture);
        var refs = UpdateChange(GatewayEwsItemIdCodec.Encode(GatewayEwsFixtureDomain.ForeignAccountId, foreign), true)
            + UpdateChange(ItemId(foreign), true) + UpdateChange(ItemId(Guid.CreateVersion7()), true)
            + UpdateChange(FolderId(GatewayEwsFixtureDomain.ChildId), true) + UpdateChange(ItemId(source), true, "bad")
            + UpdateChange(ItemId(source), true) + UpdateChange(ItemId(source), false);
        var document = XDocument.Parse(await UpdateResponseAsync(fixture, refs).ConfigureAwait(false));
        CollectionAssert.AreEqual(UpdateCodes, document.Descendants(Messages + "ResponseCode").Select(item => item.Value).ToArray());
        Assert.HasCount(1, document.Descendants(Types + "ItemId").ToArray());
        var database = Context(fixture);
        await using var databaseLifetime = database.ConfigureAwait(false);
        Assert.IsTrue((await database.Emails.SingleAsync(email => email.Id == source).ConfigureAwait(false)).IsRead);
        Assert.IsFalse((await database.Emails.SingleAsync(email => email.Id == foreign).ConfigureAwait(false)).IsRead);
        Assert.AreEqual(0, await database.ExpungedUids.CountAsync().ConfigureAwait(false));
    }

    [TestMethod]
    [DataRow("bad", "ErrorInvalidChangeKey")]
    [DataRow("c3RhbGU=", "ErrorIrresolvableConflict")]
    public async Task UpdateItemBadKeysNeverApplyFlags(string key, string code)
    {
        var fixture = await CaptureFixture.CreateAsync(mailFolders: true).ConfigureAwait(false);
        await using var lifetime = fixture.ConfigureAwait(false);
        var source = await SeedItemAsync(fixture, GatewayEwsFixtureDomain.ChildId).ConfigureAwait(false);
        Authenticate(fixture);
        AssertCode(XDocument.Parse(await UpdateResponseAsync(fixture, UpdateChange(ItemId(source), true, key)).ConfigureAwait(false)), code);
        var database = Context(fixture);
        await using var databaseLifetime = database.ConfigureAwait(false);
        Assert.IsFalse((await database.Emails.SingleAsync(email => email.Id == source).ConfigureAwait(false)).IsRead);
    }

    [TestMethod]
    [DataRow("ConflictResolution='AutoResolve' MessageDisposition='SaveOnly' SuppressReadReceipts='true'", "message:IsRead", "ErrorInvalidRequest")]
    [DataRow("ConflictResolution='NeverOverwrite' MessageDisposition='SendOnly' SuppressReadReceipts='true'", "message:IsRead", "ErrorInvalidRequest")]
    [DataRow("ConflictResolution='NeverOverwrite' MessageDisposition='SaveOnly'", "message:IsRead", "ErrorInvalidRequest")]
    [DataRow(UpdateAttributes, "item:Subject", "ErrorInvalidPropertySet")]
    public async Task UpdateItemUnsupportedModesAndFieldsRefuseBeforeWorker(string attributes, string uri, string code)
    {
        var fixture = await CaptureFixture.CreateAsync(mailFolders: true).ConfigureAwait(false);
        await using var lifetime = fixture.ConfigureAwait(false);
        Authenticate(fixture);
        using var content = XmlContent(UpdateRequest(UpdateChange("opaque", true).Replace("message:IsRead", uri, StringComparison.Ordinal), attributes));
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
    public async Task UpdateItemCommittedChangesRejectTheAdmittedStateEvenWithoutClientKey(string change)
    {
        CaptureFixture? fixture = null;
        var source = Guid.Empty;
        fixture = await CaptureFixture.CreateAsync(mailFolders: true, decorateDispatcher: inner => new FindRaceDispatcher(inner,
            MailOperationKind.ReadMessages, async token =>
            {
                var database = Context(fixture!);
                await using var databaseLifetime = database.ConfigureAwait(false);
                var email = await database.Emails.SingleAsync(email => email.Id == source, token).ConfigureAwait(false);
                if (change is "scope") email.FolderId = GatewayEwsFixtureDomain.ForeignFolderId;
                else if (change is "deleted") email.IsDeleted = true;
                else email.IsFlagged = true;
                await database.SaveChangesAsync(token).ConfigureAwait(false);
            })).ConfigureAwait(false);
        await using var lifetime = fixture.ConfigureAwait(false);
        source = await SeedItemAsync(fixture, GatewayEwsFixtureDomain.ChildId).ConfigureAwait(false);
        Authenticate(fixture);
        AssertCode(XDocument.Parse(await UpdateResponseAsync(fixture, UpdateChange(ItemId(source), true)).ConfigureAwait(false)), "ErrorIrresolvableConflict");
        var after = Context(fixture);
        await using var afterLifetime = after.ConfigureAwait(false);
        Assert.IsFalse((await after.Emails.SingleAsync(email => email.Id == source).ConfigureAwait(false)).IsRead);
    }

    [TestMethod]
    [DataRow(0L)]
    [DataRow(1L)]
    public async Task UpdateItemJournalFailureWithholdsSuccessEvenAfterFlagCommit(long sequence)
    {
        var fixture = await CaptureFixture.CreateAsync(failSequence: sequence, mailFolders: true).ConfigureAwait(false);
        await using var lifetime = fixture.ConfigureAwait(false);
        var source = await SeedItemAsync(fixture, GatewayEwsFixtureDomain.ChildId).ConfigureAwait(false);
        Authenticate(fixture);
        using var content = XmlContent(UpdateRequest(UpdateChange(ItemId(source), true)));
        using var response = await fixture.Client.PostAsync(new Uri(CanonicalPath, UriKind.Relative), content).ConfigureAwait(false);
        var xml = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        AssertFault(xml, "ErrorServerBusy");
        Assert.IsFalse(xml.Contains("NoError", StringComparison.Ordinal));
        await fixture.AssertPresentationRecordCountAsync(sequence).ConfigureAwait(false);
        var after = Context(fixture);
        await using var afterLifetime = after.ConfigureAwait(false);
        Assert.AreEqual(sequence != 0, (await after.Emails.SingleAsync(email => email.Id == source).ConfigureAwait(false)).IsRead);
        Assert.AreEqual(0, await after.ExpungedUids.CountAsync().ConfigureAwait(false));
    }

    [TestMethod]
    public async Task UpdateItemDurableReplayDoesNotIncrementModseqAgain()
    {
        var fixture = await CaptureFixture.CreateAsync(mailFolders: true, decorateDispatcher: inner => new UpdateReplayDispatcher(inner)).ConfigureAwait(false);
        await using var lifetime = fixture.ConfigureAwait(false);
        var source = await SeedItemAsync(fixture, GatewayEwsFixtureDomain.ChildId).ConfigureAwait(false);
        Authenticate(fixture);
        var before = Context(fixture);
        await using var beforeLifetime = before.ConfigureAwait(false);
        var modseq = (await before.Emails.AsNoTracking().SingleAsync(email => email.Id == source).ConfigureAwait(false)).ModSeq;
        AssertCode(XDocument.Parse(await UpdateResponseAsync(fixture, UpdateChange(ItemId(source), true)).ConfigureAwait(false)), "NoError");
        var after = Context(fixture);
        await using var afterLifetime = after.ConfigureAwait(false);
        Assert.AreEqual(modseq + 1, (await after.Emails.SingleAsync(email => email.Id == source).ConfigureAwait(false)).ModSeq);
    }

    [TestMethod]
    public async Task UpdateItemSeenAtKeywordLimitRefusesWithoutReplacingCustomKeywords()
    {
        var fixture = await CaptureFixture.CreateAsync(mailFolders: true).ConfigureAwait(false);
        await using var lifetime = fixture.ConfigureAwait(false);
        var source = await SeedItemAsync(fixture, GatewayEwsFixtureDomain.ChildId).ConfigureAwait(false);
        var database = Context(fixture);
        await using var databaseLifetime = database.ConfigureAwait(false);
        var email = await database.Emails.SingleAsync(email => email.Id == source).ConfigureAwait(false);
        email.Keywords = Enumerable.Range(0, 128).Select(index => "custom" + index.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        await database.SaveChangesAsync().ConfigureAwait(false);
        Authenticate(fixture);
        AssertCode(XDocument.Parse(await UpdateResponseAsync(fixture, UpdateChange(ItemId(source), true)).ConfigureAwait(false)), "ErrorInvalidPropertySet");
        var after = Context(fixture);
        await using var afterLifetime = after.ConfigureAwait(false);
        var actual = await after.Emails.SingleAsync(item => item.Id == source).ConfigureAwait(false);
        Assert.IsFalse(actual.IsRead);
        CollectionAssert.AreEqual(email.Keywords, actual.Keywords!);
    }

    private static async Task AssertUpdatedValuesAsync(CaptureFixture fixture, Guid source, bool read, int uid, DateTime received, long modseq)
    {
        using var scope = fixture.DomainScopes.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
        var email = await database.Emails.SingleAsync(item => item.Id == source).ConfigureAwait(false);
        Assert.AreEqual(read, email.IsRead);
        Assert.IsTrue(email.IsDraft && email.IsFlagged && email.IsAnswered);
        CollectionAssert.AreEqual(MoveKeywords, email.Keywords!);
        Assert.AreEqual(uid, email.Uid);
        Assert.AreEqual(received, email.ReceivedAt);
        Assert.AreEqual(GatewayEwsFixtureDomain.ChildId, email.FolderId);
        Assert.IsTrue(email.ModSeq > modseq);
        Assert.AreEqual(0, await database.ExpungedUids.CountAsync().ConfigureAwait(false));
        var content = scope.ServiceProvider.GetRequiredService<MailboxMessageContentService>();
        CollectionAssert.AreEqual(Encoding.ASCII.GetBytes(NativeMime), await content.ReadAsync(email, CancellationToken.None).ConfigureAwait(false));
    }

    private static async Task AssertJmapReadStateAsync(CaptureFixture fixture, Guid source, bool read)
    {
        using var content = new StringContent($"{{\"using\":[\"urn:ietf:params:jmap:core\",\"urn:ietf:params:jmap:mail\"],\"methodCalls\":[[\"Email/get\",{{\"accountId\":\"A{GatewayEwsFixtureDomain.AccountId:N}\",\"ids\":[\"E{source:N}\"],\"properties\":[\"id\",\"keywords\"]}},\"read\"]]}}", Encoding.UTF8, "application/json");
        using var response = await fixture.Client.PostAsync(new Uri("/jmap/api", UriKind.Relative), content).ConfigureAwait(false);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var keywords = json.RootElement.GetProperty("methodResponses")[0][1].GetProperty("list")[0].GetProperty("keywords");
        Assert.AreEqual(read, keywords.TryGetProperty("$seen", out _));
        Assert.IsTrue(keywords.GetProperty("custom-keyword").GetBoolean());
    }

    private static async Task<string> UpdateResponseAsync(CaptureFixture fixture, string changes, string path = CanonicalPath)
    {
        using var content = XmlContent(UpdateRequest(changes));
        using var response = await fixture.Client.PostAsync(new Uri(path, UriKind.Relative), content).ConfigureAwait(false);
        var xml = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, xml);
        Assert.IsTrue(response.Headers.CacheControl?.NoStore);
        await fixture.AssertRecordedResponseAsync("ews", path, 200, xml, rejection: false).ConfigureAwait(false);
        return xml;
    }

    private static string UpdateRequest(string changes, string attributes = UpdateAttributes) =>
        $"<s:Envelope xmlns:s='{GatewayEwsSoap.Soap}' xmlns:m='{Messages}' xmlns:t='{Types}'><s:Body><m:UpdateItem {attributes}><m:ItemChanges>{changes}</m:ItemChanges></m:UpdateItem></s:Body></s:Envelope>";
    private static string UpdateChange(string id, bool read, string? key = null) =>
        $"<t:ItemChange><t:ItemId Id='{id}'{(key is null ? "" : $" ChangeKey='{key}'")}/><t:Updates><t:SetItemField><t:FieldURI FieldURI='message:IsRead'/><t:Message><t:IsRead>{(read ? "true" : "false")}</t:IsRead></t:Message></t:SetItemField></t:Updates></t:ItemChange>";

    private sealed class UpdateReplayDispatcher(mk8.email.Application.Interfaces.IApplicationRequestDispatcher inner) : mk8.email.Application.Interfaces.IApplicationRequestDispatcher
    {
        public async Task<ApplicationResponse> DispatchAsync(ApplicationRequest request, CancellationToken cancellationToken = default)
        {
            var response = await inner.DispatchAsync(request, cancellationToken).ConfigureAwait(false);
            if (string.Equals(request.Operation, ApplicationOperations.MailOperationExecute, StringComparison.Ordinal)
                && JsonSerializer.Deserialize<MailOperationApplicationRequest>(request.Payload, JsonSerializerOptions.Web)!.Command.Operation == MailOperationKind.MutateMessages)
            {
                var again = await inner.DispatchAsync(request, cancellationToken).ConfigureAwait(false);
                CollectionAssert.AreEqual(response.Payload.ToArray(), again.Payload.ToArray());
            }
            return response;
        }
    }
}
