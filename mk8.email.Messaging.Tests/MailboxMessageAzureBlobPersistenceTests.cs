using System.Text;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using mk8.email.Application.Services;
using mk8.email.Contracts.Enums;
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
internal sealed class MailboxMessageAzureBlobPersistenceTests
{
    [TestMethod]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "MA0051", Justification = "The ConcurrentLegacyMigrationExternalizesMimeAndEnforcesReferenceOnlyRows scenario keeps one fixture's ordered setup, operation and invariant assertions together.")]
    public async Task ConcurrentLegacyMigrationExternalizesMimeAndEnforcesReferenceOnlyRows()
    {
        var databaseServer = (await RequirePostgresAsync().ConfigureAwait(false));
        await using var databaseServerLifetime = databaseServer.ConfigureAwait(false);
        var serviceClient = new BlobServiceClient(RequireAzureBlobConnection());
        var containerName = $"mk8-mailbox-{Guid.NewGuid():N}";
        var container = serviceClient.GetBlobContainerClient(containerName);
        var store = CreateStore(serviceClient, containerName);
        var rawMessageId = Guid.CreateVersion7();
        var reconstructedMessageId = Guid.CreateVersion7();
        var raw = BuildMessageWithLargeAttachment();
        const string reconstructedHeaders =
            "From: legacy@example.test\r\nTo: mailbox@example.test\r\nSubject: reconstructed";
        const string reconstructedBody = "legacy body\r\n";
        var reconstructedRaw = Encoding.Latin1.GetBytes(
            reconstructedHeaders + "\r\n\r\n" + reconstructedBody);
        await CreateSchemaAndLegacyRowsAsync(
            databaseServer.ConnectionString,
            rawMessageId,
            raw,
            reconstructedMessageId,
            reconstructedHeaders,
            reconstructedBody).ConfigureAwait(false);

        try
        {
            async Task MigrateAsync()
            {
                var context = CreateContext(databaseServer.ConnectionString);
                await using var contextLifetime = context.ConfigureAwait(false);
                var migrationEffects = CreateEffects(store);
                await new MailboxMessageLargeObjectMigrationService(
                    context,
                    store,
                    new MailboxMessageContentService(store, migrationEffects),
                    NullLogger<MailboxMessageLargeObjectMigrationService>.Instance)
                    .MigrateAsync().ConfigureAwait(false);
            }

            await Task.WhenAll(MigrateAsync(), MigrateAsync()).ConfigureAwait(false);

            var verification = CreateContext(databaseServer.ConnectionString);
            await using var verificationLifetime = verification.ConfigureAwait(false);
            var migrated = await verification.Emails.AsNoTracking()
                .OrderBy(message => message.Uid)
                .ToListAsync().ConfigureAwait(false);
            Assert.HasCount(2, migrated);
            Assert.IsTrue(migrated.All(message => message.RawMessage is null));
            Assert.IsTrue(migrated.All(message => string.Equals(message.RawMessageObjectProvider, LargeObjectProviders.AzureBlob, StringComparison.Ordinal)));
            Assert.IsTrue(migrated.All(message => message.Body.Length <= 65_536));
            Assert.IsTrue(migrated.All(message => (message.RawHeaders?.Length ?? 0) <= 65_536));

            var externalized = migrated.Single(message => message.Id == rawMessageId);
            StringAssert.Contains(externalized.Body, "visible message text", StringComparison.Ordinal);
            Assert.IsFalse(externalized.Body.Contains("attachment-sentinel", StringComparison.Ordinal));
            var effects = new LargeObjectTransactionEffects(
                store,
                NullLogger<LargeObjectTransactionEffects>.Instance);
            var content = new MailboxMessageContentService(store, effects);
            CollectionAssert.AreEqual(raw, await content.ReadAsync(externalized, CancellationToken.None).ConfigureAwait(false));
            CollectionAssert.AreEqual(
                reconstructedRaw,
                await content.ReadAsync(
                    migrated.Single(message => message.Id == reconstructedMessageId),
                    CancellationToken.None).ConfigureAwait(false));

            var exception = await Assert.ThrowsExactlyAsync<PostgresException>(() =>
                InsertInlineMessageAsync(
                    databaseServer.ConnectionString,
                    Guid.CreateVersion7(),
                    externalized.FolderId,
                    uid: 3,
                    raw)).ConfigureAwait(false);
            Assert.AreEqual(PostgresErrorCodes.CheckViolation, exception.SqlState, StringComparer.Ordinal);
        }
        finally
        {
            await container.DeleteIfExistsAsync().ConfigureAwait(false);
        }
    }

    [TestMethod]
    public async Task FailedLegacyMigrationRetainsMailboxRowAndRemovesCreatedBlob()
    {
        var databaseServer = (await RequirePostgresAsync().ConfigureAwait(false));
        await using var databaseServerLifetime = databaseServer.ConfigureAwait(false);
        var serviceClient = new BlobServiceClient(RequireAzureBlobConnection());
        var containerName = $"mk8-mailbox-{Guid.NewGuid():N}";
        var container = serviceClient.GetBlobContainerClient(containerName);
        var store = CreateStore(serviceClient, containerName);
        var messageId = Guid.CreateVersion7();
        var raw = Encoding.Latin1.GetBytes(
            "From: sender@example.test\r\nTo: mailbox@example.test\r\nSubject: legacy\r\n\r\nbody\r\n");
        {
            var setup = CreateContext(databaseServer.ConnectionString);
            await using var setupLifetime = setup.ConfigureAwait(false);
            await setup.Database.EnsureCreatedAsync().ConfigureAwait(false);
            var folderId = await SeedMailboxAsync(setup).ConfigureAwait(false);
            var message = CreateMessage(messageId, folderId, 1);
            message.RawMessage = raw;
            message.SizeBytes = raw.Length;
            await (setup.Emails.AddAsync(message)).ConfigureAwait(false);
            await setup.SaveChangesAsync().ConfigureAwait(false);
            await setup.Database.ExecuteSqlRawAsync(
                "ALTER TABLE emails ADD CONSTRAINT ck_test_keep_legacy_mail CHECK (raw_message IS NOT NULL)").ConfigureAwait(false);
        }

        try
        {
            {
                var migration = CreateContext(databaseServer.ConnectionString);
                await using var migrationLifetime = migration.ConfigureAwait(false);
                var effects = CreateEffects(store);
                await Assert.ThrowsExactlyAsync<DbUpdateException>(() =>
                    new MailboxMessageLargeObjectMigrationService(
                        migration,
                        store,
                        new MailboxMessageContentService(store, effects),
                        NullLogger<MailboxMessageLargeObjectMigrationService>.Instance)
                        .MigrateAsync()).ConfigureAwait(false);
            }

            var verification = CreateContext(databaseServer.ConnectionString);
            await using var verificationLifetime = verification.ConfigureAwait(false);
            var legacy = await verification.Emails.AsNoTracking()
                .SingleAsync(message => message.Id == messageId).ConfigureAwait(false);
            CollectionAssert.AreEqual(raw, legacy.RawMessage);
            Assert.IsNull(legacy.RawMessageObjectName);
            Assert.HasCount(0, await GetBlobNamesAsync(container, messageId).ConfigureAwait(false));
        }
        finally
        {
            await container.DeleteIfExistsAsync().ConfigureAwait(false);
        }
    }

    [TestMethod]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "MA0051", Justification = "The TransactionEffectsPreserveRollbackAndDeleteOnlyCommittedMessages scenario keeps one fixture's ordered setup, operation and invariant assertions together.")]
    public async Task TransactionEffectsPreserveRollbackAndDeleteOnlyCommittedMessages()
    {
        var databaseServer = (await RequirePostgresAsync().ConfigureAwait(false));
        await using var databaseServerLifetime = databaseServer.ConfigureAwait(false);
        var serviceClient = new BlobServiceClient(RequireAzureBlobConnection());
        var containerName = $"mk8-mailbox-tx-{Guid.NewGuid():N}";
        var container = serviceClient.GetBlobContainerClient(containerName);
        var store = CreateStore(serviceClient, containerName);
        var folderId = await CreateEmptyMigratedSchemaAsync(databaseServer.ConnectionString, store).ConfigureAwait(false);
        var raw = Encoding.Latin1.GetBytes(
            "From: sender@example.test\r\nTo: mailbox@example.test\r\nSubject: transaction\r\n\r\nbody\r\n");

        try
        {
            var rolledBackId = Guid.CreateVersion7();
            {
                var context = CreateContext(databaseServer.ConnectionString);
                await using var contextLifetime = context.ConfigureAwait(false);
                var effects = CreateEffects(store);
                var content = new MailboxMessageContentService(store, effects);
                var marker = effects.Mark();
                var transaction = (await context.Database.BeginTransactionAsync().ConfigureAwait(false));
                await using var transactionLifetime = transaction.ConfigureAwait(false);
                var message = CreateMessage(rolledBackId, folderId, 1);
                await content.SetAsync(message, raw, CancellationToken.None).ConfigureAwait(false);
                await (context.Emails.AddAsync(message)).ConfigureAwait(false);
                await context.SaveChangesAsync().ConfigureAwait(false);
                await transaction.RollbackAsync().ConfigureAwait(false);
                await effects.RollbackAsync(marker).ConfigureAwait(false);
            }
            Assert.HasCount(0, await GetBlobNamesAsync(container, rolledBackId).ConfigureAwait(false));

            var committedId = Guid.CreateVersion7();
            {
                var context = CreateContext(databaseServer.ConnectionString);
                await using var contextLifetime = context.ConfigureAwait(false);
                var effects = CreateEffects(store);
                var content = new MailboxMessageContentService(store, effects);
                var marker = effects.Mark();
                var transaction = (await context.Database.BeginTransactionAsync().ConfigureAwait(false));
                await using var transactionLifetime = transaction.ConfigureAwait(false);
                var message = CreateMessage(committedId, folderId, 1);
                await content.SetAsync(message, raw, CancellationToken.None).ConfigureAwait(false);
                await (context.Emails.AddAsync(message)).ConfigureAwait(false);
                await context.SaveChangesAsync().ConfigureAwait(false);
                await transaction.CommitAsync().ConfigureAwait(false);
                await effects.CommitAsync(marker).ConfigureAwait(false);
            }
            Assert.HasCount(1, await GetBlobNamesAsync(container, committedId).ConfigureAwait(false));

            {
                var context = CreateContext(databaseServer.ConnectionString);
                await using var contextLifetime = context.ConfigureAwait(false);
                var effects = CreateEffects(store);
                var content = new MailboxMessageContentService(store, effects);
                var marker = effects.Mark();
                var transaction = (await context.Database.BeginTransactionAsync().ConfigureAwait(false));
                await using var transactionLifetime = transaction.ConfigureAwait(false);
                var message = await context.Emails.SingleAsync(candidate => candidate.Id == committedId).ConfigureAwait(false);
                content.DeleteOnCommit(message);
                context.Emails.Remove(message);
                await context.SaveChangesAsync().ConfigureAwait(false);
                await transaction.CommitAsync().ConfigureAwait(false);
                await effects.CommitAsync(marker).ConfigureAwait(false);
            }
            Assert.HasCount(0, await GetBlobNamesAsync(container, committedId).ConfigureAwait(false));
        }
        finally
        {
            await container.DeleteIfExistsAsync().ConfigureAwait(false);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task EmailServiceCommitsBlobBackedMetadataAndReplaysIdempotently(bool sentCopy)
    {
        var server = (await RequirePostgresAsync().ConfigureAwait(false));
        await using var serverLifetime = server.ConfigureAwait(false);
        var client = new BlobServiceClient(RequireAzureBlobConnection());
        var containerName = $"mk8-email-service-{Guid.NewGuid():N}";
        var container = client.GetBlobContainerClient(containerName);
        var store = CreateStore(client, containerName);
        var folderId = await PrepareEmailServiceFolderAsync(server.ConnectionString, store, sentCopy).ConfigureAwait(false);
        var deliveryId = Guid.CreateVersion7();
        var raw = BuildEmailServiceMessage(sentCopy);

        try
        {
            {
                var context = CreateContext(server.ConnectionString);
                await using var contextLifetime = context.ConfigureAwait(false);
                var effects = CreateEffects(store);
                var content = new MailboxMessageContentService(store, effects);
                var service = new EmailService(context, content, effects);
                Assert.IsTrue(await StoreEmailAsync(service, sentCopy, raw, deliveryId).ConfigureAwait(false));
                Assert.IsTrue(await StoreEmailAsync(service, sentCopy, "replacement body", deliveryId).ConfigureAwait(false));
            }
            var verification = CreateContext(server.ConnectionString);
            await using var verificationLifetime = verification.ConfigureAwait(false);
            var email = await verification.Emails.AsNoTracking().SingleAsync().ConfigureAwait(false);
            VerifyEmailServiceMetadata(email, sentCopy, deliveryId, folderId);
            var contentReader = new MailboxMessageContentService(store, CreateEffects(store));
            CollectionAssert.AreEqual(Encoding.Latin1.GetBytes(raw),
                await contentReader.ReadAsync(email, CancellationToken.None).ConfigureAwait(false));
            Assert.HasCount(1, await GetBlobNamesAsync(container).ConfigureAwait(false));
            var folder = await verification.Folders.AsNoTracking().SingleAsync(item => item.Id == folderId).ConfigureAwait(false);
            Assert.AreEqual(4, folder.NextUid);
            Assert.AreEqual(3L, folder.HighestModSeq);
        }
        finally
        {
            await container.DeleteIfExistsAsync().ConfigureAwait(false);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task EmailServiceDatabaseFailureRollsBackCountersAndRemovesCreatedBlob(bool sentCopy)
    {
        var server = (await RequirePostgresAsync().ConfigureAwait(false));
        await using var serverLifetime = server.ConfigureAwait(false);
        var client = new BlobServiceClient(RequireAzureBlobConnection());
        var containerName = $"mk8-email-failure-{Guid.NewGuid():N}";
        var container = client.GetBlobContainerClient(containerName);
        var store = CreateStore(client, containerName);
        var folderId = await PrepareEmailServiceFolderAsync(server.ConnectionString, store, sentCopy).ConfigureAwait(false);
        {
            var setup = CreateContext(server.ConnectionString);
            await using var setupLifetime = setup.ConfigureAwait(false);
            await setup.Database.ExecuteSqlRawAsync(
                "ALTER TABLE emails ADD CONSTRAINT ck_test_reject_email CHECK (false)").ConfigureAwait(false);
        }

        try
        {
            {
                var context = CreateContext(server.ConnectionString);
                await using var contextLifetime = context.ConfigureAwait(false);
                var effects = CreateEffects(store);
                var service = new EmailService(context, new MailboxMessageContentService(store, effects), effects);
                var exception = await Assert.ThrowsExactlyAsync<DbUpdateException>(() =>
                    StoreEmailAsync(service, sentCopy, BuildEmailServiceMessage(sentCopy), Guid.CreateVersion7())).ConfigureAwait(false);
                Assert.AreEqual(PostgresErrorCodes.CheckViolation,
                    Assert.IsInstanceOfType<PostgresException>(exception.InnerException).SqlState, StringComparer.Ordinal);
            }
            var verification = CreateContext(server.ConnectionString);
            await using var verificationLifetime = verification.ConfigureAwait(false);
            Assert.AreEqual(0, await verification.Emails.CountAsync().ConfigureAwait(false));
            var folder = await verification.Folders.AsNoTracking().SingleAsync(item => item.Id == folderId).ConfigureAwait(false);
            Assert.AreEqual(3, folder.NextUid);
            Assert.AreEqual(2L, folder.HighestModSeq);
            Assert.HasCount(0, await GetBlobNamesAsync(container).ConfigureAwait(false));
        }
        finally
        {
            await container.DeleteIfExistsAsync().ConfigureAwait(false);
        }
    }

    private static async Task<Guid> PrepareEmailServiceFolderAsync(
        string connectionString,
        AzureBlobLargeObjectStore store,
        bool sentCopy)
    {
        var inboxFolderId = await CreateEmptyMigratedSchemaAsync(connectionString, store).ConfigureAwait(false);
        var context = CreateContext(connectionString);
        await using var contextLifetime = context.ConfigureAwait(false);
        var inboxFolder = await context.Folders.SingleAsync(folder => folder.Id == inboxFolderId).ConfigureAwait(false);
        inboxFolder.Name = DefaultFolders.Inbox;
        var selected = inboxFolder;
        if (sentCopy)
        {
            selected = new FolderDB
            {
                Id = Guid.CreateVersion7(),
                InboxId = inboxFolder.InboxId,
                Name = DefaultFolders.Sent,
                NextUid = 3,
                HighestModSeq = 2,
            };
            await (context.Folders.AddAsync(selected)).ConfigureAwait(false);
        }
        await context.SaveChangesAsync().ConfigureAwait(false);
        return selected.Id;
    }

    private static Task<bool> StoreEmailAsync(EmailService service, bool sentCopy, string raw, Guid deliveryId) =>
        sentCopy
            ? service.SaveSentCopyAsync("mailbox@mailbox.example.test", raw, deliveryId)
            : service.DeliverAsync("sender@example.test", "mailbox@mailbox.example.test", raw,
                queueDeliveryId: deliveryId, flags: ["\\Flagged", "tag"]);

    private static string BuildEmailServiceMessage(bool sentCopy)
    {
        var sender = sentCopy ? "mailbox@mailbox.example.test" : "sender@example.test";
        return Encoding.Latin1.GetString(BuildMessageWithLargeAttachment())
            .Replace("From: sender@example.test", $"From: {sender}", StringComparison.Ordinal)
            .Replace("To: mailbox@mailbox.example.test", "To: header@example.test", StringComparison.Ordinal)
            .Replace("Subject: attachment migration",
                "Subject: " + new string('s', 1001) + "\r\nMessage-ID: <email-service@example.test>\r\n"
                    + "In-Reply-To: <parent@example.test>\r\nCc: copy@example.test",
                StringComparison.Ordinal);
    }

    private static void VerifyEmailServiceMetadata(EmailDB email, bool sentCopy, Guid deliveryId, Guid folderId)
    {
        Assert.AreEqual(folderId, email.FolderId);
        Assert.AreEqual(deliveryId, email.QueueDeliveryId);
        Assert.AreEqual(3, email.Uid);
        Assert.AreEqual(3L, email.ModSeq);
        Assert.AreEqual(sentCopy, email.IsRead);
        Assert.AreEqual(!sentCopy, email.IsFlagged);
        CollectionAssert.AreEqual(sentCopy ? Array.Empty<string>() : ExpectedVector1, email.Keywords);
        Assert.AreEqual(sentCopy ? "header@example.test" : "mailbox@mailbox.example.test", email.Recipient, StringComparer.Ordinal);
        Assert.AreEqual(new string('s', 998), email.Subject, StringComparer.Ordinal);
        Assert.AreEqual("<email-service@example.test>", email.MessageId, StringComparer.Ordinal);
        Assert.AreEqual("<parent@example.test>", email.InReplyTo, StringComparer.Ordinal);
        Assert.AreEqual("copy@example.test", email.Cc, StringComparer.Ordinal);
        Assert.IsFalse(string.IsNullOrEmpty(email.EmailObjectId));
        Assert.IsFalse(string.IsNullOrEmpty(email.ThreadObjectId));
        Assert.IsNull(email.RawMessage);
        Assert.AreEqual(LargeObjectProviders.AzureBlob, email.RawMessageObjectProvider, StringComparer.Ordinal);
        StringAssert.Contains(email.Body, "visible message text", StringComparison.Ordinal);
        Assert.IsFalse(email.Body.Contains("attachment-sentinel", StringComparison.Ordinal));
    }

    private static async Task CreateSchemaAndLegacyRowsAsync(
        string connectionString,
        Guid rawMessageId,
        byte[] raw,
        Guid reconstructedMessageId,
        string reconstructedHeaders,
        string reconstructedBody)
    {
        var context = CreateContext(connectionString);
        await using var contextLifetime = context.ConfigureAwait(false);
        await context.Database.EnsureCreatedAsync().ConfigureAwait(false);
        var folderId = await SeedMailboxAsync(context).ConfigureAwait(false);
        var externalized = CreateMessage(rawMessageId, folderId, 1);
        externalized.RawMessage = raw;
        externalized.SizeBytes = raw.Length;
        await (context.Emails.AddAsync(externalized)).ConfigureAwait(false);
        var reconstructed = CreateMessage(reconstructedMessageId, folderId, 2);
        reconstructed.RawHeaders = reconstructedHeaders;
        reconstructed.Body = reconstructedBody;
        await (context.Emails.AddAsync(reconstructed)).ConfigureAwait(false);
        await context.SaveChangesAsync().ConfigureAwait(false);
    }

    private static async Task<Guid> CreateEmptyMigratedSchemaAsync(
        string connectionString,
        AzureBlobLargeObjectStore store)
    {
        {
            var context = CreateContext(connectionString);
            await using var contextLifetime = context.ConfigureAwait(false);
            await context.Database.EnsureCreatedAsync().ConfigureAwait(false);
            var folderId = await SeedMailboxAsync(context).ConfigureAwait(false);
            var effects = CreateEffects(store);
            await new MailboxMessageLargeObjectMigrationService(
                context,
                store,
                new MailboxMessageContentService(store, effects),
                NullLogger<MailboxMessageLargeObjectMigrationService>.Instance)
                .MigrateAsync().ConfigureAwait(false);
            return folderId;
        }
    }

    private static async Task<Guid> SeedMailboxAsync(EmailDbContext context)
    {
        var company = new CompanyDB
        {
            Id = Guid.CreateVersion7(),
            Name = "Mailbox Azure test",
        };
        var address = new AddressDB
        {
            Id = Guid.CreateVersion7(),
            Domain = "mailbox.example.test",
            IsActive = true,
            Company = company,
        };
        var user = new UserDB
        {
            Id = Guid.CreateVersion7(),
            Username = "mailbox@mailbox.example.test",
            PasswordHash = "unused",
            Role = "User",
            Company = company,
        };
        var inbox = new InboxDB
        {
            Id = Guid.CreateVersion7(),
            Name = "mailbox",
            Address = address,
            Owner = user,
        };
        var folder = new FolderDB
        {
            Id = Guid.CreateVersion7(),
            Name = "INBOX",
            Inbox = inbox,
            NextUid = 3,
            HighestModSeq = 2,
        };
        await context.Folders.AddAsync(folder).ConfigureAwait(false);
        await context.SaveChangesAsync().ConfigureAwait(false);
        return folder.Id;
    }

    private static EmailDB CreateMessage(Guid id, Guid folderId, int uid) => new()
    {
        Id = id,
        Sender = "sender@example.test",
        Recipient = "mailbox@mailbox.example.test",
        Subject = "External message",
        Body = string.Empty,
        SizeBytes = 0,
        Uid = uid,
        ModSeq = uid,
        FolderId = folderId,
        ReceivedAt = DateTime.UtcNow,
    };

    private static byte[] BuildMessageWithLargeAttachment()
    {
        var attachment = string.Concat(Enumerable.Repeat("attachment-sentinel-", 5_000));
        return Encoding.Latin1.GetBytes(
            "From: sender@example.test\r\n" +
            "To: mailbox@mailbox.example.test\r\n" +
            "Subject: attachment migration\r\n" +
            "MIME-Version: 1.0\r\n" +
            "Content-Type: multipart/mixed; boundary=mk8-boundary\r\n\r\n" +
            "--mk8-boundary\r\n" +
            "Content-Type: text/plain; charset=us-ascii\r\n\r\n" +
            "visible message text\r\n" +
            "--mk8-boundary\r\n" +
            "Content-Type: application/octet-stream\r\n" +
            "Content-Disposition: attachment; filename=large.bin\r\n" +
            "Content-Transfer-Encoding: 8bit\r\n\r\n" +
            attachment + "\r\n" +
            "--mk8-boundary--\r\n");
    }

    private static async Task InsertInlineMessageAsync(
        string connectionString,
        Guid id,
        Guid folderId,
        int uid,
        byte[] raw)
    {
        var connection = new NpgsqlConnection(connectionString);
        await using var connectionLifetime = connection.ConfigureAwait(false);
        await connection.OpenAsync().ConfigureAwait(false);
        var command = connection.CreateCommand();
        await using var commandLifetime = command.ConfigureAwait(false);
        command.CommandText =
            """
            INSERT INTO emails (
                id, sender, recipient, subject, body, raw_message, size_bytes,
                is_read, is_deleted, is_flagged, is_draft, is_answered, keywords,
                email_object_id, received_at, uid, mod_seq, folder_id)
            VALUES (
                @id, 'sender@example.test', 'mailbox@mailbox.example.test', 'inline', '',
                @raw, @size, false, false, false, false, false, ARRAY[]::text[],
                @email_object_id, @received_at, @uid, @uid, @folder_id)
            """;
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("raw", raw);
        command.Parameters.AddWithValue("size", raw.Length);
        command.Parameters.AddWithValue("email_object_id", id.ToString("N"));
        command.Parameters.AddWithValue("received_at", DateTime.UtcNow);
        command.Parameters.AddWithValue("uid", uid);
        command.Parameters.AddWithValue("folder_id", folderId);
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private static LargeObjectTransactionEffects CreateEffects(AzureBlobLargeObjectStore store) =>
        new(store, NullLogger<LargeObjectTransactionEffects>.Instance);

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

    private static async Task<List<string>> GetBlobNamesAsync(
        BlobContainerClient container,
        Guid? messageId = null)
    {
        var prefix = messageId is { } id
            ? $"objects/mail/messages/{id:N}/"
            : "objects/mail/messages/";
        var names = new List<string>();
        await foreach (var item in container.GetBlobsAsync(
                           BlobTraits.None,
                           BlobStates.None,
                           prefix,
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
    private static readonly string[] ExpectedVector1 = new[] { "tag" };
}
