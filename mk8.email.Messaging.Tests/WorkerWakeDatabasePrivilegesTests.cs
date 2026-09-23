using Microsoft.EntityFrameworkCore;
using mk8.email.Contracts.Messaging;
using mk8.email.Infrastructure.Data;
using mk8.email.Wake;
using Npgsql;

namespace mk8.email.Messaging.Tests;

[TestClass]
[DoNotParallelize]
[TestCategory("PostgreSQL")]
public sealed class WorkerWakeDatabasePrivilegesTests
{
    [TestMethod]
    public async Task WakeRoleCanObserveWorkButCannotReadOtherTablesOrWrite()
    {
        await using var database = await RequirePostgresAsync();
        await using (var context = new EmailDbContext(
                         new DbContextOptionsBuilder<EmailDbContext>()
                             .UseNpgsql(database.ConnectionString).Options))
        {
            await context.Database.EnsureCreatedAsync();
            await new MailRuntimeSchemaService(context).EnsureAsync();
        }

        await using var adminDataSource = NpgsqlDataSource.Create(database.ConnectionString);
        await PostgresMessagingSchema.EnsureAsync(adminDataSource);
        var role = $"mk8_wake_test_{Guid.NewGuid():N}";
        var password = Guid.NewGuid().ToString("N");
        await using var admin = await adminDataSource.OpenConnectionAsync();
        await using (var setup = admin.CreateCommand())
        {
            setup.CommandText = $"""
                REVOKE ALL ON DATABASE "{database.DatabaseName}" FROM PUBLIC;
                REVOKE CREATE ON SCHEMA public FROM PUBLIC;
                CREATE TABLE wake_private_mail_content (id integer PRIMARY KEY);
                CREATE ROLE "{role}" LOGIN PASSWORD '{password}'
                    NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION
                    NOBYPASSRLS NOINHERIT;
                """;
            await setup.ExecuteNonQueryAsync();
        }

        try
        {
            await using (var grant = admin.CreateCommand())
            {
                grant.CommandText = $"""
                    GRANT CONNECT ON DATABASE "{database.DatabaseName}" TO "{role}";
                    GRANT USAGE ON SCHEMA public TO "{role}";
                    """;
                await grant.ExecuteNonQueryAsync();
            }

            var wakeConnection = new NpgsqlConnectionStringBuilder(database.ConnectionString)
            {
                Username = role,
                Password = password,
            };
            await using var wakeDataSource = NpgsqlDataSource.Create(
                wakeConnection.ConnectionString);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                WorkerWakeDatabasePrivilegeProbe.ProbeAsync(wakeDataSource));

            await using (var grant = admin.CreateCommand())
            {
                grant.CommandText = $"""
                    GRANT SELECT ON application_requests, mail_queue_messages,
                        jmap_push_subscriptions, jmap_changes, users, inboxes,
                        addresses, companies TO "{role}";
                    """;
                await grant.ExecuteNonQueryAsync();
            }
            await WorkerWakeDatabasePrivilegeProbe.ProbeAsync(wakeDataSource);
            Assert.IsFalse((await new WorkerWakeProbe(wakeDataSource).ReadAsync()).HasDueWork);
            using (var protector = AesGcmPayloadProtectorTests.CreateProtector(
                       "wake", "restricted-role"))
            {
                var now = DateTimeOffset.UtcNow;
                await new PostgresApplicationBus(adminDataSource, protector).EnqueueAsync(
                    new ApplicationRequest(
                        Guid.CreateVersion7(), Guid.CreateVersion7(), 0, "admin",
                        ApplicationOperations.SystemPing, "application/json",
                        "{}"u8.ToArray(), new Dictionary<string, string>(),
                        now, now.AddMinutes(2)));
            }
            Assert.IsTrue((await new WorkerWakeProbe(wakeDataSource).ReadAsync()).HasDueWork);

            await using (var privateRead = wakeDataSource.CreateCommand(
                             "SELECT id FROM wake_private_mail_content"))
            {
                var error = await Assert.ThrowsExactlyAsync<PostgresException>(
                    () => privateRead.ExecuteScalarAsync());
                Assert.AreEqual(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
            }
            await using (var forbiddenWrite = wakeDataSource.CreateCommand(
                             "DELETE FROM application_requests"))
            {
                var error = await Assert.ThrowsExactlyAsync<PostgresException>(
                    () => forbiddenWrite.ExecuteNonQueryAsync());
                Assert.AreEqual(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
            }

            await AssertExcessGrantRejectedAsync(
                admin, wakeDataSource,
                $"GRANT SELECT ON wake_private_mail_content TO \"{role}\"",
                $"REVOKE SELECT ON wake_private_mail_content FROM \"{role}\"");
            await AssertExcessGrantRejectedAsync(
                admin, wakeDataSource,
                $"GRANT SELECT (id) ON wake_private_mail_content TO \"{role}\"",
                $"REVOKE SELECT (id) ON wake_private_mail_content FROM \"{role}\"");
            await AssertExcessGrantRejectedAsync(
                admin, wakeDataSource,
                $"GRANT UPDATE ON application_requests TO \"{role}\"",
                $"REVOKE UPDATE ON application_requests FROM \"{role}\"");
            await AssertExcessGrantRejectedAsync(
                admin, wakeDataSource,
                $"GRANT UPDATE (id) ON application_requests TO \"{role}\"",
                $"REVOKE UPDATE (id) ON application_requests FROM \"{role}\"");
            await AssertExcessGrantRejectedAsync(
                admin, wakeDataSource,
                $"GRANT CREATE ON SCHEMA public TO \"{role}\"",
                $"REVOKE CREATE ON SCHEMA public FROM \"{role}\"");

            await using (var definer = admin.CreateCommand())
            {
                definer.CommandText = """
                    CREATE FUNCTION wake_private_definer() RETURNS integer
                    LANGUAGE sql SECURITY DEFINER AS 'SELECT 1';
                    REVOKE ALL ON FUNCTION wake_private_definer() FROM PUBLIC;
                    """;
                await definer.ExecuteNonQueryAsync();
            }
            await AssertExcessGrantRejectedAsync(
                admin, wakeDataSource,
                $"GRANT EXECUTE ON FUNCTION wake_private_definer() TO \"{role}\"",
                $"REVOKE EXECUTE ON FUNCTION wake_private_definer() FROM \"{role}\"");
        }
        finally
        {
            await using var cleanup = admin.CreateCommand();
            cleanup.CommandText = $"DROP OWNED BY \"{role}\"; DROP ROLE \"{role}\";";
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    private static async Task AssertExcessGrantRejectedAsync(
        NpgsqlConnection admin,
        NpgsqlDataSource wake,
        string grant,
        string revoke)
    {
        await using var command = admin.CreateCommand();
        command.CommandText = grant;
        await command.ExecuteNonQueryAsync();
        try
        {
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                WorkerWakeDatabasePrivilegeProbe.ProbeAsync(wake));
        }
        finally
        {
            command.CommandText = revoke;
            await command.ExecuteNonQueryAsync();
        }
        await WorkerWakeDatabasePrivilegeProbe.ProbeAsync(wake);
    }

    private static async Task<PostgresTestDatabase> RequirePostgresAsync()
    {
        var database = await PostgresTestDatabase.TryCreateAsync();
        if (database is null)
        {
            Assert.Inconclusive("Set MK8_EMAIL_TEST_POSTGRES.");
            throw new InvalidOperationException("PostgreSQL integration tests require a database.");
        }
        return database;
    }
}
