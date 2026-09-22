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
public sealed class SmtpPresentationTransportTests
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
            "Subject: retry\r\n\r\nbody\r\n");

        Assert.AreEqual(OutboundDeliveryStatus.TemporaryFailure, result.Status);
        Assert.AreEqual("4.4.2", result.EnhancedStatusCode);
    }

    [TestMethod]
    public async Task SlowSmtpDeliveryDoesNotBlockAnotherGatewayPresentationRequest()
    {
        await using var database = await PostgresTestDatabase.TryCreateAsync();
        if (database is null)
        {
            Assert.Inconclusive("Set MK8_EMAIL_TEST_POSTGRES to a PostgreSQL admin connection string.");
            return;
        }

        await using var applicationDataSource = NpgsqlDataSource.Create(database.ConnectionString);
        await using var gatewayDataSource = NpgsqlDataSource.Create(database.ConnectionString);
        await PostgresMessagingSchema.EnsureAsync(gatewayDataSource);
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
        using var webPush = new GatewayWebPushService(
            new HttpClientHandler(), journal, options.MaxPayloadBytes);
        var smtp = new BlockingSmtpRelay();
        var gatewayWorker = new GatewayPresentationWorker(
            gatewayBus,
            journal,
            webPush,
            smtp,
            new EnvironmentConfig(),
            NullLogger<GatewayPresentationWorker>.Instance);
        var application = new OutboundSmtpPresentationClient(
            applicationBus,
            new EnvironmentConfig(),
            NullLogger<OutboundSmtpPresentationClient>.Instance);
        await gatewayWorker.StartAsync(CancellationToken.None);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var first = application.RelayAsync(
                "sender@example.test",
                "first@remote.test",
                "Subject: first\r\n\r\nbody\r\n",
                cancellationToken: timeout.Token);
            await smtp.FirstEntered.Task.WaitAsync(timeout.Token);
            var second = application.RelayAsync(
                "sender@example.test",
                "second@remote.test",
                "Subject: second\r\n\r\nbody\r\n",
                cancellationToken: timeout.Token);

            Assert.AreEqual(
                OutboundDeliveryStatus.Delivered,
                (await second.WaitAsync(timeout.Token)).Status);
            Assert.IsFalse(first.IsCompleted);
            smtp.ReleaseFirst.TrySetResult();
            Assert.AreEqual(
                OutboundDeliveryStatus.Delivered,
                (await first.WaitAsync(timeout.Token)).Status);
        }
        finally
        {
            smtp.ReleaseFirst.TrySetResult();
            await gatewayWorker.StopAsync(CancellationToken.None);
            gatewayWorker.Dispose();
        }
    }

    [TestMethod]
    [TestCategory("AzureBlobCompatible")]
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
        await using var database = await PostgresTestDatabase.TryCreateAsync();
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
            await using var applicationDataSource = NpgsqlDataSource.Create(database.ConnectionString);
            await using var gatewayDataSource = NpgsqlDataSource.Create(database.ConnectionString);
            await PostgresMessagingSchema.EnsureAsync(gatewayDataSource);
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
            using var webPush = new GatewayWebPushService(
                new HttpClientHandler(), journal, options.MaxPayloadBytes);
            var smtp = new RecordingSmtpRelay();
            var gatewayWorker = new GatewayPresentationWorker(
                gatewayBus,
                journal,
                webPush,
                smtp,
                new EnvironmentConfig(),
                NullLogger<GatewayPresentationWorker>.Instance);
            var application = new OutboundSmtpPresentationClient(
                applicationBus,
                new EnvironmentConfig(),
                NullLogger<OutboundSmtpPresentationClient>.Instance);
            await gatewayWorker.StartAsync(CancellationToken.None);
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                var rawMessage = "Subject: large\r\n\r\n" + new string('x', 350_000) + "\r\n";
                var result = await application.RelayAsync(
                    "sender@example.test",
                    "recipient@remote.test",
                    rawMessage,
                    cancellationToken: timeout.Token);
                Assert.AreEqual(OutboundDeliveryStatus.Delivered, result.Status);
                Assert.AreEqual(rawMessage, smtp.Request?.RawMessage);

                await using var requestQuery = gatewayDataSource.CreateCommand(
                    "SELECT count(*) FROM presentation_requests "
                    + "WHERE operation = @operation AND request_payload_inline IS NULL "
                    + "AND request_payload_blob_provider = 'azure-blob'");
                requestQuery.Parameters.AddWithValue("operation", SmtpPresentationOperations.Relay);
                Assert.AreEqual(1L, await requestQuery.ExecuteScalarAsync(timeout.Token));

                await using var journalQuery = gatewayDataSource.CreateCommand(
                    "SELECT count(*) FROM gateway_traffic_records "
                    + "WHERE protocol = 'smtp' AND direction = 'inbound' "
                    + "AND payload_inline IS NULL AND payload_blob_provider = 'azure-blob'");
                Assert.AreEqual(1L, await journalQuery.ExecuteScalarAsync(timeout.Token));
            }
            finally
            {
                await gatewayWorker.StopAsync(CancellationToken.None);
                gatewayWorker.Dispose();
            }
        }
        finally
        {
            await container.DeleteIfExistsAsync();
        }
    }

    [TestMethod]
    public async Task WorkerMailDeliveryCrossesReverseLaneToGatewayWithoutExposingMessage()
    {
        await using var database = await PostgresTestDatabase.TryCreateAsync();
        if (database is null)
        {
            Assert.Inconclusive("Set MK8_EMAIL_TEST_POSTGRES to a PostgreSQL admin connection string.");
            return;
        }

        await using var applicationDataSource = NpgsqlDataSource.Create(database.ConnectionString);
        await using var gatewayDataSource = NpgsqlDataSource.Create(database.ConnectionString);
        await PostgresMessagingSchema.EnsureAsync(gatewayDataSource);
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
        using var webPush = new GatewayWebPushService(
            new HttpClientHandler(), journal, options.MaxPayloadBytes);
        var smtp = new RecordingSmtpRelay();
        var gatewayWorker = new GatewayPresentationWorker(
            gatewayBus,
            journal,
            webPush,
            smtp,
            new EnvironmentConfig(),
            NullLogger<GatewayPresentationWorker>.Instance);
        var application = new OutboundSmtpPresentationClient(
            applicationBus,
            new EnvironmentConfig(),
            NullLogger<OutboundSmtpPresentationClient>.Instance);

        await gatewayWorker.StartAsync(CancellationToken.None);
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
                timeout.Token);

            Assert.AreEqual(OutboundDeliveryStatus.Delivered, delivery.Status);
            Assert.IsNotNull(smtp.Request);
            Assert.AreEqual(rawMessage, smtp.Request.RawMessage);
            Assert.AreEqual("sender@example.test", smtp.Request.Sender);
            Assert.AreEqual("recipient@remote.test", smtp.Request.Recipient);
            Assert.AreEqual("job+2B42", smtp.Request.Options?.Dsn?.EnvelopeId);

            await using var requestQuery = gatewayDataSource.CreateCommand(
                "SELECT id, request_payload_inline FROM presentation_requests WHERE operation = @operation");
            requestQuery.Parameters.AddWithValue("operation", SmtpPresentationOperations.Relay);
            await using var requestReader = await requestQuery.ExecuteReaderAsync(timeout.Token);
            Assert.IsTrue(await requestReader.ReadAsync(timeout.Token));
            var requestId = requestReader.GetGuid(0);
            Assert.AreEqual(requestId, smtp.ApplicationRequestId);
            var ciphertext = requestReader.GetFieldValue<byte[]>(1);
            Assert.IsFalse(Encoding.UTF8.GetString(ciphertext).Contains("reverse lane", StringComparison.Ordinal));
            await requestReader.DisposeAsync();

            await using var trafficQuery = gatewayDataSource.CreateCommand(
                "SELECT DISTINCT session_id FROM gateway_traffic_records "
                + "WHERE application_request_id = @request_id");
            trafficQuery.Parameters.AddWithValue("request_id", requestId);
            var sessionId = (Guid)(await trafficQuery.ExecuteScalarAsync(timeout.Token)
                ?? throw new AssertFailedException("The Gateway journal session is missing."));
            var records = await journal.ReadSessionAsync(sessionId, timeout.Token);
            Assert.HasCount(2, records);
            CollectionAssert.AreEqual(
                new[] { GatewayTrafficDirections.Inbound, GatewayTrafficDirections.Outbound },
                records.Select(record => record.Direction).ToArray());
            Assert.IsFalse(records[0].Metadata.ContainsKey("rawMessage"));
        }
        finally
        {
            await gatewayWorker.StopAsync(CancellationToken.None);
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
            if (request.Recipient == "first@remote.test")
            {
                FirstEntered.TrySetResult();
                await ReleaseFirst.Task.WaitAsync(cancellationToken);
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
