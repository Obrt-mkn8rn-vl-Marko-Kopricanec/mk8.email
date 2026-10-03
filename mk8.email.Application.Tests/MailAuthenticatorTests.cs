using Microsoft.EntityFrameworkCore;
using mk8.email.Application.Services;
using mk8.email.Infrastructure.Data;
using mk8.email.Infrastructure.Models;
using mk8.email.Utils;

namespace mk8.email.Application.Tests;

[TestClass]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1812", Justification = "MSTest DiscoverInternals instantiates this test class by reflection; focused discovery is verified by executed test counts.")]
internal sealed class MailAuthenticatorTests
{
    private const string Username = "user@mk8n.com";
    private const string Password = "test-password-value";

    [TestMethod]
    public async Task ActiveAccountAuthenticatesWithNormalizedUsername()
    {
        var database = CreateDatabase(domainActive: true, companyActive: true);
        await using var databaseLifetime = database.ConfigureAwait(false);
        var authenticator = new MailAuthenticator(database);

        var result = await authenticator.AuthenticateAsync("USER@MK8N.COM", Password).ConfigureAwait(false);

        Assert.IsNotNull(result);
        Assert.AreEqual(Username, result.Username, StringComparer.Ordinal);
    }

    [TestMethod]
    public async Task InactiveDomainRejectsMailAuthentication()
    {
        var database = CreateDatabase(domainActive: false, companyActive: true);
        await using var databaseLifetime = database.ConfigureAwait(false);
        var authenticator = new MailAuthenticator(database);

        var result = await authenticator.AuthenticateAsync(Username, Password).ConfigureAwait(false);

        Assert.IsNull(result);
    }

    [TestMethod]
    public async Task InactiveCompanyRejectsMailAuthentication()
    {
        var database = CreateDatabase(domainActive: true, companyActive: false);
        await using var databaseLifetime = database.ConfigureAwait(false);
        var authenticator = new MailAuthenticator(database);

        var result = await authenticator.AuthenticateAsync(Username, Password).ConfigureAwait(false);

        Assert.IsNull(result);
    }

    private static EmailDbContext CreateDatabase(bool domainActive, bool companyActive)
    {
        var options = new DbContextOptionsBuilder<EmailDbContext>()
            .UseInMemoryDatabase($"mail-authenticator-{Guid.NewGuid():N}")
            .Options;
        var database = new EmailDbContext(options);
        var company = new CompanyDB
        {
            Id = Guid.CreateVersion7(),
            Name = "Test Company",
            IsActive = companyActive,
        };
        database.Addresses.Add(new AddressDB
        {
            Id = Guid.CreateVersion7(),
            Domain = "mk8n.com",
            Company = company,
            IsActive = domainActive,
        });
        database.Users.Add(new UserDB
        {
            Id = Guid.CreateVersion7(),
            Username = Username,
            PasswordHash = PasswordHasher.Hash(Password),
            Company = company,
            IsActive = true,
        });
        database.SaveChanges();
        return database;
    }
}
