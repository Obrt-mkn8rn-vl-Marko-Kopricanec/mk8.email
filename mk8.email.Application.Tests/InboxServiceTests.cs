using Microsoft.EntityFrameworkCore;
using mk8.email.Application.Services;
using mk8.email.Contracts.DTOs;
using mk8.email.Contracts.Enums;
using mk8.email.Infrastructure.Data;
using mk8.email.Infrastructure.Models;

namespace mk8.email.Application.Tests;

[TestClass]
public sealed class InboxServiceTests
{
    [TestMethod]
    [DataRow(UserRole.User, true, true)]
    [DataRow(UserRole.User, false, false)]
    [DataRow(UserRole.CompanyAdmin, true, true)]
    [DataRow(UserRole.CompanyAdmin, false, false)]
    [DataRow(UserRole.SuperAdmin, false, true)]
    public async Task CreationRetainsRoleAndCompanyBoundaries(UserRole role, bool sameCompany, bool allowed)
    {
        await using var fixture = await InboxFixture.CreateAsync(role, sameCompany).ConfigureAwait(false);
        var result = await fixture.Service.CreateInboxAsync(fixture.User.Id,
            new CreateInboxRequestDTO("new", fixture.Address.Id)).ConfigureAwait(false);
        Assert.AreEqual(allowed, result is not null);
        Assert.AreEqual(allowed ? 1 : 0, await fixture.Database.Inboxes.CountAsync().ConfigureAwait(false));
        Assert.AreEqual(allowed ? DefaultFolders.All.Count : 0,
            await fixture.Database.Folders.CountAsync().ConfigureAwait(false));
    }

    [TestMethod]
    [DataRow(UserRole.User, false)]
    [DataRow(UserRole.CompanyAdmin, true)]
    [DataRow(UserRole.SuperAdmin, true)]
    public async Task CreatingForAnotherOwnerRetainsRolePolicy(UserRole role, bool allowed)
    {
        await using var fixture = await InboxFixture.CreateAsync(role).ConfigureAwait(false);
        var other = new UserDB
        {
            Id = Guid.CreateVersion7(),
            Username = "other",
            PasswordHash = "unused",
            CompanyId = fixture.User.CompanyId,
        };
        fixture.Database.Users.Add(other);
        await fixture.Database.SaveChangesAsync().ConfigureAwait(false);
        var result = await fixture.Service.CreateInboxAsync(fixture.User.Id,
            new CreateInboxRequestDTO("new", fixture.Address.Id, other.Id)).ConfigureAwait(false);
        Assert.AreEqual(allowed, result is not null);
        if (allowed)
        {
            Assert.IsNotNull(result);
            Assert.AreEqual(other.Id, result.OwnerId);
        }
        else
            Assert.AreEqual(0, await fixture.Database.Folders.CountAsync().ConfigureAwait(false));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task OnlyPrimaryInboxesReceiveDefaultFolders(bool alias)
    {
        await using var fixture = await InboxFixture.CreateAsync(UserRole.SuperAdmin).ConfigureAwait(false);
        var target = await fixture.AddExistingInboxAsync().ConfigureAwait(false);
        var result = await fixture.Service.CreateInboxAsync(fixture.User.Id,
            new CreateInboxRequestDTO("new", fixture.Address.Id, AliasForInboxId: alias ? target.Id : null))
            .ConfigureAwait(false);
        Assert.IsNotNull(result);
        Assert.AreEqual(alias ? target.Id : (Guid?)null, result.AliasForInboxId);
        var folders = await fixture.Database.Folders.Where(folder => folder.InboxId == result.Id)
            .Select(folder => folder.Name).ToArrayAsync().ConfigureAwait(false);
        CollectionAssert.AreEquivalent(alias ? Array.Empty<string>() : DefaultFolders.All.ToArray(), folders);
        var listed = await fixture.Service.GetUserInboxesAsync(fixture.User.Id).ConfigureAwait(false);
        Assert.IsTrue(listed.Any(inbox => inbox.Id == result.Id && inbox.Domain == fixture.Address.Domain));
    }

    [TestMethod]
    public async Task OrdinaryUserCannotCreateASecondInbox()
    {
        await using var fixture = await InboxFixture.CreateAsync(UserRole.User).ConfigureAwait(false);
        await fixture.AddExistingInboxAsync().ConfigureAwait(false);
        Assert.IsNull(await fixture.Service.CreateInboxAsync(fixture.User.Id,
            new CreateInboxRequestDTO("second", fixture.Address.Id)).ConfigureAwait(false));
        Assert.AreEqual(1, await fixture.Database.Inboxes.CountAsync().ConfigureAwait(false));
        Assert.AreEqual(0, await fixture.Database.Folders.CountAsync().ConfigureAwait(false));
    }

    [TestMethod]
    [DataRow(false, 1, null, false)]
    [DataRow(true, 1, null, false)]
    [DataRow(false, 0, 1, false)]
    [DataRow(true, 0, 1, false)]
    [DataRow(false, 1, 0, true)]
    [DataRow(true, 1, 0, true)]
    public async Task CapacityRetainsGlobalFallbackAndExplicitUnlimitedOverrides(
        bool domainLimit, int globalLimit, int? companyLimit, bool allowed)
    {
        await using var fixture = await InboxFixture.CreateAsync(UserRole.CompanyAdmin).ConfigureAwait(false);
        await fixture.AddExistingInboxAsync().ConfigureAwait(false);
        var global = await fixture.Database.GlobalLimits.SingleAsync().ConfigureAwait(false);
        global.DefaultMaxInboxesPerCompany = domainLimit ? 0 : globalLimit;
        global.DefaultMaxInboxesPerDomain = domainLimit ? globalLimit : 0;
        fixture.Database.CompanyLimits.Add(new CompanyLimitsDB
        {
            Id = Guid.CreateVersion7(),
            CompanyId = fixture.Address.CompanyId,
            MaxInboxes = domainLimit ? null : companyLimit,
            MaxInboxesPerDomain = domainLimit ? companyLimit : null,
        });
        await fixture.Database.SaveChangesAsync().ConfigureAwait(false);
        var result = await fixture.Service.CreateInboxAsync(fixture.User.Id,
            new CreateInboxRequestDTO("second", fixture.Address.Id)).ConfigureAwait(false);
        Assert.AreEqual(allowed, result is not null);
        Assert.AreEqual(allowed ? 2 : 1, await fixture.Database.Inboxes.CountAsync().ConfigureAwait(false));
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, true)]
    public async Task CompanyCapacityCountsOtherDomainsButDomainCapacityDoesNot(bool domainLimit, bool allowed)
    {
        await using var fixture = await InboxFixture.CreateAsync(UserRole.CompanyAdmin).ConfigureAwait(false);
        var existing = await fixture.AddExistingInboxAsync().ConfigureAwait(false);
        var otherAddress = new AddressDB
        {
            Id = Guid.CreateVersion7(),
            Domain = "other.example.test",
            CompanyId = fixture.Address.CompanyId,
        };
        fixture.Database.Addresses.Add(otherAddress);
        existing.Address = otherAddress;
        var global = await fixture.Database.GlobalLimits.SingleAsync().ConfigureAwait(false);
        global.DefaultMaxInboxesPerCompany = domainLimit ? 0 : 1;
        global.DefaultMaxInboxesPerDomain = domainLimit ? 1 : 0;
        await fixture.Database.SaveChangesAsync().ConfigureAwait(false);
        var result = await fixture.Service.CreateInboxAsync(fixture.User.Id,
            new CreateInboxRequestDTO("second", fixture.Address.Id)).ConfigureAwait(false);
        Assert.AreEqual(allowed, result is not null);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task MissingUserOrAddressCannotCreateAnInbox(bool missingUser)
    {
        await using var fixture = await InboxFixture.CreateAsync(UserRole.SuperAdmin).ConfigureAwait(false);
        Assert.IsNull(await fixture.Service.CreateInboxAsync(missingUser ? Guid.NewGuid() : fixture.User.Id,
            new CreateInboxRequestDTO("new", missingUser ? fixture.Address.Id : Guid.NewGuid())).ConfigureAwait(false));
        Assert.AreEqual(0, await fixture.Database.Inboxes.CountAsync().ConfigureAwait(false));
        Assert.AreEqual(0, await fixture.Database.Folders.CountAsync().ConfigureAwait(false));
    }

    private sealed class InboxFixture(EmailDbContext database, UserDB user, AddressDB address) : IAsyncDisposable
    {
        public EmailDbContext Database { get; } = database;
        public UserDB User { get; } = user;
        public AddressDB Address { get; } = address;
        public InboxService Service { get; } = new(database);

        public static async Task<InboxFixture> CreateAsync(UserRole role, bool sameCompany = true)
        {
            var database = new EmailDbContext(new DbContextOptionsBuilder<EmailDbContext>()
                .UseInMemoryDatabase($"inbox-policy-{Guid.NewGuid():N}").Options);
            await database.Database.EnsureCreatedAsync().ConfigureAwait(false);
            var company = new CompanyDB { Id = Guid.CreateVersion7(), Name = "Inbox policy" };
            var user = new UserDB
            {
                Id = Guid.CreateVersion7(),
                Username = "owner",
                PasswordHash = "unused",
                Role = role.ToString(),
                Company = company,
            };
            var address = new AddressDB
            {
                Id = Guid.CreateVersion7(),
                Domain = "mail.example.test",
                IsActive = true,
                Company = sameCompany ? company : new CompanyDB { Id = Guid.CreateVersion7(), Name = "Other company" },
            };
            database.Users.Add(user);
            database.Addresses.Add(address);
            await database.SaveChangesAsync().ConfigureAwait(false);
            return new InboxFixture(database, user, address);
        }

        public async Task<InboxDB> AddExistingInboxAsync()
        {
            var inbox = new InboxDB
            {
                Id = Guid.CreateVersion7(),
                Name = "existing",
                Address = Address,
                Owner = User,
            };
            Database.Inboxes.Add(inbox);
            await Database.SaveChangesAsync().ConfigureAwait(false);
            return inbox;
        }

        public ValueTask DisposeAsync() => Database.DisposeAsync();
    }
}
