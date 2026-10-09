using mk8.email.Contracts.Storage;

namespace mk8.email.Messaging.Tests;

// Only the Worker domain store is decorated; transport/journal storage remains unchanged.
internal sealed class GatewayFixtureBlobDiagnostics(ILargeObjectStore inner, GatewayFixtureDiagnostics diagnostics)
    : ILargeObjectStore
{
    public string Provider => inner.Provider;

    public Task<LargeObjectWriteResult> PutIfAbsentAsync(string objectName, Stream content, long length,
        string sha256, string contentType, CancellationToken cancellationToken = default) =>
        ObserveAsync(GatewayFixtureDiagnostics.Activity.BlobPut,
            () => inner.PutIfAbsentAsync(objectName, content, length, sha256, contentType, cancellationToken), cancellationToken);

    public async Task CopyToAsync(LargeObjectReference reference, Stream destination, CancellationToken cancellationToken = default) =>
        await ObserveAsync(GatewayFixtureDiagnostics.Activity.BlobRead, async () =>
        {
            await inner.CopyToAsync(reference, destination, cancellationToken).ConfigureAwait(false);
            return true;
        }, cancellationToken).ConfigureAwait(false);

    public Task<bool> DeleteIfMatchAsync(LargeObjectReference reference, CancellationToken cancellationToken = default) =>
        ObserveAsync(GatewayFixtureDiagnostics.Activity.BlobDelete,
            () => inner.DeleteIfMatchAsync(reference, cancellationToken), cancellationToken);

    private async Task<T> ObserveAsync<T>(GatewayFixtureDiagnostics.Activity activity, Func<Task<T>> invoke,
        CancellationToken cancellationToken)
    {
        var span = Guid.CreateVersion7();
        diagnostics.RecordIo(GatewayFixtureDiagnostics.Phase.IoStart, activity, span);
        var phase = GatewayFixtureDiagnostics.Phase.IoFault;
        try
        {
            var result = await invoke().ConfigureAwait(false);
            phase = GatewayFixtureDiagnostics.Phase.IoReturned;
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            phase = GatewayFixtureDiagnostics.Phase.IoCancelled;
            throw;
        }
        finally { diagnostics.RecordIo(phase, activity, span); }
    }
}
