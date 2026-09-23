using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using mk8.email.Application.Services;
using mk8.email.Contracts.Imap;
using mk8.email.Infrastructure.Data;
using mk8.email.Infrastructure.Models;

namespace mk8.email.Messaging.Tests;

[TestClass]
[TestCategory("PostgreSQL")]
public sealed class ImapStorePostgresTests
{
    [TestMethod]
    [Timeout(20_000)]
    public async Task StoreIsOwnerScopedAtomicAndResolvesSequenceUidAndSavedSelections()
    {
        await using var server = await PostgresTestDatabase.TryCreateAsync();
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
        await using (var database = new EmailDbContext(options))
        {
            await database.Database.EnsureCreatedAsync();
            await new MailRuntimeSchemaService(database).EnsureAsync();
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
            database.Users.Add(new UserDB
            {
                Id = otherUserId,
                Username = "other@example.test",
                PasswordHash = "unused",
                Role = "User",
                IsActive = true,
                Company = company,
            });
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
                database.Emails.Add(new EmailDB
                {
                    Id = Guid.CreateVersion7(),
                    Folder = folder,
                    Uid = uid,
                    ModSeq = uid + 1,
                    Sender = "sender@example.test",
                    Recipient = "owner@example.test",
                    Subject = $"Message {uid}",
                    Body = string.Empty,
                });
            }
            await database.SaveChangesAsync();
        }

        await using (var database = new EmailDbContext(options))
        {
            var application = CreateApplication(database);
            var denied = await application.StoreFlagsAsync(new ImapStoreRequest(
                otherUserId, folderId, false,
                new ImapMessageSelection([new ImapMessageRange(1, null)], null),
                null, ImapFlagMutationMode.Add, ["\\Seen"]));
            Assert.AreEqual(ImapStoreDisposition.FolderNotFound, denied.Disposition);
            await Assert.ThrowsAsync<ArgumentException>(() => application.StoreFlagsAsync(
                new ImapStoreRequest(userId, folderId, false,
                    new ImapMessageSelection([], null), null,
                    ImapFlagMutationMode.Add, ["\\Seen"])));
            await Assert.ThrowsAsync<ArgumentException>(() => application.StoreFlagsAsync(
                new ImapStoreRequest(userId, folderId, false,
                    new ImapMessageSelection([new ImapMessageRange(1, null)], null), null,
                    ImapFlagMutationMode.Add, ["\\Recent"])));
        }

        await using (var database = new EmailDbContext(options))
        {
            var application = CreateApplication(database);
            var result = await application.StoreFlagsAsync(new ImapStoreRequest(
                userId, folderId, false,
                new ImapMessageSelection([new ImapMessageRange(2, null)], null),
                3, ImapFlagMutationMode.Add, ["\\Seen", "$Tag"]));
            Assert.AreEqual(ImapStoreDisposition.Stored, result.Disposition);
            CollectionAssert.AreEqual(new[] { 3 }, result.Modified);
            Assert.HasCount(1, result.Updated);
            Assert.AreEqual(2, result.Updated[0].Sequence);
            Assert.AreEqual(2, result.Updated[0].Uid);
            Assert.AreEqual(5L, result.Updated[0].ModSeq);
            Assert.IsTrue(result.Updated[0].IsRead);
            CollectionAssert.AreEqual(new[] { "$Tag" }, result.Updated[0].Keywords);
        }

        await using (var database = new EmailDbContext(options))
        {
            var application = CreateApplication(database);
            var result = await application.StoreFlagsAsync(new ImapStoreRequest(
                userId, folderId, true,
                new ImapMessageSelection(null, [1, 3]),
                null, ImapFlagMutationMode.Replace, ["\\Flagged"]));
            Assert.AreEqual(ImapStoreDisposition.Stored, result.Disposition);
            Assert.IsEmpty(result.Modified);
            CollectionAssert.AreEqual(new[] { 1, 3 }, result.Updated
                .Select(message => message.Uid).ToArray());
            Assert.IsTrue(result.Updated.All(message => message.IsFlagged && message.ModSeq == 6));
        }

        await using (var database = new EmailDbContext(options))
        {
            var application = CreateApplication(database);
            var keywords = Enumerable.Range(0, 129)
                .Select(index => $"$Tag{index}")
                .ToArray();
            var limited = await application.StoreFlagsAsync(new ImapStoreRequest(
                userId, folderId, true,
                new ImapMessageSelection([new ImapMessageRange(1, 3)], null),
                null, ImapFlagMutationMode.Add, keywords));
            Assert.AreEqual(ImapStoreDisposition.KeywordLimitExceeded, limited.Disposition);
            Assert.IsEmpty(limited.Updated);
            Assert.IsEmpty(limited.Modified);
        }
        await using (var database = new EmailDbContext(options))
        {
            Assert.AreEqual(6L, await database.Folders
                .Where(folder => folder.Id == folderId)
                .Select(folder => folder.HighestModSeq)
                .SingleAsync());
            Assert.IsFalse(await database.Emails
                .Where(email => email.Uid == 1)
                .Select(email => email.IsDeleted)
                .SingleAsync());
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
                """);
        }

        await using (var database = new EmailDbContext(options))
        {
            var application = CreateApplication(database);
            await Assert.ThrowsAsync<DbUpdateException>(() => application.StoreFlagsAsync(
                new ImapStoreRequest(userId, folderId, true,
                    new ImapMessageSelection([new ImapMessageRange(1, 1)], null),
                    null, ImapFlagMutationMode.Add, ["\\Deleted"])));
        }
        await using (var database = new EmailDbContext(options))
        {
            Assert.AreEqual(6L, await database.Folders
                .Where(folder => folder.Id == folderId)
                .Select(folder => folder.HighestModSeq)
                .SingleAsync());
            Assert.IsFalse(await database.Emails
                .Where(email => email.Uid == 1)
                .Select(email => email.IsDeleted)
                .SingleAsync());
            await database.Database.ExecuteSqlRawAsync(
                "DROP TRIGGER reject_imap_store ON emails; "
                + "DROP FUNCTION reject_imap_store();");
        }

        await using (var database = new EmailDbContext(options))
        {
            var application = CreateApplication(database);
            var stored = await application.StoreFlagsAsync(new ImapStoreRequest(
                userId, folderId, true,
                new ImapMessageSelection([new ImapMessageRange(1, 1)], null),
                null, ImapFlagMutationMode.Add, ["\\Deleted"]));
            Assert.AreEqual(ImapStoreDisposition.Stored, stored.Disposition);
            Assert.HasCount(1, stored.Updated);
            Assert.IsTrue(stored.Updated[0].IsDeleted);
            Assert.AreEqual(7L, stored.Updated[0].ModSeq);
        }
        await using (var database = new EmailDbContext(options))
        {
            Assert.AreEqual(7L, await database.Folders
                .Where(folder => folder.Id == folderId)
                .Select(folder => folder.HighestModSeq)
                .SingleAsync());
            var messages = await database.Emails
                .OrderBy(email => email.Uid)
                .ToListAsync();
            Assert.HasCount(3, messages);
            Assert.IsTrue(messages[0].IsDeleted);
            Assert.IsTrue(messages[0].IsFlagged);
            Assert.IsTrue(messages[1].IsRead);
            CollectionAssert.AreEqual(new[] { "$Tag" }, messages[1].Keywords);
            Assert.IsTrue(messages[2].IsFlagged);
        }
    }

    private static ImapApplicationService CreateApplication(EmailDbContext database) => new(
        null!, null!, database, null!, null!, NullLogger<ImapApplicationService>.Instance);
}
