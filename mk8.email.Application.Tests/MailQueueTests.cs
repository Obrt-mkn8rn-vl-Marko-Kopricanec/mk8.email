using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Text;
using MimeKit;
using mk8.email.Application.Interfaces;
using mk8.email.Application.Services;
using mk8.email.Contracts.Enums;
using mk8.email.Contracts.Storage;
using mk8.email.Infrastructure.Data;
using mk8.email.Configuration;
using mk8.email.Infrastructure.Models;
using mk8.email.Jmap;

namespace mk8.email.Application.Tests;

[TestClass]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1812", Justification = "MSTest DiscoverInternals instantiates this test class by reflection; focused discovery is verified by executed test counts.")]
internal sealed class MailQueueTests
{
    private const string TestDomain = "mk8n.com";
    private const string TestAccount = "admin@mk8n.com";
    private const string RawMessage =
        "From: sender@example.net\r\n" +
        "To: admin@mk8n.com\r\n" +
        "Subject: queue test\r\n\r\n" +
        "body\r\n";

    [TestMethod]
    [DataRow("id", "The queue identifier is not valid.")]
    [DataRow("sender", "The envelope sender is not valid.")]
    [DataRow("none", "The recipient count is not valid.")]
    [DataRow("many", "The recipient count is not valid.")]
    [DataRow("wire", "The message is not in the mail wire byte representation.")]
    [DataRow("size", "The message is larger than the configured limit.")]
    [DataRow("recipient", "A recipient address is not valid.")]
    [DataRow("auth", "The authenticated user is not valid.")]
    [DataRow("return", "The DSN return-content request is not valid.")]
    [DataRow("envelope", "The DSN envelope identifier is not valid.")]
    [DataRow("notify", "A DSN notification request is not valid.")]
    [DataRow("original", "A DSN original recipient is not valid.")]
    [DataRow("recipient-before-auth", "A recipient address is not valid.")]
    [DataRow("authenticated-empty-sender", "The envelope sender is not valid.")]
    [DataRow("wire-before-recipient", "The message is not in the mail wire byte representation.")]
    public async Task SubmissionValidationFinishesBeforeAnyPayloadOrRowIsCreated(string input, string message)
    {
        var services = CreateServices(CreateEnvironment(),
            CleanScan(), new StubRelay(OutboundDeliveryStatus.Delivered));
        await using var servicesLifetime = services.ConfigureAwait(false);
        using var scope = services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
        await database.Database.EnsureCreatedAsync().ConfigureAwait(false);
        var queue = scope.ServiceProvider.GetRequiredService<IMailSubmissionQueue>();
        var exception = await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
            queue.EnqueueAsync(CreateInvalidSubmission(input))).ConfigureAwait(false);
        Assert.AreEqual("submission", exception.ParamName, StringComparer.Ordinal);
        StringAssert.StartsWith(exception.Message, message, StringComparison.Ordinal);
        Assert.AreEqual(0, await database.MailQueueMessages.CountAsync().ConfigureAwait(false));
        Assert.AreEqual(0, await database.MailQueueRecipients.CountAsync().ConfigureAwait(false));
        Assert.AreEqual(0, database.MailQueueMessages.Local.Count);
        Assert.AreEqual(0, services.GetRequiredService<InMemoryLargeObjectStore>().Count);
    }

    private static MailSubmission CreateInvalidSubmission(string input)
    {
        var submission = new MailSubmission(Guid.CreateVersion7(), "sender@example.net",
            [new MailEnvelopeRecipient(TestAccount, true)], RawMessage, null, null, null);
        var invalidRecipient = new MailEnvelopeRecipient("invalid", false);
        return input switch
        {
            "id" => submission with { QueueId = Guid.Empty },
            "sender" => submission with { EnvelopeSender = "invalid" },
            "none" => submission with { Recipients = [] },
            "many" => submission with { Recipients = Enumerable.Repeat(submission.Recipients[0], 11).ToArray() },
            "wire" => submission with { RawMessage = "\u0100" },
            "size" => submission with { RawMessage = new string('x', 1024 * 1024 + 1) },
            "recipient" => submission with { Recipients = [invalidRecipient] },
            "auth" => submission with { AuthenticatedUser = "invalid" },
            "return" => submission with { Dsn = new MailDsnEnvelope("invalid", null) },
            "envelope" => submission with { Dsn = new MailDsnEnvelope(null, "bad\r\nvalue") },
            "notify" => submission with
            {
                Recipients = [submission.Recipients[0],
                    new MailEnvelopeRecipient("other@example.net", false, new MailDsnRecipient("never,failure", null))],
            },
            "original" => submission with
            {
                Recipients = [submission.Recipients[0],
                    new MailEnvelopeRecipient("other@example.net", false, new MailDsnRecipient(null, "invalid"))],
            },
            "recipient-before-auth" => submission with { Recipients = [invalidRecipient], AuthenticatedUser = "invalid" },
            "authenticated-empty-sender" => submission with { EnvelopeSender = string.Empty, AuthenticatedUser = TestAccount },
            "wire-before-recipient" => submission with { RawMessage = "\u0100", Recipients = [invalidRecipient] },
            _ => throw new ArgumentOutOfRangeException(nameof(input)),
        };
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task SubmissionRetainsFirstDuplicateMetadataAndOneSchedulingInstant(bool invalidMetadata)
    {
        var services = CreateServices(CreateEnvironment(),
            CleanScan(), new StubRelay(OutboundDeliveryStatus.Delivered));
        await using var servicesLifetime = services.ConfigureAwait(false);
        using var scope = services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
        await database.Database.EnsureCreatedAsync().ConfigureAwait(false);
        var before = DateTime.UtcNow;
        var queueId = Guid.CreateVersion7();
        var queue = scope.ServiceProvider.GetRequiredService<IMailSubmissionQueue>();
        Assert.AreEqual(queueId, await queue.EnqueueAsync(new MailSubmission(queueId, TestAccount,
            [new MailEnvelopeRecipient(TestAccount, true, new MailDsnRecipient("delay", null)),
                new MailEnvelopeRecipient("ADMIN@MK8N.COM", false, new MailDsnRecipient("invalid", "invalid")),
                new MailEnvelopeRecipient("josé@example.net", false)],
            RawMessage, invalidMetadata ? new string('x', 46) : " 192.0.2.42 ",
            invalidMetadata ? "unsafe\r\nhelo" : " mx.example.net ", TestAccount)).ConfigureAwait(false));
        var queued = await database.MailQueueMessages.Include(item => item.Recipients).SingleAsync().ConfigureAwait(false);
        Assert.AreEqual(invalidMetadata ? null : "192.0.2.42", queued.ClientIp, StringComparer.Ordinal);
        Assert.AreEqual(invalidMetadata ? null : "mx.example.net", queued.Helo, StringComparer.Ordinal);
        Assert.AreEqual(MailQueueDirections.Submission, queued.Direction, StringComparer.Ordinal);
        Assert.AreEqual(TestAccount, queued.AuthenticatedUser, StringComparer.Ordinal);
        Assert.IsTrue(queued.RequiresSmtpUtf8);
        Assert.IsTrue(queued.ReceivedAt >= before && queued.ReceivedAt <= DateTime.UtcNow);
        Assert.AreEqual(queued.ReceivedAt, queued.NextAttemptAt);
        Assert.AreEqual(2, queued.Recipients.Count);
        Assert.IsTrue(queued.Recipients.All(recipient => recipient.NextAttemptAt == queued.ReceivedAt));
        var local = queued.Recipients.Single(recipient => string.Equals(recipient.Recipient, TestAccount, StringComparison.Ordinal));
        Assert.IsTrue(local.IsLocal);
        Assert.AreEqual("DELAY", local.DsnNotify, StringComparer.Ordinal);
        Assert.IsNull(local.DsnOriginalRecipient);
        CollectionAssert.AreEqual(new[] { TestAccount }, local.RedirectHistory);
        Assert.AreEqual(RawMessage, await ReadQueueContentAsync(scope, queued).ConfigureAwait(false), StringComparer.Ordinal);
        Assert.AreEqual(1, services.GetRequiredService<InMemoryLargeObjectStore>().Count);
    }

    [TestMethod]
    public async Task CompletedQueueCleanupDrainsEveryExpiredBatch()
    {
        var environment = CreateEnvironment();
        var services = CreateServices(
            environment, CleanScan(), new StubRelay(OutboundDeliveryStatus.Delivered));
        await using var servicesLifetime = services.ConfigureAwait(false);
        using (var scope = services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
            await (database.MailQueueMessages.AddRangeAsync(Enumerable.Range(0, 1001).Select(_ =>
                new MailQueueMessageDB
                {
                    Id = Guid.CreateVersion7(),
                    EnvelopeSender = "retention@example.test",
                    Direction = MailQueueDirections.Inbound,
                    State = MailQueueStates.Completed,
                    ScanState = MailQueueScanStates.Complete,
                    CompletedAt = DateTime.UtcNow.AddDays(-8),
                }))).ConfigureAwait(false);
            await database.SaveChangesAsync().ConfigureAwait(false);
        }
        using
                var worker = new MailQueueWorker(
                    services.GetRequiredService<IServiceScopeFactory>(),
                    environment,
                    TimeProvider.System,
                    new CapturingQueueLogger());
        await worker.CleanupCompletedAsync(CancellationToken.None).ConfigureAwait(false);

        using var verification = services.CreateScope();
        Assert.AreEqual(0, await verification.ServiceProvider
            .GetRequiredService<EmailDbContext>().MailQueueMessages.CountAsync().ConfigureAwait(false));
    }

    [TestMethod]
    public async Task SubmissionQueuePersistsRawMessageAndDistinctRecipients()
    {
        var environment = CreateEnvironment();
        var services = CreateServices(
            environment,
            CleanScan(),
            new StubRelay(OutboundDeliveryStatus.Delivered));
        await using var servicesLifetime = services.ConfigureAwait(false);
        var queueId = Guid.CreateVersion7();

        using (var scope = services.CreateScope())
        {
            var initializationDatabase = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
            await initializationDatabase.Database.EnsureCreatedAsync().ConfigureAwait(false);
            var queue = scope.ServiceProvider.GetRequiredService<IMailSubmissionQueue>();
            await queue.EnqueueAsync(new MailSubmission(
                queueId,
                "sender@example.net",
                [
                    new MailEnvelopeRecipient(
                        TestAccount,
                        true,
                        new MailDsnRecipient(
                            "failure,delay",
                            "rfc822;admin+40mk8n.com")),
                    new MailEnvelopeRecipient("ADMIN@MK8N.COM", true),
                ],
                RawMessage,
                "192.0.2.10",
                "sender.example.net",
                null,
                Dsn: new MailDsnEnvelope("hdrs", "queue+2Btest"))).ConfigureAwait(false);
        }

        using var verificationScope = services.CreateScope();
        var database = verificationScope.ServiceProvider.GetRequiredService<EmailDbContext>();
        var queued = await database.MailQueueMessages
            .Include(message => message.Recipients)
            .SingleAsync().ConfigureAwait(false);
        Assert.AreEqual(queueId, queued.Id);
        Assert.IsNull(queued.RawMessage);
        Assert.AreEqual(RawMessage, await ReadQueueContentAsync(verificationScope, queued).ConfigureAwait(false), StringComparer.Ordinal);
        Assert.AreEqual(MailQueueStates.Pending, queued.State, StringComparer.Ordinal);
        Assert.AreEqual(MailQueueDirections.Inbound, queued.Direction, StringComparer.Ordinal);
        Assert.AreEqual(1, queued.Recipients.Count);
        Assert.AreEqual(TestAccount, queued.Recipients.Single().Recipient, StringComparer.Ordinal);
        Assert.IsFalse(queued.RequiresSmtpUtf8);
        Assert.AreEqual("HDRS", queued.DsnReturnContent, StringComparer.Ordinal);
        Assert.AreEqual("queue+2Btest", queued.DsnEnvelopeId, StringComparer.Ordinal);
        Assert.AreEqual("FAILURE,DELAY", queued.Recipients.Single().DsnNotify, StringComparer.Ordinal);
        Assert.AreEqual(
            "rfc822;admin+40mk8n.com",
            queued.Recipients.Single().DsnOriginalRecipient, StringComparer.Ordinal);
    }

    [TestMethod]
    public async Task QueuePersistsAndRelaysSmtpUtf8Requirement()
    {
        var environment = CreateEnvironment();
        var relay = new StubRelay(OutboundDeliveryStatus.Delivered);
        var services = CreateServices(environment, CleanScan(), relay);
        await using var servicesLifetime = services.ConfigureAwait(false);
        var queueId = Guid.CreateVersion7();
        var rawMessage = Encoding.Latin1.GetString(Encoding.UTF8.GetBytes(
            "From: josé@example.net\r\n" +
            "To: recipient@example.com\r\n" +
            "Subject: Žuta pošta\r\n\r\n" +
            "body\r\n"));

        using (var scope = services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
            await database.Database.EnsureCreatedAsync().ConfigureAwait(false);
            var queue = scope.ServiceProvider.GetRequiredService<IMailSubmissionQueue>();
            await queue.EnqueueAsync(new MailSubmission(
                queueId,
                "josé@example.net",
                [new MailEnvelopeRecipient("recipient@example.com", false)],
                rawMessage,
                "192.0.2.10",
                "sender.example.net",
                null,
                RequiresSmtpUtf8: true)).ConfigureAwait(false);
        }

        Assert.IsTrue(await ProcessOneAsync(services, environment).ConfigureAwait(false));

        using var verificationScope = services.CreateScope();
        var verificationDatabase = verificationScope.ServiceProvider.GetRequiredService<EmailDbContext>();
        var queued = await verificationDatabase.MailQueueMessages.SingleAsync(
            message => message.Id == queueId).ConfigureAwait(false);
        Assert.IsTrue(queued.RequiresSmtpUtf8);
        Assert.IsNotNull(relay.LastOptions);
        Assert.IsTrue(relay.LastOptions.RequiresSmtpUtf8);
    }

    [TestMethod]
    public async Task QueueDecodesSmtpUtf8HeadersAndBodyForLocalMailboxMetadata()
    {
        var environment = CreateEnvironment();
        var services = CreateServices(
            environment,
            CleanScan(),
            new StubRelay(OutboundDeliveryStatus.Delivered));
        await using var servicesLifetime = services.ConfigureAwait(false);
        await SeedAccountAsync(services, includeCatchAll: false).ConfigureAwait(false);
        var rawMessage = Encoding.Latin1.GetString(Encoding.UTF8.GetBytes(
            "From: José <josé@example.net>\r\n" +
            $"To: {TestAccount}\r\n" +
            "Subject: Žuta pošta\r\n\r\n" +
            "Pozdrav iz Zagreba\r\n"));

        using (var scope = services.CreateScope())
        {
            var queue = scope.ServiceProvider.GetRequiredService<IMailSubmissionQueue>();
            await queue.EnqueueAsync(new MailSubmission(
                Guid.CreateVersion7(),
                "josé@example.net",
                [new MailEnvelopeRecipient(TestAccount, true)],
                rawMessage,
                "192.0.2.10",
                "sender.example.net",
                null,
                RequiresSmtpUtf8: true)).ConfigureAwait(false);
        }

        Assert.IsTrue(await ProcessOneAsync(services, environment).ConfigureAwait(false));

        using var verificationScope = services.CreateScope();
        var database = verificationScope.ServiceProvider.GetRequiredService<EmailDbContext>();
        var delivered = await database.Emails.SingleAsync().ConfigureAwait(false);
        Assert.AreEqual("Žuta pošta", delivered.Subject, StringComparer.Ordinal);
        StringAssert.Contains(delivered.Body, "Pozdrav iz Zagreba", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task SubmissionQueueRejectsMalformedInternationalizedHeaders()
    {
        var environment = CreateEnvironment();
        var services = CreateServices(
            environment,
            CleanScan(),
            new StubRelay(OutboundDeliveryStatus.Delivered));
        await using var servicesLifetime = services.ConfigureAwait(false);
        using var scope = services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
        await database.Database.EnsureCreatedAsync().ConfigureAwait(false);
        var queue = scope.ServiceProvider.GetRequiredService<IMailSubmissionQueue>();
        var malformed = "From: sender@example.net\r\nSubject: "
            + Encoding.Latin1.GetString([0xc3, 0x28])
            + "\r\n\r\nbody\r\n";

        await Assert.ThrowsExactlyAsync<ArgumentException>(() => queue.EnqueueAsync(
            new MailSubmission(
                Guid.CreateVersion7(),
                "sender@example.net",
                [new MailEnvelopeRecipient(TestAccount, true)],
                malformed,
                "192.0.2.10",
                "sender.example.net",
                null,
                RequiresSmtpUtf8: true))).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task WorkerScansAndDeliversInboundMessageFromDurableQueue()
    {
        var environment = CreateEnvironment();
        var scan = CleanScan("Authentication-Results: email.mk8n.com; spf=pass; dkim=pass; dmarc=pass\r\n");
        var services = CreateServices(
            environment,
            scan,
            new StubRelay(OutboundDeliveryStatus.Delivered));
        await using var servicesLifetime = services.ConfigureAwait(false);
        await SeedAccountAsync(services, includeCatchAll: true).ConfigureAwait(false);
        var queueId = await EnqueueAsync(
            services,
            "sender@example.net",
            "undefined@mk8n.com",
            isLocal: true,
            authenticatedUser: null).ConfigureAwait(false);

        Assert.IsTrue(await ProcessOneAsync(services, environment).ConfigureAwait(false));

        using var scope = services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
        var queued = await database.MailQueueMessages
            .Include(message => message.Recipients)
            .SingleAsync(message => message.Id == queueId).ConfigureAwait(false);
        Assert.AreEqual(MailQueueStates.Completed, queued.State, StringComparer.Ordinal);
        Assert.AreEqual(MailQueueRecipientStates.Delivered, queued.Recipients.Single().State, StringComparer.Ordinal);
        var delivered = await database.Emails.Include(message => message.Folder).SingleAsync().ConfigureAwait(false);
        Assert.AreEqual(queued.Recipients.Single().Id, delivered.QueueDeliveryId);
        Assert.AreEqual(DefaultFolders.Inbox, delivered.Folder.Name, StringComparer.Ordinal);
        StringAssert.Contains(delivered.RawHeaders!, "Authentication-Results: email.mk8n.com", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task WorkerRetainsMessageWhenScannerRequestsRetry()
    {
        var environment = CreateEnvironment();
        var scan = new MailScanResult(
            "soft reject",
            0,
            15,
            new HashSet<string>(["CLAM_VIRUS_FAIL"], StringComparer.Ordinal),
            string.Empty,
            IsMalware: false,
            IsTemporaryFailure: true);
        var services = CreateServices(
            environment,
            scan,
            new StubRelay(OutboundDeliveryStatus.Delivered));
        await using var servicesLifetime = services.ConfigureAwait(false);
        await SeedAccountAsync(services, includeCatchAll: false).ConfigureAwait(false);
        var queueId = await EnqueueAsync(
            services,
            "sender@example.net",
            TestAccount,
            isLocal: true,
            authenticatedUser: null).ConfigureAwait(false);

        Assert.IsTrue(await ProcessOneAsync(services, environment).ConfigureAwait(false));

        using var scope = services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
        var queued = await database.MailQueueMessages.SingleAsync(message => message.Id == queueId).ConfigureAwait(false);
        Assert.AreEqual(MailQueueStates.Pending, queued.State, StringComparer.Ordinal);
        Assert.AreEqual(MailQueueScanStates.Pending, queued.ScanState, StringComparer.Ordinal);
        Assert.AreEqual(1, queued.AttemptCount);
        Assert.IsTrue(queued.NextAttemptAt > queued.ReceivedAt);
        Assert.AreEqual(RawMessage, await ReadQueueContentAsync(scope, queued).ConfigureAwait(false), StringComparer.Ordinal);
        Assert.AreEqual(0, await database.Emails.CountAsync().ConfigureAwait(false));
    }

    [TestMethod]
    public async Task WorkerQuarantinesMalwareWithoutMailboxDelivery()
    {
        var environment = CreateEnvironment();
        var scan = new MailScanResult(
            "reject",
            20,
            15,
            new HashSet<string>(["CLAM_VIRUS"], StringComparer.Ordinal),
            string.Empty,
            IsMalware: true,
            IsTemporaryFailure: false);
        var services = CreateServices(
            environment,
            scan,
            new StubRelay(OutboundDeliveryStatus.Delivered));
        await using var servicesLifetime = services.ConfigureAwait(false);
        await SeedAccountAsync(services, includeCatchAll: false).ConfigureAwait(false);
        var queueId = await EnqueueAsync(
            services,
            "sender@example.net",
            TestAccount,
            isLocal: true,
            authenticatedUser: null).ConfigureAwait(false);

        Assert.IsTrue(await ProcessOneAsync(services, environment).ConfigureAwait(false));

        using var scope = services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
        var queued = await database.MailQueueMessages
            .Include(message => message.Recipients)
            .SingleAsync(message => message.Id == queueId).ConfigureAwait(false);
        Assert.AreEqual(MailQueueStates.Quarantined, queued.State, StringComparer.Ordinal);
        Assert.AreEqual(MailQueueRecipientStates.Quarantined, queued.Recipients.Single().State, StringComparer.Ordinal);
        Assert.AreEqual(RawMessage, await ReadQueueContentAsync(scope, queued).ConfigureAwait(false), StringComparer.Ordinal);
        Assert.AreEqual(0, await database.Emails.CountAsync().ConfigureAwait(false));
    }

    [TestMethod]
    public async Task WorkerStoresSentCopyAndFailureNoticeAfterPermanentRejection()
    {
        var environment = CreateEnvironment();
        var relay = new StubRelay(OutboundDeliveryStatus.PermanentFailure);
        var services = CreateServices(environment, CleanScan("DKIM-Signature: test\r\n"), relay);
        await using var servicesLifetime = services.ConfigureAwait(false);
        await SeedAccountAsync(services, includeCatchAll: false).ConfigureAwait(false);
        var queueId = await EnqueueAsync(
            services,
            TestAccount,
            "recipient@example.net",
            isLocal: false,
            authenticatedUser: TestAccount).ConfigureAwait(false);

        Assert.IsTrue(await ProcessOneAsync(services, environment).ConfigureAwait(false));

        using var scope = services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
        var queued = await database.MailQueueMessages
            .Include(message => message.Recipients)
            .SingleAsync(message => message.Id == queueId).ConfigureAwait(false);
        Assert.AreEqual(MailQueueStates.Completed, queued.State, StringComparer.Ordinal);
        Assert.IsTrue(queued.SentCopyCreated);
        Assert.AreEqual(MailQueueRecipientStates.PermanentFailure, queued.Recipients.Single().State, StringComparer.Ordinal);
        Assert.IsTrue(queued.Recipients.Single().FailureNoticeCreated);
        Assert.AreEqual(1, relay.CallCount);

        var stored = await database.Emails.Include(message => message.Folder).ToListAsync().ConfigureAwait(false);
        Assert.AreEqual(2, stored.Count);
        CollectionAssert.AreEquivalent(
            new[] { DefaultFolders.Inbox, DefaultFolders.Sent },
            stored.Select(message => message.Folder.Name).ToArray());
        Assert.IsTrue(stored.Any(message => message.QueueDeliveryId == queueId));
        var failureNotice = stored.Single(message => string.Equals(message.Folder.Name, DefaultFolders.Inbox, StringComparison.Ordinal));
        Assert.AreNotEqual(queued.Recipients.Single().Id, failureNotice.QueueDeliveryId);
        var content = scope.ServiceProvider.GetRequiredService<MailboxMessageContentService>();
        using var parsedNotice = await MimeMessage.LoadAsync(new MemoryStream(
            await content.ReadAsync(failureNotice, CancellationToken.None).ConfigureAwait(false))).ConfigureAwait(false);
        Assert.AreEqual("Delivery Status Notification (Failure)", parsedNotice.Subject, StringComparer.Ordinal);
        var report = Assert.IsInstanceOfType<MultipartReport>(parsedNotice.Body);
        Assert.AreEqual("delivery-status", report.ContentType.Parameters["report-type"], StringComparer.Ordinal);
        var deliveryStatus = Assert.IsInstanceOfType<MessageDeliveryStatus>(report[1]);
        Assert.AreEqual("failed", deliveryStatus.StatusGroups[1]["Action"], StringComparer.Ordinal);
        Assert.AreEqual("5.0.0", deliveryStatus.StatusGroups[1]["Status"], StringComparer.Ordinal);
        Assert.AreEqual("rfc822; recipient@example.net", deliveryStatus.StatusGroups[1]["Final-Recipient"], StringComparer.Ordinal);
    }

    [TestMethod]
    public async Task WorkerSuppressesFailureNoticeWhenNotifyIsNever()
    {
        var environment = CreateEnvironment();
        var relay = new StubRelay(OutboundDeliveryStatus.PermanentFailure);
        var services = CreateServices(environment, CleanScan(), relay);
        await using var servicesLifetime = services.ConfigureAwait(false);
        await SeedAccountAsync(services, includeCatchAll: false).ConfigureAwait(false);
        var queueId = await EnqueueAsync(
            services,
            TestAccount,
            "recipient@example.net",
            isLocal: false,
            authenticatedUser: TestAccount,
            recipientDsn: new MailDsnRecipient("NEVER")).ConfigureAwait(false);

        Assert.IsTrue(await ProcessOneAsync(services, environment).ConfigureAwait(false));

        using var scope = services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
        var queued = await database.MailQueueMessages
            .Include(message => message.Recipients)
            .SingleAsync(message => message.Id == queueId).ConfigureAwait(false);
        Assert.AreEqual(MailQueueStates.Completed, queued.State, StringComparer.Ordinal);
        Assert.IsTrue(queued.Recipients.Single().FailureNoticeCreated);
        Assert.AreEqual(1, relay.CallCount);
        Assert.AreEqual(1, await database.Emails.CountAsync().ConfigureAwait(false));
        Assert.AreEqual(DefaultFolders.Sent, (await database.Emails.Include(message => message.Folder).SingleAsync().ConfigureAwait(false)).Folder.Name, StringComparer.Ordinal);
    }

    [TestMethod]
    public async Task WorkerNeverCreatesDsnForNullReversePath()
    {
        var environment = CreateEnvironment();
        var relay = new StubRelay(OutboundDeliveryStatus.PermanentFailure);
        var services = CreateServices(environment, CleanScan(), relay);
        await using var servicesLifetime = services.ConfigureAwait(false);
        var queueId = await EnqueueAsync(
            services,
            string.Empty,
            "recipient@example.net",
            isLocal: false,
            authenticatedUser: null,
            recipientDsn: new MailDsnRecipient("FAILURE")).ConfigureAwait(false);

        Assert.IsTrue(await ProcessOneAsync(services, environment).ConfigureAwait(false));

        using var scope = services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
        var queued = await database.MailQueueMessages
            .Include(message => message.Recipients)
            .SingleAsync(message => message.Id == queueId).ConfigureAwait(false);
        Assert.AreEqual(MailQueueStates.Dead, queued.State, StringComparer.Ordinal);
        Assert.IsTrue(queued.Recipients.Single().FailureNoticeCreated);
        Assert.AreEqual(1, relay.CallCount);
        Assert.AreEqual(0, await database.Emails.CountAsync().ConfigureAwait(false));
    }

    [TestMethod]
    public async Task WorkerCreatesSuccessNoticeForRequestedLocalDelivery()
    {
        var environment = CreateEnvironment();
        var services = CreateServices(
            environment,
            CleanScan(),
            new StubRelay(OutboundDeliveryStatus.Delivered));
        await using var servicesLifetime = services.ConfigureAwait(false);
        await SeedAccountAsync(services, includeCatchAll: false).ConfigureAwait(false);
        var queueId = await EnqueueAsync(
            services,
            TestAccount,
            TestAccount,
            isLocal: true,
            authenticatedUser: null,
            recipientDsn: new MailDsnRecipient("SUCCESS")).ConfigureAwait(false);

        Assert.IsTrue(await ProcessOneAsync(services, environment).ConfigureAwait(false));

        using var scope = services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
        var queued = await database.MailQueueMessages
            .Include(message => message.Recipients)
            .SingleAsync(message => message.Id == queueId).ConfigureAwait(false);
        Assert.AreEqual(MailQueueStates.Completed, queued.State, StringComparer.Ordinal);
        Assert.IsTrue(queued.Recipients.Single().SuccessNoticeCreated);
        var delivered = await database.Emails.ToListAsync().ConfigureAwait(false);
        Assert.AreEqual(2, delivered.Count);
        Assert.IsTrue(delivered.Any(message => string.Equals(message.Subject, "Delivery Status Notification (Success)", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task WorkerDoesNotDuplicateSuccessNoticeForwardedToNextHop()
    {
        var environment = CreateEnvironment();
        var relay = new StubRelay(
            OutboundDeliveryStatus.Delivered,
            dsnParametersForwarded: true,
            enhancedStatusCode: "2.0.0",
            remoteMta: "mx.example.net");
        var services = CreateServices(environment, CleanScan(), relay);
        await using var servicesLifetime = services.ConfigureAwait(false);
        await SeedAccountAsync(services, includeCatchAll: false).ConfigureAwait(false);
        var queueId = await EnqueueAsync(
            services,
            TestAccount,
            "recipient@example.net",
            isLocal: false,
            authenticatedUser: TestAccount,
            recipientDsn: new MailDsnRecipient("SUCCESS")).ConfigureAwait(false);

        Assert.IsTrue(await ProcessOneAsync(services, environment).ConfigureAwait(false));

        using var scope = services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
        var queued = await database.MailQueueMessages
            .Include(message => message.Recipients)
            .SingleAsync(message => message.Id == queueId).ConfigureAwait(false);
        var recipient = queued.Recipients.Single();
        Assert.AreEqual(MailQueueStates.Completed, queued.State, StringComparer.Ordinal);
        Assert.IsTrue(recipient.DsnForwarded);
        Assert.IsTrue(recipient.SuccessNoticeCreated);
        Assert.AreEqual("2.0.0", recipient.LastEnhancedStatusCode, StringComparer.Ordinal);
        Assert.AreEqual("mx.example.net", recipient.LastRemoteMta, StringComparer.Ordinal);
        Assert.AreEqual(1, relay.CallCount);
        Assert.AreEqual(1, await database.Emails.CountAsync().ConfigureAwait(false));
    }

    [TestMethod]
    public async Task WorkerCreatesRelayedNoticeWhenNextHopLacksDsn()
    {
        var environment = CreateEnvironment();
        var relay = new StubRelay(
            OutboundDeliveryStatus.Delivered,
            enhancedStatusCode: "2.0.0",
            remoteMta: "legacy-mx.example.net");
        var services = CreateServices(environment, CleanScan(), relay);
        await using var servicesLifetime = services.ConfigureAwait(false);
        await SeedAccountAsync(services, includeCatchAll: false).ConfigureAwait(false);
        var queueId = await EnqueueAsync(
            services,
            TestAccount,
            "recipient@example.net",
            isLocal: false,
            authenticatedUser: TestAccount,
            recipientDsn: new MailDsnRecipient("SUCCESS")).ConfigureAwait(false);

        Assert.IsTrue(await ProcessOneAsync(services, environment).ConfigureAwait(false));

        using var scope = services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
        var queued = await database.MailQueueMessages
            .Include(message => message.Recipients)
            .SingleAsync(message => message.Id == queueId).ConfigureAwait(false);
        Assert.AreEqual(MailQueueStates.Completed, queued.State, StringComparer.Ordinal);
        Assert.IsFalse(queued.Recipients.Single().DsnForwarded);
        var notice = await database.Emails.SingleAsync(message =>
            message.Subject == "Delivery Status Notification (Relayed)").ConfigureAwait(false);
        var content = scope.ServiceProvider.GetRequiredService<MailboxMessageContentService>();
        using var parsedNotice = await MimeMessage.LoadAsync(new MemoryStream(
            await content.ReadAsync(notice, CancellationToken.None).ConfigureAwait(false))).ConfigureAwait(false);
        var report = Assert.IsInstanceOfType<MultipartReport>(parsedNotice.Body);
        var deliveryStatus = Assert.IsInstanceOfType<MessageDeliveryStatus>(report[1]);
        Assert.AreEqual("relayed", deliveryStatus.StatusGroups[1]["Action"], StringComparer.Ordinal);
        Assert.AreEqual("dns; legacy-mx.example.net", deliveryStatus.StatusGroups[1]["Remote-MTA"], StringComparer.Ordinal);
        Assert.AreEqual(1, relay.CallCount);
    }

    [TestMethod]
    public async Task WorkerCreatesOneDelayNoticeAfterFourHours()
    {
        var environment = CreateEnvironment();
        var relay = new StubRelay(
            OutboundDeliveryStatus.TemporaryFailure,
            enhancedStatusCode: "4.4.1",
            remoteMta: "mx.example.net");
        var services = CreateServices(environment, CleanScan(), relay);
        await using var servicesLifetime = services.ConfigureAwait(false);
        await SeedAccountAsync(services, includeCatchAll: false).ConfigureAwait(false);
        var queueId = await EnqueueAsync(
            services,
            TestAccount,
            "recipient@example.net",
            isLocal: false,
            authenticatedUser: null,
            recipientDsn: new MailDsnRecipient("DELAY,FAILURE")).ConfigureAwait(false);
        using (var setupScope = services.CreateScope())
        {
            var setupDatabase = setupScope.ServiceProvider.GetRequiredService<EmailDbContext>();
            var queued = await setupDatabase.MailQueueMessages.SingleAsync(message => message.Id == queueId).ConfigureAwait(false);
            queued.ReceivedAt = DateTime.UtcNow.AddHours(-5);
            queued.NextAttemptAt = DateTime.UtcNow.AddMinutes(-1);
            await setupDatabase.SaveChangesAsync().ConfigureAwait(false);
        }

        Assert.IsTrue(await ProcessOneAsync(services, environment).ConfigureAwait(false));

        using var scope = services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
        var message = await database.MailQueueMessages
            .Include(item => item.Recipients)
            .SingleAsync(item => item.Id == queueId).ConfigureAwait(false);
        Assert.AreEqual(MailQueueStates.Pending, message.State, StringComparer.Ordinal);
        Assert.IsTrue(message.Recipients.Single().DelayNoticeCreated);
        var notice = await database.Emails.SingleAsync().ConfigureAwait(false);
        Assert.AreEqual("Delivery Status Notification (Delay)", notice.Subject, StringComparer.Ordinal);
        var content = scope.ServiceProvider.GetRequiredService<MailboxMessageContentService>();
        using var parsedNotice = await MimeMessage.LoadAsync(new MemoryStream(
            await content.ReadAsync(notice, CancellationToken.None).ConfigureAwait(false))).ConfigureAwait(false);
        var report = Assert.IsInstanceOfType<MultipartReport>(parsedNotice.Body);
        var deliveryStatus = Assert.IsInstanceOfType<MessageDeliveryStatus>(report[1]);
        Assert.AreEqual("delayed", deliveryStatus.StatusGroups[1]["Action"], StringComparer.Ordinal);
        Assert.AreEqual("4.4.1", deliveryStatus.StatusGroups[1]["Status"], StringComparer.Ordinal);
        Assert.AreEqual("dns; mx.example.net", deliveryStatus.StatusGroups[1]["Remote-MTA"], StringComparer.Ordinal);
    }

    [TestMethod]
    public async Task WorkerReclaimsExpiredLease()
    {
        var environment = CreateEnvironment();
        var services = CreateServices(
            environment,
            CleanScan(),
            new StubRelay(OutboundDeliveryStatus.Delivered));
        await using var servicesLifetime = services.ConfigureAwait(false);
        await SeedAccountAsync(services, includeCatchAll: false).ConfigureAwait(false);
        var queueId = await EnqueueAsync(
            services,
            "sender@example.net",
            TestAccount,
            isLocal: true,
            authenticatedUser: null).ConfigureAwait(false);

        using (var scope = services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
            var queued = await database.MailQueueMessages.SingleAsync(message => message.Id == queueId).ConfigureAwait(false);
            queued.State = MailQueueStates.Processing;
            queued.LeaseToken = Guid.CreateVersion7();
            queued.LeaseExpiresAt = DateTime.UtcNow.AddMinutes(-1);
            await database.SaveChangesAsync().ConfigureAwait(false);
        }

        Assert.IsTrue(await ProcessOneAsync(services, environment).ConfigureAwait(false));

        using var verificationScope = services.CreateScope();
        var verificationDatabase = verificationScope.ServiceProvider.GetRequiredService<EmailDbContext>();
        var completed = await verificationDatabase.MailQueueMessages.SingleAsync(message => message.Id == queueId).ConfigureAwait(false);
        Assert.AreEqual(MailQueueStates.Completed, completed.State, StringComparer.Ordinal);
        Assert.IsNull(completed.LeaseToken);
    }

    [TestMethod]
    public async Task WorkerRetainsInboundMessageWhenMailboxIsOverQuota()
    {
        var environment = CreateEnvironment(maxAttempts: 1);
        var services = CreateServices(
            environment,
            CleanScan(),
            new StubRelay(OutboundDeliveryStatus.Delivered));
        await using var servicesLifetime = services.ConfigureAwait(false);
        await SeedAccountAsync(services, includeCatchAll: false).ConfigureAwait(false);

        using (var quotaScope = services.CreateScope())
        {
            var quotaDatabase = quotaScope.ServiceProvider.GetRequiredService<EmailDbContext>();
            var user = await quotaDatabase.Users.SingleAsync(item => item.Username == TestAccount).ConfigureAwait(false);
            user.QuotaBytes = 1;
            await quotaDatabase.SaveChangesAsync().ConfigureAwait(false);

            var delivery = quotaScope.ServiceProvider.GetRequiredService<IEmailService>();
            Assert.IsTrue(await delivery.CanReceiveAsync(TestAccount).ConfigureAwait(false));
        }

        var queueId = await EnqueueAsync(
            services,
            "sender@example.net",
            TestAccount,
            isLocal: true,
            authenticatedUser: null).ConfigureAwait(false);

        Assert.IsTrue(await ProcessOneAsync(services, environment).ConfigureAwait(false));

        using var verificationScope = services.CreateScope();
        var database = verificationScope.ServiceProvider.GetRequiredService<EmailDbContext>();
        var queued = await database.MailQueueMessages
            .Include(message => message.Recipients)
            .SingleAsync(message => message.Id == queueId).ConfigureAwait(false);
        Assert.AreEqual(MailQueueStates.Dead, queued.State, StringComparer.Ordinal);
        Assert.AreEqual(MailQueueRecipientStates.PermanentFailure, queued.Recipients.Single().State, StringComparer.Ordinal);
        Assert.AreEqual(RawMessage, await ReadQueueContentAsync(verificationScope, queued).ConfigureAwait(false), StringComparer.Ordinal);
        Assert.AreEqual(0, await database.Emails.CountAsync().ConfigureAwait(false));
    }

    [TestMethod]
    public async Task DeletingQueueRecordPublishesSubmissionStatusChange()
    {
        var environment = CreateEnvironment();
        var services = CreateServices(
            environment,
            CleanScan(),
            new StubRelay(OutboundDeliveryStatus.Delivered));
        await using var servicesLifetime = services.ConfigureAwait(false);
        using var scope = services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
        await database.Database.EnsureCreatedAsync().ConfigureAwait(false);
        var accountId = Guid.CreateVersion7();
        var queueId = Guid.CreateVersion7();
        var queue = new MailQueueMessageDB
        {
            Id = queueId,
            EnvelopeSender = TestAccount,
            RawMessage = RawMessage,
            AuthenticatedUser = TestAccount,
            Direction = MailQueueDirections.Submission,
            State = MailQueueStates.Completed,
            ScanState = MailQueueScanStates.Complete,
            ReceivedAt = DateTime.UtcNow,
            NextAttemptAt = DateTime.UtcNow,
            CompletedAt = DateTime.UtcNow,
        };
        queue.Recipients.Add(new MailQueueRecipientDB
        {
            Id = Guid.CreateVersion7(),
            Recipient = "recipient@example.net",
            State = MailQueueRecipientStates.Delivered,
            NextAttemptAt = DateTime.UtcNow,
            CompletedAt = DateTime.UtcNow,
        });
        var submissionId = Guid.CreateVersion7();
        await database.MailQueueMessages.AddAsync(queue).ConfigureAwait(false);
        await database.JmapEmailSubmissions.AddAsync(new JmapEmailSubmissionDB
        {
            Id = submissionId,
            SubmissionObjectId = JmapId.Submission(submissionId),
            AccountId = accountId,
            IdentityId = JmapId.Identity(Guid.CreateVersion7()),
            EmailId = JmapId.Email(Guid.CreateVersion7()),
            ThreadId = JmapId.Thread(Guid.CreateVersion7().ToString("N")),
            QueueId = queueId,
            EnvelopeSender = TestAccount,
            EnvelopeRecipients = ["recipient@example.net"],
            UndoStatus = "final",
        }).ConfigureAwait(false);
        await database.SaveChangesAsync().ConfigureAwait(false);

        database.MailQueueMessages.Remove(queue);
        await database.SaveChangesAsync().ConfigureAwait(false);

        var changes = await database.JmapChanges
            .Where(change => change.AccountId == accountId
                && change.DataType == "EmailSubmission"
                && change.ObjectId == JmapId.Submission(submissionId))
            .Select(change => change.ChangeKind)
            .ToListAsync().ConfigureAwait(false);
        CollectionAssert.AreEquivalent(ExpectedVector1, changes);
    }

    [TestMethod]
    public async Task SieveFileIntoCreatesFolderAndAppliesFlags()
    {
        var environment = CreateEnvironment();
        var services = CreateServices(
            environment,
            CleanScan(),
            new StubRelay(OutboundDeliveryStatus.Delivered));
        await using var servicesLifetime = services.ConfigureAwait(false);
        await SeedAccountAsync(services, includeCatchAll: false).ConfigureAwait(false);
        await ActivateScriptAsync(
            services,
            TestAccount,
            """
            require ["fileinto", "mailbox", "imap4flags"];
            fileinto :create :flags ["\\Seen", "\\Flagged", "project"] "Projects/MK8";
            """).ConfigureAwait(false);
        var queueId = await EnqueueAsync(
            services,
            "sender@example.net",
            TestAccount,
            isLocal: true,
            authenticatedUser: null).ConfigureAwait(false);

        Assert.IsTrue(await ProcessOneAsync(services, environment).ConfigureAwait(false));

        using var scope = services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
        var queued = await database.MailQueueMessages
            .Include(message => message.Recipients)
            .SingleAsync(message => message.Id == queueId).ConfigureAwait(false);
        Assert.AreEqual(MailQueueStates.Completed, queued.State, StringComparer.Ordinal);
        var delivered = await database.Emails.Include(message => message.Folder).SingleAsync().ConfigureAwait(false);
        Assert.AreEqual("Projects/MK8", delivered.Folder.Name, StringComparer.Ordinal);
        Assert.IsTrue(delivered.IsRead);
        Assert.IsTrue(delivered.IsFlagged);
        CollectionAssert.AreEqual(ExpectedVector2, delivered.Keywords);
        Assert.AreNotEqual(queued.Recipients.Single().Id, delivered.QueueDeliveryId);
    }

    [TestMethod]
    public async Task SieveDiscardCompletesWithoutMailboxDelivery()
    {
        var environment = CreateEnvironment();
        var services = CreateServices(
            environment,
            CleanScan(),
            new StubRelay(OutboundDeliveryStatus.Delivered));
        await using var servicesLifetime = services.ConfigureAwait(false);
        await SeedAccountAsync(services, includeCatchAll: false).ConfigureAwait(false);
        await ActivateScriptAsync(services, TestAccount, "discard;").ConfigureAwait(false);
        var queueId = await EnqueueAsync(
            services,
            "sender@example.net",
            TestAccount,
            isLocal: true,
            authenticatedUser: null).ConfigureAwait(false);

        Assert.IsTrue(await ProcessOneAsync(services, environment).ConfigureAwait(false));

        using var scope = services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
        Assert.AreEqual(
            MailQueueStates.Completed,
            (await database.MailQueueMessages.SingleAsync(message => message.Id == queueId).ConfigureAwait(false)).State, StringComparer.Ordinal);
        Assert.AreEqual(0, await database.Emails.CountAsync().ConfigureAwait(false));
    }

    [TestMethod]
    public async Task MissingSieveFileIntoMailboxFallsBackToInbox()
    {
        var environment = CreateEnvironment();
        var services = CreateServices(
            environment,
            CleanScan(),
            new StubRelay(OutboundDeliveryStatus.Delivered));
        await using var servicesLifetime = services.ConfigureAwait(false);
        await SeedAccountAsync(services, includeCatchAll: false).ConfigureAwait(false);
        await ActivateScriptAsync(
            services,
            TestAccount,
            "require \"fileinto\"; fileinto \"Missing\";").ConfigureAwait(false);
        await EnqueueAsync(
            services,
            "sender@example.net",
            TestAccount,
            isLocal: true,
            authenticatedUser: null).ConfigureAwait(false);

        Assert.IsTrue(await ProcessOneAsync(services, environment).ConfigureAwait(false));

        using var scope = services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
        var delivered = await database.Emails.Include(message => message.Folder).SingleAsync().ConfigureAwait(false);
        Assert.AreEqual(DefaultFolders.Inbox, delivered.Folder.Name, StringComparer.Ordinal);
    }

    [TestMethod]
    public async Task CatchAllDeliveryUsesOwningUsersActiveSieveScript()
    {
        var environment = CreateEnvironment();
        var services = CreateServices(
            environment,
            CleanScan(),
            new StubRelay(OutboundDeliveryStatus.Delivered));
        await using var servicesLifetime = services.ConfigureAwait(false);
        await SeedAccountAsync(services, includeCatchAll: true).ConfigureAwait(false);
        await ActivateScriptAsync(services, TestAccount, "discard;").ConfigureAwait(false);
        await EnqueueAsync(
            services,
            "sender@example.net",
            "undefined@mk8n.com",
            isLocal: true,
            authenticatedUser: null).ConfigureAwait(false);

        Assert.IsTrue(await ProcessOneAsync(services, environment).ConfigureAwait(false));

        using var scope = services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
        Assert.AreEqual(0, await database.Emails.CountAsync().ConfigureAwait(false));
        Assert.AreEqual(MailQueueStates.Completed, (await database.MailQueueMessages.SingleAsync().ConfigureAwait(false)).State, StringComparer.Ordinal);
    }

    [TestMethod]
    public async Task SieveRedirectAddsDurableRecipientForNextQueuePass()
    {
        var environment = CreateEnvironment();
        var relay = new StubRelay(OutboundDeliveryStatus.Delivered);
        var services = CreateServices(environment, CleanScan(), relay);
        await using var servicesLifetime = services.ConfigureAwait(false);
        await SeedAccountAsync(services, includeCatchAll: false).ConfigureAwait(false);
        await ActivateScriptAsync(
            services,
            TestAccount,
            "redirect \"archive@example.org\";").ConfigureAwait(false);
        var queueId = await EnqueueAsync(
            services,
            "sender@example.net",
            TestAccount,
            isLocal: true,
            authenticatedUser: null).ConfigureAwait(false);

        Assert.IsTrue(await ProcessOneAsync(services, environment).ConfigureAwait(false));
        Assert.AreEqual(0, relay.CallCount);
        Assert.IsTrue(await ProcessOneAsync(services, environment).ConfigureAwait(false));

        using var scope = services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
        var queued = await database.MailQueueMessages
            .Include(message => message.Recipients)
            .SingleAsync(message => message.Id == queueId).ConfigureAwait(false);
        Assert.AreEqual(
            MailQueueStates.Completed,
            queued.State,
StringComparer.Ordinal, $"message={queued.AttemptCount}:{queued.NextAttemptAt:o}:{queued.LastError};" +
            string.Join(';', queued.Recipients.Select(item =>
                $"{item.Recipient}:{item.State}:{item.AttemptCount}:{item.NextAttemptAt:o}:{item.LastError}")));
        Assert.AreEqual(2, queued.Recipients.Count);
        var redirected = queued.Recipients.Single(item => string.Equals(item.Recipient, "archive@example.org", StringComparison.Ordinal));
        Assert.AreEqual(1, redirected.RedirectDepth);
        CollectionAssert.AreEquivalent(
            new[] { TestAccount, "archive@example.org" },
            redirected.RedirectHistory);
        Assert.AreEqual(1, relay.CallCount);
        Assert.AreEqual(0, await database.Emails.CountAsync().ConfigureAwait(false));
    }

    [TestMethod]
    public async Task SieveExpansionIssuesOneSuccessDsnAndPropagatesFailureOnly()
    {
        var environment = CreateEnvironment();
        var relay = new StubRelay(OutboundDeliveryStatus.Delivered);
        var services = CreateServices(environment, CleanScan(), relay);
        await using var servicesLifetime = services.ConfigureAwait(false);
        await SeedAccountAsync(services, includeCatchAll: false).ConfigureAwait(false);
        await ActivateScriptAsync(
            services,
            TestAccount,
            "redirect \"archive@example.org\";").ConfigureAwait(false);
        var queueId = await EnqueueAsync(
            services,
            TestAccount,
            TestAccount,
            isLocal: true,
            authenticatedUser: null,
            recipientDsn: new MailDsnRecipient("SUCCESS,FAILURE")).ConfigureAwait(false);

        Assert.IsTrue(await ProcessOneAsync(services, environment).ConfigureAwait(false));
        Assert.IsTrue(await ProcessOneAsync(services, environment).ConfigureAwait(false));

        using var scope = services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
        var queued = await database.MailQueueMessages
            .Include(message => message.Recipients)
            .SingleAsync(message => message.Id == queueId).ConfigureAwait(false);
        Assert.AreEqual(MailQueueStates.Completed, queued.State, StringComparer.Ordinal);
        var redirect = queued.Recipients.Single(recipient => string.Equals(recipient.Recipient, "archive@example.org", StringComparison.Ordinal));
        Assert.AreEqual("FAILURE", redirect.DsnNotify, StringComparer.Ordinal);
        Assert.AreEqual("FAILURE", relay.LastOptions!.RecipientDsn!.Notify, StringComparer.Ordinal);
        var notices = await database.Emails
            .Where(message => message.Subject == "Delivery Status Notification (Expanded)")
            .ToListAsync().ConfigureAwait(false);
        Assert.HasCount(1, notices);
        var content = scope.ServiceProvider.GetRequiredService<MailboxMessageContentService>();
        using var parsedNotice = await MimeMessage.LoadAsync(new MemoryStream(
            await content.ReadAsync(notices[0], CancellationToken.None).ConfigureAwait(false))).ConfigureAwait(false);
        var report = Assert.IsInstanceOfType<MultipartReport>(parsedNotice.Body);
        var deliveryStatus = Assert.IsInstanceOfType<MessageDeliveryStatus>(report[1]);
        Assert.AreEqual("expanded", deliveryStatus.StatusGroups[1]["Action"], StringComparer.Ordinal);
    }

    [TestMethod]
    public async Task SieveRedirectLoopFallsBackToKeepAtLastRecipient()
    {
        const string secondAccount = "second@mk8n.com";
        var environment = CreateEnvironment();
        var relay = new StubRelay(OutboundDeliveryStatus.Delivered);
        var services = CreateServices(environment, CleanScan(), relay);
        await using var servicesLifetime = services.ConfigureAwait(false);
        await SeedAccountAsync(services, includeCatchAll: false).ConfigureAwait(false);
        using (var setupScope = services.CreateScope())
        {
            var setupDatabase = setupScope.ServiceProvider.GetRequiredService<EmailDbContext>();
            var administration = new MailAdministrationService(setupDatabase);
            Assert.IsTrue((await administration.CreateAccountAsync(
                secondAccount,
                "second-account-password-value",
                UserRole.User).ConfigureAwait(false)).Succeeded);
        }
        await ActivateScriptAsync(services, TestAccount, $"redirect \"{secondAccount}\";").ConfigureAwait(false);
        await ActivateScriptAsync(services, secondAccount, $"redirect \"{TestAccount}\";").ConfigureAwait(false);
        var queueId = await EnqueueAsync(
            services,
            "sender@example.net",
            TestAccount,
            isLocal: true,
            authenticatedUser: null).ConfigureAwait(false);

        Assert.IsTrue(await ProcessOneAsync(services, environment).ConfigureAwait(false));
        Assert.IsTrue(await ProcessOneAsync(services, environment).ConfigureAwait(false));

        using var scope = services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
        var queueMessage = await database.MailQueueMessages.SingleAsync(message => message.Id == queueId).ConfigureAwait(false);
        Assert.AreEqual(
            MailQueueStates.Completed,
            queueMessage.State,
StringComparer.Ordinal, $"message={queueMessage.AttemptCount}:{queueMessage.NextAttemptAt:o}:{queueMessage.LastError};" +
            string.Join(';', (await database.MailQueueRecipients
                .Where(item => item.MessageId == queueId)
                .ToListAsync().ConfigureAwait(false)).Select(item =>
                $"{item.Recipient}:{item.State}:{item.AttemptCount}:{item.NextAttemptAt:o}:{item.LastError}")));
        var delivered = await database.Emails.Include(message => message.Folder).SingleAsync().ConfigureAwait(false);
        Assert.AreEqual(secondAccount, delivered.Recipient, StringComparer.Ordinal);
        Assert.AreEqual(DefaultFolders.Inbox, delivered.Folder.Name, StringComparer.Ordinal);
        Assert.AreEqual(0, relay.CallCount);
    }

    [TestMethod]
    public async Task SieveRejectNotifiesEnvelopeSenderWithoutStoringOriginal()
    {
        var environment = CreateEnvironment();
        var relay = new StubRelay(OutboundDeliveryStatus.Delivered);
        var services = CreateServices(environment, CleanScan(), relay);
        await using var servicesLifetime = services.ConfigureAwait(false);
        await SeedAccountAsync(services, includeCatchAll: false).ConfigureAwait(false);
        await ActivateScriptAsync(
            services,
            TestAccount,
            "require \"reject\"; reject \"This mailbox does not accept automated reports.\";").ConfigureAwait(false);
        var queueId = await EnqueueAsync(
            services,
            "sender@example.net",
            TestAccount,
            isLocal: true,
            authenticatedUser: null).ConfigureAwait(false);

        Assert.IsTrue(await ProcessOneAsync(services, environment).ConfigureAwait(false));

        using var scope = services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
        Assert.AreEqual(
            MailQueueStates.Completed,
            (await database.MailQueueMessages.SingleAsync(message => message.Id == queueId).ConfigureAwait(false)).State, StringComparer.Ordinal);
        Assert.AreEqual(0, await database.Emails.CountAsync().ConfigureAwait(false));
        Assert.AreEqual(1, relay.CallCount);
        Assert.AreEqual(string.Empty, relay.LastSender, StringComparer.Ordinal);
        Assert.AreEqual("sender@example.net", relay.LastRecipient, StringComparer.Ordinal);
        Assert.AreEqual("NEVER", relay.LastOptions!.RecipientDsn!.Notify, StringComparer.Ordinal);
        StringAssert.Contains(relay.LastRawMessage!, "This mailbox does not accept automated reports.", StringComparison.Ordinal);
        StringAssert.Contains(relay.LastRawMessage!, "Auto-Submitted: auto-replied", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task WorkerRetainsCompletedScanWithoutCallingScannerAgain()
    {
        var environment = CreateEnvironment();
        var relay = new StubRelay(OutboundDeliveryStatus.Delivered);
        var services = CreateServices(environment, CleanScan() with { IsTemporaryFailure = true }, relay);
        await using var servicesLifetime = services.ConfigureAwait(false);
        await SeedAccountAsync(services, includeCatchAll: false).ConfigureAwait(false);
        var queueId = await EnqueueAsync(services, "sender@example.net", TestAccount, isLocal: true, authenticatedUser: null).ConfigureAwait(false);
        using (var setup = services.CreateScope())
        {
            var database = setup.ServiceProvider.GetRequiredService<EmailDbContext>();
            var queued = await database.MailQueueMessages.SingleAsync(message => message.Id == queueId).ConfigureAwait(false);
            queued.ScanState = MailQueueScanStates.Complete;
            queued.ScanAction = "no action";
            queued.AddedHeaders = "X-Retained-Scan: yes\r\n";
            queued.TargetFolder = DefaultFolders.Inbox;
            await database.SaveChangesAsync().ConfigureAwait(false);
        }
        Assert.IsTrue(await ProcessOneAsync(services, environment).ConfigureAwait(false));
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
        var message = await db.MailQueueMessages.Include(item => item.Recipients).SingleAsync(item => item.Id == queueId).ConfigureAwait(false);
        Assert.AreEqual(MailQueueStates.Completed, message.State, StringComparer.Ordinal);
        Assert.AreEqual(MailQueueRecipientStates.Delivered, message.Recipients.Single().State, StringComparer.Ordinal);
        Assert.AreEqual(0, ((StubScanner)scope.ServiceProvider.GetRequiredService<IMailScanner>()).CallCount);
        var stored = await db.Emails.SingleAsync().ConfigureAwait(false);
        StringAssert.Contains(stored.RawHeaders!, "X-Retained-Scan: yes", StringComparison.Ordinal);
        Assert.AreEqual(0, relay.CallCount);
    }

    [TestMethod]
    [DataRow("add header")]
    [DataRow("rewrite subject")]
    [DataRow("reject")]
    [DataRow("discard")]
    public async Task WorkerQuarantinesSpamSubmissionBeforeSentCopyOrRelay(string action)
    {
        var environment = CreateEnvironment();
        var relay = new StubRelay(OutboundDeliveryStatus.Delivered);
        var services = CreateServices(environment, CleanScan("X-Spam: yes\r\n") with { Action = action, Score = 20 }, relay);
        await using var servicesLifetime = services.ConfigureAwait(false);
        await SeedAccountAsync(services, includeCatchAll: false).ConfigureAwait(false);
        var queueId = await EnqueueAsync(services, TestAccount, "recipient@example.net", isLocal: false, authenticatedUser: TestAccount).ConfigureAwait(false);
        Assert.IsTrue(await ProcessOneAsync(services, environment).ConfigureAwait(false));
        using var scope = services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
        var queued = await database.MailQueueMessages.Include(item => item.Recipients).SingleAsync(item => item.Id == queueId).ConfigureAwait(false);
        Assert.AreEqual(MailQueueStates.Quarantined, queued.State, StringComparer.Ordinal);
        Assert.AreEqual(MailQueueScanStates.Complete, queued.ScanState, StringComparer.Ordinal);
        Assert.AreEqual(action, queued.ScanAction, StringComparer.Ordinal);
        Assert.AreEqual(20d, queued.ScanScore);
        Assert.AreEqual(DefaultFolders.Spam, queued.TargetFolder, StringComparer.Ordinal);
        Assert.IsFalse(queued.SentCopyCreated);
        Assert.AreEqual(MailQueueRecipientStates.Quarantined, queued.Recipients.Single().State, StringComparer.Ordinal);
        Assert.AreEqual(0, queued.Recipients.Single().AttemptCount);
        Assert.AreEqual(0, relay.CallCount);
        var notice = await database.Emails.Include(item => item.Folder).SingleAsync().ConfigureAwait(false);
        Assert.AreEqual("Message quarantined", notice.Subject, StringComparer.Ordinal);
        Assert.AreEqual(DefaultFolders.Inbox, notice.Folder.Name, StringComparer.Ordinal);
        Assert.AreEqual(RawMessage, await ReadQueueContentAsync(scope, queued).ConfigureAwait(false), StringComparer.Ordinal);
    }

    [TestMethod]
    public async Task WorkerRetriesUnavailableSentCopyBeforeAttemptingRecipients()
    {
        var environment = CreateEnvironment();
        var relay = new StubRelay(OutboundDeliveryStatus.Delivered);
        var services = CreateServices(environment, CleanScan(), relay);
        await using var servicesLifetime = services.ConfigureAwait(false);
        await SeedAccountAsync(services, includeCatchAll: false).ConfigureAwait(false);
        using (var setup = services.CreateScope())
        {
            var database = setup.ServiceProvider.GetRequiredService<EmailDbContext>();
            var user = await database.Users.SingleAsync().ConfigureAwait(false);
            user.QuotaBytes = 1;
            await database.SaveChangesAsync().ConfigureAwait(false);
        }
        var queueId = await EnqueueAsync(services, TestAccount, "recipient@example.net", isLocal: false, authenticatedUser: TestAccount).ConfigureAwait(false);
        Assert.IsTrue(await ProcessOneAsync(services, environment).ConfigureAwait(false));
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
        var queued = await db.MailQueueMessages.Include(item => item.Recipients).SingleAsync(item => item.Id == queueId).ConfigureAwait(false);
        Assert.AreEqual(MailQueueStates.Pending, queued.State, StringComparer.Ordinal);
        Assert.AreEqual(MailQueueScanStates.Complete, queued.ScanState, StringComparer.Ordinal);
        Assert.IsFalse(queued.SentCopyCreated);
        Assert.AreEqual("The sent copy could not be stored.", queued.LastError, StringComparer.Ordinal);
        Assert.IsNull(queued.LeaseToken);
        Assert.IsNull(queued.LeaseExpiresAt);
        Assert.AreEqual(1, queued.AttemptCount);
        Assert.AreEqual(0, queued.Recipients.Single().AttemptCount);
        Assert.AreEqual(MailQueueRecipientStates.Pending, queued.Recipients.Single().State, StringComparer.Ordinal);
        Assert.IsTrue(queued.NextAttemptAt > queued.ReceivedAt);
        Assert.AreEqual(0, relay.CallCount);
        Assert.AreEqual(0, await db.Emails.CountAsync().ConfigureAwait(false));
    }

    [TestMethod]
    public async Task RecipientRelayExceptionDoesNotPreventOtherDueRecipientDelivery()
    {
        var environment = CreateEnvironment();
        var relay = new SelectivelyFailingRelay();
        var services = CreateServices(environment, CleanScan(), relay);
        await using var servicesLifetime = services.ConfigureAwait(false);
        var queueId = Guid.CreateVersion7();
        using (var setup = services.CreateScope())
        {
            var queue = setup.ServiceProvider.GetRequiredService<IMailSubmissionQueue>();
            await queue.EnqueueAsync(new MailSubmission(queueId, string.Empty,
                [new MailEnvelopeRecipient("failed@example.net", false), new MailEnvelopeRecipient("delivered@example.net", false)],
                RawMessage, null, null, null)).ConfigureAwait(false);
        }
        Assert.IsTrue(await ProcessOneAsync(services, environment).ConfigureAwait(false));
        using var scope = services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
        var queued = await database.MailQueueMessages.Include(item => item.Recipients).SingleAsync(item => item.Id == queueId).ConfigureAwait(false);
        var failed = queued.Recipients.Single(item => string.Equals(item.Recipient, "failed@example.net", StringComparison.Ordinal));
        var delivered = queued.Recipients.Single(item => string.Equals(item.Recipient, "delivered@example.net", StringComparison.Ordinal));
        Assert.AreEqual(MailQueueStates.Pending, queued.State, StringComparer.Ordinal);
        Assert.AreEqual(MailQueueRecipientStates.Pending, failed.State, StringComparer.Ordinal);
        Assert.AreEqual("temporary relay failure", failed.LastError, StringComparer.Ordinal);
        Assert.AreEqual(1, failed.AttemptCount);
        Assert.AreEqual(MailQueueRecipientStates.Delivered, delivered.State, StringComparer.Ordinal);
        Assert.AreEqual(1, delivered.AttemptCount);
        Assert.IsTrue(delivered.SuccessNoticeCreated);
        Assert.IsNull(queued.LeaseToken);
        Assert.AreEqual(failed.NextAttemptAt, queued.NextAttemptAt);
        Assert.AreEqual(2, relay.CallCount);
        Assert.AreEqual(1, ((StubScanner)scope.ServiceProvider.GetRequiredService<IMailScanner>()).CallCount);
        Assert.AreEqual(0, await database.Emails.CountAsync().ConfigureAwait(false));
    }

    private sealed class SelectivelyFailingRelay : IOutboundMailRelay
    {
        public int CallCount { get; private set; }

        public Task<OutboundDeliveryResult> RelayAsync(string sender, string recipient, string rawMessage,
            OutboundMailOptions? options = null, CancellationToken cancellationToken = default)
        {
            CallCount++;
            if (string.Equals(recipient, "failed@example.net", StringComparison.Ordinal))
                throw new IOException("temporary\r\nrelay\0failure");
            return Task.FromResult(new OutboundDeliveryResult(OutboundDeliveryStatus.Delivered, "delivered"));
        }
    }

    private static ServiceProvider CreateServices(
        EnvironmentConfig environment,
        MailScanResult scanResult,
        IOutboundMailRelay relay)
    {
        var databaseName = $"mail-queue-{Guid.NewGuid():N}";
        var services = new ServiceCollection();
        services.AddSingleton(environment);
        services.AddDbContext<EmailDbContext>(options =>
            options.UseInMemoryDatabase(databaseName)
                .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning)));
        services.AddScoped<IEmailService, EmailService>();
        services.AddScoped<ISieveScriptService, SieveScriptService>();
        services.AddScoped<ISieveFilterService, SieveFilterService>();
        services.AddScoped<IMailSubmissionQueue, PostgresMailSubmissionQueue>();
        services.AddSingleton<InMemoryLargeObjectStore>();
        services.AddSingleton<ILargeObjectStore>(provider =>
            provider.GetRequiredService<InMemoryLargeObjectStore>());
        services.AddScoped<LargeObjectTransactionEffects>();
        services.AddScoped<MailQueueContentService>();
        services.AddScoped<MailQueueLargeObjectMigrationService>();
        services.AddScoped<MailboxMessageContentService>();
        services.AddScoped<MailboxMessageLargeObjectMigrationService>();
        services.AddScoped<SieveScriptContentService>();
        services.AddLogging();
        services.AddSingleton<IMailScanner>(new StubScanner(scanResult));
        services.AddSingleton(relay);
        return services.BuildServiceProvider();
    }

    private static async Task SeedAccountAsync(ServiceProvider services, bool includeCatchAll)
    {
        using var scope = services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
        await database.Database.EnsureCreatedAsync().ConfigureAwait(false);
        var administration = new MailAdministrationService(database);
        Assert.IsTrue((await administration.EnsureDomainAsync("mk8n", TestDomain).ConfigureAwait(false)).Succeeded);
        Assert.IsTrue((await administration.CreateAccountAsync(
            TestAccount,
            "test-account-password-value",
            UserRole.SuperAdmin).ConfigureAwait(false)).Succeeded);
        if (includeCatchAll)
        {
            Assert.IsTrue((await administration.SetCatchAllAsync(TestDomain, TestAccount).ConfigureAwait(false)).Succeeded);
        }
        Assert.IsTrue((await administration.SetDomainActiveAsync(TestDomain, true).ConfigureAwait(false)).Succeeded);
    }

    private static async Task<Guid> EnqueueAsync(
        ServiceProvider services,
        string sender,
        string recipient,
        bool isLocal,
        string? authenticatedUser,
        MailDsnEnvelope? dsn = null,
        MailDsnRecipient? recipientDsn = null)
    {
        using var scope = services.CreateScope();
        var queue = scope.ServiceProvider.GetRequiredService<IMailSubmissionQueue>();
        var queueId = Guid.CreateVersion7();
        await queue.EnqueueAsync(new MailSubmission(
            queueId,
            sender,
            [new MailEnvelopeRecipient(recipient, isLocal, recipientDsn)],
            RawMessage,
            "192.0.2.10",
            "sender.example.net",
            authenticatedUser,
            Dsn: dsn)).ConfigureAwait(false);
        return queueId;
    }

    private static Task<string> ReadQueueContentAsync(
        IServiceScope scope,
        MailQueueMessageDB message) =>
        scope.ServiceProvider.GetRequiredService<MailQueueContentService>()
            .ReadAsync(message);

    private static async Task ActivateScriptAsync(
        ServiceProvider services,
        string username,
        string content)
    {
        using var scope = services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
        var userId = await database.Users
            .Where(user => user.Username == username)
            .Select(user => user.Id)
            .SingleAsync().ConfigureAwait(false);
        var scripts = scope.ServiceProvider.GetRequiredService<ISieveScriptService>();
        var put = await scripts.PutAsync(userId, "active", content).ConfigureAwait(false);
        Assert.IsTrue(put.Succeeded, put.Error);
        var active = await scripts.SetActiveAsync(userId, "active").ConfigureAwait(false);
        Assert.IsTrue(active.Succeeded, active.Error);
    }

    private static async Task<bool> ProcessOneAsync(
        ServiceProvider services,
        EnvironmentConfig environment)
    {
        var logger = new CapturingQueueLogger();
        using var worker = new MailQueueWorker(
                    services.GetRequiredService<IServiceScopeFactory>(),
                    environment,
                    TimeProvider.System,
                    logger);
        var processed = await worker.ProcessNextAsync(CancellationToken.None).ConfigureAwait(false);
        if (logger.Exception is not null)
            Assert.Fail(logger.Exception.ToString());
        return processed;
    }

    private static EnvironmentConfig CreateEnvironment(int maxAttempts = 5) => new()
    {
        Smtp = new SmtpConfig { Hostname = "email.mk8n.com" },
        Limits = new LimitsConfig
        {
            MaxMessageSizeBytes = 1024 * 1024,
            MaxRecipientsPerMessage = 10,
            ConnectionTimeoutSeconds = 10,
            MaxConnectionsPerIp = 10,
        },
        Queue = new QueueConfig
        {
            PollIntervalMilliseconds = 100,
            LeaseSeconds = 60,
            MaxAttempts = maxAttempts,
            MaxAgeHours = 24,
            CompletedRetentionDays = 7,
        },
    };

    private static MailScanResult CleanScan(string headers = "") => new(
        "no action",
        0,
        15,
        new HashSet<string>(StringComparer.Ordinal),
        headers,
        IsMalware: false,
        IsTemporaryFailure: false);

    private sealed class StubScanner(MailScanResult result) : IMailScanner
    {
        public int CallCount { get; private set; }

        public Task<MailScanResult> ScanAsync(
            MailScanRequest request,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(result);
        }
    }

    private sealed class CapturingQueueLogger : ILogger<MailQueueWorker>
    {
        public Exception? Exception { get; private set; }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Error && exception is not null)
                Exception = exception;
        }
    }

    private sealed class StubRelay(
        OutboundDeliveryStatus status,
        bool dsnParametersForwarded = false,
        string? enhancedStatusCode = null,
        string? remoteMta = null) : IOutboundMailRelay
    {
        public int CallCount { get; private set; }
        public string? LastSender { get; private set; }
        public string? LastRecipient { get; private set; }
        public string? LastRawMessage { get; private set; }
        public OutboundMailOptions? LastOptions { get; private set; }

        public Task<OutboundDeliveryResult> RelayAsync(
            string sender,
            string recipient,
            string rawMessage,
            OutboundMailOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastSender = sender;
            LastRecipient = recipient;
            LastRawMessage = rawMessage;
            LastOptions = options;
            return Task.FromResult(new OutboundDeliveryResult(
                status,
                "Test delivery result.",
                dsnParametersForwarded,
                remoteMta,
                enhancedStatusCode));
        }
    }
    private static readonly string[] ExpectedVector1 = new[] { "created", "updated" };
    private static readonly string[] ExpectedVector2 = new[] { "project" };
}
