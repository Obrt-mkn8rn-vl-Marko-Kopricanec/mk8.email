using System.Net.Http;
using System.Text;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Logging.Abstractions;
using mk8.email.Application.Worker;
using mk8.email.Configuration;
using mk8.email.Contracts.Messaging;
using mk8.email.Gateway.Protocols;
using mk8.email.Gateway.Protocols.Jmap;
using mk8.email.Smtp.Presentation;
using mk8.email.Storage;
using Npgsql;

namespace mk8.email.Messaging.Tests;

[TestClass]
[TestCategory("PostgreSQL")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1812", Justification = "MSTest DiscoverInternals instantiates this test class by reflection; focused discovery is verified by executed test counts.")]
internal sealed class SmtpPresentationTransportTests
{
    [TestMethod]
    public async Task UnavailableGatewayReturnsTemporaryDeliveryFailure()
    {
        var application = new OutboundSmtpPresentationClient(
            new FailingPresentationClient(),
            new EnvironmentConfig(),
            NullLogger<OutboundSmtpPresentationClient>.Instance);

        var result = await application.RelayAsync(
            "sender@example.test",
            "recipient@remote.test",
            "Subject: retry\r\n\r\nbody\r\n").ConfigureAwait(false);

        Assert.AreEqual(OutboundDeliveryStatus.TemporaryFailure, result.Status);
        Assert.AreEqual("4.4.2", result.EnhancedStatusCode, StringComparer.Ordinal);
    }

    [TestMethod]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "MA0051", Justification = "The SlowSmtpDeliveryDoesNotBlockAnotherGatewayPresentationRequest scenario keeps one fixture's ordered setup, operation and invariant assertions together.")]
    public async Task SlowSmtpDeliveryDoesNotBlockAnotherGatewayPresentationRequest()
    {
        var database = (await PostgresTestDatabase.TryCreateAsync().ConfigureAwait(false));
        await using var databaseLifetime = new NullableAsyncDisposable(database).ConfigureAwait(false);
        if (database is null)
        {
            Assert.Inconclusive("Set MK8_EMAIL_TEST_POSTGRES to a PostgreSQL admin connection string.");
            return;
        }

        var applicationDataSource = NpgsqlDataSource.Create(database.ConnectionString);
        await using var applicationDataSourceLifetime = applicationDataSource.ConfigureAwait(false);
        var gatewayDataSource = NpgsqlDataSource.Create(database.ConnectionString);
        await using var gatewayDataSourceLifetime = gatewayDataSource.ConfigureAwait(false);
        await PostgresMessagingSchema.EnsureAsync(gatewayDataSource).ConfigureAwait(false);
        using var applicationProtector = AesGcmPayloadProtectorTests.CreateProtector(
            "test", "smtp-concurrency-key");
        using var gatewayProtector = AesGcmPayloadProtectorTests.CreateProtector(
            "test", "smtp-concurrency-key");
        var options = new PostgresMessagingOptions
        {
            NotificationFallbackInterval = TimeSpan.FromSeconds(1),
        };
        var applicationBus = new PostgresPresentationBus(
            applicationDataSource, applicationProtector, options);
        var gatewayBus = new PostgresPresentationBus(
            gatewayDataSource, gatewayProtector, options);
        var journal = new PostgresGatewayTrafficJournal(
            gatewayDataSource, gatewayProtector, options);
        using var ownedResource1 = new HttpClientHandler();
        using var webPush = new GatewayWebPushService(
            ownedResource1, journal, options.MaxPayloadBytes);
        var smtp = new BlockingSmtpRelay();
        var gatewayWorker = new GatewayPresentationWorker(
            gatewayBus,
            new PostgresApplicationTransportControl(gatewayDataSource),
            journal,
            webPush,
            smtp,
            new EnvironmentConfig(),
            NullLogger<GatewayPresentationWorker>.Instance);
        var application = new OutboundSmtpPresentationClient(
            applicationBus,
            new EnvironmentConfig(),
            NullLogger<OutboundSmtpPresentationClient>.Instance);
        await gatewayWorker.StartAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var first = application.RelayAsync(
                "sender@example.test",
                "first@remote.test",
                "Subject: first\r\n\r\nbody\r\n",
                cancellationToken: timeout.Token);
            await smtp.FirstEntered.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
            var second = application.RelayAsync(
                "sender@example.test",
                "second@remote.test",
                "Subject: second\r\n\r\nbody\r\n",
                cancellationToken: timeout.Token);

            Assert.AreEqual(
                OutboundDeliveryStatus.Delivered,
                (await second.WaitAsync(timeout.Token).ConfigureAwait(false)).Status);
            Assert.IsFalse(first.IsCompleted);
            smtp.ReleaseFirst.TrySetResult();
            Assert.AreEqual(
                OutboundDeliveryStatus.Delivered,
                (await first.WaitAsync(timeout.Token).ConfigureAwait(false)).Status);
        }
        finally
        {
            smtp.ReleaseFirst.TrySetResult();
            await gatewayWorker.StopAsync(CancellationToken.None).ConfigureAwait(false);
            gatewayWorker.Dispose();
        }
    }

    [TestMethod]
    [TestCategory("AzureBlobCompatible")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "MA0051", Justification = "The LargeOutboundMessageAndGatewayJournalUseAzureBlobObjects scenario keeps one fixture's ordered setup, operation and invariant assertions together.")]
    public async Task LargeOutboundMessageAndGatewayJournalUseAzureBlobObjects()
    {
        var blobConnection = Environment.GetEnvironmentVariable(
            "MK8_EMAIL_TEST_AZURE_BLOB_CONNECTION");
        if (string.IsNullOrWhiteSpace(blobConnection))
        {
            Assert.Inconclusive(
                "Set MK8_EMAIL_TEST_AZURE_BLOB_CONNECTION to an Azure Blob-compatible test endpoint.");
            return;
        }
        var database = (await PostgresTestDatabase.TryCreateAsync().ConfigureAwait(false));
        await using var databaseLifetime = new NullableAsyncDisposable(database).ConfigureAwait(false);
        if (database is null)
        {
            Assert.Inconclusive("Set MK8_EMAIL_TEST_POSTGRES to a PostgreSQL admin connection string.");
            return;
        }

        var serviceClient = new BlobServiceClient(blobConnection);
        var containerName = "mk8-smtp-" + Guid.NewGuid().ToString("N");
        var container = serviceClient.GetBlobContainerClient(containerName);
        var objects = new AzureBlobLargeObjectStore(
            serviceClient,
            new AzureBlobLargeObjectStoreOptions
            {
                ContainerName = containerName,
                ObjectPrefix = "presentation-test",
                CreateContainerIfMissing = true,
            });
        try
        {
            var applicationDataSource = NpgsqlDataSource.Create(database.ConnectionString);
            await using var applicationDataSourceLifetime = applicationDataSource.ConfigureAwait(false);
            var gatewayDataSource = NpgsqlDataSource.Create(database.ConnectionString);
            await using var gatewayDataSourceLifetime = gatewayDataSource.ConfigureAwait(false);
            await PostgresMessagingSchema.EnsureAsync(gatewayDataSource).ConfigureAwait(false);
            using var applicationProtector = AesGcmPayloadProtectorTests.CreateProtector(
                "test", "smtp-large-blob-key");
            using var gatewayProtector = AesGcmPayloadProtectorTests.CreateProtector(
                "test", "smtp-large-blob-key");
            var options = new PostgresMessagingOptions
            {
                InlinePayloadThresholdBytes = 256,
                NotificationFallbackInterval = TimeSpan.FromSeconds(1),
            };
            var applicationBus = new PostgresPresentationBus(
                applicationDataSource, applicationProtector, options, largeObjectStore: objects);
            var gatewayBus = new PostgresPresentationBus(
                gatewayDataSource, gatewayProtector, options, largeObjectStore: objects);
            var journal = new PostgresGatewayTrafficJournal(
                gatewayDataSource, gatewayProtector, options, largeObjectStore: objects);
            using var ownedResource2 = new HttpClientHandler();
            using var webPush = new GatewayWebPushService(
                ownedResource2, journal, options.MaxPayloadBytes);
            var smtp = new RecordingSmtpRelay();
            var gatewayWorker = new GatewayPresentationWorker(
                gatewayBus,
                new PostgresApplicationTransportControl(gatewayDataSource),
                journal,
                webPush,
                smtp,
                new EnvironmentConfig(),
                NullLogger<GatewayPresentationWorker>.Instance);
            var application = new OutboundSmtpPresentationClient(
                applicationBus,
                new EnvironmentConfig(),
                NullLogger<OutboundSmtpPresentationClient>.Instance);
            await gatewayWorker.StartAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                var rawMessage = "Subject: large\r\n\r\n" + new string('x', 350_000) + "\r\n";
                var result = await application.RelayAsync(
                    "sender@example.test",
                    "recipient@remote.test",
                    rawMessage,
                    cancellationToken: timeout.Token).ConfigureAwait(false);
                Assert.AreEqual(OutboundDeliveryStatus.Delivered, result.Status);
                Assert.AreEqual(rawMessage, smtp.Request?.RawMessage, StringComparer.Ordinal);

                var requestQuery = gatewayDataSource.CreateCommand(
                    "SELECT count(*) FROM presentation_requests "
                    + "WHERE operation = @operation AND request_payload_inline IS NULL "
                    + "AND request_payload_blob_provider = 'azure-blob'");
                await using var requestQueryLifetime = requestQuery.ConfigureAwait(false);
                requestQuery.Parameters.AddWithValue("operation", SmtpPresentationOperations.Relay);
                Assert.AreEqual(1L, await requestQuery.ExecuteScalarAsync(timeout.Token).ConfigureAwait(false));

                var journalQuery = gatewayDataSource.CreateCommand(
                    "SELECT count(*) FROM gateway_traffic_records "
                    + "WHERE protocol = 'smtp' AND direction = 'inbound' "
                    + "AND payload_inline IS NULL AND payload_blob_provider = 'azure-blob'");
                await using var journalQueryLifetime = journalQuery.ConfigureAwait(false);
                Assert.AreEqual(1L, await journalQuery.ExecuteScalarAsync(timeout.Token).ConfigureAwait(false));
            }
            finally
            {
                await gatewayWorker.StopAsync(CancellationToken.None).ConfigureAwait(false);
                gatewayWorker.Dispose();
            }
        }
        finally
        {
            await container.DeleteIfExistsAsync().ConfigureAwait(false);
        }
    }

    [TestMethod]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "MA0051", Justification = "The WorkerMailDeliveryCrossesReverseLaneToGatewayWithoutExposingMessage scenario keeps one fixture's ordered setup, operation and invariant assertions together.")]
    public async Task WorkerMailDeliveryCrossesReverseLaneToGatewayWithoutExposingMessage()
    {
        var database = (await PostgresTestDatabase.TryCreateAsync().ConfigureAwait(false));
        await using var databaseLifetime = new NullableAsyncDisposable(database).ConfigureAwait(false);
        if (database is null)
        {
            Assert.Inconclusive("Set MK8_EMAIL_TEST_POSTGRES to a PostgreSQL admin connection string.");
            return;
        }

        var applicationDataSource = NpgsqlDataSource.Create(database.ConnectionString);
        await using var applicationDataSourceLifetime = applicationDataSource.ConfigureAwait(false);
        var gatewayDataSource = NpgsqlDataSource.Create(database.ConnectionString);
        await using var gatewayDataSourceLifetime = gatewayDataSource.ConfigureAwait(false);
        await PostgresMessagingSchema.EnsureAsync(gatewayDataSource).ConfigureAwait(false);
        using var applicationProtector = AesGcmPayloadProtectorTests.CreateProtector(
            "test", "smtp-presentation-key");
        using var gatewayProtector = AesGcmPayloadProtectorTests.CreateProtector(
            "test", "smtp-presentation-key");
        var options = new PostgresMessagingOptions
        {
            NotificationFallbackInterval = TimeSpan.FromSeconds(1),
        };
        var applicationBus = new PostgresPresentationBus(
            applicationDataSource, applicationProtector, options);
        var gatewayBus = new PostgresPresentationBus(
            gatewayDataSource, gatewayProtector, options);
        var journal = new PostgresGatewayTrafficJournal(
            gatewayDataSource, gatewayProtector, options);
        using var ownedResource3 = new HttpClientHandler();
        using var webPush = new GatewayWebPushService(
            ownedResource3, journal, options.MaxPayloadBytes);
        var smtp = new RecordingSmtpRelay();
        var gatewayWorker = new GatewayPresentationWorker(
            gatewayBus,
            new PostgresApplicationTransportControl(gatewayDataSource),
            journal,
            webPush,
            smtp,
            new EnvironmentConfig(),
            NullLogger<GatewayPresentationWorker>.Instance);
        var application = new OutboundSmtpPresentationClient(
            applicationBus,
            new EnvironmentConfig(),
            NullLogger<OutboundSmtpPresentationClient>.Instance);

        await gatewayWorker.StartAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            const string rawMessage = "Subject: reverse lane\r\n\r\n\u00ff\r\n";
            var delivery = await application.RelayAsync(
                "sender@example.test",
                "recipient@remote.test",
                rawMessage,
                new OutboundMailOptions(
                    RequiresSmtpUtf8: true,
                    new MailDsnEnvelope("HDRS", "job+2B42")),
                timeout.Token).ConfigureAwait(false);

            Assert.AreEqual(OutboundDeliveryStatus.Delivered, delivery.Status);
            Assert.IsNotNull(smtp.Request);
            Assert.AreEqual(rawMessage, smtp.Request.RawMessage, StringComparer.Ordinal);
            Assert.AreEqual("sender@example.test", smtp.Request.Sender, StringComparer.Ordinal);
            Assert.AreEqual("recipient@remote.test", smtp.Request.Recipient, StringComparer.Ordinal);
            Assert.AreEqual("job+2B42", smtp.Request.Options?.Dsn?.EnvelopeId, StringComparer.Ordinal);

            Guid requestId;
            {
                var requestQuery = gatewayDataSource.CreateCommand(
                "SELECT id, request_payload_inline FROM presentation_requests WHERE operation = @operation");
                await using var requestQueryLifetime = requestQuery.ConfigureAwait(false);
                requestQuery.Parameters.AddWithValue("operation", SmtpPresentationOperations.Relay);
                var requestReader = (await requestQuery.ExecuteReaderAsync(timeout.Token).ConfigureAwait(false));
                await using var requestReaderLifetime = requestReader.ConfigureAwait(false);
                Assert.IsTrue(await requestReader.ReadAsync(timeout.Token).ConfigureAwait(false));
                requestId = requestReader.GetGuid(0);
                Assert.AreEqual(requestId, smtp.ApplicationRequestId);
                var ciphertext = await (requestReader.GetFieldValueAsync<byte[]>(1)).ConfigureAwait(false);
                Assert.IsFalse(Encoding.UTF8.GetString(ciphertext).Contains("reverse lane", StringComparison.Ordinal));
            }

            var trafficQuery = gatewayDataSource.CreateCommand(
                "SELECT DISTINCT session_id FROM gateway_traffic_records "
                + "WHERE application_request_id = @request_id");
            await using var trafficQueryLifetime = trafficQuery.ConfigureAwait(false);
            trafficQuery.Parameters.AddWithValue("request_id", requestId);
            var sessionId = (Guid)(await trafficQuery.ExecuteScalarAsync(timeout.Token).ConfigureAwait(false)
                ?? throw new AssertFailedException("The Gateway journal session is missing."));
            var records = await journal.ReadSessionAsync(sessionId, timeout.Token).ConfigureAwait(false);
            Assert.HasCount(2, records);
            CollectionAssert.AreEqual(
                new[] { GatewayTrafficDirections.Inbound, GatewayTrafficDirections.Outbound },
                records.Select(record => record.Direction).ToArray());
            Assert.IsFalse(records[0].Metadata.ContainsKey("rawMessage"));
        }
        finally
        {
            await gatewayWorker.StopAsync(CancellationToken.None).ConfigureAwait(false);
            gatewayWorker.Dispose();
        }
    }

    private sealed class RecordingSmtpRelay : ISmtpPresentationRelay
    {
        public SmtpRelayPresentationRequest? Request { get; private set; }
        public Guid ApplicationRequestId { get; private set; }

        public Task<OutboundDeliveryResult> RelayAsync(
            SmtpRelayPresentationRequest request,
            Guid applicationRequestId,
            CancellationToken cancellationToken)
        {
            Request = request;
            ApplicationRequestId = applicationRequestId;
            return Task.FromResult(new OutboundDeliveryResult(
                OutboundDeliveryStatus.Delivered,
                "The remote mail server accepted the message.",
                RemoteMta: "mx.remote.test"));
        }
    }

    private sealed class BlockingSmtpRelay : ISmtpPresentationRelay
    {
        public TaskCompletionSource FirstEntered { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirst { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<OutboundDeliveryResult> RelayAsync(
            SmtpRelayPresentationRequest request,
            Guid applicationRequestId,
            CancellationToken cancellationToken)
        {
            if (string.Equals(request.Recipient, "first@remote.test", StringComparison.Ordinal))
            {
                FirstEntered.TrySetResult();
                await ReleaseFirst.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            return new OutboundDeliveryResult(
                OutboundDeliveryStatus.Delivered,
                "The remote mail server accepted the message.");
        }
    }

    private sealed class FailingPresentationClient : IPresentationRequestClient
    {
        public Task EnqueueAsync(
            ApplicationRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ApplicationResponse> WaitForResponseAsync(
            Guid requestId,
            DateTimeOffset deadline,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ApplicationResponse> SendAsync(
            ApplicationRequest request,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Gateway is unavailable.");

        public Task<ApplicationExchangeSnapshot?> GetAsync(
            Guid requestId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
