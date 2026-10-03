using System.Text;
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
internal sealed class ImapSearchPostgresTests
{
    [TestMethod]
    [Timeout(60_000)]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "MA0051", Justification = "The SearchIsOwnerScopedAndReadsAzureCompatibleMessageContent scenario keeps one fixture's ordered setup, operation and invariant assertions together.")]
    public async Task SearchIsOwnerScopedAndReadsAzureCompatibleMessageContent()
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
        var otherId = Guid.CreateVersion7();
        var folderId = Guid.CreateVersion7();
        var objects = new InMemoryLargeObjectStore();
        {
            var database = new EmailDbContext(options);
            await using var databaseLifetime = database.ConfigureAwait(false);
            await database.Database.EnsureCreatedAsync().ConfigureAwait(false);
            await new MailRuntimeSchemaService(database).EnsureAsync().ConfigureAwait(false);
            var company = new CompanyDB
            {
                Id = Guid.CreateVersion7(),
                Name = "IMAP SEARCH test",
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
                Id = ownerId,
                Username = "owner@example.test",
                PasswordHash = "unused",
                Role = "User",
                IsActive = true,
                Company = company,
            };
            await (database.Users.AddAsync(new UserDB
            {
                Id = otherId,
                Username = "other@example.test",
                PasswordHash = "unused",
                Role = "User",
                IsActive = true,
                Company = company,
            })).ConfigureAwait(false);
            var folder = new FolderDB
            {
                Id = folderId,
                Name = "Inbox",
                NextUid = 4,
                HighestModSeq = 3,
                Inbox = new InboxDB
                {
                    Id = Guid.CreateVersion7(),
                    Name = "owner",
                    Address = address,
                    Owner = owner,
                },
            };
            var effects = new LargeObjectTransactionEffects(
                objects, NullLogger<LargeObjectTransactionEffects>.Instance);
            var content = new MailboxMessageContentService(objects, effects);
            var marker = effects.Mark();
            foreach (var (uid, subject, body, label) in new[]
            {
                (1, "Alpha", "needle one", "zebra"),
                (2, "Beta", "plain body", "other"),
                (3, "Gamma", "needle three", "other"),
            })
            {
                var email = new EmailDB
                {
                    Id = Guid.CreateVersion7(),
                    Folder = folder,
                    Uid = uid,
                    ModSeq = uid,
                    Sender = "sender@example.test",
                    Recipient = "owner@example.test",
                    Subject = uid == 1 ? "Zebra metadata" : subject,
                };
                var rawSubject = uid == 3 ? "Re: Alpha" : subject;
                var replyHeader = uid == 3
                    ? "In-Reply-To: <message-1@example.test>\r\n"
                    : string.Empty;
                var raw = Encoding.ASCII.GetBytes(
                    $"Subject: {rawSubject}\r\n" +
                    $"Date: Mon, 01 Jan 2024 00:00:0{uid} +0000\r\n" +
                    $"Message-ID: <message-{uid}@example.test>\r\n" +
                    replyHeader +
                    $"X-Label: {label}\r\n\r\n{body}\r\n");
                await content.SetAsync(email, raw, CancellationToken.None).ConfigureAwait(false);
                await (database.Emails.AddAsync(email)).ConfigureAwait(false);
            }
            await database.SaveChangesAsync().ConfigureAwait(false);
            await effects.CommitAsync(marker).ConfigureAwait(false);
        }

        {
            var database = new EmailDbContext(options);
            await using var databaseLifetime = database.ConfigureAwait(false);
            var effects = new LargeObjectTransactionEffects(
                objects, NullLogger<LargeObjectTransactionEffects>.Instance);
            var application = new ImapApplicationService(
                null!, null!, database,
                new MailboxMessageContentService(objects, effects),
                effects, NullLogger<ImapApplicationService>.Instance);
            Assert.IsFalse((await application.SearchMessagesAsync(new ImapSearchRequest(
                otherId, folderId, "ALL", [], false)).ConfigureAwait(false)).FolderFound);
            Assert.IsFalse((await application.SearchMessagesAsync(new ImapSearchRequest(
                ownerId, Guid.CreateVersion7(), "ALL", [], false)).ConfigureAwait(false)).FolderFound);

            var body = await application.SearchMessagesAsync(new ImapSearchRequest(
                ownerId, folderId, "BODY needle", [], false)).ConfigureAwait(false);
            Assert.IsTrue(body.FolderFound);
            Assert.IsNull(body.FailureResponse);
            CollectionAssert.AreEqual(ExpectedVector1,
                body.Matches.Select(match => match.Uid).ToArray());
            CollectionAssert.AreEqual(ExpectedVector1,
                body.Matches.Select(match => match.SequenceNumber).ToArray());

            var header = await application.SearchMessagesAsync(new ImapSearchRequest(
                ownerId, folderId, "HEADER X-Label zebra", [], false)).ConfigureAwait(false);
            CollectionAssert.AreEqual(ExpectedVector2,
                header.Matches.Select(match => match.Uid).ToArray());
            var saved = await application.SearchMessagesAsync(new ImapSearchRequest(
                ownerId, folderId, "$", [2], false)).ConfigureAwait(false);
            CollectionAssert.AreEqual(ExpectedVector3,
                saved.Matches.Select(match => match.Uid).ToArray());
            var invalid = await application.SearchMessagesAsync(new ImapSearchRequest(
                ownerId, folderId, "OR", [], false)).ConfigureAwait(false);
            StringAssert.StartsWith(invalid.FailureResponse, "BAD ", StringComparison.Ordinal);
            Assert.IsEmpty(invalid.Matches);
            await Assert.ThrowsAsync<ArgumentException>(() => application.SearchMessagesAsync(
                new ImapSearchRequest(ownerId, folderId, "ALL", [-1], false))).ConfigureAwait(false);

            var sortRequest = new ImapSortRequest(
                ownerId, folderId, "ALL", [], false, "US-ASCII",
                [new ImapSortCriterion(ImapSortKey.Subject, true)]);
            Assert.IsFalse((await application.SortMessagesAsync(
                sortRequest with { UserId = otherId }).ConfigureAwait(false)).FolderFound);
            var sorted = await application.SortMessagesAsync(sortRequest).ConfigureAwait(false);
            Assert.IsTrue(sorted.FolderFound);
            CollectionAssert.AreEqual(ExpectedVector4,
                sorted.SortedMatches.Select(match => match.Uid).ToArray());
            var savedSort = await application.SortMessagesAsync(
                sortRequest with { SearchCriteria = "$", SavedSearchUids = [1, 3] }).ConfigureAwait(false);
            CollectionAssert.AreEqual(ExpectedVector1,
                savedSort.SortedMatches.Select(match => match.Uid).ToArray());
            CollectionAssert.AreEqual(ExpectedVector1,
                savedSort.SortedMatches.Select(match => match.SequenceNumber).ToArray());
            var emptySort = await application.SortMessagesAsync(
                sortRequest with { SearchCriteria = "UID 999" }).ConfigureAwait(false);
            Assert.IsTrue(emptySort.FolderFound);
            Assert.IsNull(emptySort.FailureResponse);
            Assert.IsEmpty(emptySort.SortedMatches);
            var invalidSort = await application.SortMessagesAsync(
                sortRequest with { SearchCriteria = "OR" }).ConfigureAwait(false);
            StringAssert.StartsWith(invalidSort.FailureResponse, "BAD ", StringComparison.Ordinal);
            Assert.IsEmpty(invalidSort.SortedMatches);
            await Assert.ThrowsAsync<ArgumentException>(() => application.SortMessagesAsync(
                sortRequest with { Charset = "UNSUPPORTED" })).ConfigureAwait(false);
            await Assert.ThrowsAsync<ArgumentException>(() => application.SortMessagesAsync(
                sortRequest with
                {
                    SortCriteria = [new ImapSortCriterion((ImapSortKey)999, false)],
                })).ConfigureAwait(false);

            var threadRequest = new ImapThreadRequest(
                ownerId, folderId, "ALL", [], false, "US-ASCII",
                ImapThreadAlgorithm.References, true);
            Assert.IsFalse((await application.ThreadMessagesAsync(
                threadRequest with { UserId = otherId }).ConfigureAwait(false)).FolderFound);
            var references = await application.ThreadMessagesAsync(threadRequest).ConfigureAwait(false);
            Assert.IsTrue(references.FolderFound);
            Assert.IsNull(references.FailureResponse);
            Assert.HasCount(3, references.Nodes);
            var referenceIndex = references.Nodes
                .Select((node, index) => (
                    Identifier: node.Identifier ?? throw new AssertFailedException(
                        "Unexpected dummy thread node."), index))
                .ToDictionary(item => item.Identifier, item => item.index);
            Assert.AreEqual(referenceIndex[1],
                references.Nodes[referenceIndex[3]].ParentIndex);
            Assert.AreEqual(-1, references.Nodes[referenceIndex[2]].ParentIndex);

            var ordered = await application.ThreadMessagesAsync(threadRequest with
            {
                Algorithm = ImapThreadAlgorithm.OrderedSubject,
            }).ConfigureAwait(false);
            Assert.HasCount(3, ordered.Nodes);
            var orderedIndex = ordered.Nodes
                .Select((node, index) => (
                    Identifier: node.Identifier ?? throw new AssertFailedException(
                        "Unexpected dummy thread node."), index))
                .ToDictionary(item => item.Identifier, item => item.index);
            Assert.AreEqual(orderedIndex[1],
                ordered.Nodes[orderedIndex[3]].ParentIndex);
            var savedThread = await application.ThreadMessagesAsync(threadRequest with
            {
                SearchCriteria = "$",
                SavedSearchUids = [1, 3],
            }).ConfigureAwait(false);
            Assert.HasCount(2, savedThread.Nodes);
            Assert.IsFalse(savedThread.Nodes.Any(node => node.Identifier == 2));
            var emptyThread = await application.ThreadMessagesAsync(threadRequest with
            {
                Algorithm = ImapThreadAlgorithm.OrderedSubject,
                SearchCriteria = "UID 999",
            }).ConfigureAwait(false);
            Assert.IsTrue(emptyThread.FolderFound);
            Assert.IsNull(emptyThread.FailureResponse);
            Assert.IsEmpty(emptyThread.Nodes);
            var invalidThread = await application.ThreadMessagesAsync(
                threadRequest with { SearchCriteria = "OR" }).ConfigureAwait(false);
            StringAssert.StartsWith(invalidThread.FailureResponse, "BAD ", StringComparison.Ordinal);
            Assert.IsEmpty(invalidThread.Nodes);
            await Assert.ThrowsAsync<ArgumentException>(() => application.ThreadMessagesAsync(
                threadRequest with { Charset = "UNSUPPORTED" })).ConfigureAwait(false);
            await Assert.ThrowsAsync<ArgumentException>(() => application.ThreadMessagesAsync(
                threadRequest with { Algorithm = (ImapThreadAlgorithm)999 })).ConfigureAwait(false);

            var messageIds = await database.Emails
                .AsNoTracking()
                .Where(email => email.FolderId == folderId)
                .OrderBy(email => email.Uid)
                .Select(email => email.Id)
                .ToListAsync().ConfigureAwait(false);
            var seenRequest = new ImapMarkSeenRequest(
                ownerId, folderId, [messageIds[0], messageIds[1]]);
            Assert.IsFalse((await application.MarkMessagesSeenAsync(
                seenRequest with { UserId = otherId }).ConfigureAwait(false)).FolderFound);
            var seen = await application.MarkMessagesSeenAsync(seenRequest).ConfigureAwait(false);
            Assert.IsTrue(seen.FolderFound);
            CollectionAssert.AreEqual(new long[] { 4, 5 },
                seen.Messages.Select(message => message.ModSeq).ToArray());
            Assert.IsTrue(seen.Messages.All(message => message.Found));
            var replay = await application.MarkMessagesSeenAsync(seenRequest).ConfigureAwait(false);
            CollectionAssert.AreEqual(new long[] { 4, 5 },
                replay.Messages.Select(message => message.ModSeq).ToArray());
            var missing = await application.MarkMessagesSeenAsync(seenRequest with
            {
                MessageIds = [Guid.CreateVersion7()],
            }).ConfigureAwait(false);
            Assert.IsFalse(missing.Messages[0].Found);
            await Assert.ThrowsAsync<ArgumentException>(() => application.MarkMessagesSeenAsync(
                seenRequest with { MessageIds = [messageIds[0], messageIds[0]] })).ConfigureAwait(false);
            var mixedRequest = seenRequest with
            {
                MessageIds = [Guid.CreateVersion7(), messageIds[2], messageIds[0]],
            };
            var mixed = await application.MarkMessagesSeenAsync(mixedRequest).ConfigureAwait(false);
            CollectionAssert.AreEqual(mixedRequest.MessageIds, mixed.Messages
                .Select(message => message.Id).ToList());
            CollectionAssert.AreEqual(ExpectedVector5,
                mixed.Messages.Select(message => message.Found).ToArray());
            CollectionAssert.AreEqual(new long[] { 0, 6, 4 },
                mixed.Messages.Select(message => message.ModSeq).ToArray());
            var mixedReplay = await application.MarkMessagesSeenAsync(mixedRequest).ConfigureAwait(false);
            CollectionAssert.AreEqual(new long[] { 0, 6, 4 },
                mixedReplay.Messages.Select(message => message.ModSeq).ToArray());

            var fetchRequest = new ImapFetchPageRequest(
                ownerId, folderId, true,
                new ImapMessageSelection([new ImapMessageRange(1, null)], null),
                0, null, null, null, false);
            Assert.IsFalse((await application.GetFetchPageAsync(fetchRequest with
            {
                UserId = otherId,
            }).ConfigureAwait(false)).FolderFound);
            var metadataPage = await application.GetFetchPageAsync(fetchRequest).ConfigureAwait(false);
            Assert.IsTrue(metadataPage.FolderFound);
            Assert.IsFalse(metadataPage.HasMore);
            CollectionAssert.AreEqual(ExpectedVector6,
                metadataPage.Messages.Select(message => message.Uid).ToArray());
            Assert.IsTrue(metadataPage.Messages.All(message =>
                message.RawMessage is null && message.Body.Length == 0
                && message.RawHeaders is null));
            var rawPage = await application.GetFetchPageAsync(fetchRequest with
            {
                Selection = new ImapMessageSelection(null, [1]),
                IncludeStoredContent = true,
            }).ConfigureAwait(false);
            Assert.HasCount(1, rawPage.Messages);
            var rawMessage = rawPage.Messages[0].RawMessage
                ?? throw new AssertFailedException("The Blob-backed message was missing.");
            StringAssert.Contains(
                Encoding.ASCII.GetString(rawMessage), "needle one", StringComparison.Ordinal);
            var sequencePage = await application.GetFetchPageAsync(fetchRequest with
            {
                UseUid = false,
                Selection = new ImapMessageSelection(
                    [new ImapMessageRange(2, 2)], null),
            }).ConfigureAwait(false);
            Assert.HasCount(1, sequencePage.Messages);
            Assert.AreEqual(2, sequencePage.Messages[0].Uid);
            await Assert.ThrowsAsync<ArgumentException>(() => application.GetFetchPageAsync(
                fetchRequest with
                {
                    AfterUid = 4,
                    SnapshotMaxUid = 3,
                    SnapshotMaximumIdentifier = 3,
                    SnapshotMessageCount = 3,
                })).ConfigureAwait(false);
        }
        {
            var database = new EmailDbContext(options);
            await using var databaseLifetime = database.ConfigureAwait(false);
            Assert.AreEqual(6L, await database.Folders
                .Where(folder => folder.Id == folderId)
                .Select(folder => folder.HighestModSeq)
                .SingleAsync().ConfigureAwait(false));
            var flags = await database.Emails
                .Where(email => email.FolderId == folderId)
                .OrderBy(email => email.Uid)
                .Select(email => new { email.IsRead, email.ModSeq })
                .ToListAsync().ConfigureAwait(false);
            CollectionAssert.AreEqual(ExpectedVector7,
                flags.Select(email => email.IsRead).ToArray());
            CollectionAssert.AreEqual(new long[] { 4, 5, 6 },
                flags.Select(email => email.ModSeq).ToArray());
        }

        {
            var database = new EmailDbContext(options);
            await using var databaseLifetime = database.ConfigureAwait(false);
            var effects = new LargeObjectTransactionEffects(
                objects, NullLogger<LargeObjectTransactionEffects>.Instance);
            var content = new MailboxMessageContentService(objects, effects);
            var marker = effects.Mark();
            var folder = await database.Folders.SingleAsync(item => item.Id == folderId).ConfigureAwait(false);
            for (var uid = 4; uid <= 260; uid++)
            {
                var email = new EmailDB
                {
                    Id = Guid.CreateVersion7(),
                    FolderId = folderId,
                    Uid = uid,
                    ModSeq = 6,
                    Sender = "sender@example.test",
                    Recipient = "owner@example.test",
                    Subject = $"Bulk {uid}",
                };
                await content.SetAsync(email,
                    Encoding.ASCII.GetBytes($"Subject: Bulk {uid}\r\n\r\nbody\r\n"),
                    CancellationToken.None).ConfigureAwait(false);
                await (database.Emails.AddAsync(email)).ConfigureAwait(false);
            }
            folder.NextUid = 261;
            folder.HighestModSeq = 6;
            await database.SaveChangesAsync().ConfigureAwait(false);
            await effects.CommitAsync(marker).ConfigureAwait(false);
        }
        {
            var database = new EmailDbContext(options);
            await using var databaseLifetime = database.ConfigureAwait(false);
            var effects = new LargeObjectTransactionEffects(
                objects, NullLogger<LargeObjectTransactionEffects>.Instance);
            var application = new ImapApplicationService(
                null!, null!, database,
                new MailboxMessageContentService(objects, effects),
                effects, NullLogger<ImapApplicationService>.Instance);
            var request = new ImapFetchPageRequest(
                ownerId, folderId, true,
                new ImapMessageSelection([new ImapMessageRange(1, null)], null),
                0, null, null, null, false);
            var fetchedUids = new List<int>();
            var pageCount = 0;
            while (true)
            {
                var page = await application.GetFetchPageAsync(request).ConfigureAwait(false);
                pageCount++;
                fetchedUids.AddRange(page.Messages.Select(message => message.Uid));
                Assert.AreEqual(260, page.SnapshotMaxUid);
                Assert.AreEqual(260, page.SnapshotMaximumIdentifier);
                Assert.AreEqual(260, page.SnapshotMessageCount);
                if (!page.HasMore)
                    break;
                request = request with
                {
                    AfterUid = page.NextAfterUid,
                    SnapshotMaxUid = page.SnapshotMaxUid,
                    SnapshotMaximumIdentifier = page.SnapshotMaximumIdentifier,
                    SnapshotMessageCount = page.SnapshotMessageCount,
                };
            }
            Assert.AreEqual(3, pageCount);
            CollectionAssert.AreEqual(Enumerable.Range(1, 260).ToArray(),
                fetchedUids.ToArray());

            var sparseRequest = request with
            {
                Selection = new ImapMessageSelection(null, [260]),
                AfterUid = 0,
                SnapshotMaxUid = null,
                SnapshotMaximumIdentifier = null,
                SnapshotMessageCount = null,
            };
            var emptyScan = await application.GetFetchPageAsync(sparseRequest).ConfigureAwait(false);
            Assert.IsEmpty(emptyScan.Messages);
            Assert.IsTrue(emptyScan.HasMore);
            Assert.AreEqual(256, emptyScan.NextAfterUid);
            var sparsePage = await application.GetFetchPageAsync(sparseRequest with
            {
                AfterUid = emptyScan.NextAfterUid,
                SnapshotMaxUid = emptyScan.SnapshotMaxUid,
                SnapshotMaximumIdentifier = emptyScan.SnapshotMaximumIdentifier,
                SnapshotMessageCount = emptyScan.SnapshotMessageCount,
            }).ConfigureAwait(false);
            Assert.IsFalse(sparsePage.HasMore);
            Assert.HasCount(1, sparsePage.Messages);
            Assert.AreEqual(260, sparsePage.Messages[0].Uid);

            var beforeExpunge = await application.GetFetchPageAsync(sparseRequest with
            {
                Selection = new ImapMessageSelection(
                    [new ImapMessageRange(1, null)], null),
            }).ConfigureAwait(false);
            Assert.IsTrue(beforeExpunge.HasMore);
            {
                var concurrent = new EmailDbContext(options);
                await using var concurrentLifetime = concurrent.ConfigureAwait(false);
                Assert.AreEqual(1, await concurrent.Emails
                    .Where(email => email.FolderId == folderId && email.Uid == 1)
                    .ExecuteDeleteAsync().ConfigureAwait(false));
            }
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                application.GetFetchPageAsync(sparseRequest with
                {
                    Selection = new ImapMessageSelection(
                        [new ImapMessageRange(1, null)], null),
                    AfterUid = beforeExpunge.NextAfterUid,
                    SnapshotMaxUid = beforeExpunge.SnapshotMaxUid,
                    SnapshotMaximumIdentifier = beforeExpunge.SnapshotMaximumIdentifier,
                    SnapshotMessageCount = beforeExpunge.SnapshotMessageCount,
                })).ConfigureAwait(false);
        }
        Assert.AreEqual(260, objects.ObjectCount);
    }
    private static readonly int[] ExpectedVector1 = new[] { 1, 3 };
    private static readonly int[] ExpectedVector2 = new[] { 1 };
    private static readonly int[] ExpectedVector3 = new[] { 2 };
    private static readonly int[] ExpectedVector4 = new[] { 2, 1, 3 };
    private static readonly bool[] ExpectedVector5 = new[] { false, true, true };
    private static readonly int[] ExpectedVector6 = new[] { 1, 2, 3 };
    private static readonly bool[] ExpectedVector7 = new[] { true, true, true };
}
