using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using mk8.email.Configuration;
using mk8.email.Contracts.Messaging;
using mk8.email.Hosting;
using mk8.email.Infrastructure.Data;
using mk8.email.Infrastructure.Models;
using mk8.email.Wake;
using Npgsql;

namespace mk8.email.Messaging.Tests;

[TestClass]
[DoNotParallelize]
[TestCategory("PostgreSQL")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1812", Justification = "MSTest DiscoverInternals instantiates this test class by reflection; focused discovery is verified by executed test counts.")]
internal sealed class WorkerWakeSchemaTransitionTests
{
    [TestMethod]
    public async Task LegacyUpgradeAndEmptyRollbackRestoreSchemaAndExactPermissions()
    {
        var fixture = (await Fixture.CreateAsync(legacy: true).ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        Assert.AreEqual(WorkerWakeSchemaState.Legacy, await WorkerWakeSchemaTransition.ReadAsync(fixture.Source, fixture.Role).ConfigureAwait(false));
        await Assert.ThrowsAsync<InvalidOperationException>(() => WorkerWakeSchemaTransition.EnableAsync(fixture.Source, fixture.Role)).ConfigureAwait(false);
        await fixture.PrepareAsync().ConfigureAwait(false);
        await Assert.ThrowsAsync<InvalidOperationException>(() => WorkerWakeSchemaTransition.ReadAsync(fixture.Source, fixture.Role)).ConfigureAwait(false);
        await WorkerWakeSchemaTransition.EnableAsync(fixture.Source, fixture.Role).ConfigureAwait(false);
        await WorkerWakeSchemaTransition.EnableAsync(fixture.Source, fixture.Role).ConfigureAwait(false);
        Assert.AreEqual(WorkerWakeSchemaState.Receipts, await WorkerWakeSchemaTransition.ReadAsync(fixture.Source, fixture.Role).ConfigureAwait(false));
        await WorkerWakeDatabasePrivilegeProbe.ProbeAsync(fixture.Wake).ConfigureAwait(false);
        await WorkerWakeSchemaTransition.RestoreAsync(fixture.Source, fixture.Role, WorkerWakeSchemaState.Legacy).ConfigureAwait(false);
        Assert.AreEqual(WorkerWakeSchemaState.Legacy, await WorkerWakeSchemaTransition.ReadAsync(fixture.Source, fixture.Role).ConfigureAwait(false));
        await WorkerWakeSchemaTransition.RestoreAsync(fixture.Source, fixture.Role, WorkerWakeSchemaState.Legacy).ConfigureAwait(false);
        var table = fixture.Source.CreateCommand("SELECT to_regclass('public.application_operation_receipts') IS NULL");
        await using var tableLifetime = table.ConfigureAwait(false);
        Assert.AreEqual(true, await table.ExecuteScalarAsync().ConfigureAwait(false));
        var access = fixture.Source.CreateCommand("SELECT has_column_privilege(@role, 'public.users', 'id', 'SELECT')");
        await using var accessLifetime = access.ConfigureAwait(false);
        access.Parameters.AddWithValue("role", fixture.Role);
        Assert.AreEqual(true, await access.ExecuteScalarAsync().ConfigureAwait(false));
    }

    [TestMethod]
    public async Task OperatorCliExecutesSchemaGrantAndGuardedRestoreAgainstPostgres()
    {
        var fixture = (await Fixture.CreateAsync(legacy: true).ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        var directory = Directory.CreateTempSubdirectory("mk8-wake-operator-");
        try
        {
            var connection = new NpgsqlConnectionStringBuilder(fixture.Database.ConnectionString);
            var config = new EnvironmentConfig
            {
                Database = new DatabaseConfig
                {
                    Host = connection.Host!,
                    Port = connection.Port,
                    Name = connection.Database!,
                    Username = connection.Username!,
                    Password = string.IsNullOrEmpty(connection.Password) ? "local-test-only" : connection.Password,
                },
                Smtp = new SmtpConfig { Hostname = "mail.example.test" },
                Messaging = new MessagingConfig { Enabled = true, EncryptionKey = Convert.ToBase64String(new byte[32]) },
                ObjectStorage = new ObjectStorageConfig { ConnectionString = "UseDevelopmentStorage=true" },
            };
            var configPath = Path.Combine(directory.FullName, "worker.json");
            var wakePath = Path.Combine(directory.FullName, "wake.connection");
            await File.WriteAllTextAsync(configPath, JsonSerializer.Serialize(config)).ConfigureAwait(false);
            await File.WriteAllTextAsync(wakePath, fixture.Wake.ConnectionString).ConfigureAwait(false);
            var legacy = await ManagementCliCommandBoundaryTests.RunCliAsync(configPath,
                ["--worker-wake-schema-state", configPath, wakePath], development: true).ConfigureAwait(false);
            Assert.AreEqual(0, legacy.ExitCode, legacy.Output);
            Assert.AreEqual("legacy", legacy.Output.Trim(), StringComparer.Ordinal);
            await fixture.PrepareAsync().ConfigureAwait(false);
            var enabled = await ManagementCliCommandBoundaryTests.RunCliAsync(configPath,
                ["--prepare-worker-wake", configPath, wakePath], development: true).ConfigureAwait(false);
            Assert.AreEqual(0, enabled.ExitCode, enabled.Output);
            await WorkerWakeDatabasePrivilegeProbe.ProbeAsync(fixture.Wake).ConfigureAwait(false);
            var receipts = await ManagementCliCommandBoundaryTests.RunCliAsync(configPath,
                ["--worker-wake-schema-state", configPath, wakePath], development: true).ConfigureAwait(false);
            Assert.AreEqual(0, receipts.ExitCode, receipts.Output);
            Assert.AreEqual("receipts", receipts.Output.Trim(), StringComparer.Ordinal);
            var restored = await ManagementCliCommandBoundaryTests.RunCliAsync(configPath,
                ["--restore-worker-wake", configPath, wakePath, "legacy"], development: true).ConfigureAwait(false);
            Assert.AreEqual(0, restored.ExitCode, restored.Output);
            Assert.AreEqual(WorkerWakeSchemaState.Legacy,
                await WorkerWakeSchemaTransition.ReadAsync(fixture.Source, fixture.Role).ConfigureAwait(false));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [TestMethod]
    public async Task CommittedReceiptBlocksLegacyRollbackWithoutChangingRowsOrGrants()
    {
        var fixture = (await Fixture.CreateAsync().ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        await fixture.EnableAsync().ConfigureAwait(false);
        await fixture.AddReceiptAsync().ConfigureAwait(false);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            WorkerWakeSchemaTransition.RestoreAsync(fixture.Source, fixture.Role, WorkerWakeSchemaState.Legacy)).ConfigureAwait(false);
        var database = fixture.Context();
        await using var databaseLifetime = database.ConfigureAwait(false);
        Assert.AreEqual(1, await database.ApplicationOperationReceipts.CountAsync().ConfigureAwait(false));
        await WorkerWakeDatabasePrivilegeProbe.ProbeAsync(fixture.Wake).ConfigureAwait(false);
    }

    [TestMethod]
    [DataRow("jmap.batch.execute.v4", false, false)]
    [DataRow("jmap.batch.execute.v4", true, false)]
    [DataRow("jmap.batch.execute.v4", false, true)]
    [DataRow("jmap.batch.execute.v4", true, true)]
    [DataRow("jmap.batch.execute.v5", false, false)]
    [DataRow("jmap.batch.execute.v5", true, false)]
    [DataRow("jmap.batch.execute.v5", false, true)]
    [DataRow("jmap.batch.execute.v5", true, true)]
    [DataRow("jmap.batch.execute.v6", false, false)]
    [DataRow("jmap.batch.execute.v6", true, false)]
    [DataRow("jmap.batch.execute.v6", false, true)]
    [DataRow("jmap.batch.execute.v6", true, true)]
    [DataRow("mail.operation.execute", false, false)]
    [DataRow("mail.operation.execute", true, false)]
    [DataRow("mail.operation.execute", false, true)]
    [DataRow("mail.operation.execute", true, true)]
    [DataRow("mail.operation.execute.v2", false, false)]
    [DataRow("mail.operation.execute.v2", true, false)]
    [DataRow("mail.operation.execute.v2", false, true)]
    [DataRow("mail.operation.execute.v2", true, true)]
    [DataRow("mail.operation.execute.v3", false, false)]
    [DataRow("mail.operation.execute.v3", true, false)]
    [DataRow("mail.operation.execute.v3", false, true)]
    [DataRow("mail.operation.execute.v3", true, true)]
    [DataRow("mail.operation.execute.v4", false, false)]
    [DataRow("mail.operation.execute.v4", true, false)]
    [DataRow("mail.operation.execute.v4", false, true)]
    [DataRow("mail.operation.execute.v4", true, true)]
    [DataRow("mail.operation.execute.v5", false, false)]
    [DataRow("mail.operation.execute.v5", true, false)]
    [DataRow("mail.operation.execute.v5", false, true)]
    [DataRow("mail.operation.execute.v5", true, true)]
    [DataRow("mail.operation.execute.v6", false, false)]
    [DataRow("mail.operation.execute.v6", true, false)]
    [DataRow("mail.operation.execute.v6", false, true)]
    [DataRow("mail.operation.execute.v6", true, true)]
    [DataRow("mail.operation.execute.v7", false, false)]
    [DataRow("mail.operation.execute.v7", true, false)]
    [DataRow("mail.operation.execute.v7", false, true)]
    [DataRow("mail.operation.execute.v7", true, true)]
    [DataRow("mail.operation.execute.v8", false, false)]
    [DataRow("mail.operation.execute.v8", true, false)]
    [DataRow("mail.operation.execute.v8", false, true)]
    [DataRow("mail.operation.execute.v8", true, true)]
    [DataRow("mail.operation.execute.v9", false, false)]
    [DataRow("mail.operation.execute.v9", true, false)]
    [DataRow("mail.operation.execute.v9", false, true)]
    [DataRow("mail.operation.execute.v9", true, true)]
    [DataRow("mail.operation.execute.v10", false, false)]
    [DataRow("mail.operation.execute.v10", true, false)]
    [DataRow("mail.operation.execute.v10", false, true)]
    [DataRow("mail.operation.execute.v10", true, true)]
    [DataRow("mail.operation.execute.v11", false, false)]
    [DataRow("mail.operation.execute.v11", true, false)]
    [DataRow("mail.operation.execute.v11", false, true)]
    [DataRow("mail.operation.execute.v11", true, true)]
    [DataRow("mail.operation.execute.v12", false, false)]
    [DataRow("mail.operation.execute.v12", true, false)]
    [DataRow("mail.operation.execute.v12", false, true)]
    [DataRow("mail.operation.execute.v12", true, true)]
    [DataRow("mail.operation.execute.v13", false, false)]
    [DataRow("mail.operation.execute.v13", true, false)]
    [DataRow("mail.operation.execute.v13", false, true)]
    [DataRow("mail.operation.execute.v13", true, true)]
    [DataRow("mail.operation.execute.v14", false, false)]
    [DataRow("mail.operation.execute.v14", true, false)]
    [DataRow("mail.operation.execute.v14", false, true)]
    [DataRow("mail.operation.execute.v14", true, true)]
    [DataRow("mail.operation.execute.v15", false, false)]
    [DataRow("mail.operation.execute.v15", true, false)]
    [DataRow("mail.operation.execute.v15", false, true)]
    [DataRow("mail.operation.execute.v15", true, true)]
    [DataRow("mail.operation.execute.v16", false, false)]
    [DataRow("mail.operation.execute.v16", true, false)]
    [DataRow("mail.operation.execute.v16", false, true)]
    [DataRow("mail.operation.execute.v16", true, true)]
    [DataRow("mail.operation.execute.v17", false, false)]
    [DataRow("mail.operation.execute.v17", true, false)]
    [DataRow("mail.operation.execute.v17", false, true)]
    [DataRow("mail.operation.execute.v17", true, true)]
    [DataRow("mail.operation.execute.v18", false, false)]
    [DataRow("mail.operation.execute.v18", true, false)]
    [DataRow("mail.operation.execute.v18", false, true)]
    [DataRow("mail.operation.execute.v18", true, true)]
    [DataRow("mail.operation.execute.v26", false, false)]
    [DataRow("mail.operation.execute.v26", true, false)]
    [DataRow("mail.operation.execute.v26", false, true)]
    [DataRow("mail.operation.execute.v26", true, true)]
    [DataRow("mail.operation.execute.v27", false, false)]
    [DataRow("mail.operation.execute.v27", true, false)]
    [DataRow("mail.operation.execute.v27", false, true)]
    [DataRow("mail.operation.execute.v27", true, true)]
    [DataRow(ApplicationOperations.MailOperationExecute, false, false)]
    [DataRow(ApplicationOperations.MailOperationExecute, true, false)]
    [DataRow(ApplicationOperations.MailOperationExecute, false, true)]
    [DataRow(ApplicationOperations.MailOperationExecute, true, true)]
    public async Task PendingOrLeasedNewContractWorkBlocksLegacyRollbackEvenAfterDeadline(string operation, bool leased, bool expired)
    {
        var fixture = (await Fixture.CreateAsync().ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        await fixture.EnableAsync().ConfigureAwait(false);
        using var protector = AesGcmPayloadProtectorTests.CreateProtector("wake", "transition");
        var bus = new PostgresApplicationBus(fixture.Source, protector);
        var now = DateTimeOffset.UtcNow;
        var request = new ApplicationRequest(Guid.CreateVersion7(), Guid.CreateVersion7(), 0, "jmap",
            operation, "application/json", "{}"u8.ToArray(),
            new Dictionary<string, string>(StringComparer.Ordinal), now.AddMinutes(-3), now.AddMinutes(2));
        await bus.EnqueueAsync(request).ConfigureAwait(false);
        if (leased) Assert.IsNotNull(await bus.TryClaimAsync("transition-worker").ConfigureAwait(false));
        if (expired)
        {
            var expiry = fixture.Source.CreateCommand("UPDATE application_requests SET deadline_at = clock_timestamp() - interval '1 second' WHERE id = @id");
            await using var expiryLifetime = expiry.ConfigureAwait(false);
            expiry.Parameters.AddWithValue("id", request.Id);
            await expiry.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            WorkerWakeSchemaTransition.RestoreAsync(fixture.Source, fixture.Role, WorkerWakeSchemaState.Legacy)).ConfigureAwait(false);
        await WorkerWakeDatabasePrivilegeProbe.ProbeAsync(fixture.Wake).ConfigureAwait(false);
        var state = fixture.Source.CreateCommand("SELECT state FROM application_requests WHERE id = @id");
        await using var stateLifetime = state.ConfigureAwait(false);
        state.Parameters.AddWithValue("id", request.Id);
        Assert.AreEqual(leased ? "processing" : "pending", await state.ExecuteScalarAsync().ConfigureAwait(false));
    }

    [TestMethod]
    public async Task V19MailOperationsBlockLegacyWakeRollbackInEveryLeaseState()
    {
        await PendingOrLeasedNewContractWorkBlocksLegacyRollbackEvenAfterDeadline(
            "mail.operation.execute.v19", false, false).ConfigureAwait(false);
        await PendingOrLeasedNewContractWorkBlocksLegacyRollbackEvenAfterDeadline(
            "mail.operation.execute.v19", true, false).ConfigureAwait(false);
        await PendingOrLeasedNewContractWorkBlocksLegacyRollbackEvenAfterDeadline(
            "mail.operation.execute.v19", false, true).ConfigureAwait(false);
        await PendingOrLeasedNewContractWorkBlocksLegacyRollbackEvenAfterDeadline(
            "mail.operation.execute.v19", true, true).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task V20MailOperationsBlockLegacyWakeRollbackInEveryLeaseState()
    {
        await PendingOrLeasedNewContractWorkBlocksLegacyRollbackEvenAfterDeadline(
            "mail.operation.execute.v20", false, false).ConfigureAwait(false);
        await PendingOrLeasedNewContractWorkBlocksLegacyRollbackEvenAfterDeadline(
            "mail.operation.execute.v20", true, false).ConfigureAwait(false);
        await PendingOrLeasedNewContractWorkBlocksLegacyRollbackEvenAfterDeadline(
            "mail.operation.execute.v20", false, true).ConfigureAwait(false);
        await PendingOrLeasedNewContractWorkBlocksLegacyRollbackEvenAfterDeadline(
            "mail.operation.execute.v20", true, true).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task V21MailOperationsBlockLegacyWakeRollbackInEveryLeaseState()
    {
        await PendingOrLeasedNewContractWorkBlocksLegacyRollbackEvenAfterDeadline(
            "mail.operation.execute.v21", false, false).ConfigureAwait(false);
        await PendingOrLeasedNewContractWorkBlocksLegacyRollbackEvenAfterDeadline(
            "mail.operation.execute.v21", true, false).ConfigureAwait(false);
        await PendingOrLeasedNewContractWorkBlocksLegacyRollbackEvenAfterDeadline(
            "mail.operation.execute.v21", false, true).ConfigureAwait(false);
        await PendingOrLeasedNewContractWorkBlocksLegacyRollbackEvenAfterDeadline(
            "mail.operation.execute.v21", true, true).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task V22MailOperationsBlockLegacyWakeRollbackInEveryLeaseState()
    {
        await PendingOrLeasedNewContractWorkBlocksLegacyRollbackEvenAfterDeadline(
            "mail.operation.execute.v22", false, false).ConfigureAwait(false);
        await PendingOrLeasedNewContractWorkBlocksLegacyRollbackEvenAfterDeadline(
            "mail.operation.execute.v22", true, false).ConfigureAwait(false);
        await PendingOrLeasedNewContractWorkBlocksLegacyRollbackEvenAfterDeadline(
            "mail.operation.execute.v22", false, true).ConfigureAwait(false);
        await PendingOrLeasedNewContractWorkBlocksLegacyRollbackEvenAfterDeadline(
            "mail.operation.execute.v22", true, true).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task V23MailOperationsBlockLegacyWakeRollbackInEveryLeaseState()
    {
        await PendingOrLeasedNewContractWorkBlocksLegacyRollbackEvenAfterDeadline(
            "mail.operation.execute.v23", false, false).ConfigureAwait(false);
        await PendingOrLeasedNewContractWorkBlocksLegacyRollbackEvenAfterDeadline(
            "mail.operation.execute.v23", true, false).ConfigureAwait(false);
        await PendingOrLeasedNewContractWorkBlocksLegacyRollbackEvenAfterDeadline(
            "mail.operation.execute.v23", false, true).ConfigureAwait(false);
        await PendingOrLeasedNewContractWorkBlocksLegacyRollbackEvenAfterDeadline(
            "mail.operation.execute.v23", true, true).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task V24MailOperationsBlockLegacyWakeRollbackInEveryLeaseState()
    {
        await PendingOrLeasedNewContractWorkBlocksLegacyRollbackEvenAfterDeadline(
            "mail.operation.execute.v24", false, false).ConfigureAwait(false);
        await PendingOrLeasedNewContractWorkBlocksLegacyRollbackEvenAfterDeadline(
            "mail.operation.execute.v24", true, false).ConfigureAwait(false);
        await PendingOrLeasedNewContractWorkBlocksLegacyRollbackEvenAfterDeadline(
            "mail.operation.execute.v24", false, true).ConfigureAwait(false);
        await PendingOrLeasedNewContractWorkBlocksLegacyRollbackEvenAfterDeadline(
            "mail.operation.execute.v24", true, true).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task V25MailOperationsBlockLegacyWakeRollbackInEveryLeaseState()
    {
        await PendingOrLeasedNewContractWorkBlocksLegacyRollbackEvenAfterDeadline(
            "mail.operation.execute.v25", false, false).ConfigureAwait(false);
        await PendingOrLeasedNewContractWorkBlocksLegacyRollbackEvenAfterDeadline(
            "mail.operation.execute.v25", true, false).ConfigureAwait(false);
        await PendingOrLeasedNewContractWorkBlocksLegacyRollbackEvenAfterDeadline(
            "mail.operation.execute.v25", false, true).ConfigureAwait(false);
        await PendingOrLeasedNewContractWorkBlocksLegacyRollbackEvenAfterDeadline(
            "mail.operation.execute.v25", true, true).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task V26MailOperationsBlockLegacyWakeRollbackInEveryLeaseState()
    {
        await PendingOrLeasedNewContractWorkBlocksLegacyRollbackEvenAfterDeadline(
            "mail.operation.execute.v26", false, false).ConfigureAwait(false);
        await PendingOrLeasedNewContractWorkBlocksLegacyRollbackEvenAfterDeadline(
            "mail.operation.execute.v26", true, false).ConfigureAwait(false);
        await PendingOrLeasedNewContractWorkBlocksLegacyRollbackEvenAfterDeadline(
            "mail.operation.execute.v26", false, true).ConfigureAwait(false);
        await PendingOrLeasedNewContractWorkBlocksLegacyRollbackEvenAfterDeadline(
            "mail.operation.execute.v26", true, true).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task V27MailOperationsBlockLegacyWakeRollbackInEveryLeaseState()
    {
        await PendingOrLeasedNewContractWorkBlocksLegacyRollbackEvenAfterDeadline(
            "mail.operation.execute.v27", false, false).ConfigureAwait(false);
        await PendingOrLeasedNewContractWorkBlocksLegacyRollbackEvenAfterDeadline(
            "mail.operation.execute.v27", true, false).ConfigureAwait(false);
        await PendingOrLeasedNewContractWorkBlocksLegacyRollbackEvenAfterDeadline(
            "mail.operation.execute.v27", false, true).ConfigureAwait(false);
        await PendingOrLeasedNewContractWorkBlocksLegacyRollbackEvenAfterDeadline(
            "mail.operation.execute.v27", true, true).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task ReceiptAwareRollbackPreservesCommittedRows()
    {
        var fixture = (await Fixture.CreateAsync().ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        await fixture.EnableAsync().ConfigureAwait(false);
        await fixture.AddReceiptAsync().ConfigureAwait(false);
        var prior = await WorkerWakeSchemaTransition.ReadAsync(fixture.Source, fixture.Role).ConfigureAwait(false);
        await WorkerWakeSchemaTransition.RestoreAsync(fixture.Source, fixture.Role, prior).ConfigureAwait(false);
        var database = fixture.Context();
        await using var databaseLifetime = database.ConfigureAwait(false);
        Assert.AreEqual(1, await database.ApplicationOperationReceipts.CountAsync().ConfigureAwait(false));
        await WorkerWakeDatabasePrivilegeProbe.ProbeAsync(fixture.Wake).ConfigureAwait(false);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task LegacyRollbackWaitsForInFlightReceiptTransactionBeforeDeciding(bool commit)
    {
        var fixture = (await Fixture.CreateAsync().ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        await fixture.EnableAsync().ConfigureAwait(false);
        var writer = (await fixture.Source.OpenConnectionAsync().ConfigureAwait(false));
        await using var writerLifetime = writer.ConfigureAwait(false);
        var transaction = (await writer.BeginTransactionAsync().ConfigureAwait(false));
        await using var transactionLifetime = transaction.ConfigureAwait(false);
        {
            var insert = new NpgsqlCommand("""
                         INSERT INTO application_operation_receipts
                             (id, operation_id, step_number, user_id, purpose, payload_object_provider,
                              payload_object_name, payload_object_sha256, payload_object_etag, payload_length,
                              created_at, effects_pending, effects_retry_at)
                         VALUES (@id, @operation, 0, @user, 'jmap.batch', 'azure-blob',
                             'receipt/concurrent', @hash, 'guard', 1, clock_timestamp(),
                             false, clock_timestamp())
                         """, writer, transaction);
            await using var insertLifetime = insert.ConfigureAwait(false);
            insert.Parameters.AddWithValue("id", Guid.CreateVersion7());
            insert.Parameters.AddWithValue("operation", Guid.CreateVersion7());
            insert.Parameters.AddWithValue("user", Guid.CreateVersion7());
            insert.Parameters.AddWithValue("hash", new string('b', 64));
            await insert.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var restore = WorkerWakeSchemaTransition.RestoreAsync(
            fixture.Source, fixture.Role, WorkerWakeSchemaState.Legacy, timeout.Token);
        var waiting = fixture.Source.CreateCommand("""
            SELECT EXISTS (SELECT 1 FROM pg_locks
                WHERE relation = 'public.application_operation_receipts'::regclass
                    AND mode = 'AccessExclusiveLock' AND NOT granted)
            """);
        await using var waitingLifetime = waiting.ConfigureAwait(false);
        while (await waiting.ExecuteScalarAsync(timeout.Token).ConfigureAwait(false) is not true)
            await Task.Delay(25, timeout.Token).ConfigureAwait(false);
        Assert.IsFalse(restore.IsCompleted);
        if (commit)
        {
            await transaction.CommitAsync(timeout.Token).ConfigureAwait(false);

            // This async test intentionally joins its pre-started background operation; no foreground synchronization context or JTF is involved.
#pragma warning disable VSTHRD003
            await Assert.ThrowsAsync<InvalidOperationException>(() => restore).ConfigureAwait(false);

#pragma warning restore VSTHRD003

            var database = fixture.Context();
            await using var databaseLifetime = database.ConfigureAwait(false);
            Assert.AreEqual(1, await database.ApplicationOperationReceipts.CountAsync().ConfigureAwait(false));
            await WorkerWakeDatabasePrivilegeProbe.ProbeAsync(fixture.Wake).ConfigureAwait(false);
        }
        else
        {
            await transaction.RollbackAsync(timeout.Token).ConfigureAwait(false);
            await restore.ConfigureAwait(false);
            Assert.AreEqual(WorkerWakeSchemaState.Legacy,
                await WorkerWakeSchemaTransition.ReadAsync(fixture.Source, fixture.Role).ConfigureAwait(false));
        }
    }

    [TestMethod]
    public async Task UnsafeRoleAndSensitiveOrDelegableGrantsFailWithoutPermissionNormalization()
    {
        var fixture = (await Fixture.CreateAsync().ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        {
            var inherit = fixture.Source.CreateCommand($"ALTER ROLE \"{fixture.Role}\" INHERIT");
            await using var inheritLifetime = inherit.ConfigureAwait(false);
            await inherit.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.EnableAsync()).ConfigureAwait(false);
        {
            var safe = fixture.Source.CreateCommand($"ALTER ROLE \"{fixture.Role}\" NOINHERIT");
            await using var safeLifetime = safe.ConfigureAwait(false);
            await safe.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        {
            var sensitive = fixture.Source.CreateCommand($"GRANT SELECT (payload_object_name) ON application_operation_receipts TO \"{fixture.Role}\"");
            await using var sensitiveLifetime = sensitive.ConfigureAwait(false);
            await sensitive.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.EnableAsync()).ConfigureAwait(false);
        {
            var check = fixture.Source.CreateCommand("SELECT has_column_privilege(@role, 'application_operation_receipts', 'effects_pending', 'SELECT')");
            await using var checkLifetime = check.ConfigureAwait(false);
            check.Parameters.AddWithValue("role", fixture.Role);
            Assert.AreEqual(false, await check.ExecuteScalarAsync().ConfigureAwait(false));
        }
        {
            var revoke = fixture.Source.CreateCommand($"REVOKE SELECT (payload_object_name) ON application_operation_receipts FROM \"{fixture.Role}\"");
            await using var revokeLifetime = revoke.ConfigureAwait(false);
            await revoke.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        {
            var delegable = fixture.Source.CreateCommand($"GRANT SELECT (effects_pending, effects_retry_at) ON application_operation_receipts TO \"{fixture.Role}\" WITH GRANT OPTION");
            await using var delegableLifetime = delegable.ConfigureAwait(false);
            await delegable.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.EnableAsync()).ConfigureAwait(false);
        await Assert.ThrowsAsync<ArgumentException>(() => WorkerWakeSchemaTransition.ReadAsync(fixture.Source, "bad;role")).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task LegacyRollbackWaitsForSnapshotBarrierAndCancellationLeavesSchemaAndGrantsUnchanged()
    {
        var fixture = (await Fixture.CreateAsync().ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        await fixture.EnableAsync().ConfigureAwait(false);
        var snapshot = (await PostgresBlobDeletionBarrier.AcquireExclusiveAsync(fixture.Source).ConfigureAwait(false));
        await using var snapshotLifetime = snapshot.ConfigureAwait(false);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var restore = WorkerWakeSchemaTransition.RestoreAsync(fixture.Source, fixture.Role, WorkerWakeSchemaState.Legacy, cancellation.Token);
        var waiting = fixture.Source.CreateCommand("SELECT EXISTS (SELECT 1 FROM pg_stat_activity WHERE datname = @database AND wait_event = 'advisory')");
        await using var waitingLifetime = waiting.ConfigureAwait(false);
        waiting.Parameters.AddWithValue("database", fixture.Database.DatabaseName);
        while (await waiting.ExecuteScalarAsync(cancellation.Token).ConfigureAwait(false) is not true)
            await Task.Delay(25, cancellation.Token).ConfigureAwait(false);
        Assert.IsFalse(restore.IsCompleted);
        await cancellation.CancelAsync().ConfigureAwait(false);

        // This async test intentionally joins its pre-started background operation; no foreground synchronization context or JTF is involved.
#pragma warning disable VSTHRD003
        await Assert.ThrowsAsync<OperationCanceledException>(() => restore).ConfigureAwait(false);

#pragma warning restore VSTHRD003

        await WorkerWakeDatabasePrivilegeProbe.ProbeAsync(fixture.Wake).ConfigureAwait(false);
    }

    private sealed class Fixture(PostgresTestDatabase database, NpgsqlDataSource source, NpgsqlDataSource wake, string role) : IAsyncDisposable
    {
        public PostgresTestDatabase Database { get; } = database;
        public NpgsqlDataSource Source { get; } = source;
        public NpgsqlDataSource Wake { get; } = wake;
        public string Role { get; } = role;
        public EmailDbContext Context() => new(new DbContextOptionsBuilder<EmailDbContext>().UseNpgsql(Database.ConnectionString).Options);
        public Task EnableAsync() => WorkerWakeSchemaTransition.EnableAsync(Source, Role);
        public async Task PrepareAsync()
        {
            var context = Context();
            await using var contextLifetime = context.ConfigureAwait(false);
            await new MailRuntimeSchemaService(context).EnsureAsync().ConfigureAwait(false);
        }
        public async Task AddReceiptAsync()
        {
            var context = Context();
            await using var contextLifetime = context.ConfigureAwait(false);
            // Even an unreadable/dangling receipt is committed state, not permission to discard it.
            await (context.ApplicationOperationReceipts.AddAsync(new ApplicationOperationReceiptDB
            {
                Id = Guid.CreateVersion7(),
                OperationId = Guid.CreateVersion7(),
                UserId = Guid.CreateVersion7(),
                Purpose = "jmap.batch",
                ObjectProvider = "azure-blob",
                ObjectName = "receipt/guard",
                ObjectSha256 = new string('a', 64),
                ObjectEntityTag = "guard",
                PayloadLength = 1,
                CreatedAt = DateTime.UtcNow,
                EffectsRetryAt = DateTime.UtcNow
            })).ConfigureAwait(false);
            await context.SaveChangesAsync().ConfigureAwait(false);
        }


        public static async Task<Fixture> CreateAsync(bool legacy = false)
        {
            var database = await PostgresTestDatabase.TryCreateAsync().ConfigureAwait(false);
            if (database is null) Assert.Inconclusive("PostgreSQL is required.");
            NpgsqlDataSource? source = null;
            NpgsqlDataSource? wake = null;
            var role = $"mk8_wake_transition_{Guid.NewGuid():N}";
            try
            {

                // The returned fixture owns this allocation; finally releases untransferred resources if initialization fails.
#pragma warning disable CA2000
                source = NpgsqlDataSource.Create(database!.ConnectionString);

#pragma warning restore CA2000

                // The returned fixture owns this allocation; finally releases untransferred resources if initialization fails.
#pragma warning disable CA2000
                wake = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(database.ConnectionString)
                { Username = role, Password = "test-only" }.ConnectionString);

#pragma warning restore CA2000

                // The returned fixture owns this allocation; finally releases untransferred resources if initialization fails.
#pragma warning disable CA2000
                var fixture = new Fixture(database, source, wake, role);

#pragma warning restore CA2000
                await InitializeAsync(fixture, legacy).ConfigureAwait(false);
                source = null;
                wake = null;
                database = null;
                return fixture;
            }
            finally
            {
                if (wake is not null) await wake.DisposeAsync().ConfigureAwait(false);
                try
                {
                    if (source is not null) await DeleteRoleAsync(source, role).ConfigureAwait(false);
                }
                finally
                {
                    if (source is not null) await source.DisposeAsync().ConfigureAwait(false);
                    if (database is not null) await database.DisposeAsync().ConfigureAwait(false);
                }
            }
        }

        private static async Task InitializeAsync(Fixture fixture, bool legacy)
        {
            {
                var context = fixture.Context();
                await using var contextLifetime = context.ConfigureAwait(false);
                await context.Database.EnsureCreatedAsync().ConfigureAwait(false);
                await fixture.PrepareAsync().ConfigureAwait(false);
            }
            await PostgresMessagingSchema.EnsureAsync(fixture.Source).ConfigureAwait(false);
            var setup = fixture.Source.CreateCommand($"""
                REVOKE ALL ON DATABASE "{fixture.Database.DatabaseName}" FROM PUBLIC;
                REVOKE CREATE ON SCHEMA public FROM PUBLIC;
                CREATE ROLE "{fixture.Role}" LOGIN PASSWORD 'test-only' NOINHERIT;
                GRANT CONNECT ON DATABASE "{fixture.Database.DatabaseName}" TO "{fixture.Role}";
                GRANT USAGE ON SCHEMA public TO "{fixture.Role}";
                GRANT SELECT (state, lease_expires_at, deadline_at) ON application_requests TO "{fixture.Role}";
                GRANT SELECT (state, next_attempt_at, lease_expires_at) ON mail_queue_messages TO "{fixture.Role}";
                GRANT SELECT (expires_at, is_verified, next_push_at, user_id, last_pushed_change) ON jmap_push_subscriptions TO "{fixture.Role}";
                GRANT SELECT (id, is_active) ON users TO "{fixture.Role}";
                GRANT SELECT (id, owner_id, alias_for_inbox_id, name, address_id) ON inboxes TO "{fixture.Role}";
                GRANT SELECT (id, company_id, is_active) ON addresses TO "{fixture.Role}";
                GRANT SELECT (id, is_active) ON companies TO "{fixture.Role}";
                GRANT SELECT (account_id, sequence) ON jmap_changes TO "{fixture.Role}";
                """);
            await using var setupLifetime = setup.ConfigureAwait(false);
            await setup.ExecuteNonQueryAsync().ConfigureAwait(false);
            if (legacy)
            {
                var drop = fixture.Source.CreateCommand("DROP TABLE application_operation_receipts");
                await using var dropLifetime = drop.ConfigureAwait(false);
                await drop.ExecuteNonQueryAsync().ConfigureAwait(false);
            }

        }

        private static async Task DeleteRoleAsync(NpgsqlDataSource source, string role)
        {
            var exists = source.CreateCommand("SELECT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = @role)");
            await using var existsLifetime = exists.ConfigureAwait(false);
            exists.Parameters.AddWithValue("role", role);
            if (await exists.ExecuteScalarAsync().ConfigureAwait(false) is not true) return;
            var cleanup = source.CreateCommand($"DROP OWNED BY \"{role}\"; DROP ROLE \"{role}\"");
            await using var cleanupLifetime = cleanup.ConfigureAwait(false);
            await cleanup.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            await Wake.DisposeAsync().ConfigureAwait(false);
            {
                var cleanup = Source.CreateCommand($"DROP OWNED BY \"{Role}\"; DROP ROLE \"{Role}\"");
                await using var cleanupLifetime = cleanup.ConfigureAwait(false);
                await cleanup.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
            await Source.DisposeAsync().ConfigureAwait(false);
            await Database.DisposeAsync().ConfigureAwait(false);
        }
    }
}
