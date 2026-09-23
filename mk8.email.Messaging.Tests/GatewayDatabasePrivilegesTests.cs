using System.Text;
using mk8.email.Contracts.Messaging;
using Npgsql;

namespace mk8.email.Messaging.Tests;

[TestClass]
[TestCategory("PostgreSQL")]
public sealed class GatewayDatabasePrivilegesTests
{
    [TestMethod]
    public async Task GatewayTransportWorksWithoutSchemaOrApplicationTablePrivileges()
    {
        await using var database = await RequirePostgresAsync();
        await using var adminDataSource = NpgsqlDataSource.Create(database.ConnectionString);
        await PostgresMessagingSchema.EnsureAsync(adminDataSource);
        var role = $"mk8_gateway_test_{Guid.NewGuid():N}";
        var password = Guid.NewGuid().ToString("N");
        await using var admin = await adminDataSource.OpenConnectionAsync();
        await using (var setup = admin.CreateCommand())
        {
            setup.CommandText = $"""
                REVOKE CREATE ON SCHEMA public FROM PUBLIC;
                CREATE TABLE private_application_state (id integer PRIMARY KEY);
                CREATE ROLE "{role}" LOGIN PASSWORD '{password}'
                    NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;
                """;
            await setup.ExecuteNonQueryAsync();
        }

        try
        {
            await using (var baseGrants = admin.CreateCommand())
            {
                baseGrants.CommandText = $"""
                    GRANT CONNECT ON DATABASE "{database.DatabaseName}" TO "{role}";
                    GRANT USAGE ON SCHEMA public TO "{role}";
                    """;
                await baseGrants.ExecuteNonQueryAsync();
            }
            var gatewayConnection = new NpgsqlConnectionStringBuilder(database.ConnectionString)
            {
                Username = role,
                Password = password,
            };
            await using (var gatewayDataSource = NpgsqlDataSource.Create(gatewayConnection.ConnectionString))
            {
                var transport = new PostgresApplicationTransportControl(gatewayDataSource);
                Assert.IsFalse(await transport.IsAvailableAsync());
                await using (var grant = admin.CreateCommand())
                {
                    grant.CommandText = $"""
                        GRANT SELECT, INSERT ON TABLE
                            gateway_traffic_records TO "{role}";
                        GRANT SELECT, INSERT, UPDATE ON TABLE
                            application_requests, presentation_requests TO "{role}";
                        GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE
                            pop3_maildrop_leases TO "{role}";
                        """;
                    await grant.ExecuteNonQueryAsync();
                }
                Assert.IsTrue(await transport.IsAvailableAsync());
                await using (var forbiddenControlWrites = gatewayDataSource.CreateCommand(
                                 """
                                 SELECT has_table_privilege(current_user,
                                     'gateway_traffic_records', 'UPDATE')
                                     OR has_table_privilege(current_user,
                                         'application_requests', 'DELETE')
                                     OR has_table_privilege(current_user,
                                         'presentation_requests', 'DELETE')
                                 """))
                {
                    Assert.AreEqual(false, await forbiddenControlWrites.ExecuteScalarAsync());
                }
                await using (var privileges = gatewayDataSource.CreateCommand(
                                 "SELECT has_schema_privilege(current_user, 'public', 'CREATE')"))
                {
                    Assert.AreEqual(false, await privileges.ExecuteScalarAsync());
                }
                await using (var forbidden = gatewayDataSource.CreateCommand(
                                 "SELECT id FROM private_application_state"))
                {
                    var error = await Assert.ThrowsExactlyAsync<PostgresException>(
                        () => forbidden.ExecuteScalarAsync());
                    Assert.AreEqual(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
                }
                await using (var forbiddenSchema = gatewayDataSource.CreateCommand(
                                 "CREATE TABLE unauthorized_gateway_schema_change (id integer)"))
                {
                    var error = await Assert.ThrowsExactlyAsync<PostgresException>(
                        () => forbiddenSchema.ExecuteNonQueryAsync());
                    Assert.AreEqual(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
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
                    new Dictionary<string, string>(),
                    DateTimeOffset.UtcNow);
                var journal = new PostgresGatewayTrafficJournal(gatewayDataSource, protector);
                await journal.AppendAsync(record);
                Assert.HasCount(1, await journal.ReadSessionAsync(sessionId));

                var now = DateTimeOffset.UtcNow;
                var request = new ApplicationRequest(
                    Guid.CreateVersion7(),
                    Guid.CreateVersion7(),
                    0,
                    "smtp",
                    "smtp.recipient.check",
                    "application/json",
                    Encoding.ASCII.GetBytes("{}"),
                    new Dictionary<string, string>(),
                    now,
                    now.AddMinutes(1));
                var requests = new PostgresApplicationBus(gatewayDataSource, protector);
                await requests.EnqueueAsync(request);
                Assert.AreEqual(ApplicationExchangeStates.Pending,
                    (await requests.GetAsync(request.Id))?.State);

                var reverseRequest = request with
                {
                    Id = Guid.CreateVersion7(),
                    SessionId = Guid.CreateVersion7(),
                    Protocol = "presentation",
                    Operation = "smtp.deliver",
                };
                var adminPresentation = new PostgresPresentationBus(adminDataSource, protector);
                await adminPresentation.EnqueueAsync(reverseRequest);
                var presentation = new PostgresPresentationBus(gatewayDataSource, protector);
                var lease = await presentation.TryClaimAsync("gateway@test");
                Assert.IsNotNull(lease);
                Assert.AreEqual(reverseRequest.Id, lease.Request.Id);
                await presentation.CompleteAsync(
                    lease,
                    new ApplicationResponse(
                        reverseRequest.Id,
                        "application/json",
                        Encoding.ASCII.GetBytes("{}"),
                        new Dictionary<string, string>()));

                var maildrop = new PostgresPop3MaildropLeaseStore(gatewayDataSource);
                var acquired = await maildrop.TryAcquireAsync(
                    Guid.CreateVersion7(), TimeSpan.FromMinutes(1));
                Assert.IsNotNull(acquired);
                Assert.IsTrue(await maildrop.RenewAsync(acquired, TimeSpan.FromMinutes(1)));
                await maildrop.ReleaseAsync(acquired);
            }
        }
        finally
        {
            await using var cleanup = admin.CreateCommand();
            cleanup.CommandText = $"DROP OWNED BY \"{role}\"; DROP ROLE \"{role}\";";
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    private static async Task<PostgresTestDatabase> RequirePostgresAsync()
    {
        var database = await PostgresTestDatabase.TryCreateAsync();
        if (database is null)
        {
            Assert.Inconclusive(
                "Set MK8_EMAIL_TEST_POSTGRES to a PostgreSQL admin connection string.");
            throw new InvalidOperationException("PostgreSQL integration test configuration is required.");
        }
        return database;
    }
}
