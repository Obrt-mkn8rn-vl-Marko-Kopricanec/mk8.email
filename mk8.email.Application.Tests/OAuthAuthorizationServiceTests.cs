using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using mk8.email.Application.Protocol;
using mk8.email.Application.Services;
using mk8.email.Infrastructure.Data;
using mk8.email.Configuration;
using mk8.email.Infrastructure.Models;
using mk8.email.Utils;

namespace mk8.email.Application.Tests;

[TestClass]
public sealed class OAuthAuthorizationServiceTests
{
    [TestMethod]
    public async Task AuthorizationCodeIsHashedBoundToPkceAndSingleUse()
    {
        await using var database = CreateDatabase();
        var environment = CreateEnvironment();
        var tokenService = new OAuthTokenService(database, environment);
        var service = new OAuthAuthorizationService(database, tokenService, environment);
        var userId = await database.Users.Select(user => user.Id).SingleAsync();
        var verifier = new string('v', 64);
        var challenge = OAuthProtocolValues.CreatePkceChallenge(verifier);

        var code = await service.CreateAuthorizationCodeAsync(
            userId,
            "thunderbird",
            "http://127.0.0.1:49152/",
            "Work laptop",
            ["offline_access", "imap", "smtp"],
            challenge);

        Assert.IsNotNull(code);
        StringAssert.StartsWith(code, "mk8_ac_");
        var stored = await database.OAuthAuthorizationCodes.SingleAsync();
        Assert.AreEqual(32, stored.CodeHash.Length);
        Assert.IsFalse(stored.CodeHash.SequenceEqual(Encoding.ASCII.GetBytes(code)));
        Assert.IsNull(await service.RedeemAuthorizationCodeAsync(
            code,
            "thunderbird",
            "http://127.0.0.1:49152/",
            new string('x', 64)));

        var pair = await service.RedeemAuthorizationCodeAsync(
            code,
            "thunderbird",
            "http://127.0.0.1:49152/",
            verifier);

        Assert.IsNotNull(pair);
        Assert.IsNotNull(await tokenService.AuthenticateAccessTokenAsync(pair.AccessToken, "imap"));
        Assert.IsNull(await service.RedeemAuthorizationCodeAsync(
            code,
            "thunderbird",
            "http://127.0.0.1:49152/",
            verifier));
        Assert.IsNotNull(stored.ConsumedAt);
    }

    [TestMethod]
    public async Task AuthorizationCodeRejectsUnregisteredOrUnsafeRequests()
    {
        await using var database = CreateDatabase();
        var environment = CreateEnvironment();
        var service = new OAuthAuthorizationService(
            database,
            new OAuthTokenService(database, environment),
            environment);
        var userId = await database.Users.Select(user => user.Id).SingleAsync();
        var challenge = OAuthProtocolValues.CreatePkceChallenge(new string('v', 64));

        Assert.IsNull(await service.CreateAuthorizationCodeAsync(
            userId,
            "other-client",
            "http://127.0.0.1:49152/",
            "Laptop",
            ["offline_access", "imap"],
            challenge));
        Assert.IsNull(await service.CreateAuthorizationCodeAsync(
            userId,
            "thunderbird",
            "https://attacker.example/",
            "Laptop",
            ["offline_access", "imap"],
            challenge));
        Assert.IsNull(await service.CreateAuthorizationCodeAsync(
            userId,
            "thunderbird",
            "http://127.0.0.1:49152/",
            "Laptop",
            ["imap"],
            challenge));
        Assert.AreEqual(0, await database.OAuthAuthorizationCodes.CountAsync());
    }

    [TestMethod]
    public async Task PasswordResetInvalidatesOutstandingAuthorizationCode()
    {
        await using var database = CreateDatabase();
        var environment = CreateEnvironment();
        var tokenService = new OAuthTokenService(database, environment);
        var service = new OAuthAuthorizationService(database, tokenService, environment);
        var userId = await database.Users.Select(user => user.Id).SingleAsync();
        var verifier = new string('v', 64);
        var code = await service.CreateAuthorizationCodeAsync(
            userId,
            "thunderbird",
            "http://127.0.0.1:49152/",
            "Laptop",
            ["offline_access", "imap"],
            OAuthProtocolValues.CreatePkceChallenge(verifier));

        var result = await new MailAdministrationService(database)
            .ResetPasswordAsync(userId, "replacement-password");

        Assert.IsTrue(result.Succeeded);
        Assert.IsNull(await service.RedeemAuthorizationCodeAsync(
            code!,
            "thunderbird",
            "http://127.0.0.1:49152/",
            verifier));
        Assert.IsNotNull((await database.OAuthAuthorizationCodes.SingleAsync()).ConsumedAt);
    }

    private static EnvironmentConfig CreateEnvironment() => new()
    {
        OAuth = new OAuthConfig
        {
            EnableOAuth = true,
            ClientId = "thunderbird",
            AccessTokenMinutes = 10,
            RefreshTokenDays = 90,
            AuthorizationCodeMinutes = 5,
        },
    };

    private static EmailDbContext CreateDatabase()
    {
        var options = new DbContextOptionsBuilder<EmailDbContext>()
            .UseInMemoryDatabase($"oauth-authorization-{Guid.NewGuid():N}")
            .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        var database = new EmailDbContext(options);
        var company = new CompanyDB
        {
            Id = Guid.CreateVersion7(),
            Name = "OAuth Test",
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
            Username = "user@example.com",
            PasswordHash = PasswordHasher.Hash("primary-password"),
            Company = company,
            IsActive = true,
        });
        database.SaveChanges();
        return database;
    }
}
