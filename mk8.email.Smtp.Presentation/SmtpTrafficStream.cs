using mk8.email.Contracts.Messaging;

namespace mk8.email.Smtp.Presentation;

internal sealed class SmtpTrafficStream(
    Stream inner,
    SmtpTrafficSession traffic,
    bool leaveInnerOpen) : Stream
{
    private int _disposed;

    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => inner.CanWrite;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        var count = inner.Read(buffer);
        if (count > 0)
        {
            traffic.RecordAsync(
                GatewayTrafficDirections.Inbound,
                buffer[..count].ToArray(),
                CancellationToken.None).GetAwaiter().GetResult();
        }
        return count;
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        var count = await inner.ReadAsync(buffer, cancellationToken);
        if (count > 0)
        {
            await traffic.RecordAsync(
                GatewayTrafficDirections.Inbound,
                buffer[..count],
                cancellationToken);
        }
        return count;
    }

    public override Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Write(byte[] buffer, int offset, int count) =>
        Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        traffic.RecordAsync(
            GatewayTrafficDirections.Outbound,
            buffer.ToArray(),
            CancellationToken.None).GetAwaiter().GetResult();
        inner.Write(buffer);
    }

    public override async ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        await traffic.RecordAsync(
            GatewayTrafficDirections.Outbound,
            buffer,
            cancellationToken);
        await inner.WriteAsync(buffer, cancellationToken);
    }

    public override Task WriteAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush() => inner.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) =>
        inner.FlushAsync(cancellationToken);

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0 && !leaveInnerOpen)
            inner.Dispose();
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0 && !leaveInnerOpen)
            await inner.DisposeAsync();
        await base.DisposeAsync();
    }
}
