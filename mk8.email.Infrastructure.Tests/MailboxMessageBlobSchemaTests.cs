using Microsoft.EntityFrameworkCore;
using mk8.email.Infrastructure.Data;
using mk8.email.Infrastructure.Models;
using Npgsql;

namespace mk8.email.Infrastructure.Tests;

[TestClass]
public sealed class MailboxMessageBlobSchemaTests
{
    [TestMethod]
    public void ModelMapsNullableRawMessageAndAzureReferenceMetadata()
    {
        using var database = CreateModelContext();
        var entity = database.Model.FindEntityType(typeof(EmailDB));
        Assert.IsNotNull(entity);

        var rawMessage = entity.FindProperty(nameof(EmailDB.RawMessage));
        Assert.IsNotNull(rawMessage);
        Assert.IsTrue(rawMessage.IsNullable);
        Assert.AreEqual("bytea", rawMessage.GetColumnType());
        Assert.AreEqual(32, entity.FindProperty(nameof(EmailDB.RawMessageObjectProvider))?.GetMaxLength());
        Assert.AreEqual(1024, entity.FindProperty(nameof(EmailDB.RawMessageObjectName))?.GetMaxLength());
        Assert.AreEqual(64, entity.FindProperty(nameof(EmailDB.RawMessageObjectSha256))?.GetMaxLength());
        Assert.AreEqual(256, entity.FindProperty(nameof(EmailDB.RawMessageObjectEntityTag))?.GetMaxLength());
    }

    [TestMethod]
    [TestCategory("PostgreSQL")]
    public async Task RuntimeSchemaPreservesLegacyMessageWhileAddingReferenceShape()
    {
        await using var server = await RequirePostgresAsync();
        var company = new CompanyDB
        {
            Id = Guid.CreateVersion7(),
            Name = "Mailbox blob schema test",
        };
        var address = new AddressDB
        {
            Id = Guid.CreateVersion7(),
            Domain = "mailbox-schema.example.test",
            IsActive = true,
            Company = company,
        };
        var user = new UserDB
        {
            Id = Guid.CreateVersion7(),
            Username = "mailbox-schema@example.test",
            PasswordHash = "unused",
            Role = "User",
            Company = company,
        };
        var inbox = new InboxDB
        {
            Id = Guid.CreateVersion7(),
            Name = "mailbox-schema",
            Address = address,
            Owner = user,
        };
        var folder = new FolderDB
        {
            Id = Guid.CreateVersion7(),
            Name = "INBOX",
            Inbox = inbox,
        };
        var messageId = Guid.CreateVersion7();
        var raw = "From: sender@example.test\r\nTo: mailbox-schema@example.test\r\n\r\nlegacy"u8.ToArray();
        var message = new EmailDB
        {
            Id = messageId,
            Sender = "sender@example.test",
            Recipient = user.Username,
            Subject = "Legacy",
            Body = "legacy",
            RawHeaders = "From: sender@example.test\r\nTo: mailbox-schema@example.test",
            RawMessage = raw,
            SizeBytes = raw.Length,
            Uid = 1,
            ModSeq = 1,
            Folder = folder,
        };

        await using (var database = server.CreateContext())
        {
            await database.Database.EnsureCreatedAsync();
            database.Emails.Add(message);
            await database.SaveChangesAsync();
            await database.Database.ExecuteSqlRawAsync(
                """
                ALTER TABLE emails
                    DROP CONSTRAINT ck_emails_raw_storage_shape;
                ALTER TABLE emails
                    DROP COLUMN raw_message_object_provider,
                    DROP COLUMN raw_message_object_name,
                    DROP COLUMN raw_message_object_sha256,
                    DROP COLUMN raw_message_object_etag;
                """);

            await new MailRuntimeSchemaService(database).EnsureAsync();
            database.ChangeTracker.Clear();

            var preserved = await database.Emails.AsNoTracking()
                .SingleAsync(candidate => candidate.Id == messageId);
            CollectionAssert.AreEqual(raw, preserved.RawMessage);
            Assert.IsNull(preserved.RawMessageObjectProvider);
            Assert.IsNull(preserved.RawMessageObjectName);
            Assert.IsNull(preserved.RawMessageObjectSha256);
            Assert.IsNull(preserved.RawMessageObjectEntityTag);
        }

        await using var connection = new NpgsqlConnection(server.ConnectionString);
        await connection.OpenAsync();
        await using var constraint = connection.CreateCommand();
        constraint.CommandText =
            """
            SELECT convalidated
            FROM pg_constraint
            WHERE conrelid = 'emails'::regclass
              AND conname = 'ck_emails_raw_storage_shape'
            """;
        Assert.AreEqual(true, await constraint.ExecuteScalarAsync());
    }

    private static EmailDbContext CreateModelContext() => new(
        new DbContextOptionsBuilder<EmailDbContext>()
            .UseNpgsql("Host=localhost;Database=unused;Username=unused;Password=unused")
            .Options);

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
}
