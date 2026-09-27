using mk8.email.Contracts.Messaging;

namespace mk8.email.Application.Interfaces;

public interface IDurablePresentationEffectSink
{
    Task EnqueueAsync(ApplicationRequest request, CancellationToken cancellationToken);
}
