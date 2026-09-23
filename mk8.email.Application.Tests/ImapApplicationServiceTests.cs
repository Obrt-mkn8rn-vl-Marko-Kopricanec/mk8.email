using Microsoft.EntityFrameworkCore;
using mk8.email.Application.Services;
using mk8.email.Contracts.Imap;
using mk8.email.Infrastructure.Data;
using mk8.email.Infrastructure.Models;

namespace mk8.email.Application.Tests;

[TestClass]
public sealed class ImapApplicationServiceTests
{
    [TestMethod]
    public async Task MailboxListingIsAccountScopedAndPreservesPrimaryAndSubscriptionFlags()
    {
        var options = new DbContextOptionsBuilder<EmailDbContext>()
            .UseInMemoryDatabase($"imap-mailboxes-{Guid.NewGuid():N}")
            .Options;
        await using var database = new EmailDbContext(options);
        await database.Database.EnsureCreatedAsync();
        var company = new CompanyDB
        {
            Id = Guid.CreateVersion7(),
            Name = "IMAP test company",
            IsActive = true,
        };
        var address = new AddressDB
        {
            Id = Guid.CreateVersion7(),
            Domain = "example.test",
            IsActive = true,
            Company = company,
        };
        var owner = new UserDB
        {
            Id = Guid.CreateVersion7(),
            Username = "owner@example.test",
            PasswordHash = "unused",
            Role = "User",
            IsActive = true,
            Company = company,
        };
        var other = new UserDB
        {
            Id = Guid.CreateVersion7(),
            Username = "other@example.test",
            PasswordHash = "unused",
            Role = "User",
            IsActive = true,
            Company = company,
        };
        var primary = CreateInbox(owner, address, "owner");
        AddFolder(database, primary, "INBOX", subscribed: true);
        AddFolder(database, primary, "Archive", subscribed: false);
        AddFolder(database, CreateInbox(owner, address, "alias"), "INBOX", subscribed: true);
        AddFolder(database, CreateInbox(other, address, "other"), "INBOX", subscribed: true);
        await database.SaveChangesAsync();

        var service = new ImapApplicationService(null!, null!, database);
        var all = await service.ListMailboxesAsync(
            new ImapMailboxListRequest(owner.Id, SubscribedOnly: false));
        Assert.HasCount(3, all.Mailboxes);
        Assert.IsTrue(all.Mailboxes.Any(mailbox =>
            mailbox.InboxName == "owner" && mailbox.FolderName == "INBOX" && mailbox.IsPrimary));
        Assert.IsTrue(all.Mailboxes.Any(mailbox =>
            mailbox.InboxName == "owner" && mailbox.FolderName == "Archive" && !mailbox.IsSubscribed));
        Assert.IsTrue(all.Mailboxes.Any(mailbox =>
            mailbox.InboxName == "alias" && mailbox.FolderName == "INBOX" && !mailbox.IsPrimary));
        Assert.IsFalse(all.Mailboxes.Any(mailbox => mailbox.InboxName == "other"));

        var subscribed = await service.ListMailboxesAsync(
            new ImapMailboxListRequest(owner.Id, SubscribedOnly: true));
        Assert.HasCount(2, subscribed.Mailboxes);
        Assert.IsTrue(subscribed.Mailboxes.All(mailbox => mailbox.IsSubscribed));
        await Assert.ThrowsAsync<ArgumentException>(() => service.ListMailboxesAsync(
            new ImapMailboxListRequest(Guid.Empty, SubscribedOnly: false)));
    }

    private static InboxDB CreateInbox(
        UserDB owner,
        AddressDB address,
        string inboxName) => new()
        {
            Id = Guid.CreateVersion7(),
            Name = inboxName,
            Address = address,
            Owner = owner,
        };

    private static void AddFolder(
        EmailDbContext database,
        InboxDB inbox,
        string folderName,
        bool subscribed)
    {
        database.Folders.Add(new FolderDB
        {
            Id = Guid.CreateVersion7(),
            Name = folderName,
            Inbox = inbox,
            IsSubscribed = subscribed,
        });
    }
}
