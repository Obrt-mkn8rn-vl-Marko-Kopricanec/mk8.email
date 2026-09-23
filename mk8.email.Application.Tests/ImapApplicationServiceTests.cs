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
            QuotaBytes = 4096,
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
        primaryFolder.HighestModSeq = 4;
        AddFolder(database, primary, "Archive", subscribed: false);
        var alias = CreateInbox(owner, address, "alias");
        AddFolder(database, alias, DefaultFolders.Inbox, subscribed: true);
        var otherFolder = AddFolder(
            database, CreateInbox(other, address, "other"), DefaultFolders.Inbox, subscribed: true);
        AddMessage(database, primaryFolder, uid: 1, sizeBytes: 10, isRead: false,
            modSeq: 2, keywords: ["$Label1"]);
        AddMessage(database, primaryFolder, uid: 2, sizeBytes: 20, isRead: true,
            modSeq: 3, keywords: ["$Label2"]);
        database.ExpungedUids.Add(new ExpungedUidDB
        {
            Id = Guid.CreateVersion7(),
            Folder = primaryFolder,
            Uid = 77,
            ModSeq = 4,
        });
        await database.SaveChangesAsync();

        var service = new ImapApplicationService(null!, null!, database, null!, null!, null!);
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
        var quota = await service.GetQuotaAsync(new ImapQuotaRequest(owner.Id, "INBOX"));
        Assert.IsTrue(quota.MailboxFound);
        Assert.AreEqual(30L, quota.UsedBytes);
        Assert.AreEqual(4096L, quota.LimitBytes);
        Assert.IsFalse((await service.GetQuotaAsync(new ImapQuotaRequest(
            owner.Id, "other/example.test/Inbox"))).MailboxFound);
        Assert.IsTrue((await service.GetQuotaAsync(
            new ImapQuotaRequest(owner.Id, null))).MailboxFound);
        var idle = await service.GetIdleSnapshotAsync(
            new ImapIdleSnapshotRequest(owner.Id, primaryFolder.Id));
        Assert.IsTrue(idle.FolderFound);
        Assert.AreEqual(4L, idle.HighestModSeq);
        Assert.HasCount(2, idle.Messages);
        Assert.AreEqual(1, idle.Messages[0].Uid);
        Assert.AreEqual(2, idle.Messages[1].Uid);
        Assert.IsFalse((await service.GetIdleSnapshotAsync(
            new ImapIdleSnapshotRequest(owner.Id, otherFolder.Id))).FolderFound);
        var selected = (await service.SelectMailboxAsync(
            new ImapMailboxSelectRequest(owner.Id, "INBOX", primaryFolder.UidValidity, 1))).Mailbox;
        Assert.IsNotNull(selected);
        Assert.AreEqual(primaryFolder.Id, selected.FolderId);
        Assert.AreEqual(2, selected.MessageCount);
        Assert.AreEqual(1, selected.FirstUnseenSequence);
        CollectionAssert.AreEquivalent(new[] { "$Label1", "$Label2" }, selected.Keywords);
        CollectionAssert.AreEqual(new[] { 77 }, selected.VanishedUids);
        Assert.HasCount(2, selected.ChangedMessages);
        Assert.AreEqual(1, selected.ChangedMessages[0].Sequence);
        Assert.AreEqual(2, selected.ChangedMessages[1].Sequence);
        Assert.IsFalse(selected.ChangedMessages[0].IsRead);
        Assert.IsTrue(selected.ChangedMessages[1].IsRead);
        var staleSelection = (await service.SelectMailboxAsync(
            new ImapMailboxSelectRequest(owner.Id, "INBOX", primaryFolder.UidValidity + 1, 1))).Mailbox;
        Assert.IsNotNull(staleSelection);
        Assert.IsEmpty(staleSelection.VanishedUids);
        Assert.IsEmpty(staleSelection.ChangedMessages);
        Assert.IsNull((await service.SelectMailboxAsync(
            new ImapMailboxSelectRequest(owner.Id, "other/example.test/Inbox", null, null))).Mailbox);
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
        var created = await service.CreateMailboxAsync(
            new ImapMailboxCreateRequest(owner.Id, "alias/example.test/Projects"));
        Assert.AreEqual(ImapMailboxCreateDisposition.Created, created.Disposition);
        Assert.AreNotEqual(Guid.Empty, created.FolderId);
        Assert.IsFalse(string.IsNullOrEmpty(created.MailboxId));
        Assert.AreEqual(alias.Id, (await database.Folders.SingleAsync(
            folder => folder.Id == created.FolderId)).InboxId);
        Assert.AreEqual(ImapMailboxCreateDisposition.AlreadyExists,
            (await service.CreateMailboxAsync(
                new ImapMailboxCreateRequest(owner.Id, "alias/example.test/Projects"))).Disposition);
        Assert.AreEqual(ImapMailboxCreateDisposition.InvalidName,
            (await service.CreateMailboxAsync(
                new ImapMailboxCreateRequest(owner.Id, "Invalid//Path"))).Disposition);
        Assert.AreEqual(ImapMailboxCreateDisposition.Created,
            (await service.CreateMailboxAsync(
                new ImapMailboxCreateRequest(owner.Id, "alias/example.test/Projects/2026"))).Disposition);
        Assert.AreEqual(ImapMailboxRenameDisposition.SystemFolder,
            (await service.RenameMailboxAsync(
                new ImapMailboxRenameRequest(owner.Id, "INBOX", "NewInbox"))).Disposition);
        Assert.AreEqual(ImapMailboxRenameDisposition.NotFound,
            (await service.RenameMailboxAsync(
                new ImapMailboxRenameRequest(owner.Id, "Missing", "New"))).Disposition);
        Assert.AreEqual(ImapMailboxRenameDisposition.InvalidDestination,
            (await service.RenameMailboxAsync(
                new ImapMailboxRenameRequest(owner.Id,
                    "alias/example.test/Projects", "Archive"))).Disposition);
        var tooDeep = string.Join('/', Enumerable.Repeat("n", FolderDB.MaximumHierarchyDepth));
        Assert.AreEqual(ImapMailboxRenameDisposition.InvalidDestination,
            (await service.RenameMailboxAsync(
                new ImapMailboxRenameRequest(owner.Id,
                    "alias/example.test/Projects", $"alias/example.test/{tooDeep}"))).Disposition);
        Assert.AreEqual(ImapMailboxRenameDisposition.Renamed,
            (await service.RenameMailboxAsync(
                new ImapMailboxRenameRequest(owner.Id,
                    "alias/example.test/Projects", "alias/example.test/Archive"))).Disposition);
        Assert.AreEqual("Archive", (await database.Folders.SingleAsync(
            folder => folder.Id == created.FolderId)).Name);
        Assert.IsTrue(await database.Folders.AnyAsync(
            folder => folder.InboxId == alias.Id && folder.Name == "Archive/2026"));
        Assert.AreEqual(ImapMailboxCreateDisposition.Created,
            (await service.CreateMailboxAsync(
                new ImapMailboxCreateRequest(owner.Id, "alias/example.test/Existing"))).Disposition);
        Assert.AreEqual(ImapMailboxRenameDisposition.AlreadyExists,
            (await service.RenameMailboxAsync(
                new ImapMailboxRenameRequest(owner.Id,
                    "alias/example.test/Archive", "alias/example.test/Existing"))).Disposition);
        Assert.AreEqual(ImapMailboxDeleteDisposition.SystemFolder,
            (await service.DeleteMailboxAsync(
                new ImapMailboxDeleteRequest(owner.Id, "INBOX"))).Disposition);
        Assert.AreEqual(ImapMailboxDeleteDisposition.NotFound,
            (await service.DeleteMailboxAsync(
                new ImapMailboxDeleteRequest(owner.Id, "Missing"))).Disposition);
        await Assert.ThrowsAsync<ArgumentException>(() => service.ListMailboxesAsync(
            new ImapMailboxListRequest(Guid.Empty, SubscribedOnly: false)));
        await Assert.ThrowsAsync<ArgumentException>(() => service.GetMailboxStatusesAsync(
            new ImapMailboxStatusRequest(Guid.Empty, ["INBOX"], true, true, true)));
        await Assert.ThrowsAsync<ArgumentException>(() => service.SetMailboxSubscriptionAsync(
            new ImapMailboxSubscriptionRequest(Guid.Empty, "INBOX", true)));
        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateMailboxAsync(
            new ImapMailboxCreateRequest(Guid.Empty, "Projects")));
        await Assert.ThrowsAsync<ArgumentException>(() => service.RenameMailboxAsync(
            new ImapMailboxRenameRequest(Guid.Empty, "Projects", "Archive")));
        await Assert.ThrowsAsync<ArgumentException>(() => service.DeleteMailboxAsync(
            new ImapMailboxDeleteRequest(Guid.Empty, "Archive")));
        await Assert.ThrowsAsync<ArgumentException>(() => service.SelectMailboxAsync(
            new ImapMailboxSelectRequest(Guid.Empty, "INBOX", null, null)));
        await Assert.ThrowsAsync<ArgumentException>(() => service.GetQuotaAsync(
            new ImapQuotaRequest(Guid.Empty, null)));
        await Assert.ThrowsAsync<ArgumentException>(() => service.GetIdleSnapshotAsync(
            new ImapIdleSnapshotRequest(Guid.Empty, primaryFolder.Id)));
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
        bool isRead,
        long modSeq,
        string[] keywords)
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
            ModSeq = modSeq,
            Keywords = keywords,
        });
    }
}
