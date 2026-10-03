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
internal sealed class ImapMovePostgresTests
{
    [TestMethod]
    [Timeout(20_000)]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "MA0051", Justification = "The MoveIsOwnerScopedTransactionalAndPreservesAzureBlobReference scenario keeps one fixture's ordered setup, operation and invariant assertions together.")]
    public async Task MoveIsOwnerScopedTransactionalAndPreservesAzureBlobReference()
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
        var blobMessageId = Guid.CreateVersion7();
        var rawMessage = Encoding.UTF8.GetBytes(
            "From: sender@example.test\r\nSubject: Blob message\r\n\r\nBody\r\n");
        var objects = new InMemoryLargeObjectStore();
        string? objectName = null;
        {
            var database = new EmailDbContext(options);
            await using var databaseLifetime = database.ConfigureAwait(false);
            await database.Database.EnsureCreatedAsync().ConfigureAwait(false);
            await new MailRuntimeSchemaService(database).EnsureAsync().ConfigureAwait(false);
            var company = new CompanyDB
            {
                Id = Guid.CreateVersion7(),
                Name = "IMAP MOVE test",
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
            var source = new FolderDB
            {
                Id = sourceId,
                Name = "Inbox",
                HighestModSeq = 3,
                NextUid = 4,
                Inbox = inbox,
            };
            var destination = new FolderDB
            {
                Id = destinationId,
                Name = "Archive",
                UidValidity = 23,
                HighestModSeq = 5,
                NextUid = 10,
                Inbox = inbox,
            };
            await (database.Folders.AddAsync(destination)).ConfigureAwait(false);
            for (var uid = 1; uid <= 3; uid++)
            {
                var message = new EmailDB
                {
                    Id = uid == 2 ? blobMessageId : Guid.CreateVersion7(),
                    Folder = source,
                    Uid = uid,
                    ModSeq = uid,
                    Sender = "sender@example.test",
                    Recipient = "owner@example.test",
                    Subject = $"Message {uid}",
                    Body = string.Empty,
                };
                if (uid == 2)
                {
                    var effects = CreateEffects(objects);
                    var content = new MailboxMessageContentService(objects, effects);
                    var marker = effects.Mark();
                    await content.SetAsync(message, rawMessage, CancellationToken.None).ConfigureAwait(false);
                    await effects.CommitAsync(marker).ConfigureAwait(false);
                    objectName = message.RawMessageObjectName;
                }
                await (database.Emails.AddAsync(message)).ConfigureAwait(false);
            }
            await database.SaveChangesAsync().ConfigureAwait(false);
        }
        Assert.AreEqual(1, objects.ObjectCount);
        {
            var database = new EmailDbContext(options);
            await using var databaseLifetime = database.ConfigureAwait(false);
            var application = CreateApplication(database);
            var denied = await application.MoveMessagesAsync(new ImapMoveRequest(
                otherUserId, sourceId, "Archive", false,
                new ImapMessageSelection([new ImapMessageRange(1, null)], null))).ConfigureAwait(false);
            Assert.AreEqual(ImapMoveDisposition.SourceNotFound, denied.Disposition);
            var missing = await application.MoveMessagesAsync(new ImapMoveRequest(
                userId, sourceId, "Missing", false,
                new ImapMessageSelection([new ImapMessageRange(1, null)], null))).ConfigureAwait(false);
            Assert.AreEqual(ImapMoveDisposition.DestinationNotFound, missing.Disposition);
            var empty = await application.MoveMessagesAsync(new ImapMoveRequest(
                userId, sourceId, "Archive", true,
                new ImapMessageSelection([new ImapMessageRange(99, 99)], null))).ConfigureAwait(false);
            Assert.AreEqual(ImapMoveDisposition.Moved, empty.Disposition);
            Assert.AreEqual(23, empty.DestinationUidValidity);
            Assert.IsEmpty(empty.SourceUids);
            Assert.IsEmpty(empty.DestinationUids);
            Assert.IsEmpty(empty.ExpungeSequenceNumbers);
            await Assert.ThrowsAsync<ArgumentException>(() => application.MoveMessagesAsync(
                new ImapMoveRequest(userId, sourceId, "Archive", false,
                    new ImapMessageSelection([new ImapMessageRange(0, 1)], null)))).ConfigureAwait(false);
            await database.Database.ExecuteSqlRawAsync(
                """
                CREATE FUNCTION reject_imap_move() RETURNS trigger AS $$
                BEGIN
                    RAISE EXCEPTION 'test rollback of IMAP MOVE';
                END;
                $$ LANGUAGE plpgsql;
                CREATE TRIGGER reject_imap_move
                BEFORE UPDATE ON emails
                FOR EACH ROW EXECUTE FUNCTION reject_imap_move();
                """).ConfigureAwait(false);
        }

        {
            var database = new EmailDbContext(options);
            await using var databaseLifetime = database.ConfigureAwait(false);
            var application = CreateApplication(database);
            await Assert.ThrowsAsync<DbUpdateException>(() => application.MoveMessagesAsync(
                new ImapMoveRequest(userId, sourceId, "Archive", false,
                    new ImapMessageSelection([new ImapMessageRange(2, 2)], null)))).ConfigureAwait(false);
        }
        {
            var database = new EmailDbContext(options);
            await using var databaseLifetime = database.ConfigureAwait(false);
            Assert.AreEqual(sourceId, await database.Emails
                .Where(message => message.Id == blobMessageId)
                .Select(message => message.FolderId)
                .SingleAsync().ConfigureAwait(false));
            Assert.AreEqual(0, await database.ExpungedUids.CountAsync().ConfigureAwait(false));
            Assert.AreEqual(3L, await database.Folders
                .Where(folder => folder.Id == sourceId)
                .Select(folder => folder.HighestModSeq)
                .SingleAsync().ConfigureAwait(false));
            Assert.AreEqual(10, await database.Folders
                .Where(folder => folder.Id == destinationId)
                .Select(folder => folder.NextUid)
                .SingleAsync().ConfigureAwait(false));
            await database.Database.ExecuteSqlRawAsync(
                "DROP TRIGGER reject_imap_move ON emails; "
                + "DROP FUNCTION reject_imap_move();").ConfigureAwait(false);
        }
        Assert.AreEqual(1, objects.ObjectCount);

        {
            var database = new EmailDbContext(options);
            await using var databaseLifetime = database.ConfigureAwait(false);
            var application = CreateApplication(database);
            var moved = await application.MoveMessagesAsync(new ImapMoveRequest(
                userId, sourceId, "Archive", false,
                new ImapMessageSelection([new ImapMessageRange(2, 2)], null))).ConfigureAwait(false);
            Assert.AreEqual(ImapMoveDisposition.Moved, moved.Disposition);
            Assert.AreEqual(23, moved.DestinationUidValidity);
            CollectionAssert.AreEqual(ExpectedVector1, moved.SourceUids);
            CollectionAssert.AreEqual(ExpectedVector2, moved.DestinationUids);
            CollectionAssert.AreEqual(ExpectedVector1, moved.ExpungeSequenceNumbers);
        }
        {
            var database = new EmailDbContext(options);
            await using var databaseLifetime = database.ConfigureAwait(false);
            var movedMessage = await database.Emails.SingleAsync(message => message.Id == blobMessageId).ConfigureAwait(false);
            Assert.AreEqual(destinationId, movedMessage.FolderId);
            Assert.AreEqual(10, movedMessage.Uid);
            Assert.AreEqual(6L, movedMessage.ModSeq);
            Assert.AreEqual(objectName, movedMessage.RawMessageObjectName, StringComparer.Ordinal);
            var effects = CreateEffects(objects);
            var content = new MailboxMessageContentService(objects, effects);
            CollectionAssert.AreEqual(rawMessage, await content.ReadAsync(
                movedMessage, CancellationToken.None).ConfigureAwait(false));
            Assert.AreEqual(4L, await database.Folders
                .Where(folder => folder.Id == sourceId)
                .Select(folder => folder.HighestModSeq)
                .SingleAsync().ConfigureAwait(false));
            Assert.AreEqual(11, await database.Folders
                .Where(folder => folder.Id == destinationId)
                .Select(folder => folder.NextUid)
                .SingleAsync().ConfigureAwait(false));
        }

        {
            var database = new EmailDbContext(options);
            await using var databaseLifetime = database.ConfigureAwait(false);
            var application = CreateApplication(database);
            var moved = await application.MoveMessagesAsync(new ImapMoveRequest(
                userId, sourceId, "Archive", true,
                new ImapMessageSelection(null, [1, 3]))).ConfigureAwait(false);
            CollectionAssert.AreEqual(ExpectedVector3, moved.SourceUids);
            CollectionAssert.AreEqual(ExpectedVector4, moved.DestinationUids);
            CollectionAssert.AreEqual(ExpectedVector5, moved.ExpungeSequenceNumbers);
        }
        {
            var database = new EmailDbContext(options);
            await using var databaseLifetime = database.ConfigureAwait(false);
            Assert.AreEqual(0, await database.Emails.CountAsync(
                message => message.FolderId == sourceId).ConfigureAwait(false));
            CollectionAssert.AreEqual(ExpectedVector6, await database.Emails
                .Where(message => message.FolderId == destinationId)
                .OrderBy(message => message.Uid)
                .Select(message => message.Uid)
                .ToListAsync().ConfigureAwait(false));
            CollectionAssert.AreEqual(ExpectedVector7, await database.ExpungedUids
                .Where(expunged => expunged.FolderId == sourceId)
                .OrderBy(expunged => expunged.Uid)
                .Select(expunged => expunged.Uid)
                .ToListAsync().ConfigureAwait(false));
        }
        Assert.AreEqual(1, objects.ObjectCount);
        Assert.AreEqual(0, objects.DeleteCount);
    }

    private static ImapApplicationService CreateApplication(EmailDbContext database) => new(
        null!, null!, database, null!, null!, NullLogger<ImapApplicationService>.Instance);

    private static LargeObjectTransactionEffects CreateEffects(InMemoryLargeObjectStore objects) =>
        new(objects, NullLogger<LargeObjectTransactionEffects>.Instance);
    private static readonly int[] ExpectedVector1 = new[] { 2 };
    private static readonly int[] ExpectedVector2 = new[] { 10 };
    private static readonly int[] ExpectedVector3 = new[] { 1, 3 };
    private static readonly int[] ExpectedVector4 = new[] { 11, 12 };
    private static readonly int[] ExpectedVector5 = new[] { 1, 1 };
    private static readonly int[] ExpectedVector6 = new[] { 10, 11, 12 };
    private static readonly int[] ExpectedVector7 = new[] { 1, 2, 3 };
}
