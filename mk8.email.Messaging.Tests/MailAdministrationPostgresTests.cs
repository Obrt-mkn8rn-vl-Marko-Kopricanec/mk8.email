using Microsoft.EntityFrameworkCore;
using mk8.email.Application.Services;
using mk8.email.Infrastructure.Data;
using mk8.email.Infrastructure.Models;

namespace mk8.email.Messaging.Tests;

[TestClass]
[TestCategory("PostgreSQL")]
public sealed class MailAdministrationPostgresTests
{
    [TestMethod]
    [Timeout(30_000)]
    public async Task DeactivationRevokesOnlyMatchingDomainThenTheRemainingAccount()
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

        await using var database = new EmailDbContext(options);
        await database.Database.EnsureCreatedAsync();
        database.Companies.Add(company);
        database.Addresses.AddRange(
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
            });
        database.Users.AddRange(users);
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
            database.OAuthGrants.Add(grant);
            database.OAuthTokens.Add(new OAuthTokenDB
            {
                Id = Guid.CreateVersion7(),
                Grant = grant,
                TokenType = "access",
                TokenHash = Enumerable.Repeat((byte)(index + 1), 32).ToArray(),
                ExpiresAt = now.AddHours(1),
            });
            database.OAuthAuthorizationCodes.Add(new OAuthAuthorizationCodeDB
            {
                Id = Guid.CreateVersion7(),
                User = user,
                ClientId = "test-client",
                RedirectUri = "https://example.test/callback",
                DeviceName = "test-device",
                CodeChallenge = "test-challenge",
                CodeHash = Enumerable.Repeat((byte)(index + 4), 32).ToArray(),
                ExpiresAt = now.AddMinutes(5),
            });
            database.JmapPushSubscriptions.Add(new JmapPushSubscriptionDB
            {
                Id = Guid.CreateVersion7(),
                SubscriptionObjectId = Guid.CreateVersion7().ToString("N"),
                UserId = user.Id,
                DeviceClientId = "test-client",
                Url = "https://push.example.test/notify",
                VerificationCode = "test-code",
                ExpiresAt = now.AddHours(1),
            });
        }
        await database.SaveChangesAsync();

        var administration = new MailAdministrationService(database);
        Assert.IsTrue((await administration.SetDomainActiveAsync("example.test", false)).Succeeded);
        database.ChangeTracker.Clear();

        var grants = await database.OAuthGrants.AsNoTracking().ToDictionaryAsync(grant => grant.UserId);
        var tokens = await database.OAuthTokens.AsNoTracking()
            .Include(token => token.Grant).ToListAsync();
        var codes = await database.OAuthAuthorizationCodes.AsNoTracking()
            .ToDictionaryAsync(code => code.UserId);
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
            .Select(subscription => subscription.UserId).ToArrayAsync();
        CollectionAssert.AreEqual(new[] { retainedUser.Id }, remainingSubscriptions);

        Assert.IsTrue((await administration.SetAccountActiveAsync(retainedUser.Id, false)).Succeeded);
        database.ChangeTracker.Clear();
        Assert.AreEqual(3, await database.OAuthGrants.CountAsync(grant => grant.RevokedAt != null));
        Assert.AreEqual(3, await database.OAuthTokens.CountAsync(token => token.RevokedAt != null));
        Assert.AreEqual(3, await database.OAuthAuthorizationCodes.CountAsync(code => code.ConsumedAt != null));
        Assert.AreEqual(0, await database.JmapPushSubscriptions.CountAsync());
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
