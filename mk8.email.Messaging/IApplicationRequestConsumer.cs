using mk8.email.Contracts.Messaging;

namespace mk8.email.Messaging;

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
