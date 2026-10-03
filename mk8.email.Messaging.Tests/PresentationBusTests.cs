using System.Security.Cryptography;
using System.Text;
using mk8.email.Contracts.Messaging;
using Npgsql;
using NpgsqlTypes;

namespace mk8.email.Messaging.Tests;

[TestClass]
[TestCategory("PostgreSQL")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1812", Justification = "MSTest DiscoverInternals instantiates this test class by reflection; focused discovery is verified by executed test counts.")]
internal sealed class PresentationBusTests
{
    [TestMethod]
    public async Task PresentationRequestWaitsForGatewayAndCompletesAcrossConnections()
    {
        var database = (await RequirePostgresAsync().ConfigureAwait(false));
        await using var databaseLifetime = database.ConfigureAwait(false);
        var applicationDataSource = NpgsqlDataSource.Create(database.ConnectionString);
        await using var applicationDataSourceLifetime = applicationDataSource.ConfigureAwait(false);
        var gatewayDataSource = NpgsqlDataSource.Create(database.ConnectionString);
        await using var gatewayDataSourceLifetime = gatewayDataSource.ConfigureAwait(false);
        await PostgresMessagingSchema.EnsureAsync(applicationDataSource).ConfigureAwait(false);
        using var applicationProtector = AesGcmPayloadProtectorTests.CreateProtector(
            "test", "presentation-lane-key");
        using var gatewayProtector = AesGcmPayloadProtectorTests.CreateProtector(
            "test", "presentation-lane-key");
        var options = new PostgresMessagingOptions
        {
            NotificationFallbackInterval = TimeSpan.FromMinutes(1),
        };
        var application = new PostgresPresentationBus(
            applicationDataSource, applicationProtector, options);
        var gateway = new PostgresPresentationBus(
            gatewayDataSource, gatewayProtector, options);
        var request = NewRequest(Encoding.UTF8.GetBytes("deliver encrypted Web Push"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await application.EnqueueAsync(request, timeout.Token).ConfigureAwait(false);
        Assert.AreEqual(
            ApplicationExchangeStates.Pending,
            (await application.GetAsync(request.Id, timeout.Token).ConfigureAwait(false))?.State, StringComparer.Ordinal);
        var waitingForResponse = application.WaitForResponseAsync(
            request.Id, request.Deadline, timeout.Token);
        var lease = await gateway.WaitForRequestAsync("gateway@remote-host", timeout.Token).ConfigureAwait(false);
        CollectionAssert.AreEqual(request.Payload, lease.Request.Payload);
        var response = new ApplicationResponse(
            request.Id,
            "application/json",
            Encoding.UTF8.GetBytes("{\"delivered\":true}"),
            new Dictionary<string, string>(StringComparer.Ordinal));
        await gateway.CompleteAsync(lease, response, timeout.Token).ConfigureAwait(false);
        CollectionAssert.AreEqual(response.Payload, (await waitingForResponse.ConfigureAwait(false)).Payload);

        var query = applicationDataSource.CreateCommand(
            "SELECT request_payload_inline, response_payload_inline "
            + "FROM presentation_requests WHERE id = @id");
        await using var queryLifetime = query.ConfigureAwait(false);
        query.Parameters.AddWithValue("id", NpgsqlDbType.Uuid, request.Id);
        var reader = (await query.ExecuteReaderAsync(timeout.Token).ConfigureAwait(false));
        await using var readerLifetime = reader.ConfigureAwait(false);
        Assert.IsTrue(await reader.ReadAsync(timeout.Token).ConfigureAwait(false));
        Assert.IsFalse((await (reader.GetFieldValueAsync<byte[]>(0)).ConfigureAwait(false)).AsSpan().SequenceEqual(request.Payload));
        Assert.IsFalse((await (reader.GetFieldValueAsync<byte[]>(1)).ConfigureAwait(false)).AsSpan().SequenceEqual(response.Payload));
        await reader.DisposeAsync().ConfigureAwait(false);

        var applicationQuery = applicationDataSource.CreateCommand(
            "SELECT count(*) FROM application_requests");
        await using var applicationQueryLifetime = applicationQuery.ConfigureAwait(false);
        Assert.AreEqual(0L, await applicationQuery.ExecuteScalarAsync(timeout.Token).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task ReverseLaneUsesDistinctAzureObjectsForTheSameRequestIdentity()
    {
        var database = (await RequirePostgresAsync().ConfigureAwait(false));
        await using var databaseLifetime = database.ConfigureAwait(false);
        var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        await using var dataSourceLifetime = dataSource.ConfigureAwait(false);
        await PostgresMessagingSchema.EnsureAsync(dataSource).ConfigureAwait(false);
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

        await application.EnqueueAsync(request).ConfigureAwait(false);
        await presentation.EnqueueAsync(request).ConfigureAwait(false);
        CollectionAssert.AreEqual(request.Payload, (await application.GetAsync(request.Id).ConfigureAwait(false))!.Request.Payload);
        CollectionAssert.AreEqual(request.Payload, (await presentation.GetAsync(request.Id).ConfigureAwait(false))!.Request.Payload);
        Assert.AreEqual(2, objects.ObjectCount);

        var query = dataSource.CreateCommand(
            "SELECT (SELECT request_payload_blob_name FROM application_requests WHERE id = @id), "
            + "(SELECT request_payload_blob_name FROM presentation_requests WHERE id = @id)");
        await using var queryLifetime = query.ConfigureAwait(false);
        query.Parameters.AddWithValue("id", NpgsqlDbType.Uuid, request.Id);
        var reader = (await query.ExecuteReaderAsync().ConfigureAwait(false));
        await using var readerLifetime = reader.ConfigureAwait(false);
        Assert.IsTrue(await reader.ReadAsync().ConfigureAwait(false));
        StringAssert.StartsWith(reader.GetString(0), "messaging/v1/application-requests/", StringComparison.Ordinal);
        StringAssert.StartsWith(reader.GetString(1), "messaging/v1/presentation-requests/", StringComparison.Ordinal);
        Assert.AreNotEqual(reader.GetString(0), reader.GetString(1), StringComparer.Ordinal);
    }

    [TestMethod]
    public async Task PresentationLaneRejectsCiphertextCopiedFromApplicationLane()
    {
        var database = (await RequirePostgresAsync().ConfigureAwait(false));
        await using var databaseLifetime = database.ConfigureAwait(false);
        var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        await using var dataSourceLifetime = dataSource.ConfigureAwait(false);
        await PostgresMessagingSchema.EnsureAsync(dataSource).ConfigureAwait(false);
        using var protector = AesGcmPayloadProtectorTests.CreateProtector(
            "test", "presentation-domain-key");
        var application = new PostgresApplicationBus(dataSource, protector);
        var presentation = new PostgresPresentationBus(dataSource, protector);
        var request = NewRequest(Encoding.UTF8.GetBytes("cannot cross the lane boundary"));
        await application.EnqueueAsync(request).ConfigureAwait(false);

        var copy = dataSource.CreateCommand(
            "INSERT INTO presentation_requests "
            + "SELECT * FROM application_requests WHERE id = @id");
        await using var copyLifetime = copy.ConfigureAwait(false);
        copy.Parameters.AddWithValue("id", NpgsqlDbType.Uuid, request.Id);
        Assert.AreEqual(1, await copy.ExecuteNonQueryAsync().ConfigureAwait(false));
        await Assert.ThrowsExactlyAsync<AuthenticationTagMismatchException>(
            () => presentation.GetAsync(request.Id)).ConfigureAwait(false);
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
            new Dictionary<string, string>(StringComparer.Ordinal),
            now,
            now.AddMinutes(1),
            Guid.NewGuid().ToString("N"));
    }

    private static async Task<PostgresTestDatabase> RequirePostgresAsync()
    {
        var database = await PostgresTestDatabase.TryCreateAsync().ConfigureAwait(false);
        if (database is null)
        {
            Assert.Inconclusive("Set MK8_EMAIL_TEST_POSTGRES to a PostgreSQL admin connection string.");
            throw new InvalidOperationException("PostgreSQL integration test configuration is required.");
        }
        return database;
    }
}
