namespace mk8.email.MailWire;

// A response writer (including its disposal flush) must never lose the session
// token. The session still owns the underlying network/TLS/journal stream.
internal sealed class SessionOutputStream(Stream inner, CancellationToken sessionToken) : Stream
{
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => inner.CanWrite;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
        throw new NotSupportedException("Protocol response flushes must be asynchronous.");
    }

    public override async Task FlushAsync(CancellationToken cancellationToken)
    {
        sessionToken.ThrowIfCancellationRequested();
        if (!cancellationToken.CanBeCanceled || cancellationToken == sessionToken)
        {
            await inner.FlushAsync(sessionToken).ConfigureAwait(false);
            return;
        }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(sessionToken, cancellationToken);
        await inner.FlushAsync(linked.Token).ConfigureAwait(false);
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        sessionToken.ThrowIfCancellationRequested();
        if (!cancellationToken.CanBeCanceled || cancellationToken == sessionToken)
        {
            await inner.WriteAsync(buffer, sessionToken).ConfigureAwait(false);
            return;
        }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(sessionToken, cancellationToken);
        await inner.WriteAsync(buffer, linked.Token).ConfigureAwait(false);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    // Protocol responses use awaited asynchronous writes. Refuse an accidental
    // synchronous response rather than silently lose cooperative cancellation.
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
