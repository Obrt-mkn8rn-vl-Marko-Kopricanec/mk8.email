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
        await DistributedRestoreActivationGuard.RequireReadyAsync(dataSource, cancellationToken).ConfigureAwait(false);
        await journal.AppendAsync(record, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<GatewayTrafficRecord>> ReadSessionAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        await DistributedRestoreActivationGuard.RequireReadyAsync(dataSource, cancellationToken).ConfigureAwait(false);
        return await journal.ReadSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
    }
}
