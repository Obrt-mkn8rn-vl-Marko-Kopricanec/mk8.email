using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using mk8.email.Contracts.Messaging;
using mk8.email.Hosting;
using mk8.email.Infrastructure.Data;
using Npgsql;

namespace mk8.email.Messaging.Tests;

[TestClass]
[TestCategory("PostgreSQL")]
public sealed class DistributedMessagingKeyAuditTests
{
    [TestMethod]
    public async Task AuditsBothRequestLanesJournalAndBlobBackedReceiptBeforeKeyRetirement()
    {
        await using var database = await RequirePostgresAsync();
        await PrepareAsync(database);
        await using var source = NpgsqlDataSource.Create(database.ConnectionString);
        var objects = new InMemoryLargeObjectStore();
        using var current = AesGcmPayloadProtectorTests.CreateProtector("current", "current-key");
        using var previous = AesGcmPayloadProtectorTests.CreateProtector("previous", "previous-key");

        var now = DateTimeOffset.UtcNow;
        var journal = new PostgresGatewayTrafficJournal(source, previous);
        await journal.AppendAsync(new GatewayTrafficRecord(
            Guid.CreateVersion7(), Guid.CreateVersion7(), 0, GatewayTrafficDirections.Inbound,
            "imap", "application/octet-stream", "secret traffic"u8.ToArray(),
            new Dictionary<string, string>(), now));

        var application = new PostgresApplicationBus(source, current);
        var presentation = new PostgresPresentationBus(source, previous);
        await CompleteAsync(application, NewRequest(now));
        await CompleteAsync(presentation, NewRequest(now));

        var receipt = await StoreReceiptAsync(source, objects, previous);
        Assert.AreEqual(6L, await DistributedMessagingKeyAudit.AuditAsync(
            source, objects, ["current", "previous"], 4096));
        var missing = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            DistributedMessagingKeyAudit.AuditAsync(source, objects, ["current"], 4096));
        StringAssert.Contains(missing.Message, "configured key ring");
        Assert.IsFalse(missing.Message.Contains("secret traffic", StringComparison.Ordinal));

        objects.Corrupt(receipt);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            DistributedMessagingKeyAudit.AuditAsync(source, objects, ["current", "previous"], 4096));
    }

    [TestMethod]
    public async Task ReceiptOnlyKeyIsRequiredEvenWhenNoQueueRowsRemain()
    {
        await using var database = await RequirePostgresAsync();
        await PrepareAsync(database);
        await using var source = NpgsqlDataSource.Create(database.ConnectionString);
        var objects = new InMemoryLargeObjectStore();
        using var previous = AesGcmPayloadProtectorTests.CreateProtector("previous", "previous-key");
        await StoreReceiptAsync(source, objects, previous);

        Assert.AreEqual(1L, await DistributedMessagingKeyAudit.AuditAsync(
            source, objects, ["current", "previous"], 4096));
        var failure = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            DistributedMessagingKeyAudit.AuditAsync(source, objects, ["current"], 4096));
        StringAssert.Contains(failure.Message, "unavailable messaging key");
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            DistributedMessagingKeyAudit.AuditAsync(source, objects, ["previous"], 1));
    }

    [TestMethod]
    public async Task UnknownEncryptedKeyColumnFailsClosedEvenWhenNoRowsUseIt()
    {
        await using var database = await RequirePostgresAsync();
        await PrepareAsync(database);
        await using var source = NpgsqlDataSource.Create(database.ConnectionString);
        await using var alter = source.CreateCommand(
            "ALTER TABLE gateway_traffic_records ADD COLUMN future_encryption_key_id varchar(64)");
        await alter.ExecuteNonQueryAsync();

        var failure = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            DistributedMessagingKeyAudit.AuditAsync(
                source, new InMemoryLargeObjectStore(), ["current"], 4096));
        StringAssert.Contains(failure.Message, "key-column schema changed");
    }

    private static ApplicationRequest NewRequest(DateTimeOffset now) => new(
        Guid.CreateVersion7(), Guid.CreateVersion7(), 0, "admin", "accounts.create",
        "application/json", "secret request"u8.ToArray(),
        new Dictionary<string, string>(), now, now.AddMinutes(2), Guid.NewGuid().ToString("N"));

    private static async Task CompleteAsync(PostgresApplicationBus bus, ApplicationRequest request)
    {
        await bus.EnqueueAsync(request);
        var lease = await bus.TryClaimAsync("audit-worker");
        Assert.IsNotNull(lease);
        await bus.CompleteAsync(lease, new ApplicationResponse(
            request.Id, "application/json", "secret response"u8.ToArray(),
            new Dictionary<string, string>()));
    }

    private static async Task<string> StoreReceiptAsync(
        NpgsqlDataSource source, InMemoryLargeObjectStore objects, AesGcmPayloadProtector protector)
    {
        var envelope = new MessagingStoredContentProtector(protector)
            .Protect("encrypted receipt"u8, "receipt-associated-data"u8).ToArray();
        var sha256 = Convert.ToHexStringLower(SHA256.HashData(envelope));
        var name = $"application-receipts/audit/{Guid.CreateVersion7():N}";
        await using var content = new MemoryStream(envelope, writable: false);
        var written = await objects.PutIfAbsentAsync(
            name, content, envelope.LongLength, sha256,
            "application/vnd.mk8.protected-receipt+json");
        await using var command = source.CreateCommand("""
            INSERT INTO application_operation_receipts (
                id, operation_id, step_number, user_id, purpose, payload_object_provider,
                payload_object_name, payload_object_sha256, payload_object_etag, payload_length,
                created_at, effects_pending, effects_retry_at)
            VALUES (@id, @operation, 0, @user, 'mail.operation', @provider,
                @name, @hash, @etag, @length, now(), false, now())
            """);
        command.Parameters.AddWithValue("id", Guid.CreateVersion7());
        command.Parameters.AddWithValue("operation", Guid.CreateVersion7());
        command.Parameters.AddWithValue("user", Guid.CreateVersion7());
        command.Parameters.AddWithValue("provider", written.Reference.Provider);
        command.Parameters.AddWithValue("name", written.Reference.ObjectName);
        command.Parameters.AddWithValue("hash", written.Reference.Sha256);
        command.Parameters.AddWithValue("etag", written.Reference.EntityTag);
        command.Parameters.AddWithValue("length", written.Reference.Length);
        await command.ExecuteNonQueryAsync();
        return name;
    }

    private static async Task PrepareAsync(PostgresTestDatabase testDatabase)
    {
        await using var context = new EmailDbContext(
            new DbContextOptionsBuilder<EmailDbContext>()
                .UseNpgsql(testDatabase.ConnectionString).Options);
        await context.Database.EnsureCreatedAsync();
        await new MailRuntimeSchemaService(context).EnsureAsync();
        await using var source = NpgsqlDataSource.Create(testDatabase.ConnectionString);
        await PostgresMessagingSchema.EnsureAsync(source);
    }

    private static async Task<PostgresTestDatabase> RequirePostgresAsync()
    {
        var database = await PostgresTestDatabase.TryCreateAsync();
        if (database is null)
            Assert.Inconclusive("Set MK8_EMAIL_TEST_POSTGRES.");
        return database!;
    }
}
