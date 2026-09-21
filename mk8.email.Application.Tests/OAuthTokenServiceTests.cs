using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using mk8.email.Application.Services;
using mk8.email.Infrastructure.Data;
using mk8.email.Infrastructure.Environment;
using mk8.email.Infrastructure.Models;
using mk8.email.Utils;

namespace mk8.email.Application.Tests;

[TestClass]
public sealed class OAuthTokenServiceTests
{
    private const string Username = "user@example.com";

    [TestMethod]
    public async Task AccessTokenIsOpaqueHashedScopedAndAudited()
    {
        await using var database = CreateDatabase();
        var userId = await database.Users.Select(user => user.Id).SingleAsync();
        var service = CreateService(database);

        var pair = await service.CreateGrantAsync(
            userId,
            "thunderbird",
            "Work laptop",
            ["smtp", "imap", "offline_access"]);

        Assert.IsNotNull(pair);
        StringAssert.StartsWith(pair.AccessToken, "mk8_at_");
        StringAssert.StartsWith(pair.RefreshToken, "mk8_rt_");
        Assert.AreEqual("imap offline_access smtp", pair.Scope);
        var storedHashes = await database.OAuthTokens
            .Select(token => token.TokenHash)
            .ToListAsync();
        Assert.IsTrue(storedHashes.All(hash => hash.Length == 32));
        Assert.IsTrue(storedHashes.All(hash =>
            !hash.SequenceEqual(System.Text.Encoding.ASCII.GetBytes(pair.AccessToken))));
        database.ChangeTracker.Clear();

        var authenticated = await service.AuthenticateAccessTokenAsync(pair.AccessToken, "imap");

        Assert.IsNotNull(authenticated);
        Assert.AreEqual(Username, authenticated.Username);
        Assert.IsNull(await service.AuthenticateAccessTokenAsync(pair.AccessToken, "jmap"));
        var grant = await database.OAuthGrants.AsNoTracking().SingleAsync();
        var access = await database.OAuthTokens.AsNoTracking()
            .SingleAsync(token => token.TokenType == OAuthTokenService.AccessTokenType);
        Assert.IsNotNull(grant.LastUsedAt);
        Assert.IsNotNull(access.LastUsedAt);
    }

    [TestMethod]
    public async Task RefreshRotatesBothTokensAndInvalidatesPriorAccess()
    {
        await using var database = CreateDatabase();
        var userId = await database.Users.Select(user => user.Id).SingleAsync();
        var service = CreateService(database);
        var original = await service.CreateGrantAsync(
            userId,
            "thunderbird",
            "Work laptop",
            ["imap", "offline_access"]);

        var rotated = await service.RefreshAsync(original!.RefreshToken, "thunderbird");

        Assert.IsNotNull(rotated);
        Assert.AreNotEqual(original.AccessToken, rotated.AccessToken);
        Assert.AreNotEqual(original.RefreshToken, rotated.RefreshToken);
        Assert.IsNull(await service.AuthenticateAccessTokenAsync(original.AccessToken, "imap"));
        Assert.IsNotNull(await service.AuthenticateAccessTokenAsync(rotated.AccessToken, "imap"));
        var originalRefresh = await database.OAuthTokens.AsNoTracking()
            .SingleAsync(token => token.TokenHash.SequenceEqual(HashToken(original.RefreshToken)));
        Assert.IsNotNull(originalRefresh.RevokedAt);
        Assert.IsNotNull(originalRefresh.ReplacedByTokenId);
    }

    [TestMethod]
    public async Task ReplayedRefreshTokenRevokesTheWholeDeviceGrant()
    {
        await using var database = CreateDatabase();
        var userId = await database.Users.Select(user => user.Id).SingleAsync();
        var service = CreateService(database);
        var original = await service.CreateGrantAsync(
            userId,
            "thunderbird",
            "Work laptop",
            ["imap", "offline_access"]);
        var rotated = await service.RefreshAsync(original!.RefreshToken, "thunderbird");

        Assert.IsNull(await service.RefreshAsync(original.RefreshToken, "thunderbird"));

        Assert.IsNull(await service.AuthenticateAccessTokenAsync(rotated!.AccessToken, "imap"));
        Assert.IsNull(await service.RefreshAsync(rotated.RefreshToken, "thunderbird"));
        Assert.IsNotNull((await service.ListGrantsAsync(userId)).Single().RevokedAt);
    }

    [TestMethod]
    public async Task ExplicitGrantRevocationInvalidatesAccessAndRefreshTokens()
    {
        await using var database = CreateDatabase();
        var userId = await database.Users.Select(user => user.Id).SingleAsync();
        var service = CreateService(database);
        var pair = await service.CreateGrantAsync(
            userId,
            "thunderbird",
            "Phone",
            ["imap", "offline_access"]);

        Assert.IsTrue(await service.RevokeGrantAsync(userId, pair!.GrantId));

        Assert.IsNull(await service.AuthenticateAccessTokenAsync(pair.AccessToken, "imap"));
        Assert.IsNull(await service.RefreshAsync(pair.RefreshToken, "thunderbird"));
        Assert.IsTrue(await database.OAuthTokens.AllAsync(token => token.RevokedAt != null));
    }

    [TestMethod]
    public async Task PasswordResetRevokesOAuthGrants()
    {
        await using var database = CreateDatabase();
        var userId = await database.Users.Select(user => user.Id).SingleAsync();
        var service = CreateService(database);
        var pair = await service.CreateGrantAsync(
            userId,
            "thunderbird",
            "Phone",
            ["imap", "offline_access"]);

        var result = await new MailAdministrationService(database)
            .ResetPasswordAsync(userId, "replacement-account-password");

        Assert.IsTrue(result.Succeeded);
        Assert.IsNull(await service.AuthenticateAccessTokenAsync(pair!.AccessToken, "imap"));
        Assert.IsNotNull((await service.ListGrantsAsync(userId)).Single().RevokedAt);
    }

    [TestMethod]
    public async Task InvalidScopeOrInactiveAccountCannotReceiveTokens()
    {
        await using var database = CreateDatabase();
        var user = await database.Users.SingleAsync();
        var service = CreateService(database);

        Assert.IsNull(await service.CreateGrantAsync(
            user.Id,
            "thunderbird",
            "Laptop",
            ["administrator"]));
        user.IsActive = false;
        await database.SaveChangesAsync();
        Assert.IsNull(await service.CreateGrantAsync(
            user.Id,
            "thunderbird",
            "Laptop",
            ["imap"]));
        Assert.AreEqual(0, await database.OAuthGrants.CountAsync());
    }

    private static OAuthTokenService CreateService(EmailDbContext database) =>
        new(database, new EnvironmentConfig
        {
            OAuth = new OAuthConfig
            {
                AccessTokenMinutes = 10,
                RefreshTokenDays = 90,
            },
        });

    private static byte[] HashToken(string value) =>
        System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.ASCII.GetBytes(value));

    private static EmailDbContext CreateDatabase()
    {
        var options = new DbContextOptionsBuilder<EmailDbContext>()
            .UseInMemoryDatabase($"oauth-token-service-{Guid.NewGuid():N}")
            .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        var database = new EmailDbContext(options);
        var company = new CompanyDB
        {
            Id = Guid.CreateVersion7(),
            Name = "Example",
            IsActive = true,
        };
        database.Addresses.Add(new AddressDB
        {
            Id = Guid.CreateVersion7(),
            Domain = "example.com",
            Company = company,
            IsActive = true,
        });
        database.Users.Add(new UserDB
        {
            Id = Guid.CreateVersion7(),
            Username = Username,
            PasswordHash = PasswordHasher.Hash("primary-account-password"),
            Company = company,
            IsActive = true,
        });
        database.SaveChanges();
        return database;
    }
}
