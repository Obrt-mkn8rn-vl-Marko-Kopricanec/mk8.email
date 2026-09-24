using System.Text;
using mk8.email.Contracts.Messaging;
using Npgsql;
using NpgsqlTypes;

namespace mk8.email.Messaging.Tests;

[TestClass]
[TestCategory("PostgreSQL")]
public sealed class PostgresMessagingTests
{
    [TestMethod]
    public async Task TransportControlReportsAvailabilityOnlyAfterSchemaProvisioning()
    {
        await using var database = await RequirePostgresAsync();
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        var transport = new PostgresApplicationTransportControl(dataSource);

        Assert.IsFalse(await transport.IsAvailableAsync());
        await PostgresMessagingSchema.EnsureAsync(dataSource);
        Assert.IsTrue(await transport.IsAvailableAsync());
    }

    [TestMethod]
    public async Task GatewayJournalRecordsEncryptedBidirectionalTraffic()
    {
        await using var database = await RequirePostgresAsync();
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        await PostgresMessagingSchema.EnsureAsync(dataSource);
        using var protector = AesGcmPayloadProtectorTests.CreateProtector("test", "journal-key");
        var journal = new PostgresGatewayTrafficJournal(dataSource, protector);
        var sessionId = Guid.CreateVersion7();
        var requestId = Guid.CreateVersion7();
        var now = DateTimeOffset.UtcNow;
        var inboundPayload = Encoding.UTF8.GetBytes("AUTH secret-value");
        var outboundPayload = Encoding.UTF8.GetBytes("OK authenticated");
        var inbound = new GatewayTrafficRecord(
            Guid.CreateVersion7(),
            sessionId,
            0,
            GatewayTrafficDirections.Inbound,
            "imap",
            "application/octet-stream",
            inboundPayload,
            new Dictionary<string, string> { ["remote-address"] = "192.0.2.4" },
            now,
            requestId);
        var outbound = new GatewayTrafficRecord(
            Guid.CreateVersion7(),
            sessionId,
            1,
            GatewayTrafficDirections.Outbound,
            "imap",
            "application/octet-stream",
            outboundPayload,
            new Dictionary<string, string>(),
            now.AddMilliseconds(1),
            requestId);

        await journal.AppendAsync(inbound);
        await journal.AppendAsync(inbound);
        await journal.AppendAsync(outbound);

        var records = await journal.ReadSessionAsync(sessionId);
        Assert.HasCount(2, records);
        CollectionAssert.AreEqual(inboundPayload, records[0].Payload);
        CollectionAssert.AreEqual(outboundPayload, records[1].Payload);
        Assert.AreEqual(GatewayTrafficDirections.Inbound, records[0].Direction);
        Assert.AreEqual(GatewayTrafficDirections.Outbound, records[1].Direction);
        Assert.AreEqual(requestId, records[0].ApplicationRequestId);

        await using var ciphertextCommand = dataSource.CreateCommand(
            "SELECT payload_inline FROM gateway_traffic_records WHERE id = @id");
        ciphertextCommand.Parameters.AddWithValue("id", NpgsqlDbType.Uuid, inbound.Id);
        var ciphertext = (byte[])(await ciphertextCommand.ExecuteScalarAsync()
            ?? throw new AssertFailedException("The encrypted traffic record is missing."));
        Assert.IsFalse(ciphertext.AsSpan().SequenceEqual(inboundPayload));
        Assert.IsFalse(Encoding.UTF8.GetString(ciphertext).Contains("secret-value", StringComparison.Ordinal));

        var conflicting = inbound with { Id = Guid.CreateVersion7(), Payload = [1, 2, 3] };
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => journal.AppendAsync(conflicting));
    }

    [TestMethod]
    public async Task RequestSurvivesAbsentWorkerAndCompletesAcrossConnections()
    {
        await using var database = await RequirePostgresAsync();
        await using var gatewayDataSource = NpgsqlDataSource.Create(database.ConnectionString);
        await PostgresMessagingSchema.EnsureAsync(gatewayDataSource);
        using var gatewayProtector = AesGcmPayloadProtectorTests.CreateProtector("test", "bus-key");
        var gateway = new PostgresApplicationBus(gatewayDataSource, gatewayProtector);
        var request = NewRequest("queued while application is offline");

        await gateway.EnqueueAsync(request);
        Assert.AreEqual(
            ApplicationExchangeStates.Pending,
            (await gateway.GetAsync(request.Id))?.State);

        await using var workerDataSource = NpgsqlDataSource.Create(database.ConnectionString);
        using var workerProtector = AesGcmPayloadProtectorTests.CreateProtector("test", "bus-key");
        var worker = new PostgresApplicationBus(workerDataSource, workerProtector);
        var lease = await worker.TryClaimAsync("worker@example-1");
        Assert.IsNotNull(lease);
        CollectionAssert.AreEqual(request.Payload, lease.Request.Payload);
        Assert.AreEqual(1, lease.AttemptCount);
        var response = new ApplicationResponse(
            request.Id,
            "application/json",
            Encoding.UTF8.GetBytes("{\"accepted\":true}"),
            new Dictionary<string, string> { ["result"] = "accepted" });
        await worker.CompleteAsync(lease, response);

        var received = await gateway.WaitForResponseAsync(request.Id, request.Deadline);
        CollectionAssert.AreEqual(response.Payload, received.Payload);
        Assert.AreEqual("accepted", received.Metadata["result"]);
        var snapshot = await gateway.GetAsync(request.Id);
        Assert.AreEqual(ApplicationExchangeStates.Completed, snapshot?.State);
        Assert.AreEqual("worker@example-1", lease.WorkerId);

        await using var encrypted = gatewayDataSource.CreateCommand(
            "SELECT request_payload_inline, response_payload_inline "
            + "FROM application_requests WHERE id = @id");
        encrypted.Parameters.AddWithValue("id", NpgsqlDbType.Uuid, request.Id);
        await using var reader = await encrypted.ExecuteReaderAsync();
        Assert.IsTrue(await reader.ReadAsync());
        Assert.IsFalse(reader.GetFieldValue<byte[]>(0).AsSpan().SequenceEqual(request.Payload));
        Assert.IsFalse(reader.GetFieldValue<byte[]>(1).AsSpan().SequenceEqual(response.Payload));
    }

    [TestMethod]
    public async Task RequestReplyUsesNotificationsWithoutSharingAProcess()
    {
        await using var database = await RequirePostgresAsync();
        await using var gatewayDataSource = NpgsqlDataSource.Create(database.ConnectionString);
        await using var workerDataSource = NpgsqlDataSource.Create(database.ConnectionString);
        await PostgresMessagingSchema.EnsureAsync(gatewayDataSource);
        using var gatewayProtector = AesGcmPayloadProtectorTests.CreateProtector("test", "notify-key");
        using var workerProtector = AesGcmPayloadProtectorTests.CreateProtector("test", "notify-key");
        var options = new PostgresMessagingOptions
        {
            NotificationFallbackInterval = TimeSpan.FromMinutes(1),
        };
        var gateway = new PostgresApplicationBus(gatewayDataSource, gatewayProtector, options);
        var worker = new PostgresApplicationBus(workerDataSource, workerProtector, options);
        var request = NewRequest("notification request");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var workerTask = worker.WaitForRequestAsync("worker@example-2", timeout.Token);
        var responseTask = gateway.SendAsync(request, timeout.Token);
        var lease = await workerTask;
        await worker.CompleteAsync(
            lease,
            new ApplicationResponse(
                request.Id,
                "application/json",
                Encoding.UTF8.GetBytes("{\"ok\":true}"),
                new Dictionary<string, string>()),
            timeout.Token);
        var response = await responseTask;

        Assert.AreEqual(request.Id, response.RequestId);
        Assert.AreEqual("{\"ok\":true}", Encoding.UTF8.GetString(response.Payload));
    }

    [TestMethod]
    public async Task IdleRequestWaitCanBeCancelledAfterTimedFallbackScans()
    {
        await using var database = await RequirePostgresAsync();
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        await PostgresMessagingSchema.EnsureAsync(dataSource);
        using var protector = AesGcmPayloadProtectorTests.CreateProtector("test", "idle-wait-key");
        var options = new PostgresMessagingOptions
        {
            NotificationFallbackInterval = TimeSpan.FromMilliseconds(100),
        };
        var bus = new PostgresApplicationBus(dataSource, protector, options);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => bus.WaitForRequestAsync("worker@example-idle", cancellation.Token)
                .WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [TestMethod]
    public async Task PendingResponseExpiresAfterTimedFallbackScans()
    {
        await using var database = await RequirePostgresAsync();
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        await PostgresMessagingSchema.EnsureAsync(dataSource);
        using var protector = AesGcmPayloadProtectorTests.CreateProtector("test", "response-wait-key");
        var options = new PostgresMessagingOptions
        {
            NotificationFallbackInterval = TimeSpan.FromMilliseconds(100),
        };
        var bus = new PostgresApplicationBus(dataSource, protector, options);
        var request = NewRequest("expires without worker") with
        {
            Deadline = DateTimeOffset.UtcNow.AddMilliseconds(600),
        };
        await bus.EnqueueAsync(request);

        await Assert.ThrowsExactlyAsync<ApplicationRequestExpiredException>(
            () => bus.WaitForResponseAsync(request.Id, request.Deadline)
                .WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [TestMethod]
    public async Task ConcurrentWorkersClaimARequestOnlyOnceAndExpiredLeaseRecovers()
    {
        await using var database = await RequirePostgresAsync();
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        await PostgresMessagingSchema.EnsureAsync(dataSource);
        using var protector = AesGcmPayloadProtectorTests.CreateProtector("test", "lease-key");
        var bus = new PostgresApplicationBus(dataSource, protector);
        var request = NewRequest("claim once");
        await bus.EnqueueAsync(request);

        var claims = await Task.WhenAll(
            bus.TryClaimAsync("worker@example-a"),
            bus.TryClaimAsync("worker@example-b"));
        var first = claims.Single(claim => claim is not null)!;
        Assert.AreEqual(1, claims.Count(claim => claim is not null));

        await using (var expire = dataSource.CreateCommand(
            "UPDATE application_requests SET lease_expires_at = clock_timestamp() - interval '1 second' "
            + "WHERE id = @id"))
        {
            expire.Parameters.AddWithValue("id", NpgsqlDbType.Uuid, request.Id);
            Assert.AreEqual(1, await expire.ExecuteNonQueryAsync());
        }
        var recovered = await bus.TryClaimAsync("worker@example-recovery");
        Assert.IsNotNull(recovered);
        Assert.AreEqual(2, recovered.AttemptCount);
        Assert.AreEqual("worker@example-recovery", recovered.WorkerId);
        await Assert.ThrowsExactlyAsync<ApplicationRequestLeaseLostException>(
            () => bus.CompleteAsync(
                first,
                new ApplicationResponse(
                    request.Id,
                    "application/json",
                    [],
                    new Dictionary<string, string>())));
    }

    [TestMethod]
    public async Task WorkerFailureIsDurableAndPropagatesToGateway()
    {
        await using var database = await RequirePostgresAsync();
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        await PostgresMessagingSchema.EnsureAsync(dataSource);
        using var protector = AesGcmPayloadProtectorTests.CreateProtector("test", "failure-key");
        var bus = new PostgresApplicationBus(dataSource, protector);
        var request = NewRequest("failing request");
        await bus.EnqueueAsync(request);
        var lease = await bus.TryClaimAsync("worker@example-failure");
        Assert.IsNotNull(lease);
        await bus.FailAsync(lease, "application-unavailable", "The domain service is unavailable.");

        var exception = await Assert.ThrowsExactlyAsync<ApplicationRequestFailedException>(
            () => bus.WaitForResponseAsync(request.Id, request.Deadline));
        Assert.AreEqual(request.Id, exception.RequestId);
        Assert.AreEqual("application-unavailable", exception.ErrorCode);
        Assert.AreEqual("The domain service is unavailable.", exception.Message);
    }

    [TestMethod]
    public async Task LargeTrafficUsesAzureBlobProtocolAndCleansUpConflictUpload()
    {
        await using var database = await RequirePostgresAsync();
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        await PostgresMessagingSchema.EnsureAsync(dataSource);
        using var protector = AesGcmPayloadProtectorTests.CreateProtector("test", "traffic-blob-key");
        var store = new InMemoryLargeObjectStore();
        var options = LargeObjectOptions();
        var journal = new PostgresGatewayTrafficJournal(dataSource, protector, options, store);
        var payload = Enumerable.Range(0, 512).Select(value => (byte)(value % 251)).ToArray();
        var record = new GatewayTrafficRecord(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            0,
            GatewayTrafficDirections.Inbound,
            "smtp",
            "message/rfc822",
            payload,
            new Dictionary<string, string>(),
            DateTimeOffset.UtcNow);

        await journal.AppendAsync(record);
        await journal.AppendAsync(record);

        var roundTrip = await journal.ReadSessionAsync(record.SessionId);
        Assert.HasCount(1, roundTrip);
        CollectionAssert.AreEqual(payload, roundTrip[0].Payload);
        Assert.AreEqual(2, store.PutCount);
        Assert.AreEqual(1, store.DeleteCount);
        Assert.AreEqual(1, store.ObjectCount);

        await using var command = dataSource.CreateCommand(
            "SELECT payload_inline, payload_blob_provider, payload_blob_name "
            + "FROM gateway_traffic_records WHERE id = @id");
        command.Parameters.AddWithValue("id", NpgsqlDbType.Uuid, record.Id);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.IsTrue(await reader.ReadAsync());
        Assert.IsTrue(reader.IsDBNull(0));
        Assert.AreEqual("azure-blob", reader.GetString(1));
        StringAssert.StartsWith(reader.GetString(2), "messaging/v1/gateway-traffic/");
    }

    [TestMethod]
    public async Task LargeRequestAndResponseRoundTripAcrossSeparateBusInstances()
    {
        await using var database = await RequirePostgresAsync();
        await using var gatewayDataSource = NpgsqlDataSource.Create(database.ConnectionString);
        await using var workerDataSource = NpgsqlDataSource.Create(database.ConnectionString);
        await PostgresMessagingSchema.EnsureAsync(gatewayDataSource);
        using var gatewayProtector = AesGcmPayloadProtectorTests.CreateProtector("test", "queue-blob-key");
        using var workerProtector = AesGcmPayloadProtectorTests.CreateProtector("test", "queue-blob-key");
        var store = new InMemoryLargeObjectStore();
        var options = LargeObjectOptions();
        var gateway = new PostgresApplicationBus(
            gatewayDataSource,
            gatewayProtector,
            options,
            largeObjectStore: store);
        var worker = new PostgresApplicationBus(
            workerDataSource,
            workerProtector,
            options,
            largeObjectStore: store);
        var requestPayload = Enumerable.Repeat((byte)0x5a, 512).ToArray();
        var responsePayload = Enumerable.Repeat((byte)0xa5, 768).ToArray();
        var request = NewRequest(requestPayload);

        await gateway.EnqueueAsync(request);
        var lease = await worker.TryClaimAsync("worker@remote-host");
        Assert.IsNotNull(lease);
        CollectionAssert.AreEqual(requestPayload, lease.Request.Payload);
        await worker.CompleteAsync(
            lease,
            new ApplicationResponse(
                request.Id,
                "application/octet-stream",
                responsePayload,
                new Dictionary<string, string>()));

        var response = await gateway.WaitForResponseAsync(request.Id, request.Deadline);
        CollectionAssert.AreEqual(responsePayload, response.Payload);
        Assert.AreEqual(2, store.ObjectCount);
        await using var command = gatewayDataSource.CreateCommand(
            "SELECT request_payload_inline, request_payload_blob_provider, "
            + "response_payload_inline, response_payload_blob_provider "
            + "FROM application_requests WHERE id = @id");
        command.Parameters.AddWithValue("id", NpgsqlDbType.Uuid, request.Id);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.IsTrue(await reader.ReadAsync());
        Assert.IsTrue(reader.IsDBNull(0));
        Assert.AreEqual("azure-blob", reader.GetString(1));
        Assert.IsTrue(reader.IsDBNull(2));
        Assert.AreEqual("azure-blob", reader.GetString(3));
    }

    [TestMethod]
    public async Task MissingOrTamperedLargeObjectFailsClosed()
    {
        await using var database = await RequirePostgresAsync();
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        await PostgresMessagingSchema.EnsureAsync(dataSource);
        using var protector = AesGcmPayloadProtectorTests.CreateProtector("test", "missing-blob-key");
        var store = new InMemoryLargeObjectStore();
        var bus = new PostgresApplicationBus(
            dataSource,
            protector,
            LargeObjectOptions(),
            largeObjectStore: store);
        var request = NewRequest(Enumerable.Repeat((byte)0x41, 512).ToArray());
        await bus.EnqueueAsync(request);
        var objectName = await ReadRequestObjectNameAsync(dataSource, request.Id);

        store.Corrupt(objectName);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => bus.GetAsync(request.Id));
        store.Remove(objectName);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => bus.GetAsync(request.Id));
    }

    [TestMethod]
    public async Task LargePayloadCannotFallBackToPostgresWhenBlobStoreIsUnavailable()
    {
        await using var database = await RequirePostgresAsync();
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        await PostgresMessagingSchema.EnsureAsync(dataSource);
        using var protector = AesGcmPayloadProtectorTests.CreateProtector("test", "required-blob-key");
        var bus = new PostgresApplicationBus(dataSource, protector, LargeObjectOptions());
        var request = NewRequest(Enumerable.Repeat((byte)0x42, 512).ToArray());

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => bus.EnqueueAsync(request));
        await using var command = dataSource.CreateCommand("SELECT count(*) FROM application_requests");
        Assert.AreEqual(0L, await command.ExecuteScalarAsync());
    }

    private static ApplicationRequest NewRequest(string payload)
        => NewRequest(Encoding.UTF8.GetBytes(payload));

    private static ApplicationRequest NewRequest(byte[] payload)
    {
        var now = DateTimeOffset.UtcNow;
        return new ApplicationRequest(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            0,
            "admin",
            "accounts.create",
            "application/json",
            payload,
            new Dictionary<string, string> { ["trace-id"] = Guid.NewGuid().ToString("N") },
            now,
            now.AddMinutes(1),
            Guid.NewGuid().ToString("N"));
    }

    private static PostgresMessagingOptions LargeObjectOptions() => new()
    {
        InlinePayloadThresholdBytes = 64,
        NotificationFallbackInterval = TimeSpan.FromSeconds(1),
    };

    private static async Task<string> ReadRequestObjectNameAsync(
        NpgsqlDataSource dataSource,
        Guid requestId)
    {
        await using var command = dataSource.CreateCommand(
            "SELECT request_payload_blob_name FROM application_requests WHERE id = @id");
        command.Parameters.AddWithValue("id", NpgsqlDbType.Uuid, requestId);
        return (string)(await command.ExecuteScalarAsync()
            ?? throw new AssertFailedException("The request large-object reference is missing."));
    }

    private static async Task<PostgresTestDatabase> RequirePostgresAsync()
    {
        var database = await PostgresTestDatabase.TryCreateAsync();
        if (database is null)
        {
            Assert.Inconclusive(
                "Set MK8_EMAIL_TEST_POSTGRES to a PostgreSQL admin connection string.");
            throw new InvalidOperationException("PostgreSQL integration test configuration is required.");
        }
        return database;
    }
}
