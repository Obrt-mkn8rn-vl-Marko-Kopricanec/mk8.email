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
    [Timeout(20_000)]
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
                var raw = Encoding.ASCII.GetBytes(
                    $"Subject: {subject}\r\nX-Label: {label}\r\n\r\n{body}\r\n");
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
            CollectionAssert.AreEqual(new[] { 3, 2, 1 },
                sorted.SortedMatches.Select(match => match.Uid).ToArray());
            var savedSort = await application.SortMessagesAsync(
                sortRequest with { SearchCriteria = "$", SavedSearchUids = [1, 3] });
            CollectionAssert.AreEqual(new[] { 3, 1 },
                savedSort.SortedMatches.Select(match => match.Uid).ToArray());
            await Assert.ThrowsAsync<ArgumentException>(() => application.SortMessagesAsync(
                sortRequest with { Charset = "UNSUPPORTED" }));
            await Assert.ThrowsAsync<ArgumentException>(() => application.SortMessagesAsync(
                sortRequest with
                {
                    SortCriteria = [new ImapSortCriterion((ImapSortKey)999, false)],
                }));
        }
        Assert.AreEqual(3, objects.ObjectCount);
    }
}
