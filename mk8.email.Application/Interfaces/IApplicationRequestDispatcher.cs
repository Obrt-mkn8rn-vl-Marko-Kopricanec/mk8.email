using mk8.email.Contracts.Messaging;

namespace mk8.email.Application.Interfaces;

public interface IApplicationRequestDispatcher
{
    Task<ApplicationResponse> DispatchAsync(
        ApplicationRequest request,
        CancellationToken cancellationToken = default);
}
