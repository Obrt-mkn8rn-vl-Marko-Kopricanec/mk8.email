namespace mk8.email.Testing;

internal readonly struct NullableAsyncDisposable(IAsyncDisposable? resource) : IAsyncDisposable
{
    public ValueTask DisposeAsync() => resource?.DisposeAsync() ?? ValueTask.CompletedTask;
}
