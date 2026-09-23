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
public sealed class ImapMailboxDeletePostgresTests
{
    [TestMethod]
    [Timeout(20_000)]
    public async Task FailedCommitRetainsTheMessageBlobAndSuccessfulDeleteRemovesIt()
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
        var folderId = Guid.CreateVersion7();
        var objects = new InMemoryLargeObjectStore();
        await using (var database = new EmailDbContext(options))
        {
            await database.Database.EnsureCreatedAsync();
            await new MailRuntimeSchemaService(database).EnsureAsync();
            var company = new CompanyDB
            {
                Id = Guid.CreateVersion7(),
                Name = "IMAP delete test",
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
            var inbox = new InboxDB
            {
                Id = Guid.CreateVersion7(),
                Name = "owner",
                Address = address,
                Owner = user,
            };
            var folder = new FolderDB
            {
                Id = folderId,
                Name = "Projects",
                Inbox = inbox,
            };
            var message = new EmailDB
            {
                Id = Guid.CreateVersion7(),
                Folder = folder,
                Uid = 1,
                Sender = "sender@example.test",
                Recipient = "owner@example.test",
                Subject = "Blob cleanup",
            };
            var effects = CreateEffects(objects);
            var content = new MailboxMessageContentService(objects, effects);
            var marker = effects.Mark();
            await content.SetAsync(message, Encoding.UTF8.GetBytes(
                "From: sender@example.test\r\nTo: owner@example.test\r\nSubject: Blob cleanup\r\n\r\nBody\r\n"),
                CancellationToken.None);
            database.Emails.Add(message);
            await database.SaveChangesAsync();
            await effects.CommitAsync(marker);
            Assert.AreEqual(1, objects.ObjectCount);
            Assert.IsNull(message.RawMessage);

            await database.Database.ExecuteSqlRawAsync(
                """
                CREATE FUNCTION reject_imap_mailbox_delete() RETURNS trigger AS $$
                BEGIN
                    RAISE EXCEPTION 'test rollback of IMAP mailbox deletion';
                END;
                $$ LANGUAGE plpgsql;
                CREATE TRIGGER reject_imap_mailbox_delete
                BEFORE DELETE ON folders
                FOR EACH ROW EXECUTE FUNCTION reject_imap_mailbox_delete();
                """);
        }

        await using (var database = new EmailDbContext(options))
        {
            var effects = CreateEffects(objects);
            var application = new ImapApplicationService(
                null!, null!, database,
                new MailboxMessageContentService(objects, effects),
                effects,
                NullLogger<ImapApplicationService>.Instance);
            await Assert.ThrowsAsync<DbUpdateException>(() => application.DeleteMailboxAsync(
                new ImapMailboxDeleteRequest(userId, "Projects")));
        }
        Assert.AreEqual(1, objects.ObjectCount);
        await using (var database = new EmailDbContext(options))
        {
            Assert.AreEqual(1, await database.Folders.CountAsync(folder => folder.Id == folderId));
            Assert.AreEqual(1, await database.Emails.CountAsync(email => email.FolderId == folderId));
            await database.Database.ExecuteSqlRawAsync(
                "DROP TRIGGER reject_imap_mailbox_delete ON folders; "
                + "DROP FUNCTION reject_imap_mailbox_delete();");
        }

        await using (var database = new EmailDbContext(options))
        {
            var effects = CreateEffects(objects);
            var application = new ImapApplicationService(
                null!, null!, database,
                new MailboxMessageContentService(objects, effects),
                effects,
                NullLogger<ImapApplicationService>.Instance);
            var deleted = await application.DeleteMailboxAsync(
                new ImapMailboxDeleteRequest(userId, "Projects"));
            Assert.AreEqual(ImapMailboxDeleteDisposition.Deleted, deleted.Disposition);
            Assert.AreEqual(folderId, deleted.FolderId);
        }
        Assert.AreEqual(0, objects.ObjectCount);
        Assert.AreEqual(1, objects.DeleteCount);
        await using (var database = new EmailDbContext(options))
        {
            Assert.AreEqual(0, await database.Folders.CountAsync(folder => folder.Id == folderId));
            Assert.AreEqual(0, await database.Emails.CountAsync(email => email.FolderId == folderId));
        }
    }

    private static LargeObjectTransactionEffects CreateEffects(InMemoryLargeObjectStore objects) =>
        new(objects, NullLogger<LargeObjectTransactionEffects>.Instance);
}
