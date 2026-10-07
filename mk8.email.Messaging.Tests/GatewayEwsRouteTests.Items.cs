using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using mk8.email.Application.Services;
using mk8.email.Contracts.Messaging;
using mk8.email.Infrastructure.Data;
using mk8.email.Infrastructure.Models;
using mk8.email.Gateway.Protocols.Ews;
using CaptureFixture = mk8.email.Messaging.Tests.GatewayHttpCaptureBoundaryTests.CaptureFixture;

namespace mk8.email.Messaging.Tests;

internal sealed partial class GatewayEwsRouteTests
{
    private const string ItemProperties = "<t:AdditionalProperties><t:FieldURI FieldURI='item:ParentFolderId'/><t:FieldURI FieldURI='item:ItemClass'/><t:FieldURI FieldURI='item:Subject'/><t:FieldURI FieldURI='item:DateTimeReceived'/><t:FieldURI FieldURI='item:Size'/><t:FieldURI FieldURI='item:IsDraft'/><t:FieldURI FieldURI='item:DateTimeSent'/><t:FieldURI FieldURI='item:HasAttachments'/><t:FieldURI FieldURI='message:Sender'/><t:FieldURI FieldURI='message:ToRecipients'/><t:FieldURI FieldURI='message:From'/><t:FieldURI FieldURI='message:InternetMessageId'/><t:FieldURI FieldURI='message:IsRead'/></t:AdditionalProperties>";
    private const string ItemMime = "From: Sender <sender@example.test>\r\nTo: Owner <owner@example.test>\r\nSubject: Mail <&> metadata\r\nMessage-ID: <item-fixture@example.test>\r\nDate: Wed, 7 Oct 2026 01:00:00 +0000\r\nMIME-Version: 1.0\r\nContent-Type: text/plain; charset=utf-8\r\n\r\nPRIVATE BODY NOT REQUESTED\r\n";
    private static readonly string[] ItemAuthorityOutcomes = ["ErrorAccessDenied", "ErrorItemNotFound", "ErrorItemNotFound", "ErrorInvalidIdMalformed", "ErrorInvalidChangeKey", "NoError", "NoError"];

    [TestMethod]
    [DataRow(CanonicalPath, false)]
    [DataRow(CanonicalPath, true)]
    [DataRow("/eWs/EXCHANGE.ASMX/", false)]
    [DataRow("/eWs/EXCHANGE.ASMX/", true)]
    public async Task GetItemReadsAzureBackedOwnedMetadataInOriginalDurableSession(string path, bool ordinaryClient)
    {
        var fixture = await CaptureFixture.CreateAsync(ordinaryClient, mailFolders: true).ConfigureAwait(false);
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        var item = await SeedItemAsync(fixture, GatewayEwsFixtureDomain.InboxId).ConfigureAwait(false);
        Authenticate(fixture);
        using var content = XmlContent(ItemRequest($"<t:ItemId Id='{ItemId(item)}'/>", ItemProperties));
        using var response = await fixture.Client.PostAsync(new Uri(path, UriKind.Relative), content).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.IsTrue(response.Headers.CacheControl?.NoStore);
        Assert.AreEqual("text/xml", response.Content.Headers.ContentType?.MediaType, StringComparer.Ordinal);
        var message = XDocument.Parse(body).Descendants(Types + "Message").Single();
        Assert.AreEqual("Mail <&> metadata", message.Element(Types + "Subject")!.Value, StringComparer.Ordinal);
        Assert.AreEqual("IPM.Note", message.Element(Types + "ItemClass")!.Value, StringComparer.Ordinal);
        Assert.AreEqual("false", message.Element(Types + "IsRead")!.Value, StringComparer.Ordinal);
        Assert.AreEqual("<item-fixture@example.test>", message.Element(Types + "InternetMessageId")!.Value, StringComparer.Ordinal);
        Assert.AreEqual("sender@example.test", message.Element(Types + "From")!.Descendants(Types + "EmailAddress").Single().Value, StringComparer.Ordinal);
        Assert.AreEqual(FolderId(GatewayEwsFixtureDomain.InboxId), (string?)message.Element(Types + "ParentFolderId")!.Attribute("Id"), StringComparer.Ordinal);
        Assert.AreEqual(ItemId(item), (string?)message.Element(Types + "ItemId")!.Attribute("Id"), StringComparer.Ordinal);
        Assert.IsFalse(body.Contains("PRIVATE BODY", StringComparison.Ordinal));
        Assert.IsFalse(body.Contains("test-protocol-secret", StringComparison.Ordinal));
        await fixture.AssertRecordedResponseAsync("ews", path, 200, body, rejection: false).ConfigureAwait(false);
        await fixture.AssertWorkerOperationsAsync(ApplicationOperations.JmapProfileGet, ApplicationOperations.MailOperationExecute).ConfigureAwait(false);
        var database = Context(fixture);
        await using var databaseLifetime = database.ConfigureAwait(false);
        var stored = await database.Emails.SingleAsync(email => email.Id == item).ConfigureAwait(false);
        Assert.IsNull(stored.RawMessage);
        Assert.AreEqual("azure-blob", stored.RawMessageObjectProvider, StringComparer.Ordinal);
    }

    [TestMethod]
    public async Task GetItemKeepsOrderedOutcomesWithoutForeignAccountOrFolderAuthority()
    {
        var fixture = await CaptureFixture.CreateAsync(mailFolders: true).ConfigureAwait(false);
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        var owned = await SeedItemAsync(fixture, GatewayEwsFixtureDomain.InboxId).ConfigureAwait(false);
        var foreign = await SeedItemAsync(fixture, GatewayEwsFixtureDomain.ForeignFolderId).ConfigureAwait(false);
        Authenticate(fixture);
        var refs = $"<t:ItemId Id='{GatewayEwsItemIdCodec.Encode(GatewayEwsFixtureDomain.ForeignAccountId, foreign)}'/>"
            + $"<t:ItemId Id='{ItemId(foreign)}'/><t:ItemId Id='{ItemId(Guid.CreateVersion7())}'/>"
            + $"<t:ItemId Id='{FolderId(GatewayEwsFixtureDomain.InboxId)}'/><t:ItemId Id='{ItemId(owned)}' ChangeKey='bad'/>"
            + $"<t:ItemId Id='{ItemId(owned)}'/><t:ItemId Id='{ItemId(owned)}' ChangeKey='b2xk'/>";
        using var content = XmlContent(ItemRequest(refs));
        using var response = await fixture.Client.PostAsync(new Uri(CanonicalPath, UriKind.Relative), content).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        var document = XDocument.Parse(body);
        CollectionAssert.AreEqual(ItemAuthorityOutcomes, document.Descendants(Messages + "ResponseCode").Select(code => code.Value).ToArray());
        Assert.AreEqual(2, document.Descendants(Types + "Message").Count());
        Assert.IsTrue(document.Descendants(Types + "Message").All(message => message.Elements().Count() == 1));
        await fixture.AssertRecordedResponseAsync("ews", CanonicalPath, 200, body, rejection: false).ConfigureAwait(false);
        await fixture.AssertWorkerOperationsAsync(ApplicationOperations.JmapProfileGet, ApplicationOperations.MailOperationExecute).ConfigureAwait(false);
    }

    [TestMethod]
    [DataRow("<t:IncludeMimeContent>true</t:IncludeMimeContent>")]
    [DataRow("<t:AdditionalProperties><t:FieldURI FieldURI='item:Body'/></t:AdditionalProperties>")]
    [DataRow("<t:AdditionalProperties><t:FieldURI FieldURI='item:Attachments'/></t:AdditionalProperties>")]
    [DataRow("<t:AdditionalProperties><t:IndexedFieldURI FieldURI='contacts:EmailAddress' FieldIndex='EmailAddress1'/></t:AdditionalProperties>")]
    public async Task UnsupportedItemPropertiesHaveJournaledSoapRefusalWithoutAnyDispatch(string fields)
    {
        var fixture = await CaptureFixture.CreateAsync(mailFolders: true).ConfigureAwait(false);
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        Authenticate(fixture);
        using var content = XmlContent(ItemRequest("<t:ItemId Id='opaque'/>", fields));
        using var response = await fixture.Client.PostAsync(new Uri(CanonicalPath, UriKind.Relative), content).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.InternalServerError, response.StatusCode);
        AssertFault(body, "ErrorInvalidPropertyRequest");
        await fixture.AssertNoWorkerRequestsAsync().ConfigureAwait(false);
        await fixture.AssertRecordedResponseAsync("ews", CanonicalPath, 500, body, rejection: false).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task JmapFlagUpdateChangesConservativeItemKeyWithoutChangingEwsIdentity()
    {
        var fixture = await CaptureFixture.CreateAsync(mailFolders: true).ConfigureAwait(false);
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        var item = await SeedItemAsync(fixture, GatewayEwsFixtureDomain.InboxId).ConfigureAwait(false);
        Authenticate(fixture);
        var before = await ReadItemAsync(fixture, item).ConfigureAwait(false);
        var request = new JsonObject
        {
            ["using"] = new JsonArray("urn:ietf:params:jmap:core", "urn:ietf:params:jmap:mail"),
            ["methodCalls"] = new JsonArray(new JsonArray("Email/set", new JsonObject
            {
                ["accountId"] = $"A{GatewayEwsFixtureDomain.AccountId:N}",
                ["update"] = new JsonObject { [$"E{item:N}"] = new JsonObject { ["keywords/$seen"] = true } },
            }, "read")),
        };
        using var mutationContent = new StringContent(request.ToJsonString(), Encoding.UTF8, "application/json");
        using var mutation = await fixture.Client.PostAsync(new Uri("/jmap/api", UriKind.Relative), mutationContent).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.OK, mutation.StatusCode);
        var after = await ReadItemAsync(fixture, item).ConfigureAwait(false);
        Assert.AreEqual(ItemId(item), (string?)after.Element(Types + "ItemId")!.Attribute("Id"), StringComparer.Ordinal);
        Assert.AreEqual("true", after.Element(Types + "IsRead")!.Value, StringComparer.Ordinal);
        Assert.AreNotEqual((string?)before.Element(Types + "ItemId")!.Attribute("ChangeKey"), (string?)after.Element(Types + "ItemId")!.Attribute("ChangeKey"), StringComparer.Ordinal);
    }

    [TestMethod]
    [DataRow(0L)]
    [DataRow(1L)]
    public async Task GetItemJournalFailureWithholdsEveryItemProperty(long sequence)
    {
        var fixture = await CaptureFixture.CreateAsync(failSequence: sequence, mailFolders: true).ConfigureAwait(false);
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        var item = await SeedItemAsync(fixture, GatewayEwsFixtureDomain.InboxId).ConfigureAwait(false);
        Authenticate(fixture);
        using var content = XmlContent(ItemRequest($"<t:ItemId Id='{ItemId(item)}'/>", ItemProperties));
        using var response = await fixture.Client.PostAsync(new Uri(CanonicalPath, UriKind.Relative), content).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        AssertFault(body, "ErrorServerBusy");
        Assert.IsFalse(body.Contains("Mail &lt;", StringComparison.Ordinal));
        Assert.IsFalse(body.Contains("NoError", StringComparison.Ordinal));
        await fixture.AssertPresentationRecordCountAsync(sequence).ConfigureAwait(false);
    }

    private static async Task<Guid> SeedItemAsync(CaptureFixture fixture, Guid folder)
    {
        using var scope = fixture.DomainScopes.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
        var transaction = await database.Database.BeginTransactionAsync().ConfigureAwait(false);
        await using var transactionLifetime = transaction.ConfigureAwait(false);
        var email = new EmailDB
        {
            Id = Guid.CreateVersion7(),
            FolderId = folder,
            Uid = 100,
            ReceivedAt = new DateTime(2026, 10, 7, 1, 0, 0, DateTimeKind.Utc)
        };
        await database.Emails.AddAsync(email).ConfigureAwait(false);
        await scope.ServiceProvider.GetRequiredService<MailboxMessageContentService>().SetAsync(email, Encoding.UTF8.GetBytes(ItemMime), CancellationToken.None).ConfigureAwait(false);
        await database.SaveChangesAsync().ConfigureAwait(false);
        await transaction.CommitAsync().ConfigureAwait(false);
        return email.Id;
    }

    private static async Task<XElement> ReadItemAsync(CaptureFixture fixture, Guid item)
    {
        using var content = XmlContent(ItemRequest($"<t:ItemId Id='{ItemId(item)}'/>", ItemProperties));
        using var response = await fixture.Client.PostAsync(new Uri(CanonicalPath, UriKind.Relative), content).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        await fixture.AssertRecordedResponseAsync("ews", CanonicalPath, 200, body, rejection: false).ConfigureAwait(false);
        return XDocument.Parse(body).Descendants(Types + "Message").Single();
    }

    private static string ItemId(Guid item) => GatewayEwsItemIdCodec.Encode(GatewayEwsFixtureDomain.AccountId, item);
    private static string ItemRequest(string references, string fields = "") =>
        $"<s:Envelope xmlns:s='{GatewayEwsSoap.Soap}' xmlns:m='{Messages}' xmlns:t='{Types}'><s:Body><m:GetItem><m:ItemShape><t:BaseShape>IdOnly</t:BaseShape>{fields}</m:ItemShape><m:ItemIds>{references}</m:ItemIds></m:GetItem></s:Body></s:Envelope>";
}
