using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using mk8.email.Application.Interfaces;
using mk8.email.Application.Services;
using mk8.email.Configuration;
using mk8.email.Contracts.Storage;
using mk8.email.Infrastructure.Data;
using mk8.email.Infrastructure.Models;
using mk8.email.Storage;
using Npgsql;

namespace mk8.email.Messaging.Tests;

[TestClass]
[DoNotParallelize]
[TestCategory("PostgreSQL")]
[TestCategory("AzureBlobCompatible")]
public sealed class MailQueueAzureBlobPersistenceTests
{
    private const string RawMessage =
        "From: sender@example.test\r\n" +
        "To: recipient@example.test\r\n" +
        "Subject: external queue payload\r\n\r\n" +
        "attachment-like-body\r\n";

    [TestMethod]
    public async Task ConcurrentLegacyMigrationExternalizesQueueAndRejectsInlineRows()
    {
        await using var databaseServer = await RequirePostgresAsync();
        var connectionString = RequireAzureBlobConnection();
        var serviceClient = new BlobServiceClient(connectionString);
        var containerName = $"mk8-queue-{Guid.NewGuid():N}";
        var container = serviceClient.GetBlobContainerClient(containerName);
        var store = CreateStore(serviceClient, containerName);
        var queueId = Guid.CreateVersion7();
        await CreateSchemaAsync(databaseServer.ConnectionString);
        await InsertLegacyAsync(databaseServer.ConnectionString, queueId, RawMessage);

        try
        {
            async Task MigrateAsync()
            {
                await using var context = CreateContext(databaseServer.ConnectionString);
                await new MailQueueLargeObjectMigrationService(
                    context,
                    store,
                    NullLogger<MailQueueLargeObjectMigrationService>.Instance)
                    .MigrateAsync();
            }

            await Task.WhenAll(MigrateAsync(), MigrateAsync());

            await using var verification = CreateContext(databaseServer.ConnectionString);
            var migrated = await verification.MailQueueMessages.AsNoTracking()
                .SingleAsync(candidate => candidate.Id == queueId);
            Assert.IsNull(migrated.RawMessage);
            Assert.AreEqual(LargeObjectProviders.AzureBlob, migrated.RawMessageObjectProvider);
            Assert.AreEqual(
                $"mail/queue/{queueId:N}/raw.eml",
                migrated.RawMessageObjectName);
            Assert.AreEqual(RawMessage.Length, migrated.RawMessageSizeBytes);
            Assert.AreEqual(64, migrated.RawMessageObjectSha256?.Length);
            Assert.IsFalse(string.IsNullOrWhiteSpace(migrated.RawMessageObjectEntityTag));

            await using var downloaded = new MemoryStream();
            await store.CopyToAsync(ToReference(migrated), downloaded);
            Assert.AreEqual(
                RawMessage,
                System.Text.Encoding.Latin1.GetString(downloaded.ToArray()));

            var exception = await Assert.ThrowsExactlyAsync<PostgresException>(() =>
                InsertLegacyAsync(
                    databaseServer.ConnectionString,
                    Guid.CreateVersion7(),
                    RawMessage));
            Assert.AreEqual(PostgresErrorCodes.CheckViolation, exception.SqlState);
        }
        finally
        {
            await container.DeleteIfExistsAsync();
        }
    }

    [TestMethod]
    public async Task SubmissionAndWorkerRetryUseReferenceOnlyQueueContent()
    {
        await using var databaseServer = await RequirePostgresAsync();
        var connectionString = RequireAzureBlobConnection();
        var serviceClient = new BlobServiceClient(connectionString);
        var containerName = $"mk8-queue-{Guid.NewGuid():N}";
        var container = serviceClient.GetBlobContainerClient(containerName);
        var store = CreateStore(serviceClient, containerName);
        await CreateSchemaAsync(databaseServer.ConnectionString);
        await using (var migrationContext = CreateContext(databaseServer.ConnectionString))
        {
            await new MailQueueLargeObjectMigrationService(
                migrationContext,
                store,
                NullLogger<MailQueueLargeObjectMigrationService>.Instance)
                .MigrateAsync();
        }

        try
        {
            var environment = CreateEnvironment();
            var scanner = new RetryScanner();
            var services = new ServiceCollection();
            services.AddSingleton(environment);
            services.AddSingleton<ILargeObjectStore>(store);
            services.AddDbContext<EmailDbContext>(options =>
                options.UseNpgsql(databaseServer.ConnectionString));
            services.AddScoped<LargeObjectTransactionEffects>();
            services.AddScoped<MailQueueContentService>();
            services.AddScoped<MailQueueLargeObjectMigrationService>();
            services.AddScoped<IMailSubmissionQueue, PostgresMailSubmissionQueue>();
            services.AddScoped<IEmailService, UnusedEmailService>();
            services.AddSingleton<IMailScanner>(scanner);
            services.AddSingleton<IOutboundMailRelay, UnusedRelay>();
            services.AddLogging();
            await using var provider = services.BuildServiceProvider();

            var failedQueueId = Guid.CreateVersion7();
            await using (var constraintContext = CreateContext(databaseServer.ConnectionString))
            {
                await constraintContext.Database.ExecuteSqlRawAsync(
                    """
                    ALTER TABLE mail_queue_messages
                        ADD CONSTRAINT ck_test_reject_rollback_sender
                        CHECK (envelope_sender <> 'rollback@example.test')
                    """);
            }
            using (var failedScope = provider.CreateScope())
            {
                await Assert.ThrowsExactlyAsync<DbUpdateException>(() =>
                    failedScope.ServiceProvider.GetRequiredService<IMailSubmissionQueue>()
                        .EnqueueAsync(new MailSubmission(
                            failedQueueId,
                            "rollback@example.test",
                            [new MailEnvelopeRecipient("recipient@example.test", false)],
                            RawMessage,
                            "192.0.2.1",
                            "sender.example.test",
                            null)));
            }
            Assert.HasCount(0, await GetBlobNamesAsync(container, failedQueueId));
            await using (var constraintContext = CreateContext(databaseServer.ConnectionString))
            {
                await constraintContext.Database.ExecuteSqlRawAsync(
                    """
                    ALTER TABLE mail_queue_messages
                        DROP CONSTRAINT ck_test_reject_rollback_sender
                    """);
            }

            var queueId = Guid.CreateVersion7();
            using (var scope = provider.CreateScope())
            {
                await scope.ServiceProvider.GetRequiredService<IMailSubmissionQueue>()
                    .EnqueueAsync(new MailSubmission(
                        queueId,
                        "sender@example.test",
                        [new MailEnvelopeRecipient("recipient@example.test", false)],
                        RawMessage,
                        "192.0.2.1",
                        "sender.example.test",
                        null));
            }

            await using (var queuedContext = CreateContext(databaseServer.ConnectionString))
            {
                var queued = await queuedContext.MailQueueMessages.AsNoTracking()
                    .SingleAsync(message => message.Id == queueId);
                Assert.IsNull(queued.RawMessage);
                Assert.AreEqual(LargeObjectProviders.AzureBlob, queued.RawMessageObjectProvider);
                Assert.AreEqual(RawMessage.Length, queued.RawMessageSizeBytes);
            }

            var worker = new MailQueueWorker(
                provider.GetRequiredService<IServiceScopeFactory>(),
                environment,
                TimeProvider.System,
                NullLogger<MailQueueWorker>.Instance);
            Assert.IsTrue(await worker.ProcessNextAsync(CancellationToken.None));
            Assert.AreEqual(RawMessage, scanner.RawMessage);

            await using var verification = CreateContext(databaseServer.ConnectionString);
            var retained = await verification.MailQueueMessages.AsNoTracking()
                .SingleAsync(message => message.Id == queueId);
            Assert.IsNull(retained.RawMessage);
            Assert.AreEqual(MailQueueStates.Pending, retained.State);
            Assert.AreEqual(1, retained.AttemptCount);
            var names = await GetBlobNamesAsync(container, queueId);
            Assert.HasCount(1, names);
            Assert.AreEqual(
                $"objects/mail/queue/{queueId:N}/raw.eml",
                names[0]);

            await using (var completion = CreateContext(databaseServer.ConnectionString))
            {
                var completed = await completion.MailQueueMessages
                    .SingleAsync(message => message.Id == queueId);
                completed.State = MailQueueStates.Completed;
                completed.CompletedAt = DateTime.UtcNow.AddDays(-2);
                await completion.SaveChangesAsync();
            }
            await worker.CleanupCompletedAsync(CancellationToken.None);
            await using var cleanupVerification = CreateContext(databaseServer.ConnectionString);
            Assert.AreEqual(0, await cleanupVerification.MailQueueMessages.CountAsync());
            Assert.HasCount(0, await GetBlobNamesAsync(container, queueId));
        }
        finally
        {
            await container.DeleteIfExistsAsync();
        }
    }

    private static EnvironmentConfig CreateEnvironment() => new()
    {
        Smtp = new SmtpConfig { Hostname = "email.example.test" },
        Limits = new LimitsConfig
        {
            MaxMessageSizeBytes = 1_048_576,
            MaxRecipientsPerMessage = 10,
        },
        Queue = new QueueConfig
        {
            LeaseSeconds = 60,
            MaxAttempts = 5,
            MaxAgeHours = 24,
            CompletedRetentionDays = 1,
        },
    };

    private static AzureBlobLargeObjectStore CreateStore(
        BlobServiceClient serviceClient,
        string containerName) => new(
        serviceClient,
        new AzureBlobLargeObjectStoreOptions
        {
            ContainerName = containerName,
            ObjectPrefix = "objects",
            CreateContainerIfMissing = true,
        });

    private static EmailDbContext CreateContext(string connectionString) => new(
        new DbContextOptionsBuilder<EmailDbContext>()
            .UseNpgsql(connectionString)
            .Options);

    private static async Task CreateSchemaAsync(string connectionString)
    {
        await using var context = CreateContext(connectionString);
        await context.Database.EnsureCreatedAsync();
    }

    private static async Task InsertLegacyAsync(
        string connectionString,
        Guid queueId,
        string rawMessage)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO mail_queue_messages (
                id, envelope_sender, raw_message, raw_message_size_bytes,
                requires_smtp_utf8, direction, state, scan_state, attempt_count,
                received_at, next_attempt_at, sent_copy_created)
            VALUES (
                @id, 'sender@example.test', @raw_message, @size_bytes,
                false, 'inbound', 'pending', 'pending', 0,
                @received_at, @next_attempt_at, false)
            """;
        command.Parameters.AddWithValue("id", queueId);
        command.Parameters.AddWithValue("raw_message", rawMessage);
        command.Parameters.AddWithValue("size_bytes", rawMessage.Length);
        command.Parameters.AddWithValue("received_at", DateTime.UtcNow);
        command.Parameters.AddWithValue("next_attempt_at", DateTime.UtcNow);
        await command.ExecuteNonQueryAsync();
    }

    private static LargeObjectReference ToReference(MailQueueMessageDB message) => new(
        message.RawMessageObjectProvider
            ?? throw new AssertFailedException("The object provider is missing."),
        message.RawMessageObjectName
            ?? throw new AssertFailedException("The object name is missing."),
        message.RawMessageSizeBytes,
        message.RawMessageObjectSha256
            ?? throw new AssertFailedException("The object hash is missing."),
        message.RawMessageObjectEntityTag
            ?? throw new AssertFailedException("The object entity tag is missing."));

    private static async Task<List<string>> GetBlobNamesAsync(
        BlobContainerClient container,
        Guid queueId)
    {
        var names = new List<string>();
        await foreach (var item in container.GetBlobsAsync(
                           BlobTraits.None,
                           BlobStates.None,
                           $"objects/mail/queue/{queueId:N}/",
                           CancellationToken.None))
        {
            names.Add(item.Name);
        }
        names.Sort(StringComparer.Ordinal);
        return names;
    }

    private static async Task<PostgresTestDatabase> RequirePostgresAsync()
    {
        var database = await PostgresTestDatabase.TryCreateAsync();
        if (database is null)
        {
            Assert.Inconclusive(
                "Set MK8_EMAIL_TEST_POSTGRES to a PostgreSQL admin connection string.");
            throw new InvalidOperationException("PostgreSQL integration test configuration is required.");
        }
        return database;
    }

    private static string RequireAzureBlobConnection()
    {
        var connectionString = Environment.GetEnvironmentVariable(
            "MK8_EMAIL_TEST_AZURE_BLOB_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Assert.Inconclusive(
                "Set MK8_EMAIL_TEST_AZURE_BLOB_CONNECTION to an Azure Blob-compatible endpoint.");
            throw new InvalidOperationException("Azure Blob-compatible test configuration is required.");
        }
        return connectionString;
    }

    private sealed class RetryScanner : IMailScanner
    {
        public string? RawMessage { get; private set; }

        public Task<MailScanResult> ScanAsync(
            MailScanRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RawMessage = request.RawMessage;
            return Task.FromResult(new MailScanResult(
                "soft reject",
                0,
                10,
                new HashSet<string>(StringComparer.Ordinal),
                string.Empty,
                IsMalware: false,
                IsTemporaryFailure: true));
        }
    }

    private sealed class UnusedEmailService : IEmailService
    {
        public Task<bool> CanReceiveAsync(
            string recipient,
            CancellationToken cancellationToken = default) =>
            throw new AssertFailedException("Delivery must not run for a scanner retry.");

        public Task<bool> DeliverAsync(
            string sender,
            string recipient,
            string rawMessage,
            string folderName = "INBOX",
            Guid? queueDeliveryId = null,
            CancellationToken cancellationToken = default,
            IReadOnlyCollection<string>? flags = null,
            bool createFolder = false) =>
            throw new AssertFailedException("Delivery must not run for a scanner retry.");

        public Task<bool> SaveSentCopyAsync(
            string sender,
            string rawMessage,
            Guid? queueDeliveryId = null,
            CancellationToken cancellationToken = default) =>
            throw new AssertFailedException("Delivery must not run for a scanner retry.");
    }

    private sealed class UnusedRelay : IOutboundMailRelay
    {
        public Task<OutboundDeliveryResult> RelayAsync(
            string sender,
            string recipient,
            string rawMessage,
            OutboundMailOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new AssertFailedException("Relay must not run for a scanner retry.");
    }
}
