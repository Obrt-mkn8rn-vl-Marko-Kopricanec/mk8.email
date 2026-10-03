using Microsoft.EntityFrameworkCore;
using mk8.email.Application.Services;
using mk8.email.Contracts.Enums;
using mk8.email.Contracts.Imap;
using mk8.email.Infrastructure.Data;
using mk8.email.Infrastructure.Models;

namespace mk8.email.Application.Tests;

[TestClass]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1812", Justification = "MSTest DiscoverInternals instantiates this test class by reflection; focused discovery is verified by executed test counts.")]
internal sealed class ImapApplicationServiceTests
{
    [TestMethod]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "MA0051", Justification = "The MailboxListingIsAccountScopedAndPreservesPrimaryAndSubscriptionFlags scenario keeps one fixture's ordered setup, operation and invariant assertions together.")]
    public async Task MailboxListingIsAccountScopedAndPreservesPrimaryAndSubscriptionFlags()
    {
        var options = new DbContextOptionsBuilder<EmailDbContext>()
            .UseInMemoryDatabase($"imap-mailboxes-{Guid.NewGuid():N}")
            .Options;
        var database = new EmailDbContext(options);
        await using var databaseLifetime = database.ConfigureAwait(false);
        await database.Database.EnsureCreatedAsync().ConfigureAwait(false);
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
        await (database.ExpungedUids.AddAsync(new ExpungedUidDB
        {
            Id = Guid.CreateVersion7(),
            Folder = primaryFolder,
            Uid = 77,
            ModSeq = 4,
        })).ConfigureAwait(false);
        await database.SaveChangesAsync().ConfigureAwait(false);

        var service = new ImapApplicationService(null!, null!, database, null!, null!, null!);
        var all = await service.ListMailboxesAsync(
            new ImapMailboxListRequest(owner.Id, SubscribedOnly: false)).ConfigureAwait(false);
        Assert.HasCount(3, all.Mailboxes);
        Assert.IsTrue(all.Mailboxes.Any(mailbox => string.Equals(mailbox.InboxName, "owner", StringComparison.Ordinal) && string.Equals(mailbox.FolderName, DefaultFolders.Inbox, StringComparison.Ordinal) && mailbox.IsPrimary));
        Assert.IsTrue(all.Mailboxes.Any(mailbox => string.Equals(mailbox.InboxName, "owner", StringComparison.Ordinal) && string.Equals(mailbox.FolderName, "Archive", StringComparison.Ordinal) && !mailbox.IsSubscribed));
        Assert.IsTrue(all.Mailboxes.Any(mailbox => string.Equals(mailbox.InboxName, "alias", StringComparison.Ordinal) && string.Equals(mailbox.FolderName, DefaultFolders.Inbox, StringComparison.Ordinal) && !mailbox.IsPrimary));
        Assert.IsFalse(all.Mailboxes.Any(mailbox => string.Equals(mailbox.InboxName, "other", StringComparison.Ordinal)));

        var subscribed = await service.ListMailboxesAsync(
            new ImapMailboxListRequest(owner.Id, SubscribedOnly: true)).ConfigureAwait(false);
        Assert.HasCount(2, subscribed.Mailboxes);
        Assert.IsTrue(subscribed.Mailboxes.All(mailbox => mailbox.IsSubscribed));

        var aliasLocation = await ImapMailboxResolver.ResolveLocationAsync(
            database, owner.Id, "alias/example.test/Inbox", CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(aliasLocation);
        var aliasFolder = await ImapMailboxResolver.ResolveFolderAsync(
            database, owner.Id, "alias/example.test/Inbox", CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(aliasFolder);

        var statuses = await service.GetMailboxStatusesAsync(new ImapMailboxStatusRequest(
            owner.Id,
            ["INBOX", "alias/example.test/Inbox", "other/example.test/Inbox", "Missing"],
            IncludeMessageCount: true,
            IncludeUnseenCount: true,
            IncludeSize: true)).ConfigureAwait(false);
        Assert.IsTrue(statuses.Statuses.ContainsKey("INBOX"), string.Join(',', statuses.Statuses.Keys));
        Assert.IsTrue(
            statuses.Statuses.ContainsKey("alias/example.test/Inbox"),
            string.Join(',', statuses.Statuses.Keys));
        Assert.HasCount(2, statuses.Statuses);
        var inboxStatus = statuses.Statuses["INBOX"];
        Assert.AreEqual(primaryFolder.Id, inboxStatus.FolderId);
        Assert.AreEqual(primaryFolder.MailboxId, inboxStatus.MailboxId, StringComparer.Ordinal);
        Assert.AreEqual(2, inboxStatus.MessageCount);
        Assert.AreEqual(1, inboxStatus.UnseenCount);
        Assert.AreEqual(30L, inboxStatus.SizeBytes);
        Assert.AreEqual(0, statuses.Statuses["alias/example.test/Inbox"].MessageCount);

        var sizeOnly = await service.GetMailboxStatusesAsync(new ImapMailboxStatusRequest(
            owner.Id, ["INBOX"], false, false, true)).ConfigureAwait(false);
        Assert.IsNull(sizeOnly.Statuses["INBOX"].MessageCount);
        Assert.IsNull(sizeOnly.Statuses["INBOX"].UnseenCount);
        Assert.AreEqual(30L, sizeOnly.Statuses["INBOX"].SizeBytes);
        var quota = await service.GetQuotaAsync(new ImapQuotaRequest(owner.Id, "INBOX")).ConfigureAwait(false);
        Assert.IsTrue(quota.MailboxFound);
        Assert.AreEqual(30L, quota.UsedBytes);
        Assert.AreEqual(4096L, quota.LimitBytes);
        Assert.IsFalse((await service.GetQuotaAsync(new ImapQuotaRequest(
            owner.Id, "other/example.test/Inbox")).ConfigureAwait(false)).MailboxFound);
        Assert.IsTrue((await service.GetQuotaAsync(
            new ImapQuotaRequest(owner.Id, null)).ConfigureAwait(false)).MailboxFound);
        var idle = await service.GetIdleSnapshotAsync(
            new ImapIdleSnapshotRequest(owner.Id, primaryFolder.Id)).ConfigureAwait(false);
        Assert.IsTrue(idle.FolderFound);
        Assert.AreEqual(4L, idle.HighestModSeq);
        Assert.HasCount(2, idle.Messages);
        Assert.AreEqual(1, idle.Messages[0].Uid);
        Assert.AreEqual(2, idle.Messages[1].Uid);
        Assert.IsFalse((await service.GetIdleSnapshotAsync(
            new ImapIdleSnapshotRequest(owner.Id, otherFolder.Id)).ConfigureAwait(false)).FolderFound);
        var selected = (await service.SelectMailboxAsync(
            new ImapMailboxSelectRequest(owner.Id, "INBOX", primaryFolder.UidValidity, 1)).ConfigureAwait(false)).Mailbox;
        Assert.IsNotNull(selected);
        Assert.AreEqual(primaryFolder.Id, selected.FolderId);
        Assert.AreEqual(2, selected.MessageCount);
        Assert.AreEqual(1, selected.FirstUnseenSequence);
        CollectionAssert.AreEquivalent(ExpectedVector1, selected.Keywords);
        CollectionAssert.AreEqual(ExpectedVector2, selected.VanishedUids);
        Assert.HasCount(2, selected.ChangedMessages);
        Assert.AreEqual(1, selected.ChangedMessages[0].Sequence);
        Assert.AreEqual(2, selected.ChangedMessages[1].Sequence);
        Assert.IsFalse(selected.ChangedMessages[0].IsRead);
        Assert.IsTrue(selected.ChangedMessages[1].IsRead);
        var staleSelection = (await service.SelectMailboxAsync(
            new ImapMailboxSelectRequest(owner.Id, "INBOX", primaryFolder.UidValidity + 1, 1)).ConfigureAwait(false)).Mailbox;
        Assert.IsNotNull(staleSelection);
        Assert.IsEmpty(staleSelection.VanishedUids);
        Assert.IsEmpty(staleSelection.ChangedMessages);
        Assert.IsNull((await service.SelectMailboxAsync(
            new ImapMailboxSelectRequest(owner.Id, "other/example.test/Inbox", null, null)).ConfigureAwait(false)).Mailbox);
        Assert.IsTrue((await service.SetMailboxSubscriptionAsync(
            new ImapMailboxSubscriptionRequest(owner.Id, "INBOX", false)).ConfigureAwait(false)).Found);
        Assert.IsFalse(primaryFolder.IsSubscribed);
        var afterUnsubscribe = await service.ListMailboxesAsync(
            new ImapMailboxListRequest(owner.Id, SubscribedOnly: true)).ConfigureAwait(false);
        Assert.HasCount(1, afterUnsubscribe.Mailboxes);
        Assert.AreEqual("alias", afterUnsubscribe.Mailboxes[0].InboxName, StringComparer.Ordinal);
        Assert.IsTrue((await service.SetMailboxSubscriptionAsync(
            new ImapMailboxSubscriptionRequest(owner.Id, "INBOX", true)).ConfigureAwait(false)).Found);
        Assert.IsTrue((await service.SetMailboxSubscriptionAsync(
            new ImapMailboxSubscriptionRequest(owner.Id, "INBOX", true)).ConfigureAwait(false)).Found);
        Assert.IsTrue(primaryFolder.IsSubscribed);
        Assert.IsFalse((await service.SetMailboxSubscriptionAsync(
            new ImapMailboxSubscriptionRequest(owner.Id, "other/example.test/Inbox", false)).ConfigureAwait(false)).Found);
        Assert.IsFalse((await service.SetMailboxSubscriptionAsync(
            new ImapMailboxSubscriptionRequest(owner.Id, "Missing", false)).ConfigureAwait(false)).Found);
        var created = await service.CreateMailboxAsync(
            new ImapMailboxCreateRequest(owner.Id, "alias/example.test/Projects")).ConfigureAwait(false);
        Assert.AreEqual(ImapMailboxCreateDisposition.Created, created.Disposition);
        Assert.AreNotEqual(Guid.Empty, created.FolderId);
        Assert.IsFalse(string.IsNullOrEmpty(created.MailboxId));
        Assert.AreEqual(alias.Id, (await database.Folders.SingleAsync(
            folder => folder.Id == created.FolderId).ConfigureAwait(false)).InboxId);
        Assert.AreEqual(ImapMailboxCreateDisposition.AlreadyExists,
            (await service.CreateMailboxAsync(
                new ImapMailboxCreateRequest(owner.Id, "alias/example.test/Projects")).ConfigureAwait(false)).Disposition);
        Assert.AreEqual(ImapMailboxCreateDisposition.InvalidName,
            (await service.CreateMailboxAsync(
                new ImapMailboxCreateRequest(owner.Id, "Invalid//Path")).ConfigureAwait(false)).Disposition);
        Assert.AreEqual(ImapMailboxCreateDisposition.Created,
            (await service.CreateMailboxAsync(
                new ImapMailboxCreateRequest(owner.Id, "alias/example.test/Projects/2026")).ConfigureAwait(false)).Disposition);
        Assert.AreEqual(ImapMailboxRenameDisposition.SystemFolder,
            (await service.RenameMailboxAsync(
                new ImapMailboxRenameRequest(owner.Id, "INBOX", "NewInbox")).ConfigureAwait(false)).Disposition);
        Assert.AreEqual(ImapMailboxRenameDisposition.NotFound,
            (await service.RenameMailboxAsync(
                new ImapMailboxRenameRequest(owner.Id, "Missing", "New")).ConfigureAwait(false)).Disposition);
        Assert.AreEqual(ImapMailboxRenameDisposition.InvalidDestination,
            (await service.RenameMailboxAsync(
                new ImapMailboxRenameRequest(owner.Id,
                    "alias/example.test/Projects", "Archive")).ConfigureAwait(false)).Disposition);
        var tooDeep = string.Join('/', Enumerable.Repeat("n", FolderDB.MaximumHierarchyDepth));
        Assert.AreEqual(ImapMailboxRenameDisposition.InvalidDestination,
            (await service.RenameMailboxAsync(
                new ImapMailboxRenameRequest(owner.Id,
                    "alias/example.test/Projects", $"alias/example.test/{tooDeep}")).ConfigureAwait(false)).Disposition);
        Assert.AreEqual(ImapMailboxRenameDisposition.Renamed,
            (await service.RenameMailboxAsync(
                new ImapMailboxRenameRequest(owner.Id,
                    "alias/example.test/Projects", "alias/example.test/Archive")).ConfigureAwait(false)).Disposition);
        Assert.AreEqual("Archive", (await database.Folders.SingleAsync(
            folder => folder.Id == created.FolderId).ConfigureAwait(false)).Name, StringComparer.Ordinal);
        Assert.IsTrue(await database.Folders.AnyAsync(
            folder => folder.InboxId == alias.Id && folder.Name == "Archive/2026").ConfigureAwait(false));
        Assert.AreEqual(ImapMailboxCreateDisposition.Created,
            (await service.CreateMailboxAsync(
                new ImapMailboxCreateRequest(owner.Id, "alias/example.test/Existing")).ConfigureAwait(false)).Disposition);
        Assert.AreEqual(ImapMailboxRenameDisposition.AlreadyExists,
            (await service.RenameMailboxAsync(
                new ImapMailboxRenameRequest(owner.Id,
                    "alias/example.test/Archive", "alias/example.test/Existing")).ConfigureAwait(false)).Disposition);
        Assert.AreEqual(ImapMailboxDeleteDisposition.SystemFolder,
            (await service.DeleteMailboxAsync(
                new ImapMailboxDeleteRequest(owner.Id, "INBOX")).ConfigureAwait(false)).Disposition);
        Assert.AreEqual(ImapMailboxDeleteDisposition.NotFound,
            (await service.DeleteMailboxAsync(
                new ImapMailboxDeleteRequest(owner.Id, "Missing")).ConfigureAwait(false)).Disposition);
        await Assert.ThrowsAsync<ArgumentException>(() => service.ListMailboxesAsync(
            new ImapMailboxListRequest(Guid.Empty, SubscribedOnly: false))).ConfigureAwait(false);
        await Assert.ThrowsAsync<ArgumentException>(() => service.GetMailboxStatusesAsync(
            new ImapMailboxStatusRequest(Guid.Empty, ["INBOX"], true, true, true))).ConfigureAwait(false);
        await Assert.ThrowsAsync<ArgumentException>(() => service.SetMailboxSubscriptionAsync(
            new ImapMailboxSubscriptionRequest(Guid.Empty, "INBOX", true))).ConfigureAwait(false);
        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateMailboxAsync(
            new ImapMailboxCreateRequest(Guid.Empty, "Projects"))).ConfigureAwait(false);
        await Assert.ThrowsAsync<ArgumentException>(() => service.RenameMailboxAsync(
            new ImapMailboxRenameRequest(Guid.Empty, "Projects", "Archive"))).ConfigureAwait(false);
        await Assert.ThrowsAsync<ArgumentException>(() => service.DeleteMailboxAsync(
            new ImapMailboxDeleteRequest(Guid.Empty, "Archive"))).ConfigureAwait(false);
        await Assert.ThrowsAsync<ArgumentException>(() => service.SelectMailboxAsync(
            new ImapMailboxSelectRequest(Guid.Empty, "INBOX", null, null))).ConfigureAwait(false);
        await Assert.ThrowsAsync<ArgumentException>(() => service.GetQuotaAsync(
            new ImapQuotaRequest(Guid.Empty, null))).ConfigureAwait(false);
        await Assert.ThrowsAsync<ArgumentException>(() => service.GetIdleSnapshotAsync(
            new ImapIdleSnapshotRequest(Guid.Empty, primaryFolder.Id))).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task MissingNestedRequestCollectionsIdentifyThePublicRequestParameter()
    {
        var service = new ImapApplicationService(null!, null!, null!, null!, null!, null!);
        var userId = Guid.CreateVersion7();
        var folderId = Guid.CreateVersion7();
        Func<Task>[] invalidRequests =
        [
            () => service.GetMailboxStatusesAsync(new ImapMailboxStatusRequest(
                userId, null!, false, false, false)),
            () => service.AppendMessagesAsync(new ImapAppendRequest(userId, "INBOX", false, null!)),
            () => service.SearchMessagesAsync(new ImapSearchRequest(userId, folderId, "ALL", null!, false)),
            () => service.SortMessagesAsync(new ImapSortRequest(
                userId, folderId, "ALL", null!, false, "US-ASCII", [])),
            () => service.SortMessagesAsync(new ImapSortRequest(
                userId, folderId, "ALL", [], false, "US-ASCII", null!)),
            () => service.ThreadMessagesAsync(new ImapThreadRequest(
                userId, folderId, "ALL", null!, false, "US-ASCII",
                ImapThreadAlgorithm.References, false)),
            () => service.MarkMessagesSeenAsync(new ImapMarkSeenRequest(userId, folderId, null!)),
        ];

        foreach (var invoke in invalidRequests)
        {
            var exception = await Assert.ThrowsExactlyAsync<ArgumentNullException>(invoke).ConfigureAwait(false);
            Assert.AreEqual("request", exception.ParamName, StringComparer.Ordinal);
        }
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
    private static readonly string[] ExpectedVector1 = new[] { "$Label1", "$Label2" };
    private static readonly int[] ExpectedVector2 = new[] { 77 };
}
