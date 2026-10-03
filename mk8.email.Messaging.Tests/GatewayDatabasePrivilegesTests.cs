using System.Text;
using mk8.email.Contracts.Messaging;
using mk8.email.Hosting;
using Npgsql;

namespace mk8.email.Messaging.Tests;

[TestClass]
[TestCategory("PostgreSQL")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1812", Justification = "MSTest DiscoverInternals instantiates this test class by reflection; focused discovery is verified by executed test counts.")]
internal sealed class GatewayDatabasePrivilegesTests
{
    [TestMethod]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "MA0051", Justification = "The GatewayTransportWorksWithoutSchemaOrApplicationTablePrivileges scenario keeps one fixture's ordered setup, operation and invariant assertions together.")]
    public async Task GatewayTransportWorksWithoutSchemaOrApplicationTablePrivileges()
    {
        var database = (await RequirePostgresAsync().ConfigureAwait(false));
        await using var databaseLifetime = database.ConfigureAwait(false);
        var adminDataSource = NpgsqlDataSource.Create(database.ConnectionString);
        await using var adminDataSourceLifetime = adminDataSource.ConfigureAwait(false);
        await PostgresMessagingSchema.EnsureAsync(adminDataSource).ConfigureAwait(false);
        var role = $"mk8_gateway_test_{Guid.NewGuid():N}";
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
                CREATE TABLE private_application_state (id integer PRIMARY KEY);
                CREATE TABLE mk8_restore_state (
                    id smallint PRIMARY KEY,
                    state text NOT NULL,
                    database_sha256 text NOT NULL);
                INSERT INTO mk8_restore_state VALUES (1, 'complete', 'private-hash');
                CREATE ROLE "{role}" LOGIN PASSWORD '{password}'
                    NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS NOINHERIT;
                """;

#pragma warning restore CA2100

            await setup.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        try
        {
            {
                var baseGrants = admin.CreateCommand();
                await using var baseGrantsLifetime = baseGrants.ConfigureAwait(false);

                // Test-only DDL uses quoted GUID-generated identifiers or fixed tracked SQL; PostgreSQL identifiers cannot be value parameters.
#pragma warning disable CA2100
                baseGrants.CommandText = $"""
                    GRANT CONNECT ON DATABASE "{database.DatabaseName}" TO "{role}";
                    GRANT USAGE ON SCHEMA public TO "{role}";
                    """;

#pragma warning restore CA2100

                await baseGrants.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
            var gatewayConnection = new NpgsqlConnectionStringBuilder(database.ConnectionString)
            {
                Username = role,
                Password = password,
            };
            {
                var gatewayDataSource = NpgsqlDataSource.Create(gatewayConnection.ConnectionString);
                await using var gatewayDataSourceLifetime = gatewayDataSource.ConfigureAwait(false);
                var transport = new PostgresApplicationTransportControl(gatewayDataSource);
                Assert.IsFalse(await transport.IsAvailableAsync().ConfigureAwait(false));
                await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                    GatewayDatabasePrivilegeProbe.ProbeAsync(gatewayDataSource)).ConfigureAwait(false);
                {
                    var grant = admin.CreateCommand();
                    await using var grantLifetime = grant.ConfigureAwait(false);

                    // Test-only DDL uses quoted GUID-generated identifiers or fixed tracked SQL; PostgreSQL identifiers cannot be value parameters.
#pragma warning disable CA2100
                    grant.CommandText = $"""
                        GRANT SELECT, INSERT ON TABLE
                            gateway_traffic_records TO "{role}";
                        GRANT SELECT, INSERT, UPDATE ON TABLE
                            application_requests, presentation_requests TO "{role}";
                        GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE
                            pop3_maildrop_leases TO "{role}";
                        GRANT SELECT (state) ON TABLE
                            mk8_restore_state TO "{role}";
                        """;

#pragma warning restore CA2100

                    await grant.ExecuteNonQueryAsync().ConfigureAwait(false);
                }
                Assert.IsTrue(await transport.IsAvailableAsync().ConfigureAwait(false));
                await GatewayDatabasePrivilegeProbe.ProbeAsync(gatewayDataSource).ConfigureAwait(false);
                await DistributedRestoreActivationGuard.RequireReadyAsync(gatewayDataSource).ConfigureAwait(false);
                {
                    var privateMarker = gatewayDataSource.CreateCommand(
                                 "SELECT database_sha256 FROM mk8_restore_state");
                    await using var privateMarkerLifetime = privateMarker.ConfigureAwait(false);
                    var error = await Assert.ThrowsExactlyAsync<PostgresException>(
                        () => privateMarker.ExecuteScalarAsync()).ConfigureAwait(false);
                    Assert.AreEqual(PostgresErrorCodes.InsufficientPrivilege, error.SqlState, StringComparer.Ordinal);
                }
                {
                    var excessMarkerGrant = admin.CreateCommand();
                    await using var excessMarkerGrantLifetime = excessMarkerGrant.ConfigureAwait(false);

                    // Test-only DDL uses quoted GUID-generated identifiers or fixed tracked SQL; PostgreSQL identifiers cannot be value parameters.
#pragma warning disable CA2100
                    excessMarkerGrant.CommandText =
                        $"GRANT UPDATE (state) ON mk8_restore_state TO \"{role}\"";

#pragma warning restore CA2100

                    await excessMarkerGrant.ExecuteNonQueryAsync().ConfigureAwait(false);
                    await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                        GatewayDatabasePrivilegeProbe.ProbeAsync(gatewayDataSource)).ConfigureAwait(false);

                    // Test-only DDL uses quoted GUID-generated identifiers or fixed tracked SQL; PostgreSQL identifiers cannot be value parameters.
#pragma warning disable CA2100
                    excessMarkerGrant.CommandText =
                        $"REVOKE UPDATE (state) ON mk8_restore_state FROM \"{role}\"";

#pragma warning restore CA2100

                    await excessMarkerGrant.ExecuteNonQueryAsync().ConfigureAwait(false);
                    await GatewayDatabasePrivilegeProbe.ProbeAsync(gatewayDataSource).ConfigureAwait(false);

                    // Test-only DDL uses quoted GUID-generated identifiers or fixed tracked SQL; PostgreSQL identifiers cannot be value parameters.
#pragma warning disable CA2100
                    excessMarkerGrant.CommandText =
                        $"GRANT SELECT (database_sha256) ON mk8_restore_state TO \"{role}\"";

#pragma warning restore CA2100

                    await excessMarkerGrant.ExecuteNonQueryAsync().ConfigureAwait(false);
                    await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                        GatewayDatabasePrivilegeProbe.ProbeAsync(gatewayDataSource)).ConfigureAwait(false);

                    // Test-only DDL uses quoted GUID-generated identifiers or fixed tracked SQL; PostgreSQL identifiers cannot be value parameters.
#pragma warning disable CA2100
                    excessMarkerGrant.CommandText =
                        $"REVOKE SELECT (database_sha256) ON mk8_restore_state FROM \"{role}\"";

#pragma warning restore CA2100

                    await excessMarkerGrant.ExecuteNonQueryAsync().ConfigureAwait(false);
                }
                {
                    var excessGrant = admin.CreateCommand();
                    await using var excessGrantLifetime = excessGrant.ConfigureAwait(false);

                    // Test-only DDL uses quoted GUID-generated identifiers or fixed tracked SQL; PostgreSQL identifiers cannot be value parameters.
#pragma warning disable CA2100
                    excessGrant.CommandText = $"GRANT SELECT ON private_application_state TO \"{role}\"";

#pragma warning restore CA2100

                    await excessGrant.ExecuteNonQueryAsync().ConfigureAwait(false);
                    await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                        GatewayDatabasePrivilegeProbe.ProbeAsync(gatewayDataSource)).ConfigureAwait(false);

                    // Test-only DDL uses quoted GUID-generated identifiers or fixed tracked SQL; PostgreSQL identifiers cannot be value parameters.
#pragma warning disable CA2100
                    excessGrant.CommandText = $"REVOKE SELECT ON private_application_state FROM \"{role}\"";

#pragma warning restore CA2100

                    await excessGrant.ExecuteNonQueryAsync().ConfigureAwait(false);
                }
                await GatewayDatabasePrivilegeProbe.ProbeAsync(gatewayDataSource).ConfigureAwait(false);
                {
                    var excessSchemaGrant = admin.CreateCommand();
                    await using var excessSchemaGrantLifetime = excessSchemaGrant.ConfigureAwait(false);

                    // Test-only DDL uses quoted GUID-generated identifiers or fixed tracked SQL; PostgreSQL identifiers cannot be value parameters.
#pragma warning disable CA2100
                    excessSchemaGrant.CommandText = $"GRANT CREATE ON SCHEMA public TO \"{role}\"";

#pragma warning restore CA2100

                    await excessSchemaGrant.ExecuteNonQueryAsync().ConfigureAwait(false);
                    await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                        GatewayDatabasePrivilegeProbe.ProbeAsync(gatewayDataSource)).ConfigureAwait(false);

                    // Test-only DDL uses quoted GUID-generated identifiers or fixed tracked SQL; PostgreSQL identifiers cannot be value parameters.
#pragma warning disable CA2100
                    excessSchemaGrant.CommandText = $"REVOKE CREATE ON SCHEMA public FROM \"{role}\"";

#pragma warning restore CA2100

                    await excessSchemaGrant.ExecuteNonQueryAsync().ConfigureAwait(false);
                }
                await GatewayDatabasePrivilegeProbe.ProbeAsync(gatewayDataSource).ConfigureAwait(false);
                {
                    var excessControlGrant = admin.CreateCommand();
                    await using var excessControlGrantLifetime = excessControlGrant.ConfigureAwait(false);

                    // Test-only DDL uses quoted GUID-generated identifiers or fixed tracked SQL; PostgreSQL identifiers cannot be value parameters.
#pragma warning disable CA2100
                    excessControlGrant.CommandText = $"GRANT DELETE ON gateway_traffic_records TO \"{role}\"";

#pragma warning restore CA2100

                    await excessControlGrant.ExecuteNonQueryAsync().ConfigureAwait(false);
                    await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                        GatewayDatabasePrivilegeProbe.ProbeAsync(gatewayDataSource)).ConfigureAwait(false);

                    // Test-only DDL uses quoted GUID-generated identifiers or fixed tracked SQL; PostgreSQL identifiers cannot be value parameters.
#pragma warning disable CA2100
                    excessControlGrant.CommandText = $"REVOKE DELETE ON gateway_traffic_records FROM \"{role}\"";

#pragma warning restore CA2100

                    await excessControlGrant.ExecuteNonQueryAsync().ConfigureAwait(false);
                }
                await GatewayDatabasePrivilegeProbe.ProbeAsync(gatewayDataSource).ConfigureAwait(false);
                {
                    var forbiddenControlWrites = gatewayDataSource.CreateCommand(
                                 """
                                 SELECT has_table_privilege(current_user,
                                     'gateway_traffic_records', 'UPDATE')
                                     OR has_table_privilege(current_user,
                                         'application_requests', 'DELETE')
                                     OR has_table_privilege(current_user,
                                         'presentation_requests', 'DELETE')
                                 """);
                    await using var forbiddenControlWritesLifetime = forbiddenControlWrites.ConfigureAwait(false);
                    Assert.AreEqual(false, await forbiddenControlWrites.ExecuteScalarAsync().ConfigureAwait(false));
                }
                {
                    var privileges = gatewayDataSource.CreateCommand(
                                 "SELECT has_schema_privilege(current_user, 'public', 'CREATE')");
                    await using var privilegesLifetime = privileges.ConfigureAwait(false);
                    Assert.AreEqual(false, await privileges.ExecuteScalarAsync().ConfigureAwait(false));
                }
                {
                    var forbidden = gatewayDataSource.CreateCommand(
                                 "SELECT id FROM private_application_state");
                    await using var forbiddenLifetime = forbidden.ConfigureAwait(false);
                    var error = await Assert.ThrowsExactlyAsync<PostgresException>(
                        () => forbidden.ExecuteScalarAsync()).ConfigureAwait(false);
                    Assert.AreEqual(PostgresErrorCodes.InsufficientPrivilege, error.SqlState, StringComparer.Ordinal);
                }
                {
                    var forbiddenSchema = gatewayDataSource.CreateCommand(
                                 "CREATE TABLE unauthorized_gateway_schema_change (id integer)");
                    await using var forbiddenSchemaLifetime = forbiddenSchema.ConfigureAwait(false);
                    var error = await Assert.ThrowsExactlyAsync<PostgresException>(
                        () => forbiddenSchema.ExecuteNonQueryAsync()).ConfigureAwait(false);
                    Assert.AreEqual(PostgresErrorCodes.InsufficientPrivilege, error.SqlState, StringComparer.Ordinal);
                }

                using var protector = AesGcmPayloadProtectorTests.CreateProtector(
                    "gateway", "restricted-role");
                var sessionId = Guid.CreateVersion7();
                var record = new GatewayTrafficRecord(
                    Guid.CreateVersion7(),
                    sessionId,
                    0,
                    GatewayTrafficDirections.Inbound,
                    "smtp",
                    "application/octet-stream",
                    Encoding.ASCII.GetBytes("EHLO client.example\r\n"),
                    new Dictionary<string, string>(StringComparer.Ordinal),
                    DateTimeOffset.UtcNow);
                var journal = new RestoreAwareGatewayTrafficJournal(
                    gatewayDataSource,
                    new PostgresGatewayTrafficJournal(gatewayDataSource, protector));
                await journal.AppendAsync(record).ConfigureAwait(false);
                Assert.HasCount(1, await journal.ReadSessionAsync(sessionId).ConfigureAwait(false));

                var guardedTransport = new RestoreAwareApplicationTransportControl(
                    gatewayDataSource, transport);
                Assert.IsTrue(await guardedTransport.IsAvailableAsync().ConfigureAwait(false));
                {
                    var pending = admin.CreateCommand();
                    await using var pendingLifetime = pending.ConfigureAwait(false);
                    pending.CommandText =
                        "UPDATE mk8_restore_state SET state = 'pending' WHERE id = 1";
                    await pending.ExecuteNonQueryAsync().ConfigureAwait(false);
                }
                Assert.IsFalse(await guardedTransport.IsAvailableAsync().ConfigureAwait(false));
                await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                    journal.AppendAsync(record with { Id = Guid.CreateVersion7(), Sequence = 1 })).ConfigureAwait(false);
                {
                    var complete = admin.CreateCommand();
                    await using var completeLifetime = complete.ConfigureAwait(false);
                    complete.CommandText =
                        "UPDATE mk8_restore_state SET state = 'complete' WHERE id = 1";
                    await complete.ExecuteNonQueryAsync().ConfigureAwait(false);
                }
                Assert.IsTrue(await guardedTransport.IsAvailableAsync().ConfigureAwait(false));
                Assert.HasCount(1, await journal.ReadSessionAsync(sessionId).ConfigureAwait(false));

                var now = DateTimeOffset.UtcNow;
                var request = new ApplicationRequest(
                    Guid.CreateVersion7(),
                    Guid.CreateVersion7(),
                    0,
                    "smtp",
                    "smtp.recipient.check",
                    "application/json",
                    Encoding.ASCII.GetBytes("{}"),
                    new Dictionary<string, string>(StringComparer.Ordinal),
                    now,
                    now.AddMinutes(1));
                var requests = new PostgresApplicationBus(gatewayDataSource, protector);
                await requests.EnqueueAsync(request).ConfigureAwait(false);
                Assert.AreEqual(ApplicationExchangeStates.Pending,
                    (await requests.GetAsync(request.Id).ConfigureAwait(false))?.State, StringComparer.Ordinal);

                var reverseRequest = request with
                {
                    Id = Guid.CreateVersion7(),
                    SessionId = Guid.CreateVersion7(),
                    Protocol = "presentation",
                    Operation = "smtp.deliver",
                };
                var adminPresentation = new PostgresPresentationBus(adminDataSource, protector);
                await adminPresentation.EnqueueAsync(reverseRequest).ConfigureAwait(false);
                var presentation = new PostgresPresentationBus(gatewayDataSource, protector);
                var lease = await presentation.TryClaimAsync("gateway@test").ConfigureAwait(false);
                Assert.IsNotNull(lease);
                Assert.AreEqual(reverseRequest.Id, lease.Request.Id);
                await presentation.CompleteAsync(
                    lease,
                    new ApplicationResponse(
                        reverseRequest.Id,
                        "application/json",
                        Encoding.ASCII.GetBytes("{}"),
                        new Dictionary<string, string>(StringComparer.Ordinal))).ConfigureAwait(false);

                var maildrop = new PostgresPop3MaildropLeaseStore(gatewayDataSource);
                var acquired = await maildrop.TryAcquireAsync(
                    Guid.CreateVersion7(), TimeSpan.FromMinutes(1)).ConfigureAwait(false);
                Assert.IsNotNull(acquired);
                Assert.IsTrue(await maildrop.RenewAsync(acquired, TimeSpan.FromMinutes(1)).ConfigureAwait(false));
                await maildrop.ReleaseAsync(acquired).ConfigureAwait(false);
            }
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

    private static async Task<PostgresTestDatabase> RequirePostgresAsync()
    {
        var database = await PostgresTestDatabase.TryCreateAsync().ConfigureAwait(false);
        if (database is null)
        {
            Assert.Inconclusive(
                "Set MK8_EMAIL_TEST_POSTGRES to a PostgreSQL admin connection string.");
            throw new InvalidOperationException("PostgreSQL integration test configuration is required.");
        }
        return database;
    }
}
