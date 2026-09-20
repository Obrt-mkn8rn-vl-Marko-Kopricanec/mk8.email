using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using mk8.email.Infrastructure.Data;
using mk8.email.Infrastructure.Models;
using mk8.email.Jmap;

namespace mk8.email.Application.Tests;

[TestClass]
public sealed class JmapChangeCollectorTests
{
    [TestMethod]
    public async Task RecipientOnlyChangePublishesEmailSubmissionUpdate()
    {
        await using var fixture = await JmapFixture.CreateAsync();
        using var scope = fixture.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
        var states = scope.ServiceProvider.GetRequiredService<JmapStateService>();
        var queueId = Guid.CreateVersion7();
        var recipientId = Guid.CreateVersion7();
        var submissionId = Guid.CreateVersion7();
        database.MailQueueMessages.Add(new MailQueueMessageDB
        {
            Id = queueId,
            EnvelopeSender = fixture.User.Username,
            RawMessage = "From: user@mk8n.com\r\nTo: recipient@example.net\r\n\r\nbody",
            Direction = MailQueueDirections.Submission,
            State = MailQueueStates.Pending,
            ScanState = MailQueueScanStates.Pending,
            Recipients =
            [
                new MailQueueRecipientDB
                {
                    Id = recipientId,
                    MessageId = queueId,
                    Recipient = "recipient@example.net",
                    State = MailQueueRecipientStates.Pending,
                },
            ],
        });
        database.JmapEmailSubmissions.Add(new JmapEmailSubmissionDB
        {
            Id = submissionId,
            SubmissionObjectId = submissionId.ToString("N"),
            AccountId = fixture.InboxId,
            IdentityId = JmapId.Identity(fixture.InboxId),
            EmailId = JmapId.Email(Guid.CreateVersion7()),
            ThreadId = JmapId.Thread(Guid.CreateVersion7().ToString("N")),
            QueueId = queueId,
            EnvelopeSender = fixture.User.Username,
            EnvelopeRecipients = ["recipient@example.net"],
        });
        await database.SaveChangesAsync();
        var oldState = await states.GetStateAsync(
            fixture.InboxId,
            JmapConstants.EmailSubmissionDataType);
        database.ChangeTracker.Clear();

        var recipient = await database.MailQueueRecipients.SingleAsync(item => item.Id == recipientId);
        recipient.State = MailQueueRecipientStates.Delivered;
        recipient.CompletedAt = DateTime.UtcNow;
        await database.SaveChangesAsync();

        Assert.AreEqual(
            EntityState.Unchanged,
            database.Entry(await database.MailQueueMessages.SingleAsync(item => item.Id == queueId)).State);
        var changes = await states.GetChangesAsync(
            fixture.InboxId,
            JmapConstants.EmailSubmissionDataType,
            oldState,
            10,
            10);
        Assert.IsNotNull(changes);
        Assert.AreNotEqual(oldState, changes.NewState);
        CollectionAssert.AreEqual(
            new[] { JmapId.Submission(submissionId) },
            changes.Updated.ToArray());
    }

    [TestMethod]
    public async Task CascadeMailboxDeletionPublishesEmailAndThreadDestruction()
    {
        await using var fixture = await JmapFixture.CreateAsync();
        using var scope = fixture.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
        var folderId = Guid.CreateVersion7();
        var emailId = Guid.CreateVersion7();
        var threadValue = Guid.CreateVersion7().ToString("N");
        database.Folders.Add(new FolderDB
        {
            Id = folderId,
            InboxId = fixture.InboxId,
            Name = "Cascade deletion",
        });
        database.Emails.Add(new EmailDB
        {
            Id = emailId,
            FolderId = folderId,
            Sender = "sender@example.net",
            Recipient = fixture.User.Username,
            Subject = "Cascade deletion",
            Body = "body",
            RawMessage = "From: sender@example.net\r\n\r\nbody"u8.ToArray(),
            SizeBytes = 39,
            EmailObjectId = emailId.ToString("N"),
            ThreadObjectId = threadValue,
            MessageId = $"<{emailId:N}@example.net>",
            Uid = 1,
            ModSeq = 1,
        });
        await database.SaveChangesAsync();
        database.ChangeTracker.Clear();

        var folder = await database.Folders.SingleAsync(candidate => candidate.Id == folderId);
        Assert.AreEqual(0, database.ChangeTracker.Entries<EmailDB>().Count());
        database.Folders.Remove(folder);
        await database.SaveChangesAsync();

        var emailChanges = await ChangesForAsync(JmapConstants.EmailDataType, JmapId.Email(emailId));
        var threadChanges = await ChangesForAsync(
            JmapConstants.ThreadDataType,
            JmapId.Thread(threadValue));
        CollectionAssert.AreEquivalent(new[] { "created", "destroyed" }, emailChanges);
        CollectionAssert.AreEquivalent(new[] { "created", "destroyed" }, threadChanges);

        Task<List<string>> ChangesForAsync(string dataType, string objectId) =>
            database.JmapChanges
                .Where(change => change.AccountId == fixture.InboxId
                    && change.DataType == dataType
                    && change.ObjectId == objectId)
                .Select(change => change.ChangeKind)
                .ToListAsync();
    }

    [TestMethod]
    public async Task PartialMoveDoesNotPublishHiddenEmailAsCreated()
    {
        await using var fixture = await JmapFixture.CreateAsync();
        using var scope = fixture.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
        var sourceFolderId = Guid.CreateVersion7();
        var destinationFolderId = Guid.CreateVersion7();
        var emailId = Guid.CreateVersion7();
        var threadValue = Guid.CreateVersion7().ToString("N");
        database.Folders.AddRange(
            new FolderDB
            {
                Id = sourceFolderId,
                InboxId = fixture.InboxId,
                Name = "Hidden source",
            },
            new FolderDB
            {
                Id = destinationFolderId,
                InboxId = fixture.InboxId,
                Name = "Hidden destination",
            });
        database.Emails.Add(new EmailDB
        {
            Id = emailId,
            FolderId = sourceFolderId,
            Sender = "sender@example.net",
            Recipient = fixture.User.Username,
            Subject = "Hidden move",
            Body = "body",
            IsDeleted = true,
            EmailObjectId = emailId.ToString("N"),
            ThreadObjectId = threadValue,
            Uid = 1,
            ModSeq = 1,
        });
        await database.SaveChangesAsync();
        database.ChangeTracker.Clear();

        var partialUpdate = new EmailDB
        {
            Id = emailId,
            FolderId = sourceFolderId,
        };
        database.Emails.Attach(partialUpdate);
        partialUpdate.FolderId = destinationFolderId;
        database.Entry(partialUpdate).Property(email => email.FolderId).IsModified = true;
        await database.SaveChangesAsync();
        database.ChangeTracker.Clear();

        var stored = await database.Emails.AsNoTracking().SingleAsync(email => email.Id == emailId);
        Assert.AreEqual(destinationFolderId, stored.FolderId);
        Assert.IsTrue(stored.IsDeleted);
        Assert.AreEqual(0, await database.JmapChanges.CountAsync(change =>
            change.AccountId == fixture.InboxId
            && (change.DataType == JmapConstants.EmailDataType
                && change.ObjectId == JmapId.Email(emailId)
                || change.DataType == JmapConstants.ThreadDataType
                && change.ObjectId == JmapId.Thread(threadValue))));
    }

    [TestMethod]
    public async Task CrossAccountMovePublishesTargetEmailDelivery()
    {
        await using var fixture = await JmapFixture.CreateAsync();
        using var scope = fixture.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
        var sourceInbox = await database.Inboxes
            .AsNoTracking()
            .SingleAsync(inbox => inbox.Id == fixture.InboxId);
        var targetInboxId = Guid.CreateVersion7();
        var targetFolderId = Guid.CreateVersion7();
        database.Inboxes.Add(new InboxDB
        {
            Id = targetInboxId,
            Name = "alternate",
            AddressId = sourceInbox.AddressId,
            OwnerId = sourceInbox.OwnerId,
        });
        database.Folders.Add(new FolderDB
        {
            Id = targetFolderId,
            InboxId = targetInboxId,
            Name = "INBOX",
            JmapRole = "inbox",
        });
        var emailId = Guid.CreateVersion7();
        database.Emails.Add(new EmailDB
        {
            Id = emailId,
            FolderId = fixture.InboxFolderId,
            Sender = "sender@example.net",
            Recipient = fixture.User.Username,
            Subject = "Cross-account move",
            Body = "body",
            RawMessage = "From: sender@example.net\r\n\r\nbody"u8.ToArray(),
            SizeBytes = 39,
            EmailObjectId = emailId.ToString("N"),
            ThreadObjectId = Guid.CreateVersion7().ToString("N"),
            MessageId = $"<{emailId:N}@example.net>",
            Uid = 1,
            ModSeq = 1,
        });
        await database.SaveChangesAsync();
        database.ChangeTracker.Clear();

        var email = await database.Emails.SingleAsync(candidate => candidate.Id == emailId);
        email.FolderId = targetFolderId;
        await database.SaveChangesAsync();

        var emailObjectId = JmapId.Email(emailId);
        Assert.AreEqual(1, await database.JmapChanges.CountAsync(change =>
            change.AccountId == targetInboxId
            && change.DataType == JmapConstants.EmailDataType
            && change.ObjectId == emailObjectId
            && change.ChangeKind == JmapConstants.CreatedChange));
        Assert.AreEqual(1, await database.JmapChanges.CountAsync(change =>
            change.AccountId == targetInboxId
            && change.DataType == JmapConstants.EmailDeliveryDataType
            && change.ObjectId == emailObjectId
            && change.ChangeKind == JmapConstants.CreatedChange));
    }
}
