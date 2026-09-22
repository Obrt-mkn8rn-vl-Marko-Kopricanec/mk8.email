using System.Security.Cryptography;
using System.Text;
using mk8.email.Contracts.Messaging;
using Npgsql;
using NpgsqlTypes;

namespace mk8.email.Messaging.Tests;

[TestClass]
[TestCategory("PostgreSQL")]
public sealed class PresentationBusTests
{
    [TestMethod]
    public async Task PresentationRequestWaitsForGatewayAndCompletesAcrossConnections()
    {
        await using var database = await RequirePostgresAsync();
        await using var applicationDataSource = NpgsqlDataSource.Create(database.ConnectionString);
        await using var gatewayDataSource = NpgsqlDataSource.Create(database.ConnectionString);
        await PostgresMessagingSchema.EnsureAsync(applicationDataSource);
        using var applicationProtector = AesGcmPayloadProtectorTests.CreateProtector(
            "test", "presentation-lane-key");
        using var gatewayProtector = AesGcmPayloadProtectorTests.CreateProtector(
            "test", "presentation-lane-key");
        var options = new PostgresMessagingOptions
        {
            NotificationFallbackInterval = TimeSpan.FromMinutes(1),
        };
        IPresentationRequestClient application = new PostgresPresentationBus(
            applicationDataSource, applicationProtector, options);
        IPresentationRequestConsumer gateway = new PostgresPresentationBus(
            gatewayDataSource, gatewayProtector, options);
        var request = NewRequest(Encoding.UTF8.GetBytes("deliver encrypted Web Push"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await application.EnqueueAsync(request, timeout.Token);
        Assert.AreEqual(
            ApplicationExchangeStates.Pending,
            (await application.GetAsync(request.Id, timeout.Token))?.State);
        var waitingForResponse = application.WaitForResponseAsync(
            request.Id, request.Deadline, timeout.Token);
        var lease = await gateway.WaitForRequestAsync("gateway@remote-host", timeout.Token);
        CollectionAssert.AreEqual(request.Payload, lease.Request.Payload);
        var response = new ApplicationResponse(
            request.Id,
            "application/json",
            Encoding.UTF8.GetBytes("{\"delivered\":true}"),
            new Dictionary<string, string>());
        await gateway.CompleteAsync(lease, response, timeout.Token);
        CollectionAssert.AreEqual(response.Payload, (await waitingForResponse).Payload);

        await using var query = applicationDataSource.CreateCommand(
            "SELECT request_payload_inline, response_payload_inline "
            + "FROM presentation_requests WHERE id = @id");
        query.Parameters.AddWithValue("id", NpgsqlDbType.Uuid, request.Id);
        await using var reader = await query.ExecuteReaderAsync(timeout.Token);
        Assert.IsTrue(await reader.ReadAsync(timeout.Token));
        Assert.IsFalse(reader.GetFieldValue<byte[]>(0).AsSpan().SequenceEqual(request.Payload));
        Assert.IsFalse(reader.GetFieldValue<byte[]>(1).AsSpan().SequenceEqual(response.Payload));
        await reader.DisposeAsync();

        await using var applicationQuery = applicationDataSource.CreateCommand(
            "SELECT count(*) FROM application_requests");
        Assert.AreEqual(0L, await applicationQuery.ExecuteScalarAsync(timeout.Token));
    }

    [TestMethod]
    public async Task ReverseLaneUsesDistinctAzureObjectsForTheSameRequestIdentity()
    {
        await using var database = await RequirePostgresAsync();
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        await PostgresMessagingSchema.EnsureAsync(dataSource);
        using var protector = AesGcmPayloadProtectorTests.CreateProtector(
            "test", "presentation-blob-key");
        var objects = new InMemoryLargeObjectStore();
        var options = new PostgresMessagingOptions
        {
            InlinePayloadThresholdBytes = 64,
        };
        var application = new PostgresApplicationBus(
            dataSource, protector, options, largeObjectStore: objects);
        var presentation = new PostgresPresentationBus(
            dataSource, protector, options, largeObjectStore: objects);
        var request = NewRequest(Enumerable.Repeat((byte)0x5a, 512).ToArray());

        await application.EnqueueAsync(request);
        await presentation.EnqueueAsync(request);
        CollectionAssert.AreEqual(request.Payload, (await application.GetAsync(request.Id))!.Request.Payload);
        CollectionAssert.AreEqual(request.Payload, (await presentation.GetAsync(request.Id))!.Request.Payload);
        Assert.AreEqual(2, objects.ObjectCount);

        await using var query = dataSource.CreateCommand(
            "SELECT (SELECT request_payload_blob_name FROM application_requests WHERE id = @id), "
            + "(SELECT request_payload_blob_name FROM presentation_requests WHERE id = @id)");
        query.Parameters.AddWithValue("id", NpgsqlDbType.Uuid, request.Id);
        await using var reader = await query.ExecuteReaderAsync();
        Assert.IsTrue(await reader.ReadAsync());
        StringAssert.StartsWith(reader.GetString(0), "messaging/v1/application-requests/");
        StringAssert.StartsWith(reader.GetString(1), "messaging/v1/presentation-requests/");
        Assert.AreNotEqual(reader.GetString(0), reader.GetString(1));
    }

    [TestMethod]
    public async Task PresentationLaneRejectsCiphertextCopiedFromApplicationLane()
    {
        await using var database = await RequirePostgresAsync();
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        await PostgresMessagingSchema.EnsureAsync(dataSource);
        using var protector = AesGcmPayloadProtectorTests.CreateProtector(
            "test", "presentation-domain-key");
        var application = new PostgresApplicationBus(dataSource, protector);
        var presentation = new PostgresPresentationBus(dataSource, protector);
        var request = NewRequest(Encoding.UTF8.GetBytes("cannot cross the lane boundary"));
        await application.EnqueueAsync(request);

        await using var copy = dataSource.CreateCommand(
            "INSERT INTO presentation_requests "
            + "SELECT * FROM application_requests WHERE id = @id");
        copy.Parameters.AddWithValue("id", NpgsqlDbType.Uuid, request.Id);
        Assert.AreEqual(1, await copy.ExecuteNonQueryAsync());
        await Assert.ThrowsExactlyAsync<AuthenticationTagMismatchException>(
            () => presentation.GetAsync(request.Id));
    }

    private static ApplicationRequest NewRequest(byte[] payload)
    {
        var now = DateTimeOffset.UtcNow;
        return new ApplicationRequest(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            0,
            "jmap-push",
            "webpush.send",
            "application/json",
            payload,
            new Dictionary<string, string>(),
            now,
            now.AddMinutes(1),
            Guid.NewGuid().ToString("N"));
    }

    private static async Task<PostgresTestDatabase> RequirePostgresAsync()
    {
        var database = await PostgresTestDatabase.TryCreateAsync();
        if (database is null)
        {
            Assert.Inconclusive("Set MK8_EMAIL_TEST_POSTGRES to a PostgreSQL admin connection string.");
            throw new InvalidOperationException("PostgreSQL integration test configuration is required.");
        }
        return database;
    }
}
