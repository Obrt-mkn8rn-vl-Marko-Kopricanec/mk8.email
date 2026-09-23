using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using mk8.email.Application.Interfaces;
using mk8.email.Application.Services;
using mk8.email.Application.Worker;
using mk8.email.Contracts.Imap;
using mk8.email.Contracts.Messaging;
using mk8.email.Gateway.ApplicationBridge;
using mk8.email.Gateway.Protocols.Imap;
using Npgsql;

namespace mk8.email.Messaging.Tests;

[TestClass]
[TestCategory("PostgreSQL")]
public sealed class ImapGatewayTransportTests
{
    [TestMethod]
    [Timeout(20_000)]
    public async Task ImapAuthenticationUsesRemoteWorkerAndEncryptedTrafficRecords()
    {
        await using var database = await PostgresTestDatabase.TryCreateAsync();
        if (database is null)
        {
            Assert.Inconclusive("Set MK8_EMAIL_TEST_POSTGRES to a PostgreSQL admin connection string.");
            return;
        }

        await using var gatewayDataSource = NpgsqlDataSource.Create(database.ConnectionString);
        await using var workerDataSource = NpgsqlDataSource.Create(database.ConnectionString);
        await PostgresMessagingSchema.EnsureAsync(gatewayDataSource);
        using var gatewayProtector = AesGcmPayloadProtectorTests.CreateProtector(
            "test", "imap-auth-key");
        using var workerProtector = AesGcmPayloadProtectorTests.CreateProtector(
            "test", "imap-auth-key");
        var options = new PostgresMessagingOptions
        {
            NotificationFallbackInterval = TimeSpan.FromSeconds(1),
        };
        var gatewayBus = new PostgresApplicationBus(
            gatewayDataSource, gatewayProtector, options);
        var workerBus = new PostgresApplicationBus(
            workerDataSource, workerProtector, options);
        var journal = new PostgresGatewayTrafficJournal(
            gatewayDataSource, gatewayProtector, options);
        var application = new RecordingImapApplication();
        await using var workerProvider = new ServiceCollection()
            .AddSingleton<IImapApplicationService>(application)
            .AddScoped<IApplicationRequestDispatcher>(provider =>
                new ApplicationRequestDispatcher(provider))
            .BuildServiceProvider();
        var worker = new ApplicationRequestWorker(
            workerBus,
            workerProvider.GetRequiredService<IServiceScopeFactory>(),
            new ApplicationWorkerIdentity("application@imap-test-host", TimeSpan.FromSeconds(30)),
            NullLogger<ApplicationRequestWorker>.Instance);
        var transport = new GatewayApplicationTransport(
            gatewayBus,
            journal,
            new GatewayApplicationOptions(
                "gateway@imap-test-host", TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5)));
        var client = new GatewayImapApplicationService(transport);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await worker.StartAsync(timeout.Token);
        try
        {
            var password = await client.AuthenticatePasswordAsync(
                new ImapPasswordAuthentication("user@example.test", "imap-secret"), timeout.Token);
            var oauth = await client.AuthenticateOAuthAsync(
                new ImapOAuthAuthentication("user@example.test", "imap-access-token"), timeout.Token);
            var mailboxes = await client.ListMailboxesAsync(
                new ImapMailboxListRequest(application.UserId, SubscribedOnly: true), timeout.Token);
            var statuses = await client.GetMailboxStatusesAsync(
                new ImapMailboxStatusRequest(application.UserId, ["INBOX"], true, true, true),
                timeout.Token);
            var subscription = await client.SetMailboxSubscriptionAsync(
                new ImapMailboxSubscriptionRequest(application.UserId, "INBOX", false),
                timeout.Token);
            var created = await client.CreateMailboxAsync(
                new ImapMailboxCreateRequest(application.UserId, "Projects"), timeout.Token);
            var renamed = await client.RenameMailboxAsync(
                new ImapMailboxRenameRequest(application.UserId, "Projects", "Archive"),
                timeout.Token);
            var deleted = await client.DeleteMailboxAsync(
                new ImapMailboxDeleteRequest(application.UserId, "Archive"), timeout.Token);
            var selected = await client.SelectMailboxAsync(
                new ImapMailboxSelectRequest(application.UserId, "INBOX", 1, 2), timeout.Token);
            var quota = await client.GetQuotaAsync(
                new ImapQuotaRequest(application.UserId, "INBOX"), timeout.Token);
            var idle = await client.GetIdleSnapshotAsync(
                new ImapIdleSnapshotRequest(application.UserId, Guid.CreateVersion7()),
                timeout.Token);
            var expunged = await client.ExpungeDeletedAsync(
                new ImapExpungeRequest(application.UserId, Guid.CreateVersion7(),
                    new ImapUidSelection([new ImapUidRange(2, null)], null)), timeout.Token);
            var stored = await client.StoreFlagsAsync(new ImapStoreRequest(
                application.UserId,
                Guid.CreateVersion7(),
                true,
                new ImapMessageSelection([new ImapMessageRange(1, null)], null),
                5,
                ImapFlagMutationMode.Add,
                ["\\Seen", "$Label1"]), timeout.Token);
            var moved = await client.MoveMessagesAsync(new ImapMoveRequest(
                application.UserId,
                Guid.CreateVersion7(),
                "Archive",
                true,
                new ImapMessageSelection([new ImapMessageRange(1, null)], null)), timeout.Token);
            Assert.AreEqual(application.UserId, password.UserId);
            Assert.AreEqual(application.UserId, oauth.UserId);
            Assert.AreEqual("imap-secret", application.Password);
            Assert.AreEqual("imap-access-token", application.AccessToken);
            Assert.HasCount(1, mailboxes.Mailboxes);
            Assert.AreEqual("INBOX", mailboxes.Mailboxes[0].FolderName);
            Assert.AreEqual(2, statuses.Statuses["INBOX"].MessageCount);
            Assert.IsTrue(subscription.Found);
            Assert.IsFalse(application.IsSubscribed);
            Assert.AreEqual(ImapMailboxCreateDisposition.Created, created.Disposition);
            Assert.AreEqual("Projects", application.CreatedMailbox);
            Assert.AreEqual(ImapMailboxRenameDisposition.Renamed, renamed.Disposition);
            Assert.AreEqual("Archive", application.RenamedMailbox);
            Assert.AreEqual(ImapMailboxDeleteDisposition.Deleted, deleted.Disposition);
            Assert.AreEqual("Archive", application.DeletedMailbox);
            Assert.IsNotNull(selected.Mailbox);
            Assert.AreEqual(2, selected.Mailbox.MessageCount);
            Assert.AreEqual("INBOX", application.SelectedMailbox);
            Assert.AreEqual(2048L, quota.LimitBytes);
            Assert.AreEqual("INBOX", application.QuotaMailbox);
            Assert.IsTrue(idle.FolderFound);
            Assert.HasCount(1, idle.Messages);
            Assert.IsTrue(expunged.FolderFound);
            Assert.HasCount(1, expunged.Messages);
            Assert.AreEqual(2, expunged.Messages[0].SequenceNumber);
            var selectedUidRanges = application.LastExpungeSelection?.Ranges;
            Assert.IsNotNull(selectedUidRanges);
            Assert.AreEqual(2, selectedUidRanges[0].Start);
            Assert.IsNull(selectedUidRanges[0].End);
            Assert.AreEqual(ImapStoreDisposition.Stored, stored.Disposition);
            var recordedStore = application.LastStoreRequest;
            Assert.IsNotNull(recordedStore);
            Assert.AreEqual(ImapFlagMutationMode.Add, recordedStore.Mode);
            Assert.AreEqual(5L, recordedStore.UnchangedSince);
            CollectionAssert.AreEqual(new[] { "\\Seen", "$Label1" }, recordedStore.Flags);
            Assert.AreEqual(ImapMoveDisposition.Moved, moved.Disposition);
            Assert.AreEqual("Archive", application.LastMoveRequest?.DestinationMailboxName);

            await using var operations = gatewayDataSource.CreateCommand(
                "SELECT operation FROM application_requests ORDER BY created_at");
            await using var operationReader = await operations.ExecuteReaderAsync(timeout.Token);
            var observed = new List<string>();
            while (await operationReader.ReadAsync(timeout.Token))
                observed.Add(operationReader.GetString(0));
            CollectionAssert.AreEqual(
                new[]
                {
                    ApplicationOperations.ImapAuthenticatePassword,
                    ApplicationOperations.ImapAuthenticateOAuth,
                    ApplicationOperations.ImapListMailboxes,
                    ApplicationOperations.ImapGetMailboxStatuses,
                    ApplicationOperations.ImapSetMailboxSubscription,
                    ApplicationOperations.ImapCreateMailbox,
                    ApplicationOperations.ImapRenameMailbox,
                    ApplicationOperations.ImapDeleteMailbox,
                    ApplicationOperations.ImapSelectMailbox,
                    ApplicationOperations.ImapGetQuota,
                    ApplicationOperations.ImapGetIdleSnapshot,
                    ApplicationOperations.ImapExpungeDeleted,
                    ApplicationOperations.ImapStoreFlags,
                    ApplicationOperations.ImapMoveMessages,
                },
                observed);

            await using var records = gatewayDataSource.CreateCommand(
                "SELECT payload_inline FROM gateway_traffic_records "
                + "WHERE protocol = 'imap' AND application_request_id IS NOT NULL");
            await using var recordReader = await records.ExecuteReaderAsync(timeout.Token);
            var recordCount = 0;
            while (await recordReader.ReadAsync(timeout.Token))
            {
                recordCount++;
                var ciphertext = Encoding.UTF8.GetString(recordReader.GetFieldValue<byte[]>(0));
                Assert.IsFalse(ciphertext.Contains("imap-secret", StringComparison.Ordinal));
                Assert.IsFalse(ciphertext.Contains("imap-access-token", StringComparison.Ordinal));
            }
            Assert.AreEqual(28, recordCount);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
            worker.Dispose();
        }
    }

    private sealed class RecordingImapApplication : IImapApplicationService
    {
        public Guid UserId { get; } = Guid.CreateVersion7();
        public string? Password { get; private set; }
        public string? AccessToken { get; private set; }
        public bool IsSubscribed { get; private set; } = true;
        public string? CreatedMailbox { get; private set; }
        public string? RenamedMailbox { get; private set; }
        public string? DeletedMailbox { get; private set; }
        public string? SelectedMailbox { get; private set; }
        public string? QuotaMailbox { get; private set; }
        public ImapUidSelection? LastExpungeSelection { get; private set; }
        public ImapStoreRequest? LastStoreRequest { get; private set; }
        public ImapMoveRequest? LastMoveRequest { get; private set; }

        public Task<ImapIdentityResult> AuthenticatePasswordAsync(
            ImapPasswordAuthentication request,
            CancellationToken cancellationToken = default)
        {
            Password = request.Password;
            return Task.FromResult(new ImapIdentityResult(UserId, request.Username));
        }

        public Task<ImapIdentityResult> AuthenticateOAuthAsync(
            ImapOAuthAuthentication request,
            CancellationToken cancellationToken = default)
        {
            AccessToken = request.AccessToken;
            return Task.FromResult(new ImapIdentityResult(UserId, request.Username));
        }

        public Task<ImapMailboxListResult> ListMailboxesAsync(
            ImapMailboxListRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ImapMailboxListResult(
                [new ImapMailboxInfo("user", "example.test", "INBOX", true, true)]));

        public Task<ImapMailboxStatusResult> GetMailboxStatusesAsync(
            ImapMailboxStatusRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ImapMailboxStatusResult(new Dictionary<string, ImapMailboxStatus>
            {
                ["INBOX"] = new(Guid.CreateVersion7(), 1, 3, 5, "mailbox-id", 2, 1, 12),
            }));

        public Task<ImapMailboxSubscriptionResult> SetMailboxSubscriptionAsync(
            ImapMailboxSubscriptionRequest request,
            CancellationToken cancellationToken = default)
        {
            IsSubscribed = request.IsSubscribed;
            return Task.FromResult(new ImapMailboxSubscriptionResult(true));
        }

        public Task<ImapMailboxCreateResult> CreateMailboxAsync(
            ImapMailboxCreateRequest request,
            CancellationToken cancellationToken = default)
        {
            CreatedMailbox = request.MailboxName;
            return Task.FromResult(new ImapMailboxCreateResult(
                ImapMailboxCreateDisposition.Created, Guid.CreateVersion7(), "mailbox-id"));
        }

        public Task<ImapMailboxRenameResult> RenameMailboxAsync(
            ImapMailboxRenameRequest request,
            CancellationToken cancellationToken = default)
        {
            RenamedMailbox = request.NewName;
            return Task.FromResult(new ImapMailboxRenameResult(ImapMailboxRenameDisposition.Renamed));
        }

        public Task<ImapMailboxDeleteResult> DeleteMailboxAsync(
            ImapMailboxDeleteRequest request,
            CancellationToken cancellationToken = default)
        {
            DeletedMailbox = request.MailboxName;
            return Task.FromResult(new ImapMailboxDeleteResult(
                ImapMailboxDeleteDisposition.Deleted, Guid.CreateVersion7()));
        }

        public Task<ImapMailboxSelectResult> SelectMailboxAsync(
            ImapMailboxSelectRequest request,
            CancellationToken cancellationToken = default)
        {
            SelectedMailbox = request.MailboxName;
            return Task.FromResult(new ImapMailboxSelectResult(new ImapSelectedMailbox(
                Guid.CreateVersion7(), 1, 3, 5, "mailbox-id", 2, 1,
                ["$Label1"], [77],
                [new ImapChangedMessage(1, 1, 3, false, false, false, false, false, ["$Label1"])])));
        }

        public Task<ImapQuotaResult> GetQuotaAsync(
            ImapQuotaRequest request,
            CancellationToken cancellationToken = default)
        {
            QuotaMailbox = request.MailboxName;
            return Task.FromResult(new ImapQuotaResult(true, 12, 2048));
        }

        public Task<ImapIdleSnapshotResult> GetIdleSnapshotAsync(
            ImapIdleSnapshotRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ImapIdleSnapshotResult(true, 5,
                [new ImapIdleMessage(Guid.CreateVersion7(), 1, 5,
                    false, false, false, false, false, ["$Label1"])]));

        public Task<ImapExpungeResult> ExpungeDeletedAsync(
            ImapExpungeRequest request,
            CancellationToken cancellationToken = default)
        {
            LastExpungeSelection = request.UidSelection;
            return Task.FromResult(new ImapExpungeResult(true,
                [new ImapExpungedMessage(2, 7)]));
        }

        public Task<ImapStoreResult> StoreFlagsAsync(
            ImapStoreRequest request,
            CancellationToken cancellationToken = default)
        {
            LastStoreRequest = request;
            return Task.FromResult(new ImapStoreResult(ImapStoreDisposition.Stored, [], []));
        }

        public Task<ImapMoveResult> MoveMessagesAsync(
            ImapMoveRequest request,
            CancellationToken cancellationToken = default)
        {
            LastMoveRequest = request;
            return Task.FromResult(new ImapMoveResult(ImapMoveDisposition.Moved, 1,
                [2], [7], [2]));
        }
    }
}
