using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using mk8.email.Application.Interfaces;
using mk8.email.Application.Worker;
using mk8.email.Configuration;
using mk8.email.Contracts.Mail;
using mk8.email.Contracts.Messaging;
using mk8.email.Gateway.ApplicationBridge;
using mk8.email.Gateway.Protocols;
using mk8.email.Gateway.Protocols.Jmap;
using mk8.email.Hosting;
using mk8.email.Smtp.Presentation;
using Npgsql;

namespace mk8.email.Messaging.Tests;

[TestClass]
[TestCategory("PostgreSQL")]
public sealed class DistributedContractCompatibilityTests
{
    [TestMethod]
    [DataRow("jmap.api.process", false, false)]
    [DataRow("jmap.api.process", false, true)]
    [DataRow("jmap.batch.execute", false, false)]
    [DataRow("jmap.batch.execute", false, true)]
    [DataRow("jmap.batch.execute.v2", false, false)]
    [DataRow("jmap.batch.execute.v2", false, true)]
    [DataRow("jmap.batch.execute.v3", false, false)]
    [DataRow("jmap.batch.execute.v3", false, true)]
    [DataRow("jmap.batch.execute.v4", false, false)]
    [DataRow("jmap.batch.execute.v4", false, true)]
    [DataRow("jmap.batch.execute.v5", false, false)]
    [DataRow("jmap.batch.execute.v5", false, true)]
    [DataRow("jmap.batch.execute.v6", false, false)]
    [DataRow("jmap.batch.execute.v6", false, true)]
    [DataRow("mail.operation.execute", false, false)]
    [DataRow("mail.operation.execute", false, true)]
    [DataRow("mail.operation.execute.v2", false, false)]
    [DataRow("mail.operation.execute.v2", false, true)]
    [DataRow("mail.operation.execute.v3", false, false)]
    [DataRow("mail.operation.execute.v3", false, true)]
    [DataRow("mail.operation.execute.v4", false, false)]
    [DataRow("mail.operation.execute.v4", false, true)]
    [DataRow("mail.operation.execute.v5", false, false)]
    [DataRow("mail.operation.execute.v5", false, true)]
    [DataRow("mail.operation.execute.v6", false, false)]
    [DataRow("mail.operation.execute.v6", false, true)]
    [DataRow("mail.operation.execute.v7", false, false)]
    [DataRow("mail.operation.execute.v7", false, true)]
    [DataRow("mail.operation.execute.v8", false, false)]
    [DataRow("mail.operation.execute.v8", false, true)]
    [DataRow("mail.operation.execute.v9", false, false)]
    [DataRow("mail.operation.execute.v9", false, true)]
    [DataRow("mail.operation.execute.v10", false, false)]
    [DataRow("mail.operation.execute.v10", false, true)]
    [DataRow("mail.operation.execute.v11", false, false)]
    [DataRow("mail.operation.execute.v11", false, true)]
    [DataRow("mail.operation.execute.v12", false, false)]
    [DataRow("mail.operation.execute.v12", false, true)]
    [DataRow("mail.operation.execute.v13", false, false)]
    [DataRow("mail.operation.execute.v13", false, true)]
    [DataRow("mail.operation.execute.v14", false, false)]
    [DataRow("mail.operation.execute.v14", false, true)]
    [DataRow("mail.operation.execute.v15", false, false)]
    [DataRow("mail.operation.execute.v15", false, true)]
    [DataRow("mail.operation.execute.v16", false, false)]
    [DataRow("mail.operation.execute.v16", false, true)]
    [DataRow("mail.operation.execute.v17", false, false)]
    [DataRow("mail.operation.execute.v17", false, true)]
    [DataRow("mail.operation.execute.v18", false, false)]
    [DataRow("mail.operation.execute.v18", false, true)]
    [DataRow("mail.operation.execute.v26", false, false)]
    [DataRow("mail.operation.execute.v26", false, true)]
    [DataRow("mail.operation.execute.v27", false, false)]
    [DataRow("mail.operation.execute.v27", false, true)]
    [DataRow("jmap.profile.get", false, false)]
    [DataRow("jmap.profile.get", false, true)]
    [DataRow("jmap.upload", false, false)]
    [DataRow("jmap.upload", false, true)]
    [DataRow("jmap.download", false, false)]
    [DataRow("jmap.download", false, true)]
    [DataRow("jmap.changes.poll", false, false)]
    [DataRow("jmap.changes.poll", false, true)]
    [DataRow("jmap.session.get", false, false)]
    [DataRow("jmap.session.get", false, true)]
    [DataRow("jmap.event.poll", false, false)]
    [DataRow("jmap.event.poll", false, true)]
    [DataRow("webpush.send", true, false)]
    [DataRow("webpush.send", true, true)]
    public async Task SupersededPendingAndLeasedWorkBlocksWithoutChangingOpaqueData(string operation, bool presentation, bool leased)
    {
        await using var database = await RequirePostgresAsync();
        await using var source = NpgsqlDataSource.Create(database.ConnectionString);
        await PostgresMessagingSchema.EnsureAsync(source);
        using var protector = AesGcmPayloadProtectorTests.CreateProtector("original", "original-escrow-only-key");
        PostgresApplicationBus bus = presentation
            ? new PostgresPresentationBus(source, protector) : new PostgresApplicationBus(source, protector);
        var request = Request(operation);
        await bus.EnqueueAsync(request);
        if (leased)
            Assert.IsNotNull(await bus.TryClaimAsync("original-role"));
        var table = presentation ? "presentation_requests" : "application_requests";
        var before = await SnapshotAsync(source, table, request.Id);
        Assert.IsFalse(await DistributedQueueContractGuard.IsCompatibleAsync(source));
        await Assert.ThrowsAsync<InvalidOperationException>(() => DistributedQueueContractGuard.RequireCompatibleAsync(source));
        Assert.IsFalse(await Control(source).IsAvailableAsync());
        Assert.AreEqual(before, await SnapshotAsync(source, table, request.Id));
    }

    [TestMethod]
    [DataRow("jmap.batch.execute.v2", false)]
    [DataRow("webpush.send", true)]
    [DataRow("jmap.batch.execute.v5", false)]
    [DataRow("jmap.batch.execute.v6", false)]
    [DataRow("mail.operation.execute", false)]
    [DataRow("mail.operation.execute.v2", false)]
    [DataRow("mail.operation.execute.v3", false)]
    [DataRow("mail.operation.execute.v4", false)]
    [DataRow("mail.operation.execute.v5", false)]
    [DataRow("mail.operation.execute.v6", false)]
    [DataRow("mail.operation.execute.v7", false)]
    [DataRow("mail.operation.execute.v8", false)]
    [DataRow("mail.operation.execute.v9", false)]
    [DataRow("mail.operation.execute.v10", false)]
    [DataRow("mail.operation.execute.v11", false)]
    [DataRow("mail.operation.execute.v12", false)]
    [DataRow("mail.operation.execute.v13", false)]
    [DataRow("mail.operation.execute.v14", false)]
    [DataRow("mail.operation.execute.v15", false)]
    [DataRow("mail.operation.execute.v16", false)]
    [DataRow("mail.operation.execute.v17", false)]
    [DataRow("mail.operation.execute.v18", false)]
    [DataRow("jmap.profile.get", false)]
    [DataRow("jmap.upload", false)]
    [DataRow("jmap.download", false)]
    [DataRow("jmap.changes.poll", false)]
    public async Task ElapsedLegacyDeadlinesRemainBlockedAndAreNotAutomaticallyExpired(string operation, bool presentation)
    {
        await using var database = await RequirePostgresAsync();
        await using var source = NpgsqlDataSource.Create(database.ConnectionString);
        await PostgresMessagingSchema.EnsureAsync(source);
        using var protector = AesGcmPayloadProtectorTests.CreateProtector("original", "elapsed-legacy-key");
        PostgresApplicationBus bus = presentation
            ? new PostgresPresentationBus(source, protector) : new PostgresApplicationBus(source, protector);
        var request = Request(operation) with
        {
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-2),
            Deadline = DateTimeOffset.UtcNow.AddMinutes(-1),
        };
        await bus.EnqueueAsync(request);
        var table = presentation ? "presentation_requests" : "application_requests";
        var before = await SnapshotAsync(source, table, request.Id);
        Assert.IsFalse(await DistributedQueueContractGuard.IsCompatibleAsync(source));
        Assert.IsNull(await bus.TryClaimAsync("new-role"));
        Assert.IsFalse(await Control(source).IsAvailableAsync());
        Assert.AreEqual(before, await SnapshotAsync(source, table, request.Id));
    }

    [TestMethod]
    public async Task V19MailOperationsAreBlockedBeforeV20Dispatch()
    {
        await SupersededPendingAndLeasedWorkBlocksWithoutChangingOpaqueData(
            "mail.operation.execute.v19", false, false);
        await SupersededPendingAndLeasedWorkBlocksWithoutChangingOpaqueData(
            "mail.operation.execute.v19", false, true);
        await ElapsedLegacyDeadlinesRemainBlockedAndAreNotAutomaticallyExpired(
            "mail.operation.execute.v19", false);
    }

    [TestMethod]
    public async Task V20MailOperationsAreBlockedBeforeV21Dispatch()
    {
        await SupersededPendingAndLeasedWorkBlocksWithoutChangingOpaqueData(
            "mail.operation.execute.v20", false, false);
        await SupersededPendingAndLeasedWorkBlocksWithoutChangingOpaqueData(
            "mail.operation.execute.v20", false, true);
        await ElapsedLegacyDeadlinesRemainBlockedAndAreNotAutomaticallyExpired(
            "mail.operation.execute.v20", false);
    }

    [TestMethod]
    public async Task V21MailOperationsAreBlockedBeforeV22Dispatch()
    {
        await SupersededPendingAndLeasedWorkBlocksWithoutChangingOpaqueData(
            "mail.operation.execute.v21", false, false);
        await SupersededPendingAndLeasedWorkBlocksWithoutChangingOpaqueData(
            "mail.operation.execute.v21", false, true);
        await ElapsedLegacyDeadlinesRemainBlockedAndAreNotAutomaticallyExpired(
            "mail.operation.execute.v21", false);
    }

    [TestMethod]
    public async Task V22MailOperationsAreBlockedBeforeV23Dispatch()
    {
        await SupersededPendingAndLeasedWorkBlocksWithoutChangingOpaqueData(
            "mail.operation.execute.v22", false, false);
        await SupersededPendingAndLeasedWorkBlocksWithoutChangingOpaqueData(
            "mail.operation.execute.v22", false, true);
        await ElapsedLegacyDeadlinesRemainBlockedAndAreNotAutomaticallyExpired(
            "mail.operation.execute.v22", false);
    }

    [TestMethod]
    public async Task V23MailOperationsAreBlockedBeforeV24Dispatch()
    {
        await SupersededPendingAndLeasedWorkBlocksWithoutChangingOpaqueData(
            "mail.operation.execute.v23", false, false);
        await SupersededPendingAndLeasedWorkBlocksWithoutChangingOpaqueData(
            "mail.operation.execute.v23", false, true);
        await ElapsedLegacyDeadlinesRemainBlockedAndAreNotAutomaticallyExpired(
            "mail.operation.execute.v23", false);
    }

    [TestMethod]
    public async Task V24MailOperationsAreBlockedBeforeV25Dispatch()
    {
        await SupersededPendingAndLeasedWorkBlocksWithoutChangingOpaqueData(
            "mail.operation.execute.v24", false, false);
        await SupersededPendingAndLeasedWorkBlocksWithoutChangingOpaqueData(
            "mail.operation.execute.v24", false, true);
        await ElapsedLegacyDeadlinesRemainBlockedAndAreNotAutomaticallyExpired(
            "mail.operation.execute.v24", false);
    }

    [TestMethod]
    public async Task V25MailOperationsAreBlockedBeforeV26Dispatch()
    {
        await SupersededPendingAndLeasedWorkBlocksWithoutChangingOpaqueData(
            "mail.operation.execute.v25", false, false);
        await SupersededPendingAndLeasedWorkBlocksWithoutChangingOpaqueData(
            "mail.operation.execute.v25", false, true);
        await ElapsedLegacyDeadlinesRemainBlockedAndAreNotAutomaticallyExpired(
            "mail.operation.execute.v25", false);
    }

    [TestMethod]
    public async Task V26MailOperationsAreBlockedBeforeV27Dispatch()
    {
        await SupersededPendingAndLeasedWorkBlocksWithoutChangingOpaqueData(
            "mail.operation.execute.v26", false, false);
        await SupersededPendingAndLeasedWorkBlocksWithoutChangingOpaqueData(
            "mail.operation.execute.v26", false, true);
        await ElapsedLegacyDeadlinesRemainBlockedAndAreNotAutomaticallyExpired(
            "mail.operation.execute.v26", false);
    }

    [TestMethod]
    public async Task V27MailOperationsAreBlockedBeforeV28Dispatch()
    {
        await SupersededPendingAndLeasedWorkBlocksWithoutChangingOpaqueData(
            "mail.operation.execute.v27", false, false);
        await SupersededPendingAndLeasedWorkBlocksWithoutChangingOpaqueData(
            "mail.operation.execute.v27", false, true);
        await ElapsedLegacyDeadlinesRemainBlockedAndAreNotAutomaticallyExpired(
            "mail.operation.execute.v27", false);
    }

    [TestMethod]
    public async Task V28MailOperationsAreBlockedBeforeV29Dispatch()
    {
        await SupersededPendingAndLeasedWorkBlocksWithoutChangingOpaqueData(
            "mail.operation.execute.v28", false, false);
        await SupersededPendingAndLeasedWorkBlocksWithoutChangingOpaqueData(
            "mail.operation.execute.v28", false, true);
        await ElapsedLegacyDeadlinesRemainBlockedAndAreNotAutomaticallyExpired(
            "mail.operation.execute.v28", false);
    }

    [TestMethod]
    public async Task FreshSchemasAndCurrentOrTerminalWorkAreCompatible()
    {
        await using var database = await RequirePostgresAsync();
        await using var source = NpgsqlDataSource.Create(database.ConnectionString);
        Assert.IsTrue(await DistributedQueueContractGuard.IsCompatibleAsync(source));
        await PostgresMessagingSchema.EnsureAsync(source);
        using var protector = AesGcmPayloadProtectorTests.CreateProtector("test", "compatible-key");
        var application = new PostgresApplicationBus(source, protector);
        var presentation = new PostgresPresentationBus(source, protector);
        await application.EnqueueAsync(Request(ApplicationOperations.MailOperationExecute));
        await presentation.EnqueueAsync(Request(WebPushPresentationOperations.Send));
        Assert.IsTrue(await Control(source).IsAvailableAsync());
        var current = await application.TryClaimAsync("current-role");
        Assert.IsNotNull(current);
        await application.CompleteAsync(current, new ApplicationResponse(current.Request.Id, "application/json", "{}"u8.ToArray(), new Dictionary<string, string>()));
        var old = Request("jmap.batch.execute.v2");
        await application.EnqueueAsync(old);
        var legacy = await application.TryClaimAsync("original-role");
        Assert.IsNotNull(legacy);
        Assert.AreEqual(old.Id, legacy.Request.Id);
        await application.FailAsync(legacy, "operator-reconciled", "Test operator reconciliation.");
        Assert.IsTrue(await Control(source).IsAvailableAsync());
    }

    [TestMethod]
    public async Task IncompleteQueueSchemaFailsClosed()
    {
        await using var database = await RequirePostgresAsync();
        await using var source = NpgsqlDataSource.Create(database.ConnectionString);
        await using var command = source.CreateCommand("CREATE TABLE public.application_requests (id uuid)");
        await command.ExecuteNonQueryAsync();
        Assert.IsFalse(await DistributedQueueContractGuard.IsCompatibleAsync(source));
        await Assert.ThrowsAsync<InvalidOperationException>(() => DistributedQueueContractGuard.RequireCompatibleAsync(source));
    }

    [TestMethod]
    public async Task GatewayRecordsRejectedWorkButDoesNotQueueItWhenReconciliationIsRequired()
    {
        await using var database = await RequirePostgresAsync();
        await using var source = NpgsqlDataSource.Create(database.ConnectionString);
        await PostgresMessagingSchema.EnsureAsync(source);
        using var protector = AesGcmPayloadProtectorTests.CreateProtector("test", "record-rejection-key");
        var bus = new PostgresApplicationBus(source, protector);
        var journal = new PostgresGatewayTrafficJournal(source, protector);
        await bus.EnqueueAsync(Request("jmap.session.get"));
        var gateway = new GatewayApplicationTransport(bus, journal,
            new GatewayApplicationOptions("gateway@test", TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5)), Control(source));
        var error = await Assert.ThrowsAsync<GatewayApplicationException>(() =>
            gateway.SendAsync<object, SystemPingResult>("health", ApplicationOperations.SystemPing, new { }));
        Assert.IsTrue(error.IsUnavailable);
        Assert.AreEqual("application-unavailable", error.Code);
        await using var count = source.CreateCommand("SELECT count(*) FROM public.application_requests");
        Assert.AreEqual(1L, await count.ExecuteScalarAsync());
        await using var session = source.CreateCommand("SELECT DISTINCT session_id FROM public.gateway_traffic_records");
        var sessionId = (Guid)(await session.ExecuteScalarAsync())!;
        var records = await journal.ReadSessionAsync(sessionId);
        Assert.HasCount(2, records);
        Assert.AreEqual(GatewayTrafficDirections.Inbound, records[0].Direction);
        Assert.AreEqual(GatewayTrafficDirections.Outbound, records[1].Direction);
        using var payload = JsonDocument.Parse(records[1].Payload);
        Assert.AreEqual("application-unavailable", payload.RootElement.GetProperty("code").GetString());
    }

    [TestMethod]
    public async Task CancellationDuringAvailabilityCheckIsJournaledWithoutQueuingWork()
    {
        await using var database = await RequirePostgresAsync();
        await using var source = NpgsqlDataSource.Create(database.ConnectionString);
        await PostgresMessagingSchema.EnsureAsync(source);
        using var protector = AesGcmPayloadProtectorTests.CreateProtector("test", "record-cancellation-key");
        var bus = new PostgresApplicationBus(source, protector);
        var journal = new PostgresGatewayTrafficJournal(source, protector);
        using var cancellation = new CancellationTokenSource();
        var gateway = new GatewayApplicationTransport(bus, journal,
            new GatewayApplicationOptions("gateway@test", TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5)),
            new CancelingAvailability(cancellation));
        await Assert.ThrowsAsync<OperationCanceledException>(() => gateway.SendAsync<object, SystemPingResult>(
            "health", ApplicationOperations.SystemPing, new { }, cancellation.Token));
        await using var count = source.CreateCommand("SELECT count(*) FROM public.application_requests");
        Assert.AreEqual(0L, await count.ExecuteScalarAsync());
        await using var session = source.CreateCommand("SELECT DISTINCT session_id FROM public.gateway_traffic_records");
        var records = await journal.ReadSessionAsync((Guid)(await session.ExecuteScalarAsync())!);
        Assert.HasCount(2, records);
        Assert.AreEqual(GatewayTrafficDirections.Outbound, records[1].Direction);
        using var payload = JsonDocument.Parse(records[1].Payload);
        Assert.AreEqual("gateway-request-cancelled", payload.RootElement.GetProperty("code").GetString());
    }

    [TestMethod]
    public async Task ApplicationLeaseArrivingAfterPreflightIsNotCompletedOrFailed()
    {
        await using var database = await RequirePostgresAsync();
        await using var source = NpgsqlDataSource.Create(database.ConnectionString);
        await PostgresMessagingSchema.EnsureAsync(source);
        Assert.IsTrue(await DistributedQueueContractGuard.IsCompatibleAsync(source));
        using var protector = AesGcmPayloadProtectorTests.CreateProtector("original", "application-race-key");
        var bus = new PostgresApplicationBus(source, protector);
        await bus.EnqueueAsync(Request("jmap.batch.execute.v2"));
        var lease = await bus.TryClaimAsync("new-role");
        Assert.IsNotNull(lease);
        var before = await SnapshotAsync(source, "application_requests", lease.Request.Id);
        var dispatcher = new RejectingDispatcher();
        await using var services = new ServiceCollection().AddSingleton<IApplicationRequestDispatcher>(dispatcher).BuildServiceProvider();
        using var worker = new ApplicationRequestWorker(bus, services.GetRequiredService<IServiceScopeFactory>(),
            new ApplicationWorkerIdentity("new-role", TimeSpan.FromSeconds(1)), NullLogger<ApplicationRequestWorker>.Instance);
        await Assert.ThrowsAsync<InvalidOperationException>(() => worker.ProcessLeaseAsync(lease, CancellationToken.None));
        Assert.AreEqual(0, dispatcher.Calls);
        Assert.AreEqual(before, await SnapshotAsync(source, "application_requests", lease.Request.Id));
    }

    [TestMethod]
    public async Task GatewayLeaseRaceKeepsPresentationWorkerAliveAndPreservesWork()
    {
        await using var database = await RequirePostgresAsync();
        await using var source = NpgsqlDataSource.Create(database.ConnectionString);
        await PostgresMessagingSchema.EnsureAsync(source);
        using var protector = AesGcmPayloadProtectorTests.CreateProtector("original", "presentation-race-key");
        var bus = new PostgresPresentationBus(source, protector, new PostgresMessagingOptions { NotificationFallbackInterval = TimeSpan.FromSeconds(1) });
        var availability = new AvailabilityBarrier(Control(source));
        var journal = new PostgresGatewayTrafficJournal(source, protector);
        using var sender = new GatewayWebPushService(new RejectingHandler(), journal, 1_048_576);
        using var worker = new GatewayPresentationWorker(bus, availability, journal, sender, new RejectingRelay(),
            new EnvironmentConfig(), NullLogger<GatewayPresentationWorker>.Instance);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await worker.StartAsync(timeout.Token);
        try
        {
            await availability.FirstCheck.Task.WaitAsync(timeout.Token);
            var request = Request("webpush.send");
            await bus.EnqueueAsync(request, timeout.Token);
            await availability.Blocked.Task.WaitAsync(timeout.Token);
            Assert.IsFalse(worker.ExecuteTask!.IsCompleted);
            using var snapshot = JsonDocument.Parse(await SnapshotAsync(source, "presentation_requests", request.Id));
            Assert.AreEqual("processing", snapshot.RootElement.GetProperty("state").GetString());
            Assert.AreEqual(JsonValueKind.Null, snapshot.RootElement.GetProperty("completed_at").ValueKind);
            Assert.AreEqual(1, snapshot.RootElement.GetProperty("attempt_count").GetInt32());
            await using var traffic = source.CreateCommand("SELECT count(*) FROM public.gateway_traffic_records");
            Assert.AreEqual(0L, await traffic.ExecuteScalarAsync(timeout.Token));
        }
        finally
        {
            await worker.StopAsync(timeout.Token);
        }
    }

    private static RestoreAwareApplicationTransportControl Control(NpgsqlDataSource source) =>
        new(source, new PostgresApplicationTransportControl(source));

    private static ApplicationRequest Request(string operation) => new(
        Guid.CreateVersion7(), Guid.CreateVersion7(), 0, "test", operation, "application/json",
        "original opaque document, not a modern DTO"u8.ToArray(), new Dictionary<string, string>(),
        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(5));

    private static async Task<string> SnapshotAsync(NpgsqlDataSource source, string table, Guid id)
    {
        Assert.IsTrue(table is "application_requests" or "presentation_requests");
        await using var command = source.CreateCommand($"SELECT to_jsonb(queue_row)::text FROM public.{table} AS queue_row WHERE id = @id");
        command.Parameters.AddWithValue("id", id);
        return (string)(await command.ExecuteScalarAsync())!;
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

    private sealed class RejectingDispatcher : IApplicationRequestDispatcher
    {
        public int Calls { get; private set; }
        public Task<ApplicationResponse> DispatchAsync(ApplicationRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            throw new AssertFailedException("A superseded operation must not enter modern dispatch.");
        }
    }

    private sealed class AvailabilityBarrier(IApplicationTransportControl inner) : IApplicationTransportControl
    {
        public TaskCompletionSource FirstCheck { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Blocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
        {
            var result = await inner.IsAvailableAsync(cancellationToken);
            if (result) FirstCheck.TrySetResult();
            else Blocked.TrySetResult();
            return result;
        }
    }

    private sealed class CancelingAvailability(CancellationTokenSource cancellation) : IApplicationTransportControl
    {
        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
        {
            cancellation.Cancel();
            throw new OperationCanceledException(cancellationToken);
        }
    }

    private sealed class RejectingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new AssertFailedException("Superseded work must not reach HTTP delivery.");
    }

    private sealed class RejectingRelay : ISmtpPresentationRelay
    {
        public Task<OutboundDeliveryResult> RelayAsync(SmtpRelayPresentationRequest request, Guid applicationRequestId, CancellationToken cancellationToken) =>
            throw new AssertFailedException("Superseded work must not reach SMTP delivery.");
    }
}
