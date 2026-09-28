using mk8.email.Configuration;

namespace mk8.email.Gateway.Protocols.Jmap;

internal sealed class GatewayJmapBatchLimiter(EnvironmentConfig environment)
{
    private int _active;

    public IDisposable? TryAcquire(int applicationLimit)
    {
        var limit = Math.Min(environment.Jmap.MaxConcurrentRequests, applicationLimit);
        if (Interlocked.Increment(ref _active) <= limit)
            return new Lease(this);
        Interlocked.Decrement(ref _active);
        return null;
    }

    private sealed class Lease(GatewayJmapBatchLimiter owner) : IDisposable
    {
        private GatewayJmapBatchLimiter? _owner = owner;

        public void Dispose()
        {
            var current = Interlocked.Exchange(ref _owner, null);
            if (current is not null)
                Interlocked.Decrement(ref current._active);
        }
    }
}
