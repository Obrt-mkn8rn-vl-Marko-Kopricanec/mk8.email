using Microsoft.EntityFrameworkCore;
using mk8.email.Hosting;
using mk8.email.Infrastructure.Data;
using mk8.email.Wake;
using Npgsql;

namespace mk8.email.Messaging.Tests;

[TestClass]
[DoNotParallelize]
[TestCategory("PostgreSQL")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1812", Justification = "MSTest DiscoverInternals instantiates this test class by reflection; focused discovery is verified by executed test counts.")]
internal sealed class RestrictedDatabaseRolePolicyTests
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
        var fixture = (await Fixture.CreateAsync(kind).ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        await fixture.ProbeAsync().ConfigureAwait(false);
        var mutation = fixture.Admin.CreateCommand(fixture.Violation(violation));
        await using var mutationLifetime = mutation.ConfigureAwait(false);
        await mutation.ExecuteNonQueryAsync().ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => fixture.ProbeAsync()).ConfigureAwait(false);
        if (string.Equals(kind, "wake", StringComparison.Ordinal))
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                WorkerWakeSchemaTransition.EnableAsync(fixture.Admin, fixture.Role)).ConfigureAwait(false);
        // Rejection is observational: it must not repair privileges or rotate a role.
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => fixture.ProbeAsync()).ConfigureAwait(false);
    }

    [TestMethod]
    [DataRow("gateway")]
    [DataRow("wake")]
    public async Task SessionSettingsCannotSpoofTheAuditedRoleOrPolicy(string kind)
    {
        var fixture = (await Fixture.CreateAsync(kind).ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        var spoof = new NpgsqlConnectionStringBuilder(fixture.Database.ConnectionString)
        { Options = $"-c mk8.restricted_role={fixture.Role} -c mk8.restricted_role_kind={kind}" };
        var elevated = NpgsqlDataSource.Create(spoof.ConnectionString);
        await using var elevatedLifetime = elevated.ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => string.Equals(kind, "gateway"
, StringComparison.Ordinal) ? GatewayDatabasePrivilegeProbe.ProbeAsync(elevated)
            : WorkerWakeDatabasePrivilegeProbe.ProbeAsync(elevated)).ConfigureAwait(false);
        await fixture.ProbeAsync().ConfigureAwait(false);
    }

    [TestMethod]
    [DataRow("gateway")]
    [DataRow("wake")]
    public async Task BuiltInPublicRoutineAndTypeDefaultsDoNotGiveAdditionalAuthority(string kind)
    {
        var fixture = (await Fixture.CreateAsync(kind).ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        var setup = fixture.Admin.CreateCommand("""
            ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT EXECUTE ON FUNCTIONS TO PUBLIC;
            ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT USAGE ON TYPES TO PUBLIC;
            CREATE FUNCTION harmless_policy_invoker() RETURNS integer LANGUAGE sql AS 'SELECT 1';
            CREATE TYPE harmless_policy_type AS ENUM ('a');
            """);
        await using var setupLifetime = setup.ConfigureAwait(false);
        await setup.ExecuteNonQueryAsync().ConfigureAwait(false);
        var builtIn = fixture.Admin.CreateCommand(
            "SELECT has_table_privilege(@role, 'pg_catalog.pg_settings', 'UPDATE')");
        await using var builtInLifetime = builtIn.ConfigureAwait(false);
        builtIn.Parameters.AddWithValue("role", fixture.Role);
        Assert.IsTrue(await builtIn.ExecuteScalarAsync().ConfigureAwait(false) is true);
        await fixture.ProbeAsync().ConfigureAwait(false);
    }

    [TestMethod]
    [DataRow("gateway", "ROLE")]
    [DataRow("wake", "ROLE")]
    [DataRow("gateway", "SESSION AUTHORIZATION")]
    [DataRow("wake", "SESSION AUTHORIZATION")]
    public async Task ElevatedLoginCannotPassBySwitchingToARestrictedIdentity(string kind, string switchKind)
    {
        var fixture = (await Fixture.CreateAsync(kind).ConfigureAwait(false));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        var builder = new NpgsqlDataSourceBuilder(fixture.Database.ConnectionString);
        var switchedIdentity = false;
        builder.UsePhysicalConnectionInitializer(_ => throw new NotSupportedException("Async initialization is required."),
            async connection =>
            {

                // Only fixed DataRow SQL grammar and GUID-generated quoted fixture role names reach this test-only command.
#pragma warning disable CA2100
                var command = new NpgsqlCommand($"SET {switchKind} \"{fixture.Role}\"", connection);

#pragma warning restore CA2100

                await using var commandLifetime = command.ConfigureAwait(false);
                await command.ExecuteNonQueryAsync().ConfigureAwait(false);
                var identity = new NpgsqlCommand("SELECT current_user", connection);
                await using var identityLifetime = identity.ConfigureAwait(false);
                Assert.AreEqual(fixture.Role, await identity.ExecuteScalarAsync().ConfigureAwait(false));
                switchedIdentity = true;
            });
        var switched = builder.Build();
        await using var switchedLifetime = switched.ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => string.Equals(kind, "gateway"
, StringComparison.Ordinal) ? GatewayDatabasePrivilegeProbe.ProbeAsync(switched)
            : WorkerWakeDatabasePrivilegeProbe.ProbeAsync(switched)).ConfigureAwait(false);
        Assert.IsTrue(switchedIdentity);
        await fixture.ProbeAsync().ConfigureAwait(false);
    }

    private sealed class Fixture(PostgresTestDatabase database, NpgsqlDataSource admin, NpgsqlDataSource restricted,
        string role, string kind) : IAsyncDisposable
    {
        internal PostgresTestDatabase Database { get; } = database;
        internal NpgsqlDataSource Admin { get; } = admin;
        internal string Role { get; } = role;
        internal Task ProbeAsync() => string.Equals(kind, "gateway"
, StringComparison.Ordinal) ? GatewayDatabasePrivilegeProbe.ProbeAsync(restricted)
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
            "data-delegation" => string.Equals(kind, "gateway"
, StringComparison.Ordinal) ? $"GRANT SELECT ON application_requests TO \"{Role}\" WITH GRANT OPTION"
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
            "control-column-write" => string.Equals(kind, "gateway"
, StringComparison.Ordinal) ? $"GRANT UPDATE (sequence) ON gateway_traffic_records TO \"{Role}\""
                : $"GRANT UPDATE (state) ON application_requests TO \"{Role}\"",
            "column-reference" => $"GRANT REFERENCES (state) ON application_requests TO \"{Role}\"",
            _ => throw new ArgumentOutOfRangeException(nameof(violation)),
        };


        internal static async Task<Fixture> CreateAsync(string kind)
        {
            var database = await PostgresTestDatabase.TryCreateAsync().ConfigureAwait(false);
            if (database is null) Assert.Inconclusive("PostgreSQL is required.");
            NpgsqlDataSource? admin = null;
            NpgsqlDataSource? restrictedSource = null;
            var role = $"mk8_policy_{kind}_{Guid.NewGuid():N}";
            try
            {

                // The returned fixture owns this allocation; finally releases untransferred resources if initialization fails.
#pragma warning disable CA2000
                admin = NpgsqlDataSource.Create(database!.ConnectionString);

#pragma warning restore CA2000
                var connection = new NpgsqlConnectionStringBuilder(database.ConnectionString)
                { Username = role, Password = "test-only" };

                // The returned fixture owns this allocation; finally releases untransferred resources if initialization fails.
#pragma warning disable CA2000
                restrictedSource = NpgsqlDataSource.Create(connection.ConnectionString);

#pragma warning restore CA2000

                // The returned fixture owns this allocation; finally releases untransferred resources if initialization fails.
#pragma warning disable CA2000
                var fixture = new Fixture(database, admin, restrictedSource, role, kind);

#pragma warning restore CA2000
                await InitializeAsync(fixture, kind).ConfigureAwait(false);
                admin = null;
                restrictedSource = null;
                database = null;
                return fixture;
            }
            finally
            {
                if (restrictedSource is not null) await restrictedSource.DisposeAsync().ConfigureAwait(false);
                try
                {
                    if (admin is not null) await DeleteRoleAsync(admin, role).ConfigureAwait(false);
                }
                finally
                {
                    if (admin is not null) await admin.DisposeAsync().ConfigureAwait(false);
                    if (database is not null) await database.DisposeAsync().ConfigureAwait(false);
                }
            }
        }

        private static async Task InitializeAsync(Fixture fixture, string kind)
        {
            {
                var context = new EmailDbContext(new DbContextOptionsBuilder<EmailDbContext>()
                             .UseNpgsql(fixture.Database.ConnectionString).Options);
                await using var contextLifetime = context.ConfigureAwait(false);
                await context.Database.EnsureCreatedAsync().ConfigureAwait(false);
            }
            await PostgresMessagingSchema.EnsureAsync(fixture.Admin).ConfigureAwait(false);
            var setup = fixture.Admin.CreateCommand($"""
                    REVOKE ALL ON DATABASE "{fixture.Database.DatabaseName}" FROM PUBLIC;
                    REVOKE CREATE ON SCHEMA public FROM PUBLIC;
                    CREATE TABLE private_policy_state (secret text);
                    CREATE ROLE "{fixture.Role}" LOGIN PASSWORD 'test-only' NOINHERIT;
                    GRANT CONNECT ON DATABASE "{fixture.Database.DatabaseName}" TO "{fixture.Role}";
                    GRANT USAGE ON SCHEMA public TO "{fixture.Role}";
                    """);
            await using var setupLifetime = setup.ConfigureAwait(false);
            await setup.ExecuteNonQueryAsync().ConfigureAwait(false);
            var grant = string.Equals(kind, "gateway", StringComparison.Ordinal) ? $"""
                    GRANT SELECT, INSERT ON gateway_traffic_records TO "{fixture.Role}";
                    GRANT SELECT, INSERT, UPDATE ON application_requests, presentation_requests TO "{fixture.Role}";
                    GRANT SELECT, INSERT, UPDATE, DELETE ON pop3_maildrop_leases TO "{fixture.Role}";
                    """ : $"""
                    GRANT SELECT (state, lease_expires_at, deadline_at) ON application_requests TO "{fixture.Role}";
                    GRANT SELECT (effects_pending, effects_retry_at) ON application_operation_receipts TO "{fixture.Role}";
                    GRANT SELECT (state, next_attempt_at, lease_expires_at) ON mail_queue_messages TO "{fixture.Role}";
                    GRANT SELECT (expires_at, is_verified, next_push_at, user_id, last_pushed_change) ON jmap_push_subscriptions TO "{fixture.Role}";
                    GRANT SELECT (id, is_active) ON users TO "{fixture.Role}";
                    GRANT SELECT (id, owner_id, alias_for_inbox_id, name, address_id) ON inboxes TO "{fixture.Role}";
                    GRANT SELECT (id, company_id, is_active) ON addresses TO "{fixture.Role}";
                    GRANT SELECT (id, is_active) ON companies TO "{fixture.Role}";
                    GRANT SELECT (account_id, sequence) ON jmap_changes TO "{fixture.Role}";
                    """;
            var allowed = fixture.Admin.CreateCommand(grant);
            await using var allowedLifetime = allowed.ConfigureAwait(false);
            await allowed.ExecuteNonQueryAsync().ConfigureAwait(false);

        }

        private static async Task DeleteRoleAsync(NpgsqlDataSource admin, string role)
        {
            var exists = admin.CreateCommand("SELECT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = @role)");
            await using var existsLifetime = exists.ConfigureAwait(false);
            exists.Parameters.AddWithValue("role", role);
            if (await exists.ExecuteScalarAsync().ConfigureAwait(false) is not true) return;
            var cleanup = admin.CreateCommand($"DROP OWNED BY \"{role}\"; DROP ROLE \"{role}\"");
            await using var cleanupLifetime = cleanup.ConfigureAwait(false);
            await cleanup.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            await restricted.DisposeAsync().ConfigureAwait(false);
            try
            {
                var exists = Admin.CreateCommand("SELECT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = @role)");
                await using var existsLifetime = exists.ConfigureAwait(false);
                exists.Parameters.AddWithValue("role", Role);
                if (await exists.ExecuteScalarAsync().ConfigureAwait(false) is true)
                {
                    var cleanup = Admin.CreateCommand($"DROP OWNED BY \"{Role}\"; DROP ROLE \"{Role}\"");
                    await using var cleanupLifetime = cleanup.ConfigureAwait(false);
                    await cleanup.ExecuteNonQueryAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                await Admin.DisposeAsync().ConfigureAwait(false);
                await Database.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
