using Microsoft.EntityFrameworkCore;
using mk8.email.Application.Services;
using mk8.email.Configuration;
using mk8.email.Contracts.Enums;
using mk8.email.Infrastructure.Data;
using mk8.email.Infrastructure.Models;

namespace mk8.email.Messaging.Tests;

[TestClass]
[TestCategory("PostgreSQL")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1812", Justification = "MSTest DiscoverInternals instantiates this test class by reflection; focused discovery is verified by executed test counts.")]
internal sealed class GlobalSingletonPostgresTests
{
    [TestMethod]
    [Timeout(30_000)]
    public async Task DuplicateGlobalRowsAreRejectedInsteadOfSelected()
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
        var database = new EmailDbContext(options);
        await using var databaseLifetime = database.ConfigureAwait(false);
        await database.Database.EnsureCreatedAsync().ConfigureAwait(false);

        var companies = new CompanyService(database);
        var config = await companies.GetGlobalConfigAsync().ConfigureAwait(false);
        var limits = await companies.GetGlobalLimitsAsync().ConfigureAwait(false);
        var adminId = Guid.CreateVersion7();

        await (database.GlobalConfig.AddAsync(new GlobalConfigDB { Id = Guid.CreateVersion7() })).ConfigureAwait(false);
        await (database.GlobalLimits.AddAsync(new GlobalLimitsDB { Id = Guid.CreateVersion7() })).ConfigureAwait(false);
        await (database.Users.AddAsync(new UserDB
        {
            Id = adminId,
            Username = "admin@example.test",
            PasswordHash = "unused",
            Role = nameof(UserRole.SuperAdmin),
        })).ConfigureAwait(false);
        await database.SaveChangesAsync().ConfigureAwait(false);
        database.ChangeTracker.Clear();

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(companies.GetGlobalConfigAsync).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(companies.GetGlobalLimitsAsync).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => companies.UpdateGlobalConfigAsync(adminId, config)).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => companies.UpdateGlobalLimitsAsync(adminId, limits)).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => new SeederService(database, new EnvironmentConfig()).SeedAsync()).ConfigureAwait(false);
    }
}
