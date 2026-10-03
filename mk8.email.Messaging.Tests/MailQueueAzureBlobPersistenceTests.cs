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
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1812", Justification = "MSTest DiscoverInternals instantiates this test class by reflection; focused discovery is verified by executed test counts.")]
internal sealed class MailQueueAzureBlobPersistenceTests
{
    private const string RawMessage =
        "From: sender@example.test\r\n" +
        "To: recipient@example.test\r\n" +
        "Subject: external queue payload\r\n\r\n" +
        "attachment-like-body\r\n";

    [TestMethod]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "MA0051", Justification = "The QuarantinedSmokeCleanupRequiresExactMarkerAndDeletesBlob scenario keeps one fixture's ordered setup, operation and invariant assertions together.")]
    public async Task QuarantinedSmokeCleanupRequiresExactMarkerAndDeletesBlob()
    {
        var databaseServer = (await RequirePostgresAsync().ConfigureAwait(false));
        await using var databaseServerLifetime = databaseServer.ConfigureAwait(false);
        var serviceClient = new BlobServiceClient(RequireAzureBlobConnection());
        var containerName = $"mk8-queue-{Guid.NewGuid():N}";
        var container = serviceClient.GetBlobContainerClient(containerName);
        var store = CreateStore(serviceClient, containerName);
        await CreateSchemaAsync(databaseServer.ConnectionString).ConfigureAwait(false);
        {
            var migrationContext = CreateContext(databaseServer.ConnectionString);
            await using var migrationContextLifetime = migrationContext.ConfigureAwait(false);
            await new MailQueueLargeObjectMigrationService(
                migrationContext,
                store,
                NullLogger<MailQueueLargeObjectMigrationService>.Instance)
                .MigrateAsync().ConfigureAwait(false);
        }

        try
        {
            var services = new ServiceCollection();
            services.AddSingleton(CreateEnvironment());
            services.AddSingleton<ILargeObjectStore>(store);
            services.AddDbContext<EmailDbContext>(options =>
                options.UseNpgsql(databaseServer.ConnectionString));
            services.AddScoped<LargeObjectTransactionEffects>();
            services.AddScoped<MailQueueContentService>();
            services.AddScoped<MailQueueMaintenanceService>();
            services.AddScoped<IMailSubmissionQueue, PostgresMailSubmissionQueue>();
            services.AddLogging();
            var provider = services.BuildServiceProvider();
            await using var providerLifetime = provider.ConfigureAwait(false);

            var queueId = Guid.CreateVersion7();
            var marker = Guid.NewGuid().ToString("N");
            var raw = $"From: probe@debian.org\r\nTo: admin@example.test\r\n" +
                $"X-Mk8-Test: {marker}\r\nSubject: smoke\r\n\r\nquarantine probe\r\n";
            using (var enqueueScope = provider.CreateScope())
            {
                await enqueueScope.ServiceProvider.GetRequiredService<IMailSubmissionQueue>()
                    .EnqueueAsync(new MailSubmission(
                        queueId,
                        "probe@debian.org",
                        [new MailEnvelopeRecipient("admin@example.test", true)],
                        raw,
                        "192.0.2.1",
                        "probe.debian.org",
                        null,
                        Dsn: new MailDsnEnvelope(EnvelopeId: marker))).ConfigureAwait(false);
            }

            Assert.HasCount(1, await GetBlobNamesAsync(container, queueId).ConfigureAwait(false));
            using (var pendingScope = provider.CreateScope())
            {
                var maintenance = pendingScope.ServiceProvider
                    .GetRequiredService<MailQueueMaintenanceService>();
                Assert.IsFalse(await maintenance.PurgeQuarantinedSmokeMessageAsync(marker).ConfigureAwait(false));
                Assert.IsFalse(await maintenance.PurgeQuarantinedSmokeMessageAsync(
                    Guid.NewGuid().ToString("N")).ConfigureAwait(false));
                await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
                    maintenance.PurgeQuarantinedSmokeMessageAsync("unsafe-marker")).ConfigureAwait(false);
            }

            {
                var quarantine = CreateContext(databaseServer.ConnectionString);
                await using var quarantineLifetime = quarantine.ConfigureAwait(false);
                var message = await quarantine.MailQueueMessages.SingleAsync().ConfigureAwait(false);
                message.State = MailQueueStates.Quarantined;
                await quarantine.SaveChangesAsync().ConfigureAwait(false);
            }

            using (var purgeScope = provider.CreateScope())
            {
                Assert.IsTrue(await purgeScope.ServiceProvider
                    .GetRequiredService<MailQueueMaintenanceService>()
                    .PurgeQuarantinedSmokeMessageAsync(marker).ConfigureAwait(false));
            }

            var verification = CreateContext(databaseServer.ConnectionString);
            await using var verificationLifetime = verification.ConfigureAwait(false);
            Assert.AreEqual(0, await verification.MailQueueMessages.CountAsync().ConfigureAwait(false));
            Assert.HasCount(0, await GetBlobNamesAsync(container, queueId).ConfigureAwait(false));

            var forgedId = Guid.CreateVersion7();
            using (var enqueueScope = provider.CreateScope())
            {
                await enqueueScope.ServiceProvider.GetRequiredService<IMailSubmissionQueue>()
                    .EnqueueAsync(new MailSubmission(
                        forgedId,
                        "probe@debian.org",
                        [new MailEnvelopeRecipient("admin@example.test", true)],
                        raw.Replace(marker, Guid.NewGuid().ToString("N"), StringComparison.Ordinal),
                        "192.0.2.1",
                        "probe.debian.org",
                        null,
                        Dsn: new MailDsnEnvelope(EnvelopeId: marker))).ConfigureAwait(false);
            }
            {
                var quarantine = CreateContext(databaseServer.ConnectionString);
                await using var quarantineLifetime = quarantine.ConfigureAwait(false);
                var message = await quarantine.MailQueueMessages.SingleAsync().ConfigureAwait(false);
                message.State = MailQueueStates.Quarantined;
                await quarantine.SaveChangesAsync().ConfigureAwait(false);
            }
            using (var purgeScope = provider.CreateScope())
            {
                await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                    purgeScope.ServiceProvider.GetRequiredService<MailQueueMaintenanceService>()
                        .PurgeQuarantinedSmokeMessageAsync(marker)).ConfigureAwait(false);
            }
            var forgedVerification = CreateContext(databaseServer.ConnectionString);
            await using var forgedVerificationLifetime = forgedVerification.ConfigureAwait(false);
            Assert.AreEqual(1, await forgedVerification.MailQueueMessages.CountAsync().ConfigureAwait(false));
            Assert.HasCount(1, await GetBlobNamesAsync(container, forgedId).ConfigureAwait(false));
        }
        finally
        {
            await container.DeleteIfExistsAsync().ConfigureAwait(false);
        }
    }

    [TestMethod]
    public async Task ConcurrentLegacyMigrationExternalizesQueueAndRejectsInlineRows()
    {
        var databaseServer = (await RequirePostgresAsync().ConfigureAwait(false));
        await using var databaseServerLifetime = databaseServer.ConfigureAwait(false);
        var connectionString = RequireAzureBlobConnection();
        var serviceClient = new BlobServiceClient(connectionString);
        var containerName = $"mk8-queue-{Guid.NewGuid():N}";
        var container = serviceClient.GetBlobContainerClient(containerName);
        var store = CreateStore(serviceClient, containerName);
        var queueId = Guid.CreateVersion7();
        await CreateSchemaAsync(databaseServer.ConnectionString).ConfigureAwait(false);
        await InsertLegacyAsync(databaseServer.ConnectionString, queueId, RawMessage).ConfigureAwait(false);

        try
        {
            async Task MigrateAsync()
            {
                var context = CreateContext(databaseServer.ConnectionString);
                await using var contextLifetime = context.ConfigureAwait(false);
                await new MailQueueLargeObjectMigrationService(
                    context,
                    store,
                    NullLogger<MailQueueLargeObjectMigrationService>.Instance)
                    .MigrateAsync().ConfigureAwait(false);
            }

            await Task.WhenAll(MigrateAsync(), MigrateAsync()).ConfigureAwait(false);

            var verification = CreateContext(databaseServer.ConnectionString);
            await using var verificationLifetime = verification.ConfigureAwait(false);
            var migrated = await verification.MailQueueMessages.AsNoTracking()
                .SingleAsync(candidate => candidate.Id == queueId).ConfigureAwait(false);
            Assert.IsNull(migrated.RawMessage);
            Assert.AreEqual(LargeObjectProviders.AzureBlob, migrated.RawMessageObjectProvider, StringComparer.Ordinal);
            Assert.AreEqual(
                $"mail/queue/{queueId:N}/raw.eml",
                migrated.RawMessageObjectName, StringComparer.Ordinal);
            Assert.AreEqual(RawMessage.Length, migrated.RawMessageSizeBytes);
            Assert.AreEqual(64, migrated.RawMessageObjectSha256?.Length);
            Assert.IsFalse(string.IsNullOrWhiteSpace(migrated.RawMessageObjectEntityTag));

            var downloaded = new MemoryStream();
            await using var downloadedLifetime = downloaded.ConfigureAwait(false);
            await store.CopyToAsync(ToReference(migrated), downloaded).ConfigureAwait(false);
            Assert.AreEqual(
                RawMessage,
                System.Text.Encoding.Latin1.GetString(downloaded.ToArray()), StringComparer.Ordinal);

            var exception = await Assert.ThrowsExactlyAsync<PostgresException>(() =>
                InsertLegacyAsync(
                    databaseServer.ConnectionString,
                    Guid.CreateVersion7(),
                    RawMessage)).ConfigureAwait(false);
            Assert.AreEqual(PostgresErrorCodes.CheckViolation, exception.SqlState, StringComparer.Ordinal);
        }
        finally
        {
            await container.DeleteIfExistsAsync().ConfigureAwait(false);
        }
    }

    [TestMethod]
    public async Task FailedLegacyMigrationRetainsQueueRowAndRemovesCreatedBlob()
    {
        var databaseServer = (await RequirePostgresAsync().ConfigureAwait(false));
        await using var databaseServerLifetime = databaseServer.ConfigureAwait(false);
        var serviceClient = new BlobServiceClient(RequireAzureBlobConnection());
        var containerName = $"mk8-queue-{Guid.NewGuid():N}";
        var container = serviceClient.GetBlobContainerClient(containerName);
        var store = CreateStore(serviceClient, containerName);
        var queueId = Guid.CreateVersion7();
        await CreateSchemaAsync(databaseServer.ConnectionString).ConfigureAwait(false);
        await InsertLegacyAsync(databaseServer.ConnectionString, queueId, RawMessage).ConfigureAwait(false);
        {
            var constraint = CreateContext(databaseServer.ConnectionString);
            await using var constraintLifetime = constraint.ConfigureAwait(false);
            await constraint.Database.ExecuteSqlRawAsync(
                "ALTER TABLE mail_queue_messages ADD CONSTRAINT ck_test_keep_legacy_queue CHECK (raw_message IS NOT NULL)").ConfigureAwait(false);
        }

        try
        {
            {
                var migration = CreateContext(databaseServer.ConnectionString);
                await using var migrationLifetime = migration.ConfigureAwait(false);
                await Assert.ThrowsExactlyAsync<DbUpdateException>(() =>
                    new MailQueueLargeObjectMigrationService(
                        migration,
                        store,
                        NullLogger<MailQueueLargeObjectMigrationService>.Instance)
                        .MigrateAsync()).ConfigureAwait(false);
            }

            var verification = CreateContext(databaseServer.ConnectionString);
            await using var verificationLifetime = verification.ConfigureAwait(false);
            var legacy = await verification.MailQueueMessages.AsNoTracking()
                .SingleAsync(message => message.Id == queueId).ConfigureAwait(false);
            Assert.AreEqual(RawMessage, legacy.RawMessage, StringComparer.Ordinal);
            Assert.IsNull(legacy.RawMessageObjectName);
            Assert.HasCount(0, await GetBlobNamesAsync(container, queueId).ConfigureAwait(false));
        }
        finally
        {
            await container.DeleteIfExistsAsync().ConfigureAwait(false);
        }
    }

    [TestMethod]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "MA0051", Justification = "The SubmissionAndWorkerRetryUseReferenceOnlyQueueContent scenario keeps one fixture's ordered setup, operation and invariant assertions together.")]
    public async Task SubmissionAndWorkerRetryUseReferenceOnlyQueueContent()
    {
        var databaseServer = (await RequirePostgresAsync().ConfigureAwait(false));
        await using var databaseServerLifetime = databaseServer.ConfigureAwait(false);
        var connectionString = RequireAzureBlobConnection();
        var serviceClient = new BlobServiceClient(connectionString);
        var containerName = $"mk8-queue-{Guid.NewGuid():N}";
        var container = serviceClient.GetBlobContainerClient(containerName);
        var store = CreateStore(serviceClient, containerName);
        await CreateSchemaAsync(databaseServer.ConnectionString).ConfigureAwait(false);
        {
            var migrationContext = CreateContext(databaseServer.ConnectionString);
            await using var migrationContextLifetime = migrationContext.ConfigureAwait(false);
            await new MailQueueLargeObjectMigrationService(
                migrationContext,
                store,
                NullLogger<MailQueueLargeObjectMigrationService>.Instance)
                .MigrateAsync().ConfigureAwait(false);
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
            var provider = services.BuildServiceProvider();
            await using var providerLifetime = provider.ConfigureAwait(false);

            var failedQueueId = Guid.CreateVersion7();
            {
                var constraintContext = CreateContext(databaseServer.ConnectionString);
                await using var constraintContextLifetime = constraintContext.ConfigureAwait(false);
                await constraintContext.Database.ExecuteSqlRawAsync(
                    """
                    ALTER TABLE mail_queue_messages
                        ADD CONSTRAINT ck_test_reject_rollback_sender
                        CHECK (envelope_sender <> 'rollback@example.test')
                    """).ConfigureAwait(false);
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
                            null))).ConfigureAwait(false);
            }
            Assert.HasCount(0, await GetBlobNamesAsync(container, failedQueueId).ConfigureAwait(false));
            {
                var constraintContext = CreateContext(databaseServer.ConnectionString);
                await using var constraintContextLifetime = constraintContext.ConfigureAwait(false);
                await constraintContext.Database.ExecuteSqlRawAsync(
                    """
                    ALTER TABLE mail_queue_messages
                        DROP CONSTRAINT ck_test_reject_rollback_sender
                    """).ConfigureAwait(false);
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
                        null)).ConfigureAwait(false);
            }

            {
                var queuedContext = CreateContext(databaseServer.ConnectionString);
                await using var queuedContextLifetime = queuedContext.ConfigureAwait(false);
                var queued = await queuedContext.MailQueueMessages.AsNoTracking()
                    .SingleAsync(message => message.Id == queueId).ConfigureAwait(false);
                Assert.IsNull(queued.RawMessage);
                Assert.AreEqual(LargeObjectProviders.AzureBlob, queued.RawMessageObjectProvider, StringComparer.Ordinal);
                Assert.AreEqual(RawMessage.Length, queued.RawMessageSizeBytes);
            }
            using
                        var worker = new MailQueueWorker(
                            provider.GetRequiredService<IServiceScopeFactory>(),
                            environment,
                            TimeProvider.System,
                            NullLogger<MailQueueWorker>.Instance);
            Assert.IsTrue(await worker.ProcessNextAsync(CancellationToken.None).ConfigureAwait(false));
            Assert.AreEqual(RawMessage, scanner.RawMessage, StringComparer.Ordinal);

            var verification = CreateContext(databaseServer.ConnectionString);
            await using var verificationLifetime = verification.ConfigureAwait(false);
            var retained = await verification.MailQueueMessages.AsNoTracking()
                .SingleAsync(message => message.Id == queueId).ConfigureAwait(false);
            Assert.IsNull(retained.RawMessage);
            Assert.AreEqual(MailQueueStates.Pending, retained.State, StringComparer.Ordinal);
            Assert.AreEqual(1, retained.AttemptCount);
            var names = await GetBlobNamesAsync(container, queueId).ConfigureAwait(false);
            Assert.HasCount(1, names);
            Assert.AreEqual(
                $"objects/mail/queue/{queueId:N}/raw.eml",
                names[0], StringComparer.Ordinal);

            {
                var completion = CreateContext(databaseServer.ConnectionString);
                await using var completionLifetime = completion.ConfigureAwait(false);
                var completed = await completion.MailQueueMessages
                    .SingleAsync(message => message.Id == queueId).ConfigureAwait(false);
                completed.State = MailQueueStates.Completed;
                completed.CompletedAt = DateTime.UtcNow.AddDays(-2);
                await completion.SaveChangesAsync().ConfigureAwait(false);
                await completion.Database.ExecuteSqlRawAsync(
                    """
                    CREATE FUNCTION reject_queue_retention() RETURNS trigger AS $$
                    BEGIN
                        RAISE EXCEPTION 'test rollback of queue retention';
                    END;
                    $$ LANGUAGE plpgsql;
                    CREATE TRIGGER reject_queue_retention
                    BEFORE DELETE ON mail_queue_messages
                    FOR EACH ROW EXECUTE FUNCTION reject_queue_retention();
                    """).ConfigureAwait(false);
            }
            await Assert.ThrowsExactlyAsync<DbUpdateException>(() => worker.CleanupCompletedAsync(CancellationToken.None)).ConfigureAwait(false);
            {
                var failedCleanup = CreateContext(databaseServer.ConnectionString);
                await using var failedCleanupLifetime = failedCleanup.ConfigureAwait(false);
                Assert.AreEqual(1, await failedCleanup.MailQueueMessages.CountAsync().ConfigureAwait(false));
                Assert.HasCount(1, await GetBlobNamesAsync(container, queueId).ConfigureAwait(false));
                await failedCleanup.Database.ExecuteSqlRawAsync(
                    "DROP TRIGGER reject_queue_retention ON mail_queue_messages; DROP FUNCTION reject_queue_retention();").ConfigureAwait(false);
            }
            await worker.CleanupCompletedAsync(CancellationToken.None).ConfigureAwait(false);
            var cleanupVerification = CreateContext(databaseServer.ConnectionString);
            await using var cleanupVerificationLifetime = cleanupVerification.ConfigureAwait(false);
            Assert.AreEqual(0, await cleanupVerification.MailQueueMessages.CountAsync().ConfigureAwait(false));
            Assert.HasCount(0, await GetBlobNamesAsync(container, queueId).ConfigureAwait(false));
        }
        finally
        {
            await container.DeleteIfExistsAsync().ConfigureAwait(false);
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
        var context = CreateContext(connectionString);
        await using var contextLifetime = context.ConfigureAwait(false);
        await context.Database.EnsureCreatedAsync().ConfigureAwait(false);
    }

    private static async Task InsertLegacyAsync(
        string connectionString,
        Guid queueId,
        string rawMessage)
    {
        var connection = new NpgsqlConnection(connectionString);
        await using var connectionLifetime = connection.ConfigureAwait(false);
        await connection.OpenAsync().ConfigureAwait(false);
        var command = connection.CreateCommand();
        await using var commandLifetime = command.ConfigureAwait(false);
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
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
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
                           CancellationToken.None).ConfigureAwait(false))
        {
            names.Add(item.Name);
        }
        names.Sort(StringComparer.Ordinal);
        return names;
    }

    private static async Task<PostgresTestDatabase> RequirePostgresAsync()
    {
        var database = await PostgresTestDatabase.TryCreateAsync().ConfigureAwait(false);
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
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1812", Justification = "This fixture implementation is activated through the test service provider's registered generic interface mapping.")]
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
            string folderName = "Inbox",
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
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1812", Justification = "This fixture implementation is activated through the test service provider's registered generic interface mapping.")]
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
