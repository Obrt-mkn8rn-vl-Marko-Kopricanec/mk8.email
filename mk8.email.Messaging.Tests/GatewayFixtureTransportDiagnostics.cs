using mk8.email.Contracts.Messaging;

namespace mk8.email.Messaging.Tests;

// Test-only request/response boundary data, never payloads, error details, SQL or provider identities.
internal static class GatewayFixtureTransportDiagnostics
{
    private static async Task<T> ObserveAsync<T>(GatewayFixtureDiagnostics diagnostics, Guid request,
        GatewayFixtureDiagnostics.Operation operation, GatewayFixtureDiagnostics.Activity activity,
        Func<Task<T>> invoke, CancellationToken cancellationToken)
    {
        var span = Guid.CreateVersion7();
        diagnostics.Record(GatewayFixtureDiagnostics.Phase.TransportStart, request, operation, activity, span);
        var outcome = GatewayFixtureDiagnostics.Phase.TransportFault;
        try
        {
            var result = await invoke().ConfigureAwait(false);
            outcome = GatewayFixtureDiagnostics.Phase.TransportReturned;
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            outcome = GatewayFixtureDiagnostics.Phase.TransportCancelled;
            throw;
        }
        finally { diagnostics.Record(outcome, request, operation, activity, span); }
    }

    internal sealed class Consumer(IApplicationRequestConsumer inner, GatewayFixtureDiagnostics diagnostics)
        : IApplicationRequestConsumer
    {
        // Unclaimed/background scans are not assigned an invented request correlation.
        public Task<ApplicationRequestLease> WaitForRequestAsync(string workerId, CancellationToken cancellationToken = default) =>
            inner.WaitForRequestAsync(workerId, cancellationToken);
        public Task<ApplicationRequestLease?> TryClaimAsync(string workerId, CancellationToken cancellationToken = default) =>
            inner.TryClaimAsync(workerId, cancellationToken);

        public Task<bool> RenewLeaseAsync(ApplicationRequestLease lease, CancellationToken cancellationToken = default) =>
            ObserveAsync(diagnostics, lease.Request.Id, GatewayFixtureDiagnostics.Classify(lease.Request.Operation),
                GatewayFixtureDiagnostics.Activity.LeaseRenew, () => inner.RenewLeaseAsync(lease, cancellationToken), cancellationToken);

        public async Task CompleteAsync(ApplicationRequestLease lease, ApplicationResponse response,
            CancellationToken cancellationToken = default) =>
            await ObserveAsync(diagnostics, lease.Request.Id, GatewayFixtureDiagnostics.Classify(lease.Request.Operation),
                GatewayFixtureDiagnostics.Activity.ResponseComplete, async () =>
                {
                    await inner.CompleteAsync(lease, response, cancellationToken).ConfigureAwait(false);
                    return true;
                }, cancellationToken).ConfigureAwait(false);

        public async Task FailAsync(ApplicationRequestLease lease, string errorCode, string errorDetail,
            CancellationToken cancellationToken = default) =>
            await ObserveAsync(diagnostics, lease.Request.Id, GatewayFixtureDiagnostics.Classify(lease.Request.Operation),
                GatewayFixtureDiagnostics.Activity.RequestFail, async () =>
                {
                    await inner.FailAsync(lease, errorCode, errorDetail, cancellationToken).ConfigureAwait(false);
                    return true;
                }, cancellationToken).ConfigureAwait(false);
    }

    internal sealed class Client(IApplicationRequestClient inner, GatewayFixtureDiagnostics diagnostics)
        : IApplicationRequestClient
    {
        public Task<ApplicationResponse> SendAsync(ApplicationRequest request, CancellationToken cancellationToken = default) =>
            ObserveAsync(diagnostics, request.Id, GatewayFixtureDiagnostics.Classify(request.Operation),
                GatewayFixtureDiagnostics.Activity.ClientBusSend, () => inner.SendAsync(request, cancellationToken), cancellationToken);

        // These are direct pass-throughs, not per-poll logging or a replacement Send implementation.
        public Task EnqueueAsync(ApplicationRequest request, CancellationToken cancellationToken = default) =>
            inner.EnqueueAsync(request, cancellationToken);
        public Task<ApplicationResponse> WaitForResponseAsync(Guid requestId, DateTimeOffset deadline,
            CancellationToken cancellationToken = default) => inner.WaitForResponseAsync(requestId, deadline, cancellationToken);
        public Task<ApplicationExchangeSnapshot?> GetAsync(Guid requestId, CancellationToken cancellationToken = default) =>
            inner.GetAsync(requestId, cancellationToken);
    }
}
