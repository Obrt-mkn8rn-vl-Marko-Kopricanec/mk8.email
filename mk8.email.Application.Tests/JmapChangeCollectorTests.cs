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
}
