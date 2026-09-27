using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using mk8.email.Application.Services;
using mk8.email.Contracts.Imap;
using mk8.email.Infrastructure.Data;
using mk8.email.Infrastructure.Models;

namespace mk8.email.Messaging.Tests;

[TestClass]
[TestCategory("PostgreSQL")]
public sealed class ImapFetchPagingPostgresTests
{
    [TestMethod]
    [Timeout(90_000)]
    [DataRow(8 * 1024 * 1024, false)]
    [DataRow(8 * 1024 * 1024 + 1, true)]
    [DataRow(17 * 1024 * 1024, true)]
    public async Task ContentByteLimitPreservesCursorAndAllowsOneOversizedFirstMessage(int size, bool split)
    {
        await using var server = await PostgresTestDatabase.TryCreateAsync();
        if (server is null)
        {
            Assert.Inconclusive("Set MK8_EMAIL_TEST_POSTGRES to a PostgreSQL admin connection string.");
            return;
        }
        var options = new DbContextOptionsBuilder<EmailDbContext>().UseNpgsql(server.ConnectionString).Options;
        await using var database = new EmailDbContext(options);
        var userId = Guid.CreateVersion7();
        var folderId = Guid.CreateVersion7();
        var objects = new InMemoryLargeObjectStore();
        var effects = new LargeObjectTransactionEffects(objects, NullLogger<LargeObjectTransactionEffects>.Instance);
        var content = new MailboxMessageContentService(objects, effects);
        await SeedMessagesAsync(database, content, effects, userId, folderId, size);
        var application = new ImapApplicationService(null!, null!, database, content, effects,
            NullLogger<ImapApplicationService>.Instance);
        var request = new ImapFetchPageRequest(userId, folderId, true, new ImapMessageSelection(null, [2, 9]),
            0, null, null, null, true);
        var first = await application.GetFetchPageAsync(request);
        Assert.IsTrue(first.FolderFound);
        Assert.AreEqual(9, first.SnapshotMaxUid);
        Assert.AreEqual(9, first.SnapshotMaximumIdentifier);
        Assert.AreEqual(2, first.SnapshotMessageCount);
        Assert.AreEqual(split, first.HasMore);
        CollectionAssert.AreEqual(split ? new[] { 2 } : new[] { 2, 9 }, first.Messages.Select(item => item.Uid).ToArray());
        CollectionAssert.AreEqual(split ? new[] { 1 } : new[] { 1, 2 }, first.Messages.Select(item => item.SequenceNumber).ToArray());
        Assert.AreEqual(split ? 2 : 9, first.NextAfterUid);
        Assert.IsTrue(first.Messages.All(item => item.RawMessage?.Length == size));
        Assert.AreEqual(2, objects.ReadCount);
        if (split)
            await AssertSecondPageAsync(application, request, first, size);
        var reads = objects.ReadCount;
        var metadata = await application.GetFetchPageAsync(request with { IncludeStoredContent = false });
        Assert.IsFalse(metadata.HasMore);
        Assert.HasCount(2, metadata.Messages);
        Assert.IsTrue(metadata.Messages.All(item => item.RawMessage is null && item.Body.Length == 0 && item.RawHeaders is null));
        Assert.AreEqual(reads, objects.ReadCount);
        Assert.AreEqual(2, objects.ObjectCount);
    }

    private static async Task AssertSecondPageAsync(ImapApplicationService application,
        ImapFetchPageRequest request, ImapFetchPageResult first, int size)
    {
        var second = await application.GetFetchPageAsync(request with
        {
            AfterUid = first.NextAfterUid,
            SnapshotMaxUid = first.SnapshotMaxUid,
            SnapshotMaximumIdentifier = first.SnapshotMaximumIdentifier,
            SnapshotMessageCount = first.SnapshotMessageCount,
        });
        Assert.IsFalse(second.HasMore);
        Assert.AreEqual(9, second.NextAfterUid);
        Assert.HasCount(1, second.Messages);
        Assert.AreEqual(9, second.Messages[0].Uid);
        Assert.AreEqual(2, second.Messages[0].SequenceNumber);
        Assert.AreEqual(size, second.Messages[0].RawMessage?.Length);
    }

    private static async Task SeedMessagesAsync(EmailDbContext database, MailboxMessageContentService content,
        LargeObjectTransactionEffects effects, Guid userId, Guid folderId, int size)
    {
        await database.Database.EnsureCreatedAsync();
        await new MailRuntimeSchemaService(database).EnsureAsync();
        var company = new CompanyDB { Id = Guid.CreateVersion7(), Name = "FETCH byte boundary", IsActive = true };
        var folder = new FolderDB
        {
            Id = folderId,
            Name = "Inbox",
            NextUid = 10,
            HighestModSeq = 9,
            Inbox = new InboxDB
            {
                Id = Guid.CreateVersion7(),
                Name = "owner",
                Owner = new UserDB { Id = userId, Username = "owner@example.test", PasswordHash = "unused", Role = "User", Company = company },
                Address = new AddressDB { Id = Guid.CreateVersion7(), Domain = "example.test", IsActive = true, Company = company },
            },
        };
        var raw = new byte[size];
        Array.Fill(raw, (byte)'a');
        "Subject: boundary\r\n\r\n"u8.CopyTo(raw);
        var marker = effects.Mark();
        foreach (var uid in new[] { 2, 9 })
        {
            var email = new EmailDB { Id = Guid.CreateVersion7(), Folder = folder, Uid = uid, ModSeq = uid };
            await content.SetAsync(email, raw, CancellationToken.None);
            database.Emails.Add(email);
        }
        await database.SaveChangesAsync();
        await effects.CommitAsync(marker);
    }
}
