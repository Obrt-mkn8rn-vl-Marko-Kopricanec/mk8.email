using System.Net;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using mk8.email.Application.Services;
using mk8.email.Contracts.Messaging;
using mk8.email.Contracts.Storage;
using mk8.email.Gateway.Protocols.Ews;
using CaptureFixture = mk8.email.Messaging.Tests.GatewayHttpCaptureBoundaryTests.CaptureFixture;

namespace mk8.email.Messaging.Tests;

internal sealed partial class GatewayEwsRouteTests
{
    private const string MimeOption = "<t:IncludeMimeContent>true</t:IncludeMimeContent>";
    private static readonly string[] MimeFields = ["MimeContent", "ItemId"];
    private static readonly string[] MimeReferenceCodes = ["ErrorAccessDenied", "ErrorItemNotFound", "ErrorItemNotFound", "ErrorItemNotFound", "NoError", "NoError"];
    private const string NativeMime = "From: Sender <sender@example.test>\r\nSubject: =?UTF-8?B?xI0=?=\r\nMIME-Version: 1.0\r\n"
        + "Content-Type: multipart/mixed; boundary=native\r\n\r\n--native\r\nContent-Type: text/plain; charset=utf-8\r\n"
        + "Content-Transfer-Encoding: quoted-printable\r\n\r\nVisible =C4=8D text\r\n"
        + "--native\r\nContent-Type: application/octet-stream; name=private.bin\r\nContent-Disposition: attachment; filename=private.bin\r\n"
        + "Content-Transfer-Encoding: base64\r\n\r\nAAECA//+\r\n--native--\r\n";

    [TestMethod]
    [DataRow(CanonicalPath, false)]
    [DataRow(CanonicalPath, true)]
    [DataRow("/EwS/EXCHANGE.asmx/", false)]
    [DataRow("/EwS/EXCHANGE.asmx/", true)]
    public async Task NativeMimeIsExactAzureContentWithOriginalDurableSession(string path, bool ordinaryClient)
    {
        var fixture = await CaptureFixture.CreateAsync(ordinaryClient, mailFolders: true).ConfigureAwait(false);
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        await fixture.AssertAdminBoundaryAsync(ordinaryClient).ConfigureAwait(false);
        var item = await SeedItemAsync(fixture, GatewayEwsFixtureDomain.InboxId, NativeMime).ConfigureAwait(false);
        Authenticate(fixture);
        var xml = await MimeResponseAsync(fixture, $"<t:ItemId Id='{ItemId(item)}'/>", path: path).ConfigureAwait(false);
        var message = XDocument.Parse(xml).Descendants(Types + "Message").Single();
        CollectionAssert.AreEqual(MimeFields, message.Elements().Select(element => element.Name.LocalName).ToArray());
        CollectionAssert.AreEqual(System.Text.Encoding.ASCII.GetBytes(NativeMime), Convert.FromBase64String(message.Element(Types + "MimeContent")!.Value));
        await fixture.AssertWorkerOperationsAsync(ApplicationOperations.JmapProfileGet, ApplicationOperations.MailOperationExecute).ConfigureAwait(false);
        using var scope = fixture.DomainScopes.CreateScope();
        var stored = await scope.ServiceProvider.GetRequiredService<mk8.email.Infrastructure.Data.EmailDbContext>().Emails.SingleAsync(email => email.Id == item).ConfigureAwait(false);
        Assert.IsNull(stored.RawMessage);
        Assert.AreEqual("azure-blob", stored.RawMessageObjectProvider, StringComparer.Ordinal);
    }

    [TestMethod]
    public async Task NativeMimeAndBodyHaveDistinctAttachmentCapabilities()
    {
        var fixture = await CaptureFixture.CreateAsync(mailFolders: true).ConfigureAwait(false);
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        var item = await SeedItemAsync(fixture, GatewayEwsFixtureDomain.InboxId, MixedBodyMime).ConfigureAwait(false);
        Authenticate(fixture);
        var xml = await MimeResponseAsync(fixture, $"<t:ItemId Id='{ItemId(item)}'/>", BodyProperty).ConfigureAwait(false);
        var message = XDocument.Parse(xml).Descendants(Types + "Message").Single();
        Assert.IsFalse(message.Element(Types + "Body")!.Value.Contains("PRIVATE ATTACHMENT", StringComparison.Ordinal));
        CollectionAssert.AreEqual(System.Text.Encoding.ASCII.GetBytes(MixedBodyMime), Convert.FromBase64String(message.Element(Types + "MimeContent")!.Value));
    }

    [TestMethod]
    public async Task NativeMimeDoesNotRequireThePublicJmapListener()
    {
        var fixture = await CaptureFixture.CreateAsync(mailFolders: true, disableJmap: true).ConfigureAwait(false);
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        var item = await SeedItemAsync(fixture, GatewayEwsFixtureDomain.InboxId, NativeMime).ConfigureAwait(false);
        Authenticate(fixture);
        var xml = await MimeResponseAsync(fixture, $"<t:ItemId Id='{ItemId(item)}'/>").ConfigureAwait(false);
        CollectionAssert.AreEqual(System.Text.Encoding.ASCII.GetBytes(NativeMime),
            Convert.FromBase64String(XDocument.Parse(xml).Descendants(Types + "MimeContent").Single().Value));
    }

    [TestMethod]
    public async Task MimeForgeryMissingDeletedAndRepeatedReferencesKeepOrderedOutcomes()
    {
        var fixture = await CaptureFixture.CreateAsync(mailFolders: true).ConfigureAwait(false);
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        var item = await SeedItemAsync(fixture, GatewayEwsFixtureDomain.InboxId, NativeMime).ConfigureAwait(false);
        var foreign = await SeedItemAsync(fixture, GatewayEwsFixtureDomain.ForeignFolderId, "Subject: PRIVATE FOREIGN MIME\r\n\r\nPRIVATE FOREIGN MIME").ConfigureAwait(false);
        var deleted = await SeedItemAsync(fixture, GatewayEwsFixtureDomain.ChildId, NativeMime).ConfigureAwait(false);
        var database = Context(fixture);
        await using var databaseLifetime = database.ConfigureAwait(false);
        (await database.Emails.SingleAsync(email => email.Id == deleted).ConfigureAwait(false)).IsDeleted = true;
        await database.SaveChangesAsync().ConfigureAwait(false);
        Authenticate(fixture);
        var xml = await MimeResponseAsync(fixture, $"<t:ItemId Id='{GatewayEwsItemIdCodec.Encode(GatewayEwsFixtureDomain.ForeignAccountId, foreign)}'/>"
            + $"<t:ItemId Id='{ItemId(foreign)}'/><t:ItemId Id='{ItemId(deleted)}'/><t:ItemId Id='{ItemId(Guid.CreateVersion7())}'/>"
            + $"<t:ItemId Id='{ItemId(item)}'/><t:ItemId Id='{ItemId(item)}'/>").ConfigureAwait(false);
        var document = XDocument.Parse(xml);
        CollectionAssert.AreEqual(MimeReferenceCodes,
            document.Descendants(Messages + "ResponseCode").Select(element => element.Value).ToArray());
        Assert.HasCount(2, document.Descendants(Types + "MimeContent").ToArray());
        foreach (var native in document.Descendants(Types + "MimeContent"))
            CollectionAssert.AreEqual(System.Text.Encoding.ASCII.GetBytes(NativeMime), Convert.FromBase64String(native.Value));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task MimeLimitIsInclusiveAndOversizedContentIsNotFetched(bool above)
    {
        var fixture = await CaptureFixture.CreateAsync(mailFolders: true).ConfigureAwait(false);
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        const string headers = "Subject: Native boundary\r\nContent-Type: text/plain\r\n\r\n";
        var mime = headers + new string('x', GatewayEwsClient.MaximumMimeBytes + (above ? 1 : 0) - headers.Length);
        var item = await SeedItemAsync(fixture, GatewayEwsFixtureDomain.InboxId, mime).ConfigureAwait(false);
        if (above)
        {
            // Remove only this test's object. An attempted fetch would now fail,
            // so the stable per-item size refusal proves admission precedes I/O.
            using var scope = fixture.DomainScopes.CreateScope();
            var email = await scope.ServiceProvider.GetRequiredService<mk8.email.Infrastructure.Data.EmailDbContext>().Emails.SingleAsync(value => value.Id == item).ConfigureAwait(false);
            var reference = scope.ServiceProvider.GetRequiredService<MailboxMessageContentService>().TryGetReference(email)!;
            Assert.IsTrue(await scope.ServiceProvider.GetRequiredService<ILargeObjectStore>().DeleteIfMatchAsync(reference).ConfigureAwait(false));
        }
        Authenticate(fixture);
        var xml = await MimeResponseAsync(fixture, $"<t:ItemId Id='{ItemId(item)}'/>").ConfigureAwait(false);
        var document = XDocument.Parse(xml);
        Assert.AreEqual(above ? "ErrorDataSizeLimitExceeded" : "NoError", document.Descendants(Messages + "ResponseCode").Single().Value, StringComparer.Ordinal);
        if (!above) CollectionAssert.AreEqual(System.Text.Encoding.ASCII.GetBytes(mime), Convert.FromBase64String(document.Descendants(Types + "MimeContent").Single().Value));
        else Assert.IsEmpty(document.Descendants(Types + "MimeContent"));
    }

    [TestMethod]
    public async Task DuplicateMimeCannotOverflowTheEncodedDurableResponseBudget()
    {
        var fixture = await CaptureFixture.CreateAsync(mailFolders: true).ConfigureAwait(false);
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        var item = await SeedItemAsync(fixture, GatewayEwsFixtureDomain.InboxId, "Subject: Aggregate\r\n\r\n" + new string('x', 900_000)).ConfigureAwait(false);
        Authenticate(fixture);
        using var content = XmlContent(ItemRequest($"<t:ItemId Id='{ItemId(item)}'/><t:ItemId Id='{ItemId(item)}'/>", MimeOption));
        using var response = await fixture.Client.PostAsync(new Uri(CanonicalPath, UriKind.Relative), content).ConfigureAwait(false);
        var xml = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.InternalServerError, response.StatusCode);
        AssertFault(xml, "ErrorDataSizeLimitExceeded");
        Assert.IsEmpty(XDocument.Parse(xml).Descendants(Types + "MimeContent"));
        await fixture.AssertRecordedResponseAsync("ews", CanonicalPath, 500, xml, rejection: false).ConfigureAwait(false);
    }

    [TestMethod]
    [DataRow(0L)]
    [DataRow(1L)]
    public async Task NativeMimeJournalFailureWithholdsContent(long sequence)
    {
        var fixture = await CaptureFixture.CreateAsync(failSequence: sequence, mailFolders: true).ConfigureAwait(false);
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        var item = await SeedItemAsync(fixture, GatewayEwsFixtureDomain.InboxId, NativeMime).ConfigureAwait(false);
        Authenticate(fixture);
        using var content = XmlContent(ItemRequest($"<t:ItemId Id='{ItemId(item)}'/>", MimeOption));
        using var response = await fixture.Client.PostAsync(new Uri(CanonicalPath, UriKind.Relative), content).ConfigureAwait(false);
        var xml = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        AssertFault(xml, "ErrorServerBusy");
        Assert.IsFalse(xml.Contains("NoError", StringComparison.Ordinal));
        Assert.IsEmpty(XDocument.Parse(xml).Descendants(Types + "MimeContent"));
        await fixture.AssertPresentationRecordCountAsync(sequence).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task FindItemMimeIsJournaledAndRefusedBeforeDispatch()
    {
        var fixture = await CaptureFixture.CreateAsync(mailFolders: true).ConfigureAwait(false);
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        Authenticate(fixture);
        using var content = XmlContent(FindRequest("<t:DistinguishedFolderId Id='inbox'/>", fields: MimeOption));
        using var response = await fixture.Client.PostAsync(new Uri(CanonicalPath, UriKind.Relative), content).ConfigureAwait(false);
        var xml = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        AssertFault(xml, "ErrorInvalidPropertyRequest");
        await fixture.AssertNoWorkerRequestsAsync().ConfigureAwait(false);
        await fixture.AssertRecordedResponseAsync("ews", CanonicalPath, 500, xml, rejection: false).ConfigureAwait(false);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task MimeRefusesUnrepresentableOrMissingOriginalStreams(bool missing)
    {
        var fixture = await CaptureFixture.CreateAsync(mailFolders: true).ConfigureAwait(false);
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        var item = await SeedItemAsync(fixture, GatewayEwsFixtureDomain.InboxId, "Subject: 8-bit č\r\n\r\nExact č stream").ConfigureAwait(false);
        if (missing)
        {
            var database = Context(fixture);
            await using var databaseLifetime = database.ConfigureAwait(false);
            var email = await database.Emails.SingleAsync(value => value.Id == item).ConfigureAwait(false);
            email.RawMessageObjectProvider = null;
            email.RawMessageObjectName = null;
            email.RawMessageObjectSha256 = null;
            email.RawMessageObjectEntityTag = null;
            await database.SaveChangesAsync().ConfigureAwait(false);
        }
        Authenticate(fixture);
        var xml = await MimeResponseAsync(fixture, $"<t:ItemId Id='{ItemId(item)}'/>").ConfigureAwait(false);
        Assert.AreEqual("ErrorInvalidPropertyRequest", XDocument.Parse(xml).Descendants(Messages + "ResponseCode").Single().Value, StringComparer.Ordinal);
        Assert.IsEmpty(XDocument.Parse(xml).Descendants(Types + "MimeContent"));
    }

    [TestMethod]
    [DataRow("plain-http", 403)]
    [DataRow("missing-password", 401)]
    [DataRow("bearer", 401)]
    public async Task MimeCannotRelaxTlsOrPasswordAuthority(string mode, int status)
    {
        var fixture = await CaptureFixture.CreateAsync(mailFolders: true).ConfigureAwait(false);
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        if (mode is "plain-http") fixture.Client.DefaultRequestHeaders.Remove("X-Forwarded-Proto");
        if (mode is "missing-password") fixture.Client.DefaultRequestHeaders.Authorization = null;
        if (mode is "bearer") fixture.Client.DefaultRequestHeaders.Authorization = new("Bearer", "test-not-authority");
        using var content = XmlContent(ItemRequest("<t:ItemId Id='opaque'/>", MimeOption));
        using var response = await fixture.Client.PostAsync(new Uri(CanonicalPath, UriKind.Relative), content).ConfigureAwait(false);
        var xml = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.AreEqual(status, (int)response.StatusCode);
        Assert.IsEmpty(XDocument.Parse(xml).Descendants(Types + "MimeContent"));
        await fixture.AssertNoWorkerRequestsAsync().ConfigureAwait(false);
        await fixture.AssertRecordedResponseAsync("ews", CanonicalPath, status, xml, rejection: false).ConfigureAwait(false);
    }

    private static async Task<string> MimeResponseAsync(CaptureFixture fixture, string references, string fields = "", string path = CanonicalPath)
    {
        using var content = XmlContent(ItemRequest(references, MimeOption + fields));
        using var response = await fixture.Client.PostAsync(new Uri(path, UriKind.Relative), content).ConfigureAwait(false);
        var xml = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        await fixture.AssertRecordedResponseAsync("ews", path, 200, xml, rejection: false).ConfigureAwait(false);
        return xml;
    }
}
