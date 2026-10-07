using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace mk8.email.Infrastructure.Data;

internal sealed class MailWriteTransactionInterceptor : DbTransactionInterceptor
{
    internal static readonly MailWriteTransactionInterceptor Instance = new();
    private const string GateSql = "SELECT pg_catalog.pg_advisory_xact_lock(1296775238, 1)";

    public override DbTransaction TransactionStarted(DbConnection connection, TransactionEndEventData eventData,
        DbTransaction result)
    {
        if (connection is not NpgsqlConnection) return result;
        using var command = connection.CreateCommand();
        command.Transaction = result;
        command.CommandText = GateSql;
        try { command.ExecuteNonQuery(); }
        catch
        {
            // EF has not registered ownership of the newly begun transaction yet.
            result.Dispose();
            throw;
        }
        return result;
    }

    public override async ValueTask<DbTransaction> TransactionStartedAsync(DbConnection connection,
        TransactionEndEventData eventData, DbTransaction result, CancellationToken cancellationToken = default)
    {
        if (connection is not NpgsqlConnection) return result;
        var command = connection.CreateCommand();
        await using var lifetime = command.ConfigureAwait(false);
        command.Transaction = result;
        command.CommandText = GateSql;
        try { await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false); }
        catch
        {
            await result.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        return result;
    }
}
