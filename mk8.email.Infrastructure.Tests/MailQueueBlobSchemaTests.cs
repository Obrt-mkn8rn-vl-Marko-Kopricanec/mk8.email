using Microsoft.EntityFrameworkCore;
using mk8.email.Infrastructure.Data;
using mk8.email.Infrastructure.Models;
using Npgsql;

namespace mk8.email.Infrastructure.Tests;

[TestClass]
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Maintainability", "CA1515",
    Justification = "MSTest discovers this public test class by reflection.")]
public sealed class MailQueueBlobSchemaTests
{
    [TestMethod]
    public void ModelMapsNullableLegacyContentAndAzureReferenceMetadata()
    {
        using var database = CreateModelContext();
        var entity = database.Model.FindEntityType(typeof(MailQueueMessageDB));
        Assert.IsNotNull(entity);

        var content = entity.FindProperty(nameof(MailQueueMessageDB.RawMessage));
        Assert.IsNotNull(content);
        Assert.IsTrue(content.IsNullable);
        Assert.AreEqual(32, entity.FindProperty(
            nameof(MailQueueMessageDB.RawMessageObjectProvider))?.GetMaxLength());
        Assert.AreEqual(1024, entity.FindProperty(
            nameof(MailQueueMessageDB.RawMessageObjectName))?.GetMaxLength());
        Assert.AreEqual(64, entity.FindProperty(
            nameof(MailQueueMessageDB.RawMessageObjectSha256))?.GetMaxLength());
        Assert.AreEqual(256, entity.FindProperty(
            nameof(MailQueueMessageDB.RawMessageObjectEntityTag))?.GetMaxLength());
    }

    [TestMethod]
    [TestCategory("PostgreSQL")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Maintainability", "MA0051",
        Justification = "The schema migration test keeps setup and verification together.")]
    public async Task RuntimeSchemaPreservesLegacyQueueContentWhileAddingReferenceShape()
    {
        var server = await RequirePostgresAsync().ConfigureAwait(false);
        await using var serverLifetime = server.ConfigureAwait(false);
        var queueId = Guid.CreateVersion7();
        const string rawMessage =
            "From: sender@example.test\r\nTo: recipient@example.test\r\n\r\nlegacy\r\n";

        {
            var database = server.CreateContext();
            await using var databaseLifetime = database.ConfigureAwait(false);
            await database.Database.EnsureCreatedAsync().ConfigureAwait(false);
            await database.Database.ExecuteSqlRawAsync(
                """
                ALTER TABLE mail_queue_messages
                    DROP CONSTRAINT ck_mail_queue_messages_raw_storage_shape;
                ALTER TABLE mail_queue_messages
                    DROP COLUMN raw_message_size_bytes,
                    DROP COLUMN raw_message_object_provider,
                    DROP COLUMN raw_message_object_name,
                    DROP COLUMN raw_message_object_sha256,
                    DROP COLUMN raw_message_object_etag;
                ALTER TABLE mail_queue_messages
                    ALTER COLUMN raw_message SET NOT NULL;
                """).ConfigureAwait(false);
            await database.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO mail_queue_messages (
                    id, envelope_sender, raw_message, requires_smtp_utf8,
                    direction, state, scan_state, attempt_count,
                    received_at, next_attempt_at, sent_copy_created)
                VALUES (
                    {queueId}, {"sender@example.test"}, {rawMessage}, {false},
                    {"inbound"}, {"pending"}, {"pending"}, {0},
                    {DateTime.UtcNow}, {DateTime.UtcNow}, {false})
                """).ConfigureAwait(false);

            await new MailRuntimeSchemaService(database).EnsureAsync().ConfigureAwait(false);
            database.ChangeTracker.Clear();

            var preserved = await database.MailQueueMessages.AsNoTracking()
                .SingleAsync(candidate => candidate.Id == queueId).ConfigureAwait(false);
            Assert.AreEqual(rawMessage, preserved.RawMessage, StringComparer.Ordinal);
            Assert.AreEqual(rawMessage.Length, preserved.RawMessageSizeBytes);
            Assert.IsNull(preserved.RawMessageObjectProvider);
            Assert.IsNull(preserved.RawMessageObjectName);
            Assert.IsNull(preserved.RawMessageObjectSha256);
            Assert.IsNull(preserved.RawMessageObjectEntityTag);
        }

        var connection = new NpgsqlConnection(server.ConnectionString);
        await using var connectionLifetime = connection.ConfigureAwait(false);
        await connection.OpenAsync().ConfigureAwait(false);
        {
            var column = connection.CreateCommand();
            await using var columnLifetime = column.ConfigureAwait(false);
            column.CommandText =
                """
                SELECT is_nullable
                FROM information_schema.columns
                WHERE table_schema = 'public'
                  AND table_name = 'mail_queue_messages'
                  AND column_name = 'raw_message'
                """;
            Assert.AreEqual("YES", await column.ExecuteScalarAsync().ConfigureAwait(false));
        }
        {
            var constraint = connection.CreateCommand();
            await using var constraintLifetime = constraint.ConfigureAwait(false);
            constraint.CommandText =
                """
                SELECT convalidated
                FROM pg_constraint
                WHERE conrelid = 'mail_queue_messages'::regclass
                  AND conname = 'ck_mail_queue_messages_raw_storage_shape'
                """;
            Assert.AreEqual(true, await constraint.ExecuteScalarAsync().ConfigureAwait(false));
        }

        {
            var listen = connection.CreateCommand();
            await using var listenLifetime = listen.ConfigureAwait(false);
            listen.CommandText = "LISTEN mk8_mail_queue_ready";
            await listen.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        {
            var writer = new NpgsqlConnection(server.ConnectionString);
            await using var writerLifetime = writer.ConfigureAwait(false);
            await writer.OpenAsync().ConfigureAwait(false);
            var insert = writer.CreateCommand();
            await using var insertLifetime = insert.ConfigureAwait(false);
            insert.CommandText =
                """
                INSERT INTO mail_queue_messages (
                    id, envelope_sender, raw_message, raw_message_size_bytes,
                    requires_smtp_utf8, direction, state, scan_state, attempt_count,
                    received_at, next_attempt_at, sent_copy_created)
                VALUES (
                    @id, 'notify@example.test', 'notification', 12,
                    false, 'inbound', 'pending', 'pending', 0,
                    @received_at, @next_attempt_at, false)
                """;
            insert.Parameters.AddWithValue("id", Guid.CreateVersion7());
            insert.Parameters.AddWithValue("received_at", DateTime.UtcNow);
            insert.Parameters.AddWithValue("next_attempt_at", DateTime.UtcNow);
            await insert.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        Assert.IsTrue(await connection.WaitAsync(5_000).ConfigureAwait(false));
    }

    private static EmailDbContext CreateModelContext() => new(
        new DbContextOptionsBuilder<EmailDbContext>()
            .UseNpgsql("Host=localhost;Database=unused;Username=unused;Password=unused")
            .Options);

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
}
