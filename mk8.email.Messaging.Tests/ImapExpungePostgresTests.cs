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
public sealed class ImapExpungePostgresTests
{
    [TestMethod]
    [Timeout(20_000)]
    public async Task ExpungeIsOwnerScopedAndDeletesBlobsOnlyAfterCommit()
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
        var objects = new InMemoryLargeObjectStore();
        await using (var database = new EmailDbContext(options))
        {
            await database.Database.EnsureCreatedAsync();
            await new MailRuntimeSchemaService(database).EnsureAsync();
            var company = new CompanyDB
            {
                Id = Guid.CreateVersion7(),
                Name = "IMAP expunge test",
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
                NextUid = 5,
                Inbox = new InboxDB
                {
                    Id = Guid.CreateVersion7(),
                    Name = "owner",
                    Address = address,
                    Owner = user,
                },
            };
            var effects = CreateEffects(objects);
            var content = new MailboxMessageContentService(objects, effects);
            var marker = effects.Mark();
            for (var uid = 1; uid <= 4; uid++)
            {
                var message = new EmailDB
                {
                    Id = Guid.CreateVersion7(),
                    Folder = folder,
                    Uid = uid,
                    IsDeleted = uid is 2 or 3 or 4,
                    Sender = "sender@example.test",
                    Recipient = "owner@example.test",
                    Subject = $"Message {uid}",
                };
                await content.SetAsync(message, Encoding.UTF8.GetBytes(
                    $"From: sender@example.test\r\nSubject: Message {uid}\r\n\r\nBody\r\n"),
                    CancellationToken.None);
                database.Emails.Add(message);
            }
            await database.SaveChangesAsync();
            await effects.CommitAsync(marker);
            Assert.AreEqual(4, objects.ObjectCount);
        }

        await using (var database = new EmailDbContext(options))
        {
            var effects = CreateEffects(objects);
            var application = CreateApplication(database, objects, effects);
            var denied = await application.ExpungeDeletedAsync(
                new ImapExpungeRequest(otherUserId, folderId));
            Assert.IsFalse(denied.FolderFound);
            Assert.IsEmpty(denied.Messages);
            Assert.AreEqual(4, objects.ObjectCount);
            await Assert.ThrowsAsync<ArgumentException>(() => application.ExpungeDeletedAsync(
                new ImapExpungeRequest(Guid.Empty, folderId)));
            await Assert.ThrowsAsync<ArgumentException>(() => application.ExpungeDeletedAsync(
                new ImapExpungeRequest(userId, folderId, new ImapUidSelection([], null))));
            await Assert.ThrowsAsync<ArgumentException>(() => application.ExpungeDeletedAsync(
                new ImapExpungeRequest(userId, folderId,
                    new ImapUidSelection([new ImapUidRange(0, 1)], null))));
            await database.Database.ExecuteSqlRawAsync(
                """
                CREATE FUNCTION reject_imap_expunge() RETURNS trigger AS $$
                BEGIN
                    RAISE EXCEPTION 'test rollback of IMAP expunge';
                END;
                $$ LANGUAGE plpgsql;
                CREATE TRIGGER reject_imap_expunge
                BEFORE DELETE ON emails
                FOR EACH ROW EXECUTE FUNCTION reject_imap_expunge();
                """);
        }

        await using (var database = new EmailDbContext(options))
        {
            var effects = CreateEffects(objects);
            var application = CreateApplication(database, objects, effects);
            await Assert.ThrowsAsync<DbUpdateException>(() => application.ExpungeDeletedAsync(
                new ImapExpungeRequest(userId, folderId)));
        }
        Assert.AreEqual(4, objects.ObjectCount);
        Assert.AreEqual(0, objects.DeleteCount);
        await using (var database = new EmailDbContext(options))
        {
            Assert.AreEqual(4, await database.Emails.CountAsync());
            Assert.AreEqual(0, await database.ExpungedUids.CountAsync());
            Assert.AreEqual(4L, await database.Folders
                .Where(folder => folder.Id == folderId)
                .Select(folder => folder.HighestModSeq)
                .SingleAsync());
            await database.Database.ExecuteSqlRawAsync(
                "DROP TRIGGER reject_imap_expunge ON emails; "
                + "DROP FUNCTION reject_imap_expunge();");
        }

        await using (var database = new EmailDbContext(options))
        {
            var effects = CreateEffects(objects);
            var application = CreateApplication(database, objects, effects);
            var highest = await application.ExpungeDeletedAsync(
                new ImapExpungeRequest(userId, folderId,
                    new ImapUidSelection([new ImapUidRange(null, null)], null)));
            CollectionAssert.AreEqual(
                new[] { new ImapExpungedMessage(4, 4) }, highest.Messages);
            var saved = await application.ExpungeDeletedAsync(
                new ImapExpungeRequest(userId, folderId,
                    new ImapUidSelection(null, [3])));
            CollectionAssert.AreEqual(
                new[] { new ImapExpungedMessage(3, 3) }, saved.Messages);
            var remaining = await application.ExpungeDeletedAsync(
                new ImapExpungeRequest(userId, folderId));
            Assert.IsTrue(remaining.FolderFound);
            CollectionAssert.AreEqual(
                new[] { new ImapExpungedMessage(2, 2) }, remaining.Messages);
            var repeat = await application.ExpungeDeletedAsync(
                new ImapExpungeRequest(userId, folderId));
            Assert.IsTrue(repeat.FolderFound);
            Assert.IsEmpty(repeat.Messages);
        }
        Assert.AreEqual(1, objects.ObjectCount);
        Assert.AreEqual(3, objects.DeleteCount);
        await using (var database = new EmailDbContext(options))
        {
            CollectionAssert.AreEqual(new[] { 1 }, await database.Emails
                .OrderBy(email => email.Uid)
                .Select(email => email.Uid)
                .ToListAsync());
            CollectionAssert.AreEqual(new[] { 2, 3, 4 }, await database.ExpungedUids
                .OrderBy(expunged => expunged.Uid)
                .Select(expunged => expunged.Uid)
                .ToListAsync());
            CollectionAssert.AreEqual(new long[] { 7, 6, 5 }, await database.ExpungedUids
                .OrderBy(expunged => expunged.Uid)
                .Select(expunged => expunged.ModSeq)
                .ToListAsync());
            Assert.AreEqual(7L, await database.Folders
                .Where(folder => folder.Id == folderId)
                .Select(folder => folder.HighestModSeq)
                .SingleAsync());
        }
    }

    private static ImapApplicationService CreateApplication(
        EmailDbContext database,
        InMemoryLargeObjectStore objects,
        LargeObjectTransactionEffects effects) => new(
        null!, null!, database,
        new MailboxMessageContentService(objects, effects),
        effects,
        NullLogger<ImapApplicationService>.Instance);

    private static LargeObjectTransactionEffects CreateEffects(InMemoryLargeObjectStore objects) =>
        new(objects, NullLogger<LargeObjectTransactionEffects>.Instance);
}
