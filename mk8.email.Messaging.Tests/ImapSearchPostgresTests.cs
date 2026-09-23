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
public sealed class ImapSearchPostgresTests
{
    [TestMethod]
    [Timeout(60_000)]
    public async Task SearchIsOwnerScopedAndReadsAzureCompatibleMessageContent()
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
        var otherId = Guid.CreateVersion7();
        var folderId = Guid.CreateVersion7();
        var objects = new InMemoryLargeObjectStore();
        await using (var database = new EmailDbContext(options))
        {
            await database.Database.EnsureCreatedAsync();
            await new MailRuntimeSchemaService(database).EnsureAsync();
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
            database.Users.Add(new UserDB
            {
                Id = otherId,
                Username = "other@example.test",
                PasswordHash = "unused",
                Role = "User",
                IsActive = true,
                Company = company,
            });
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
                await content.SetAsync(email, raw, CancellationToken.None);
                database.Emails.Add(email);
            }
            await database.SaveChangesAsync();
            await effects.CommitAsync(marker);
        }

        await using (var database = new EmailDbContext(options))
        {
            var effects = new LargeObjectTransactionEffects(
                objects, NullLogger<LargeObjectTransactionEffects>.Instance);
            var application = new ImapApplicationService(
                null!, null!, database,
                new MailboxMessageContentService(objects, effects),
                effects, NullLogger<ImapApplicationService>.Instance);
            Assert.IsFalse((await application.SearchMessagesAsync(new ImapSearchRequest(
                otherId, folderId, "ALL", [], false))).FolderFound);
            Assert.IsFalse((await application.SearchMessagesAsync(new ImapSearchRequest(
                ownerId, Guid.CreateVersion7(), "ALL", [], false))).FolderFound);

            var body = await application.SearchMessagesAsync(new ImapSearchRequest(
                ownerId, folderId, "BODY needle", [], false));
            Assert.IsTrue(body.FolderFound);
            Assert.IsNull(body.FailureResponse);
            CollectionAssert.AreEqual(new[] { 1, 3 },
                body.Matches.Select(match => match.Uid).ToArray());
            CollectionAssert.AreEqual(new[] { 1, 3 },
                body.Matches.Select(match => match.SequenceNumber).ToArray());

            var header = await application.SearchMessagesAsync(new ImapSearchRequest(
                ownerId, folderId, "HEADER X-Label zebra", [], false));
            CollectionAssert.AreEqual(new[] { 1 },
                header.Matches.Select(match => match.Uid).ToArray());
            var saved = await application.SearchMessagesAsync(new ImapSearchRequest(
                ownerId, folderId, "$", [2], false));
            CollectionAssert.AreEqual(new[] { 2 },
                saved.Matches.Select(match => match.Uid).ToArray());
            var invalid = await application.SearchMessagesAsync(new ImapSearchRequest(
                ownerId, folderId, "OR", [], false));
            StringAssert.StartsWith(invalid.FailureResponse, "BAD ");
            Assert.IsEmpty(invalid.Matches);
            await Assert.ThrowsAsync<ArgumentException>(() => application.SearchMessagesAsync(
                new ImapSearchRequest(ownerId, folderId, "ALL", [-1], false)));

            var sortRequest = new ImapSortRequest(
                ownerId, folderId, "ALL", [], false, "US-ASCII",
                [new ImapSortCriterion(ImapSortKey.Subject, true)]);
            Assert.IsFalse((await application.SortMessagesAsync(
                sortRequest with { UserId = otherId })).FolderFound);
            var sorted = await application.SortMessagesAsync(sortRequest);
            Assert.IsTrue(sorted.FolderFound);
            CollectionAssert.AreEqual(new[] { 2, 1, 3 },
                sorted.SortedMatches.Select(match => match.Uid).ToArray());
            var savedSort = await application.SortMessagesAsync(
                sortRequest with { SearchCriteria = "$", SavedSearchUids = [1, 3] });
            CollectionAssert.AreEqual(new[] { 1, 3 },
                savedSort.SortedMatches.Select(match => match.Uid).ToArray());
            await Assert.ThrowsAsync<ArgumentException>(() => application.SortMessagesAsync(
                sortRequest with { Charset = "UNSUPPORTED" }));
            await Assert.ThrowsAsync<ArgumentException>(() => application.SortMessagesAsync(
                sortRequest with
                {
                    SortCriteria = [new ImapSortCriterion((ImapSortKey)999, false)],
                }));

            var threadRequest = new ImapThreadRequest(
                ownerId, folderId, "ALL", [], false, "US-ASCII",
                ImapThreadAlgorithm.References, true);
            Assert.IsFalse((await application.ThreadMessagesAsync(
                threadRequest with { UserId = otherId })).FolderFound);
            var references = await application.ThreadMessagesAsync(threadRequest);
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
            });
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
            });
            Assert.HasCount(2, savedThread.Nodes);
            Assert.IsFalse(savedThread.Nodes.Any(node => node.Identifier == 2));
            await Assert.ThrowsAsync<ArgumentException>(() => application.ThreadMessagesAsync(
                threadRequest with { Charset = "UNSUPPORTED" }));
            await Assert.ThrowsAsync<ArgumentException>(() => application.ThreadMessagesAsync(
                threadRequest with { Algorithm = (ImapThreadAlgorithm)999 }));

            var messageIds = await database.Emails
                .AsNoTracking()
                .Where(email => email.FolderId == folderId)
                .OrderBy(email => email.Uid)
                .Select(email => email.Id)
                .ToListAsync();
            var seenRequest = new ImapMarkSeenRequest(
                ownerId, folderId, [messageIds[0], messageIds[1]]);
            Assert.IsFalse((await application.MarkMessagesSeenAsync(
                seenRequest with { UserId = otherId })).FolderFound);
            var seen = await application.MarkMessagesSeenAsync(seenRequest);
            Assert.IsTrue(seen.FolderFound);
            CollectionAssert.AreEqual(new long[] { 4, 5 },
                seen.Messages.Select(message => message.ModSeq).ToArray());
            Assert.IsTrue(seen.Messages.All(message => message.Found));
            var replay = await application.MarkMessagesSeenAsync(seenRequest);
            CollectionAssert.AreEqual(new long[] { 4, 5 },
                replay.Messages.Select(message => message.ModSeq).ToArray());
            var missing = await application.MarkMessagesSeenAsync(seenRequest with
            {
                MessageIds = [Guid.CreateVersion7()],
            });
            Assert.IsFalse(missing.Messages[0].Found);
            await Assert.ThrowsAsync<ArgumentException>(() => application.MarkMessagesSeenAsync(
                seenRequest with { MessageIds = [messageIds[0], messageIds[0]] }));

            var fetchRequest = new ImapFetchPageRequest(
                ownerId, folderId, true,
                new ImapMessageSelection([new ImapMessageRange(1, null)], null),
                0, null, null, false);
            Assert.IsFalse((await application.GetFetchPageAsync(fetchRequest with
            {
                UserId = otherId,
            })).FolderFound);
            var metadataPage = await application.GetFetchPageAsync(fetchRequest);
            Assert.IsTrue(metadataPage.FolderFound);
            Assert.IsFalse(metadataPage.HasMore);
            CollectionAssert.AreEqual(new[] { 1, 2, 3 },
                metadataPage.Messages.Select(message => message.Uid).ToArray());
            Assert.IsTrue(metadataPage.Messages.All(message =>
                message.RawMessage is null && message.Body.Length == 0
                && message.RawHeaders is null));
            var rawPage = await application.GetFetchPageAsync(fetchRequest with
            {
                Selection = new ImapMessageSelection(null, [1]),
                IncludeStoredContent = true,
            });
            Assert.HasCount(1, rawPage.Messages);
            var rawMessage = rawPage.Messages[0].RawMessage
                ?? throw new AssertFailedException("The Blob-backed message was missing.");
            StringAssert.Contains(
                Encoding.ASCII.GetString(rawMessage), "needle one");
            var sequencePage = await application.GetFetchPageAsync(fetchRequest with
            {
                UseUid = false,
                Selection = new ImapMessageSelection(
                    [new ImapMessageRange(2, 2)], null),
            });
            Assert.HasCount(1, sequencePage.Messages);
            Assert.AreEqual(2, sequencePage.Messages[0].Uid);
            await Assert.ThrowsAsync<ArgumentException>(() => application.GetFetchPageAsync(
                fetchRequest with
                {
                    AfterUid = 4,
                    SnapshotMaxUid = 3,
                    SnapshotMaximumIdentifier = 3,
                }));
        }
        await using (var database = new EmailDbContext(options))
        {
            Assert.AreEqual(5L, await database.Folders
                .Where(folder => folder.Id == folderId)
                .Select(folder => folder.HighestModSeq)
                .SingleAsync());
            var flags = await database.Emails
                .Where(email => email.FolderId == folderId)
                .OrderBy(email => email.Uid)
                .Select(email => new { email.IsRead, email.ModSeq })
                .ToListAsync();
            CollectionAssert.AreEqual(new[] { true, true, false },
                flags.Select(email => email.IsRead).ToArray());
            CollectionAssert.AreEqual(new long[] { 4, 5, 3 },
                flags.Select(email => email.ModSeq).ToArray());
        }

        await using (var database = new EmailDbContext(options))
        {
            var effects = new LargeObjectTransactionEffects(
                objects, NullLogger<LargeObjectTransactionEffects>.Instance);
            var content = new MailboxMessageContentService(objects, effects);
            var marker = effects.Mark();
            var folder = await database.Folders.SingleAsync(item => item.Id == folderId);
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
                    CancellationToken.None);
                database.Emails.Add(email);
            }
            folder.NextUid = 261;
            folder.HighestModSeq = 6;
            await database.SaveChangesAsync();
            await effects.CommitAsync(marker);
        }
        await using (var database = new EmailDbContext(options))
        {
            var effects = new LargeObjectTransactionEffects(
                objects, NullLogger<LargeObjectTransactionEffects>.Instance);
            var application = new ImapApplicationService(
                null!, null!, database,
                new MailboxMessageContentService(objects, effects),
                effects, NullLogger<ImapApplicationService>.Instance);
            var request = new ImapFetchPageRequest(
                ownerId, folderId, true,
                new ImapMessageSelection([new ImapMessageRange(1, null)], null),
                0, null, null, false);
            var fetchedUids = new List<int>();
            var pageCount = 0;
            while (true)
            {
                var page = await application.GetFetchPageAsync(request);
                pageCount++;
                fetchedUids.AddRange(page.Messages.Select(message => message.Uid));
                Assert.AreEqual(260, page.SnapshotMaxUid);
                Assert.AreEqual(260, page.SnapshotMaximumIdentifier);
                if (!page.HasMore)
                    break;
                request = request with
                {
                    AfterUid = page.NextAfterUid,
                    SnapshotMaxUid = page.SnapshotMaxUid,
                    SnapshotMaximumIdentifier = page.SnapshotMaximumIdentifier,
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
            };
            var emptyScan = await application.GetFetchPageAsync(sparseRequest);
            Assert.IsEmpty(emptyScan.Messages);
            Assert.IsTrue(emptyScan.HasMore);
            Assert.AreEqual(256, emptyScan.NextAfterUid);
            var sparsePage = await application.GetFetchPageAsync(sparseRequest with
            {
                AfterUid = emptyScan.NextAfterUid,
                SnapshotMaxUid = emptyScan.SnapshotMaxUid,
                SnapshotMaximumIdentifier = emptyScan.SnapshotMaximumIdentifier,
            });
            Assert.IsFalse(sparsePage.HasMore);
            Assert.HasCount(1, sparsePage.Messages);
            Assert.AreEqual(260, sparsePage.Messages[0].Uid);
        }
        Assert.AreEqual(260, objects.ObjectCount);
    }
}
