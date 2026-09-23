using mk8.email.Contracts.Messaging;
using mk8.email.Messaging;
using Npgsql;

namespace mk8.email.Hosting;

public sealed class RestoreAwareGatewayTrafficJournal(
    NpgsqlDataSource dataSource,
    PostgresGatewayTrafficJournal journal) : IGatewayTrafficJournal
{
    public async Task AppendAsync(
        GatewayTrafficRecord record,
        CancellationToken cancellationToken = default)
    {
        await DistributedRestoreActivationGuard.RequireReadyAsync(dataSource, cancellationToken);
        await journal.AppendAsync(record, cancellationToken);
    }

    public async Task<IReadOnlyList<GatewayTrafficRecord>> ReadSessionAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        await DistributedRestoreActivationGuard.RequireReadyAsync(dataSource, cancellationToken);
        return await journal.ReadSessionAsync(sessionId, cancellationToken);
    }
}

public sealed class RestoreAwareApplicationTransportControl(
    NpgsqlDataSource dataSource,
    PostgresApplicationTransportControl transport) : IApplicationTransportControl
{
    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default) =>
        await DistributedRestoreActivationGuard.IsReadyAsync(dataSource, cancellationToken)
        && await transport.IsAvailableAsync(cancellationToken);
}
