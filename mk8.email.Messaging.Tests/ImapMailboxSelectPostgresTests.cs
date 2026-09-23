using Microsoft.EntityFrameworkCore;
using mk8.email.Application.Services;
using mk8.email.Contracts.Imap;
using mk8.email.Infrastructure.Data;
using mk8.email.Infrastructure.Models;

namespace mk8.email.Messaging.Tests;

[TestClass]
[TestCategory("PostgreSQL")]
public sealed class ImapMailboxSelectPostgresTests
{
    [TestMethod]
    [Timeout(20_000)]
    public async Task SelectionReturnsOwnedMetadataAndQresyncChangesWithoutOtherAccountMail()
    {
        await using var server = await PostgresTestDatabase.TryCreateAsync();
        if (server is null)
        {
            Assert.Inconclusive("Set MK8_EMAIL_TEST_POSTGRES to a PostgreSQL admin connection string.");
            return;
        }

        var options = new DbContextOptionsBuilder<EmailDbContext>()
            .UseNpgsql(server.ConnectionString)
            .Options;
        var ownerId = Guid.CreateVersion7();
        await using (var database = new EmailDbContext(options))
        {
            await database.Database.EnsureCreatedAsync();
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
                Id = Guid.CreateVersion7(),
                Name = "Inbox",
                Inbox = primary,
                UidValidity = 23,
                NextUid = 3,
                HighestModSeq = 8,
            };
            var foreignInbox = new FolderDB
            {
                Id = Guid.CreateVersion7(),
                Name = "Inbox",
                Inbox = foreign,
            };
            database.Emails.AddRange(
                NewMessage(inbox, 1, 5, 10, isRead: false, keywords: ["$Label1"]),
                NewMessage(inbox, 2, 6, 20, isRead: true, keywords: ["$Label2"]),
                NewMessage(foreignInbox, 1, 9, 99, isRead: false, keywords: ["Foreign"]));
            database.ExpungedUids.Add(new ExpungedUidDB
            {
                Id = Guid.CreateVersion7(),
                Folder = inbox,
                Uid = 77,
                ModSeq = 7,
            });
            await database.SaveChangesAsync();
        }

        await using (var database = new EmailDbContext(options))
        {
            var application = new ImapApplicationService(
                null!, null!, database, null!, null!, null!);
            var selected = (await application.SelectMailboxAsync(
                new ImapMailboxSelectRequest(ownerId, "INBOX", 23, 4))).Mailbox;
            Assert.IsNotNull(selected);
            Assert.AreEqual(2, selected.MessageCount);
            Assert.AreEqual(1, selected.FirstUnseenSequence);
            Assert.AreEqual(3, selected.NextUid);
            CollectionAssert.AreEquivalent(new[] { "$Label1", "$Label2" }, selected.Keywords);
            CollectionAssert.AreEqual(new[] { 77 }, selected.VanishedUids);
            Assert.HasCount(2, selected.ChangedMessages);
            Assert.AreEqual(1, selected.ChangedMessages[0].Sequence);
            Assert.AreEqual(2, selected.ChangedMessages[1].Sequence);
            Assert.IsFalse(selected.ChangedMessages[0].IsRead);
            Assert.IsTrue(selected.ChangedMessages[1].IsRead);
            Assert.IsNull((await application.SelectMailboxAsync(
                new ImapMailboxSelectRequest(ownerId,
                    "other/example.test/Inbox", null, null))).Mailbox);
            var mismatched = (await application.SelectMailboxAsync(
                new ImapMailboxSelectRequest(ownerId, "INBOX", 24, 4))).Mailbox;
            Assert.IsNotNull(mismatched);
            Assert.IsEmpty(mismatched.VanishedUids);
            Assert.IsEmpty(mismatched.ChangedMessages);
            var quota = await application.GetQuotaAsync(new ImapQuotaRequest(ownerId, "INBOX"));
            Assert.IsTrue(quota.MailboxFound);
            Assert.AreEqual(30L, quota.UsedBytes);
            Assert.AreEqual(4096L, quota.LimitBytes);
            Assert.IsFalse((await application.GetQuotaAsync(new ImapQuotaRequest(
                ownerId, "other/example.test/Inbox"))).MailboxFound);
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
}
