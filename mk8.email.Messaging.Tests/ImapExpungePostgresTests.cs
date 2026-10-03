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
internal sealed class ImapExpungePostgresTests
{
    [TestMethod]
    [Timeout(20_000)]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "MA0051", Justification = "The ExpungeIsOwnerScopedAndDeletesBlobsOnlyAfterCommit scenario keeps one fixture's ordered setup, operation and invariant assertions together.")]
    public async Task ExpungeIsOwnerScopedAndDeletesBlobsOnlyAfterCommit()
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
        var objects = new InMemoryLargeObjectStore();
        {
            var database = new EmailDbContext(options);
            await using var databaseLifetime = database.ConfigureAwait(false);
            await database.Database.EnsureCreatedAsync().ConfigureAwait(false);
            await new MailRuntimeSchemaService(database).EnsureAsync().ConfigureAwait(false);
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
                    CancellationToken.None).ConfigureAwait(false);
                await (database.Emails.AddAsync(message)).ConfigureAwait(false);
            }
            await database.SaveChangesAsync().ConfigureAwait(false);
            await effects.CommitAsync(marker).ConfigureAwait(false);
            Assert.AreEqual(4, objects.ObjectCount);
        }

        {
            var database = new EmailDbContext(options);
            await using var databaseLifetime = database.ConfigureAwait(false);
            var effects = CreateEffects(objects);
            var application = CreateApplication(database, objects, effects);
            var denied = await application.ExpungeDeletedAsync(
                new ImapExpungeRequest(otherUserId, folderId)).ConfigureAwait(false);
            Assert.IsFalse(denied.FolderFound);
            Assert.IsEmpty(denied.Messages);
            Assert.AreEqual(4, objects.ObjectCount);
            await Assert.ThrowsAsync<ArgumentException>(() => application.ExpungeDeletedAsync(
                new ImapExpungeRequest(Guid.Empty, folderId))).ConfigureAwait(false);
            await Assert.ThrowsAsync<ArgumentException>(() => application.ExpungeDeletedAsync(
                new ImapExpungeRequest(userId, folderId, new ImapUidSelection([], null)))).ConfigureAwait(false);
            await Assert.ThrowsAsync<ArgumentException>(() => application.ExpungeDeletedAsync(
                new ImapExpungeRequest(userId, folderId,
                    new ImapUidSelection([new ImapUidRange(0, 1)], null)))).ConfigureAwait(false);
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => application.ExpungeDeletedAsync(
                new ImapExpungeRequest(userId, folderId, new ImapUidSelection([], [])))).ConfigureAwait(false);
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => application.ExpungeDeletedAsync(
                new ImapExpungeRequest(userId, folderId, new ImapUidSelection(null, [0])))).ConfigureAwait(false);
            var empty = await application.ExpungeDeletedAsync(
                new ImapExpungeRequest(userId, folderId, new ImapUidSelection(null, []))).ConfigureAwait(false);
            Assert.IsTrue(empty.FolderFound);
            Assert.IsEmpty(empty.Messages);
            Assert.AreEqual(4, await database.Emails.CountAsync().ConfigureAwait(false));
            Assert.AreEqual(0, await database.ExpungedUids.CountAsync().ConfigureAwait(false));
            Assert.AreEqual(4L, await database.Folders.AsNoTracking()
                .Where(folder => folder.Id == folderId).Select(folder => folder.HighestModSeq).SingleAsync().ConfigureAwait(false));
            Assert.AreEqual(4, objects.ObjectCount);
            Assert.AreEqual(0, objects.DeleteCount);
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
                """).ConfigureAwait(false);
        }

        {
            var database = new EmailDbContext(options);
            await using var databaseLifetime = database.ConfigureAwait(false);
            var effects = CreateEffects(objects);
            var application = CreateApplication(database, objects, effects);
            await Assert.ThrowsAsync<DbUpdateException>(() => application.ExpungeDeletedAsync(
                new ImapExpungeRequest(userId, folderId))).ConfigureAwait(false);
        }
        Assert.AreEqual(4, objects.ObjectCount);
        Assert.AreEqual(0, objects.DeleteCount);
        {
            var database = new EmailDbContext(options);
            await using var databaseLifetime = database.ConfigureAwait(false);
            Assert.AreEqual(4, await database.Emails.CountAsync().ConfigureAwait(false));
            Assert.AreEqual(0, await database.ExpungedUids.CountAsync().ConfigureAwait(false));
            Assert.AreEqual(4L, await database.Folders
                .Where(folder => folder.Id == folderId)
                .Select(folder => folder.HighestModSeq)
                .SingleAsync().ConfigureAwait(false));
            await database.Database.ExecuteSqlRawAsync(
                "DROP TRIGGER reject_imap_expunge ON emails; "
                + "DROP FUNCTION reject_imap_expunge();").ConfigureAwait(false);
        }

        {
            var database = new EmailDbContext(options);
            await using var databaseLifetime = database.ConfigureAwait(false);
            var effects = CreateEffects(objects);
            var application = CreateApplication(database, objects, effects);
            var highest = await application.ExpungeDeletedAsync(
                new ImapExpungeRequest(userId, folderId,
                    new ImapUidSelection(
                        [new ImapUidRange(null, null), new ImapUidRange(4, 4), new ImapUidRange(100, 200)], null))).ConfigureAwait(false);
            CollectionAssert.AreEqual(
                new[] { new ImapExpungedMessage(4, 4) }, highest.Messages);
            var saved = await application.ExpungeDeletedAsync(
                new ImapExpungeRequest(userId, folderId,
                    new ImapUidSelection(null, [3, 3, 999]))).ConfigureAwait(false);
            CollectionAssert.AreEqual(
                new[] { new ImapExpungedMessage(3, 3) }, saved.Messages);
            var remaining = await application.ExpungeDeletedAsync(
                new ImapExpungeRequest(userId, folderId)).ConfigureAwait(false);
            Assert.IsTrue(remaining.FolderFound);
            CollectionAssert.AreEqual(
                new[] { new ImapExpungedMessage(2, 2) }, remaining.Messages);
            var repeat = await application.ExpungeDeletedAsync(
                new ImapExpungeRequest(userId, folderId)).ConfigureAwait(false);
            Assert.IsTrue(repeat.FolderFound);
            Assert.IsEmpty(repeat.Messages);
        }
        Assert.AreEqual(1, objects.ObjectCount);
        Assert.AreEqual(3, objects.DeleteCount);
        {
            var database = new EmailDbContext(options);
            await using var databaseLifetime = database.ConfigureAwait(false);
            CollectionAssert.AreEqual(ExpectedVector1, await database.Emails
                .OrderBy(email => email.Uid)
                .Select(email => email.Uid)
                .ToListAsync().ConfigureAwait(false));
            CollectionAssert.AreEqual(ExpectedVector2, await database.ExpungedUids
                .OrderBy(expunged => expunged.Uid)
                .Select(expunged => expunged.Uid)
                .ToListAsync().ConfigureAwait(false));
            CollectionAssert.AreEqual(new long[] { 7, 6, 5 }, await database.ExpungedUids
                .OrderBy(expunged => expunged.Uid)
                .Select(expunged => expunged.ModSeq)
                .ToListAsync().ConfigureAwait(false));
            Assert.AreEqual(7L, await database.Folders
                .Where(folder => folder.Id == folderId)
                .Select(folder => folder.HighestModSeq)
                .SingleAsync().ConfigureAwait(false));
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
    private static readonly int[] ExpectedVector1 = new[] { 1 };
    private static readonly int[] ExpectedVector2 = new[] { 2, 3, 4 };
}
