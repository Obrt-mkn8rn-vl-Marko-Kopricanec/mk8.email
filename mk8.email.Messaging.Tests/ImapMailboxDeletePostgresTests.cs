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
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1812", Justification = "MSTest DiscoverInternals instantiates this test class by reflection; focused discovery is verified by executed test counts.")]
internal sealed class ImapMailboxDeletePostgresTests
{
    [TestMethod]
    [Timeout(20_000)]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "MA0051", Justification = "The FailedCommitRetainsTheMessageBlobAndSuccessfulDeleteRemovesIt scenario keeps one fixture's ordered setup, operation and invariant assertions together.")]
    public async Task FailedCommitRetainsTheMessageBlobAndSuccessfulDeleteRemovesIt()
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
        var folderId = Guid.CreateVersion7();
        var objects = new InMemoryLargeObjectStore();
        {
            var database = new EmailDbContext(options);
            await using var databaseLifetime = database.ConfigureAwait(false);
            await database.Database.EnsureCreatedAsync().ConfigureAwait(false);
            await new MailRuntimeSchemaService(database).EnsureAsync().ConfigureAwait(false);
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
                CancellationToken.None).ConfigureAwait(false);
            await (database.Emails.AddAsync(message)).ConfigureAwait(false);
            await database.SaveChangesAsync().ConfigureAwait(false);
            await effects.CommitAsync(marker).ConfigureAwait(false);
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
                """).ConfigureAwait(false);
        }

        {
            var database = new EmailDbContext(options);
            await using var databaseLifetime = database.ConfigureAwait(false);
            var effects = CreateEffects(objects);
            var application = new ImapApplicationService(
                null!, null!, database,
                new MailboxMessageContentService(objects, effects),
                effects,
                NullLogger<ImapApplicationService>.Instance);
            await Assert.ThrowsAsync<DbUpdateException>(() => application.DeleteMailboxAsync(
                new ImapMailboxDeleteRequest(userId, "Projects"))).ConfigureAwait(false);
        }
        Assert.AreEqual(1, objects.ObjectCount);
        {
            var database = new EmailDbContext(options);
            await using var databaseLifetime = database.ConfigureAwait(false);
            Assert.AreEqual(1, await database.Folders.CountAsync(folder => folder.Id == folderId).ConfigureAwait(false));
            Assert.AreEqual(1, await database.Emails.CountAsync(email => email.FolderId == folderId).ConfigureAwait(false));
            await database.Database.ExecuteSqlRawAsync(
                "DROP TRIGGER reject_imap_mailbox_delete ON folders; "
                + "DROP FUNCTION reject_imap_mailbox_delete();").ConfigureAwait(false);
        }

        {
            var database = new EmailDbContext(options);
            await using var databaseLifetime = database.ConfigureAwait(false);
            var effects = CreateEffects(objects);
            var application = new ImapApplicationService(
                null!, null!, database,
                new MailboxMessageContentService(objects, effects),
                effects,
                NullLogger<ImapApplicationService>.Instance);
            var deleted = await application.DeleteMailboxAsync(
                new ImapMailboxDeleteRequest(userId, "Projects")).ConfigureAwait(false);
            Assert.AreEqual(ImapMailboxDeleteDisposition.Deleted, deleted.Disposition);
            Assert.AreEqual(folderId, deleted.FolderId);
        }
        Assert.AreEqual(0, objects.ObjectCount);
        Assert.AreEqual(1, objects.DeleteCount);
        {
            var database = new EmailDbContext(options);
            await using var databaseLifetime = database.ConfigureAwait(false);
            Assert.AreEqual(0, await database.Folders.CountAsync(folder => folder.Id == folderId).ConfigureAwait(false));
            Assert.AreEqual(0, await database.Emails.CountAsync(email => email.FolderId == folderId).ConfigureAwait(false));
        }
    }

    private static LargeObjectTransactionEffects CreateEffects(InMemoryLargeObjectStore objects) =>
        new(objects, NullLogger<LargeObjectTransactionEffects>.Instance);
}
