using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using mk8.email.Application.Services;
using mk8.email.Contracts.Imap;
using mk8.email.Infrastructure.Data;
using mk8.email.Infrastructure.Models;

namespace mk8.email.Messaging.Tests;

[TestClass]
[TestCategory("PostgreSQL")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1812", Justification = "MSTest DiscoverInternals instantiates this test class by reflection; focused discovery is verified by executed test counts.")]
internal sealed class ImapFetchPagingPostgresTests
{
    [TestMethod]
    [Timeout(90_000)]
    [DataRow(8 * 1024 * 1024, false)]
    [DataRow(8 * 1024 * 1024 + 1, true)]
    [DataRow(17 * 1024 * 1024, true)]
    public async Task ContentByteLimitPreservesCursorAndAllowsOneOversizedFirstMessage(int size, bool split)
    {
        var server = (await PostgresTestDatabase.TryCreateAsync().ConfigureAwait(false));
        await using var serverLifetime = new NullableAsyncDisposable(server).ConfigureAwait(false);
        if (server is null)
        {
            Assert.Inconclusive("Set MK8_EMAIL_TEST_POSTGRES to a PostgreSQL admin connection string.");
            return;
        }
        var options = new DbContextOptionsBuilder<EmailDbContext>().UseNpgsql(server.ConnectionString).Options;
        var database = new EmailDbContext(options);
        await using var databaseLifetime = database.ConfigureAwait(false);
        var userId = Guid.CreateVersion7();
        var folderId = Guid.CreateVersion7();
        var objects = new InMemoryLargeObjectStore();
        var effects = new LargeObjectTransactionEffects(objects, NullLogger<LargeObjectTransactionEffects>.Instance);
        var content = new MailboxMessageContentService(objects, effects);
        await SeedMessagesAsync(database, content, effects, userId, folderId, size).ConfigureAwait(false);
        var application = new ImapApplicationService(null!, null!, database, content, effects,
            NullLogger<ImapApplicationService>.Instance);
        var request = new ImapFetchPageRequest(userId, folderId, true, new ImapMessageSelection(null, [2, 9]),
            0, null, null, null, true);
        var first = await application.GetFetchPageAsync(request).ConfigureAwait(false);
        Assert.IsTrue(first.FolderFound);
        Assert.AreEqual(9, first.SnapshotMaxUid);
        Assert.AreEqual(9, first.SnapshotMaximumIdentifier);
        Assert.AreEqual(2, first.SnapshotMessageCount);
        Assert.AreEqual(split, first.HasMore);
        CollectionAssert.AreEqual(split ? ExpectedVector1 : ExpectedVector2, first.Messages.Select(item => item.Uid).ToArray());
        CollectionAssert.AreEqual(split ? ExpectedVector3 : ExpectedVector4, first.Messages.Select(item => item.SequenceNumber).ToArray());
        Assert.AreEqual(split ? 2 : 9, first.NextAfterUid);
        Assert.IsTrue(first.Messages.All(item => item.RawMessage?.Length == size));
        Assert.AreEqual(2, objects.ReadCount);
        if (split)
            await AssertSecondPageAsync(application, request, first, size).ConfigureAwait(false);
        var reads = objects.ReadCount;
        var metadata = await application.GetFetchPageAsync(request with { IncludeStoredContent = false }).ConfigureAwait(false);
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
        }).ConfigureAwait(false);
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
        await database.Database.EnsureCreatedAsync().ConfigureAwait(false);
        await new MailRuntimeSchemaService(database).EnsureAsync().ConfigureAwait(false);
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
            await content.SetAsync(email, raw, CancellationToken.None).ConfigureAwait(false);
            await database.Emails.AddAsync(email).ConfigureAwait(false);
        }
        await database.SaveChangesAsync().ConfigureAwait(false);
        await effects.CommitAsync(marker).ConfigureAwait(false);
    }
    private static readonly int[] ExpectedVector1 = new[] { 2 };
    private static readonly int[] ExpectedVector2 = new[] { 2, 9 };
    private static readonly int[] ExpectedVector3 = new[] { 1 };
    private static readonly int[] ExpectedVector4 = new[] { 1, 2 };
}
