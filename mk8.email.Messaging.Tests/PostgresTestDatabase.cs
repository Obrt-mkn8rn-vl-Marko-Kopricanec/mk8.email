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
        await using (var connection = new NpgsqlConnection(admin.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"CREATE DATABASE \"{databaseName}\"";
            await command.ExecuteNonQueryAsync();
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
        await using var connection = new NpgsqlConnection(_adminConnectionString);
        await connection.OpenAsync();
        await using (var terminate = connection.CreateCommand())
        {
            terminate.CommandText =
                "SELECT pg_terminate_backend(pid) FROM pg_stat_activity "
                + "WHERE datname = @database AND pid <> pg_backend_pid()";
            terminate.Parameters.AddWithValue("database", DatabaseName);
            await terminate.ExecuteNonQueryAsync();
        }
        await using var drop = connection.CreateCommand();
        drop.CommandText = $"DROP DATABASE IF EXISTS \"{DatabaseName}\"";
        await drop.ExecuteNonQueryAsync();
    }
}
