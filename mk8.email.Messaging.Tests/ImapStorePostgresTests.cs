using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using mk8.email.Application.Services;
using mk8.email.Contracts.Imap;
using mk8.email.Infrastructure.Data;
using mk8.email.Infrastructure.Models;

namespace mk8.email.Messaging.Tests;

[TestClass]
[TestCategory("PostgreSQL")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1812", Justification = "MSTest DiscoverInternals instantiates this test class by reflection; focused discovery is verified by executed test counts.")]
internal sealed class ImapStorePostgresTests
{
    [TestMethod]
    [Timeout(20_000)]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "MA0051", Justification = "The StoreIsOwnerScopedAtomicAndResolvesSequenceUidAndSavedSelections scenario keeps one fixture's ordered setup, operation and invariant assertions together.")]
    public async Task StoreIsOwnerScopedAtomicAndResolvesSequenceUidAndSavedSelections()
    {
        var server = (await PostgresTestDatabase.TryCreateAsync().ConfigureAwait(false));
        await using var serverLifetime = new NullableAsyncDisposable(server).ConfigureAwait(false);
        if (server is null)
        {
            Assert.Inconclusive("Set MK8_EMAIL_TEST_POSTGRES to a PostgreSQL admin connection string.");
            return;
        }

        var options = new DbContextOptionsBuilder<EmailDbContext>()
            .UseNpgsql(server.ConnectionString)
            .Options;
        var userId = Guid.CreateVersion7();
        var otherUserId = Guid.CreateVersion7();
        var folderId = Guid.CreateVersion7();
        {
            var database = new EmailDbContext(options);
            await using var databaseLifetime = database.ConfigureAwait(false);
            await database.Database.EnsureCreatedAsync().ConfigureAwait(false);
            await new MailRuntimeSchemaService(database).EnsureAsync().ConfigureAwait(false);
            var company = new CompanyDB
            {
                Id = Guid.CreateVersion7(),
                Name = "IMAP STORE test",
                IsActive = true,
            };
            var address = new AddressDB
            {
                Id = Guid.CreateVersion7(),
                Domain = "example.test",
                IsActive = true,
                Company = company,
            };
            var user = new UserDB
            {
                Id = userId,
                Username = "owner@example.test",
                PasswordHash = "unused",
                Role = "User",
                IsActive = true,
                Company = company,
            };
            await (database.Users.AddAsync(new UserDB
            {
                Id = otherUserId,
                Username = "other@example.test",
                PasswordHash = "unused",
                Role = "User",
                IsActive = true,
                Company = company,
            })).ConfigureAwait(false);
            var folder = new FolderDB
            {
                Id = folderId,
                Name = "Projects",
                HighestModSeq = 4,
                NextUid = 4,
                Inbox = new InboxDB
                {
                    Id = Guid.CreateVersion7(),
                    Name = "owner",
                    Address = address,
                    Owner = user,
                },
            };
            for (var uid = 1; uid <= 3; uid++)
            {
                await (database.Emails.AddAsync(new EmailDB
                {
                    Id = Guid.CreateVersion7(),
                    Folder = folder,
                    Uid = uid,
                    ModSeq = uid + 1,
                    Sender = "sender@example.test",
                    Recipient = "owner@example.test",
                    Subject = $"Message {uid}",
                    Body = string.Empty,
                })).ConfigureAwait(false);
            }
            await database.SaveChangesAsync().ConfigureAwait(false);
        }

        {
            var database = new EmailDbContext(options);
            await using var databaseLifetime = database.ConfigureAwait(false);
            var application = CreateApplication(database);
            var denied = await application.StoreFlagsAsync(new ImapStoreRequest(
                otherUserId, folderId, false,
                new ImapMessageSelection([new ImapMessageRange(1, null)], null),
                null, ImapFlagMutationMode.Add, ["\\Seen"])).ConfigureAwait(false);
            Assert.AreEqual(ImapStoreDisposition.FolderNotFound, denied.Disposition);
            await Assert.ThrowsAsync<ArgumentException>(() => application.StoreFlagsAsync(
                new ImapStoreRequest(userId, folderId, false,
                    new ImapMessageSelection([], null), null,
                    ImapFlagMutationMode.Add, ["\\Seen"]))).ConfigureAwait(false);
            await Assert.ThrowsAsync<ArgumentException>(() => application.StoreFlagsAsync(
                new ImapStoreRequest(userId, folderId, false,
                    new ImapMessageSelection([new ImapMessageRange(1, null)], null), null,
                    ImapFlagMutationMode.Add, ["\\Recent"]))).ConfigureAwait(false);
        }

        {
            var database = new EmailDbContext(options);
            await using var databaseLifetime = database.ConfigureAwait(false);
            var application = CreateApplication(database);
            var result = await application.StoreFlagsAsync(new ImapStoreRequest(
                userId, folderId, false,
                new ImapMessageSelection([new ImapMessageRange(2, null)], null),
                3, ImapFlagMutationMode.Add, ["\\Seen", "$Tag"])).ConfigureAwait(false);
            Assert.AreEqual(ImapStoreDisposition.Stored, result.Disposition);
            CollectionAssert.AreEqual(ExpectedVector1, result.Modified);
            Assert.HasCount(1, result.Updated);
            Assert.AreEqual(2, result.Updated[0].Sequence);
            Assert.AreEqual(2, result.Updated[0].Uid);
            Assert.AreEqual(5L, result.Updated[0].ModSeq);
            Assert.IsTrue(result.Updated[0].IsRead);
            CollectionAssert.AreEqual(ExpectedVector2, result.Updated[0].Keywords);
        }

        {
            var database = new EmailDbContext(options);
            await using var databaseLifetime = database.ConfigureAwait(false);
            var application = CreateApplication(database);
            var result = await application.StoreFlagsAsync(new ImapStoreRequest(
                userId, folderId, true,
                new ImapMessageSelection(null, [1, 3]),
                null, ImapFlagMutationMode.Replace, ["\\Flagged"])).ConfigureAwait(false);
            Assert.AreEqual(ImapStoreDisposition.Stored, result.Disposition);
            Assert.IsEmpty(result.Modified);
            CollectionAssert.AreEqual(ExpectedVector3, result.Updated
                .Select(message => message.Uid).ToArray());
            Assert.IsTrue(result.Updated.All(message => message.IsFlagged && message.ModSeq == 6));
        }

        {
            var database = new EmailDbContext(options);
            await using var databaseLifetime = database.ConfigureAwait(false);
            var application = CreateApplication(database);
            var excluded = await application.StoreFlagsAsync(new ImapStoreRequest(
                userId, folderId, true,
                new ImapMessageSelection([new ImapMessageRange(1, null)], null),
                0, ImapFlagMutationMode.Add, ["\\Deleted"])).ConfigureAwait(false);
            Assert.AreEqual(ImapStoreDisposition.Stored, excluded.Disposition);
            CollectionAssert.AreEqual(ExpectedVector4, excluded.Modified);
            Assert.IsEmpty(excluded.Updated);
            var empty = await application.StoreFlagsAsync(new ImapStoreRequest(
                userId, folderId, true, new ImapMessageSelection(null, []),
                null, ImapFlagMutationMode.Add, ["\\Deleted"])).ConfigureAwait(false);
            Assert.AreEqual(ImapStoreDisposition.Stored, empty.Disposition);
            Assert.IsEmpty(empty.Modified);
            Assert.IsEmpty(empty.Updated);
        }

        {
            var database = new EmailDbContext(options);
            await using var databaseLifetime = database.ConfigureAwait(false);
            var application = CreateApplication(database);
            var keywords = Enumerable.Range(0, 129)
                .Select(index => $"$Tag{index}")
                .ToArray();
            var limited = await application.StoreFlagsAsync(new ImapStoreRequest(
                userId, folderId, true,
                new ImapMessageSelection([new ImapMessageRange(1, 3)], null),
                null, ImapFlagMutationMode.Add, keywords)).ConfigureAwait(false);
            Assert.AreEqual(ImapStoreDisposition.KeywordLimitExceeded, limited.Disposition);
            Assert.IsEmpty(limited.Updated);
            Assert.IsEmpty(limited.Modified);
            var laterOverflow = await application.StoreFlagsAsync(new ImapStoreRequest(
                userId, folderId, true,
                new ImapMessageSelection([new ImapMessageRange(1, 3)], null),
                null, ImapFlagMutationMode.Add,
                ["\\Seen", .. Enumerable.Range(0, 128).Select(index => $"new{index}")])).ConfigureAwait(false);
            Assert.AreEqual(ImapStoreDisposition.KeywordLimitExceeded, laterOverflow.Disposition);
            Assert.IsEmpty(laterOverflow.Updated);
            Assert.IsEmpty(laterOverflow.Modified);
        }
        {
            var database = new EmailDbContext(options);
            await using var databaseLifetime = database.ConfigureAwait(false);
            Assert.AreEqual(6L, await database.Folders
                .Where(folder => folder.Id == folderId)
                .Select(folder => folder.HighestModSeq)
                .SingleAsync().ConfigureAwait(false));
            Assert.IsFalse(await database.Emails.Where(email => email.Uid == 1)
                .Select(email => email.IsRead).SingleAsync().ConfigureAwait(false));
            CollectionAssert.AreEqual(Array.Empty<string>(), await database.Emails
                .Where(email => email.Uid == 1).Select(email => email.Keywords).SingleAsync().ConfigureAwait(false));
            Assert.IsFalse(await database.Emails
                .Where(email => email.Uid == 1)
                .Select(email => email.IsDeleted)
                .SingleAsync().ConfigureAwait(false));
            await database.Database.ExecuteSqlRawAsync(
                """
                CREATE FUNCTION reject_imap_store() RETURNS trigger AS $$
                BEGIN
                    RAISE EXCEPTION 'test rollback of IMAP STORE';
                END;
                $$ LANGUAGE plpgsql;
                CREATE TRIGGER reject_imap_store
                BEFORE UPDATE ON emails
                FOR EACH ROW EXECUTE FUNCTION reject_imap_store();
                """).ConfigureAwait(false);
        }

        {
            var database = new EmailDbContext(options);
            await using var databaseLifetime = database.ConfigureAwait(false);
            var application = CreateApplication(database);
            await Assert.ThrowsAsync<DbUpdateException>(() => application.StoreFlagsAsync(
                new ImapStoreRequest(userId, folderId, true,
                    new ImapMessageSelection([new ImapMessageRange(1, 1)], null),
                    null, ImapFlagMutationMode.Add, ["\\Deleted"]))).ConfigureAwait(false);
        }
        {
            var database = new EmailDbContext(options);
            await using var databaseLifetime = database.ConfigureAwait(false);
            Assert.AreEqual(6L, await database.Folders
                .Where(folder => folder.Id == folderId)
                .Select(folder => folder.HighestModSeq)
                .SingleAsync().ConfigureAwait(false));
            Assert.IsFalse(await database.Emails
                .Where(email => email.Uid == 1)
                .Select(email => email.IsDeleted)
                .SingleAsync().ConfigureAwait(false));
            await database.Database.ExecuteSqlRawAsync(
                "DROP TRIGGER reject_imap_store ON emails; "
                + "DROP FUNCTION reject_imap_store();").ConfigureAwait(false);
        }

        {
            var database = new EmailDbContext(options);
            await using var databaseLifetime = database.ConfigureAwait(false);
            var application = CreateApplication(database);
            var stored = await application.StoreFlagsAsync(new ImapStoreRequest(
                userId, folderId, true,
                new ImapMessageSelection([new ImapMessageRange(1, 1)], null),
                null, ImapFlagMutationMode.Add, ["\\Deleted"])).ConfigureAwait(false);
            Assert.AreEqual(ImapStoreDisposition.Stored, stored.Disposition);
            Assert.HasCount(1, stored.Updated);
            Assert.IsTrue(stored.Updated[0].IsDeleted);
            Assert.AreEqual(7L, stored.Updated[0].ModSeq);
        }
        {
            var database = new EmailDbContext(options);
            await using var databaseLifetime = database.ConfigureAwait(false);
            Assert.AreEqual(7L, await database.Folders
                .Where(folder => folder.Id == folderId)
                .Select(folder => folder.HighestModSeq)
                .SingleAsync().ConfigureAwait(false));
            var messages = await database.Emails
                .OrderBy(email => email.Uid)
                .ToListAsync().ConfigureAwait(false);
            Assert.HasCount(3, messages);
            Assert.IsTrue(messages[0].IsDeleted);
            Assert.IsTrue(messages[0].IsFlagged);
            Assert.IsTrue(messages[1].IsRead);
            CollectionAssert.AreEqual(ExpectedVector2, messages[1].Keywords);
            Assert.IsTrue(messages[2].IsFlagged);
        }
    }

    private static ImapApplicationService CreateApplication(EmailDbContext database) => new(
        null!, null!, database, null!, null!, NullLogger<ImapApplicationService>.Instance);
    private static readonly int[] ExpectedVector1 = new[] { 3 };
    private static readonly string[] ExpectedVector2 = new[] { "$Tag" };
    private static readonly int[] ExpectedVector3 = new[] { 1, 3 };
    private static readonly int[] ExpectedVector4 = new[] { 1, 2, 3 };
}
