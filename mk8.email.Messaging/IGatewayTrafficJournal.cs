using mk8.email.Contracts.Messaging;

namespace mk8.email.Messaging;

public interface IGatewayTrafficJournal
{
    Task AppendAsync(
        GatewayTrafficRecord record,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GatewayTrafficRecord>> ReadSessionAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default);
}
