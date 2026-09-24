using System.Text;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using mk8.email.Application.Services;
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
public sealed class MailboxMessageAzureBlobPersistenceTests
{
    [TestMethod]
    public async Task ConcurrentLegacyMigrationExternalizesMimeAndEnforcesReferenceOnlyRows()
    {
        await using var databaseServer = await RequirePostgresAsync();
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
            reconstructedBody);

        try
        {
            async Task MigrateAsync()
            {
                await using var context = CreateContext(databaseServer.ConnectionString);
                var effects = CreateEffects(store);
                await new MailboxMessageLargeObjectMigrationService(
                    context,
                    store,
                    new MailboxMessageContentService(store, effects),
                    NullLogger<MailboxMessageLargeObjectMigrationService>.Instance)
                    .MigrateAsync();
            }

            await Task.WhenAll(MigrateAsync(), MigrateAsync());

            await using var verification = CreateContext(databaseServer.ConnectionString);
            var migrated = await verification.Emails.AsNoTracking()
                .OrderBy(message => message.Uid)
                .ToListAsync();
            Assert.HasCount(2, migrated);
            Assert.IsTrue(migrated.All(message => message.RawMessage is null));
            Assert.IsTrue(migrated.All(message =>
                message.RawMessageObjectProvider == LargeObjectProviders.AzureBlob));
            Assert.IsTrue(migrated.All(message => message.Body.Length <= 65_536));
            Assert.IsTrue(migrated.All(message => (message.RawHeaders?.Length ?? 0) <= 65_536));

            var externalized = migrated.Single(message => message.Id == rawMessageId);
            StringAssert.Contains(externalized.Body, "visible message text");
            Assert.IsFalse(externalized.Body.Contains("attachment-sentinel", StringComparison.Ordinal));
            var effects = new LargeObjectTransactionEffects(
                store,
                NullLogger<LargeObjectTransactionEffects>.Instance);
            var content = new MailboxMessageContentService(store, effects);
            CollectionAssert.AreEqual(raw, await content.ReadAsync(externalized, CancellationToken.None));
            CollectionAssert.AreEqual(
                reconstructedRaw,
                await content.ReadAsync(
                    migrated.Single(message => message.Id == reconstructedMessageId),
                    CancellationToken.None));

            var exception = await Assert.ThrowsExactlyAsync<PostgresException>(() =>
                InsertInlineMessageAsync(
                    databaseServer.ConnectionString,
                    Guid.CreateVersion7(),
                    externalized.FolderId,
                    uid: 3,
                    raw));
            Assert.AreEqual(PostgresErrorCodes.CheckViolation, exception.SqlState);
        }
        finally
        {
            await container.DeleteIfExistsAsync();
        }
    }

    [TestMethod]
    public async Task FailedLegacyMigrationRetainsMailboxRowAndRemovesCreatedBlob()
    {
        await using var databaseServer = await RequirePostgresAsync();
        var serviceClient = new BlobServiceClient(RequireAzureBlobConnection());
        var containerName = $"mk8-mailbox-{Guid.NewGuid():N}";
        var container = serviceClient.GetBlobContainerClient(containerName);
        var store = CreateStore(serviceClient, containerName);
        var messageId = Guid.CreateVersion7();
        var raw = Encoding.Latin1.GetBytes(
            "From: sender@example.test\r\nTo: mailbox@example.test\r\nSubject: legacy\r\n\r\nbody\r\n");
        await using (var setup = CreateContext(databaseServer.ConnectionString))
        {
            await setup.Database.EnsureCreatedAsync();
            var folderId = await SeedMailboxAsync(setup);
            var message = CreateMessage(messageId, folderId, 1);
            message.RawMessage = raw;
            message.SizeBytes = raw.Length;
            setup.Emails.Add(message);
            await setup.SaveChangesAsync();
            await setup.Database.ExecuteSqlRawAsync(
                "ALTER TABLE emails ADD CONSTRAINT ck_test_keep_legacy_mail CHECK (raw_message IS NOT NULL)");
        }

        try
        {
            await using (var migration = CreateContext(databaseServer.ConnectionString))
            {
                var effects = CreateEffects(store);
                await Assert.ThrowsExactlyAsync<DbUpdateException>(() =>
                    new MailboxMessageLargeObjectMigrationService(
                        migration,
                        store,
                        new MailboxMessageContentService(store, effects),
                        NullLogger<MailboxMessageLargeObjectMigrationService>.Instance)
                        .MigrateAsync());
            }

            await using var verification = CreateContext(databaseServer.ConnectionString);
            var legacy = await verification.Emails.AsNoTracking()
                .SingleAsync(message => message.Id == messageId);
            CollectionAssert.AreEqual(raw, legacy.RawMessage);
            Assert.IsNull(legacy.RawMessageObjectName);
            Assert.HasCount(0, await GetBlobNamesAsync(container, messageId));
        }
        finally
        {
            await container.DeleteIfExistsAsync();
        }
    }

    [TestMethod]
    public async Task TransactionEffectsPreserveRollbackAndDeleteOnlyCommittedMessages()
    {
        await using var databaseServer = await RequirePostgresAsync();
        var serviceClient = new BlobServiceClient(RequireAzureBlobConnection());
        var containerName = $"mk8-mailbox-tx-{Guid.NewGuid():N}";
        var container = serviceClient.GetBlobContainerClient(containerName);
        var store = CreateStore(serviceClient, containerName);
        var folderId = await CreateEmptyMigratedSchemaAsync(databaseServer.ConnectionString, store);
        var raw = Encoding.Latin1.GetBytes(
            "From: sender@example.test\r\nTo: mailbox@example.test\r\nSubject: transaction\r\n\r\nbody\r\n");

        try
        {
            var rolledBackId = Guid.CreateVersion7();
            await using (var context = CreateContext(databaseServer.ConnectionString))
            {
                var effects = CreateEffects(store);
                var content = new MailboxMessageContentService(store, effects);
                var marker = effects.Mark();
                await using var transaction = await context.Database.BeginTransactionAsync();
                var message = CreateMessage(rolledBackId, folderId, 1);
                await content.SetAsync(message, raw, CancellationToken.None);
                context.Emails.Add(message);
                await context.SaveChangesAsync();
                await transaction.RollbackAsync();
                await effects.RollbackAsync(marker);
            }
            Assert.HasCount(0, await GetBlobNamesAsync(container, rolledBackId));

            var committedId = Guid.CreateVersion7();
            await using (var context = CreateContext(databaseServer.ConnectionString))
            {
                var effects = CreateEffects(store);
                var content = new MailboxMessageContentService(store, effects);
                var marker = effects.Mark();
                await using var transaction = await context.Database.BeginTransactionAsync();
                var message = CreateMessage(committedId, folderId, 1);
                await content.SetAsync(message, raw, CancellationToken.None);
                context.Emails.Add(message);
                await context.SaveChangesAsync();
                await transaction.CommitAsync();
                await effects.CommitAsync(marker);
            }
            Assert.HasCount(1, await GetBlobNamesAsync(container, committedId));

            await using (var context = CreateContext(databaseServer.ConnectionString))
            {
                var effects = CreateEffects(store);
                var content = new MailboxMessageContentService(store, effects);
                var marker = effects.Mark();
                await using var transaction = await context.Database.BeginTransactionAsync();
                var message = await context.Emails.SingleAsync(candidate => candidate.Id == committedId);
                content.DeleteOnCommit(message);
                context.Emails.Remove(message);
                await context.SaveChangesAsync();
                await transaction.CommitAsync();
                await effects.CommitAsync(marker);
            }
            Assert.HasCount(0, await GetBlobNamesAsync(container, committedId));
        }
        finally
        {
            await container.DeleteIfExistsAsync();
        }
    }

    private static async Task CreateSchemaAndLegacyRowsAsync(
        string connectionString,
        Guid rawMessageId,
        byte[] raw,
        Guid reconstructedMessageId,
        string reconstructedHeaders,
        string reconstructedBody)
    {
        await using var context = CreateContext(connectionString);
        await context.Database.EnsureCreatedAsync();
        var folderId = await SeedMailboxAsync(context);
        var externalized = CreateMessage(rawMessageId, folderId, 1);
        externalized.RawMessage = raw;
        externalized.SizeBytes = raw.Length;
        context.Emails.Add(externalized);
        var reconstructed = CreateMessage(reconstructedMessageId, folderId, 2);
        reconstructed.RawHeaders = reconstructedHeaders;
        reconstructed.Body = reconstructedBody;
        context.Emails.Add(reconstructed);
        await context.SaveChangesAsync();
    }

    private static async Task<Guid> CreateEmptyMigratedSchemaAsync(
        string connectionString,
        AzureBlobLargeObjectStore store)
    {
        await using (var context = CreateContext(connectionString))
        {
            await context.Database.EnsureCreatedAsync();
            var folderId = await SeedMailboxAsync(context);
            var effects = CreateEffects(store);
            await new MailboxMessageLargeObjectMigrationService(
                context,
                store,
                new MailboxMessageContentService(store, effects),
                NullLogger<MailboxMessageLargeObjectMigrationService>.Instance)
                .MigrateAsync();
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
        context.Folders.Add(folder);
        await context.SaveChangesAsync();
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
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
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
        await command.ExecuteNonQueryAsync();
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
        Guid messageId)
    {
        var names = new List<string>();
        await foreach (var item in container.GetBlobsAsync(
                           BlobTraits.None,
                           BlobStates.None,
                           $"objects/mail/messages/{messageId:N}/",
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
}
