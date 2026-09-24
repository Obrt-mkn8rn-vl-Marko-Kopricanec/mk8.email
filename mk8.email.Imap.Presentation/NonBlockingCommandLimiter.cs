namespace mk8.email.Imap.Presentation;

/// <summary>Reserves a command slot without blocking or requiring host-disposal coordination.</summary>
internal sealed class NonBlockingCommandLimiter
{
    private readonly int _maximum;
    private int _active;

    public NonBlockingCommandLimiter(int maximum)
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

    private sealed class Lease(NonBlockingCommandLimiter owner) : IDisposable
    {
        private NonBlockingCommandLimiter? _owner = owner;

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release();
    }
}
