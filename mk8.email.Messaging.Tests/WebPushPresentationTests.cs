using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using mk8.email.Application.Worker;
using mk8.email.Configuration;
using mk8.email.Contracts.Messaging;
using mk8.email.Gateway.Protocols;
using mk8.email.Gateway.Protocols.Jmap;
using mk8.email.Smtp.Presentation;
using Npgsql;
using NpgsqlTypes;

namespace mk8.email.Messaging.Tests;

[TestClass]
[TestCategory("PostgreSQL")]
public sealed class WebPushPresentationTests
{
    [TestMethod]
    public async Task WorkerPushRequestCrossesReverseLaneAndGatewayJournalsHttpExchange()
    {
        await using var database = await RequirePostgresAsync();
        await using var applicationDataSource = NpgsqlDataSource.Create(database.ConnectionString);
        await using var gatewayDataSource = NpgsqlDataSource.Create(database.ConnectionString);
        await PostgresMessagingSchema.EnsureAsync(gatewayDataSource);
        using var applicationProtector = AesGcmPayloadProtectorTests.CreateProtector(
            "test", "webpush-roundtrip-key");
        using var gatewayProtector = AesGcmPayloadProtectorTests.CreateProtector(
            "test", "webpush-roundtrip-key");
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
        var handler = new CapturingPushHandler();
        using var sender = new GatewayWebPushService(handler, journal, options.MaxPayloadBytes);
        var gatewayWorker = new GatewayPresentationWorker(
            gatewayBus,
            new PostgresApplicationTransportControl(gatewayDataSource),
            journal,
            sender,
            new UnusedSmtpRelay(),
            new EnvironmentConfig(),
            NullLogger<GatewayPresentationWorker>.Instance);
        var application = new JmapPushPresentationClient(
            applicationBus,
            NullLogger<JmapPushPresentationClient>.Instance);
        var payload = Encoding.UTF8.GetBytes("{\"@type\":\"StateChange\",\"changed\":{}}");

        await gatewayWorker.StartAsync(CancellationToken.None);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            Assert.IsFalse(await application.IsSafeUrlAsync(
                "https://127.0.0.1/push", timeout.Token));
            Assert.IsTrue(await application.IsSafeUrlAsync(
                "https://1.1.1.1/push", timeout.Token));
            var result = await application.SendAsync(
                "https://push.example.net/jmap",
                keysJson: null,
                DateTime.UtcNow.AddMinutes(5),
                payload,
                timeout.Token);
            Assert.AreEqual(WebPushSendOutcome.Success, result);
            Assert.AreEqual(1, handler.CallCount);
            CollectionAssert.AreEqual(payload, handler.Body!);

            await using var requestQuery = applicationDataSource.CreateCommand(
                "SELECT id FROM presentation_requests WHERE operation = @operation");
            requestQuery.Parameters.AddWithValue(
                "operation", NpgsqlDbType.Varchar, WebPushPresentationOperations.Send);
            var requestId = (Guid)(await requestQuery.ExecuteScalarAsync(timeout.Token)
                ?? throw new AssertFailedException("The presentation request is missing."));
            await using var trafficQuery = gatewayDataSource.CreateCommand(
                "SELECT DISTINCT session_id FROM gateway_traffic_records "
                + "WHERE application_request_id = @request_id");
            trafficQuery.Parameters.AddWithValue("request_id", NpgsqlDbType.Uuid, requestId);
            var trafficSessionId = (Guid)(await trafficQuery.ExecuteScalarAsync(timeout.Token)
                ?? throw new AssertFailedException("The Gateway traffic session is missing."));
            var records = await journal.ReadSessionAsync(trafficSessionId, timeout.Token);
            Assert.HasCount(4, records);
            CollectionAssert.AreEqual(
                new[]
                {
                    GatewayTrafficDirections.Inbound,
                    GatewayTrafficDirections.Outbound,
                    GatewayTrafficDirections.Inbound,
                    GatewayTrafficDirections.Outbound,
                },
                records.Select(record => record.Direction).ToArray());
            CollectionAssert.AreEqual(new long[] { 0, 1, 2, 3 },
                records.Select(record => record.Sequence).ToArray());
            var outbound = JsonNode.Parse(records[1].Payload)!.AsObject();
            Assert.AreEqual("https://push.example.net/jmap", outbound["url"]!.GetValue<string>());
            CollectionAssert.AreEqual(
                payload,
                Convert.FromBase64String(outbound["bodyBase64"]!.GetValue<string>()));
            var inbound = JsonNode.Parse(records[2].Payload)!.AsObject();
            Assert.AreEqual(201, inbound["status"]!.GetValue<int>());
            Assert.AreEqual("accepted", Encoding.UTF8.GetString(
                Convert.FromBase64String(inbound["bodyBase64"]!.GetValue<string>())));

            await using var ciphertextQuery = gatewayDataSource.CreateCommand(
                "SELECT payload_inline FROM gateway_traffic_records "
                + "WHERE session_id = @session_id AND sequence = 1");
            ciphertextQuery.Parameters.AddWithValue(
                "session_id", NpgsqlDbType.Uuid, trafficSessionId);
            var ciphertext = (byte[])(await ciphertextQuery.ExecuteScalarAsync(timeout.Token)
                ?? throw new AssertFailedException("The encrypted request trace is missing."));
            Assert.IsFalse(Encoding.UTF8.GetString(ciphertext).Contains(
                "push.example.net", StringComparison.Ordinal));
        }
        finally
        {
            await gatewayWorker.StopAsync(CancellationToken.None);
        }
    }

    [TestMethod]
    public async Task JournalFailurePreventsOutboundWebPush()
    {
        var handler = new CapturingPushHandler();
        using var sender = new GatewayWebPushService(handler, new RejectingJournal(), 65_536);
        var request = new WebPushSendRequest(
            "https://push.example.net/jmap",
            null,
            null,
            DateTimeOffset.UtcNow.AddMinutes(5),
            Encoding.UTF8.GetBytes("{\"@type\":\"StateChange\"}"));

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => sender.SendAsync(
            request,
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            CancellationToken.None));
        Assert.AreEqual(0, handler.CallCount);
    }

    [TestMethod]
    public async Task VerificationDeliveryRemainsQueuedWhileGatewayIsOffline()
    {
        await using var database = await RequirePostgresAsync();
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        await PostgresMessagingSchema.EnsureAsync(dataSource);
        using var protector = AesGcmPayloadProtectorTests.CreateProtector(
            "test", "webpush-verification-key");
        var bus = new PostgresPresentationBus(dataSource, protector);
        var application = new JmapPushPresentationClient(
            bus,
            NullLogger<JmapPushPresentationClient>.Instance);

        await application.EnqueueVerificationAsync(
            "https://push.example.net/jmap",
            keysJson: null,
            DateTime.UtcNow.AddDays(2),
            Encoding.UTF8.GetBytes("{\"@type\":\"PushVerification\"}"),
            CancellationToken.None);

        await using var query = dataSource.CreateCommand(
            "SELECT state, deadline_at FROM presentation_requests "
            + "WHERE operation = @operation");
        query.Parameters.AddWithValue(
            "operation", NpgsqlDbType.Varchar, WebPushPresentationOperations.Send);
        await using var reader = await query.ExecuteReaderAsync();
        Assert.IsTrue(await reader.ReadAsync());
        Assert.AreEqual(ApplicationExchangeStates.Pending, reader.GetString(0));
        Assert.IsTrue(reader.GetDateTime(1) > DateTime.UtcNow.AddHours(23));
    }

    [TestMethod]
    public async Task EndpointCheckRejectsPrivateAndNonHttpsDestinations()
    {
        using var sender = new GatewayWebPushService(
            new CapturingPushHandler(),
            new RejectingJournal(),
            65_536);
        Assert.IsFalse(await sender.IsSafeUrlAsync("http://1.1.1.1/push", CancellationToken.None));
        Assert.IsFalse(await sender.IsSafeUrlAsync("https://127.0.0.1/push", CancellationToken.None));
        Assert.IsFalse(await sender.IsSafeUrlAsync("https://10.0.0.1/push", CancellationToken.None));
        Assert.IsTrue(await sender.IsSafeUrlAsync("https://1.1.1.1/push", CancellationToken.None));
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

    private sealed class CapturingPushHandler : HttpMessageHandler
    {
        public int CallCount { get; private set; }
        public byte[]? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            Body = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent("accepted"),
            };
        }
    }

    private sealed class RejectingJournal : IGatewayTrafficJournal
    {
        public Task AppendAsync(
            GatewayTrafficRecord record,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The traffic journal is unavailable.");

        public Task<IReadOnlyList<GatewayTrafficRecord>> ReadSessionAsync(
            Guid sessionId,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The traffic journal is unavailable.");
    }

    private sealed class UnusedSmtpRelay : ISmtpPresentationRelay
    {
        public Task<OutboundDeliveryResult> RelayAsync(
            SmtpRelayPresentationRequest request,
            Guid applicationRequestId,
            CancellationToken cancellationToken) =>
            throw new AssertFailedException("A Web Push request must not enter the SMTP relay.");
    }
}
