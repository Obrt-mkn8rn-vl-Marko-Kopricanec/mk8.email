using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using mk8.email.Infrastructure.Data;
using mk8.email.Infrastructure.Models;

namespace mk8.email.Infrastructure.Tests;

[TestClass]
[TestCategory("PostgreSQL")]
public sealed class DavContactUidPostgresTests
{
    [TestMethod]
    public async Task ConcurrentCrossAddressBookWritesEnforceAccountWideUidUniqueness()
    {
        await using var server = await RequirePostgresAsync();
        var userId = Guid.CreateVersion7();
        var otherUserId = Guid.CreateVersion7();
        var firstBookId = Guid.CreateVersion7();
        var secondBookId = Guid.CreateVersion7();
        var otherBookId = Guid.CreateVersion7();
        await using (var database = server.CreateContext())
        {
            await database.Database.EnsureCreatedAsync();
            await new MailRuntimeSchemaService(database).EnsureAsync();
            database.Users.AddRange(
                NewUser(userId, "uid-owner@example.test"),
                NewUser(otherUserId, "other-owner@example.test"));
            database.DavCollections.AddRange(
                NewAddressBook(firstBookId, userId, "first"),
                NewAddressBook(secondBookId, userId, "second"),
                NewAddressBook(otherBookId, otherUserId, "other"));
            await database.SaveChangesAsync();
        }

        const string duplicateUid = "concurrent-account-uid";
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ready = 0;
        async Task<bool> WriteAsync(Guid collectionId, string resourceName)
        {
            await using var database = server.CreateContext();
            if (Interlocked.Increment(ref ready) == 2)
                gate.SetResult();
            await gate.Task;
            database.DavResources.Add(NewResource(
                collectionId,
                resourceName,
                duplicateUid,
                addressBookUserId: null));
            try
            {
                await database.SaveChangesAsync();
                return true;
            }
            catch (DbUpdateException exception) when (exception.InnerException is PostgresException
                { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                return false;
            }
        }

        var results = await Task.WhenAll(
            WriteAsync(firstBookId, "first.vcf"),
            WriteAsync(secondBookId, "second.vcf"));
        Assert.AreEqual(1, results.Count(result => result));

        await using (var database = server.CreateContext())
        {
            database.DavResources.Add(NewResource(
                otherBookId,
                "other.vcf",
                duplicateUid,
                addressBookUserId: userId));
            await database.SaveChangesAsync();
            var stored = await database.DavResources
                .AsNoTracking()
                .Where(resource => resource.Uid == duplicateUid)
                .OrderBy(resource => resource.AddressBookUserId)
                .ToListAsync();
            Assert.HasCount(2, stored);
            Assert.AreEqual(2, stored.Select(resource => resource.AddressBookUserId).Distinct().Count());
            Assert.IsTrue(stored.Any(resource => resource.AddressBookUserId == userId));
            Assert.IsTrue(stored.Any(resource => resource.AddressBookUserId == otherUserId));
        }
    }

    [TestMethod]
    public async Task RuntimeSchemaRepairsLegacyDuplicatesBeforeEnablingInvariant()
    {
        await using var server = await RequirePostgresAsync();
        var userId = Guid.CreateVersion7();
        var firstBookId = Guid.CreateVersion7();
        var secondBookId = Guid.CreateVersion7();
        var keptId = Guid.CreateVersion7();
        var repairedId = Guid.CreateVersion7();
        const string duplicateUid = "legacy-duplicate";

        await using (var database = server.CreateContext())
        {
            await database.Database.EnsureCreatedAsync();
            await database.Database.ExecuteSqlRawAsync(
                """
                DROP INDEX IF EXISTS ix_dav_resources_addressbook_user_uid;
                DROP TRIGGER IF EXISTS set_dav_resource_addressbook_user_id ON dav_resources;
                DROP TRIGGER IF EXISTS propagate_dav_collection_uid_scope ON dav_collections;
                """);
            database.Users.Add(NewUser(userId, "legacy-owner@example.test"));
            database.DavCollections.AddRange(
                NewAddressBook(firstBookId, userId, "first"),
                NewAddressBook(secondBookId, userId, "second"));
            var old = DateTime.UtcNow.AddMinutes(-1);
            database.DavResources.AddRange(
                NewResource(
                    firstBookId,
                    "kept.vcf",
                    duplicateUid,
                    addressBookUserId: null,
                    id: keptId,
                    createdAt: old,
                    content: EmbeddedCard(duplicateUid, "kept details")),
                NewResource(
                    secondBookId,
                    "repaired.vcf",
                    duplicateUid,
                    addressBookUserId: null,
                    id: repairedId,
                    createdAt: old.AddSeconds(1),
                    content: EmbeddedCard(duplicateUid, "preserved details")));
            await database.SaveChangesAsync();
        }

        await using (var database = server.CreateContext())
        {
            await new MailRuntimeSchemaService(database).EnsureAsync();
            database.ChangeTracker.Clear();
            var resources = await database.DavResources
                .AsNoTracking()
                .OrderBy(resource => resource.CreatedAt)
                .ToListAsync();
            Assert.HasCount(2, resources);
            Assert.AreEqual(duplicateUid, resources[0].Uid);
            Assert.AreEqual($"urn:uuid:{repairedId:D}", resources[1].Uid);
            Assert.AreEqual(userId, resources[0].AddressBookUserId);
            Assert.AreEqual(userId, resources[1].AddressBookUserId);
            Assert.AreEqual(2, resources.Select(resource => resource.Uid).Distinct().Count());
            var repairedContent = resources[1].Content
                ?? throw new AssertFailedException("The repaired legacy vCard content is missing.");
            Assert.AreEqual(repairedContent.Length, resources[1].SizeBytes);
            Assert.AreEqual(
                Convert.ToHexStringLower(SHA256.HashData(repairedContent)),
                resources[1].Etag);
            var migratedCard = DecodeEmbeddedCard(repairedContent);
            Assert.AreEqual(resources[1].Uid, migratedCard["uid"]?.GetValue<string>());
            Assert.AreEqual(
                "preserved details",
                migratedCard["notes"]?["legacy"]?["note"]?.GetValue<string>());

            await new MailRuntimeSchemaService(database).EnsureAsync();
            Assert.AreEqual(
                2,
                await database.DavResources.AsNoTracking()
                    .Select(resource => resource.Uid)
                    .Distinct()
                    .CountAsync());
        }
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

    private static UserDB NewUser(Guid id, string username) => new()
    {
        Id = id,
        Username = username,
        PasswordHash = "unused",
        Role = "User",
    };

    private static DavCollectionDB NewAddressBook(Guid id, Guid userId, string slug) => new()
    {
        Id = id,
        UserId = userId,
        CollectionType = DavCollectionDB.AddressBookType,
        Slug = slug,
        DisplayName = slug,
        IsDefault = slug == "first",
        IsSubscribed = true,
        SyncToken = 1,
    };

    private static DavResourceDB NewResource(
        Guid collectionId,
        string resourceName,
        string uid,
        Guid? addressBookUserId,
        Guid? id = null,
        DateTime? createdAt = null,
        byte[]? content = null)
    {
        var payload = content ?? Encoding.UTF8.GetBytes(
            $"BEGIN:VCARD\r\nVERSION:4.0\r\nUID;VALUE=text:{uid}\r\nFN:Test\r\nEND:VCARD\r\n");
        var now = createdAt ?? DateTime.UtcNow;
        return new DavResourceDB
        {
            Id = id ?? Guid.CreateVersion7(),
            CollectionId = collectionId,
            AddressBookUserId = addressBookUserId,
            ResourceName = resourceName,
            Uid = uid,
            ContentType = "text/vcard",
            Content = payload,
            Etag = Convert.ToHexStringLower(SHA256.HashData(payload)),
            SizeBytes = payload.Length,
            ChangeSequence = 1,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    private static byte[] EmbeddedCard(string uid, string note)
    {
        var card = new JsonObject
        {
            ["@type"] = "Card",
            ["version"] = "1.0",
            ["uid"] = uid,
            ["notes"] = new JsonObject
            {
                ["legacy"] = new JsonObject { ["note"] = note },
            },
        };
        var core = new[]
        {
            "BEGIN:VCARD",
            "VERSION:4.0",
            "UID;VALUE=text:" + uid,
            "FN:Legacy contact",
            "END:VCARD",
        };
        var hash = Convert.ToHexStringLower(SHA256.HashData(
            Encoding.UTF8.GetBytes(string.Join("\r\n", core) + "\r\n")));
        var embedded = Base64UrlEncode(Encoding.UTF8.GetBytes(card.ToJsonString()));
        return Encoding.UTF8.GetBytes(string.Join("\r\n", new[]
        {
            core[0], core[1], core[2], core[3],
            "X-MK8-JSCONTACT-HASH:" + hash,
            "X-MK8-JSCONTACT:" + embedded,
            core[4], string.Empty,
        }));
    }

    private static JsonObject DecodeEmbeddedCard(byte[] content)
    {
        var logicalLines = new List<string>();
        foreach (var line in Encoding.UTF8.GetString(content).Split("\r\n"))
        {
            if (line.StartsWith(' ') && logicalLines.Count > 0)
                logicalLines[^1] += line[1..];
            else if (line.Length > 0)
                logicalLines.Add(line);
        }
        var encoded = logicalLines.Single(line => line.StartsWith(
            "X-MK8-JSCONTACT:", StringComparison.Ordinal))["X-MK8-JSCONTACT:".Length..];
        var padded = encoded.Replace('-', '+').Replace('_', '/');
        padded += new string('=', (4 - padded.Length % 4) % 4);
        return JsonNode.Parse(Convert.FromBase64String(padded))!.AsObject();
    }

    private static string Base64UrlEncode(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
