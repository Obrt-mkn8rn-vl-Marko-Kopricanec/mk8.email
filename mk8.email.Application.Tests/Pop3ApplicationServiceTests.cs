using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using mk8.email.Application.Services;
using mk8.email.Contracts.Enums;
using mk8.email.Contracts.Pop3;
using mk8.email.Infrastructure.Data;
using mk8.email.Infrastructure.Models;
using mk8.email.MailWire;

namespace mk8.email.Application.Tests;

[TestClass]
public sealed class Pop3ApplicationServiceTests
{
    [TestMethod]
    public async Task MaildropSnapshotReadAndCommitKeepAccountBoundaries()
    {
        var options = new DbContextOptionsBuilder<EmailDbContext>()
            .UseInMemoryDatabase($"pop3-application-{Guid.NewGuid():N}")
            .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        await using var database = new EmailDbContext(options);
        await database.Database.EnsureCreatedAsync();
        var owner = SeedAccount(database, "owner", 1);
        var other = SeedAccount(database, "other", 2);
        var archive = new FolderDB
        {
            Id = Guid.CreateVersion7(),
            Name = "Archive",
            Inbox = owner.Folder.Inbox,
        };
        database.Folders.Add(archive);
        var raw = "From: sender@example.test\nTo: owner@example.test\n\n.body\n"u8.ToArray();
        var ownerMessage = AddMessage(database, owner.Folder, 10, raw);
        var otherMessage = AddMessage(database, other.Folder, 20, "Other message\n"u8.ToArray());
        var archiveMessage = AddMessage(database, archive, 30, "Archive message\n"u8.ToArray());
        await database.SaveChangesAsync();

        var store = new InMemoryLargeObjectStore();
        var effects = new LargeObjectTransactionEffects(
            store,
            NullLogger<LargeObjectTransactionEffects>.Instance);
        var service = new Pop3ApplicationService(
            null!,
            null!,
            database,
            new MailboxMessageContentService(store, effects),
            effects,
            NullLogger<Pop3ApplicationService>.Instance);

        var snapshot = await service.ListMaildropAsync(new Pop3UserRequest(owner.User.Id));
        Assert.HasCount(1, snapshot.Messages);
        Assert.AreEqual(ownerMessage.Id, snapshot.Messages[0].Id);
        Assert.AreEqual(10, snapshot.Messages[0].Uid);
        Assert.AreEqual(Pop3WireCodec.NormalizeCrlf(raw).Length, snapshot.Messages[0].SizeBytes);
        CollectionAssert.AreEqual(
            raw,
            (await service.GetMessageAsync(new Pop3MessageRequest(
                owner.User.Id, ownerMessage.Id))).RawMessage);
        Assert.IsNull((await service.GetMessageAsync(new Pop3MessageRequest(
            owner.User.Id, otherMessage.Id))).RawMessage);
        Assert.IsNull((await service.GetMessageAsync(new Pop3MessageRequest(
            owner.User.Id, archiveMessage.Id))).RawMessage);

        var unrelatedDelete = await service.CommitDeletesAsync(
            new Pop3DeleteRequest(owner.User.Id, [otherMessage.Id, archiveMessage.Id]));
        Assert.AreEqual(0, unrelatedDelete.DeletedCount);
        Assert.IsTrue(await database.Emails.AnyAsync(message => message.Id == otherMessage.Id));
        Assert.IsTrue(await database.Emails.AnyAsync(message => message.Id == archiveMessage.Id));

        var deleted = await service.CommitDeletesAsync(
            new Pop3DeleteRequest(owner.User.Id, [ownerMessage.Id, ownerMessage.Id]));
        Assert.AreEqual(1, deleted.DeletedCount);
        Assert.IsFalse(await database.Emails.AnyAsync(message => message.Id == ownerMessage.Id));
        Assert.AreEqual(1, await database.ExpungedUids.CountAsync(
            item => item.FolderId == owner.Folder.Id && item.Uid == 10));
        Assert.AreEqual(1, owner.Folder.HighestModSeq);
    }

    [TestMethod]
    public async Task MissingDeleteIdsIdentifyThePublicRequestParameter()
    {
        var service = new Pop3ApplicationService(null!, null!, null!, null!, null!, null!);
        var exception = await Assert.ThrowsExactlyAsync<ArgumentNullException>(
            () => service.CommitDeletesAsync(new Pop3DeleteRequest(Guid.CreateVersion7(), null!)));
        Assert.AreEqual("request", exception.ParamName);
    }

    private static (UserDB User, FolderDB Folder) SeedAccount(
        EmailDbContext database,
        string localPart,
        int index)
    {
        var company = new CompanyDB
        {
            Id = Guid.CreateVersion7(),
            Name = $"POP3 test company {index}",
            IsActive = true,
        };
        var address = new AddressDB
        {
            Id = Guid.CreateVersion7(),
            Domain = "example.test",
            IsActive = true,
            Company = company,
        };
        var user = new UserDB
        {
            Id = Guid.CreateVersion7(),
            Username = $"{localPart}@example.test",
            PasswordHash = "unused",
            Role = "User",
            IsActive = true,
            Company = company,
        };
        var inbox = new InboxDB
        {
            Id = Guid.CreateVersion7(),
            Name = localPart,
            Address = address,
            Owner = user,
        };
        var folder = new FolderDB
        {
            Id = Guid.CreateVersion7(),
            Name = DefaultFolders.Inbox,
            Inbox = inbox,
        };
        database.Folders.Add(folder);
        return (user, folder);
    }

    private static EmailDB AddMessage(
        EmailDbContext database,
        FolderDB folder,
        int uid,
        byte[] raw)
    {
        var message = new EmailDB
        {
            Id = Guid.CreateVersion7(),
            Folder = folder,
            Uid = uid,
            Sender = "sender@example.test",
            Recipient = folder.Inbox.Owner.Username,
            Subject = "POP3 test",
            Body = string.Empty,
            RawMessage = raw,
            SizeBytes = raw.Length,
        };
        database.Emails.Add(message);
        return message;
    }
}
