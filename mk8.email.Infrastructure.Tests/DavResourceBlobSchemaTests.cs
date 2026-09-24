using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using mk8.email.Infrastructure.Data;
using mk8.email.Infrastructure.Models;
using Npgsql;

namespace mk8.email.Infrastructure.Tests;

[TestClass]
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Maintainability", "CA1515",
    Justification = "MSTest discovers this public test class by reflection.")]
public sealed class DavResourceBlobSchemaTests
{
    [TestMethod]
    [TestCategory("PostgreSQL")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Maintainability", "MA0051",
        Justification = "The schema migration test keeps setup and verification together.")]
    public async Task RuntimeSchemaPreservesLegacyDavContentWhileAddingReferenceShape()
    {
        var server = await RequirePostgresAsync().ConfigureAwait(false);
        await using var serverLifetime = server.ConfigureAwait(false);
        var company = new CompanyDB
        {
            Id = Guid.CreateVersion7(),
            Name = "DAV resource schema test",
        };
        var user = new UserDB
        {
            Id = Guid.CreateVersion7(),
            Username = "dav-schema@example.test",
            PasswordHash = "unused",
            Role = "User",
            Company = company,
        };
        var collection = new DavCollectionDB
        {
            Id = Guid.CreateVersion7(),
            User = user,
            UserId = user.Id,
            CollectionType = DavCollectionDB.CalendarType,
            Slug = "default",
            DisplayName = "Calendar",
            Components = ["VEVENT"],
            SyncToken = 1,
        };
        var resourceId = Guid.CreateVersion7();
        var content = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nEND:VCALENDAR\r\n"u8.ToArray();
        var resource = new DavResourceDB
        {
            Id = resourceId,
            Collection = collection,
            CollectionId = collection.Id,
            ResourceName = "legacy.ics",
            Uid = "legacy-dav-resource",
            ContentType = "text/calendar",
            Content = content,
            Etag = Convert.ToHexStringLower(SHA256.HashData(content)),
            SizeBytes = content.Length,
            ChangeSequence = 1,
        };

        {
            var database = server.CreateContext();
            await using var databaseLifetime = database.ConfigureAwait(false);
            await database.Database.EnsureCreatedAsync().ConfigureAwait(false);
            await database.DavResources.AddAsync(resource).ConfigureAwait(false);
            await database.SaveChangesAsync().ConfigureAwait(false);
            await database.Database.ExecuteSqlRawAsync(
                """
                ALTER TABLE dav_resources
                    DROP CONSTRAINT ck_dav_resources_storage_shape;
                ALTER TABLE dav_resources
                    DROP COLUMN object_provider,
                    DROP COLUMN object_name,
                    DROP COLUMN object_sha256,
                    DROP COLUMN object_etag;
                ALTER TABLE dav_resources
                    ALTER COLUMN content SET NOT NULL;
                """).ConfigureAwait(false);

            await new MailRuntimeSchemaService(database).EnsureAsync().ConfigureAwait(false);
            database.ChangeTracker.Clear();

            var preserved = await database.DavResources.AsNoTracking()
                .SingleAsync(candidate => candidate.Id == resourceId).ConfigureAwait(false);
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
                  AND table_name = 'dav_resources'
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
                WHERE conrelid = 'dav_resources'::regclass
                  AND conname = 'ck_dav_resources_storage_shape'
                """;
            Assert.AreEqual(true, await constraint.ExecuteScalarAsync().ConfigureAwait(false));
        }
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
}
