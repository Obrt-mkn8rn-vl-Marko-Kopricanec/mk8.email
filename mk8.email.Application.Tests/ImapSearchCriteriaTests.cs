using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using mk8.email.Application.Services;
using mk8.email.Infrastructure.Data;
using mk8.email.Infrastructure.Models;

namespace mk8.email.Application.Tests;

[TestClass]
public sealed class ImapSearchCriteriaTests
{
    private static readonly int[] OrderedUids = [2, 5, 9];

    [TestMethod]
    [DataRow("ALL", new[] { 2, 5, 9 }, 0, null)]
    [DataRow("SEEN", new[] { 2 }, 0, null)]
    [DataRow("FLAGGED", new[] { 5 }, 0, null)]
    [DataRow("UID 5:9", new[] { 5, 9 }, 0, null)]
    [DataRow("UID $", new[] { 2, 9 }, 0, null)]
    [DataRow("1:2", new[] { 2, 5 }, 0, null)]
    [DataRow("MODSEQ 40", new[] { 5, 9 }, 0, 90L)]
    [DataRow("SUBJECT \"alpha \\\"quoted\\\"\"", new[] { 2 }, 0, null)]
    [DataRow("SUBJECT \"beta\\\\path\"", new[] { 5 }, 0, null)]
    [DataRow("BODY second", new[] { 5 }, 3, null)]
    [DataRow("HEADER X-Test first", new[] { 2 }, 3, null)]
    [DataRow("TEXT gamma", new[] { 9 }, 3, null)]
    [DataRow("OR SEEN BODY second", new[] { 2, 5 }, 3, null)]
    [DataRow("ANSWERED", new[] { 9 }, 0, null)]
    [DataRow("UNANSWERED", new[] { 2, 5 }, 0, null)]
    [DataRow("DELETED", new[] { 5 }, 0, null)]
    [DataRow("UNDELETED", new[] { 2, 9 }, 0, null)]
    [DataRow("DRAFT", new[] { 9 }, 0, null)]
    [DataRow("UNDRAFT", new[] { 2, 5 }, 0, null)]
    [DataRow("UNFLAGGED", new[] { 2, 9 }, 0, null)]
    [DataRow("UNSEEN", new[] { 5, 9 }, 0, null)]
    [DataRow("NEW", new int[] { }, 0, null)]
    [DataRow("RECENT", new int[] { }, 0, null)]
    [DataRow("OLD", new[] { 2, 5, 9 }, 0, null)]
    [DataRow("NOT SEEN", new[] { 5, 9 }, 0, null)]
    [DataRow("OR SEEN (DRAFT UNDELETED)", new[] { 2, 9 }, 0, null)]
    [DataRow("EMAILID Memail2", new[] { 2 }, 0, null)]
    [DataRow("EMAILID MEMAIL2", new int[] { }, 0, null)]
    [DataRow("THREADID Tthreadshared", new[] { 2, 5 }, 0, null)]
    [DataRow("THREADID TTHREADSHARED", new int[] { }, 0, null)]
    [DataRow("UID *", new[] { 9 }, 0, null)]
    [DataRow("UID 9:5", new[] { 5, 9 }, 0, null)]
    [DataRow("KEYWORD $LABEL1", new[] { 2, 9 }, 0, null)]
    [DataRow("UNKEYWORD $label1", new[] { 5 }, 0, null)]
    [DataRow("BEFORE 5-Jan-2026", new[] { 2 }, 0, null)]
    [DataRow("ON 5-Jan-2026", new[] { 5 }, 0, null)]
    [DataRow("SINCE 5-Jan-2026", new[] { 5, 9 }, 0, null)]
    [DataRow("SENTBEFORE 5-Jan-2026", new[] { 2 }, 3, null)]
    [DataRow("SENTON 5-Jan-2026", new[] { 5 }, 3, null)]
    [DataRow("SENTSINCE 5-Jan-2026", new[] { 5, 9 }, 3, null)]
    [DataRow("FROM sender", new[] { 2, 5, 9 }, 3, null)]
    [DataRow("TO mailbox", new[] { 2, 5, 9 }, 3, null)]
    [DataRow("CC copy", new[] { 2, 5, 9 }, 3, null)]
    [DataRow("BCC blind", new[] { 2, 5, 9 }, 3, null)]
    [DataRow("LARGER 0", new[] { 2, 5, 9 }, 0, null)]
    [DataRow("SMALLER 0", new int[] { }, 0, null)]
    [DataRow("MODSEQ /flags/\\Seen SHARED 40", new[] { 5, 9 }, 0, 90L)]
    [DataRow("MODSEQ /flags/\\Seen PRIV 40", new[] { 5, 9 }, 0, 90L)]
    [DataRow("MODSEQ /flags/\\Seen ALL 40", new[] { 5, 9 }, 0, 90L)]
    [DataRow("MODSEQ 91", new int[] { }, 0, null)]
    public async Task SearchKeepsMailboxSequenceNumbersAndReadsOnlyRequiredPayloads(
        string criteria, int[] expectedUids, int reads, long? highestModSequence)
    {
        await using var fixture = await SearchFixture.CreateAsync().ConfigureAwait(false);
        var result = await fixture.SearchAsync(criteria).ConfigureAwait(false);
        Assert.IsNull(result.FailureResponse);
        CollectionAssert.AreEqual(expectedUids, result.Matches.Select(match => match.Uid).ToArray());
        CollectionAssert.AreEqual(expectedUids.Select(uid => Array.IndexOf(OrderedUids, uid) + 1).ToArray(),
            result.Matches.Select(match => match.SequenceNumber).ToArray());
        Assert.AreEqual(highestModSequence, result.HighestModSequence);
        Assert.AreEqual(reads, fixture.Objects.ReadCount);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("ALL(")]
    [DataRow("(ALL)ALL")]
    [DataRow("SUBJECT \"bad\\q\"")]
    [DataRow("SUBJECT \"unterminated")]
    [DataRow("SUBJECT \"trailing\\")]
    [DataRow("SUBJECT \"valid\"ALL")]
    [DataRow("SUBJECT \"nul\0\"")]
    [DataRow("ALL\r\n")]
    [DataRow("ALL\0")]
    [DataRow(")")]
    [DataRow("(ALL")]
    [DataRow("ALL)")]
    [DataRow("OR ALL")]
    [DataRow("HEADER \"\" value")]
    [DataRow("UNKNOWN")]
    [DataRow("NOT")]
    [DataRow("SUBJECT")]
    [DataRow("EMAILID")]
    [DataRow("EMAILID invalid!")]
    [DataRow("THREADID invalid!")]
    [DataRow("BODY")]
    [DataRow("TEXT")]
    [DataRow("HEADER name")]
    [DataRow("BEFORE 31-Feb-2026")]
    [DataRow("SENTON invalid")]
    [DataRow("LARGER -1")]
    [DataRow("SMALLER 4294967296")]
    [DataRow("UID 0")]
    [DataRow("UID")]
    [DataRow("KEYWORD")]
    [DataRow("UNKEYWORD")]
    [DataRow("MODSEQ -1")]
    [DataRow("MODSEQ /flags/\\Seen INVALID 40")]
    [DataRow("MODSEQ /flags/\\Seen SHARED")]
    [DataRow("MODSEQ /invalid SHARED 40")]
    public async Task InvalidCriteriaFailBeforePayloadReads(string criteria)
    {
        await using var fixture = await SearchFixture.CreateAsync().ConfigureAwait(false);
        var result = await fixture.SearchAsync(criteria).ConfigureAwait(false);
        Assert.AreEqual("BAD Invalid search criteria", result.FailureResponse);
        Assert.AreEqual(0, result.Matches.Count);
        Assert.IsNull(result.HighestModSequence);
        Assert.AreEqual(0, fixture.Objects.ReadCount);
    }

    [TestMethod]
    [DataRow(4096, true)]
    [DataRow(4097, false)]
    public async Task TokenLimitRetainsItsExactBoundary(int count, bool allowed)
    {
        await using var fixture = await SearchFixture.CreateAsync().ConfigureAwait(false);
        var result = await fixture.SearchAsync(string.Join(' ', Enumerable.Repeat("ALL", count))).ConfigureAwait(false);
        Assert.AreEqual(allowed, result.FailureResponse is null);
        CollectionAssert.AreEqual(allowed ? OrderedUids : Array.Empty<int>(), result.Matches.Select(match => match.Uid).ToArray());
        Assert.AreEqual(0, fixture.Objects.ReadCount);
    }

    [TestMethod]
    [DataRow(64, true)]
    [DataRow(65, false)]
    public async Task NestingLimitRetainsItsExactBoundary(int depth, bool allowed)
    {
        await using var fixture = await SearchFixture.CreateAsync().ConfigureAwait(false);
        var result = await fixture.SearchAsync(new string('(', depth) + "ALL" + new string(')', depth)).ConfigureAwait(false);
        Assert.AreEqual(allowed, result.FailureResponse is null);
        CollectionAssert.AreEqual(allowed ? OrderedUids : Array.Empty<int>(), result.Matches.Select(match => match.Uid).ToArray());
        Assert.AreEqual(0, fixture.Objects.ReadCount);
    }

    [TestMethod]
    [DataRow(64, true)]
    [DataRow(65, false)]
    public async Task NegationLimitRetainsItsExactBoundary(int depth, bool allowed)
    {
        await using var fixture = await SearchFixture.CreateAsync().ConfigureAwait(false);
        var result = await fixture.SearchAsync(string.Concat(Enumerable.Repeat("NOT ", depth)) + "ALL")
            .ConfigureAwait(false);
        Assert.AreEqual(allowed, result.FailureResponse is null);
        CollectionAssert.AreEqual(allowed ? OrderedUids : Array.Empty<int>(), result.Matches.Select(match => match.Uid).ToArray());
        Assert.AreEqual(0, fixture.Objects.ReadCount);
    }

    private sealed class SearchFixture(
        EmailDbContext database,
        InMemoryLargeObjectStore objects,
        MailboxMessageContentService content) : IAsyncDisposable
    {
        public InMemoryLargeObjectStore Objects { get; } = objects;

        public static async Task<SearchFixture> CreateAsync()
        {
            var database = new EmailDbContext(new DbContextOptionsBuilder<EmailDbContext>()
                .UseInMemoryDatabase($"imap-search-phases-{Guid.NewGuid():N}").Options);
            await database.Database.EnsureCreatedAsync().ConfigureAwait(false);
            var objects = new InMemoryLargeObjectStore();
            var effects = new LargeObjectTransactionEffects(objects, NullLogger<LargeObjectTransactionEffects>.Instance);
            var content = new MailboxMessageContentService(objects, effects);
            var marker = effects.Mark();
            var folderId = Guid.CreateVersion7();
            foreach (var uid in new[] { 9, 2, 5 })
            {
                var (subject, body) = uid switch
                {
                    2 => ("alpha \"quoted\"", "first body"),
                    5 => ("beta\\path", "second body"),
                    _ => ("gamma", "third body"),
                };
                var email = new EmailDB
                {
                    Id = Guid.CreateVersion7(),
                    Uid = uid,
                    ModSeq = uid * 10,
                    Subject = subject,
                    Sender = "sender@example.test",
                    Recipient = "mailbox@example.test",
                    IsRead = uid == 2,
                    IsFlagged = uid == 5,
                    IsDeleted = uid == 5,
                    IsDraft = uid == 9,
                    IsAnswered = uid == 9,
                    Keywords = uid == 5 ? [] : ["$label1"],
                    EmailObjectId = $"email{uid}",
                    ThreadObjectId = uid == 9 ? "thread9" : "threadshared",
                    ReceivedAt = new DateTime(2026, 1, uid, 12, 0, 0, DateTimeKind.Utc),
                    FolderId = folderId,
                };
                var raw = $"From: sender@example.test\r\nTo: mailbox@example.test\r\nSubject: {subject}\r\n"
                    + $"Date: {uid} Jan 2026 12:00:00 +0000\r\nCc: copy@example.test\r\nBcc: blind@example.test\r\n"
                    + $"X-Test: {body.Split(' ')[0]}\r\n\r\n{body}\r\n";
                await content.SetAsync(email, Encoding.Latin1.GetBytes(raw), CancellationToken.None).ConfigureAwait(false);
                database.Emails.Add(email);
            }
            await database.SaveChangesAsync().ConfigureAwait(false);
            await effects.CommitAsync(marker).ConfigureAwait(false);
            return new SearchFixture(database, objects, content);
        }

        public Task<ImapSearchEngine.SearchExecutionResult> SearchAsync(string criteria) =>
            ImapSearchEngine.FindSearchCandidatesAsync(database.Emails.AsNoTracking(), content, criteria,
                new HashSet<int> { 2, 9 }, utf8Enabled: false, CancellationToken.None);

        public ValueTask DisposeAsync() => database.DisposeAsync();
    }
}
