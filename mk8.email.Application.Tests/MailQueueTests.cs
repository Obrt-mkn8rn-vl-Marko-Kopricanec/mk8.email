using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using mk8.email.Application.Interfaces;
using mk8.email.Application.Services;
using mk8.email.Contracts.Enums;
using mk8.email.Infrastructure.Data;
using mk8.email.Infrastructure.Environment;
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
                    new MailEnvelopeRecipient(TestAccount, true),
                    new MailEnvelopeRecipient("ADMIN@MK8N.COM", true),
                ],
                RawMessage,
                "192.0.2.10",
                "sender.example.net",
                null));
        }

        using var verificationScope = services.CreateScope();
        var database = verificationScope.ServiceProvider.GetRequiredService<EmailDbContext>();
        var queued = await database.MailQueueMessages
            .Include(message => message.Recipients)
            .SingleAsync();
        Assert.AreEqual(queueId, queued.Id);
        Assert.AreEqual(RawMessage, queued.RawMessage);
        Assert.AreEqual(MailQueueStates.Pending, queued.State);
        Assert.AreEqual(MailQueueDirections.Inbound, queued.Direction);
        Assert.AreEqual(1, queued.Recipients.Count);
        Assert.AreEqual(TestAccount, queued.Recipients.Single().Recipient);
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
        Assert.AreEqual(RawMessage, queued.RawMessage);
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
        Assert.AreEqual(RawMessage, queued.RawMessage);
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
        Assert.IsTrue(stored.Any(message => message.QueueDeliveryId == queued.Recipients.Single().Id));
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
        Assert.AreEqual(RawMessage, queued.RawMessage);
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
        services.AddScoped<IMailSubmissionQueue, PostgresMailSubmissionQueue>();
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
        string? authenticatedUser)
    {
        using var scope = services.CreateScope();
        var queue = scope.ServiceProvider.GetRequiredService<IMailSubmissionQueue>();
        var queueId = Guid.CreateVersion7();
        await queue.EnqueueAsync(new MailSubmission(
            queueId,
            sender,
            [new MailEnvelopeRecipient(recipient, isLocal)],
            RawMessage,
            "192.0.2.10",
            "sender.example.net",
            authenticatedUser));
        return queueId;
    }

    private static async Task<bool> ProcessOneAsync(
        ServiceProvider services,
        EnvironmentConfig environment)
    {
        var worker = new MailQueueWorker(
            services.GetRequiredService<IServiceScopeFactory>(),
            environment,
            TimeProvider.System,
            NullLogger<MailQueueWorker>.Instance);
        return await worker.ProcessNextAsync(CancellationToken.None);
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

    private sealed class StubRelay(OutboundDeliveryStatus status) : IOutboundMailRelay
    {
        public int CallCount { get; private set; }

        public Task<OutboundDeliveryResult> RelayAsync(
            string sender,
            string recipient,
            string rawMessage,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(new OutboundDeliveryResult(status, "Test delivery result."));
        }
    }
}
