using Microsoft.EntityFrameworkCore;
using mk8.email.Contracts.Messaging;
using mk8.email.Infrastructure.Data;
using mk8.email.Wake;
using Npgsql;

namespace mk8.email.Messaging.Tests;

[TestClass]
[DoNotParallelize]
[TestCategory("PostgreSQL")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1812", Justification = "MSTest DiscoverInternals instantiates this test class by reflection; focused discovery is verified by executed test counts.")]
internal sealed class WorkerWakeDatabasePrivilegesTests
{
    [TestMethod]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "MA0051", Justification = "The WakeRoleCanObserveWorkButCannotReadOtherTablesOrWrite scenario keeps one fixture's ordered setup, operation and invariant assertions together.")]
    public async Task WakeRoleCanObserveWorkButCannotReadOtherTablesOrWrite()
    {
        var database = (await RequirePostgresAsync().ConfigureAwait(false));
        await using var databaseLifetime = database.ConfigureAwait(false);
        {
            var context = new EmailDbContext(
                         new DbContextOptionsBuilder<EmailDbContext>()
                             .UseNpgsql(database.ConnectionString).Options);
            await using var contextLifetime = context.ConfigureAwait(false);
            await context.Database.EnsureCreatedAsync().ConfigureAwait(false);
            await new MailRuntimeSchemaService(context).EnsureAsync().ConfigureAwait(false);
        }

        var adminDataSource = NpgsqlDataSource.Create(database.ConnectionString);
        await using var adminDataSourceLifetime = adminDataSource.ConfigureAwait(false);
        await PostgresMessagingSchema.EnsureAsync(adminDataSource).ConfigureAwait(false);
        var role = $"mk8_wake_test_{Guid.NewGuid():N}";
        var password = Guid.NewGuid().ToString("N");
        var admin = (await adminDataSource.OpenConnectionAsync().ConfigureAwait(false));
        await using var adminLifetime = admin.ConfigureAwait(false);
        {
            var setup = admin.CreateCommand();
            await using var setupLifetime = setup.ConfigureAwait(false);

            // Test-only DDL uses quoted GUID-generated identifiers or fixed tracked SQL; PostgreSQL identifiers cannot be value parameters.
#pragma warning disable CA2100
            setup.CommandText = $"""
                REVOKE ALL ON DATABASE "{database.DatabaseName}" FROM PUBLIC;
                REVOKE CREATE ON SCHEMA public FROM PUBLIC;
                CREATE TABLE wake_private_mail_content (id integer PRIMARY KEY);
                CREATE ROLE "{role}" LOGIN PASSWORD '{password}'
                    NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION
                    NOBYPASSRLS NOINHERIT;
                """;

#pragma warning restore CA2100

            await setup.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        try
        {
            {
                var grant = admin.CreateCommand();
                await using var grantLifetime = grant.ConfigureAwait(false);

                // Test-only DDL uses quoted GUID-generated identifiers or fixed tracked SQL; PostgreSQL identifiers cannot be value parameters.
#pragma warning disable CA2100
                grant.CommandText = $"""
                    GRANT CONNECT ON DATABASE "{database.DatabaseName}" TO "{role}";
                    GRANT USAGE ON SCHEMA public TO "{role}";
                    """;

#pragma warning restore CA2100

                await grant.ExecuteNonQueryAsync().ConfigureAwait(false);
            }

            var wakeConnection = new NpgsqlConnectionStringBuilder(database.ConnectionString)
            {
                Username = role,
                Password = password,
            };
            var wakeDataSource = NpgsqlDataSource.Create(
                wakeConnection.ConnectionString);
            await using var wakeDataSourceLifetime = wakeDataSource.ConfigureAwait(false);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                WorkerWakeDatabasePrivilegeProbe.ProbeAsync(wakeDataSource)).ConfigureAwait(false);

            {
                var grant = admin.CreateCommand();
                await using var grantLifetime = grant.ConfigureAwait(false);

                // Test-only DDL uses quoted GUID-generated identifiers or fixed tracked SQL; PostgreSQL identifiers cannot be value parameters.
#pragma warning disable CA2100
                grant.CommandText = $"""
                    GRANT SELECT (state, lease_expires_at, deadline_at)
                        ON application_requests TO "{role}";
                    GRANT SELECT (effects_pending, effects_retry_at)
                        ON application_operation_receipts TO "{role}";
                    GRANT SELECT (state, next_attempt_at, lease_expires_at)
                        ON mail_queue_messages TO "{role}";
                    GRANT SELECT (expires_at, is_verified, next_push_at, user_id,
                        last_pushed_change) ON jmap_push_subscriptions TO "{role}";
                    GRANT SELECT (id, is_active) ON users TO "{role}";
                    GRANT SELECT (id, owner_id, alias_for_inbox_id, name, address_id)
                        ON inboxes TO "{role}";
                    GRANT SELECT (id, company_id, is_active) ON addresses TO "{role}";
                    GRANT SELECT (id, is_active) ON companies TO "{role}";
                    GRANT SELECT (account_id, sequence) ON jmap_changes TO "{role}";
                    """;

#pragma warning restore CA2100

                await grant.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
            await WorkerWakeDatabasePrivilegeProbe.ProbeAsync(wakeDataSource).ConfigureAwait(false);
            Assert.IsFalse((await new WorkerWakeProbe(wakeDataSource).ReadAsync().ConfigureAwait(false)).HasDueWork);
            using (var protector = AesGcmPayloadProtectorTests.CreateProtector(
                       "wake", "restricted-role"))
            {
                var now = DateTimeOffset.UtcNow;
                await new PostgresApplicationBus(adminDataSource, protector).EnqueueAsync(
                    new ApplicationRequest(
                        Guid.CreateVersion7(), Guid.CreateVersion7(), 0, "admin",
                        ApplicationOperations.SystemPing, "application/json",
                        "{}"u8.ToArray(), new Dictionary<string, string>(StringComparer.Ordinal),
                        now, now.AddMinutes(2))).ConfigureAwait(false);
            }
            Assert.IsTrue((await new WorkerWakeProbe(wakeDataSource).ReadAsync().ConfigureAwait(false)).HasDueWork);

            {
                var privateRead = wakeDataSource.CreateCommand(
                             "SELECT id FROM wake_private_mail_content");
                await using var privateReadLifetime = privateRead.ConfigureAwait(false);
                var error = await Assert.ThrowsExactlyAsync<PostgresException>(
                    () => privateRead.ExecuteScalarAsync()).ConfigureAwait(false);
                Assert.AreEqual(PostgresErrorCodes.InsufficientPrivilege, error.SqlState, StringComparer.Ordinal);
            }
            foreach (var sensitiveColumn in new[]
                     {
                         "SELECT password_hash FROM users",
                         "SELECT request_payload_inline FROM application_requests",
                         "SELECT payload_object_name FROM application_operation_receipts",
                         "SELECT raw_message FROM mail_queue_messages",
                     })
            {
                var sensitiveRead = wakeDataSource.CreateCommand(sensitiveColumn);
                await using var sensitiveReadLifetime = sensitiveRead.ConfigureAwait(false);
                var error = await Assert.ThrowsExactlyAsync<PostgresException>(
                    () => sensitiveRead.ExecuteScalarAsync()).ConfigureAwait(false);
                Assert.AreEqual(PostgresErrorCodes.InsufficientPrivilege, error.SqlState, StringComparer.Ordinal);
            }
            {
                var forbiddenWrite = wakeDataSource.CreateCommand(
                             "DELETE FROM application_requests");
                await using var forbiddenWriteLifetime = forbiddenWrite.ConfigureAwait(false);
                var error = await Assert.ThrowsExactlyAsync<PostgresException>(
                    () => forbiddenWrite.ExecuteNonQueryAsync()).ConfigureAwait(false);
                Assert.AreEqual(PostgresErrorCodes.InsufficientPrivilege, error.SqlState, StringComparer.Ordinal);
            }

            await AssertExcessGrantRejectedAsync(
                admin, wakeDataSource,
                $"GRANT SELECT ON wake_private_mail_content TO \"{role}\"",
                $"REVOKE SELECT ON wake_private_mail_content FROM \"{role}\"").ConfigureAwait(false);
            await AssertExcessGrantRejectedAsync(
                admin, wakeDataSource,
                $"GRANT SELECT (id) ON wake_private_mail_content TO \"{role}\"",
                $"REVOKE SELECT (id) ON wake_private_mail_content FROM \"{role}\"").ConfigureAwait(false);
            await AssertExcessGrantRejectedAsync(
                admin, wakeDataSource,
                $"GRANT SELECT ON users TO \"{role}\"",
                $"REVOKE SELECT ON users FROM \"{role}\"; " +
                $"GRANT SELECT (id, is_active) ON users TO \"{role}\"").ConfigureAwait(false);
            await AssertExcessGrantRejectedAsync(
                admin, wakeDataSource,
                $"GRANT SELECT (password_hash) ON users TO \"{role}\"",
                $"REVOKE SELECT (password_hash) ON users FROM \"{role}\"").ConfigureAwait(false);
            await AssertExcessGrantRejectedAsync(
                admin, wakeDataSource,
                $"GRANT UPDATE ON application_requests TO \"{role}\"",
                $"REVOKE UPDATE ON application_requests FROM \"{role}\"").ConfigureAwait(false);
            await AssertExcessGrantRejectedAsync(
                admin, wakeDataSource,
                $"GRANT UPDATE (id) ON application_requests TO \"{role}\"",
                $"REVOKE UPDATE (id) ON application_requests FROM \"{role}\"").ConfigureAwait(false);
            await AssertExcessGrantRejectedAsync(
                admin, wakeDataSource,
                $"GRANT CREATE ON SCHEMA public TO \"{role}\"",
                $"REVOKE CREATE ON SCHEMA public FROM \"{role}\"").ConfigureAwait(false);

            {
                var definer = admin.CreateCommand();
                await using var definerLifetime = definer.ConfigureAwait(false);
                definer.CommandText = """
                    CREATE FUNCTION wake_private_definer() RETURNS integer
                    LANGUAGE sql SECURITY DEFINER AS 'SELECT 1';
                    REVOKE ALL ON FUNCTION wake_private_definer() FROM PUBLIC;
                    """;
                await definer.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
            await AssertExcessGrantRejectedAsync(
                admin, wakeDataSource,
                $"GRANT EXECUTE ON FUNCTION wake_private_definer() TO \"{role}\"",
                $"REVOKE EXECUTE ON FUNCTION wake_private_definer() FROM \"{role}\"").ConfigureAwait(false);
        }
        finally
        {
            var cleanup = admin.CreateCommand();
            await using var cleanupLifetime = cleanup.ConfigureAwait(false);

            // Test-only DDL uses quoted GUID-generated identifiers or fixed tracked SQL; PostgreSQL identifiers cannot be value parameters.
#pragma warning disable CA2100
            cleanup.CommandText = $"DROP OWNED BY \"{role}\"; DROP ROLE \"{role}\";";

#pragma warning restore CA2100

            await cleanup.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
    }

    private static async Task AssertExcessGrantRejectedAsync(
        NpgsqlConnection admin,
        NpgsqlDataSource wake,
        string grant,
        string revoke)
    {
        var command = admin.CreateCommand();
        await using var commandLifetime = command.ConfigureAwait(false);

        // Test-only DDL uses quoted GUID-generated identifiers or fixed tracked SQL; PostgreSQL identifiers cannot be value parameters.
#pragma warning disable CA2100
        command.CommandText = grant;

#pragma warning restore CA2100

        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        try
        {
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                WorkerWakeDatabasePrivilegeProbe.ProbeAsync(wake)).ConfigureAwait(false);
        }
        finally
        {

            // Test-only DDL uses quoted GUID-generated identifiers or fixed tracked SQL; PostgreSQL identifiers cannot be value parameters.
#pragma warning disable CA2100
            command.CommandText = revoke;

#pragma warning restore CA2100

            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        await WorkerWakeDatabasePrivilegeProbe.ProbeAsync(wake).ConfigureAwait(false);
    }

    private static async Task<PostgresTestDatabase> RequirePostgresAsync()
    {
        var database = await PostgresTestDatabase.TryCreateAsync().ConfigureAwait(false);
        if (database is null)
        {
            Assert.Inconclusive("Set MK8_EMAIL_TEST_POSTGRES.");
            throw new InvalidOperationException("PostgreSQL integration tests require a database.");
        }
        return database;
    }
}
