namespace mk8.email.Messaging.Tests;

// Does not retry, alter a response or extend the original HttpClient/request deadline.
internal sealed class GatewayFixtureFailureHandler(HttpMessageHandler inner, GatewayFixtureDiagnostics diagnostics,
    Func<CancellationToken, Task<string>> snapshot, Action<string> publish) : DelegatingHandler(inner)
{
    internal static readonly TimeSpan CollectionBudget = TimeSpan.FromSeconds(2);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        diagnostics.Record(GatewayFixtureDiagnostics.Phase.ClientSend);
        try
        {
            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            diagnostics.Record(GatewayFixtureDiagnostics.Phase.ClientHeaders);
            return response;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            diagnostics.Record(GatewayFixtureDiagnostics.Phase.ClientCancelled);
            await CaptureAsync().ConfigureAwait(false);
            throw; // Preserve the actual transport cancellation, not a diagnostic or synthetic assertion.
        }
    }

    private async Task CaptureAsync()
    {
        using var budget = new CancellationTokenSource(CollectionBudget);
        try { publish(await snapshot(budget.Token).ConfigureAwait(false)); }
        // A secondary diagnostic failure cannot replace the original HTTP cancellation. Emit only its type.
#pragma warning disable CA1031
        catch (Exception exception) when (exception is not OutOfMemoryException and not AccessViolationException)
#pragma warning restore CA1031
        {
            try { publish("{\"diagnosticCollectionFailureType\":\"" + exception.GetType().Name + "\"}"); }
            // The diagnostic sink is also secondary; preserve the original cancellation if it refuses output.
#pragma warning disable CA1031
            catch (Exception sinkFailure) when (sinkFailure is not OutOfMemoryException and not AccessViolationException)
#pragma warning restore CA1031
            { }
        }
    }
}
