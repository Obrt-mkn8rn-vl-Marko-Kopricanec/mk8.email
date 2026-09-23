using Microsoft.EntityFrameworkCore;
using mk8.email.Application.Services;
using mk8.email.Contracts.Imap;
using mk8.email.Infrastructure.Data;
using mk8.email.Infrastructure.Models;

namespace mk8.email.Messaging.Tests;

[TestClass]
[TestCategory("PostgreSQL")]
public sealed class ImapMailboxRenamePostgresTests
{
    [TestMethod]
    [Timeout(20_000)]
    public async Task RenameUpdatesTheOwnedSubtreeAtomicallyAndPreservesMailboxIds()
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
        var rootId = Guid.CreateVersion7();
        var childId = Guid.CreateVersion7();
        var rootMailboxId = Guid.CreateVersion7().ToString("N");
        var childMailboxId = Guid.CreateVersion7().ToString("N");
        await using (var database = new EmailDbContext(options))
        {
            await database.Database.EnsureCreatedAsync();
            var company = new CompanyDB
            {
                Id = Guid.CreateVersion7(),
                Name = "IMAP rename test",
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
            database.Folders.AddRange(
                new FolderDB
                {
                    Id = rootId,
                    Name = "Projects",
                    MailboxId = rootMailboxId,
                    Inbox = inbox,
                },
                new FolderDB
                {
                    Id = childId,
                    Name = "Projects/2026",
                    MailboxId = childMailboxId,
                    Inbox = inbox,
                },
                new FolderDB { Id = Guid.CreateVersion7(), Name = "Archive", Inbox = inbox });
            await database.SaveChangesAsync();
        }

        await using (var database = new EmailDbContext(options))
        {
            var application = new ImapApplicationService(
                null!, null!, database, null!, null!, null!);
            var collision = await application.RenameMailboxAsync(
                new ImapMailboxRenameRequest(userId, "Projects", "Archive"));
            Assert.AreEqual(ImapMailboxRenameDisposition.AlreadyExists, collision.Disposition);
            var renamed = await application.RenameMailboxAsync(
                new ImapMailboxRenameRequest(userId, "Projects", "Work"));
            Assert.AreEqual(ImapMailboxRenameDisposition.Renamed, renamed.Disposition);
        }

        await using (var database = new EmailDbContext(options))
        {
            var root = await database.Folders.AsNoTracking().SingleAsync(folder => folder.Id == rootId);
            var child = await database.Folders.AsNoTracking().SingleAsync(folder => folder.Id == childId);
            Assert.AreEqual("Work", root.Name);
            Assert.AreEqual("Work/2026", child.Name);
            Assert.AreEqual(rootMailboxId, root.MailboxId);
            Assert.AreEqual(childMailboxId, child.MailboxId);
            Assert.AreEqual(0, await database.Folders.CountAsync(
                folder => folder.Name.StartsWith("Projects")));
            Assert.AreEqual(1, await database.Folders.CountAsync(
                folder => folder.Name == "Archive"));
        }
    }
}
