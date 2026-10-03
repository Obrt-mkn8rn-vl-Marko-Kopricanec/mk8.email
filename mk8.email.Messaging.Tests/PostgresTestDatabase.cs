using Npgsql;

namespace mk8.email.Messaging.Tests;

internal sealed class PostgresTestDatabase : IAsyncDisposable
{
    private const string ConnectionVariable = "MK8_EMAIL_TEST_POSTGRES";
    private readonly string _adminConnectionString;

    private PostgresTestDatabase(
        string adminConnectionString,
        string databaseName,
        string connectionString)
    {
        _adminConnectionString = adminConnectionString;
        DatabaseName = databaseName;
        ConnectionString = connectionString;
    }

    public string DatabaseName { get; }
    public string ConnectionString { get; }

    public static async Task<PostgresTestDatabase?> TryCreateAsync()
    {
        var configured = System.Environment.GetEnvironmentVariable(ConnectionVariable);
        if (string.IsNullOrWhiteSpace(configured))
            return null;

        var configuredBuilder = new NpgsqlConnectionStringBuilder(configured);
        var admin = new NpgsqlConnectionStringBuilder(configured)
        {
            Database = string.IsNullOrWhiteSpace(configuredBuilder.Database)
                ? "postgres"
                : configuredBuilder.Database,
            Pooling = false,
        };
        var databaseName = "mk8email_messaging_test_" + Guid.NewGuid().ToString("N");
        {
            var connection = new NpgsqlConnection(admin.ConnectionString);
            await using var connectionLifetime = connection.ConfigureAwait(false);
            await connection.OpenAsync().ConfigureAwait(false);
            var command = connection.CreateCommand();
            await using var commandLifetime = command.ConfigureAwait(false);

            // Test-only DDL uses quoted GUID-generated identifiers or fixed tracked SQL; PostgreSQL identifiers cannot be value parameters.
#pragma warning disable CA2100
            command.CommandText = $"CREATE DATABASE \"{databaseName}\"";

#pragma warning restore CA2100

            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        var test = new NpgsqlConnectionStringBuilder(admin.ConnectionString)
        {
            Database = databaseName,
            Pooling = true,
            ApplicationName = "mk8.email.Messaging.Tests",
        };
        return new PostgresTestDatabase(
            admin.ConnectionString,
            databaseName,
            test.ConnectionString);
    }

    public async ValueTask DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        var connection = new NpgsqlConnection(_adminConnectionString);
        await using var connectionLifetime = connection.ConfigureAwait(false);
        await connection.OpenAsync().ConfigureAwait(false);
        {
            var terminate = connection.CreateCommand();
            await using var terminateLifetime = terminate.ConfigureAwait(false);
            terminate.CommandText =
                "SELECT pg_terminate_backend(pid) FROM pg_stat_activity "
                + "WHERE datname = @database AND pid <> pg_backend_pid()";
            terminate.Parameters.AddWithValue("database", DatabaseName);
            await terminate.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        var drop = connection.CreateCommand();
        await using var dropLifetime = drop.ConfigureAwait(false);

        // Test-only DDL uses quoted GUID-generated identifiers or fixed tracked SQL; PostgreSQL identifiers cannot be value parameters.
#pragma warning disable CA2100
        drop.CommandText = $"DROP DATABASE IF EXISTS \"{DatabaseName}\"";

#pragma warning restore CA2100

        await drop.ExecuteNonQueryAsync().ConfigureAwait(false);
    }
}
