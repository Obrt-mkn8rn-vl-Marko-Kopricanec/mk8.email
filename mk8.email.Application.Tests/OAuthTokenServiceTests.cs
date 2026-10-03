using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using mk8.email.Application.Services;
using mk8.email.Infrastructure.Data;
using mk8.email.Configuration;
using mk8.email.Infrastructure.Models;
using mk8.email.Utils;

namespace mk8.email.Application.Tests;

[TestClass]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1812", Justification = "MSTest DiscoverInternals instantiates this test class by reflection; focused discovery is verified by executed test counts.")]
internal sealed class OAuthTokenServiceTests
{
    private const string Username = "user@example.com";

    [TestMethod]
    public async Task AccessTokenIsOpaqueHashedScopedAndAudited()
    {
        var database = CreateDatabase();
        await using var databaseLifetime = database.ConfigureAwait(false);
        var userId = await database.Users.Select(user => user.Id).SingleAsync().ConfigureAwait(false);
        var service = CreateService(database);

        var pair = await service.CreateGrantAsync(
            userId,
            "thunderbird",
            "Work laptop",
            ["smtp", "imap", "offline_access"]).ConfigureAwait(false);

        Assert.IsNotNull(pair);
        StringAssert.StartsWith(pair.AccessToken, "mk8_at_", StringComparison.Ordinal);
        StringAssert.StartsWith(pair.RefreshToken, "mk8_rt_", StringComparison.Ordinal);
        Assert.AreEqual("imap offline_access smtp", pair.Scope, StringComparer.Ordinal);
        var storedHashes = await database.OAuthTokens
            .Select(token => token.TokenHash)
            .ToListAsync().ConfigureAwait(false);
        Assert.IsTrue(storedHashes.All(hash => hash.Length == 32));
        Assert.IsTrue(storedHashes.All(hash =>
            !hash.SequenceEqual(System.Text.Encoding.ASCII.GetBytes(pair.AccessToken))));
        database.ChangeTracker.Clear();

        var authenticated = await service.AuthenticateAccessTokenAsync(pair.AccessToken, "imap").ConfigureAwait(false);

        Assert.IsNotNull(authenticated);
        Assert.AreEqual(Username, authenticated.Username, StringComparer.Ordinal);
        Assert.IsNull(await service.AuthenticateAccessTokenAsync(pair.AccessToken, "jmap").ConfigureAwait(false));
        var grant = await database.OAuthGrants.AsNoTracking().SingleAsync().ConfigureAwait(false);
        var access = await database.OAuthTokens.AsNoTracking()
            .SingleAsync(token => token.TokenType == OAuthTokenService.AccessTokenType).ConfigureAwait(false);
        Assert.IsNotNull(grant.LastUsedAt);
        Assert.IsNotNull(access.LastUsedAt);
    }

    [TestMethod]
    public async Task RefreshRotatesBothTokensAndInvalidatesPriorAccess()
    {
        var database = CreateDatabase();
        await using var databaseLifetime = database.ConfigureAwait(false);
        var userId = await database.Users.Select(user => user.Id).SingleAsync().ConfigureAwait(false);
        var service = CreateService(database);
        var original = await service.CreateGrantAsync(
            userId,
            "thunderbird",
            "Work laptop",
            ["imap", "offline_access"]).ConfigureAwait(false);

        var rotated = await service.RefreshAsync(original!.RefreshToken, "thunderbird").ConfigureAwait(false);

        Assert.IsNotNull(rotated);
        Assert.AreNotEqual(original.AccessToken, rotated.AccessToken, StringComparer.Ordinal);
        Assert.AreNotEqual(original.RefreshToken, rotated.RefreshToken, StringComparer.Ordinal);
        Assert.IsNull(await service.AuthenticateAccessTokenAsync(original.AccessToken, "imap").ConfigureAwait(false));
        Assert.IsNotNull(await service.AuthenticateAccessTokenAsync(rotated.AccessToken, "imap").ConfigureAwait(false));
        var originalRefresh = await database.OAuthTokens.AsNoTracking()
            .SingleAsync(token => token.TokenHash.SequenceEqual(HashToken(original.RefreshToken))).ConfigureAwait(false);
        Assert.IsNotNull(originalRefresh.RevokedAt);
        Assert.IsNotNull(originalRefresh.ReplacedByTokenId);
    }

    [TestMethod]
    public async Task ReplayedRefreshTokenRevokesTheWholeDeviceGrant()
    {
        var database = CreateDatabase();
        await using var databaseLifetime = database.ConfigureAwait(false);
        var userId = await database.Users.Select(user => user.Id).SingleAsync().ConfigureAwait(false);
        var service = CreateService(database);
        var original = await service.CreateGrantAsync(
            userId,
            "thunderbird",
            "Work laptop",
            ["imap", "offline_access"]).ConfigureAwait(false);
        var rotated = await service.RefreshAsync(original!.RefreshToken, "thunderbird").ConfigureAwait(false);

        Assert.IsNull(await service.RefreshAsync(original.RefreshToken, "thunderbird").ConfigureAwait(false));

        Assert.IsNull(await service.AuthenticateAccessTokenAsync(rotated!.AccessToken, "imap").ConfigureAwait(false));
        Assert.IsNull(await service.RefreshAsync(rotated.RefreshToken, "thunderbird").ConfigureAwait(false));
        Assert.IsNotNull((await service.ListGrantsAsync(userId).ConfigureAwait(false)).Single().RevokedAt);
    }

    [TestMethod]
    public async Task ExplicitGrantRevocationInvalidatesAccessAndRefreshTokens()
    {
        var database = CreateDatabase();
        await using var databaseLifetime = database.ConfigureAwait(false);
        var userId = await database.Users.Select(user => user.Id).SingleAsync().ConfigureAwait(false);
        var service = CreateService(database);
        var pair = await service.CreateGrantAsync(
            userId,
            "thunderbird",
            "Phone",
            ["imap", "offline_access"]).ConfigureAwait(false);

        Assert.IsTrue(await service.RevokeGrantAsync(userId, pair!.GrantId).ConfigureAwait(false));

        Assert.IsNull(await service.AuthenticateAccessTokenAsync(pair.AccessToken, "imap").ConfigureAwait(false));
        Assert.IsNull(await service.RefreshAsync(pair.RefreshToken, "thunderbird").ConfigureAwait(false));
        Assert.IsTrue(await database.OAuthTokens.AllAsync(token => token.RevokedAt != null).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task PasswordResetRevokesOAuthGrants()
    {
        var database = CreateDatabase();
        await using var databaseLifetime = database.ConfigureAwait(false);
        var userId = await database.Users.Select(user => user.Id).SingleAsync().ConfigureAwait(false);
        var service = CreateService(database);
        var pair = await service.CreateGrantAsync(
            userId,
            "thunderbird",
            "Phone",
            ["imap", "offline_access"]).ConfigureAwait(false);

        var result = await new MailAdministrationService(database)
            .ResetPasswordAsync(userId, "replacement-account-password").ConfigureAwait(false);

        Assert.IsTrue(result.Succeeded);
        Assert.IsNull(await service.AuthenticateAccessTokenAsync(pair!.AccessToken, "imap").ConfigureAwait(false));
        Assert.IsNotNull((await service.ListGrantsAsync(userId).ConfigureAwait(false)).Single().RevokedAt);
    }

    [TestMethod]
    public async Task InvalidScopeOrInactiveAccountCannotReceiveTokens()
    {
        var database = CreateDatabase();
        await using var databaseLifetime = database.ConfigureAwait(false);
        var user = await database.Users.SingleAsync().ConfigureAwait(false);
        var service = CreateService(database);

        Assert.IsNull(await service.CreateGrantAsync(
            user.Id,
            "thunderbird",
            "Laptop",
            ["administrator"]).ConfigureAwait(false));
        user.IsActive = false;
        await database.SaveChangesAsync().ConfigureAwait(false);
        Assert.IsNull(await service.CreateGrantAsync(
            user.Id,
            "thunderbird",
            "Laptop",
            ["imap"]).ConfigureAwait(false));
        Assert.AreEqual(0, await database.OAuthGrants.CountAsync().ConfigureAwait(false));
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
