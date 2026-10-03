using Microsoft.EntityFrameworkCore;
using mk8.email.Application.Services;
using mk8.email.Infrastructure.Data;
using mk8.email.Infrastructure.Models;

namespace mk8.email.Messaging.Tests;

[TestClass]
[TestCategory("PostgreSQL")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1812", Justification = "MSTest DiscoverInternals instantiates this test class by reflection; focused discovery is verified by executed test counts.")]
internal sealed class MailAdministrationPostgresTests
{
    [TestMethod]
    [Timeout(30_000)]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "MA0051", Justification = "The DeactivationRevokesOnlyMatchingDomainThenTheRemainingAccount scenario keeps one fixture's ordered setup, operation and invariant assertions together.")]
    public async Task DeactivationRevokesOnlyMatchingDomainThenTheRemainingAccount()
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
        var company = new CompanyDB
        {
            Id = Guid.CreateVersion7(),
            Name = "Domain revocation test",
            IsActive = true,
        };
        var affectedUsers = new[]
        {
            CreateUser("alpha@example.test", company),
            CreateUser("beta@example.test", company),
        };
        var retainedUser = CreateUser("gamma@example.net", company);
        var users = affectedUsers.Append(retainedUser).ToArray();
        var now = DateTime.UtcNow;

        var database = new EmailDbContext(options);
        await using var databaseLifetime = database.ConfigureAwait(false);
        await database.Database.EnsureCreatedAsync().ConfigureAwait(false);
        await (database.Companies.AddAsync(company)).ConfigureAwait(false);
        await (database.Addresses.AddRangeAsync(
            new AddressDB
            {
                Id = Guid.CreateVersion7(),
                Domain = "example.test",
                IsActive = true,
                Company = company,
            },
            new AddressDB
            {
                Id = Guid.CreateVersion7(),
                Domain = "example.net",
                IsActive = true,
                Company = company,
            })).ConfigureAwait(false);
        await (database.Users.AddRangeAsync(users)).ConfigureAwait(false);
        for (var index = 0; index < users.Length; index++)
        {
            var user = users[index];
            var grant = new OAuthGrantDB
            {
                Id = Guid.CreateVersion7(),
                User = user,
                ClientId = "test-client",
                DeviceName = "test-device",
            };
            await (database.OAuthGrants.AddAsync(grant)).ConfigureAwait(false);
            await (database.OAuthTokens.AddAsync(new OAuthTokenDB
            {
                Id = Guid.CreateVersion7(),
                Grant = grant,
                TokenType = "access",
                TokenHash = Enumerable.Repeat((byte)(index + 1), 32).ToArray(),
                ExpiresAt = now.AddHours(1),
            })).ConfigureAwait(false);
            await (database.OAuthAuthorizationCodes.AddAsync(new OAuthAuthorizationCodeDB
            {
                Id = Guid.CreateVersion7(),
                User = user,
                ClientId = "test-client",
                RedirectUri = "https://example.test/callback",
                DeviceName = "test-device",
                CodeChallenge = "test-challenge",
                CodeHash = Enumerable.Repeat((byte)(index + 4), 32).ToArray(),
                ExpiresAt = now.AddMinutes(5),
            })).ConfigureAwait(false);
            await (database.JmapPushSubscriptions.AddAsync(new JmapPushSubscriptionDB
            {
                Id = Guid.CreateVersion7(),
                SubscriptionObjectId = Guid.CreateVersion7().ToString("N"),
                UserId = user.Id,
                DeviceClientId = "test-client",
                Url = "https://push.example.test/notify",
                VerificationCode = "test-code",
                ExpiresAt = now.AddHours(1),
            })).ConfigureAwait(false);
        }
        await database.SaveChangesAsync().ConfigureAwait(false);

        var administration = new MailAdministrationService(database);
        Assert.IsTrue((await administration.SetDomainActiveAsync("example.test", false).ConfigureAwait(false)).Succeeded);
        database.ChangeTracker.Clear();

        var grants = await database.OAuthGrants.AsNoTracking().ToDictionaryAsync(grant => grant.UserId).ConfigureAwait(false);
        var tokens = await database.OAuthTokens.AsNoTracking()
            .Include(token => token.Grant).ToListAsync().ConfigureAwait(false);
        var codes = await database.OAuthAuthorizationCodes.AsNoTracking()
            .ToDictionaryAsync(code => code.UserId).ConfigureAwait(false);
        foreach (var user in affectedUsers)
        {
            Assert.IsNotNull(grants[user.Id].RevokedAt);
            Assert.IsNotNull(tokens.Single(token => token.Grant.UserId == user.Id).RevokedAt);
            Assert.IsNotNull(codes[user.Id].ConsumedAt);
        }
        Assert.IsNull(grants[retainedUser.Id].RevokedAt);
        Assert.IsNull(tokens.Single(token => token.Grant.UserId == retainedUser.Id).RevokedAt);
        Assert.IsNull(codes[retainedUser.Id].ConsumedAt);
        var remainingSubscriptions = await database.JmapPushSubscriptions.AsNoTracking()
            .Select(subscription => subscription.UserId).ToArrayAsync().ConfigureAwait(false);
        CollectionAssert.AreEqual(new[] { retainedUser.Id }, remainingSubscriptions);

        Assert.IsTrue((await administration.SetAccountActiveAsync(retainedUser.Id, false).ConfigureAwait(false)).Succeeded);
        database.ChangeTracker.Clear();
        Assert.AreEqual(3, await database.OAuthGrants.CountAsync(grant => grant.RevokedAt != null).ConfigureAwait(false));
        Assert.AreEqual(3, await database.OAuthTokens.CountAsync(token => token.RevokedAt != null).ConfigureAwait(false));
        Assert.AreEqual(3, await database.OAuthAuthorizationCodes.CountAsync(code => code.ConsumedAt != null).ConfigureAwait(false));
        Assert.AreEqual(0, await database.JmapPushSubscriptions.CountAsync().ConfigureAwait(false));
    }

    private static UserDB CreateUser(string username, CompanyDB company) => new()
    {
        Id = Guid.CreateVersion7(),
        Username = username,
        PasswordHash = "unused",
        Role = "User",
        IsActive = true,
        Company = company,
    };
}
