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
public sealed class ImapAppendPostgresTests
{
    [TestMethod]
    [Timeout(30_000)]
    public async Task AppendIsOwnerScopedAtomicBlobBackedAndIdempotent()
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
        var ownerId = Guid.CreateVersion7();
        var otherId = Guid.CreateVersion7();
        var folderId = Guid.CreateVersion7();
        var raw1 = Encoding.ASCII.GetBytes(
            "From: sender@example.test\r\nTo: owner@example.test\r\n"
            + "Subject: First\r\nMessage-ID: <first@example.test>\r\n\r\nBody 1\r\n");
        var raw2 = Encoding.ASCII.GetBytes(
            "From: sender@example.test\r\nTo: owner@example.test\r\n"
            + "Subject: Second\r\nIn-Reply-To: <first@example.test>\r\n\r\nBody 2\r\n");
        var items = new List<ImapAppendMessage>
        {
            new(Guid.CreateVersion7(), ["\\Seen", "$Label1"], null, raw1),
            new(Guid.CreateVersion7(), ["\\Flagged"], null, raw2),
        };
        var request = new ImapAppendRequest(ownerId, "INBOX", false, items);
        var objects = new InMemoryLargeObjectStore();
        await using (var database = new EmailDbContext(options))
        {
            await database.Database.EnsureCreatedAsync();
            await new MailRuntimeSchemaService(database).EnsureAsync();
            var company = new CompanyDB
            {
                Id = Guid.CreateVersion7(),
                Name = "IMAP APPEND test",
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
                Id = ownerId,
                Username = "owner@example.test",
                PasswordHash = "unused",
                Role = "User",
                IsActive = true,
                QuotaBytes = raw1.Length + raw2.Length - 1,
                Company = company,
            };
            database.Users.Add(new UserDB
            {
                Id = otherId,
                Username = "other@example.test",
                PasswordHash = "unused",
                Role = "User",
                IsActive = true,
                Company = company,
            });
            database.Folders.Add(new FolderDB
            {
                Id = folderId,
                Name = "Inbox",
                UidValidity = 42,
                NextUid = 7,
                HighestModSeq = 4,
                Inbox = new InboxDB
                {
                    Id = Guid.CreateVersion7(),
                    Name = "owner",
                    Address = address,
                    Owner = owner,
                },
            });
            await database.SaveChangesAsync();
        }

        await using (var database = new EmailDbContext(options))
        {
            var application = CreateApplication(database, objects);
            Assert.AreEqual(ImapAppendDisposition.MailboxNotFound,
                (await application.AppendMessagesAsync(request with { UserId = otherId })).Disposition);
            Assert.AreEqual(ImapAppendDisposition.MailboxNotFound,
                (await application.AppendMessagesAsync(request with { MailboxName = "Missing" })).Disposition);
            Assert.AreEqual(ImapAppendDisposition.InvalidFlags,
                (await application.AppendMessagesAsync(request with
                {
                    Messages = [items[0] with { Flags = ["\\Recent"] }],
                })).Disposition);
            Assert.AreEqual(ImapAppendDisposition.InvalidContent,
                (await application.AppendMessagesAsync(request with
                {
                    Messages = [items[0] with { RawMessage = "bad\0message"u8.ToArray() }],
                })).Disposition);
            Assert.AreEqual(ImapAppendDisposition.OverQuota,
                (await application.AppendMessagesAsync(request)).Disposition);
            await Assert.ThrowsAsync<ArgumentException>(() => application.AppendMessagesAsync(
                request with { Messages = [items[0], items[0]] }));
            var owner = await database.Users.SingleAsync(user => user.Id == ownerId);
            owner.QuotaBytes = 0;
            await database.SaveChangesAsync();
            await database.Database.ExecuteSqlRawAsync(
                """
                CREATE FUNCTION reject_imap_append() RETURNS trigger AS $$
                BEGIN
                    RAISE EXCEPTION 'test rollback of IMAP APPEND';
                END;
                $$ LANGUAGE plpgsql;
                CREATE TRIGGER reject_imap_append
                BEFORE INSERT ON emails
                FOR EACH ROW EXECUTE FUNCTION reject_imap_append();
                """);
        }

        await using (var database = new EmailDbContext(options))
        {
            var application = CreateApplication(database, objects);
            await Assert.ThrowsAsync<DbUpdateException>(() =>
                application.AppendMessagesAsync(request));
        }
        Assert.AreEqual(0, objects.ObjectCount);
        Assert.AreEqual(2, objects.DeleteCount);
        await using (var database = new EmailDbContext(options))
        {
            Assert.AreEqual(0, await database.Emails.CountAsync());
            Assert.AreEqual(7, await database.Folders
                .Where(folder => folder.Id == folderId)
                .Select(folder => folder.NextUid)
                .SingleAsync());
            await database.Database.ExecuteSqlRawAsync(
                "DROP TRIGGER reject_imap_append ON emails; "
                + "DROP FUNCTION reject_imap_append();");
        }

        await using (var database = new EmailDbContext(options))
        {
            var application = CreateApplication(database, objects);
            var appended = await application.AppendMessagesAsync(request);
            Assert.AreEqual(ImapAppendDisposition.Appended, appended.Disposition);
            Assert.AreEqual(42, appended.UidValidity);
            CollectionAssert.AreEqual(new[] { 7, 8 }, appended.Uids);
        }
        Assert.AreEqual(2, objects.ObjectCount);
        await using (var database = new EmailDbContext(options))
        {
            var application = CreateApplication(database, objects);
            var replayed = await application.AppendMessagesAsync(request);
            CollectionAssert.AreEqual(new[] { 7, 8 }, replayed.Uids);
            Assert.AreEqual(2, objects.ObjectCount);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                application.AppendMessagesAsync(request with
                {
                    Messages = [items[0] with { RawMessage = raw2 }, items[1]],
                }));
            var stored = await database.Emails.OrderBy(email => email.Uid).ToListAsync();
            Assert.HasCount(2, stored);
            Assert.IsNull(stored[0].RawMessage);
            Assert.IsNull(stored[1].RawMessage);
            Assert.IsNotNull(stored[0].RawMessageObjectName);
            Assert.IsNotNull(stored[1].RawMessageObjectName);
            Assert.IsTrue(stored[0].IsRead);
            Assert.IsTrue(stored[1].IsFlagged);
            Assert.AreEqual("First", stored[0].Subject);
            Assert.AreEqual("Second", stored[1].Subject);
            var effects = new LargeObjectTransactionEffects(
                objects, NullLogger<LargeObjectTransactionEffects>.Instance);
            var content = new MailboxMessageContentService(objects, effects);
            CollectionAssert.AreEqual(raw1, await content.ReadAsync(stored[0], CancellationToken.None));
            CollectionAssert.AreEqual(raw2, await content.ReadAsync(stored[1], CancellationToken.None));
            Assert.AreEqual(9, await database.Folders
                .Where(folder => folder.Id == folderId)
                .Select(folder => folder.NextUid)
                .SingleAsync());
        }
    }

    private static ImapApplicationService CreateApplication(
        EmailDbContext database,
        InMemoryLargeObjectStore objects)
    {
        var effects = new LargeObjectTransactionEffects(
            objects, NullLogger<LargeObjectTransactionEffects>.Instance);
        return new ImapApplicationService(
            null!, null!, database,
            new MailboxMessageContentService(objects, effects),
            effects, NullLogger<ImapApplicationService>.Instance);
    }
}
