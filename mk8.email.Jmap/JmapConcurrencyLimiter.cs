using mk8.email.Configuration;

namespace mk8.email.Jmap;

internal sealed class JmapConcurrencyLimiter : IDisposable
{
    private readonly SemaphoreSlim _requests;
    private readonly SemaphoreSlim _uploads;

    public JmapConcurrencyLimiter(EnvironmentConfig environment)
    {
        _requests = new SemaphoreSlim(
            environment.Jmap.MaxConcurrentRequests,
            environment.Jmap.MaxConcurrentRequests);
        _uploads = new SemaphoreSlim(
            environment.Jmap.MaxConcurrentUploads,
            environment.Jmap.MaxConcurrentUploads);
    }

    public Task<IDisposable> AcquireRequestAsync(CancellationToken cancellationToken) =>
        AcquireAsync(_requests, "maxConcurrentRequests", cancellationToken);

    public Task<IDisposable> AcquireUploadAsync(CancellationToken cancellationToken) =>
        AcquireAsync(_uploads, "maxConcurrentUpload", cancellationToken);

    public void Dispose()
    {
        _requests.Dispose();
        _uploads.Dispose();
    }

    private static async Task<IDisposable> AcquireAsync(
        SemaphoreSlim semaphore,
        string limit,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!await semaphore.WaitAsync(TimeSpan.Zero, cancellationToken))
        {
            throw new JmapRequestException(
                "urn:ietf:params:jmap:error:limit",
                400,
                "Request limit exceeded",
                "The server is already processing the maximum number of concurrent requests.",
                limit);
        }
        return new Lease(semaphore);
    }

    private sealed class Lease(SemaphoreSlim semaphore) : IDisposable
    {
        private SemaphoreSlim? _semaphore = semaphore;

        public void Dispose() => Interlocked.Exchange(ref _semaphore, null)?.Release();
    }
}
