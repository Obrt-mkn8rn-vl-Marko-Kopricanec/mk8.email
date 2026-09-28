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
public sealed class WorkerWakeSchemaTransitionTests
{
    [TestMethod]
    public async Task LegacyUpgradeAndEmptyRollbackRestoreSchemaAndExactPermissions()
    {
        await using var fixture = await Fixture.CreateAsync(legacy: true);
        Assert.AreEqual(WorkerWakeSchemaState.Legacy, await WorkerWakeSchemaTransition.ReadAsync(fixture.Source, fixture.Role));
        await Assert.ThrowsAsync<InvalidOperationException>(() => WorkerWakeSchemaTransition.EnableAsync(fixture.Source, fixture.Role));
        await fixture.PrepareAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => WorkerWakeSchemaTransition.ReadAsync(fixture.Source, fixture.Role));
        await WorkerWakeSchemaTransition.EnableAsync(fixture.Source, fixture.Role);
        await WorkerWakeSchemaTransition.EnableAsync(fixture.Source, fixture.Role);
        Assert.AreEqual(WorkerWakeSchemaState.Receipts, await WorkerWakeSchemaTransition.ReadAsync(fixture.Source, fixture.Role));
        await WorkerWakeDatabasePrivilegeProbe.ProbeAsync(fixture.Wake);
        await WorkerWakeSchemaTransition.RestoreAsync(fixture.Source, fixture.Role, WorkerWakeSchemaState.Legacy);
        Assert.AreEqual(WorkerWakeSchemaState.Legacy, await WorkerWakeSchemaTransition.ReadAsync(fixture.Source, fixture.Role));
        await WorkerWakeSchemaTransition.RestoreAsync(fixture.Source, fixture.Role, WorkerWakeSchemaState.Legacy);
        await using var table = fixture.Source.CreateCommand("SELECT to_regclass('public.application_operation_receipts') IS NULL");
        Assert.AreEqual(true, await table.ExecuteScalarAsync());
        await using var access = fixture.Source.CreateCommand("SELECT has_column_privilege(@role, 'public.users', 'id', 'SELECT')");
        access.Parameters.AddWithValue("role", fixture.Role);
        Assert.AreEqual(true, await access.ExecuteScalarAsync());
    }

    [TestMethod]
    public async Task OperatorCliExecutesSchemaGrantAndGuardedRestoreAgainstPostgres()
    {
        await using var fixture = await Fixture.CreateAsync(legacy: true);
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
            await File.WriteAllTextAsync(configPath, JsonSerializer.Serialize(config));
            await File.WriteAllTextAsync(wakePath, fixture.Wake.ConnectionString);
            var legacy = await ManagementCliCommandBoundaryTests.RunCliAsync(configPath,
                ["--worker-wake-schema-state", configPath, wakePath], development: true);
            Assert.AreEqual(0, legacy.ExitCode, legacy.Output);
            Assert.AreEqual("legacy", legacy.Output.Trim());
            await fixture.PrepareAsync();
            var enabled = await ManagementCliCommandBoundaryTests.RunCliAsync(configPath,
                ["--prepare-worker-wake", configPath, wakePath], development: true);
            Assert.AreEqual(0, enabled.ExitCode, enabled.Output);
            await WorkerWakeDatabasePrivilegeProbe.ProbeAsync(fixture.Wake);
            var receipts = await ManagementCliCommandBoundaryTests.RunCliAsync(configPath,
                ["--worker-wake-schema-state", configPath, wakePath], development: true);
            Assert.AreEqual(0, receipts.ExitCode, receipts.Output);
            Assert.AreEqual("receipts", receipts.Output.Trim());
            var restored = await ManagementCliCommandBoundaryTests.RunCliAsync(configPath,
                ["--restore-worker-wake", configPath, wakePath, "legacy"], development: true);
            Assert.AreEqual(0, restored.ExitCode, restored.Output);
            Assert.AreEqual(WorkerWakeSchemaState.Legacy,
                await WorkerWakeSchemaTransition.ReadAsync(fixture.Source, fixture.Role));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [TestMethod]
    public async Task CommittedReceiptBlocksLegacyRollbackWithoutChangingRowsOrGrants()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.EnableAsync();
        await fixture.AddReceiptAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            WorkerWakeSchemaTransition.RestoreAsync(fixture.Source, fixture.Role, WorkerWakeSchemaState.Legacy));
        await using var database = fixture.Context();
        Assert.AreEqual(1, await database.ApplicationOperationReceipts.CountAsync());
        await WorkerWakeDatabasePrivilegeProbe.ProbeAsync(fixture.Wake);
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
    [DataRow(ApplicationOperations.MailOperationExecute, false, false)]
    [DataRow(ApplicationOperations.MailOperationExecute, true, false)]
    [DataRow(ApplicationOperations.MailOperationExecute, false, true)]
    [DataRow(ApplicationOperations.MailOperationExecute, true, true)]
    public async Task PendingOrLeasedNewContractWorkBlocksLegacyRollbackEvenAfterDeadline(string operation, bool leased, bool expired)
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.EnableAsync();
        using var protector = AesGcmPayloadProtectorTests.CreateProtector("wake", "transition");
        var bus = new PostgresApplicationBus(fixture.Source, protector);
        var now = DateTimeOffset.UtcNow;
        var request = new ApplicationRequest(Guid.CreateVersion7(), Guid.CreateVersion7(), 0, "jmap",
            operation, "application/json", "{}"u8.ToArray(),
            new Dictionary<string, string>(), now.AddMinutes(-3), now.AddMinutes(2));
        await bus.EnqueueAsync(request);
        if (leased) Assert.IsNotNull(await bus.TryClaimAsync("transition-worker"));
        if (expired)
        {
            await using var expiry = fixture.Source.CreateCommand("UPDATE application_requests SET deadline_at = clock_timestamp() - interval '1 second' WHERE id = @id");
            expiry.Parameters.AddWithValue("id", request.Id);
            await expiry.ExecuteNonQueryAsync();
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            WorkerWakeSchemaTransition.RestoreAsync(fixture.Source, fixture.Role, WorkerWakeSchemaState.Legacy));
        await WorkerWakeDatabasePrivilegeProbe.ProbeAsync(fixture.Wake);
        await using var state = fixture.Source.CreateCommand("SELECT state FROM application_requests WHERE id = @id");
        state.Parameters.AddWithValue("id", request.Id);
        Assert.AreEqual(leased ? "processing" : "pending", await state.ExecuteScalarAsync());
    }

    [TestMethod]
    public async Task ReceiptAwareRollbackPreservesCommittedRows()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.EnableAsync();
        await fixture.AddReceiptAsync();
        var prior = await WorkerWakeSchemaTransition.ReadAsync(fixture.Source, fixture.Role);
        await WorkerWakeSchemaTransition.RestoreAsync(fixture.Source, fixture.Role, prior);
        await using var database = fixture.Context();
        Assert.AreEqual(1, await database.ApplicationOperationReceipts.CountAsync());
        await WorkerWakeDatabasePrivilegeProbe.ProbeAsync(fixture.Wake);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task LegacyRollbackWaitsForInFlightReceiptTransactionBeforeDeciding(bool commit)
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.EnableAsync();
        await using var writer = await fixture.Source.OpenConnectionAsync();
        await using var transaction = await writer.BeginTransactionAsync();
        await using (var insert = new NpgsqlCommand("""
                         INSERT INTO application_operation_receipts
                             (id, operation_id, step_number, user_id, purpose, payload_object_provider,
                              payload_object_name, payload_object_sha256, payload_object_etag, payload_length,
                              created_at, effects_pending, effects_retry_at)
                         VALUES (@id, @operation, 0, @user, 'jmap.batch', 'azure-blob',
                             'receipt/concurrent', @hash, 'guard', 1, clock_timestamp(),
                             false, clock_timestamp())
                         """, writer, transaction))
        {
            insert.Parameters.AddWithValue("id", Guid.CreateVersion7());
            insert.Parameters.AddWithValue("operation", Guid.CreateVersion7());
            insert.Parameters.AddWithValue("user", Guid.CreateVersion7());
            insert.Parameters.AddWithValue("hash", new string('b', 64));
            await insert.ExecuteNonQueryAsync();
        }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var restore = WorkerWakeSchemaTransition.RestoreAsync(
            fixture.Source, fixture.Role, WorkerWakeSchemaState.Legacy, timeout.Token);
        await using var waiting = fixture.Source.CreateCommand("""
            SELECT EXISTS (SELECT 1 FROM pg_locks
                WHERE relation = 'public.application_operation_receipts'::regclass
                    AND mode = 'AccessExclusiveLock' AND NOT granted)
            """);
        while (await waiting.ExecuteScalarAsync(timeout.Token) is not true)
            await Task.Delay(25, timeout.Token);
        Assert.IsFalse(restore.IsCompleted);
        if (commit)
        {
            await transaction.CommitAsync(timeout.Token);
            await Assert.ThrowsAsync<InvalidOperationException>(() => restore);
            await using var database = fixture.Context();
            Assert.AreEqual(1, await database.ApplicationOperationReceipts.CountAsync());
            await WorkerWakeDatabasePrivilegeProbe.ProbeAsync(fixture.Wake);
        }
        else
        {
            await transaction.RollbackAsync(timeout.Token);
            await restore;
            Assert.AreEqual(WorkerWakeSchemaState.Legacy,
                await WorkerWakeSchemaTransition.ReadAsync(fixture.Source, fixture.Role));
        }
    }

    [TestMethod]
    public async Task UnsafeRoleAndSensitiveOrDelegableGrantsFailWithoutPermissionNormalization()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using (var inherit = fixture.Source.CreateCommand($"ALTER ROLE \"{fixture.Role}\" INHERIT"))
            await inherit.ExecuteNonQueryAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.EnableAsync());
        await using (var safe = fixture.Source.CreateCommand($"ALTER ROLE \"{fixture.Role}\" NOINHERIT"))
            await safe.ExecuteNonQueryAsync();
        await using (var sensitive = fixture.Source.CreateCommand($"GRANT SELECT (payload_object_name) ON application_operation_receipts TO \"{fixture.Role}\""))
            await sensitive.ExecuteNonQueryAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.EnableAsync());
        await using (var check = fixture.Source.CreateCommand("SELECT has_column_privilege(@role, 'application_operation_receipts', 'effects_pending', 'SELECT')"))
        {
            check.Parameters.AddWithValue("role", fixture.Role);
            Assert.AreEqual(false, await check.ExecuteScalarAsync());
        }
        await using (var revoke = fixture.Source.CreateCommand($"REVOKE SELECT (payload_object_name) ON application_operation_receipts FROM \"{fixture.Role}\""))
            await revoke.ExecuteNonQueryAsync();
        await using (var delegable = fixture.Source.CreateCommand($"GRANT SELECT (effects_pending, effects_retry_at) ON application_operation_receipts TO \"{fixture.Role}\" WITH GRANT OPTION"))
            await delegable.ExecuteNonQueryAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.EnableAsync());
        await Assert.ThrowsAsync<ArgumentException>(() => WorkerWakeSchemaTransition.ReadAsync(fixture.Source, "bad;role"));
    }

    [TestMethod]
    public async Task LegacyRollbackWaitsForSnapshotBarrierAndCancellationLeavesSchemaAndGrantsUnchanged()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.EnableAsync();
        await using var snapshot = await PostgresBlobDeletionBarrier.AcquireExclusiveAsync(fixture.Source);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var restore = WorkerWakeSchemaTransition.RestoreAsync(fixture.Source, fixture.Role, WorkerWakeSchemaState.Legacy, cancellation.Token);
        await using var waiting = fixture.Source.CreateCommand("SELECT EXISTS (SELECT 1 FROM pg_stat_activity WHERE datname = @database AND wait_event = 'advisory')");
        waiting.Parameters.AddWithValue("database", fixture.Database.DatabaseName);
        while (await waiting.ExecuteScalarAsync(cancellation.Token) is not true)
            await Task.Delay(25, cancellation.Token);
        Assert.IsFalse(restore.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => restore);
        await WorkerWakeDatabasePrivilegeProbe.ProbeAsync(fixture.Wake);
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
            await using var context = Context();
            await new MailRuntimeSchemaService(context).EnsureAsync();
        }
        public async Task AddReceiptAsync()
        {
            await using var context = Context();
            // Even an unreadable/dangling receipt is committed state, not permission to discard it.
            context.ApplicationOperationReceipts.Add(new ApplicationOperationReceiptDB
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
            });
            await context.SaveChangesAsync();
        }
        public static async Task<Fixture> CreateAsync(bool legacy = false)
        {
            var database = await PostgresTestDatabase.TryCreateAsync();
            if (database is null) Assert.Inconclusive("PostgreSQL is required.");
            var source = NpgsqlDataSource.Create(database!.ConnectionString);
            var role = $"mk8_wake_transition_{Guid.NewGuid():N}";
            var wake = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(database.ConnectionString)
            { Username = role, Password = "test-only" }.ConnectionString);
            var fixture = new Fixture(database, source, wake, role);
            await using (var context = fixture.Context())
            {
                await context.Database.EnsureCreatedAsync();
                await fixture.PrepareAsync();
            }
            await PostgresMessagingSchema.EnsureAsync(source);
            await using var setup = source.CreateCommand($"""
                REVOKE ALL ON DATABASE "{database.DatabaseName}" FROM PUBLIC;
                REVOKE CREATE ON SCHEMA public FROM PUBLIC;
                CREATE ROLE "{role}" LOGIN PASSWORD 'test-only' NOINHERIT;
                GRANT CONNECT ON DATABASE "{database.DatabaseName}" TO "{role}";
                GRANT USAGE ON SCHEMA public TO "{role}";
                GRANT SELECT (state, lease_expires_at, deadline_at) ON application_requests TO "{role}";
                GRANT SELECT (state, next_attempt_at, lease_expires_at) ON mail_queue_messages TO "{role}";
                GRANT SELECT (expires_at, is_verified, next_push_at, user_id, last_pushed_change) ON jmap_push_subscriptions TO "{role}";
                GRANT SELECT (id, is_active) ON users TO "{role}";
                GRANT SELECT (id, owner_id, alias_for_inbox_id, name, address_id) ON inboxes TO "{role}";
                GRANT SELECT (id, company_id, is_active) ON addresses TO "{role}";
                GRANT SELECT (id, is_active) ON companies TO "{role}";
                GRANT SELECT (account_id, sequence) ON jmap_changes TO "{role}";
                """);
            await setup.ExecuteNonQueryAsync();
            if (legacy)
            {
                await using var drop = source.CreateCommand("DROP TABLE application_operation_receipts");
                await drop.ExecuteNonQueryAsync();
            }
            return fixture;
        }
        public async ValueTask DisposeAsync()
        {
            await Wake.DisposeAsync();
            await using (var cleanup = Source.CreateCommand($"DROP OWNED BY \"{Role}\"; DROP ROLE \"{Role}\""))
                await cleanup.ExecuteNonQueryAsync();
            await Source.DisposeAsync();
            await Database.DisposeAsync();
        }
    }
}
