using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace mk8.email.Messaging.Tests;

// Observe async EF call boundaries only. Never inspect SQL, parameters, rows or exception messages.
// Reader-return is NOT materialization completion. Raw Npgsql/manual transaction-gate calls are outside this hook.
internal sealed class GatewayFixtureCommandDiagnostics(GatewayFixtureDiagnostics diagnostics) : DbCommandInterceptor
{
    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
        InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        Record(GatewayFixtureDiagnostics.Phase.IoStart, eventData);
        return ValueTask.FromResult(result);
    }

    public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
        DbDataReader result, CancellationToken cancellationToken = default)
    {
        Record(GatewayFixtureDiagnostics.Phase.IoReturned, eventData);
        return ValueTask.FromResult(result);
    }

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command, CommandEventData eventData,
        InterceptionResult<object> result, CancellationToken cancellationToken = default)
    {
        Record(GatewayFixtureDiagnostics.Phase.IoStart, eventData);
        return ValueTask.FromResult(result);
    }

    public override ValueTask<object?> ScalarExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
        object? result, CancellationToken cancellationToken = default)
    {
        Record(GatewayFixtureDiagnostics.Phase.IoReturned, eventData);
        return ValueTask.FromResult(result);
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
        InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Record(GatewayFixtureDiagnostics.Phase.IoStart, eventData);
        return ValueTask.FromResult(result);
    }

    public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
        int result, CancellationToken cancellationToken = default)
    {
        Record(GatewayFixtureDiagnostics.Phase.IoReturned, eventData);
        return ValueTask.FromResult(result);
    }

    public override Task CommandFailedAsync(DbCommand command, CommandErrorEventData eventData,
        CancellationToken cancellationToken = default)
    {
        Record(GatewayFixtureDiagnostics.Phase.IoFault, eventData);
        return Task.CompletedTask;
    }

    public override Task CommandCanceledAsync(DbCommand command, CommandEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        Record(GatewayFixtureDiagnostics.Phase.IoCancelled, eventData);
        return Task.CompletedTask;
    }

    private void Record(GatewayFixtureDiagnostics.Phase phase, CommandEventData data) =>
        diagnostics.RecordIo(phase, data.ExecuteMethod switch
        {
            DbCommandMethod.ExecuteReader => GatewayFixtureDiagnostics.Activity.DbReader,
            DbCommandMethod.ExecuteScalar => GatewayFixtureDiagnostics.Activity.DbScalar,
            DbCommandMethod.ExecuteNonQuery => GatewayFixtureDiagnostics.Activity.DbNonQuery,
            _ => GatewayFixtureDiagnostics.Activity.OtherDb,
        }, data.CommandId);
}
