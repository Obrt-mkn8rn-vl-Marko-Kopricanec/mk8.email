using Microsoft.EntityFrameworkCore;
using mk8.email.Application.Services;
using mk8.email.Contracts.Imap;
using mk8.email.Infrastructure.Data;
using mk8.email.Infrastructure.Models;

namespace mk8.email.Messaging.Tests;

[TestClass]
[TestCategory("PostgreSQL")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1812", Justification = "MSTest DiscoverInternals instantiates this test class by reflection; focused discovery is verified by executed test counts.")]
internal sealed class ImapMailboxCreatePostgresTests
{
    [TestMethod]
    [Timeout(20_000)]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "MA0051", Justification = "The ConcurrentWorkersCreateOneMailboxAndReportTheUniqueIndexConflict scenario keeps one fixture's ordered setup, operation and invariant assertions together.")]
    public async Task ConcurrentWorkersCreateOneMailboxAndReportTheUniqueIndexConflict()
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
        {
            var database = new EmailDbContext(options);
            await using var databaseLifetime = database.ConfigureAwait(false);
            await database.Database.EnsureCreatedAsync().ConfigureAwait(false);
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
            await (database.Inboxes.AddAsync(new InboxDB
            {
                Id = Guid.CreateVersion7(),
                Name = "owner",
                Address = address,
                Owner = user,
            })).ConfigureAwait(false);
            await database.SaveChangesAsync().ConfigureAwait(false);
        }

        var ready = 0;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<ImapMailboxCreateResult> CreateAsync()
        {
            var database = new EmailDbContext(options);
            await using var databaseLifetime = database.ConfigureAwait(false);
            var application = new ImapApplicationService(
                null!, null!, database, null!, null!, null!);
            if (Interlocked.Increment(ref ready) == 2)
                gate.SetResult();

            // This async test intentionally joins its pre-started background operation; no foreground synchronization context or JTF is involved.
#pragma warning disable VSTHRD003
            await gate.Task.ConfigureAwait(false);

#pragma warning restore VSTHRD003

            return await application.CreateMailboxAsync(
                new ImapMailboxCreateRequest(userId, "Projects")).ConfigureAwait(false);
        }

        var results = await Task.WhenAll(CreateAsync(), CreateAsync()).ConfigureAwait(false);
        Assert.AreEqual(1, results.Count(result =>
            result.Disposition == ImapMailboxCreateDisposition.Created));
        Assert.AreEqual(1, results.Count(result =>
            result.Disposition == ImapMailboxCreateDisposition.AlreadyExists));
        {
            var database = new EmailDbContext(options);
            await using var databaseLifetime = database.ConfigureAwait(false);
            Assert.AreEqual(1, await database.Folders.CountAsync(
                folder => folder.Name == "Projects").ConfigureAwait(false));
        }
    }
}
