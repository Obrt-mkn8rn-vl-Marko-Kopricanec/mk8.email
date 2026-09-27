using Microsoft.EntityFrameworkCore;
using mk8.email.Hosting;
using mk8.email.Infrastructure.Data;
using mk8.email.Wake;
using Npgsql;

namespace mk8.email.Messaging.Tests;

[TestClass]
[DoNotParallelize]
[TestCategory("PostgreSQL")]
public sealed class RestrictedDatabaseRolePolicyTests
{
    [TestMethod]
    [DataRow("gateway", "private-column")]
    [DataRow("wake", "private-column")]
    [DataRow("gateway", "public-column")]
    [DataRow("wake", "public-column")]
    [DataRow("gateway", "catalog-column")]
    [DataRow("wake", "catalog-column")]
    [DataRow("gateway", "public-catalog-column")]
    [DataRow("wake", "public-catalog-column")]
    [DataRow("gateway", "catalog-maintenance")]
    [DataRow("wake", "catalog-maintenance")]
    [DataRow("gateway", "public-catalog-write")]
    [DataRow("wake", "public-catalog-write")]
    [DataRow("gateway", "catalog-schema-create")]
    [DataRow("wake", "catalog-schema-create")]
    [DataRow("gateway", "public-catalog-schema-create")]
    [DataRow("wake", "public-catalog-schema-create")]
    [DataRow("gateway", "catalog-definer")]
    [DataRow("wake", "catalog-definer")]
    [DataRow("gateway", "system-sequence")]
    [DataRow("wake", "system-sequence")]
    [DataRow("gateway", "private-maintenance")]
    [DataRow("wake", "private-maintenance")]
    [DataRow("gateway", "control-maintenance")]
    [DataRow("wake", "control-maintenance")]
    [DataRow("gateway", "connect-delegation")]
    [DataRow("wake", "connect-delegation")]
    [DataRow("gateway", "schema-delegation")]
    [DataRow("wake", "schema-delegation")]
    [DataRow("gateway", "data-delegation")]
    [DataRow("wake", "data-delegation")]
    [DataRow("gateway", "future-role-data")]
    [DataRow("wake", "future-role-data")]
    [DataRow("gateway", "future-public-data")]
    [DataRow("wake", "future-public-data")]
    [DataRow("gateway", "definer")]
    [DataRow("wake", "definer")]
    [DataRow("gateway", "invoker-grant")]
    [DataRow("wake", "invoker-grant")]
    [DataRow("gateway", "function-owner")]
    [DataRow("wake", "function-owner")]
    [DataRow("gateway", "type-owner")]
    [DataRow("wake", "type-owner")]
    [DataRow("gateway", "language-owner")]
    [DataRow("wake", "language-owner")]
    [DataRow("gateway", "publication-owner")]
    [DataRow("wake", "publication-owner")]
    [DataRow("gateway", "parameter-set")]
    [DataRow("wake", "parameter-set")]
    [DataRow("gateway", "parameter-alter-system")]
    [DataRow("wake", "parameter-alter-system")]
    [DataRow("gateway", "large-object")]
    [DataRow("wake", "large-object")]
    [DataRow("gateway", "inheritance")]
    [DataRow("wake", "inheritance")]
    [DataRow("gateway", "foreign-server")]
    [DataRow("wake", "foreign-server")]
    [DataRow("gateway", "control-column-write")]
    [DataRow("wake", "control-column-write")]
    [DataRow("gateway", "column-reference")]
    [DataRow("wake", "column-reference")]
    public async Task RestrictedRoleRejectsAuthorityOutsideItsScopeWithoutChangingIt(string kind, string violation)
    {
        await using var fixture = await Fixture.CreateAsync(kind);
        await fixture.ProbeAsync();
        await using var mutation = fixture.Admin.CreateCommand(fixture.Violation(violation));
        await mutation.ExecuteNonQueryAsync();
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => fixture.ProbeAsync());
        if (kind == "wake")
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                WorkerWakeSchemaTransition.EnableAsync(fixture.Admin, fixture.Role));
        // Rejection is observational: it must not repair privileges or rotate a role.
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => fixture.ProbeAsync());
    }

    [TestMethod]
    [DataRow("gateway")]
    [DataRow("wake")]
    public async Task SessionSettingsCannotSpoofTheAuditedRoleOrPolicy(string kind)
    {
        await using var fixture = await Fixture.CreateAsync(kind);
        var spoof = new NpgsqlConnectionStringBuilder(fixture.Database.ConnectionString)
        { Options = $"-c mk8.restricted_role={fixture.Role} -c mk8.restricted_role_kind={kind}" };
        await using var elevated = NpgsqlDataSource.Create(spoof.ConnectionString);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => kind == "gateway"
            ? GatewayDatabasePrivilegeProbe.ProbeAsync(elevated)
            : WorkerWakeDatabasePrivilegeProbe.ProbeAsync(elevated));
        await fixture.ProbeAsync();
    }

    [TestMethod]
    [DataRow("gateway")]
    [DataRow("wake")]
    public async Task BuiltInPublicRoutineAndTypeDefaultsDoNotGiveAdditionalAuthority(string kind)
    {
        await using var fixture = await Fixture.CreateAsync(kind);
        await using var setup = fixture.Admin.CreateCommand("""
            ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT EXECUTE ON FUNCTIONS TO PUBLIC;
            ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT USAGE ON TYPES TO PUBLIC;
            CREATE FUNCTION harmless_policy_invoker() RETURNS integer LANGUAGE sql AS 'SELECT 1';
            CREATE TYPE harmless_policy_type AS ENUM ('a');
            """);
        await setup.ExecuteNonQueryAsync();
        await using var builtIn = fixture.Admin.CreateCommand(
            "SELECT has_table_privilege(@role, 'pg_catalog.pg_settings', 'UPDATE')");
        builtIn.Parameters.AddWithValue("role", fixture.Role);
        Assert.IsTrue(await builtIn.ExecuteScalarAsync() is true);
        await fixture.ProbeAsync();
    }

    [TestMethod]
    [DataRow("gateway", "ROLE")]
    [DataRow("wake", "ROLE")]
    [DataRow("gateway", "SESSION AUTHORIZATION")]
    [DataRow("wake", "SESSION AUTHORIZATION")]
    public async Task ElevatedLoginCannotPassBySwitchingToARestrictedIdentity(string kind, string switchKind)
    {
        await using var fixture = await Fixture.CreateAsync(kind);
        var builder = new NpgsqlDataSourceBuilder(fixture.Database.ConnectionString);
        var switchedIdentity = false;
        builder.UsePhysicalConnectionInitializer(_ => throw new NotSupportedException("Async initialization is required."),
            async connection =>
            {
                await using var command = new NpgsqlCommand($"SET {switchKind} \"{fixture.Role}\"", connection);
                await command.ExecuteNonQueryAsync();
                await using var identity = new NpgsqlCommand("SELECT current_user", connection);
                Assert.AreEqual(fixture.Role, await identity.ExecuteScalarAsync());
                switchedIdentity = true;
            });
        await using var switched = builder.Build();
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => kind == "gateway"
            ? GatewayDatabasePrivilegeProbe.ProbeAsync(switched)
            : WorkerWakeDatabasePrivilegeProbe.ProbeAsync(switched));
        Assert.IsTrue(switchedIdentity);
        await fixture.ProbeAsync();
    }

    private sealed class Fixture(PostgresTestDatabase database, NpgsqlDataSource admin, NpgsqlDataSource restricted,
        string role, string kind) : IAsyncDisposable
    {
        internal PostgresTestDatabase Database { get; } = database;
        internal NpgsqlDataSource Admin { get; } = admin;
        internal string Role { get; } = role;
        internal Task ProbeAsync() => kind == "gateway"
            ? GatewayDatabasePrivilegeProbe.ProbeAsync(restricted)
            : WorkerWakeDatabasePrivilegeProbe.ProbeAsync(restricted);

        internal string Violation(string violation) => violation switch
        {
            "private-column" => $"GRANT SELECT (secret) ON private_policy_state TO \"{Role}\"",
            "public-column" => "GRANT SELECT (secret) ON private_policy_state TO PUBLIC",
            "catalog-column" => $"GRANT SELECT (rolpassword) ON pg_catalog.pg_authid TO \"{Role}\"",
            "public-catalog-column" => "GRANT SELECT (rolpassword) ON pg_catalog.pg_authid TO PUBLIC",
            "catalog-maintenance" => $"GRANT MAINTAIN ON pg_catalog.pg_authid TO \"{Role}\"",
            "public-catalog-write" => "GRANT UPDATE (rolsuper) ON pg_catalog.pg_authid TO PUBLIC",
            "catalog-schema-create" => $"GRANT CREATE ON SCHEMA pg_catalog TO \"{Role}\"",
            "public-catalog-schema-create" => "GRANT CREATE ON SCHEMA pg_catalog TO PUBLIC",
            "catalog-definer" => "CREATE FUNCTION pg_catalog.private_policy_definer() RETURNS text LANGUAGE sql SECURITY DEFINER AS 'SELECT secret FROM public.private_policy_state'",
            "system-sequence" => "CREATE SEQUENCE information_schema.private_policy_sequence; GRANT USAGE ON SEQUENCE information_schema.private_policy_sequence TO PUBLIC",
            "private-maintenance" => $"GRANT MAINTAIN ON private_policy_state TO \"{Role}\"",
            "control-maintenance" => $"GRANT MAINTAIN ON application_requests TO \"{Role}\"",
            "connect-delegation" => $"GRANT CONNECT ON DATABASE \"{Database.DatabaseName}\" TO \"{Role}\" WITH GRANT OPTION",
            "schema-delegation" => $"GRANT USAGE ON SCHEMA public TO \"{Role}\" WITH GRANT OPTION",
            "data-delegation" => kind == "gateway"
                ? $"GRANT SELECT ON application_requests TO \"{Role}\" WITH GRANT OPTION"
                : $"GRANT SELECT (state) ON application_requests TO \"{Role}\" WITH GRANT OPTION",
            "future-role-data" => $"ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT SELECT ON TABLES TO \"{Role}\"",
            "future-public-data" => "ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT SELECT ON TABLES TO PUBLIC",
            "definer" => "CREATE FUNCTION private_policy_definer() RETURNS text LANGUAGE sql SECURITY DEFINER AS 'SELECT secret FROM private_policy_state'",
            "invoker-grant" => $"CREATE FUNCTION private_policy_invoker() RETURNS integer LANGUAGE sql AS 'SELECT 1'; REVOKE ALL ON FUNCTION private_policy_invoker() FROM PUBLIC; GRANT EXECUTE ON FUNCTION private_policy_invoker() TO \"{Role}\"",
            "function-owner" => $"CREATE FUNCTION private_policy_owned() RETURNS integer LANGUAGE sql AS 'SELECT 1'; ALTER FUNCTION private_policy_owned() OWNER TO \"{Role}\"",
            "type-owner" => $"CREATE TYPE private_policy_type AS ENUM ('a'); ALTER TYPE private_policy_type OWNER TO \"{Role}\"",
            "language-owner" => $"CREATE TRUSTED LANGUAGE private_policy_language HANDLER plpgsql_call_handler; ALTER LANGUAGE private_policy_language OWNER TO \"{Role}\"",
            "publication-owner" => $"CREATE PUBLICATION private_policy_publication FOR TABLE private_policy_state; ALTER PUBLICATION private_policy_publication OWNER TO \"{Role}\"",
            "parameter-set" => $"GRANT SET ON PARAMETER log_statement TO \"{Role}\"",
            "parameter-alter-system" => $"GRANT ALTER SYSTEM ON PARAMETER log_statement TO \"{Role}\"",
            "large-object" => $"SELECT lo_from_bytea(0, 'secret'::bytea) AS oid; DO $grant$ DECLARE id oid; BEGIN SELECT oid INTO id FROM pg_largeobject_metadata; EXECUTE format('GRANT SELECT ON LARGE OBJECT %s TO %I', id, '{Role}'); END $grant$",
            "inheritance" => $"ALTER ROLE \"{Role}\" INHERIT",
            "foreign-server" => $"CREATE FOREIGN DATA WRAPPER private_policy_wrapper; CREATE SERVER private_policy_server FOREIGN DATA WRAPPER private_policy_wrapper; GRANT USAGE ON FOREIGN SERVER private_policy_server TO \"{Role}\"",
            "control-column-write" => kind == "gateway"
                ? $"GRANT UPDATE (sequence) ON gateway_traffic_records TO \"{Role}\""
                : $"GRANT UPDATE (state) ON application_requests TO \"{Role}\"",
            "column-reference" => $"GRANT REFERENCES (state) ON application_requests TO \"{Role}\"",
            _ => throw new ArgumentOutOfRangeException(nameof(violation)),
        };

        internal static async Task<Fixture> CreateAsync(string kind)
        {
            var database = await PostgresTestDatabase.TryCreateAsync();
            if (database is null) Assert.Inconclusive("PostgreSQL is required.");
            var admin = NpgsqlDataSource.Create(database!.ConnectionString);
            var role = $"mk8_policy_{kind}_{Guid.NewGuid():N}";
            var connection = new NpgsqlConnectionStringBuilder(database.ConnectionString)
            { Username = role, Password = "test-only" };
            var restricted = NpgsqlDataSource.Create(connection.ConnectionString);
            var fixture = new Fixture(database, admin, restricted, role, kind);
            try
            {
                await using (var context = new EmailDbContext(new DbContextOptionsBuilder<EmailDbContext>()
                                 .UseNpgsql(database.ConnectionString).Options))
                    await context.Database.EnsureCreatedAsync();
                await PostgresMessagingSchema.EnsureAsync(admin);
                await using var setup = admin.CreateCommand($"""
                    REVOKE ALL ON DATABASE "{database.DatabaseName}" FROM PUBLIC;
                    REVOKE CREATE ON SCHEMA public FROM PUBLIC;
                    CREATE TABLE private_policy_state (secret text);
                    CREATE ROLE "{role}" LOGIN PASSWORD 'test-only' NOINHERIT;
                    GRANT CONNECT ON DATABASE "{database.DatabaseName}" TO "{role}";
                    GRANT USAGE ON SCHEMA public TO "{role}";
                    """);
                await setup.ExecuteNonQueryAsync();
                var grant = kind == "gateway" ? $"""
                    GRANT SELECT, INSERT ON gateway_traffic_records TO "{role}";
                    GRANT SELECT, INSERT, UPDATE ON application_requests, presentation_requests TO "{role}";
                    GRANT SELECT, INSERT, UPDATE, DELETE ON pop3_maildrop_leases TO "{role}";
                    """ : $"""
                    GRANT SELECT (state, lease_expires_at, deadline_at) ON application_requests TO "{role}";
                    GRANT SELECT (effects_pending, effects_retry_at) ON application_operation_receipts TO "{role}";
                    GRANT SELECT (state, next_attempt_at, lease_expires_at) ON mail_queue_messages TO "{role}";
                    GRANT SELECT (expires_at, is_verified, next_push_at, user_id, last_pushed_change) ON jmap_push_subscriptions TO "{role}";
                    GRANT SELECT (id, is_active) ON users TO "{role}";
                    GRANT SELECT (id, owner_id, alias_for_inbox_id, name, address_id) ON inboxes TO "{role}";
                    GRANT SELECT (id, company_id, is_active) ON addresses TO "{role}";
                    GRANT SELECT (id, is_active) ON companies TO "{role}";
                    GRANT SELECT (account_id, sequence) ON jmap_changes TO "{role}";
                    """;
                await using var allowed = admin.CreateCommand(grant);
                await allowed.ExecuteNonQueryAsync();
                return fixture;
            }
            catch
            {
                await fixture.DisposeAsync();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await restricted.DisposeAsync();
            try
            {
                await using var exists = Admin.CreateCommand("SELECT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = @role)");
                exists.Parameters.AddWithValue("role", Role);
                if (await exists.ExecuteScalarAsync() is true)
                {
                    await using var cleanup = Admin.CreateCommand($"DROP OWNED BY \"{Role}\"; DROP ROLE \"{Role}\"");
                    await cleanup.ExecuteNonQueryAsync();
                }
            }
            finally
            {
                await Admin.DisposeAsync();
                await Database.DisposeAsync();
            }
        }
    }
}
