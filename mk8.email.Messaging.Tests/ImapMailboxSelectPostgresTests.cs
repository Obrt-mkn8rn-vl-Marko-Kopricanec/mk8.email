using Microsoft.EntityFrameworkCore;
using mk8.email.Application.Services;
using mk8.email.Contracts.Imap;
using mk8.email.Infrastructure.Data;
using mk8.email.Infrastructure.Models;

namespace mk8.email.Messaging.Tests;

[TestClass]
[TestCategory("PostgreSQL")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1812", Justification = "MSTest DiscoverInternals instantiates this test class by reflection; focused discovery is verified by executed test counts.")]
internal sealed class ImapMailboxSelectPostgresTests
{
    [TestMethod]
    [Timeout(20_000)]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "MA0051", Justification = "The SelectionReturnsOwnedMetadataAndQresyncChangesWithoutOtherAccountMail scenario keeps one fixture's ordered setup, operation and invariant assertions together.")]
    public async Task SelectionReturnsOwnedMetadataAndQresyncChangesWithoutOtherAccountMail()
    {
        var server = (await PostgresTestDatabase.TryCreateAsync().ConfigureAwait(false));
        await using var serverLifetime = new NullableAsyncDisposable(server).ConfigureAwait(false);
        if (server is null)
        {
            Assert.Inconclusive("Set MK8_EMAIL_TEST_POSTGRES to a PostgreSQL admin connection string.");
            return;
        }

        var options = new DbContextOptionsBuilder<EmailDbContext>()
            .UseNpgsql(server.ConnectionString)
            .Options;
        var ownerId = Guid.CreateVersion7();
        var inboxId = Guid.CreateVersion7();
        var foreignInboxId = Guid.CreateVersion7();
        {
            var database = new EmailDbContext(options);
            await using var databaseLifetime = database.ConfigureAwait(false);
            await database.Database.EnsureCreatedAsync().ConfigureAwait(false);
            var company = new CompanyDB
            {
                Id = Guid.CreateVersion7(),
                Name = "IMAP selection test",
                IsActive = true,
            };
            var address = new AddressDB
            {
                Id = Guid.CreateVersion7(),
                Domain = "example.test",
                IsActive = true,
                Company = company,
            };
            var owner = NewUser(ownerId, "owner@example.test", company);
            owner.QuotaBytes = 4096;
            var other = NewUser(Guid.CreateVersion7(), "other@example.test", company);
            other.QuotaBytes = 8192;
            var primary = NewInbox(owner, address, "owner");
            var foreign = NewInbox(other, address, "other");
            var inbox = new FolderDB
            {
                Id = inboxId,
                Name = "Inbox",
                Inbox = primary,
                UidValidity = 23,
                NextUid = 3,
                HighestModSeq = 8,
            };
            var foreignInbox = new FolderDB
            {
                Id = foreignInboxId,
                Name = "Inbox",
                Inbox = foreign,
            };
            await (database.Emails.AddRangeAsync(
                NewMessage(inbox, 1, 5, 10, isRead: false, keywords: ["$Label1"]),
                NewMessage(inbox, 2, 6, 20, isRead: true, keywords: ["$Label2"]),
                NewMessage(foreignInbox, 1, 9, 99, isRead: false, keywords: ["Foreign"]))).ConfigureAwait(false);
            await (database.ExpungedUids.AddAsync(new ExpungedUidDB
            {
                Id = Guid.CreateVersion7(),
                Folder = inbox,
                Uid = 77,
                ModSeq = 7,
            })).ConfigureAwait(false);
            await database.SaveChangesAsync().ConfigureAwait(false);
        }

        {
            var database = new EmailDbContext(options);
            await using var databaseLifetime = database.ConfigureAwait(false);
            var application = new ImapApplicationService(
                null!, null!, database, null!, null!, null!);
            var selected = (await application.SelectMailboxAsync(
                new ImapMailboxSelectRequest(ownerId, "INBOX", 23, 4)).ConfigureAwait(false)).Mailbox;
            Assert.IsNotNull(selected);
            Assert.AreEqual(2, selected.MessageCount);
            Assert.AreEqual(1, selected.FirstUnseenSequence);
            Assert.AreEqual(3, selected.NextUid);
            CollectionAssert.AreEquivalent(ExpectedVector1, selected.Keywords);
            CollectionAssert.AreEqual(ExpectedVector2, selected.VanishedUids);
            Assert.HasCount(2, selected.ChangedMessages);
            Assert.AreEqual(1, selected.ChangedMessages[0].Sequence);
            Assert.AreEqual(2, selected.ChangedMessages[1].Sequence);
            Assert.IsFalse(selected.ChangedMessages[0].IsRead);
            Assert.IsTrue(selected.ChangedMessages[1].IsRead);
            var sinceFirstMessage = (await application.SelectMailboxAsync(
                new ImapMailboxSelectRequest(ownerId, "INBOX", 23, 5)).ConfigureAwait(false)).Mailbox;
            Assert.IsNotNull(sinceFirstMessage);
            Assert.HasCount(1, sinceFirstMessage.ChangedMessages);
            Assert.AreEqual(2, sinceFirstMessage.ChangedMessages[0].Uid);
            Assert.AreEqual(2, sinceFirstMessage.ChangedMessages[0].Sequence);
            CollectionAssert.AreEqual(ExpectedVector2, sinceFirstMessage.VanishedUids);
            var sinceExpunge = (await application.SelectMailboxAsync(
                new ImapMailboxSelectRequest(ownerId, "INBOX", 23, 7)).ConfigureAwait(false)).Mailbox;
            Assert.IsNotNull(sinceExpunge);
            Assert.IsEmpty(sinceExpunge.VanishedUids);
            Assert.IsEmpty(sinceExpunge.ChangedMessages);
            Assert.IsNull((await application.SelectMailboxAsync(
                new ImapMailboxSelectRequest(ownerId,
                    "other/example.test/Inbox", null, null)).ConfigureAwait(false)).Mailbox);
            var mismatched = (await application.SelectMailboxAsync(
                new ImapMailboxSelectRequest(ownerId, "INBOX", 24, 4)).ConfigureAwait(false)).Mailbox;
            Assert.IsNotNull(mismatched);
            Assert.IsEmpty(mismatched.VanishedUids);
            Assert.IsEmpty(mismatched.ChangedMessages);
            var quota = await application.GetQuotaAsync(new ImapQuotaRequest(ownerId, "INBOX")).ConfigureAwait(false);
            Assert.IsTrue(quota.MailboxFound);
            Assert.AreEqual(30L, quota.UsedBytes);
            Assert.AreEqual(4096L, quota.LimitBytes);
            Assert.IsFalse((await application.GetQuotaAsync(new ImapQuotaRequest(
                ownerId, "other/example.test/Inbox")).ConfigureAwait(false)).MailboxFound);
            var idle = await application.GetIdleSnapshotAsync(
                new ImapIdleSnapshotRequest(ownerId, inboxId)).ConfigureAwait(false);
            Assert.IsTrue(idle.FolderFound);
            Assert.AreEqual(8L, idle.HighestModSeq);
            Assert.HasCount(2, idle.Messages);
            Assert.AreEqual(1, idle.Messages[0].Uid);
            Assert.AreEqual(2, idle.Messages[1].Uid);
            Assert.IsFalse((await application.GetIdleSnapshotAsync(
                new ImapIdleSnapshotRequest(ownerId, foreignInboxId)).ConfigureAwait(false)).FolderFound);
        }
    }

    private static UserDB NewUser(Guid id, string username, CompanyDB company) => new()
    {
        Id = id,
        Username = username,
        PasswordHash = "unused",
        Role = "User",
        IsActive = true,
        Company = company,
    };

    private static InboxDB NewInbox(UserDB owner, AddressDB address, string name) => new()
    {
        Id = Guid.CreateVersion7(),
        Name = name,
        Owner = owner,
        Address = address,
    };

    private static EmailDB NewMessage(
        FolderDB folder,
        int uid,
        long modSeq,
        int sizeBytes,
        bool isRead,
        string[] keywords) => new()
        {
            Id = Guid.CreateVersion7(),
            Folder = folder,
            Uid = uid,
            ModSeq = modSeq,
            SizeBytes = sizeBytes,
            IsRead = isRead,
            Keywords = keywords,
            Sender = "sender@example.test",
            Recipient = "owner@example.test",
            Subject = "IMAP selection",
        };
    private static readonly string[] ExpectedVector1 = new[] { "$Label1", "$Label2" };
    private static readonly int[] ExpectedVector2 = new[] { 77 };
}
