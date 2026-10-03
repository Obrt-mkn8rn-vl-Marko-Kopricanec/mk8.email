using Microsoft.EntityFrameworkCore;
using mk8.email.Application.Services;
using mk8.email.Infrastructure.Data;
using mk8.email.Infrastructure.Models;

namespace mk8.email.Application.Tests;

[TestClass]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1812", Justification = "MSTest DiscoverInternals instantiates this test class by reflection; focused discovery is verified by executed test counts.")]
internal sealed class SenderAuthorizationTests : IDisposable
{
    private const string TestUsername = "user@mk8n.com";
    private EmailDbContext _database = null!;
    private SenderAuthorizationService _service = null!;
    private AddressDB _address = null!;
    private CompanyDB _company = null!;

    [TestInitialize]
    public void Initialize()
    {
        var options = new DbContextOptionsBuilder<EmailDbContext>()
            .UseInMemoryDatabase($"sender-authorization-{Guid.NewGuid():N}")
            .Options;
        _database = new EmailDbContext(options);

        _company = new CompanyDB
        {
            Id = Guid.CreateVersion7(),
            Name = "Test Company",
            IsActive = true,
        };
        _address = new AddressDB
        {
            Id = Guid.CreateVersion7(),
            Domain = "mk8n.com",
            IsActive = true,
            Company = _company,
        };
        var user = new UserDB
        {
            Id = Guid.CreateVersion7(),
            Username = TestUsername,
            PasswordHash = "unused",
            IsActive = true,
            Company = _company,
        };
        var inbox = new InboxDB
        {
            Id = Guid.CreateVersion7(),
            Name = "user",
            Address = _address,
            Owner = user,
        };

        _database.Inboxes.Add(inbox);
        _database.SaveChanges();
        _service = new SenderAuthorizationService(_database);
    }

    [TestCleanup]
    public void Cleanup() => Dispose();

    public void Dispose() => _database?.Dispose();

    [TestMethod]
    public async Task ActiveOwnedAddressIsAuthorizedWithoutCaseSensitivity()
    {
        Assert.IsTrue(await _service.CanSendAsAsync(TestUsername, "User@MK8N.COM").ConfigureAwait(false));
    }

    [TestMethod]
    public async Task UnownedOrInactiveAddressIsRejected()
    {
        Assert.IsFalse(await _service.CanSendAsAsync(TestUsername, "other@mk8n.com").ConfigureAwait(false));

        _address.IsActive = false;
        await _database.SaveChangesAsync().ConfigureAwait(false);
        Assert.IsFalse(await _service.CanSendAsAsync(TestUsername, TestUsername).ConfigureAwait(false));

        _address.IsActive = true;
        _company.IsActive = false;
        await _database.SaveChangesAsync().ConfigureAwait(false);
        Assert.IsFalse(await _service.CanSendAsAsync(TestUsername, TestUsername).ConfigureAwait(false));
    }

    [TestMethod]
    public void MatchingFromAndSenderHeadersAreAuthorized()
    {
        const string message =
            "From: Display Name <user@mk8n.com>\r\n" +
            "Sender: user@mk8n.com\r\n" +
            "Subject: authorized\r\n\r\nbody\r\n";

        Assert.IsTrue(_service.HasMatchingFromAddress(message, TestUsername));
    }

    [TestMethod]
    public void MissingMultipleOrMismatchedAuthorHeadersAreRejected()
    {
        Assert.IsFalse(_service.HasMatchingFromAddress("Subject: missing\r\n\r\nbody", TestUsername));
        Assert.IsFalse(_service.HasMatchingFromAddress(
            "From: user@mk8n.com\r\nFrom: other@mk8n.com\r\n\r\nbody",
            TestUsername));
        Assert.IsFalse(_service.HasMatchingFromAddress(
            "From: other@mk8n.com\r\n\r\nbody",
            TestUsername));
        Assert.IsFalse(_service.HasMatchingFromAddress(
            "From: user@mk8n.com\r\nSender: other@mk8n.com\r\n\r\nbody",
            TestUsername));
    }
}
