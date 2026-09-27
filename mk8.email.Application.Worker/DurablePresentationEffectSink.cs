using mk8.email.Application.Interfaces;
using mk8.email.Contracts.Messaging;
using mk8.email.Messaging;

namespace mk8.email.Application.Worker;

internal sealed class DurablePresentationEffectSink(IPresentationRequestClient requests) : IDurablePresentationEffectSink
{
    public Task EnqueueAsync(ApplicationRequest request, CancellationToken cancellationToken) =>
        requests.EnqueueAsync(request, cancellationToken);
}
