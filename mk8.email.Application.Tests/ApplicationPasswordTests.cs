using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using mk8.email.Application.Services;
using mk8.email.Infrastructure.Data;
using mk8.email.Infrastructure.Models;
using mk8.email.Utils;

namespace mk8.email.Application.Tests;

[TestClass]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1812", Justification = "MSTest DiscoverInternals instantiates this test class by reflection; focused discovery is verified by executed test counts.")]
internal sealed class ApplicationPasswordTests
{
    private const string Username = "user@example.com";
    private const string AccountPassword = "primary-account-password";

    [TestMethod]
    public async Task GeneratedPasswordAuthenticatesAndRecordsItsLastUse()
    {
        var database = CreateDatabase();
        await using var databaseLifetime = database.ConfigureAwait(false);
        var service = new ApplicationPasswordService(database);

        var created = await service.CreateAsync("USER@EXAMPLE.COM", " Thunderbird laptop ").ConfigureAwait(false);

        Assert.IsTrue(created.Succeeded);
        Assert.IsNotNull(created.Id);
        Assert.IsNotNull(created.Password);
        StringAssert.StartsWith(created.Password, $"mk8_{created.Id:N}_", StringComparison.Ordinal);
        Assert.AreEqual(64, created.Password.Length);
        var stored = await database.ApplicationPasswords.SingleAsync().ConfigureAwait(false);
        Assert.AreEqual("Thunderbird laptop", stored.Name, StringComparer.Ordinal);
        Assert.AreNotEqual(created.Password, stored.PasswordHash, StringComparer.Ordinal);

        var authenticated = await new MailAuthenticator(database)
            .AuthenticateAsync(Username, created.Password).ConfigureAwait(false);
        var primaryAuthentication = await new MailAuthenticator(database)
            .AuthenticatePrimaryAsync(Username, created.Password).ConfigureAwait(false);

        Assert.IsNotNull(authenticated);
        Assert.AreEqual(Username, authenticated.Username, StringComparer.Ordinal);
        Assert.IsNull(primaryAuthentication);
        Assert.IsNotNull(stored.LastUsedAt);
    }

    [TestMethod]
    public async Task RevocationInvalidatesOnlyTheSelectedPassword()
    {
        var database = CreateDatabase();
        await using var databaseLifetime = database.ConfigureAwait(false);
        var service = new ApplicationPasswordService(database);
        var revoked = await service.CreateAsync(Username, "Old phone").ConfigureAwait(false);
        var retained = await service.CreateAsync(Username, "Current laptop").ConfigureAwait(false);

        Assert.IsTrue(await service.RevokeAsync(Username, revoked.Id!.Value).ConfigureAwait(false));

        var authenticator = new MailAuthenticator(database);
        Assert.IsNull(await authenticator.AuthenticateAsync(Username, revoked.Password!).ConfigureAwait(false));
        Assert.IsNotNull(await authenticator.AuthenticateAsync(Username, retained.Password!).ConfigureAwait(false));
        var listed = await service.ListAsync(Username).ConfigureAwait(false);
        Assert.AreEqual(2, listed.Count);
        Assert.IsNotNull(listed.Single(item => item.Id == revoked.Id).RevokedAt);
        Assert.IsNull(listed.Single(item => item.Id == retained.Id).RevokedAt);
    }

    [TestMethod]
    public async Task PasswordResetRevokesEveryApplicationPassword()
    {
        var database = CreateDatabase();
        await using var databaseLifetime = database.ConfigureAwait(false);
        var service = new ApplicationPasswordService(database);
        var created = await service.CreateAsync(Username, "Thunderbird").ConfigureAwait(false);
        var userId = await database.Users.Select(user => user.Id).SingleAsync().ConfigureAwait(false);

        var result = await new MailAdministrationService(database)
            .ResetPasswordAsync(userId, "replacement-account-password").ConfigureAwait(false);

        Assert.IsTrue(result.Succeeded);
        Assert.IsNull(await new MailAuthenticator(database)
            .AuthenticateAsync(Username, created.Password!).ConfigureAwait(false));
        Assert.IsNotNull((await service.ListAsync(Username).ConfigureAwait(false)).Single().RevokedAt);
    }

    [TestMethod]
    public async Task UnknownOrInactiveAccountCannotCreatePassword()
    {
        var database = CreateDatabase();
        await using var databaseLifetime = database.ConfigureAwait(false);
        var service = new ApplicationPasswordService(database);

        Assert.IsFalse((await service.CreateAsync("missing@example.com", "Laptop").ConfigureAwait(false)).Succeeded);
        var user = await database.Users.SingleAsync().ConfigureAwait(false);
        user.IsActive = false;
        await database.SaveChangesAsync().ConfigureAwait(false);
        Assert.IsFalse((await service.CreateAsync(Username, "Laptop").ConfigureAwait(false)).Succeeded);
        Assert.AreEqual(0, await database.ApplicationPasswords.CountAsync().ConfigureAwait(false));
    }

    private static EmailDbContext CreateDatabase()
    {
        var options = new DbContextOptionsBuilder<EmailDbContext>()
            .UseInMemoryDatabase($"application-passwords-{Guid.NewGuid():N}")
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
            PasswordHash = PasswordHasher.Hash(AccountPassword),
            Company = company,
            IsActive = true,
        });
        database.SaveChanges();
        return database;
    }
}
