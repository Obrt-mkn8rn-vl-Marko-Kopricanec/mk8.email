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
