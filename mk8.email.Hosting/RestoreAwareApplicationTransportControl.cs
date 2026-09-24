using mk8.email.Contracts.Messaging;
using mk8.email.Messaging;
using Npgsql;

namespace mk8.email.Hosting;

public sealed class RestoreAwareApplicationTransportControl(
    NpgsqlDataSource dataSource,
    PostgresApplicationTransportControl transport) : IApplicationTransportControl
{
    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default) =>
        await DistributedRestoreActivationGuard.IsReadyAsync(dataSource, cancellationToken).ConfigureAwait(false)
        && await transport.IsAvailableAsync(cancellationToken).ConfigureAwait(false);
}
