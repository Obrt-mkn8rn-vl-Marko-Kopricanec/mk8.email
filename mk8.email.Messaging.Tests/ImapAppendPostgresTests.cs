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
internal sealed class ImapAppendPostgresTests
{
    [TestMethod]
    [Timeout(30_000)]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "MA0051", Justification = "The AppendIsOwnerScopedAtomicBlobBackedAndIdempotent scenario keeps one fixture's ordered setup, operation and invariant assertions together.")]
    public async Task AppendIsOwnerScopedAtomicBlobBackedAndIdempotent()
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
            new(Guid.CreateVersion7(), ["\\Seen", "$Label1"], new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc), raw1),
            new(Guid.CreateVersion7(), ["\\Flagged"], null, raw2),
        };
        var request = new ImapAppendRequest(ownerId, "INBOX", false, items);
        var objects = new InMemoryLargeObjectStore();
        {
            var database = new EmailDbContext(options);
            await using var databaseLifetime = database.ConfigureAwait(false);
            await database.Database.EnsureCreatedAsync().ConfigureAwait(false);
            await new MailRuntimeSchemaService(database).EnsureAsync().ConfigureAwait(false);
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
            await (database.Users.AddAsync(new UserDB
            {
                Id = otherId,
                Username = "other@example.test",
                PasswordHash = "unused",
                Role = "User",
                IsActive = true,
                Company = company,
            })).ConfigureAwait(false);
            await (database.Folders.AddAsync(new FolderDB
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
            })).ConfigureAwait(false);
            await database.SaveChangesAsync().ConfigureAwait(false);
        }

        {
            var database = new EmailDbContext(options);
            await using var databaseLifetime = database.ConfigureAwait(false);
            var application = CreateApplication(database, objects);
            Assert.AreEqual(ImapAppendDisposition.MailboxNotFound,
                (await application.AppendMessagesAsync(request with { UserId = otherId }).ConfigureAwait(false)).Disposition);
            Assert.AreEqual(ImapAppendDisposition.MailboxNotFound,
                (await application.AppendMessagesAsync(request with { MailboxName = "Missing" }).ConfigureAwait(false)).Disposition);
            Assert.AreEqual(ImapAppendDisposition.InvalidFlags,
                (await application.AppendMessagesAsync(request with
                {
                    Messages = [items[0] with { Flags = ["\\Recent"] }],
                }).ConfigureAwait(false)).Disposition);
            Assert.AreEqual(ImapAppendDisposition.InvalidContent,
                (await application.AppendMessagesAsync(request with
                {
                    Messages = [items[0] with { RawMessage = "bad\0message"u8.ToArray() }],
                }).ConfigureAwait(false)).Disposition);
            Assert.AreEqual(ImapAppendDisposition.OverQuota,
                (await application.AppendMessagesAsync(request).ConfigureAwait(false)).Disposition);
            await Assert.ThrowsAsync<ArgumentException>(() => application.AppendMessagesAsync(
                request with { Messages = [items[0], items[0]] })).ConfigureAwait(false);
            var owner = await database.Users.SingleAsync(user => user.Id == ownerId).ConfigureAwait(false);
            owner.QuotaBytes = 0;
            await database.SaveChangesAsync().ConfigureAwait(false);
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
                """).ConfigureAwait(false);
        }

        {
            var database = new EmailDbContext(options);
            await using var databaseLifetime = database.ConfigureAwait(false);
            var application = CreateApplication(database, objects);
            await Assert.ThrowsAsync<DbUpdateException>(() =>
                application.AppendMessagesAsync(request)).ConfigureAwait(false);
        }
        Assert.AreEqual(0, objects.ObjectCount);
        Assert.AreEqual(2, objects.DeleteCount);
        {
            var database = new EmailDbContext(options);
            await using var databaseLifetime = database.ConfigureAwait(false);
            Assert.AreEqual(0, await database.Emails.CountAsync().ConfigureAwait(false));
            Assert.AreEqual(7, await database.Folders
                .Where(folder => folder.Id == folderId)
                .Select(folder => folder.NextUid)
                .SingleAsync().ConfigureAwait(false));
            await database.Database.ExecuteSqlRawAsync(
                "DROP TRIGGER reject_imap_append ON emails; "
                + "DROP FUNCTION reject_imap_append();").ConfigureAwait(false);
        }

        {
            var database = new EmailDbContext(options);
            await using var databaseLifetime = database.ConfigureAwait(false);
            var application = CreateApplication(database, objects);
            var appended = await application.AppendMessagesAsync(request).ConfigureAwait(false);
            Assert.AreEqual(ImapAppendDisposition.Appended, appended.Disposition);
            Assert.AreEqual(42, appended.UidValidity);
            CollectionAssert.AreEqual(ExpectedVector1, appended.Uids);
        }
        Assert.AreEqual(2, objects.ObjectCount);
        {
            var database = new EmailDbContext(options);
            await using var databaseLifetime = database.ConfigureAwait(false);
            var application = CreateApplication(database, objects);
            var replayed = await application.AppendMessagesAsync(request).ConfigureAwait(false);
            CollectionAssert.AreEqual(ExpectedVector1, replayed.Uids);
            Assert.AreEqual(ImapAppendDisposition.Appended, replayed.Disposition);
            Assert.AreEqual(42, replayed.UidValidity);
            var owner = await database.Users.SingleAsync(user => user.Id == ownerId).ConfigureAwait(false);
            owner.QuotaBytes = 1;
            await database.SaveChangesAsync().ConfigureAwait(false);
            var reorderedReplay = await application.AppendMessagesAsync(request with
            {
                Messages = [items[1], items[0] with { Flags = [], InternalDate = DateTime.UtcNow }],
            }).ConfigureAwait(false);
            Assert.AreEqual(ImapAppendDisposition.Appended, reorderedReplay.Disposition);
            CollectionAssert.AreEqual(ExpectedVector2, reorderedReplay.Uids);
            Assert.AreEqual(2, objects.ObjectCount);
            await Assert.ThrowsAsync<InvalidOperationException>(() => application.AppendMessagesAsync(request with
            {
                Messages = [items[0], items[1] with { MessageId = Guid.CreateVersion7() }],
            })).ConfigureAwait(false);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                application.AppendMessagesAsync(request with
                {
                    Messages = [items[0] with { RawMessage = raw2 }, items[1]],
                })).ConfigureAwait(false);
            var stored = await database.Emails.OrderBy(email => email.Uid).ToListAsync().ConfigureAwait(false);
            Assert.HasCount(2, stored);
            Assert.IsNull(stored[0].RawMessage);
            Assert.IsNull(stored[1].RawMessage);
            Assert.IsNotNull(stored[0].RawMessageObjectName);
            Assert.IsNotNull(stored[1].RawMessageObjectName);
            Assert.IsTrue(stored[0].IsRead);
            Assert.IsTrue(stored[1].IsFlagged);
            CollectionAssert.AreEqual(ExpectedVector3, stored[0].Keywords);
            Assert.AreEqual(items[0].InternalDate, stored[0].ReceivedAt);
            Assert.AreEqual(stored[0].ThreadObjectId, stored[1].ThreadObjectId, StringComparer.Ordinal);
            Assert.AreEqual("First", stored[0].Subject, StringComparer.Ordinal);
            Assert.AreEqual("Second", stored[1].Subject, StringComparer.Ordinal);
            var effects = new LargeObjectTransactionEffects(
                objects, NullLogger<LargeObjectTransactionEffects>.Instance);
            var content = new MailboxMessageContentService(objects, effects);
            CollectionAssert.AreEqual(raw1, await content.ReadAsync(stored[0], CancellationToken.None).ConfigureAwait(false));
            CollectionAssert.AreEqual(raw2, await content.ReadAsync(stored[1], CancellationToken.None).ConfigureAwait(false));
            Assert.AreEqual(9, await database.Folders
                .Where(folder => folder.Id == folderId)
                .Select(folder => folder.NextUid)
                .SingleAsync().ConfigureAwait(false));
            Assert.AreEqual(6L, await database.Folders
                .Where(folder => folder.Id == folderId)
                .Select(folder => folder.HighestModSeq)
                .SingleAsync().ConfigureAwait(false));
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
    private static readonly int[] ExpectedVector1 = new[] { 7, 8 };
    private static readonly int[] ExpectedVector2 = new[] { 8, 7 };
    private static readonly string[] ExpectedVector3 = new[] { "$Label1" };
}
