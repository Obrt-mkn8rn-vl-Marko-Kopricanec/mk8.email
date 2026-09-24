namespace mk8.email.Gateway.Protocols.Pop3;

/// <summary>Reserves a retrieval slot without a blocking wait or disposable service lifetime.</summary>
internal sealed class Pop3RetrievalLimiter
{
    private readonly int _maximum;
    private int _active;

    public Pop3RetrievalLimiter(int maximum)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximum);
        _maximum = maximum;
    }

    public IDisposable? TryAcquire()
    {
        while (true)
        {
            var active = Volatile.Read(ref _active);
            if (active >= _maximum)
                return null;
            if (Interlocked.CompareExchange(ref _active, active + 1, active) == active)
                return new Lease(this);
        }
    }

    private void Release() => Interlocked.Decrement(ref _active);

    private sealed class Lease(Pop3RetrievalLimiter owner) : IDisposable
    {
        private Pop3RetrievalLimiter? _owner = owner;

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release();
    }
}
