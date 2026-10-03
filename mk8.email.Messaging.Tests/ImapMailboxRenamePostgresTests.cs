using Microsoft.EntityFrameworkCore;
using mk8.email.Application.Services;
using mk8.email.Contracts.Imap;
using mk8.email.Infrastructure.Data;
using mk8.email.Infrastructure.Models;

namespace mk8.email.Messaging.Tests;

[TestClass]
[TestCategory("PostgreSQL")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1812", Justification = "MSTest DiscoverInternals instantiates this test class by reflection; focused discovery is verified by executed test counts.")]
internal sealed class ImapMailboxRenamePostgresTests
{
    [TestMethod]
    [Timeout(20_000)]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "MA0051", Justification = "The RenameUpdatesTheOwnedSubtreeAtomicallyAndPreservesMailboxIds scenario keeps one fixture's ordered setup, operation and invariant assertions together.")]
    public async Task RenameUpdatesTheOwnedSubtreeAtomicallyAndPreservesMailboxIds()
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
        var rootId = Guid.CreateVersion7();
        var childId = Guid.CreateVersion7();
        var rootMailboxId = Guid.CreateVersion7().ToString("N");
        var childMailboxId = Guid.CreateVersion7().ToString("N");
        {
            var database = new EmailDbContext(options);
            await using var databaseLifetime = database.ConfigureAwait(false);
            await database.Database.EnsureCreatedAsync().ConfigureAwait(false);
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
            await (database.Folders.AddRangeAsync(
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
                new FolderDB { Id = Guid.CreateVersion7(), Name = "Archive", Inbox = inbox })).ConfigureAwait(false);
            await database.SaveChangesAsync().ConfigureAwait(false);
        }

        {
            var database = new EmailDbContext(options);
            await using var databaseLifetime = database.ConfigureAwait(false);
            var application = new ImapApplicationService(
                null!, null!, database, null!, null!, null!);
            var collision = await application.RenameMailboxAsync(
                new ImapMailboxRenameRequest(userId, "Projects", "Archive")).ConfigureAwait(false);
            Assert.AreEqual(ImapMailboxRenameDisposition.AlreadyExists, collision.Disposition);
            var unchanged = await application.RenameMailboxAsync(
                new ImapMailboxRenameRequest(userId, "Projects", "Projects")).ConfigureAwait(false);
            Assert.AreEqual(ImapMailboxRenameDisposition.Renamed, unchanged.Disposition);
            var renamed = await application.RenameMailboxAsync(
                new ImapMailboxRenameRequest(userId, "Projects", "Work")).ConfigureAwait(false);
            Assert.AreEqual(ImapMailboxRenameDisposition.Renamed, renamed.Disposition);
        }

        {
            var database = new EmailDbContext(options);
            await using var databaseLifetime = database.ConfigureAwait(false);
            var root = await database.Folders.AsNoTracking().SingleAsync(folder => folder.Id == rootId).ConfigureAwait(false);
            var child = await database.Folders.AsNoTracking().SingleAsync(folder => folder.Id == childId).ConfigureAwait(false);
            Assert.AreEqual("Work", root.Name, StringComparer.Ordinal);
            Assert.AreEqual("Work/2026", child.Name, StringComparer.Ordinal);
            Assert.AreEqual(rootMailboxId, root.MailboxId, StringComparer.Ordinal);
            Assert.AreEqual(childMailboxId, child.MailboxId, StringComparer.Ordinal);
            Assert.AreEqual(0, await database.Folders.CountAsync(
                folder => folder.Name.StartsWith("Projects")).ConfigureAwait(false));
            Assert.AreEqual(1, await database.Folders.CountAsync(
                folder => folder.Name == "Archive").ConfigureAwait(false));
        }
    }
}
