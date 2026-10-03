using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using mk8.email.Contracts.Messaging;
using mk8.email.Hosting;
using mk8.email.Infrastructure.Data;
using Npgsql;

namespace mk8.email.Messaging.Tests;

[TestClass]
[TestCategory("PostgreSQL")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1812", Justification = "MSTest DiscoverInternals instantiates this test class by reflection; focused discovery is verified by executed test counts.")]
internal sealed class DistributedMessagingKeyAuditTests
{
    [TestMethod]
    public async Task AuditsBothRequestLanesJournalAndBlobBackedReceiptBeforeKeyRetirement()
    {
        var database = (await RequirePostgresAsync().ConfigureAwait(false));
        await using var databaseLifetime = database.ConfigureAwait(false);
        await PrepareAsync(database).ConfigureAwait(false);
        var source = NpgsqlDataSource.Create(database.ConnectionString);
        await using var sourceLifetime = source.ConfigureAwait(false);
        var objects = new InMemoryLargeObjectStore();
        using var current = AesGcmPayloadProtectorTests.CreateProtector("current", "current-key");
        using var previous = AesGcmPayloadProtectorTests.CreateProtector("previous", "previous-key");

        var now = DateTimeOffset.UtcNow;
        var journal = new PostgresGatewayTrafficJournal(source, previous);
        await journal.AppendAsync(new GatewayTrafficRecord(
            Guid.CreateVersion7(), Guid.CreateVersion7(), 0, GatewayTrafficDirections.Inbound,
            "imap", "application/octet-stream", "secret traffic"u8.ToArray(),
            new Dictionary<string, string>(StringComparer.Ordinal), now)).ConfigureAwait(false);

        var application = new PostgresApplicationBus(source, current);
        var presentation = new PostgresPresentationBus(source, previous);
        await CompleteAsync(application, NewRequest(now)).ConfigureAwait(false);
        await CompleteAsync(presentation, NewRequest(now)).ConfigureAwait(false);

        var receipt = await StoreReceiptAsync(source, objects, previous).ConfigureAwait(false);
        Assert.AreEqual(6L, await DistributedMessagingKeyAudit.AuditAsync(
            source, objects, ["current", "previous"], 4096).ConfigureAwait(false));
        var missing = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            DistributedMessagingKeyAudit.AuditAsync(source, objects, ["current"], 4096)).ConfigureAwait(false);
        StringAssert.Contains(missing.Message, "configured key ring", StringComparison.Ordinal);
        Assert.IsFalse(missing.Message.Contains("secret traffic", StringComparison.Ordinal));

        objects.Corrupt(receipt);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            DistributedMessagingKeyAudit.AuditAsync(source, objects, ["current", "previous"], 4096)).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task ReceiptOnlyKeyIsRequiredEvenWhenNoQueueRowsRemain()
    {
        var database = (await RequirePostgresAsync().ConfigureAwait(false));
        await using var databaseLifetime = database.ConfigureAwait(false);
        await PrepareAsync(database).ConfigureAwait(false);
        var source = NpgsqlDataSource.Create(database.ConnectionString);
        await using var sourceLifetime = source.ConfigureAwait(false);
        var objects = new InMemoryLargeObjectStore();
        using var previous = AesGcmPayloadProtectorTests.CreateProtector("previous", "previous-key");
        await StoreReceiptAsync(source, objects, previous).ConfigureAwait(false);

        Assert.AreEqual(1L, await DistributedMessagingKeyAudit.AuditAsync(
            source, objects, ["current", "previous"], 4096).ConfigureAwait(false));
        var failure = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            DistributedMessagingKeyAudit.AuditAsync(source, objects, ["current"], 4096)).ConfigureAwait(false);
        StringAssert.Contains(failure.Message, "unavailable messaging key", StringComparison.Ordinal);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            DistributedMessagingKeyAudit.AuditAsync(source, objects, ["previous"], 1)).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task UnknownEncryptedKeyColumnFailsClosedEvenWhenNoRowsUseIt()
    {
        var database = (await RequirePostgresAsync().ConfigureAwait(false));
        await using var databaseLifetime = database.ConfigureAwait(false);
        await PrepareAsync(database).ConfigureAwait(false);
        var source = NpgsqlDataSource.Create(database.ConnectionString);
        await using var sourceLifetime = source.ConfigureAwait(false);
        var alter = source.CreateCommand(
            "ALTER TABLE gateway_traffic_records ADD COLUMN future_encryption_key_id varchar(64)");
        await using var alterLifetime = alter.ConfigureAwait(false);
        await alter.ExecuteNonQueryAsync().ConfigureAwait(false);

        var failure = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            DistributedMessagingKeyAudit.AuditAsync(
                source, new InMemoryLargeObjectStore(), ["current"], 4096)).ConfigureAwait(false);
        StringAssert.Contains(failure.Message, "key-column schema changed", StringComparison.Ordinal);
    }

    private static ApplicationRequest NewRequest(DateTimeOffset now) => new(
        Guid.CreateVersion7(), Guid.CreateVersion7(), 0, "admin", "accounts.create",
        "application/json", "secret request"u8.ToArray(),
        new Dictionary<string, string>(StringComparer.Ordinal), now, now.AddMinutes(2), Guid.NewGuid().ToString("N"));

    private static async Task CompleteAsync(PostgresApplicationBus bus, ApplicationRequest request)
    {
        await bus.EnqueueAsync(request).ConfigureAwait(false);
        var lease = await bus.TryClaimAsync("audit-worker").ConfigureAwait(false);
        Assert.IsNotNull(lease);
        await bus.CompleteAsync(lease, new ApplicationResponse(
            request.Id, "application/json", "secret response"u8.ToArray(),
            new Dictionary<string, string>(StringComparer.Ordinal))).ConfigureAwait(false);
    }

    private static async Task<string> StoreReceiptAsync(
        NpgsqlDataSource source, InMemoryLargeObjectStore objects, AesGcmPayloadProtector protector)
    {
        var envelope = new MessagingStoredContentProtector(protector)
            .Protect("encrypted receipt"u8, "receipt-associated-data"u8).ToArray();
        var sha256 = Convert.ToHexStringLower(SHA256.HashData(envelope));
        var name = $"application-receipts/audit/{Guid.CreateVersion7():N}";
        var content = new MemoryStream(envelope, writable: false);
        await using var contentLifetime = content.ConfigureAwait(false);
        var written = await objects.PutIfAbsentAsync(
            name, content, envelope.LongLength, sha256,
            "application/vnd.mk8.protected-receipt+json").ConfigureAwait(false);
        var command = source.CreateCommand("""
            INSERT INTO application_operation_receipts (
                id, operation_id, step_number, user_id, purpose, payload_object_provider,
                payload_object_name, payload_object_sha256, payload_object_etag, payload_length,
                created_at, effects_pending, effects_retry_at)
            VALUES (@id, @operation, 0, @user, 'mail.operation', @provider,
                @name, @hash, @etag, @length, now(), false, now())
            """);
        await using var commandLifetime = command.ConfigureAwait(false);
        command.Parameters.AddWithValue("id", Guid.CreateVersion7());
        command.Parameters.AddWithValue("operation", Guid.CreateVersion7());
        command.Parameters.AddWithValue("user", Guid.CreateVersion7());
        command.Parameters.AddWithValue("provider", written.Reference.Provider);
        command.Parameters.AddWithValue("name", written.Reference.ObjectName);
        command.Parameters.AddWithValue("hash", written.Reference.Sha256);
        command.Parameters.AddWithValue("etag", written.Reference.EntityTag);
        command.Parameters.AddWithValue("length", written.Reference.Length);
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        return name;
    }

    private static async Task PrepareAsync(PostgresTestDatabase testDatabase)
    {
        var context = new EmailDbContext(
            new DbContextOptionsBuilder<EmailDbContext>()
                .UseNpgsql(testDatabase.ConnectionString).Options);
        await using var contextLifetime = context.ConfigureAwait(false);
        await context.Database.EnsureCreatedAsync().ConfigureAwait(false);
        await new MailRuntimeSchemaService(context).EnsureAsync().ConfigureAwait(false);
        var source = NpgsqlDataSource.Create(testDatabase.ConnectionString);
        await using var sourceLifetime = source.ConfigureAwait(false);
        await PostgresMessagingSchema.EnsureAsync(source).ConfigureAwait(false);
    }

    private static async Task<PostgresTestDatabase> RequirePostgresAsync()
    {
        var database = await PostgresTestDatabase.TryCreateAsync().ConfigureAwait(false);
        if (database is null)
            Assert.Inconclusive("Set MK8_EMAIL_TEST_POSTGRES.");
        return database!;
    }
}
