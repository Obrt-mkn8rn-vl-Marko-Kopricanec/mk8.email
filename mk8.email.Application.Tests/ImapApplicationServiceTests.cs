using Microsoft.EntityFrameworkCore;
using mk8.email.Application.Services;
using mk8.email.Contracts.Enums;
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
        var primaryFolder = AddFolder(database, primary, DefaultFolders.Inbox, subscribed: true);
        AddFolder(database, primary, "Archive", subscribed: false);
        AddFolder(database, CreateInbox(owner, address, "alias"), DefaultFolders.Inbox, subscribed: true);
        AddFolder(database, CreateInbox(other, address, "other"), DefaultFolders.Inbox, subscribed: true);
        AddMessage(database, primaryFolder, uid: 1, sizeBytes: 10, isRead: false);
        AddMessage(database, primaryFolder, uid: 2, sizeBytes: 20, isRead: true);
        await database.SaveChangesAsync();

        var service = new ImapApplicationService(null!, null!, database);
        var all = await service.ListMailboxesAsync(
            new ImapMailboxListRequest(owner.Id, SubscribedOnly: false));
        Assert.HasCount(3, all.Mailboxes);
        Assert.IsTrue(all.Mailboxes.Any(mailbox =>
            mailbox.InboxName == "owner" && mailbox.FolderName == DefaultFolders.Inbox && mailbox.IsPrimary));
        Assert.IsTrue(all.Mailboxes.Any(mailbox =>
            mailbox.InboxName == "owner" && mailbox.FolderName == "Archive" && !mailbox.IsSubscribed));
        Assert.IsTrue(all.Mailboxes.Any(mailbox =>
            mailbox.InboxName == "alias" && mailbox.FolderName == DefaultFolders.Inbox && !mailbox.IsPrimary));
        Assert.IsFalse(all.Mailboxes.Any(mailbox => mailbox.InboxName == "other"));

        var subscribed = await service.ListMailboxesAsync(
            new ImapMailboxListRequest(owner.Id, SubscribedOnly: true));
        Assert.HasCount(2, subscribed.Mailboxes);
        Assert.IsTrue(subscribed.Mailboxes.All(mailbox => mailbox.IsSubscribed));

        var aliasLocation = await ImapMailboxResolver.ResolveLocationAsync(
            database, owner.Id, "alias/example.test/Inbox", CancellationToken.None);
        Assert.IsNotNull(aliasLocation);
        var aliasFolder = await ImapMailboxResolver.ResolveFolderAsync(
            database, owner.Id, "alias/example.test/Inbox", CancellationToken.None);
        Assert.IsNotNull(aliasFolder);

        var statuses = await service.GetMailboxStatusesAsync(new ImapMailboxStatusRequest(
            owner.Id,
            ["INBOX", "alias/example.test/Inbox", "other/example.test/Inbox", "Missing"],
            IncludeMessageCount: true,
            IncludeUnseenCount: true,
            IncludeSize: true));
        Assert.IsTrue(statuses.Statuses.ContainsKey("INBOX"), string.Join(',', statuses.Statuses.Keys));
        Assert.IsTrue(
            statuses.Statuses.ContainsKey("alias/example.test/Inbox"),
            string.Join(',', statuses.Statuses.Keys));
        Assert.HasCount(2, statuses.Statuses);
        var inboxStatus = statuses.Statuses["INBOX"];
        Assert.AreEqual(primaryFolder.Id, inboxStatus.FolderId);
        Assert.AreEqual(primaryFolder.MailboxId, inboxStatus.MailboxId);
        Assert.AreEqual(2, inboxStatus.MessageCount);
        Assert.AreEqual(1, inboxStatus.UnseenCount);
        Assert.AreEqual(30L, inboxStatus.SizeBytes);
        Assert.AreEqual(0, statuses.Statuses["alias/example.test/Inbox"].MessageCount);

        var sizeOnly = await service.GetMailboxStatusesAsync(new ImapMailboxStatusRequest(
            owner.Id, ["INBOX"], false, false, true));
        Assert.IsNull(sizeOnly.Statuses["INBOX"].MessageCount);
        Assert.IsNull(sizeOnly.Statuses["INBOX"].UnseenCount);
        Assert.AreEqual(30L, sizeOnly.Statuses["INBOX"].SizeBytes);
        Assert.IsTrue((await service.SetMailboxSubscriptionAsync(
            new ImapMailboxSubscriptionRequest(owner.Id, "INBOX", false))).Found);
        Assert.IsFalse(primaryFolder.IsSubscribed);
        var afterUnsubscribe = await service.ListMailboxesAsync(
            new ImapMailboxListRequest(owner.Id, SubscribedOnly: true));
        Assert.HasCount(1, afterUnsubscribe.Mailboxes);
        Assert.AreEqual("alias", afterUnsubscribe.Mailboxes[0].InboxName);
        Assert.IsTrue((await service.SetMailboxSubscriptionAsync(
            new ImapMailboxSubscriptionRequest(owner.Id, "INBOX", true))).Found);
        Assert.IsTrue((await service.SetMailboxSubscriptionAsync(
            new ImapMailboxSubscriptionRequest(owner.Id, "INBOX", true))).Found);
        Assert.IsTrue(primaryFolder.IsSubscribed);
        Assert.IsFalse((await service.SetMailboxSubscriptionAsync(
            new ImapMailboxSubscriptionRequest(owner.Id, "other/example.test/Inbox", false))).Found);
        Assert.IsFalse((await service.SetMailboxSubscriptionAsync(
            new ImapMailboxSubscriptionRequest(owner.Id, "Missing", false))).Found);
        await Assert.ThrowsAsync<ArgumentException>(() => service.ListMailboxesAsync(
            new ImapMailboxListRequest(Guid.Empty, SubscribedOnly: false)));
        await Assert.ThrowsAsync<ArgumentException>(() => service.GetMailboxStatusesAsync(
            new ImapMailboxStatusRequest(Guid.Empty, ["INBOX"], true, true, true)));
        await Assert.ThrowsAsync<ArgumentException>(() => service.SetMailboxSubscriptionAsync(
            new ImapMailboxSubscriptionRequest(Guid.Empty, "INBOX", true)));
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

    private static FolderDB AddFolder(
        EmailDbContext database,
        InboxDB inbox,
        string folderName,
        bool subscribed)
    {
        var folder = new FolderDB
        {
            Id = Guid.CreateVersion7(),
            Name = folderName,
            Inbox = inbox,
            IsSubscribed = subscribed,
        };
        database.Folders.Add(folder);
        return folder;
    }

    private static void AddMessage(
        EmailDbContext database,
        FolderDB folder,
        int uid,
        int sizeBytes,
        bool isRead)
    {
        database.Emails.Add(new EmailDB
        {
            Id = Guid.CreateVersion7(),
            Folder = folder,
            Uid = uid,
            Sender = "sender@example.test",
            Recipient = "owner@example.test",
            Subject = "IMAP status test",
            Body = string.Empty,
            SizeBytes = sizeBytes,
            IsRead = isRead,
        });
    }
}
