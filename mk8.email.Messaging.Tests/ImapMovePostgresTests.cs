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
public sealed class ImapMovePostgresTests
{
    [TestMethod]
    [Timeout(20_000)]
    public async Task MoveIsOwnerScopedTransactionalAndPreservesAzureBlobReference()
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
        var blobMessageId = Guid.CreateVersion7();
        var rawMessage = Encoding.UTF8.GetBytes(
            "From: sender@example.test\r\nSubject: Blob message\r\n\r\nBody\r\n");
        var objects = new InMemoryLargeObjectStore();
        string? objectName = null;
        await using (var database = new EmailDbContext(options))
        {
            await database.Database.EnsureCreatedAsync();
            await new MailRuntimeSchemaService(database).EnsureAsync();
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
            database.Folders.Add(destination);
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
                    await content.SetAsync(message, rawMessage, CancellationToken.None);
                    await effects.CommitAsync(marker);
                    objectName = message.RawMessageObjectName;
                }
                database.Emails.Add(message);
            }
            await database.SaveChangesAsync();
        }
        Assert.AreEqual(1, objects.ObjectCount);
        await using (var database = new EmailDbContext(options))
        {
            var application = CreateApplication(database);
            var denied = await application.MoveMessagesAsync(new ImapMoveRequest(
                otherUserId, sourceId, "Archive", false,
                new ImapMessageSelection([new ImapMessageRange(1, null)], null)));
            Assert.AreEqual(ImapMoveDisposition.SourceNotFound, denied.Disposition);
            var missing = await application.MoveMessagesAsync(new ImapMoveRequest(
                userId, sourceId, "Missing", false,
                new ImapMessageSelection([new ImapMessageRange(1, null)], null)));
            Assert.AreEqual(ImapMoveDisposition.DestinationNotFound, missing.Disposition);
            var empty = await application.MoveMessagesAsync(new ImapMoveRequest(
                userId, sourceId, "Archive", true,
                new ImapMessageSelection([new ImapMessageRange(99, 99)], null)));
            Assert.AreEqual(ImapMoveDisposition.Moved, empty.Disposition);
            Assert.IsEmpty(empty.SourceUids);
            await Assert.ThrowsAsync<ArgumentException>(() => application.MoveMessagesAsync(
                new ImapMoveRequest(userId, sourceId, "Archive", false,
                    new ImapMessageSelection([new ImapMessageRange(0, 1)], null))));
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
                """);
        }

        await using (var database = new EmailDbContext(options))
        {
            var application = CreateApplication(database);
            await Assert.ThrowsAsync<DbUpdateException>(() => application.MoveMessagesAsync(
                new ImapMoveRequest(userId, sourceId, "Archive", false,
                    new ImapMessageSelection([new ImapMessageRange(2, 2)], null))));
        }
        await using (var database = new EmailDbContext(options))
        {
            Assert.AreEqual(sourceId, await database.Emails
                .Where(message => message.Id == blobMessageId)
                .Select(message => message.FolderId)
                .SingleAsync());
            Assert.AreEqual(0, await database.ExpungedUids.CountAsync());
            Assert.AreEqual(3L, await database.Folders
                .Where(folder => folder.Id == sourceId)
                .Select(folder => folder.HighestModSeq)
                .SingleAsync());
            Assert.AreEqual(10, await database.Folders
                .Where(folder => folder.Id == destinationId)
                .Select(folder => folder.NextUid)
                .SingleAsync());
            await database.Database.ExecuteSqlRawAsync(
                "DROP TRIGGER reject_imap_move ON emails; "
                + "DROP FUNCTION reject_imap_move();");
        }
        Assert.AreEqual(1, objects.ObjectCount);

        await using (var database = new EmailDbContext(options))
        {
            var application = CreateApplication(database);
            var moved = await application.MoveMessagesAsync(new ImapMoveRequest(
                userId, sourceId, "Archive", false,
                new ImapMessageSelection([new ImapMessageRange(2, 2)], null)));
            Assert.AreEqual(ImapMoveDisposition.Moved, moved.Disposition);
            Assert.AreEqual(23, moved.DestinationUidValidity);
            CollectionAssert.AreEqual(new[] { 2 }, moved.SourceUids);
            CollectionAssert.AreEqual(new[] { 10 }, moved.DestinationUids);
            CollectionAssert.AreEqual(new[] { 2 }, moved.ExpungeSequenceNumbers);
        }
        await using (var database = new EmailDbContext(options))
        {
            var movedMessage = await database.Emails.SingleAsync(message => message.Id == blobMessageId);
            Assert.AreEqual(destinationId, movedMessage.FolderId);
            Assert.AreEqual(10, movedMessage.Uid);
            Assert.AreEqual(6L, movedMessage.ModSeq);
            Assert.AreEqual(objectName, movedMessage.RawMessageObjectName);
            var effects = CreateEffects(objects);
            var content = new MailboxMessageContentService(objects, effects);
            CollectionAssert.AreEqual(rawMessage, await content.ReadAsync(
                movedMessage, CancellationToken.None));
            Assert.AreEqual(4L, await database.Folders
                .Where(folder => folder.Id == sourceId)
                .Select(folder => folder.HighestModSeq)
                .SingleAsync());
            Assert.AreEqual(11, await database.Folders
                .Where(folder => folder.Id == destinationId)
                .Select(folder => folder.NextUid)
                .SingleAsync());
        }

        await using (var database = new EmailDbContext(options))
        {
            var application = CreateApplication(database);
            var moved = await application.MoveMessagesAsync(new ImapMoveRequest(
                userId, sourceId, "Archive", true,
                new ImapMessageSelection(null, [1, 3])));
            CollectionAssert.AreEqual(new[] { 1, 3 }, moved.SourceUids);
            CollectionAssert.AreEqual(new[] { 11, 12 }, moved.DestinationUids);
            CollectionAssert.AreEqual(new[] { 1, 1 }, moved.ExpungeSequenceNumbers);
        }
        await using (var database = new EmailDbContext(options))
        {
            Assert.AreEqual(0, await database.Emails.CountAsync(
                message => message.FolderId == sourceId));
            CollectionAssert.AreEqual(new[] { 10, 11, 12 }, await database.Emails
                .Where(message => message.FolderId == destinationId)
                .OrderBy(message => message.Uid)
                .Select(message => message.Uid)
                .ToListAsync());
            CollectionAssert.AreEqual(new[] { 1, 2, 3 }, await database.ExpungedUids
                .Where(expunged => expunged.FolderId == sourceId)
                .OrderBy(expunged => expunged.Uid)
                .Select(expunged => expunged.Uid)
                .ToListAsync());
        }
        Assert.AreEqual(1, objects.ObjectCount);
        Assert.AreEqual(0, objects.DeleteCount);
    }

    private static ImapApplicationService CreateApplication(EmailDbContext database) => new(
        null!, null!, database, null!, null!, NullLogger<ImapApplicationService>.Instance);

    private static LargeObjectTransactionEffects CreateEffects(InMemoryLargeObjectStore objects) =>
        new(objects, NullLogger<LargeObjectTransactionEffects>.Instance);
}
