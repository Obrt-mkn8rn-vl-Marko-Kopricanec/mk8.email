using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using mk8.email.Application.Services;
using mk8.email.Contracts.Imap;
using mk8.email.Contracts.Messaging;

namespace mk8.email.Application.Tests;

[TestClass]
public sealed class ImapApplicationBoundaryTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [TestMethod]
    public async Task ImapAuthenticationCrossesTransportNeutralJsonBoundary()
    {
        var application = new RecordingImapApplication();
        await using var services = new ServiceCollection()
            .AddSingleton<IImapApplicationService>(application)
            .BuildServiceProvider();
        var dispatcher = new ApplicationRequestDispatcher(services);

        var password = await SendAsync<ImapPasswordAuthentication, ImapIdentityResult>(
            dispatcher,
            ApplicationOperations.ImapAuthenticatePassword,
            new ImapPasswordAuthentication("user@example.test", "secret"));
        Assert.AreEqual(application.UserId, password.UserId);
        Assert.AreEqual("user@example.test", password.Username);
        Assert.AreEqual("secret", application.LastPassword);

        var oauth = await SendAsync<ImapOAuthAuthentication, ImapIdentityResult>(
            dispatcher,
            ApplicationOperations.ImapAuthenticateOAuth,
            new ImapOAuthAuthentication("user@example.test", "access-token"));
        Assert.AreEqual(application.UserId, oauth.UserId);
        Assert.AreEqual("access-token", application.LastAccessToken);

        var mailboxes = await SendAsync<ImapMailboxListRequest, ImapMailboxListResult>(
            dispatcher,
            ApplicationOperations.ImapListMailboxes,
            new ImapMailboxListRequest(application.UserId, SubscribedOnly: true));
        Assert.HasCount(1, mailboxes.Mailboxes);
        Assert.AreEqual("INBOX", mailboxes.Mailboxes[0].FolderName);
        Assert.IsTrue(application.LastSubscribedOnly);

        var statuses = await SendAsync<ImapMailboxStatusRequest, ImapMailboxStatusResult>(
            dispatcher,
            ApplicationOperations.ImapGetMailboxStatuses,
            new ImapMailboxStatusRequest(
                application.UserId, ["INBOX"], true, true, true));
        Assert.AreEqual(2, statuses.Statuses["INBOX"].MessageCount);
        Assert.AreEqual(12L, statuses.Statuses["INBOX"].SizeBytes);

        var subscription = await SendAsync<ImapMailboxSubscriptionRequest, ImapMailboxSubscriptionResult>(
            dispatcher,
            ApplicationOperations.ImapSetMailboxSubscription,
            new ImapMailboxSubscriptionRequest(application.UserId, "INBOX", false));
        Assert.IsTrue(subscription.Found);
        Assert.IsFalse(application.LastSubscriptionState);

        var created = await SendAsync<ImapMailboxCreateRequest, ImapMailboxCreateResult>(
            dispatcher,
            ApplicationOperations.ImapCreateMailbox,
            new ImapMailboxCreateRequest(application.UserId, "Projects"));
        Assert.AreEqual(ImapMailboxCreateDisposition.Created, created.Disposition);
        Assert.AreEqual("Projects", application.LastCreatedMailbox);

        var renamed = await SendAsync<ImapMailboxRenameRequest, ImapMailboxRenameResult>(
            dispatcher,
            ApplicationOperations.ImapRenameMailbox,
            new ImapMailboxRenameRequest(application.UserId, "Projects", "Archive"));
        Assert.AreEqual(ImapMailboxRenameDisposition.Renamed, renamed.Disposition);
        Assert.AreEqual("Archive", application.LastRenamedMailbox);

        var deleted = await SendAsync<ImapMailboxDeleteRequest, ImapMailboxDeleteResult>(
            dispatcher,
            ApplicationOperations.ImapDeleteMailbox,
            new ImapMailboxDeleteRequest(application.UserId, "Archive"));
        Assert.AreEqual(ImapMailboxDeleteDisposition.Deleted, deleted.Disposition);
        Assert.AreEqual("Archive", application.LastDeletedMailbox);

        var selected = await SendAsync<ImapMailboxSelectRequest, ImapMailboxSelectResult>(
            dispatcher,
            ApplicationOperations.ImapSelectMailbox,
            new ImapMailboxSelectRequest(application.UserId, "INBOX", 1, 2));
        Assert.IsNotNull(selected.Mailbox);
        Assert.AreEqual(2, selected.Mailbox.MessageCount);
        Assert.AreEqual("INBOX", application.LastSelectedMailbox);

        var quota = await SendAsync<ImapQuotaRequest, ImapQuotaResult>(
            dispatcher,
            ApplicationOperations.ImapGetQuota,
            new ImapQuotaRequest(application.UserId, "INBOX"));
        Assert.IsTrue(quota.MailboxFound);
        Assert.AreEqual(2048L, quota.LimitBytes);
        Assert.AreEqual("INBOX", application.LastQuotaMailbox);

        var append = await SendAsync<ImapAppendPreflightRequest, ImapAppendPreflightResult>(
            dispatcher,
            ApplicationOperations.ImapCheckAppendCapacity,
            new ImapAppendPreflightRequest(application.UserId, "INBOX", 123));
        Assert.AreEqual(ImapAppendPreflightDisposition.Ready, append.Disposition);
        Assert.AreEqual(123L, application.LastAppendBytes);

        var committed = await SendAsync<ImapAppendRequest, ImapAppendResult>(
            dispatcher,
            ApplicationOperations.ImapAppendMessages,
            new ImapAppendRequest(application.UserId, "INBOX", false,
                [new ImapAppendMessage(Guid.CreateVersion7(), ["\\Seen"], null,
                    "Subject: test\r\n\r\nbody"u8.ToArray())]));
        Assert.AreEqual(ImapAppendDisposition.Appended, committed.Disposition);
        Assert.HasCount(1, committed.Uids);
        Assert.AreEqual("INBOX", application.LastAppendMailbox);

        var search = await SendAsync<ImapSearchRequest, ImapSearchResult>(
            dispatcher,
            ApplicationOperations.ImapSearchMessages,
            new ImapSearchRequest(application.UserId, Guid.CreateVersion7(),
                "SUBJECT test", [7], false));
        Assert.IsTrue(search.FolderFound);
        Assert.HasCount(1, search.Matches);
        Assert.AreEqual("SUBJECT test", application.LastSearchCriteria);

        var sort = await SendAsync<ImapSortRequest, ImapSortResult>(
            dispatcher,
            ApplicationOperations.ImapSortMessages,
            new ImapSortRequest(application.UserId, Guid.CreateVersion7(),
                "ALL", [], false, "US-ASCII",
                [new ImapSortCriterion(ImapSortKey.Subject, true)]));
        Assert.IsTrue(sort.FolderFound);
        Assert.HasCount(1, sort.SortedMatches);
        Assert.AreEqual(ImapSortKey.Subject, application.LastSortKey);

        var thread = await SendAsync<ImapThreadRequest, ImapThreadResult>(
            dispatcher,
            ApplicationOperations.ImapThreadMessages,
            new ImapThreadRequest(application.UserId, Guid.CreateVersion7(),
                "ALL", [7], false, "US-ASCII", ImapThreadAlgorithm.References, true));
        Assert.IsTrue(thread.FolderFound);
        Assert.HasCount(1, thread.Nodes);
        Assert.AreEqual(ImapThreadAlgorithm.References, application.LastThreadAlgorithm);

        var seen = await SendAsync<ImapMarkSeenRequest, ImapMarkSeenResult>(
            dispatcher,
            ApplicationOperations.ImapMarkMessagesSeen,
            new ImapMarkSeenRequest(application.UserId, Guid.CreateVersion7(),
                [Guid.CreateVersion7()]));
        Assert.IsTrue(seen.FolderFound);
        Assert.HasCount(1, seen.Messages);
        Assert.AreEqual(7L, seen.Messages[0].ModSeq);

        var page = await SendAsync<ImapFetchPageRequest, ImapFetchPageResult>(
            dispatcher,
            ApplicationOperations.ImapFetchPage,
            new ImapFetchPageRequest(application.UserId, Guid.CreateVersion7(),
                true, new ImapMessageSelection([new ImapMessageRange(1, null)], null),
                0, null, null, true));
        Assert.IsTrue(page.FolderFound);
        Assert.AreEqual(7, page.SnapshotMaxUid);
        Assert.IsTrue(application.LastFetchIncludedContent);

        var idle = await SendAsync<ImapIdleSnapshotRequest, ImapIdleSnapshotResult>(
            dispatcher,
            ApplicationOperations.ImapGetIdleSnapshot,
            new ImapIdleSnapshotRequest(application.UserId, Guid.CreateVersion7()));
        Assert.IsTrue(idle.FolderFound);
        Assert.HasCount(1, idle.Messages);
    }

    private static async Task<TResponse> SendAsync<TRequest, TResponse>(
        ApplicationRequestDispatcher dispatcher,
        string operation,
        TRequest value)
    {
        var now = DateTimeOffset.UtcNow;
        var request = new ApplicationRequest(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            0,
            "imap",
            operation,
            "application/json",
            JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions),
            new Dictionary<string, string>(),
            now,
            now.AddMinutes(1));
        var response = await dispatcher.DispatchAsync(request);
        Assert.IsFalse(response.IsError, response.ErrorCode);
        return JsonSerializer.Deserialize<TResponse>(response.Payload, JsonOptions)
            ?? throw new InvalidOperationException("The IMAP application response was empty.");
    }

    private sealed class RecordingImapApplication : IImapApplicationService
    {
        public Guid UserId { get; } = Guid.CreateVersion7();
        public string? LastPassword { get; private set; }
        public string? LastAccessToken { get; private set; }
        public bool LastSubscribedOnly { get; private set; }
        public bool LastSubscriptionState { get; private set; } = true;
        public string? LastCreatedMailbox { get; private set; }
        public string? LastRenamedMailbox { get; private set; }
        public string? LastDeletedMailbox { get; private set; }
        public string? LastSelectedMailbox { get; private set; }
        public string? LastQuotaMailbox { get; private set; }
        public long LastAppendBytes { get; private set; }
        public string? LastAppendMailbox { get; private set; }
        public string? LastSearchCriteria { get; private set; }
        public ImapSortKey? LastSortKey { get; private set; }

        public Task<ImapIdentityResult> AuthenticatePasswordAsync(
            ImapPasswordAuthentication request,
            CancellationToken cancellationToken = default)
        {
            LastPassword = request.Password;
            return Task.FromResult(new ImapIdentityResult(UserId, request.Username));
        }

        public Task<ImapIdentityResult> AuthenticateOAuthAsync(
            ImapOAuthAuthentication request,
            CancellationToken cancellationToken = default)
        {
            LastAccessToken = request.AccessToken;
            return Task.FromResult(new ImapIdentityResult(UserId, request.Username));
        }

        public Task<ImapMailboxListResult> ListMailboxesAsync(
            ImapMailboxListRequest request,
            CancellationToken cancellationToken = default)
        {
            LastSubscribedOnly = request.SubscribedOnly;
            return Task.FromResult(new ImapMailboxListResult(
                [new ImapMailboxInfo("user", "example.test", "INBOX", true, true)]));
        }

        public Task<ImapMailboxStatusResult> GetMailboxStatusesAsync(
            ImapMailboxStatusRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ImapMailboxStatusResult(new Dictionary<string, ImapMailboxStatus>
            {
                ["INBOX"] = new(
                    Guid.CreateVersion7(), 1, 3, 5, "mailbox-id", 2, 1, 12),
            }));

        public Task<ImapMailboxSubscriptionResult> SetMailboxSubscriptionAsync(
            ImapMailboxSubscriptionRequest request,
            CancellationToken cancellationToken = default)
        {
            LastSubscriptionState = request.IsSubscribed;
            return Task.FromResult(new ImapMailboxSubscriptionResult(true));
        }

        public Task<ImapMailboxCreateResult> CreateMailboxAsync(
            ImapMailboxCreateRequest request,
            CancellationToken cancellationToken = default)
        {
            LastCreatedMailbox = request.MailboxName;
            return Task.FromResult(new ImapMailboxCreateResult(
                ImapMailboxCreateDisposition.Created, Guid.CreateVersion7(), "mailbox-id"));
        }

        public Task<ImapMailboxRenameResult> RenameMailboxAsync(
            ImapMailboxRenameRequest request,
            CancellationToken cancellationToken = default)
        {
            LastRenamedMailbox = request.NewName;
            return Task.FromResult(new ImapMailboxRenameResult(ImapMailboxRenameDisposition.Renamed));
        }

        public Task<ImapMailboxDeleteResult> DeleteMailboxAsync(
            ImapMailboxDeleteRequest request,
            CancellationToken cancellationToken = default)
        {
            LastDeletedMailbox = request.MailboxName;
            return Task.FromResult(new ImapMailboxDeleteResult(
                ImapMailboxDeleteDisposition.Deleted, Guid.CreateVersion7()));
        }

        public Task<ImapMailboxSelectResult> SelectMailboxAsync(
            ImapMailboxSelectRequest request,
            CancellationToken cancellationToken = default)
        {
            LastSelectedMailbox = request.MailboxName;
            return Task.FromResult(new ImapMailboxSelectResult(new ImapSelectedMailbox(
                Guid.CreateVersion7(), 1, 3, 5, "mailbox-id", 2, 1,
                ["$Label1"], [77],
                [new ImapChangedMessage(1, 1, 3, false, false, false, false, false, ["$Label1"])])));
        }

        public Task<ImapQuotaResult> GetQuotaAsync(
            ImapQuotaRequest request,
            CancellationToken cancellationToken = default)
        {
            LastQuotaMailbox = request.MailboxName;
            return Task.FromResult(new ImapQuotaResult(true, 12, 2048));
        }

        public Task<ImapAppendPreflightResult> CheckAppendCapacityAsync(
            ImapAppendPreflightRequest request,
            CancellationToken cancellationToken = default)
        {
            LastAppendBytes = request.AddedBytes;
            return Task.FromResult(new ImapAppendPreflightResult(
                ImapAppendPreflightDisposition.Ready));
        }

        public Task<ImapAppendResult> AppendMessagesAsync(
            ImapAppendRequest request,
            CancellationToken cancellationToken = default)
        {
            LastAppendMailbox = request.MailboxName;
            return Task.FromResult(new ImapAppendResult(
                ImapAppendDisposition.Appended, 1, [2]));
        }

        public Task<ImapSearchResult> SearchMessagesAsync(
            ImapSearchRequest request,
            CancellationToken cancellationToken = default)
        {
            LastSearchCriteria = request.Criteria;
            return Task.FromResult(new ImapSearchResult(
                true, null, [new ImapSearchMatch(7, 2)], null));
        }

        public Task<ImapSortResult> SortMessagesAsync(
            ImapSortRequest request,
            CancellationToken cancellationToken = default)
        {
            LastSortKey = request.SortCriteria[0].Key;
            return Task.FromResult(new ImapSortResult(
                true, null, [new ImapSearchMatch(7, 2)], null));
        }

        public ImapThreadAlgorithm? LastThreadAlgorithm { get; private set; }

        public Task<ImapThreadResult> ThreadMessagesAsync(
            ImapThreadRequest request,
            CancellationToken cancellationToken = default)
        {
            LastThreadAlgorithm = request.Algorithm;
            return Task.FromResult(new ImapThreadResult(
                true, null, [new ImapThreadNode(7, -1)]));
        }

        public Task<ImapMarkSeenResult> MarkMessagesSeenAsync(
            ImapMarkSeenRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ImapMarkSeenResult(true,
                [new ImapSeenMessage(request.MessageIds[0], true, 7)]));

        public bool LastFetchIncludedContent { get; private set; }

        public Task<ImapFetchPageResult> GetFetchPageAsync(
            ImapFetchPageRequest request,
            CancellationToken cancellationToken = default)
        {
            LastFetchIncludedContent = request.IncludeStoredContent;
            return Task.FromResult(new ImapFetchPageResult(true, 7, 7, 7, false, []));
        }

        public Task<ImapIdleSnapshotResult> GetIdleSnapshotAsync(
            ImapIdleSnapshotRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ImapIdleSnapshotResult(true, 5,
                [new ImapIdleMessage(Guid.CreateVersion7(), 1, 5,
                    false, false, false, false, false, ["$Label1"])]));

        public Task<ImapExpungeResult> ExpungeDeletedAsync(
            ImapExpungeRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ImapExpungeResult(true, []));

        public Task<ImapStoreResult> StoreFlagsAsync(
            ImapStoreRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ImapStoreResult(ImapStoreDisposition.Stored, [], []));

        public Task<ImapMoveResult> MoveMessagesAsync(
            ImapMoveRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ImapMoveResult(ImapMoveDisposition.Moved, 1, [], [], []));

        public Task<ImapCopyResult> CopyMessagesAsync(
            ImapCopyRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ImapCopyResult(ImapCopyDisposition.Copied, 1, [], []));
    }
}
