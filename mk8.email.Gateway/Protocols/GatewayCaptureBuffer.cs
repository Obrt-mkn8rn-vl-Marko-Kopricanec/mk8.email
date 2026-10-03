namespace mk8.email.Gateway.Protocols;

internal sealed class GatewayCaptureBuffer(long maximumBytes, int failureStatus) : MemoryStream
{
    public override void Write(byte[] buffer, int offset, int count)
    {
        Reserve(count);
        base.Write(buffer, offset, count);
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        Reserve(buffer.Length);
        base.Write(buffer);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Write(buffer, offset, count);
        return Task.CompletedTask;
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Write(buffer.Span);
        return ValueTask.CompletedTask;
    }

    public ReadOnlyMemory<byte> Content => GetBuffer().AsMemory(0, checked((int)Length));

    public override void WriteByte(byte value)
    {
        Reserve(1);
        base.WriteByte(value);
    }

    public override void SetLength(long value)
    {
        if (value > maximumBytes)
            throw new BadHttpRequestException("The HTTP capture body exceeds its budget.", failureStatus);
        if (value > Capacity)
            Capacity = checked((int)value);
        base.SetLength(value);
    }

    private void Reserve(int count)
    {
        var required = checked(Position + count);
        if (required > maximumBytes)
            throw new BadHttpRequestException("The HTTP capture body exceeds its budget.", failureStatus);
        if (required > Capacity)
            Capacity = checked((int)Math.Min(maximumBytes, Math.Max(required, Math.Max(4096L, 2L * Capacity))));
    }
}
