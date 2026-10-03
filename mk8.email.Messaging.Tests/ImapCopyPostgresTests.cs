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
internal sealed class ImapCopyPostgresTests
{
    [TestMethod]
    [Timeout(20_000)]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "MA0051", Justification = "The CopyIsOwnerScopedQuotaCheckedAndCommitsNewBlobOnlyWithDatabase scenario keeps one fixture's ordered setup, operation and invariant assertions together.")]
    public async Task CopyIsOwnerScopedQuotaCheckedAndCommitsNewBlobOnlyWithDatabase()
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
        var sourceId = Guid.CreateVersion7();
        var destinationId = Guid.CreateVersion7();
        var sourceMessageId = Guid.CreateVersion7();
        var rawMessage = Encoding.UTF8.GetBytes(
            "From: sender@example.test\r\nSubject: Copy blob\r\n\r\nBody\r\n");
        var objects = new InMemoryLargeObjectStore();
        {
            var database = new EmailDbContext(options);
            await using var databaseLifetime = database.ConfigureAwait(false);
            await database.Database.EnsureCreatedAsync().ConfigureAwait(false);
            await new MailRuntimeSchemaService(database).EnsureAsync().ConfigureAwait(false);
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
            await (database.Users.AddAsync(new UserDB
            {
                Id = otherUserId,
                Username = "other@example.test",
                PasswordHash = "unused",
                Role = "User",
                IsActive = true,
                Company = company,
            })).ConfigureAwait(false);
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
            await (database.Folders.AddAsync(new FolderDB
            {
                Id = destinationId,
                Name = "Archive",
                UidValidity = 23,
                NextUid = 10,
                HighestModSeq = 5,
                Inbox = inbox,
            })).ConfigureAwait(false);
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
            await content.SetAsync(source, rawMessage, CancellationToken.None).ConfigureAwait(false);
            await (database.Emails.AddAsync(source)).ConfigureAwait(false);
            await database.SaveChangesAsync().ConfigureAwait(false);
            await effects.CommitAsync(marker).ConfigureAwait(false);
        }
        Assert.AreEqual(1, objects.ObjectCount);

        {
            var database = new EmailDbContext(options);
            await using var databaseLifetime = database.ConfigureAwait(false);
            var application = CreateApplication(database, objects);
            var selection = new ImapMessageSelection([new ImapMessageRange(1, null)], null);
            Assert.AreEqual(ImapCopyDisposition.SourceNotFound,
                (await application.CopyMessagesAsync(new ImapCopyRequest(
                    otherUserId, sourceId, "Archive", true, selection)).ConfigureAwait(false)).Disposition);
            Assert.AreEqual(ImapCopyDisposition.DestinationNotFound,
                (await application.CopyMessagesAsync(new ImapCopyRequest(
                    userId, sourceId, "Missing", true, selection)).ConfigureAwait(false)).Disposition);
            var empty = await application.CopyMessagesAsync(new ImapCopyRequest(
                userId, sourceId, "Archive", true,
                new ImapMessageSelection([new ImapMessageRange(99, 99)], null))).ConfigureAwait(false);
            Assert.AreEqual(ImapCopyDisposition.Copied, empty.Disposition);
            Assert.AreEqual(23, empty.DestinationUidValidity);
            Assert.IsEmpty(empty.SourceUids);
            Assert.IsEmpty(empty.DestinationUids);
            foreach (var savedUids in new[] { Array.Empty<int>(), new[] { 999 } })
            {
                var savedEmpty = await application.CopyMessagesAsync(new ImapCopyRequest(
                    userId, sourceId, "Archive", true, new ImapMessageSelection(null, savedUids.ToList()))).ConfigureAwait(false);
                Assert.AreEqual(ImapCopyDisposition.Copied, savedEmpty.Disposition);
                Assert.AreEqual(23, savedEmpty.DestinationUidValidity);
                Assert.IsEmpty(savedEmpty.SourceUids);
                Assert.IsEmpty(savedEmpty.DestinationUids);
            }
            Assert.AreEqual(1, objects.ObjectCount);
            Assert.AreEqual(1, await database.Emails.CountAsync().ConfigureAwait(false));
            var unchanged = await database.Folders.AsNoTracking().SingleAsync(folder => folder.Id == destinationId).ConfigureAwait(false);
            Assert.AreEqual(10, unchanged.NextUid);
            Assert.AreEqual(5L, unchanged.HighestModSeq);
            Assert.AreEqual(ImapCopyDisposition.OverQuota,
                (await application.CopyMessagesAsync(new ImapCopyRequest(
                    userId, sourceId, "Archive", true, selection)).ConfigureAwait(false)).Disposition);
            await Assert.ThrowsAsync<ArgumentException>(() => application.CopyMessagesAsync(
                new ImapCopyRequest(userId, sourceId, "Archive", true,
                    new ImapMessageSelection([], null)))).ConfigureAwait(false);
            var owner = await database.Users.SingleAsync(user => user.Id == userId).ConfigureAwait(false);
            owner.QuotaBytes = 0;
            await database.SaveChangesAsync().ConfigureAwait(false);
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
                """).ConfigureAwait(false);
        }

        {
            var database = new EmailDbContext(options);
            await using var databaseLifetime = database.ConfigureAwait(false);
            var application = CreateApplication(database, objects);
            await Assert.ThrowsAsync<DbUpdateException>(() => application.CopyMessagesAsync(
                new ImapCopyRequest(userId, sourceId, "Archive", true,
                    new ImapMessageSelection([new ImapMessageRange(null, null)], null)))).ConfigureAwait(false);
        }
        Assert.AreEqual(1, objects.ObjectCount);
        Assert.AreEqual(1, objects.DeleteCount);
        {
            var database = new EmailDbContext(options);
            await using var databaseLifetime = database.ConfigureAwait(false);
            Assert.AreEqual(1, await database.Emails.CountAsync().ConfigureAwait(false));
            Assert.AreEqual(10, await database.Folders
                .Where(folder => folder.Id == destinationId)
                .Select(folder => folder.NextUid)
                .SingleAsync().ConfigureAwait(false));
            await database.Database.ExecuteSqlRawAsync(
                "DROP TRIGGER reject_imap_copy ON emails; "
                + "DROP FUNCTION reject_imap_copy();").ConfigureAwait(false);
        }

        {
            var database = new EmailDbContext(options);
            await using var databaseLifetime = database.ConfigureAwait(false);
            var application = CreateApplication(database, objects);
            var copied = await application.CopyMessagesAsync(new ImapCopyRequest(
                userId, sourceId, "Archive", true,
                new ImapMessageSelection(null, [1, 1, 999]))).ConfigureAwait(false);
            Assert.AreEqual(ImapCopyDisposition.Copied, copied.Disposition);
            Assert.AreEqual(23, copied.DestinationUidValidity);
            CollectionAssert.AreEqual(ExpectedVector1, copied.SourceUids);
            CollectionAssert.AreEqual(ExpectedVector2, copied.DestinationUids);
        }
        Assert.AreEqual(2, objects.ObjectCount);
        {
            var database = new EmailDbContext(options);
            await using var databaseLifetime = database.ConfigureAwait(false);
            var source = await database.Emails.SingleAsync(message => message.Id == sourceMessageId).ConfigureAwait(false);
            var copy = await database.Emails.SingleAsync(message => message.FolderId == destinationId).ConfigureAwait(false);
            Assert.AreEqual(1, source.Uid);
            Assert.IsTrue(source.IsDeleted);
            Assert.AreEqual(10, copy.Uid);
            Assert.IsFalse(copy.IsDeleted);
            Assert.AreNotEqual(source.Id, copy.Id);
            Assert.AreNotEqual(source.RawMessageObjectName, copy.RawMessageObjectName, StringComparer.Ordinal);
            Assert.IsNotNull(copy.RawMessageObjectName);
            Assert.IsNull(copy.RawMessage);
            var effects = CreateEffects(objects);
            var content = new MailboxMessageContentService(objects, effects);
            CollectionAssert.AreEqual(rawMessage, await content.ReadAsync(
                source, CancellationToken.None).ConfigureAwait(false));
            CollectionAssert.AreEqual(rawMessage, await content.ReadAsync(
                copy, CancellationToken.None).ConfigureAwait(false));
            Assert.AreEqual(11, await database.Folders
                .Where(folder => folder.Id == destinationId)
                .Select(folder => folder.NextUid)
                .SingleAsync().ConfigureAwait(false));
            Assert.AreEqual(6L, await database.Folders
                .Where(folder => folder.Id == destinationId)
                .Select(folder => folder.HighestModSeq)
                .SingleAsync().ConfigureAwait(false));
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
    private static readonly int[] ExpectedVector1 = new[] { 1 };
    private static readonly int[] ExpectedVector2 = new[] { 10 };
}
