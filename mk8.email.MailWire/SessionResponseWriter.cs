using System.Text;

namespace mk8.email.MailWire;

// Composition avoids StreamWriter subclass fallbacks and AutoFlush's synchronous
// initial flush. Every completed protocol line is still flushed before returning.
[System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "MA0182", Justification = "SMTP, IMAP and POP3 friend assemblies construct this internal response owner; actual native controls execute it.")]
internal sealed class SessionResponseWriter : IAsyncDisposable
{
    private readonly SessionOutputStream _output;
    private readonly StreamWriter _writer;
    private readonly CancellationToken _sessionToken;
    private readonly TaskCompletionSource<Task> _disposal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _disposing;

    internal SessionResponseWriter(Stream stream, Encoding encoding, CancellationToken sessionToken)
    {
        _sessionToken = sessionToken;
        _output = new SessionOutputStream(stream, sessionToken);
        try { _writer = new StreamWriter(_output, encoding, 4096, leaveOpen: true) { NewLine = "\r\n" }; }
        catch { _output.Dispose(); throw; }
    }

    internal async Task WriteLineAsync(string? value)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposing) != 0, this);
        await _writer.WriteLineAsync(value.AsMemory(), _sessionToken).ConfigureAwait(false);
        await _writer.FlushAsync(_sessionToken).ConfigureAwait(false);
    }

    internal Task FlushAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposing) != 0, this);
        return _writer.FlushAsync(cancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.CompareExchange(ref _disposing, 1, 0) == 0)
            _disposal.SetResult(DisposeCoreAsync());
        return new ValueTask(_disposal.Task.Unwrap());
    }

    private async Task DisposeCoreAsync()
    {
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        try { await _writer.DisposeAsync().ConfigureAwait(false); }
        finally { await _output.DisposeAsync().ConfigureAwait(false); }
    }
}
