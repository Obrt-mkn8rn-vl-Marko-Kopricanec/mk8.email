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
internal sealed class ImapAppendPreflightPostgresTests
{
    [TestMethod]
    [Timeout(20_000)]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "MA0051", Justification = "The PreflightChecksMailboxOwnershipAndCumulativeQuota scenario keeps one fixture's ordered setup, operation and invariant assertions together.")]
    public async Task PreflightChecksMailboxOwnershipAndCumulativeQuota()
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
        {
            var database = new EmailDbContext(options);
            await using var databaseLifetime = database.ConfigureAwait(false);
            await database.Database.EnsureCreatedAsync().ConfigureAwait(false);
            await new MailRuntimeSchemaService(database).EnsureAsync().ConfigureAwait(false);
            var company = new CompanyDB
            {
                Id = Guid.CreateVersion7(),
                Name = "IMAP APPEND preflight test",
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
                QuotaBytes = 100,
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
            var folder = new FolderDB
            {
                Id = Guid.CreateVersion7(),
                Name = "Inbox",
                Inbox = new InboxDB
                {
                    Id = Guid.CreateVersion7(),
                    Name = "owner",
                    Address = address,
                    Owner = owner,
                },
            };
            await (database.Emails.AddAsync(new EmailDB
            {
                Id = Guid.CreateVersion7(),
                Folder = folder,
                Uid = 1,
                SizeBytes = 80,
                Sender = "sender@example.test",
                Recipient = "owner@example.test",
                Subject = "Existing",
            })).ConfigureAwait(false);
            await database.SaveChangesAsync().ConfigureAwait(false);
        }

        {
            var database = new EmailDbContext(options);
            await using var databaseLifetime = database.ConfigureAwait(false);
            var objects = new InMemoryLargeObjectStore();
            var effects = new LargeObjectTransactionEffects(
                objects, NullLogger<LargeObjectTransactionEffects>.Instance);
            var application = new ImapApplicationService(
                null!, null!, database,
                new MailboxMessageContentService(objects, effects),
                effects, NullLogger<ImapApplicationService>.Instance);
            Assert.AreEqual(ImapAppendPreflightDisposition.MailboxNotFound,
                (await application.CheckAppendCapacityAsync(
                    new ImapAppendPreflightRequest(otherId, "INBOX", 1)).ConfigureAwait(false)).Disposition);
            Assert.AreEqual(ImapAppendPreflightDisposition.MailboxNotFound,
                (await application.CheckAppendCapacityAsync(
                    new ImapAppendPreflightRequest(ownerId, "Missing", 1)).ConfigureAwait(false)).Disposition);
            Assert.AreEqual(ImapAppendPreflightDisposition.Ready,
                (await application.CheckAppendCapacityAsync(
                    new ImapAppendPreflightRequest(ownerId, "INBOX", 20)).ConfigureAwait(false)).Disposition);
            Assert.AreEqual(ImapAppendPreflightDisposition.OverQuota,
                (await application.CheckAppendCapacityAsync(
                    new ImapAppendPreflightRequest(ownerId, "INBOX", 21)).ConfigureAwait(false)).Disposition);
            await Assert.ThrowsAsync<ArgumentException>(() =>
                application.CheckAppendCapacityAsync(
                    new ImapAppendPreflightRequest(ownerId, "INBOX", -1))).ConfigureAwait(false);

            var owner = await database.Users.SingleAsync(user => user.Id == ownerId).ConfigureAwait(false);
            owner.QuotaBytes = 0;
            await database.SaveChangesAsync().ConfigureAwait(false);
            Assert.AreEqual(ImapAppendPreflightDisposition.Ready,
                (await application.CheckAppendCapacityAsync(
                    new ImapAppendPreflightRequest(ownerId, "INBOX", 1000)).ConfigureAwait(false)).Disposition);
        }
    }
}
