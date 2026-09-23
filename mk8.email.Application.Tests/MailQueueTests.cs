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
public sealed class MailQueueTests
{
    private const string TestDomain = "mk8n.com";
    private const string TestAccount = "admin@mk8n.com";
    private const string RawMessage =
        "From: sender@example.net\r\n" +
        "To: admin@mk8n.com\r\n" +
        "Subject: queue test\r\n\r\n" +
        "body\r\n";

    [TestMethod]
    public async Task CompletedQueueCleanupDrainsEveryExpiredBatch()
    {
        var environment = CreateEnvironment();
        await using var services = CreateServices(
            environment, CleanScan(), new StubRelay(OutboundDeliveryStatus.Delivered));
        using (var scope = services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
            database.MailQueueMessages.AddRange(Enumerable.Range(0, 1001).Select(_ =>
                new MailQueueMessageDB
                {
                    Id = Guid.CreateVersion7(),
                    EnvelopeSender = "retention@example.test",
                    Direction = MailQueueDirections.Inbound,
                    State = MailQueueStates.Completed,
                    ScanState = MailQueueScanStates.Complete,
                    CompletedAt = DateTime.UtcNow.AddDays(-8),
                }));
            await database.SaveChangesAsync();
        }

        var worker = new MailQueueWorker(
            services.GetRequiredService<IServiceScopeFactory>(),
            environment,
            TimeProvider.System,
            new CapturingQueueLogger());
        await worker.CleanupCompletedAsync(CancellationToken.None);

        using var verification = services.CreateScope();
        Assert.AreEqual(0, await verification.ServiceProvider
            .GetRequiredService<EmailDbContext>().MailQueueMessages.CountAsync());
    }

    [TestMethod]
    public async Task SubmissionQueuePersistsRawMessageAndDistinctRecipients()
    {
        var environment = CreateEnvironment();
        await using var services = CreateServices(
            environment,
            CleanScan(),
            new StubRelay(OutboundDeliveryStatus.Delivered));
        var queueId = Guid.CreateVersion7();

        using (var scope = services.CreateScope())
        {
            var initializationDatabase = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
            await initializationDatabase.Database.EnsureCreatedAsync();
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
                Dsn: new MailDsnEnvelope("hdrs", "queue+2Btest")));
        }

        using var verificationScope = services.CreateScope();
        var database = verificationScope.ServiceProvider.GetRequiredService<EmailDbContext>();
        var queued = await database.MailQueueMessages
            .Include(message => message.Recipients)
            .SingleAsync();
        Assert.AreEqual(queueId, queued.Id);
        Assert.IsNull(queued.RawMessage);
        Assert.AreEqual(RawMessage, await ReadQueueContentAsync(verificationScope, queued));
        Assert.AreEqual(MailQueueStates.Pending, queued.State);
        Assert.AreEqual(MailQueueDirections.Inbound, queued.Direction);
        Assert.AreEqual(1, queued.Recipients.Count);
        Assert.AreEqual(TestAccount, queued.Recipients.Single().Recipient);
        Assert.IsFalse(queued.RequiresSmtpUtf8);
        Assert.AreEqual("HDRS", queued.DsnReturnContent);
        Assert.AreEqual("queue+2Btest", queued.DsnEnvelopeId);
        Assert.AreEqual("FAILURE,DELAY", queued.Recipients.Single().DsnNotify);
        Assert.AreEqual(
            "rfc822;admin+40mk8n.com",
            queued.Recipients.Single().DsnOriginalRecipient);
    }

    [TestMethod]
    public async Task QueuePersistsAndRelaysSmtpUtf8Requirement()
    {
        var environment = CreateEnvironment();
        var relay = new StubRelay(OutboundDeliveryStatus.Delivered);
        await using var services = CreateServices(environment, CleanScan(), relay);
        var queueId = Guid.CreateVersion7();
        var rawMessage = Encoding.Latin1.GetString(Encoding.UTF8.GetBytes(
            "From: josé@example.net\r\n" +
            "To: recipient@example.com\r\n" +
            "Subject: Žuta pošta\r\n\r\n" +
            "body\r\n"));

        using (var scope = services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
            await database.Database.EnsureCreatedAsync();
            var queue = scope.ServiceProvider.GetRequiredService<IMailSubmissionQueue>();
            await queue.EnqueueAsync(new MailSubmission(
                queueId,
                "josé@example.net",
                [new MailEnvelopeRecipient("recipient@example.com", false)],
                rawMessage,
                "192.0.2.10",
                "sender.example.net",
                null,
                RequiresSmtpUtf8: true));
        }

        Assert.IsTrue(await ProcessOneAsync(services, environment));

        using var verificationScope = services.CreateScope();
        var verificationDatabase = verificationScope.ServiceProvider.GetRequiredService<EmailDbContext>();
        var queued = await verificationDatabase.MailQueueMessages.SingleAsync(
            message => message.Id == queueId);
        Assert.IsTrue(queued.RequiresSmtpUtf8);
        Assert.IsNotNull(relay.LastOptions);
        Assert.IsTrue(relay.LastOptions.RequiresSmtpUtf8);
    }

    [TestMethod]
    public async Task QueueDecodesSmtpUtf8HeadersAndBodyForLocalMailboxMetadata()
    {
        var environment = CreateEnvironment();
        await using var services = CreateServices(
            environment,
            CleanScan(),
            new StubRelay(OutboundDeliveryStatus.Delivered));
        await SeedAccountAsync(services, includeCatchAll: false);
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
                RequiresSmtpUtf8: true));
        }

        Assert.IsTrue(await ProcessOneAsync(services, environment));

        using var verificationScope = services.CreateScope();
        var database = verificationScope.ServiceProvider.GetRequiredService<EmailDbContext>();
        var delivered = await database.Emails.SingleAsync();
        Assert.AreEqual("Žuta pošta", delivered.Subject);
        StringAssert.Contains(delivered.Body, "Pozdrav iz Zagreba");
    }

    [TestMethod]
    public async Task SubmissionQueueRejectsMalformedInternationalizedHeaders()
    {
        var environment = CreateEnvironment();
        await using var services = CreateServices(
            environment,
            CleanScan(),
            new StubRelay(OutboundDeliveryStatus.Delivered));
        using var scope = services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
        await database.Database.EnsureCreatedAsync();
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
                RequiresSmtpUtf8: true)));
    }

    [TestMethod]
    public async Task WorkerScansAndDeliversInboundMessageFromDurableQueue()
    {
        var environment = CreateEnvironment();
        var scan = CleanScan("Authentication-Results: email.mk8n.com; spf=pass; dkim=pass; dmarc=pass\r\n");
        await using var services = CreateServices(
            environment,
            scan,
            new StubRelay(OutboundDeliveryStatus.Delivered));
        await SeedAccountAsync(services, includeCatchAll: true);
        var queueId = await EnqueueAsync(
            services,
            "sender@example.net",
            "undefined@mk8n.com",
            isLocal: true,
            authenticatedUser: null);

        Assert.IsTrue(await ProcessOneAsync(services, environment));

        using var scope = services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
        var queued = await database.MailQueueMessages
            .Include(message => message.Recipients)
            .SingleAsync(message => message.Id == queueId);
        Assert.AreEqual(MailQueueStates.Completed, queued.State);
        Assert.AreEqual(MailQueueRecipientStates.Delivered, queued.Recipients.Single().State);
        var delivered = await database.Emails.Include(message => message.Folder).SingleAsync();
        Assert.AreEqual(queued.Recipients.Single().Id, delivered.QueueDeliveryId);
        Assert.AreEqual(DefaultFolders.Inbox, delivered.Folder.Name);
        StringAssert.Contains(delivered.RawHeaders!, "Authentication-Results: email.mk8n.com");
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
        await using var services = CreateServices(
            environment,
            scan,
            new StubRelay(OutboundDeliveryStatus.Delivered));
        await SeedAccountAsync(services, includeCatchAll: false);
        var queueId = await EnqueueAsync(
            services,
            "sender@example.net",
            TestAccount,
            isLocal: true,
            authenticatedUser: null);

        Assert.IsTrue(await ProcessOneAsync(services, environment));

        using var scope = services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
        var queued = await database.MailQueueMessages.SingleAsync(message => message.Id == queueId);
        Assert.AreEqual(MailQueueStates.Pending, queued.State);
        Assert.AreEqual(MailQueueScanStates.Pending, queued.ScanState);
        Assert.AreEqual(1, queued.AttemptCount);
        Assert.IsTrue(queued.NextAttemptAt > queued.ReceivedAt);
        Assert.AreEqual(RawMessage, await ReadQueueContentAsync(scope, queued));
        Assert.AreEqual(0, await database.Emails.CountAsync());
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
        await using var services = CreateServices(
            environment,
            scan,
            new StubRelay(OutboundDeliveryStatus.Delivered));
        await SeedAccountAsync(services, includeCatchAll: false);
        var queueId = await EnqueueAsync(
            services,
            "sender@example.net",
            TestAccount,
            isLocal: true,
            authenticatedUser: null);

        Assert.IsTrue(await ProcessOneAsync(services, environment));

        using var scope = services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
        var queued = await database.MailQueueMessages
            .Include(message => message.Recipients)
            .SingleAsync(message => message.Id == queueId);
        Assert.AreEqual(MailQueueStates.Quarantined, queued.State);
        Assert.AreEqual(MailQueueRecipientStates.Quarantined, queued.Recipients.Single().State);
        Assert.AreEqual(RawMessage, await ReadQueueContentAsync(scope, queued));
        Assert.AreEqual(0, await database.Emails.CountAsync());
    }

    [TestMethod]
    public async Task WorkerStoresSentCopyAndFailureNoticeAfterPermanentRejection()
    {
        var environment = CreateEnvironment();
        var relay = new StubRelay(OutboundDeliveryStatus.PermanentFailure);
        await using var services = CreateServices(environment, CleanScan("DKIM-Signature: test\r\n"), relay);
        await SeedAccountAsync(services, includeCatchAll: false);
        var queueId = await EnqueueAsync(
            services,
            TestAccount,
            "recipient@example.net",
            isLocal: false,
            authenticatedUser: TestAccount);

        Assert.IsTrue(await ProcessOneAsync(services, environment));

        using var scope = services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
        var queued = await database.MailQueueMessages
            .Include(message => message.Recipients)
            .SingleAsync(message => message.Id == queueId);
        Assert.AreEqual(MailQueueStates.Completed, queued.State);
        Assert.IsTrue(queued.SentCopyCreated);
        Assert.AreEqual(MailQueueRecipientStates.PermanentFailure, queued.Recipients.Single().State);
        Assert.IsTrue(queued.Recipients.Single().FailureNoticeCreated);
        Assert.AreEqual(1, relay.CallCount);

        var stored = await database.Emails.Include(message => message.Folder).ToListAsync();
        Assert.AreEqual(2, stored.Count);
        CollectionAssert.AreEquivalent(
            new[] { DefaultFolders.Inbox, DefaultFolders.Sent },
            stored.Select(message => message.Folder.Name).ToArray());
        Assert.IsTrue(stored.Any(message => message.QueueDeliveryId == queueId));
        var failureNotice = stored.Single(message => message.Folder.Name == DefaultFolders.Inbox);
        Assert.AreNotEqual(queued.Recipients.Single().Id, failureNotice.QueueDeliveryId);
        var content = scope.ServiceProvider.GetRequiredService<MailboxMessageContentService>();
        using var parsedNotice = MimeMessage.Load(new MemoryStream(
            await content.ReadAsync(failureNotice, CancellationToken.None)));
        Assert.AreEqual("Delivery Status Notification (Failure)", parsedNotice.Subject);
        var report = Assert.IsInstanceOfType<MultipartReport>(parsedNotice.Body);
        Assert.AreEqual("delivery-status", report.ContentType.Parameters["report-type"]);
        var deliveryStatus = Assert.IsInstanceOfType<MessageDeliveryStatus>(report[1]);
        Assert.AreEqual("failed", deliveryStatus.StatusGroups[1]["Action"]);
        Assert.AreEqual("5.0.0", deliveryStatus.StatusGroups[1]["Status"]);
        Assert.AreEqual("rfc822; recipient@example.net", deliveryStatus.StatusGroups[1]["Final-Recipient"]);
    }

    [TestMethod]
    public async Task WorkerSuppressesFailureNoticeWhenNotifyIsNever()
    {
        var environment = CreateEnvironment();
        var relay = new StubRelay(OutboundDeliveryStatus.PermanentFailure);
        await using var services = CreateServices(environment, CleanScan(), relay);
        await SeedAccountAsync(services, includeCatchAll: false);
        var queueId = await EnqueueAsync(
            services,
            TestAccount,
            "recipient@example.net",
            isLocal: false,
            authenticatedUser: TestAccount,
            recipientDsn: new MailDsnRecipient("NEVER"));

        Assert.IsTrue(await ProcessOneAsync(services, environment));

        using var scope = services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
        var queued = await database.MailQueueMessages
            .Include(message => message.Recipients)
            .SingleAsync(message => message.Id == queueId);
        Assert.AreEqual(MailQueueStates.Completed, queued.State);
        Assert.IsTrue(queued.Recipients.Single().FailureNoticeCreated);
        Assert.AreEqual(1, relay.CallCount);
        Assert.AreEqual(1, await database.Emails.CountAsync());
        Assert.AreEqual(DefaultFolders.Sent, (await database.Emails.Include(message => message.Folder).SingleAsync()).Folder.Name);
    }

    [TestMethod]
    public async Task WorkerNeverCreatesDsnForNullReversePath()
    {
        var environment = CreateEnvironment();
        var relay = new StubRelay(OutboundDeliveryStatus.PermanentFailure);
        await using var services = CreateServices(environment, CleanScan(), relay);
        var queueId = await EnqueueAsync(
            services,
            string.Empty,
            "recipient@example.net",
            isLocal: false,
            authenticatedUser: null,
            recipientDsn: new MailDsnRecipient("FAILURE"));

        Assert.IsTrue(await ProcessOneAsync(services, environment));

        using var scope = services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
        var queued = await database.MailQueueMessages
            .Include(message => message.Recipients)
            .SingleAsync(message => message.Id == queueId);
        Assert.AreEqual(MailQueueStates.Dead, queued.State);
        Assert.IsTrue(queued.Recipients.Single().FailureNoticeCreated);
        Assert.AreEqual(1, relay.CallCount);
        Assert.AreEqual(0, await database.Emails.CountAsync());
    }

    [TestMethod]
    public async Task WorkerCreatesSuccessNoticeForRequestedLocalDelivery()
    {
        var environment = CreateEnvironment();
        await using var services = CreateServices(
            environment,
            CleanScan(),
            new StubRelay(OutboundDeliveryStatus.Delivered));
        await SeedAccountAsync(services, includeCatchAll: false);
        var queueId = await EnqueueAsync(
            services,
            TestAccount,
            TestAccount,
            isLocal: true,
            authenticatedUser: null,
            recipientDsn: new MailDsnRecipient("SUCCESS"));

        Assert.IsTrue(await ProcessOneAsync(services, environment));

        using var scope = services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
        var queued = await database.MailQueueMessages
            .Include(message => message.Recipients)
            .SingleAsync(message => message.Id == queueId);
        Assert.AreEqual(MailQueueStates.Completed, queued.State);
        Assert.IsTrue(queued.Recipients.Single().SuccessNoticeCreated);
        var delivered = await database.Emails.ToListAsync();
        Assert.AreEqual(2, delivered.Count);
        Assert.IsTrue(delivered.Any(message =>
            message.Subject == "Delivery Status Notification (Success)"));
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
        await using var services = CreateServices(environment, CleanScan(), relay);
        await SeedAccountAsync(services, includeCatchAll: false);
        var queueId = await EnqueueAsync(
            services,
            TestAccount,
            "recipient@example.net",
            isLocal: false,
            authenticatedUser: TestAccount,
            recipientDsn: new MailDsnRecipient("SUCCESS"));

        Assert.IsTrue(await ProcessOneAsync(services, environment));

        using var scope = services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
        var queued = await database.MailQueueMessages
            .Include(message => message.Recipients)
            .SingleAsync(message => message.Id == queueId);
        var recipient = queued.Recipients.Single();
        Assert.AreEqual(MailQueueStates.Completed, queued.State);
        Assert.IsTrue(recipient.DsnForwarded);
        Assert.IsTrue(recipient.SuccessNoticeCreated);
        Assert.AreEqual("2.0.0", recipient.LastEnhancedStatusCode);
        Assert.AreEqual("mx.example.net", recipient.LastRemoteMta);
        Assert.AreEqual(1, relay.CallCount);
        Assert.AreEqual(1, await database.Emails.CountAsync());
    }

    [TestMethod]
    public async Task WorkerCreatesRelayedNoticeWhenNextHopLacksDsn()
    {
        var environment = CreateEnvironment();
        var relay = new StubRelay(
            OutboundDeliveryStatus.Delivered,
            enhancedStatusCode: "2.0.0",
            remoteMta: "legacy-mx.example.net");
        await using var services = CreateServices(environment, CleanScan(), relay);
        await SeedAccountAsync(services, includeCatchAll: false);
        var queueId = await EnqueueAsync(
            services,
            TestAccount,
            "recipient@example.net",
            isLocal: false,
            authenticatedUser: TestAccount,
            recipientDsn: new MailDsnRecipient("SUCCESS"));

        Assert.IsTrue(await ProcessOneAsync(services, environment));

        using var scope = services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
        var queued = await database.MailQueueMessages
            .Include(message => message.Recipients)
            .SingleAsync(message => message.Id == queueId);
        Assert.AreEqual(MailQueueStates.Completed, queued.State);
        Assert.IsFalse(queued.Recipients.Single().DsnForwarded);
        var notice = await database.Emails.SingleAsync(message =>
            message.Subject == "Delivery Status Notification (Relayed)");
        var content = scope.ServiceProvider.GetRequiredService<MailboxMessageContentService>();
        using var parsedNotice = MimeMessage.Load(new MemoryStream(
            await content.ReadAsync(notice, CancellationToken.None)));
        var report = Assert.IsInstanceOfType<MultipartReport>(parsedNotice.Body);
        var deliveryStatus = Assert.IsInstanceOfType<MessageDeliveryStatus>(report[1]);
        Assert.AreEqual("relayed", deliveryStatus.StatusGroups[1]["Action"]);
        Assert.AreEqual("dns; legacy-mx.example.net", deliveryStatus.StatusGroups[1]["Remote-MTA"]);
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
        await using var services = CreateServices(environment, CleanScan(), relay);
        await SeedAccountAsync(services, includeCatchAll: false);
        var queueId = await EnqueueAsync(
            services,
            TestAccount,
            "recipient@example.net",
            isLocal: false,
            authenticatedUser: null,
            recipientDsn: new MailDsnRecipient("DELAY,FAILURE"));
        using (var setupScope = services.CreateScope())
        {
            var setupDatabase = setupScope.ServiceProvider.GetRequiredService<EmailDbContext>();
            var queued = await setupDatabase.MailQueueMessages.SingleAsync(message => message.Id == queueId);
            queued.ReceivedAt = DateTime.UtcNow.AddHours(-5);
            queued.NextAttemptAt = DateTime.UtcNow.AddMinutes(-1);
            await setupDatabase.SaveChangesAsync();
        }

        Assert.IsTrue(await ProcessOneAsync(services, environment));

        using var scope = services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
        var message = await database.MailQueueMessages
            .Include(item => item.Recipients)
            .SingleAsync(item => item.Id == queueId);
        Assert.AreEqual(MailQueueStates.Pending, message.State);
        Assert.IsTrue(message.Recipients.Single().DelayNoticeCreated);
        var notice = await database.Emails.SingleAsync();
        Assert.AreEqual("Delivery Status Notification (Delay)", notice.Subject);
        var content = scope.ServiceProvider.GetRequiredService<MailboxMessageContentService>();
        using var parsedNotice = MimeMessage.Load(new MemoryStream(
            await content.ReadAsync(notice, CancellationToken.None)));
        var report = Assert.IsInstanceOfType<MultipartReport>(parsedNotice.Body);
        var deliveryStatus = Assert.IsInstanceOfType<MessageDeliveryStatus>(report[1]);
        Assert.AreEqual("delayed", deliveryStatus.StatusGroups[1]["Action"]);
        Assert.AreEqual("4.4.1", deliveryStatus.StatusGroups[1]["Status"]);
        Assert.AreEqual("dns; mx.example.net", deliveryStatus.StatusGroups[1]["Remote-MTA"]);
    }

    [TestMethod]
    public async Task WorkerReclaimsExpiredLease()
    {
        var environment = CreateEnvironment();
        await using var services = CreateServices(
            environment,
            CleanScan(),
            new StubRelay(OutboundDeliveryStatus.Delivered));
        await SeedAccountAsync(services, includeCatchAll: false);
        var queueId = await EnqueueAsync(
            services,
            "sender@example.net",
            TestAccount,
            isLocal: true,
            authenticatedUser: null);

        using (var scope = services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
            var queued = await database.MailQueueMessages.SingleAsync(message => message.Id == queueId);
            queued.State = MailQueueStates.Processing;
            queued.LeaseToken = Guid.CreateVersion7();
            queued.LeaseExpiresAt = DateTime.UtcNow.AddMinutes(-1);
            await database.SaveChangesAsync();
        }

        Assert.IsTrue(await ProcessOneAsync(services, environment));

        using var verificationScope = services.CreateScope();
        var verificationDatabase = verificationScope.ServiceProvider.GetRequiredService<EmailDbContext>();
        var completed = await verificationDatabase.MailQueueMessages.SingleAsync(message => message.Id == queueId);
        Assert.AreEqual(MailQueueStates.Completed, completed.State);
        Assert.IsNull(completed.LeaseToken);
    }

    [TestMethod]
    public async Task WorkerRetainsInboundMessageWhenMailboxIsOverQuota()
    {
        var environment = CreateEnvironment(maxAttempts: 1);
        await using var services = CreateServices(
            environment,
            CleanScan(),
            new StubRelay(OutboundDeliveryStatus.Delivered));
        await SeedAccountAsync(services, includeCatchAll: false);

        using (var quotaScope = services.CreateScope())
        {
            var quotaDatabase = quotaScope.ServiceProvider.GetRequiredService<EmailDbContext>();
            var user = await quotaDatabase.Users.SingleAsync(item => item.Username == TestAccount);
            user.QuotaBytes = 1;
            await quotaDatabase.SaveChangesAsync();

            var delivery = quotaScope.ServiceProvider.GetRequiredService<IEmailService>();
            Assert.IsTrue(await delivery.CanReceiveAsync(TestAccount));
        }

        var queueId = await EnqueueAsync(
            services,
            "sender@example.net",
            TestAccount,
            isLocal: true,
            authenticatedUser: null);

        Assert.IsTrue(await ProcessOneAsync(services, environment));

        using var verificationScope = services.CreateScope();
        var database = verificationScope.ServiceProvider.GetRequiredService<EmailDbContext>();
        var queued = await database.MailQueueMessages
            .Include(message => message.Recipients)
            .SingleAsync(message => message.Id == queueId);
        Assert.AreEqual(MailQueueStates.Dead, queued.State);
        Assert.AreEqual(MailQueueRecipientStates.PermanentFailure, queued.Recipients.Single().State);
        Assert.AreEqual(RawMessage, await ReadQueueContentAsync(verificationScope, queued));
        Assert.AreEqual(0, await database.Emails.CountAsync());
    }

    [TestMethod]
    public async Task DeletingQueueRecordPublishesSubmissionStatusChange()
    {
        var environment = CreateEnvironment();
        await using var services = CreateServices(
            environment,
            CleanScan(),
            new StubRelay(OutboundDeliveryStatus.Delivered));
        using var scope = services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
        await database.Database.EnsureCreatedAsync();
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
        database.MailQueueMessages.Add(queue);
        database.JmapEmailSubmissions.Add(new JmapEmailSubmissionDB
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
        });
        await database.SaveChangesAsync();

        database.MailQueueMessages.Remove(queue);
        await database.SaveChangesAsync();

        var changes = await database.JmapChanges
            .Where(change => change.AccountId == accountId
                && change.DataType == "EmailSubmission"
                && change.ObjectId == JmapId.Submission(submissionId))
            .Select(change => change.ChangeKind)
            .ToListAsync();
        CollectionAssert.AreEquivalent(new[] { "created", "updated" }, changes);
    }

    [TestMethod]
    public async Task SieveFileIntoCreatesFolderAndAppliesFlags()
    {
        var environment = CreateEnvironment();
        await using var services = CreateServices(
            environment,
            CleanScan(),
            new StubRelay(OutboundDeliveryStatus.Delivered));
        await SeedAccountAsync(services, includeCatchAll: false);
        await ActivateScriptAsync(
            services,
            TestAccount,
            """
            require ["fileinto", "mailbox", "imap4flags"];
            fileinto :create :flags ["\\Seen", "\\Flagged", "project"] "Projects/MK8";
            """);
        var queueId = await EnqueueAsync(
            services,
            "sender@example.net",
            TestAccount,
            isLocal: true,
            authenticatedUser: null);

        Assert.IsTrue(await ProcessOneAsync(services, environment));

        using var scope = services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
        var queued = await database.MailQueueMessages
            .Include(message => message.Recipients)
            .SingleAsync(message => message.Id == queueId);
        Assert.AreEqual(MailQueueStates.Completed, queued.State);
        var delivered = await database.Emails.Include(message => message.Folder).SingleAsync();
        Assert.AreEqual("Projects/MK8", delivered.Folder.Name);
        Assert.IsTrue(delivered.IsRead);
        Assert.IsTrue(delivered.IsFlagged);
        CollectionAssert.AreEqual(new[] { "project" }, delivered.Keywords);
        Assert.AreNotEqual(queued.Recipients.Single().Id, delivered.QueueDeliveryId);
    }

    [TestMethod]
    public async Task SieveDiscardCompletesWithoutMailboxDelivery()
    {
        var environment = CreateEnvironment();
        await using var services = CreateServices(
            environment,
            CleanScan(),
            new StubRelay(OutboundDeliveryStatus.Delivered));
        await SeedAccountAsync(services, includeCatchAll: false);
        await ActivateScriptAsync(services, TestAccount, "discard;");
        var queueId = await EnqueueAsync(
            services,
            "sender@example.net",
            TestAccount,
            isLocal: true,
            authenticatedUser: null);

        Assert.IsTrue(await ProcessOneAsync(services, environment));

        using var scope = services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
        Assert.AreEqual(
            MailQueueStates.Completed,
            (await database.MailQueueMessages.SingleAsync(message => message.Id == queueId)).State);
        Assert.AreEqual(0, await database.Emails.CountAsync());
    }

    [TestMethod]
    public async Task MissingSieveFileIntoMailboxFallsBackToInbox()
    {
        var environment = CreateEnvironment();
        await using var services = CreateServices(
            environment,
            CleanScan(),
            new StubRelay(OutboundDeliveryStatus.Delivered));
        await SeedAccountAsync(services, includeCatchAll: false);
        await ActivateScriptAsync(
            services,
            TestAccount,
            "require \"fileinto\"; fileinto \"Missing\";");
        await EnqueueAsync(
            services,
            "sender@example.net",
            TestAccount,
            isLocal: true,
            authenticatedUser: null);

        Assert.IsTrue(await ProcessOneAsync(services, environment));

        using var scope = services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
        var delivered = await database.Emails.Include(message => message.Folder).SingleAsync();
        Assert.AreEqual(DefaultFolders.Inbox, delivered.Folder.Name);
    }

    [TestMethod]
    public async Task CatchAllDeliveryUsesOwningUsersActiveSieveScript()
    {
        var environment = CreateEnvironment();
        await using var services = CreateServices(
            environment,
            CleanScan(),
            new StubRelay(OutboundDeliveryStatus.Delivered));
        await SeedAccountAsync(services, includeCatchAll: true);
        await ActivateScriptAsync(services, TestAccount, "discard;");
        await EnqueueAsync(
            services,
            "sender@example.net",
            "undefined@mk8n.com",
            isLocal: true,
            authenticatedUser: null);

        Assert.IsTrue(await ProcessOneAsync(services, environment));

        using var scope = services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
        Assert.AreEqual(0, await database.Emails.CountAsync());
        Assert.AreEqual(MailQueueStates.Completed, (await database.MailQueueMessages.SingleAsync()).State);
    }

    [TestMethod]
    public async Task SieveRedirectAddsDurableRecipientForNextQueuePass()
    {
        var environment = CreateEnvironment();
        var relay = new StubRelay(OutboundDeliveryStatus.Delivered);
        await using var services = CreateServices(environment, CleanScan(), relay);
        await SeedAccountAsync(services, includeCatchAll: false);
        await ActivateScriptAsync(
            services,
            TestAccount,
            "redirect \"archive@example.org\";");
        var queueId = await EnqueueAsync(
            services,
            "sender@example.net",
            TestAccount,
            isLocal: true,
            authenticatedUser: null);

        Assert.IsTrue(await ProcessOneAsync(services, environment));
        Assert.AreEqual(0, relay.CallCount);
        Assert.IsTrue(await ProcessOneAsync(services, environment));

        using var scope = services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
        var queued = await database.MailQueueMessages
            .Include(message => message.Recipients)
            .SingleAsync(message => message.Id == queueId);
        Assert.AreEqual(
            MailQueueStates.Completed,
            queued.State,
            $"message={queued.AttemptCount}:{queued.NextAttemptAt:o}:{queued.LastError};" +
            string.Join(';', queued.Recipients.Select(item =>
                $"{item.Recipient}:{item.State}:{item.AttemptCount}:{item.NextAttemptAt:o}:{item.LastError}")));
        Assert.AreEqual(2, queued.Recipients.Count);
        var redirected = queued.Recipients.Single(item => item.Recipient == "archive@example.org");
        Assert.AreEqual(1, redirected.RedirectDepth);
        CollectionAssert.AreEquivalent(
            new[] { TestAccount, "archive@example.org" },
            redirected.RedirectHistory);
        Assert.AreEqual(1, relay.CallCount);
        Assert.AreEqual(0, await database.Emails.CountAsync());
    }

    [TestMethod]
    public async Task SieveExpansionIssuesOneSuccessDsnAndPropagatesFailureOnly()
    {
        var environment = CreateEnvironment();
        var relay = new StubRelay(OutboundDeliveryStatus.Delivered);
        await using var services = CreateServices(environment, CleanScan(), relay);
        await SeedAccountAsync(services, includeCatchAll: false);
        await ActivateScriptAsync(
            services,
            TestAccount,
            "redirect \"archive@example.org\";");
        var queueId = await EnqueueAsync(
            services,
            TestAccount,
            TestAccount,
            isLocal: true,
            authenticatedUser: null,
            recipientDsn: new MailDsnRecipient("SUCCESS,FAILURE"));

        Assert.IsTrue(await ProcessOneAsync(services, environment));
        Assert.IsTrue(await ProcessOneAsync(services, environment));

        using var scope = services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
        var queued = await database.MailQueueMessages
            .Include(message => message.Recipients)
            .SingleAsync(message => message.Id == queueId);
        Assert.AreEqual(MailQueueStates.Completed, queued.State);
        var redirect = queued.Recipients.Single(recipient =>
            recipient.Recipient == "archive@example.org");
        Assert.AreEqual("FAILURE", redirect.DsnNotify);
        Assert.AreEqual("FAILURE", relay.LastOptions!.RecipientDsn!.Notify);
        var notices = await database.Emails
            .Where(message => message.Subject == "Delivery Status Notification (Expanded)")
            .ToListAsync();
        Assert.HasCount(1, notices);
        var content = scope.ServiceProvider.GetRequiredService<MailboxMessageContentService>();
        using var parsedNotice = MimeMessage.Load(new MemoryStream(
            await content.ReadAsync(notices[0], CancellationToken.None)));
        var report = Assert.IsInstanceOfType<MultipartReport>(parsedNotice.Body);
        var deliveryStatus = Assert.IsInstanceOfType<MessageDeliveryStatus>(report[1]);
        Assert.AreEqual("expanded", deliveryStatus.StatusGroups[1]["Action"]);
    }

    [TestMethod]
    public async Task SieveRedirectLoopFallsBackToKeepAtLastRecipient()
    {
        const string secondAccount = "second@mk8n.com";
        var environment = CreateEnvironment();
        var relay = new StubRelay(OutboundDeliveryStatus.Delivered);
        await using var services = CreateServices(environment, CleanScan(), relay);
        await SeedAccountAsync(services, includeCatchAll: false);
        using (var setupScope = services.CreateScope())
        {
            var setupDatabase = setupScope.ServiceProvider.GetRequiredService<EmailDbContext>();
            var administration = new MailAdministrationService(setupDatabase);
            Assert.IsTrue((await administration.CreateAccountAsync(
                secondAccount,
                "second-account-password-value",
                UserRole.User)).Succeeded);
        }
        await ActivateScriptAsync(services, TestAccount, $"redirect \"{secondAccount}\";");
        await ActivateScriptAsync(services, secondAccount, $"redirect \"{TestAccount}\";");
        var queueId = await EnqueueAsync(
            services,
            "sender@example.net",
            TestAccount,
            isLocal: true,
            authenticatedUser: null);

        Assert.IsTrue(await ProcessOneAsync(services, environment));
        Assert.IsTrue(await ProcessOneAsync(services, environment));

        using var scope = services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
        var queueMessage = await database.MailQueueMessages.SingleAsync(message => message.Id == queueId);
        Assert.AreEqual(
            MailQueueStates.Completed,
            queueMessage.State,
            $"message={queueMessage.AttemptCount}:{queueMessage.NextAttemptAt:o}:{queueMessage.LastError};" +
            string.Join(';', (await database.MailQueueRecipients
                .Where(item => item.MessageId == queueId)
                .ToListAsync()).Select(item =>
                $"{item.Recipient}:{item.State}:{item.AttemptCount}:{item.NextAttemptAt:o}:{item.LastError}")));
        var delivered = await database.Emails.Include(message => message.Folder).SingleAsync();
        Assert.AreEqual(secondAccount, delivered.Recipient);
        Assert.AreEqual(DefaultFolders.Inbox, delivered.Folder.Name);
        Assert.AreEqual(0, relay.CallCount);
    }

    [TestMethod]
    public async Task SieveRejectNotifiesEnvelopeSenderWithoutStoringOriginal()
    {
        var environment = CreateEnvironment();
        var relay = new StubRelay(OutboundDeliveryStatus.Delivered);
        await using var services = CreateServices(environment, CleanScan(), relay);
        await SeedAccountAsync(services, includeCatchAll: false);
        await ActivateScriptAsync(
            services,
            TestAccount,
            "require \"reject\"; reject \"This mailbox does not accept automated reports.\";");
        var queueId = await EnqueueAsync(
            services,
            "sender@example.net",
            TestAccount,
            isLocal: true,
            authenticatedUser: null);

        Assert.IsTrue(await ProcessOneAsync(services, environment));

        using var scope = services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
        Assert.AreEqual(
            MailQueueStates.Completed,
            (await database.MailQueueMessages.SingleAsync(message => message.Id == queueId)).State);
        Assert.AreEqual(0, await database.Emails.CountAsync());
        Assert.AreEqual(1, relay.CallCount);
        Assert.AreEqual(string.Empty, relay.LastSender);
        Assert.AreEqual("sender@example.net", relay.LastRecipient);
        Assert.AreEqual("NEVER", relay.LastOptions!.RecipientDsn!.Notify);
        StringAssert.Contains(relay.LastRawMessage!, "This mailbox does not accept automated reports.");
        StringAssert.Contains(relay.LastRawMessage!, "Auto-Submitted: auto-replied");
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
        await database.Database.EnsureCreatedAsync();
        var administration = new MailAdministrationService(database);
        Assert.IsTrue((await administration.EnsureDomainAsync("mk8n", TestDomain)).Succeeded);
        Assert.IsTrue((await administration.CreateAccountAsync(
            TestAccount,
            "test-account-password-value",
            UserRole.SuperAdmin)).Succeeded);
        if (includeCatchAll)
        {
            Assert.IsTrue((await administration.SetCatchAllAsync(TestDomain, TestAccount)).Succeeded);
        }
        Assert.IsTrue((await administration.SetDomainActiveAsync(TestDomain, true)).Succeeded);
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
            Dsn: dsn));
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
            .SingleAsync();
        var scripts = scope.ServiceProvider.GetRequiredService<ISieveScriptService>();
        var put = await scripts.PutAsync(userId, "active", content);
        Assert.IsTrue(put.Succeeded, put.Error);
        var active = await scripts.SetActiveAsync(userId, "active");
        Assert.IsTrue(active.Succeeded, active.Error);
    }

    private static async Task<bool> ProcessOneAsync(
        ServiceProvider services,
        EnvironmentConfig environment)
    {
        var logger = new CapturingQueueLogger();
        var worker = new MailQueueWorker(
            services.GetRequiredService<IServiceScopeFactory>(),
            environment,
            TimeProvider.System,
            logger);
        var processed = await worker.ProcessNextAsync(CancellationToken.None);
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
        public Task<MailScanResult> ScanAsync(
            MailScanRequest request,
            CancellationToken cancellationToken = default) => Task.FromResult(result);
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
}
