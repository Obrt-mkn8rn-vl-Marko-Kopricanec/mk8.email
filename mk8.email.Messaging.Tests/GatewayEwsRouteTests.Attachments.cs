using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MimeKit;
using mk8.email.Application.Services;
using mk8.email.Contracts.Messaging;
using mk8.email.Contracts.Storage;
using mk8.email.Gateway.Protocols.Ews;
using mk8.email.Infrastructure.Data;
using mk8.email.Jmap;
using CaptureFixture = mk8.email.Messaging.Tests.GatewayHttpCaptureBoundaryTests.CaptureFixture;

namespace mk8.email.Messaging.Tests;

internal sealed partial class GatewayEwsRouteTests
{
    private const string AttachmentFields = "<t:AdditionalProperties><t:FieldURI FieldURI='item:Attachments'/><t:FieldURI FieldURI='item:Body'/></t:AdditionalProperties>";
    private const string AttachmentMime = "From: Sender <sender@example.test>\r\nSubject: Attachment route\r\nMIME-Version: 1.0\r\n"
        + "Content-Type: multipart/mixed; boundary=files\r\n\r\n--files\r\nContent-Type: text/plain; charset=utf-8\r\n\r\nVisible body\r\n"
        + "--files\r\nContent-Type: application/octet-stream; name=private.bin\r\nContent-Disposition: attachment; filename=private.bin\r\n"
        + "Content-Transfer-Encoding: base64\r\n\r\nAAECA//+\r\n--files--\r\n";
    private static readonly byte[] AttachmentBytes = [0, 1, 2, 3, 255, 254];
    private static readonly string[] AttachmentOutcomes = ["ErrorInvalidAttachmentId", "ErrorAccessDenied", "ErrorInvalidAttachmentId", "NoError", "NoError"];
    private const string RelatedHasFields = "<t:AdditionalProperties><t:FieldURI FieldURI='item:HasAttachments'/></t:AdditionalProperties>";
    private static readonly string[] RelatedReadFields = [RelatedHasFields,
        "<t:AdditionalProperties><t:FieldURI FieldURI='item:HasAttachments'/><t:FieldURI FieldURI='item:Attachments'/></t:AdditionalProperties>"];

    [TestMethod]
    [DataRow(CanonicalPath, false)]
    [DataRow(CanonicalPath, true)]
    [DataRow("/eWs/EXCHANGE.ASMX/", false)]
    [DataRow("/eWs/EXCHANGE.ASMX/", true)]
    public async Task FileAttachmentMetadataAndContentUseRealAzureWorkerAndOriginalSessions(string path, bool ordinaryClient)
    {
        var fixture = await CaptureFixture.CreateAsync(ordinaryClient, mailFolders: true).ConfigureAwait(false);
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        await fixture.AssertAdminBoundaryAsync(ordinaryClient).ConfigureAwait(false);
        var item = await SeedItemAsync(fixture, GatewayEwsFixtureDomain.InboxId, AttachmentMime).ConfigureAwait(false);
        Authenticate(fixture);
        var metadata = await AttachmentMetadataAsync(fixture, item, path).ConfigureAwait(false);
        Assert.IsEmpty(metadata.Descendants(Types + "Content"));
        Assert.AreEqual("Visible body", metadata.Descendants(Types + "Body").Single().Value, StringComparer.Ordinal);
        var id = (string)metadata.Descendants(Types + "AttachmentId").Single().Attribute("Id")!;
        var document = XDocument.Parse(await AttachmentResponseAsync(fixture, $"<t:AttachmentId Id='{id}'/>", path: path).ConfigureAwait(false));
        var file = document.Descendants(Types + "FileAttachment").Single();
        Assert.AreEqual("private.bin", file.Element(Types + "Name")!.Value, StringComparer.Ordinal);
        Assert.AreEqual("6", file.Element(Types + "Size")!.Value, StringComparer.Ordinal);
        CollectionAssert.AreEqual(AttachmentBytes, Convert.FromBase64String(file.Element(Types + "Content")!.Value));
        await fixture.AssertWorkerOperationsAsync(ApplicationOperations.JmapProfileGet, ApplicationOperations.MailOperationExecute,
            ApplicationOperations.JmapProfileGet, ApplicationOperations.MailOperationExecute).ConfigureAwait(false);
        var database = Context(fixture);
        await using var databaseLifetime = database.ConfigureAwait(false);
        var stored = await database.Emails.SingleAsync(email => email.Id == item).ConfigureAwait(false);
        Assert.IsNull(stored.RawMessage);
        Assert.AreEqual("azure-blob", stored.RawMessageObjectProvider, StringComparer.Ordinal);
    }

    [TestMethod]
    public async Task AttachmentForgeryAndDuplicatesCannotSupplyParentAuthority()
    {
        var fixture = await CaptureFixture.CreateAsync(mailFolders: true).ConfigureAwait(false);
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        var item = await SeedItemAsync(fixture, GatewayEwsFixtureDomain.InboxId, AttachmentMime).ConfigureAwait(false);
        var foreign = await SeedItemAsync(fixture, GatewayEwsFixtureDomain.ForeignFolderId, AttachmentMime).ConfigureAwait(false);
        Authenticate(fixture);
        var metadata = await AttachmentMetadataAsync(fixture, item).ConfigureAwait(false);
        var id = (string)metadata.Descendants(Types + "AttachmentId").Single().Attribute("Id")!;
        var digest = SHA256.HashData(Encoding.ASCII.GetBytes(AttachmentMime));
        var foreignId = GatewayEwsAttachmentIdCodec.Encode(GatewayEwsFixtureDomain.ForeignAccountId, foreign, digest, 0);
        var forged = GatewayEwsAttachmentIdCodec.Encode(GatewayEwsFixtureDomain.AccountId, foreign, digest, 0);
        var xml = await AttachmentResponseAsync(fixture, $"<t:AttachmentId Id='opaque'/><t:AttachmentId Id='{foreignId}'/><t:AttachmentId Id='{forged}'/>"
            + $"<t:AttachmentId Id='{id}'/><t:AttachmentId Id='{id}'/>").ConfigureAwait(false);
        var document = XDocument.Parse(xml);
        CollectionAssert.AreEqual(AttachmentOutcomes, document.Descendants(Messages + "ResponseCode").Select(element => element.Value).ToArray());
        Assert.HasCount(2, document.Descendants(Types + "Content").ToArray());
        foreach (var content in document.Descendants(Types + "Content")) CollectionAssert.AreEqual(AttachmentBytes, Convert.FromBase64String(content.Value));
    }

    [TestMethod]
    [DataRow("flags", "NoError")]
    [DataRow("replaced", "ErrorInvalidAttachmentId")]
    [DataRow("deleted", "ErrorInvalidAttachmentId")]
    [DataRow("scope", "ErrorInvalidAttachmentId")]
    public async Task AttachmentIdentitySurvivesFlagsButNotMimeOrAuthorityChanges(string mode, string expected)
    {
        var fixture = await CaptureFixture.CreateAsync(mailFolders: true).ConfigureAwait(false);
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        var item = await SeedItemAsync(fixture, GatewayEwsFixtureDomain.InboxId, AttachmentMime).ConfigureAwait(false);
        Authenticate(fixture);
        var metadata = await AttachmentMetadataAsync(fixture, item).ConfigureAwait(false);
        var id = (string)metadata.Descendants(Types + "AttachmentId").Single().Attribute("Id")!;
        using (var scope = fixture.DomainScopes.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
            var transaction = await database.Database.BeginTransactionAsync().ConfigureAwait(false);
            await using var transactionLifetime = transaction.ConfigureAwait(false);
            var email = await database.Emails.SingleAsync(value => value.Id == item).ConfigureAwait(false);
            if (mode is "flags") email.IsRead = true;
            if (mode is "deleted") email.IsDeleted = true;
            if (mode is "scope") email.FolderId = GatewayEwsFixtureDomain.ForeignFolderId;
            if (mode is "replaced") await scope.ServiceProvider.GetRequiredService<MailboxMessageContentService>().SetAsync(email,
                Encoding.ASCII.GetBytes(AttachmentMime.Replace("AAECA//+", "BAUGBwgJ", StringComparison.Ordinal)), CancellationToken.None).ConfigureAwait(false);
            await database.SaveChangesAsync().ConfigureAwait(false);
            await transaction.CommitAsync().ConfigureAwait(false);
        }
        var document = XDocument.Parse(await AttachmentResponseAsync(fixture, $"<t:AttachmentId Id='{id}'/>").ConfigureAwait(false));
        Assert.AreEqual(expected, document.Descendants(Messages + "ResponseCode").Single().Value, StringComparer.Ordinal);
        if (mode is "flags") CollectionAssert.AreEqual(AttachmentBytes, Convert.FromBase64String(document.Descendants(Types + "Content").Single().Value));
        else Assert.IsEmpty(document.Descendants(Types + "Content"));
    }

    [TestMethod]
    public async Task AttachmentParentBudgetRejectsBeforeFetchingMissingOversizedBlob()
    {
        var fixture = await CaptureFixture.CreateAsync(mailFolders: true).ConfigureAwait(false);
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        var mime = AttachmentMime + new string('x', GatewayEwsClient.MaximumMimeBytes);
        var item = await SeedItemAsync(fixture, GatewayEwsFixtureDomain.InboxId, mime).ConfigureAwait(false);
        using (var scope = fixture.DomainScopes.CreateScope())
        {
            var email = await scope.ServiceProvider.GetRequiredService<EmailDbContext>().Emails.SingleAsync(value => value.Id == item).ConfigureAwait(false);
            var reference = scope.ServiceProvider.GetRequiredService<MailboxMessageContentService>().TryGetReference(email)!;
            Assert.IsTrue(await scope.ServiceProvider.GetRequiredService<ILargeObjectStore>().DeleteIfMatchAsync(reference).ConfigureAwait(false));
        }
        Authenticate(fixture);
        var id = GatewayEwsAttachmentIdCodec.Encode(GatewayEwsFixtureDomain.AccountId, item, SHA256.HashData(Encoding.ASCII.GetBytes(mime)), 0);
        var document = XDocument.Parse(await AttachmentResponseAsync(fixture, $"<t:AttachmentId Id='{id}'/>").ConfigureAwait(false));
        Assert.AreEqual("ErrorDataSizeLimitExceeded", document.Descendants(Messages + "ResponseCode").Single().Value, StringComparer.Ordinal);
        Assert.IsEmpty(document.Descendants(Types + "Content"));
    }

    [TestMethod]
    public async Task RepeatedDecodedAttachmentsCannotOverrunTheDurableResponseBudget()
    {
        var fixture = await CaptureFixture.CreateAsync(mailFolders: true).ConfigureAwait(false);
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        var mime = AttachmentMime.Replace("AAECA//+", Convert.ToBase64String(new byte[600_000]), StringComparison.Ordinal);
        var item = await SeedItemAsync(fixture, GatewayEwsFixtureDomain.InboxId, mime).ConfigureAwait(false);
        Authenticate(fixture);
        var metadata = await AttachmentMetadataAsync(fixture, item).ConfigureAwait(false);
        var id = (string)metadata.Descendants(Types + "AttachmentId").Single().Attribute("Id")!;
        using var content = XmlContent(AttachmentRequest(string.Concat(Enumerable.Repeat($"<t:AttachmentId Id='{id}'/>", 4))));
        using var response = await fixture.Client.PostAsync(new Uri(CanonicalPath, UriKind.Relative), content).ConfigureAwait(false);
        var xml = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.InternalServerError, response.StatusCode);
        AssertFault(xml, "ErrorDataSizeLimitExceeded");
        Assert.IsEmpty(XDocument.Parse(xml).Descendants(Types + "Content"));
        await fixture.AssertRecordedResponseAsync("ews", CanonicalPath, 500, xml, rejection: false).ConfigureAwait(false);
    }

    [TestMethod]
    [DataRow(0L)]
    [DataRow(1L)]
    public async Task AttachmentJournalFailureWithholdsDecodedBytes(long sequence)
    {
        var fixture = await CaptureFixture.CreateAsync(failSequence: sequence, mailFolders: true).ConfigureAwait(false);
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        var item = await SeedItemAsync(fixture, GatewayEwsFixtureDomain.InboxId, AttachmentMime).ConfigureAwait(false);
        var id = GatewayEwsAttachmentIdCodec.Encode(GatewayEwsFixtureDomain.AccountId, item, SHA256.HashData(Encoding.ASCII.GetBytes(AttachmentMime)), 0);
        Authenticate(fixture);
        using var content = XmlContent(AttachmentRequest($"<t:AttachmentId Id='{id}'/>"));
        using var response = await fixture.Client.PostAsync(new Uri(CanonicalPath, UriKind.Relative), content).ConfigureAwait(false);
        var xml = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        AssertFault(xml, "ErrorServerBusy");
        Assert.IsFalse(xml.Contains("NoError", StringComparison.Ordinal));
        Assert.IsEmpty(XDocument.Parse(xml).Descendants(Types + "Content"));
        await fixture.AssertPresentationRecordCountAsync(sequence).ConfigureAwait(false);
    }

    [TestMethod]
    [DataRow("plain-http", 403)]
    [DataRow("missing-password", 401)]
    [DataRow("bearer", 401)]
    public async Task AttachmentRequestCannotRelaxTlsOrPasswordAuthority(string mode, int status)
    {
        var fixture = await CaptureFixture.CreateAsync(mailFolders: true).ConfigureAwait(false);
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        if (mode is "plain-http") fixture.Client.DefaultRequestHeaders.Remove("X-Forwarded-Proto");
        if (mode is "missing-password") fixture.Client.DefaultRequestHeaders.Authorization = null;
        if (mode is "bearer") fixture.Client.DefaultRequestHeaders.Authorization = new("Bearer", "not-authority");
        using var content = XmlContent(AttachmentRequest("<t:AttachmentId Id='opaque'/>"));
        using var response = await fixture.Client.PostAsync(new Uri(CanonicalPath, UriKind.Relative), content).ConfigureAwait(false);
        var xml = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.AreEqual(status, (int)response.StatusCode);
        await fixture.AssertNoWorkerRequestsAsync().ConfigureAwait(false);
        await fixture.AssertRecordedResponseAsync("ews", CanonicalPath, status, xml, rejection: false).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task FindItemCannotAcquireAttachmentMetadataOrContent()
    {
        var fixture = await CaptureFixture.CreateAsync(mailFolders: true).ConfigureAwait(false);
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        Authenticate(fixture);
        using var content = XmlContent(FindRequest("<t:DistinguishedFolderId Id='inbox'/>", fields: AttachmentFields));
        using var response = await fixture.Client.PostAsync(new Uri(CanonicalPath, UriKind.Relative), content).ConfigureAwait(false);
        var xml = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        AssertFault(xml, "ErrorInvalidPropertyRequest");
        await fixture.AssertNoWorkerRequestsAsync().ConfigureAwait(false);
        await fixture.AssertRecordedResponseAsync("ews", CanonicalPath, 500, xml, rejection: false).ConfigureAwait(false);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task InlineAttachmentIndicatorAndCollectionAgreeAcrossShapes(bool reversedRoot)
    {
        var fixture = await CaptureFixture.CreateAsync(mailFolders: true).ConfigureAwait(false);
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        const string image = "Content-Type: image/png\r\nContent-ID: <photo>\r\nContent-Transfer-Encoding: base64\r\n\r\nAAECA//+\r\n";
        const string body = "Content-Type: text/html\r\nContent-ID: <body>\r\n\r\n<img src='cid:photo'>\r\n";
        var mime = "Subject: Inline fixture\r\nMIME-Version: 1.0\r\nContent-Type: multipart/related; boundary=inline; start=\"<body>\"\r\n\r\n--inline\r\n"
            + (reversedRoot ? image : body) + "--inline\r\n" + (reversedRoot ? body : image) + "--inline--\r\n";
        var item = await SeedItemAsync(fixture, GatewayEwsFixtureDomain.InboxId, mime).ConfigureAwait(false);
        Authenticate(fixture);
        foreach (var fields in new[] { "<t:AdditionalProperties><t:FieldURI FieldURI='item:HasAttachments'/></t:AdditionalProperties>",
            "<t:AdditionalProperties><t:FieldURI FieldURI='item:HasAttachments'/><t:FieldURI FieldURI='item:Attachments'/></t:AdditionalProperties>" })
        {
            using var content = XmlContent(ItemRequest($"<t:ItemId Id='{ItemId(item)}'/>", fields));
            using var response = await fixture.Client.PostAsync(new Uri(CanonicalPath, UriKind.Relative), content).ConfigureAwait(false);
            var xml = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var document = XDocument.Parse(xml);
            Assert.AreEqual("false", document.Descendants(Types + "HasAttachments").Single().Value, StringComparer.Ordinal);
            if (fields.Contains("item:Attachments", StringComparison.Ordinal))
                Assert.AreEqual("true", document.Descendants(Types + "IsInline").Single().Value, StringComparer.Ordinal);
            await fixture.AssertRecordedResponseAsync("ews", CanonicalPath, 200, xml, rejection: false).ConfigureAwait(false);
        }
    }

    [TestMethod]
    [DataRow("<t:IncludeMimeContent>true</t:IncludeMimeContent>")]
    [DataRow("<t:BodyType>Best</t:BodyType>")]
    [DataRow("<t:FilterHtmlContent>false</t:FilterHtmlContent>")]
    [DataRow("<t:AdditionalProperties><t:FieldURI FieldURI='item:Body'/></t:AdditionalProperties>")]
    public async Task UnsupportedAttachmentShapesAreJournaledWithoutWorkerDispatch(string option)
    {
        var fixture = await CaptureFixture.CreateAsync(mailFolders: true).ConfigureAwait(false);
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        Authenticate(fixture);
        using var content = XmlContent(AttachmentRequest("<t:AttachmentId Id='opaque'/>", $"<m:AttachmentShape>{option}</m:AttachmentShape>"));
        using var response = await fixture.Client.PostAsync(new Uri(CanonicalPath, UriKind.Relative), content).ConfigureAwait(false);
        var xml = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.InternalServerError, response.StatusCode);
        AssertFault(xml, "ErrorInvalidPropertyRequest");
        await fixture.AssertNoWorkerRequestsAsync().ConfigureAwait(false);
        await fixture.AssertRecordedResponseAsync("ews", CanonicalPath, 500, xml, rejection: false).ConfigureAwait(false);
    }

    [TestMethod]
    [DataRow("mixed", false)]
    [DataRow("mixed", true)]
    [DataRow("alternative", false)]
    [DataRow("alternative", true)]
    public async Task GroupedAttachmentSubtreesAreRefusedWithoutInventingFiles(string kind, bool root)
    {
        var fixture = await CaptureFixture.CreateAsync(mailFolders: true).ConfigureAwait(false);
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        const string headers = "Subject: Grouped attachment\r\nMIME-Version: 1.0\r\n";
        var group = $"Content-Type: multipart/{kind}; boundary=blocked\r\nContent-Disposition: attachment\r\n\r\n"
            + "--blocked\r\nContent-Type: text/plain\r\n\r\nPRIVATE ATTACHMENT\r\n--blocked--\r\n";
        var mime = headers + (root ? group : "Content-Type: multipart/mixed; boundary=outer\r\n\r\n"
            + "--outer\r\nContent-Type: text/plain\r\n\r\nVisible body\r\n--outer\r\n" + group + "--outer--\r\n");
        var item = await SeedItemAsync(fixture, GatewayEwsFixtureDomain.InboxId, mime).ConfigureAwait(false);
        Authenticate(fixture);
        var metadata = await AttachmentMetadataAsync(fixture, item).ConfigureAwait(false);
        Assert.AreEqual("ErrorInvalidPropertyRequest", metadata.Descendants(Messages + "ResponseCode").Single().Value, StringComparer.Ordinal);
        Assert.IsEmpty(metadata.Descendants(Types + "FileAttachment"));
        var id = GatewayEwsAttachmentIdCodec.Encode(GatewayEwsFixtureDomain.AccountId, item, SHA256.HashData(Encoding.ASCII.GetBytes(mime)), 0);
        var xml = await AttachmentResponseAsync(fixture, $"<t:AttachmentId Id='{id}'/>").ConfigureAwait(false);
        Assert.AreEqual("ErrorInvalidPropertyRequest", XDocument.Parse(xml).Descendants(Messages + "ResponseCode").Single().Value, StringComparer.Ordinal);
        Assert.IsFalse(xml.Contains("PRIVATE ATTACHMENT", StringComparison.Ordinal));
        Assert.IsEmpty(XDocument.Parse(xml).Descendants(Types + "Content"));
    }

    [TestMethod]
    [DataRow(null, false)]
    [DataRow(null, true)]
    [DataRow("inline", false)]
    [DataRow("inline", true)]
    [DataRow("attachment", false)]
    [DataRow("attachment", true)]
    public async Task RelatedAttachmentDispositionStaysInlineAcrossActualStoredAndHttpProjections(string? disposition, bool reversed)
    {
        await AssertRelatedHttpProjectionsAsync(RelatedRouteMime("valid", disposition, reversed), expected: false, refuse: false).ConfigureAwait(false);
    }

    [TestMethod]
    [DataRow("absent", false, false)]
    [DataRow("default", true, false)]
    [DataRow("valid", false, false)]
    [DataRow("comments", false, false)]
    [DataRow("unmatched", true, false)]
    [DataRow("malformed", true, false)]
    [DataRow("empty", false, true)]
    [DataRow("single", true, false)]
    public async Task RelatedRootSelectionAgreesAcrossAttachmentAndMetadataOnlyHttpReads(string mode, bool expected, bool refuse)
    {
        await AssertRelatedHttpProjectionsAsync(RelatedRouteMime(mode, null, reversed: true), expected, refuse).ConfigureAwait(false);
    }

    private static async Task AssertRelatedHttpProjectionsAsync(string mime, bool expected, bool refuse)
    {
        var fixture = await CaptureFixture.CreateAsync(mailFolders: true).ConfigureAwait(false);
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        var item = await SeedItemAsync(fixture, GatewayEwsFixtureDomain.ChildId, mime).ConfigureAwait(false);
        await AssertRelatedStoredSnapshotAsync(fixture, item, mime, expected, refuse).ConfigureAwait(false);
        Authenticate(fixture);
        string? attachment = null;
        foreach (var fields in RelatedReadFields)
        {
            using var content = XmlContent(ItemRequest($"<t:ItemId Id='{ItemId(item)}'/>", fields));
            using var response = await fixture.Client.PostAsync(new Uri(CanonicalPath, UriKind.Relative), content).ConfigureAwait(false);
            var xml = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var document = XDocument.Parse(xml);
            Assert.AreEqual(refuse ? "ErrorInvalidPropertyRequest" : "NoError", document.Descendants(Messages + "ResponseCode").Single().Value, StringComparer.Ordinal);
            Assert.IsEmpty(document.Descendants(Types + "Content"));
            Assert.IsEmpty(document.Descendants(Types + "Body"));
            Assert.IsEmpty(document.Descendants(Types + "MimeContent"));
            if (!refuse)
            {
                Assert.AreEqual(expected, bool.Parse(document.Descendants(Types + "HasAttachments").Single().Value));
                if (fields.Contains("item:Attachments", StringComparison.Ordinal))
                {
                    var photo = document.Descendants(Types + "FileAttachment").Single(file => file.Element(Types + "Name")!.Value is "photo.png");
                    Assert.AreEqual(!expected, bool.Parse(photo.Element(Types + "IsInline")!.Value));
                    attachment = (string)photo.Element(Types + "AttachmentId")!.Attribute("Id")!;
                }
            }
            await fixture.AssertRecordedResponseAsync("ews", CanonicalPath, 200, xml, rejection: false).ConfigureAwait(false);
        }
        await AssertRelatedFindItemAsync(fixture, expected, refuse).ConfigureAwait(false);
        attachment ??= GatewayEwsAttachmentIdCodec.Encode(GatewayEwsFixtureDomain.AccountId, item, SHA256.HashData(Encoding.ASCII.GetBytes(mime)), 0);
        var retrievedXml = await AttachmentResponseAsync(fixture, $"<t:AttachmentId Id='{attachment}'/>").ConfigureAwait(false);
        var retrieved = XDocument.Parse(retrievedXml);
        Assert.AreEqual(refuse ? "ErrorInvalidPropertyRequest" : "NoError", retrieved.Descendants(Messages + "ResponseCode").Single().Value, StringComparer.Ordinal);
        if (!refuse)
        {
            Assert.AreEqual(!expected, bool.Parse(retrieved.Descendants(Types + "IsInline").Single().Value));
            CollectionAssert.AreEqual(AttachmentBytes, Convert.FromBase64String(retrieved.Descendants(Types + "Content").Single().Value));
        }
        else Assert.IsEmpty(retrieved.Descendants(Types + "Content"));
    }

    private static async Task AssertRelatedFindItemAsync(CaptureFixture fixture, bool expected, bool refuse)
    {
        var folder = GatewayEwsFolderIdCodec.Encode(GatewayEwsFixtureDomain.AccountId, GatewayEwsFixtureDomain.ChildId);
        using var content = XmlContent(FindRequest($"<t:FolderId Id='{folder}'/>", fields: RelatedHasFields));
        using var response = await fixture.Client.PostAsync(new Uri(CanonicalPath, UriKind.Relative), content).ConfigureAwait(false);
        var xml = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var document = XDocument.Parse(xml);
        Assert.AreEqual(refuse ? "ErrorInvalidPropertyRequest" : "NoError", document.Descendants(Messages + "ResponseCode").Single().Value, StringComparer.Ordinal);
        if (!refuse) Assert.AreEqual(expected, bool.Parse(document.Descendants(Types + "HasAttachments").Single().Value));
        Assert.IsEmpty(document.Descendants(Types + "Attachments"));
        await fixture.AssertRecordedResponseAsync("ews", CanonicalPath, 200, xml, rejection: false).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task EarlierVisibleAttachmentCannotHideLaterRelatedRefusalInHttpProjections()
    {
        var related = RelatedRouteMime("empty", null, reversed: true);
        related = related[(related.IndexOf("Content-Type:", StringComparison.Ordinal))..];
        var mime = "Subject: Related admission ordering\r\nMIME-Version: 1.0\r\nContent-Type: multipart/mixed; boundary=outer\r\n\r\n--outer\r\n"
            + "Content-Type: application/octet-stream\r\nContent-Disposition: attachment; filename=earlier.bin\r\n\r\nEARLIER FILE\r\n--outer\r\n"
            + related + "--outer--\r\n";
        await AssertRelatedHttpProjectionsAsync(mime, expected: false, refuse: true).ConfigureAwait(false);
    }

    private static async Task AssertRelatedStoredSnapshotAsync(CaptureFixture fixture, Guid item, string mime, bool expected, bool refuse)
    {
        using var scope = fixture.DomainScopes.CreateScope();
        var email = await scope.ServiceProvider.GetRequiredService<EmailDbContext>().Emails.SingleAsync(value => value.Id == item).ConfigureAwait(false);
        Assert.IsNull(email.RawMessage);
        Assert.AreEqual("azure-blob", email.RawMessageObjectProvider, StringComparer.Ordinal);
        var raw = await scope.ServiceProvider.GetRequiredService<MailboxMessageContentService>().ReadAsync(email, CancellationToken.None).ConfigureAwait(false);
        CollectionAssert.AreEqual(Encoding.ASCII.GetBytes(mime), raw);
        using var input = new MemoryStream(raw);
        using var message = await MimeMessage.LoadAsync(input, CancellationToken.None).ConfigureAwait(false);
        var stored = JmapEmailCodec.Capture(message, item, raw.Length, includeText: false, stored: email);
        if (refuse) Assert.AreEqual("ErrorInvalidPropertyRequest", Assert.Throws<GatewayEwsRequestException>(() => GatewayEwsAttachmentCatalog.HasNonInlineAttachments(stored)).Code, StringComparer.Ordinal);
        else Assert.AreEqual(expected, GatewayEwsAttachmentCatalog.HasNonInlineAttachments(stored));
    }

    private static string RelatedRouteMime(string mode, string? disposition, bool reversed)
    {
        var parameters = mode switch
        {
            "default" => "",
            "absent" => "; type=\"text/html\"",
            "valid" => "; type=\"text/html\"; start=\"<body@example.test>\"",
            "comments" => "; type=\"text/html\"; start=\"(root) <body@example.test>\"",
            "malformed" => "; type=\"image/png\"; start=\"not-a-message-id\"",
            "empty" => "; type=\"image/png\"; start=\"\"",
            _ => "; type=\"image/png\"; start=\"<missing@example.test>\"",
        };
        var image = "Content-Type: image/png; name=photo.png\r\nContent-ID: <photo@example.test>\r\n"
            + (disposition is null ? "" : $"Content-Disposition: {disposition}; filename=photo.png\r\n")
            + "Content-Transfer-Encoding: base64\r\n\r\nAAECA//+\r\n";
        const string body = "Content-Type: text/html\r\nContent-ID: <body@example.test>\r\n\r\n<img src='cid:photo@example.test'>\r\n";
        return "Subject: Related source\r\nMIME-Version: 1.0\r\nContent-Type: multipart/related; boundary=related" + parameters + "\r\n\r\n--related\r\n"
            + (mode is "single" ? image : (reversed ? image : body) + "--related\r\n" + (reversed ? body : image)) + "--related--\r\n";
    }

    private static async Task<XDocument> AttachmentMetadataAsync(CaptureFixture fixture, Guid item, string path = CanonicalPath)
    {
        using var content = XmlContent(ItemRequest($"<t:ItemId Id='{ItemId(item)}'/>", AttachmentFields));
        using var response = await fixture.Client.PostAsync(new Uri(path, UriKind.Relative), content).ConfigureAwait(false);
        var xml = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        await fixture.AssertRecordedResponseAsync("ews", path, 200, xml, rejection: false).ConfigureAwait(false);
        return XDocument.Parse(xml);
    }

    private static async Task<string> AttachmentResponseAsync(CaptureFixture fixture, string references, string path = CanonicalPath)
    {
        using var content = XmlContent(AttachmentRequest(references));
        using var response = await fixture.Client.PostAsync(new Uri(path, UriKind.Relative), content).ConfigureAwait(false);
        var xml = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        await fixture.AssertRecordedResponseAsync("ews", path, 200, xml, rejection: false).ConfigureAwait(false);
        return xml;
    }

    private static string AttachmentRequest(string references, string shape = "") =>
        $"<s:Envelope xmlns:s='{GatewayEwsSoap.Soap}' xmlns:m='{Messages}' xmlns:t='{Types}'><s:Body><m:GetAttachment>{shape}<m:AttachmentIds>{references}</m:AttachmentIds></m:GetAttachment></s:Body></s:Envelope>";
}
