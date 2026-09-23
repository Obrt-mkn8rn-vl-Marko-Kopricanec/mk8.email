using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using mk8.email.Application.Services;
using mk8.email.Contracts.Imap;
using mk8.email.Infrastructure.Data;
using mk8.email.Infrastructure.Models;

namespace mk8.email.Messaging.Tests;

[TestClass]
[TestCategory("PostgreSQL")]
public sealed class ImapAppendPreflightPostgresTests
{
    [TestMethod]
    [Timeout(20_000)]
    public async Task PreflightChecksMailboxOwnershipAndCumulativeQuota()
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
        await using (var database = new EmailDbContext(options))
        {
            await database.Database.EnsureCreatedAsync();
            await new MailRuntimeSchemaService(database).EnsureAsync();
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
            database.Users.Add(new UserDB
            {
                Id = otherId,
                Username = "other@example.test",
                PasswordHash = "unused",
                Role = "User",
                IsActive = true,
                Company = company,
            });
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
            database.Emails.Add(new EmailDB
            {
                Id = Guid.CreateVersion7(),
                Folder = folder,
                Uid = 1,
                SizeBytes = 80,
                Sender = "sender@example.test",
                Recipient = "owner@example.test",
                Subject = "Existing",
            });
            await database.SaveChangesAsync();
        }

        await using (var database = new EmailDbContext(options))
        {
            var objects = new InMemoryLargeObjectStore();
            var effects = new LargeObjectTransactionEffects(
                objects, NullLogger<LargeObjectTransactionEffects>.Instance);
            var application = new ImapApplicationService(
                null!, null!, database,
                new MailboxMessageContentService(objects, effects),
                effects, NullLogger<ImapApplicationService>.Instance);
            Assert.AreEqual(ImapAppendPreflightDisposition.MailboxNotFound,
                (await application.CheckAppendCapacityAsync(
                    new ImapAppendPreflightRequest(otherId, "INBOX", 1))).Disposition);
            Assert.AreEqual(ImapAppendPreflightDisposition.MailboxNotFound,
                (await application.CheckAppendCapacityAsync(
                    new ImapAppendPreflightRequest(ownerId, "Missing", 1))).Disposition);
            Assert.AreEqual(ImapAppendPreflightDisposition.Ready,
                (await application.CheckAppendCapacityAsync(
                    new ImapAppendPreflightRequest(ownerId, "INBOX", 20))).Disposition);
            Assert.AreEqual(ImapAppendPreflightDisposition.OverQuota,
                (await application.CheckAppendCapacityAsync(
                    new ImapAppendPreflightRequest(ownerId, "INBOX", 21))).Disposition);
            await Assert.ThrowsAsync<ArgumentException>(() =>
                application.CheckAppendCapacityAsync(
                    new ImapAppendPreflightRequest(ownerId, "INBOX", -1)));

            var owner = await database.Users.SingleAsync(user => user.Id == ownerId);
            owner.QuotaBytes = 0;
            await database.SaveChangesAsync();
            Assert.AreEqual(ImapAppendPreflightDisposition.Ready,
                (await application.CheckAppendCapacityAsync(
                    new ImapAppendPreflightRequest(ownerId, "INBOX", 1000))).Disposition);
        }
    }
}
