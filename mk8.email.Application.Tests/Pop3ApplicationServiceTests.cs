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
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1812", Justification = "MSTest DiscoverInternals instantiates this test class by reflection; focused discovery is verified by executed test counts.")]
internal sealed class Pop3ApplicationServiceTests
{
    [TestMethod]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "MA0051", Justification = "The MaildropSnapshotReadAndCommitKeepAccountBoundaries scenario keeps one fixture's ordered setup, operation and invariant assertions together.")]
    public async Task MaildropSnapshotReadAndCommitKeepAccountBoundaries()
    {
        var options = new DbContextOptionsBuilder<EmailDbContext>()
            .UseInMemoryDatabase($"pop3-application-{Guid.NewGuid():N}")
            .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        var database = new EmailDbContext(options);
        await using var databaseLifetime = database.ConfigureAwait(false);
        await database.Database.EnsureCreatedAsync().ConfigureAwait(false);
        var owner = SeedAccount(database, "owner", 1);
        var other = SeedAccount(database, "other", 2);
        var archive = new FolderDB
        {
            Id = Guid.CreateVersion7(),
            Name = "Archive",
            Inbox = owner.Folder.Inbox,
        };
        await (database.Folders.AddAsync(archive)).ConfigureAwait(false);
        var raw = "From: sender@example.test\nTo: owner@example.test\n\n.body\n"u8.ToArray();
        var ownerMessage = AddMessage(database, owner.Folder, 10, raw);
        var otherMessage = AddMessage(database, other.Folder, 20, "Other message\n"u8.ToArray());
        var archiveMessage = AddMessage(database, archive, 30, "Archive message\n"u8.ToArray());
        await database.SaveChangesAsync().ConfigureAwait(false);

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

        var snapshot = await service.ListMaildropAsync(new Pop3UserRequest(owner.User.Id)).ConfigureAwait(false);
        Assert.HasCount(1, snapshot.Messages);
        Assert.AreEqual(ownerMessage.Id, snapshot.Messages[0].Id);
        Assert.AreEqual(10, snapshot.Messages[0].Uid);
        Assert.AreEqual(Pop3WireCodec.NormalizeCrlf(raw).Length, snapshot.Messages[0].SizeBytes);
        CollectionAssert.AreEqual(
            raw,
            (await service.GetMessageAsync(new Pop3MessageRequest(
                owner.User.Id, ownerMessage.Id)).ConfigureAwait(false)).RawMessage);
        Assert.IsNull((await service.GetMessageAsync(new Pop3MessageRequest(
            owner.User.Id, otherMessage.Id)).ConfigureAwait(false)).RawMessage);
        Assert.IsNull((await service.GetMessageAsync(new Pop3MessageRequest(
            owner.User.Id, archiveMessage.Id)).ConfigureAwait(false)).RawMessage);

        var unrelatedDelete = await service.CommitDeletesAsync(
            new Pop3DeleteRequest(owner.User.Id, [otherMessage.Id, archiveMessage.Id])).ConfigureAwait(false);
        Assert.AreEqual(0, unrelatedDelete.DeletedCount);
        Assert.IsTrue(await database.Emails.AnyAsync(message => message.Id == otherMessage.Id).ConfigureAwait(false));
        Assert.IsTrue(await database.Emails.AnyAsync(message => message.Id == archiveMessage.Id).ConfigureAwait(false));

        var deleted = await service.CommitDeletesAsync(
            new Pop3DeleteRequest(owner.User.Id, [ownerMessage.Id, ownerMessage.Id])).ConfigureAwait(false);
        Assert.AreEqual(1, deleted.DeletedCount);
        Assert.IsFalse(await database.Emails.AnyAsync(message => message.Id == ownerMessage.Id).ConfigureAwait(false));
        Assert.AreEqual(1, await database.ExpungedUids.CountAsync(
            item => item.FolderId == owner.Folder.Id && item.Uid == 10).ConfigureAwait(false));
        Assert.AreEqual(1, owner.Folder.HighestModSeq);
    }

    [TestMethod]
    public async Task MissingDeleteIdsIdentifyThePublicRequestParameter()
    {
        var service = new Pop3ApplicationService(null!, null!, null!, null!, null!, null!);
        var exception = await Assert.ThrowsExactlyAsync<ArgumentNullException>(
            () => service.CommitDeletesAsync(new Pop3DeleteRequest(Guid.CreateVersion7(), null!))).ConfigureAwait(false);
        Assert.AreEqual("request", exception.ParamName, StringComparer.Ordinal);
    }

    [TestMethod]
    public async Task DeleteBatchUsesUidOrderAndDoesNotRepeatExpungeChanges()
    {
        var options = new DbContextOptionsBuilder<EmailDbContext>()
            .UseInMemoryDatabase($"pop3-delete-batch-{Guid.NewGuid():N}")
            .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        var database = new EmailDbContext(options);
        await using var databaseLifetime = database.ConfigureAwait(false);
        await database.Database.EnsureCreatedAsync().ConfigureAwait(false);
        var owner = SeedAccount(database, "owner", 1);
        owner.Folder.HighestModSeq = 50;
        var first = AddMessage(database, owner.Folder, 10, "First message\r\n"u8.ToArray());
        var second = AddMessage(database, owner.Folder, 20, "Second message\r\n"u8.ToArray());
        await database.SaveChangesAsync().ConfigureAwait(false);
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
        var request = new Pop3DeleteRequest(owner.User.Id, [second.Id, first.Id, second.Id]);

        Assert.AreEqual(2, (await service.CommitDeletesAsync(request).ConfigureAwait(false)).DeletedCount);
        var expunged = await database.ExpungedUids.OrderBy(item => item.Uid).ToListAsync().ConfigureAwait(false);
        Assert.HasCount(2, expunged);
        Assert.AreEqual(10, expunged[0].Uid);
        Assert.AreEqual(51, expunged[0].ModSeq);
        Assert.AreEqual(20, expunged[1].Uid);
        Assert.AreEqual(52, expunged[1].ModSeq);
        Assert.AreEqual(52, owner.Folder.HighestModSeq);
        Assert.AreEqual(0, await database.Emails.CountAsync().ConfigureAwait(false));

        Assert.AreEqual(0, (await service.CommitDeletesAsync(request).ConfigureAwait(false)).DeletedCount);
        Assert.AreEqual(2, await database.ExpungedUids.CountAsync().ConfigureAwait(false));
        Assert.AreEqual(52, owner.Folder.HighestModSeq);
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
