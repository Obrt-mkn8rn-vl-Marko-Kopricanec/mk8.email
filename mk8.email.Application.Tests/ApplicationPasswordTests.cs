using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using mk8.email.Application.Services;
using mk8.email.Infrastructure.Data;
using mk8.email.Infrastructure.Models;
using mk8.email.Utils;

namespace mk8.email.Application.Tests;

[TestClass]
public sealed class ApplicationPasswordTests
{
    private const string Username = "user@example.com";
    private const string AccountPassword = "primary-account-password";

    [TestMethod]
    public async Task GeneratedPasswordAuthenticatesAndRecordsItsLastUse()
    {
        await using var database = CreateDatabase();
        var service = new ApplicationPasswordService(database);

        var created = await service.CreateAsync("USER@EXAMPLE.COM", " Thunderbird laptop ");

        Assert.IsTrue(created.Succeeded);
        Assert.IsNotNull(created.Id);
        Assert.IsNotNull(created.Password);
        StringAssert.StartsWith(created.Password, $"mk8_{created.Id:N}_");
        Assert.AreEqual(64, created.Password.Length);
        var stored = await database.ApplicationPasswords.SingleAsync();
        Assert.AreEqual("Thunderbird laptop", stored.Name);
        Assert.AreNotEqual(created.Password, stored.PasswordHash);

        var authenticated = await new MailAuthenticator(database)
            .AuthenticateAsync(Username, created.Password);

        Assert.IsNotNull(authenticated);
        Assert.AreEqual(Username, authenticated.Username);
        Assert.IsNotNull(stored.LastUsedAt);
    }

    [TestMethod]
    public async Task RevocationInvalidatesOnlyTheSelectedPassword()
    {
        await using var database = CreateDatabase();
        var service = new ApplicationPasswordService(database);
        var revoked = await service.CreateAsync(Username, "Old phone");
        var retained = await service.CreateAsync(Username, "Current laptop");

        Assert.IsTrue(await service.RevokeAsync(Username, revoked.Id!.Value));

        var authenticator = new MailAuthenticator(database);
        Assert.IsNull(await authenticator.AuthenticateAsync(Username, revoked.Password!));
        Assert.IsNotNull(await authenticator.AuthenticateAsync(Username, retained.Password!));
        var listed = await service.ListAsync(Username);
        Assert.AreEqual(2, listed.Count);
        Assert.IsNotNull(listed.Single(item => item.Id == revoked.Id).RevokedAt);
        Assert.IsNull(listed.Single(item => item.Id == retained.Id).RevokedAt);
    }

    [TestMethod]
    public async Task PasswordResetRevokesEveryApplicationPassword()
    {
        await using var database = CreateDatabase();
        var service = new ApplicationPasswordService(database);
        var created = await service.CreateAsync(Username, "Thunderbird");
        var userId = await database.Users.Select(user => user.Id).SingleAsync();

        var result = await new MailAdministrationService(database)
            .ResetPasswordAsync(userId, "replacement-account-password");

        Assert.IsTrue(result.Succeeded);
        Assert.IsNull(await new MailAuthenticator(database)
            .AuthenticateAsync(Username, created.Password!));
        Assert.IsNotNull((await service.ListAsync(Username)).Single().RevokedAt);
    }

    [TestMethod]
    public async Task UnknownOrInactiveAccountCannotCreatePassword()
    {
        await using var database = CreateDatabase();
        var service = new ApplicationPasswordService(database);

        Assert.IsFalse((await service.CreateAsync("missing@example.com", "Laptop")).Succeeded);
        var user = await database.Users.SingleAsync();
        user.IsActive = false;
        await database.SaveChangesAsync();
        Assert.IsFalse((await service.CreateAsync(Username, "Laptop")).Succeeded);
        Assert.AreEqual(0, await database.ApplicationPasswords.CountAsync());
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
