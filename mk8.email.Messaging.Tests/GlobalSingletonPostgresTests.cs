using Microsoft.EntityFrameworkCore;
using mk8.email.Application.Services;
using mk8.email.Configuration;
using mk8.email.Contracts.Enums;
using mk8.email.Infrastructure.Data;
using mk8.email.Infrastructure.Models;

namespace mk8.email.Messaging.Tests;

[TestClass]
[TestCategory("PostgreSQL")]
public sealed class GlobalSingletonPostgresTests
{
    [TestMethod]
    [Timeout(30_000)]
    public async Task DuplicateGlobalRowsAreRejectedInsteadOfSelected()
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
        await using var database = new EmailDbContext(options);
        await database.Database.EnsureCreatedAsync();

        var companies = new CompanyService(database);
        var config = await companies.GetGlobalConfigAsync();
        var limits = await companies.GetGlobalLimitsAsync();
        var adminId = Guid.CreateVersion7();

        database.GlobalConfig.Add(new GlobalConfigDB { Id = Guid.CreateVersion7() });
        database.GlobalLimits.Add(new GlobalLimitsDB { Id = Guid.CreateVersion7() });
        database.Users.Add(new UserDB
        {
            Id = adminId,
            Username = "admin@example.test",
            PasswordHash = "unused",
            Role = nameof(UserRole.SuperAdmin),
        });
        await database.SaveChangesAsync();
        database.ChangeTracker.Clear();

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(companies.GetGlobalConfigAsync);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(companies.GetGlobalLimitsAsync);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => companies.UpdateGlobalConfigAsync(adminId, config));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => companies.UpdateGlobalLimitsAsync(adminId, limits));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => new SeederService(database, new EnvironmentConfig()).SeedAsync());
    }
}
