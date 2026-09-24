using Microsoft.EntityFrameworkCore;
using Npgsql;
using mk8.email.Infrastructure.Data;

namespace mk8.email.Infrastructure.Tests;

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

        var admin = new NpgsqlConnectionStringBuilder(configured)
        {
            Database = string.IsNullOrWhiteSpace(
                new NpgsqlConnectionStringBuilder(configured).Database)
                    ? "postgres"
                    : new NpgsqlConnectionStringBuilder(configured).Database,
            Pooling = false,
        };
        var databaseName = "mk8email_test_" + Guid.NewGuid().ToString("N");
        {
            var connection = new NpgsqlConnection(admin.ConnectionString);
            await using var connectionLifetime = connection.ConfigureAwait(false);
            await connection.OpenAsync().ConfigureAwait(false);
            var command = connection.CreateCommand();
            await using var commandLifetime = command.ConfigureAwait(false);
            // The identifier is generated from Guid.ToString("N"), never supplied by a caller.
#pragma warning disable CA2100
            command.CommandText = $"CREATE DATABASE \"{databaseName}\"";
#pragma warning restore CA2100
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        var test = new NpgsqlConnectionStringBuilder(admin.ConnectionString)
        {
            Database = databaseName,
            Pooling = false,
        };
        return new PostgresTestDatabase(
            admin.ConnectionString,
            databaseName,
            test.ConnectionString);
    }

    public EmailDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<EmailDbContext>()
            .UseNpgsql(ConnectionString)
            .Options;
        return new EmailDbContext(options);
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
        // DatabaseName is the same generated GUID-based identifier used at creation.
#pragma warning disable CA2100
        drop.CommandText = $"DROP DATABASE IF EXISTS \"{DatabaseName}\"";
#pragma warning restore CA2100
        await drop.ExecuteNonQueryAsync().ConfigureAwait(false);
    }
}
