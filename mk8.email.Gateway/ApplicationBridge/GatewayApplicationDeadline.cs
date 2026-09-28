namespace mk8.email.Gateway.ApplicationBridge;

// This presentation-only scope bounds every durable RPC in one client request.
// It clips the queue envelope deadline, not a caller-supplied Worker payload.
internal sealed class GatewayApplicationDeadline : IDisposable
{
    private static readonly AsyncLocal<DateTimeOffset?> Current = new();
    private readonly DateTimeOffset? _previous;
    private bool _disposed;

    private GatewayApplicationDeadline(DateTimeOffset deadline)
    {
        _previous = Current.Value;
        Current.Value = Clip(deadline);
    }

    public static GatewayApplicationDeadline Begin(TimeSpan timeout) =>
        new(DateTimeOffset.UtcNow.Add(timeout));

    public static DateTimeOffset Clip(DateTimeOffset deadline) =>
        Current.Value is { } existing && existing < deadline ? existing : deadline;

    public static void ThrowIfExpired()
    {
        if (Current.Value <= DateTimeOffset.UtcNow)
            throw new GatewayApplicationException("application-timeout",
                "The application request exceeded its overall deadline.", isUnavailable: true);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        Current.Value = _previous;
        _disposed = true;
    }
}
