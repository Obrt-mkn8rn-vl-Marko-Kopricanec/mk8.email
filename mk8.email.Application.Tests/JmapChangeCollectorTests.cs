using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using mk8.email.Infrastructure.Data;
using mk8.email.Infrastructure.Models;
using mk8.email.Jmap;

namespace mk8.email.Application.Tests;

[TestClass]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1812", Justification = "MSTest DiscoverInternals instantiates this test class by reflection; focused discovery is verified by executed test counts.")]
internal sealed class JmapChangeCollectorTests
{
    [TestMethod]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "MA0051", Justification = "The RecipientOnlyChangePublishesEmailSubmissionUpdate scenario keeps one fixture's ordered setup, operation and invariant assertions together.")]
    public async Task RecipientOnlyChangePublishesEmailSubmissionUpdate()
    {
        var fixture = (await JmapFixture.CreateAsync().ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        using var scope = fixture.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
        var states = scope.ServiceProvider.GetRequiredService<JmapStateService>();
        var queueId = Guid.CreateVersion7();
        var recipientId = Guid.CreateVersion7();
        var submissionId = Guid.CreateVersion7();
        await database.MailQueueMessages.AddAsync(new MailQueueMessageDB
        {
            Id = queueId,
            EnvelopeSender = fixture.User.Username,
            RawMessage = "From: user@tenant.example.test\r\nTo: recipient@example.net\r\n\r\nbody",
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
        }).ConfigureAwait(false);
        await database.JmapEmailSubmissions.AddAsync(new JmapEmailSubmissionDB
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
        }).ConfigureAwait(false);
        await database.SaveChangesAsync().ConfigureAwait(false);
        var oldState = await states.GetStateAsync(
            fixture.InboxId,
            JmapConstants.EmailSubmissionDataType).ConfigureAwait(false);
        database.ChangeTracker.Clear();

        var recipient = await database.MailQueueRecipients.SingleAsync(item => item.Id == recipientId).ConfigureAwait(false);
        recipient.State = MailQueueRecipientStates.Delivered;
        recipient.CompletedAt = DateTime.UtcNow;
        await database.SaveChangesAsync().ConfigureAwait(false);

        Assert.AreEqual(
            EntityState.Unchanged,
            database.Entry(await database.MailQueueMessages.SingleAsync(item => item.Id == queueId).ConfigureAwait(false)).State);
        var changes = await states.GetChangesAsync(
            fixture.InboxId,
            JmapConstants.EmailSubmissionDataType,
            oldState,
            10,
            10).ConfigureAwait(false);
        Assert.IsNotNull(changes);
        Assert.AreNotEqual(oldState, changes.NewState, StringComparer.Ordinal);
        CollectionAssert.AreEqual(
            new[] { JmapId.Submission(submissionId) },
            changes.Updated.ToArray());
    }

    [TestMethod]
    public async Task CascadeMailboxDeletionPublishesEmailAndThreadDestruction()
    {
        var fixture = (await JmapFixture.CreateAsync().ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        using var scope = fixture.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
        var folderId = Guid.CreateVersion7();
        var emailId = Guid.CreateVersion7();
        var threadValue = Guid.CreateVersion7().ToString("N");
        await database.Folders.AddAsync(new FolderDB
        {
            Id = folderId,
            InboxId = fixture.InboxId,
            Name = "Cascade deletion",
        }).ConfigureAwait(false);
        await database.Emails.AddAsync(new EmailDB
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
        }).ConfigureAwait(false);
        await database.SaveChangesAsync().ConfigureAwait(false);
        database.ChangeTracker.Clear();

        var folder = await database.Folders.SingleAsync(candidate => candidate.Id == folderId).ConfigureAwait(false);
        Assert.AreEqual(0, database.ChangeTracker.Entries<EmailDB>().Count());
        database.Folders.Remove(folder);
        await database.SaveChangesAsync().ConfigureAwait(false);

        var emailChanges = await ChangesForAsync(JmapConstants.EmailDataType, JmapId.Email(emailId)).ConfigureAwait(false);
        var threadChanges = await ChangesForAsync(
            JmapConstants.ThreadDataType,
            JmapId.Thread(threadValue)).ConfigureAwait(false);
        CollectionAssert.AreEquivalent(ExpectedVector1, emailChanges);
        CollectionAssert.AreEquivalent(ExpectedVector1, threadChanges);

        Task<List<string>> ChangesForAsync(string dataType, string objectId) =>
            database.JmapChanges
                .Where(change => change.AccountId == fixture.InboxId
                    && string.Equals(change.DataType, dataType
, StringComparison.Ordinal) && string.Equals(change.ObjectId, objectId, StringComparison.Ordinal))
                .Select(change => change.ChangeKind)
                .ToListAsync();
    }

    [TestMethod]
    public async Task PartialMoveDoesNotPublishHiddenEmailAsCreated()
    {
        var fixture = (await JmapFixture.CreateAsync().ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        using var scope = fixture.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
        var sourceFolderId = Guid.CreateVersion7();
        var destinationFolderId = Guid.CreateVersion7();
        var emailId = Guid.CreateVersion7();
        var threadValue = Guid.CreateVersion7().ToString("N");
        await database.Folders.AddRangeAsync(
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
            }).ConfigureAwait(false);
        await database.Emails.AddAsync(new EmailDB
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
        }).ConfigureAwait(false);
        await database.SaveChangesAsync().ConfigureAwait(false);
        database.ChangeTracker.Clear();

        var partialUpdate = new EmailDB
        {
            Id = emailId,
            FolderId = sourceFolderId,
        };
        database.Emails.Attach(partialUpdate);
        partialUpdate.FolderId = destinationFolderId;
        database.Entry(partialUpdate).Property(email => email.FolderId).IsModified = true;
        await database.SaveChangesAsync().ConfigureAwait(false);
        database.ChangeTracker.Clear();

        var stored = await database.Emails.AsNoTracking().SingleAsync(email => email.Id == emailId).ConfigureAwait(false);
        Assert.AreEqual(destinationFolderId, stored.FolderId);
        Assert.IsTrue(stored.IsDeleted);
        Assert.AreEqual(0, await database.JmapChanges.CountAsync(change =>
            change.AccountId == fixture.InboxId
            && (string.Equals(change.DataType, JmapConstants.EmailDataType
, StringComparison.Ordinal) && string.Equals(change.ObjectId, JmapId.Email(emailId)
, StringComparison.Ordinal) || string.Equals(change.DataType, JmapConstants.ThreadDataType
, StringComparison.Ordinal) && string.Equals(change.ObjectId, JmapId.Thread(threadValue), StringComparison.Ordinal))).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task CrossAccountMovePublishesTargetEmailDelivery()
    {
        var fixture = (await JmapFixture.CreateAsync().ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        using var scope = fixture.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
        var sourceInbox = await database.Inboxes
            .AsNoTracking()
            .SingleAsync(inbox => inbox.Id == fixture.InboxId).ConfigureAwait(false);
        var targetInboxId = Guid.CreateVersion7();
        var targetFolderId = Guid.CreateVersion7();
        await database.Inboxes.AddAsync(new InboxDB
        {
            Id = targetInboxId,
            Name = "alternate",
            AddressId = sourceInbox.AddressId,
            OwnerId = sourceInbox.OwnerId,
        }).ConfigureAwait(false);
        await database.Folders.AddAsync(new FolderDB
        {
            Id = targetFolderId,
            InboxId = targetInboxId,
            Name = "INBOX",
            JmapRole = "inbox",
        }).ConfigureAwait(false);
        var emailId = Guid.CreateVersion7();
        await database.Emails.AddAsync(new EmailDB
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
        }).ConfigureAwait(false);
        await database.SaveChangesAsync().ConfigureAwait(false);
        database.ChangeTracker.Clear();

        var email = await database.Emails.SingleAsync(candidate => candidate.Id == emailId).ConfigureAwait(false);
        email.FolderId = targetFolderId;
        await database.SaveChangesAsync().ConfigureAwait(false);

        var emailObjectId = JmapId.Email(emailId);
        Assert.AreEqual(1, await database.JmapChanges.CountAsync(change =>
            change.AccountId == targetInboxId
            && change.DataType == JmapConstants.EmailDataType
            && change.ObjectId == emailObjectId
            && change.ChangeKind == JmapConstants.CreatedChange).ConfigureAwait(false));
        Assert.AreEqual(1, await database.JmapChanges.CountAsync(change =>
            change.AccountId == targetInboxId
            && change.DataType == JmapConstants.EmailDeliveryDataType
            && change.ObjectId == emailObjectId
            && change.ChangeKind == JmapConstants.CreatedChange).ConfigureAwait(false));
    }
    private static readonly string[] ExpectedVector1 = new[] { "created", "destroyed" };
}
