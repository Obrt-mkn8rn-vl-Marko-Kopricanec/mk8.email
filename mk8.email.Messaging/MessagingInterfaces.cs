using mk8.email.Contracts.Messaging;

namespace mk8.email.Messaging;

public interface IApplicationRequestClient
{
    Task EnqueueAsync(ApplicationRequest request, CancellationToken cancellationToken = default);

    Task<ApplicationResponse> WaitForResponseAsync(
        Guid requestId,
        DateTimeOffset deadline,
        CancellationToken cancellationToken = default);

    Task<ApplicationResponse> SendAsync(
        ApplicationRequest request,
        CancellationToken cancellationToken = default);

    Task<ApplicationExchangeSnapshot?> GetAsync(
        Guid requestId,
        CancellationToken cancellationToken = default);
}

public interface IApplicationRequestConsumer
{
    Task<ApplicationRequestLease> WaitForRequestAsync(
        string workerId,
        CancellationToken cancellationToken = default);

    Task<ApplicationRequestLease?> TryClaimAsync(
        string workerId,
        CancellationToken cancellationToken = default);

    Task<bool> RenewLeaseAsync(
        ApplicationRequestLease lease,
        CancellationToken cancellationToken = default);

    Task CompleteAsync(
        ApplicationRequestLease lease,
        ApplicationResponse response,
        CancellationToken cancellationToken = default);

    Task FailAsync(
        ApplicationRequestLease lease,
        string errorCode,
        string errorDetail,
        CancellationToken cancellationToken = default);
}

public interface IPresentationRequestClient : IApplicationRequestClient
{
}

public interface IPresentationRequestConsumer : IApplicationRequestConsumer
{
}

public interface IGatewayTrafficJournal
{
    Task AppendAsync(
        GatewayTrafficRecord record,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GatewayTrafficRecord>> ReadSessionAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default);
}

public interface IApplicationTransportControl
{
    Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default);
}
