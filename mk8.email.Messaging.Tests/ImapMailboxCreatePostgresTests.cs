using Microsoft.EntityFrameworkCore;
using mk8.email.Application.Services;
using mk8.email.Contracts.Imap;
using mk8.email.Infrastructure.Data;
using mk8.email.Infrastructure.Models;

namespace mk8.email.Messaging.Tests;

[TestClass]
[TestCategory("PostgreSQL")]
public sealed class ImapMailboxCreatePostgresTests
{
    [TestMethod]
    [Timeout(20_000)]
    public async Task ConcurrentWorkersCreateOneMailboxAndReportTheUniqueIndexConflict()
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
        await using (var database = new EmailDbContext(options))
        {
            await database.Database.EnsureCreatedAsync();
            var company = new CompanyDB
            {
                Id = Guid.CreateVersion7(),
                Name = "IMAP create test",
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
            database.Inboxes.Add(new InboxDB
            {
                Id = Guid.CreateVersion7(),
                Name = "owner",
                Address = address,
                Owner = user,
            });
            await database.SaveChangesAsync();
        }

        var ready = 0;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<ImapMailboxCreateResult> CreateAsync()
        {
            await using var database = new EmailDbContext(options);
            var application = new ImapApplicationService(null!, null!, database);
            if (Interlocked.Increment(ref ready) == 2)
                gate.SetResult();
            await gate.Task;
            return await application.CreateMailboxAsync(
                new ImapMailboxCreateRequest(userId, "Projects"));
        }

        var results = await Task.WhenAll(CreateAsync(), CreateAsync());
        Assert.AreEqual(1, results.Count(result =>
            result.Disposition == ImapMailboxCreateDisposition.Created));
        Assert.AreEqual(1, results.Count(result =>
            result.Disposition == ImapMailboxCreateDisposition.AlreadyExists));
        await using (var database = new EmailDbContext(options))
        {
            Assert.AreEqual(1, await database.Folders.CountAsync(
                folder => folder.Name == "Projects"));
        }
    }
}
