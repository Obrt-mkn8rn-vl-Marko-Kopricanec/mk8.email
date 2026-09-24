using Microsoft.EntityFrameworkCore;
using mk8.email.Infrastructure.Data;
using mk8.email.Infrastructure.Models;
using Npgsql;

namespace mk8.email.Infrastructure.Tests;

[TestClass]
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Maintainability", "CA1515",
    Justification = "MSTest discovers this public test class by reflection.")]
public sealed class JmapBlobSchemaTests
{
    [TestMethod]
    public void ModelMapsNullableContentAndAzureReferenceMetadata()
    {
        using var database = CreateModelContext();
        var entity = database.Model.FindEntityType(typeof(JmapBlobDB));
        Assert.IsNotNull(entity);

        var content = entity.FindProperty(nameof(JmapBlobDB.Content));
        Assert.IsNotNull(content);
        Assert.IsTrue(content.IsNullable);
        Assert.AreEqual("bytea", content.GetColumnType(), StringComparer.Ordinal);
        Assert.AreEqual(32, entity.FindProperty(nameof(JmapBlobDB.ObjectProvider))?.GetMaxLength());
        Assert.AreEqual(1024, entity.FindProperty(nameof(JmapBlobDB.ObjectName))?.GetMaxLength());
        Assert.AreEqual(64, entity.FindProperty(nameof(JmapBlobDB.ObjectSha256))?.GetMaxLength());
        Assert.AreEqual(256, entity.FindProperty(nameof(JmapBlobDB.ObjectEntityTag))?.GetMaxLength());
    }

    [TestMethod]
    [TestCategory("PostgreSQL")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Maintainability", "MA0051",
        Justification = "The schema migration test keeps setup and verification together.")]
    public async Task RuntimeSchemaPreservesLegacyBytesWhileAddingReferenceShape()
    {
        var server = await RequirePostgresAsync().ConfigureAwait(false);
        await using var serverLifetime = server.ConfigureAwait(false);
        var company = new CompanyDB
        {
            Id = Guid.CreateVersion7(),
            Name = "JMAP blob schema test",
        };
        var address = new AddressDB
        {
            Id = Guid.CreateVersion7(),
            Domain = "blob-schema.example.test",
            IsActive = true,
            Company = company,
        };
        var user = new UserDB
        {
            Id = Guid.CreateVersion7(),
            Username = "blob-schema@example.test",
            PasswordHash = "unused",
            Role = "User",
            Company = company,
        };
        var inbox = new InboxDB
        {
            Id = Guid.CreateVersion7(),
            Name = "blob-schema",
            Address = address,
            Owner = user,
        };
        var blobId = Guid.CreateVersion7();
        var content = "legacy-jmap-database-bytes"u8.ToArray();

        {
            var database = server.CreateContext();
            await using var databaseLifetime = database.ConfigureAwait(false);
            await database.Database.EnsureCreatedAsync().ConfigureAwait(false);
            await database.Inboxes.AddAsync(inbox).ConfigureAwait(false);
            await database.SaveChangesAsync().ConfigureAwait(false);
            await database.Database.ExecuteSqlRawAsync(
                """
                ALTER TABLE jmap_blobs
                    DROP CONSTRAINT ck_jmap_blobs_storage_shape;
                ALTER TABLE jmap_blobs
                    DROP COLUMN object_provider,
                    DROP COLUMN object_name,
                    DROP COLUMN object_sha256,
                    DROP COLUMN object_etag;
                ALTER TABLE jmap_blobs
                    ALTER COLUMN content SET NOT NULL;
                """).ConfigureAwait(false);
            await database.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO jmap_blobs (
                    id, blob_id, account_id, content_type, content, size_bytes,
                    created_at, expires_at)
                VALUES (
                    {blobId}, {"B" + blobId.ToString("N")}, {inbox.Id},
                    {"application/octet-stream"}, {content}, {content.LongLength},
                    {DateTime.UtcNow}, {DateTime.UtcNow.AddHours(1)})
                """).ConfigureAwait(false);

            await new MailRuntimeSchemaService(database).EnsureAsync().ConfigureAwait(false);
            database.ChangeTracker.Clear();

            var preserved = await database.JmapBlobs.AsNoTracking()
                .SingleAsync(candidate => candidate.Id == blobId).ConfigureAwait(false);
            CollectionAssert.AreEqual(content, preserved.Content);
            Assert.IsNull(preserved.ObjectProvider);
            Assert.IsNull(preserved.ObjectName);
            Assert.IsNull(preserved.ObjectSha256);
            Assert.IsNull(preserved.ObjectEntityTag);
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
                  AND table_name = 'jmap_blobs'
                  AND column_name = 'content'
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
                WHERE conrelid = 'jmap_blobs'::regclass
                  AND conname = 'ck_jmap_blobs_storage_shape'
                """;
            Assert.AreEqual(true, await constraint.ExecuteScalarAsync().ConfigureAwait(false));
        }
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
