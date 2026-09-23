using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using mk8.email.Application.Services;
using mk8.email.Contracts.Imap;
using mk8.email.Infrastructure.Data;
using mk8.email.Infrastructure.Models;

namespace mk8.email.Messaging.Tests;

[TestClass]
[TestCategory("PostgreSQL")]
public sealed class ImapCopyPostgresTests
{
    [TestMethod]
    [Timeout(20_000)]
    public async Task CopyIsOwnerScopedQuotaCheckedAndCommitsNewBlobOnlyWithDatabase()
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
        var sourceId = Guid.CreateVersion7();
        var destinationId = Guid.CreateVersion7();
        var sourceMessageId = Guid.CreateVersion7();
        var rawMessage = Encoding.UTF8.GetBytes(
            "From: sender@example.test\r\nSubject: Copy blob\r\n\r\nBody\r\n");
        var objects = new InMemoryLargeObjectStore();
        await using (var database = new EmailDbContext(options))
        {
            await database.Database.EnsureCreatedAsync();
            await new MailRuntimeSchemaService(database).EnsureAsync();
            var company = new CompanyDB
            {
                Id = Guid.CreateVersion7(),
                Name = "IMAP COPY test",
                IsActive = true,
            };
            var address = new AddressDB
            {
                Id = Guid.CreateVersion7(),
                Domain = "example.test",
                IsActive = true,
                Company = company,
            };
            var owner = new UserDB
            {
                Id = userId,
                Username = "owner@example.test",
                PasswordHash = "unused",
                Role = "User",
                IsActive = true,
                QuotaBytes = rawMessage.LongLength * 2 - 1,
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
            var inbox = new InboxDB
            {
                Id = Guid.CreateVersion7(),
                Name = "owner",
                Address = address,
                Owner = owner,
            };
            var sourceFolder = new FolderDB
            {
                Id = sourceId,
                Name = "Inbox",
                NextUid = 2,
                HighestModSeq = 1,
                Inbox = inbox,
            };
            database.Folders.Add(new FolderDB
            {
                Id = destinationId,
                Name = "Archive",
                UidValidity = 23,
                NextUid = 10,
                HighestModSeq = 5,
                Inbox = inbox,
            });
            var source = new EmailDB
            {
                Id = sourceMessageId,
                Folder = sourceFolder,
                Uid = 1,
                ModSeq = 1,
                Sender = "sender@example.test",
                Recipient = "owner@example.test",
                Subject = "Copy blob",
                IsDeleted = true,
            };
            var effects = CreateEffects(objects);
            var content = new MailboxMessageContentService(objects, effects);
            var marker = effects.Mark();
            await content.SetAsync(source, rawMessage, CancellationToken.None);
            database.Emails.Add(source);
            await database.SaveChangesAsync();
            await effects.CommitAsync(marker);
        }
        Assert.AreEqual(1, objects.ObjectCount);

        await using (var database = new EmailDbContext(options))
        {
            var application = CreateApplication(database, objects);
            var selection = new ImapMessageSelection([new ImapMessageRange(1, null)], null);
            Assert.AreEqual(ImapCopyDisposition.SourceNotFound,
                (await application.CopyMessagesAsync(new ImapCopyRequest(
                    otherUserId, sourceId, "Archive", true, selection))).Disposition);
            Assert.AreEqual(ImapCopyDisposition.DestinationNotFound,
                (await application.CopyMessagesAsync(new ImapCopyRequest(
                    userId, sourceId, "Missing", true, selection))).Disposition);
            var empty = await application.CopyMessagesAsync(new ImapCopyRequest(
                userId, sourceId, "Archive", true,
                new ImapMessageSelection([new ImapMessageRange(99, 99)], null)));
            Assert.AreEqual(ImapCopyDisposition.Copied, empty.Disposition);
            Assert.IsEmpty(empty.SourceUids);
            Assert.AreEqual(ImapCopyDisposition.OverQuota,
                (await application.CopyMessagesAsync(new ImapCopyRequest(
                    userId, sourceId, "Archive", true, selection))).Disposition);
            await Assert.ThrowsAsync<ArgumentException>(() => application.CopyMessagesAsync(
                new ImapCopyRequest(userId, sourceId, "Archive", true,
                    new ImapMessageSelection([], null))));
            var owner = await database.Users.SingleAsync(user => user.Id == userId);
            owner.QuotaBytes = 0;
            await database.SaveChangesAsync();
            await database.Database.ExecuteSqlRawAsync(
                """
                CREATE FUNCTION reject_imap_copy() RETURNS trigger AS $$
                BEGIN
                    RAISE EXCEPTION 'test rollback of IMAP COPY';
                END;
                $$ LANGUAGE plpgsql;
                CREATE TRIGGER reject_imap_copy
                BEFORE INSERT ON emails
                FOR EACH ROW EXECUTE FUNCTION reject_imap_copy();
                """);
        }

        await using (var database = new EmailDbContext(options))
        {
            var application = CreateApplication(database, objects);
            await Assert.ThrowsAsync<DbUpdateException>(() => application.CopyMessagesAsync(
                new ImapCopyRequest(userId, sourceId, "Archive", true,
                    new ImapMessageSelection([new ImapMessageRange(null, null)], null))));
        }
        Assert.AreEqual(1, objects.ObjectCount);
        Assert.AreEqual(1, objects.DeleteCount);
        await using (var database = new EmailDbContext(options))
        {
            Assert.AreEqual(1, await database.Emails.CountAsync());
            Assert.AreEqual(10, await database.Folders
                .Where(folder => folder.Id == destinationId)
                .Select(folder => folder.NextUid)
                .SingleAsync());
            await database.Database.ExecuteSqlRawAsync(
                "DROP TRIGGER reject_imap_copy ON emails; "
                + "DROP FUNCTION reject_imap_copy();");
        }

        await using (var database = new EmailDbContext(options))
        {
            var application = CreateApplication(database, objects);
            var copied = await application.CopyMessagesAsync(new ImapCopyRequest(
                userId, sourceId, "Archive", true,
                new ImapMessageSelection([new ImapMessageRange(null, null)], null)));
            Assert.AreEqual(ImapCopyDisposition.Copied, copied.Disposition);
            Assert.AreEqual(23, copied.DestinationUidValidity);
            CollectionAssert.AreEqual(new[] { 1 }, copied.SourceUids);
            CollectionAssert.AreEqual(new[] { 10 }, copied.DestinationUids);
        }
        Assert.AreEqual(2, objects.ObjectCount);
        await using (var database = new EmailDbContext(options))
        {
            var source = await database.Emails.SingleAsync(message => message.Id == sourceMessageId);
            var copy = await database.Emails.SingleAsync(message => message.FolderId == destinationId);
            Assert.AreEqual(1, source.Uid);
            Assert.IsTrue(source.IsDeleted);
            Assert.AreEqual(10, copy.Uid);
            Assert.IsFalse(copy.IsDeleted);
            Assert.AreNotEqual(source.Id, copy.Id);
            Assert.AreNotEqual(source.RawMessageObjectName, copy.RawMessageObjectName);
            Assert.IsNotNull(copy.RawMessageObjectName);
            Assert.IsNull(copy.RawMessage);
            var effects = CreateEffects(objects);
            var content = new MailboxMessageContentService(objects, effects);
            CollectionAssert.AreEqual(rawMessage, await content.ReadAsync(
                source, CancellationToken.None));
            CollectionAssert.AreEqual(rawMessage, await content.ReadAsync(
                copy, CancellationToken.None));
            Assert.AreEqual(11, await database.Folders
                .Where(folder => folder.Id == destinationId)
                .Select(folder => folder.NextUid)
                .SingleAsync());
            Assert.AreEqual(6L, await database.Folders
                .Where(folder => folder.Id == destinationId)
                .Select(folder => folder.HighestModSeq)
                .SingleAsync());
        }
    }

    private static ImapApplicationService CreateApplication(
        EmailDbContext database,
        InMemoryLargeObjectStore objects)
    {
        var effects = CreateEffects(objects);
        return new ImapApplicationService(
            null!, null!, database,
            new MailboxMessageContentService(objects, effects),
            effects,
            NullLogger<ImapApplicationService>.Instance);
    }

    private static LargeObjectTransactionEffects CreateEffects(InMemoryLargeObjectStore objects) =>
        new(objects, NullLogger<LargeObjectTransactionEffects>.Instance);
}
