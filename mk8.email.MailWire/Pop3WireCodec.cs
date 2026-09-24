namespace mk8.email.MailWire;

public static class Pop3WireCodec
{
    public static int GetNormalizedCrlfLength(ReadOnlySpan<byte> source)
    {
        var length = 0;
        var endsWithCrlf = false;
        for (var index = 0; index < source.Length; index++)
        {
            var value = source[index];
            if (value == '\r')
            {
                if (index + 1 < source.Length && source[index + 1] == '\n')
                    index++;
                length = checked(length + 2);
                endsWithCrlf = true;
            }
            else if (value == '\n')
            {
                length = checked(length + 2);
                endsWithCrlf = true;
            }
            else
            {
                length = checked(length + 1);
                endsWithCrlf = false;
            }
        }
        return endsWithCrlf ? length : checked(length + 2);
    }

    public static byte[] NormalizeCrlf(ReadOnlySpan<byte> source)
    {
        using var output = new MemoryStream(source.Length + 2);
        for (var index = 0; index < source.Length; index++)
        {
            var value = source[index];
            if (value == '\r')
            {
                if (index + 1 < source.Length && source[index + 1] == '\n')
                    index++;
                output.WriteByte((byte)'\r');
                output.WriteByte((byte)'\n');
            }
            else if (value == '\n')
            {
                output.WriteByte((byte)'\r');
                output.WriteByte((byte)'\n');
            }
            else
            {
                output.WriteByte(value);
            }
        }

        var normalized = output.ToArray();
        if (normalized.Length >= 2
            && normalized[^2] == '\r'
            && normalized[^1] == '\n')
        {
            return normalized;
        }

        Array.Resize(ref normalized, normalized.Length + 2);
        normalized[^2] = (byte)'\r';
        normalized[^1] = (byte)'\n';
        return normalized;
    }

    public static byte[] TakeTop(byte[] message, int bodyLineCount)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentOutOfRangeException.ThrowIfNegative(bodyLineCount);
        var bodyStart = FindHeaderBodySeparator(message);
        if (bodyStart < 0)
            return message;

        var end = bodyStart;
        for (var line = 0; line < bodyLineCount && end < message.Length; line++)
        {
            var nextLine = FindCrlf(message, end);
            end = nextLine < 0 ? message.Length : nextLine + 2;
        }
        return message[..end];
    }

    public static async Task WriteDotStuffedAsync(
        Stream stream,
        byte[] message,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(message);
        var buffer = new byte[8192];
        var buffered = 0;
        var atLineStart = true;
        foreach (var value in message)
        {
            if (atLineStart && value == '.')
            {
                if (buffered == buffer.Length)
                {
                    await stream.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
                    buffered = 0;
                }
                buffer[buffered++] = (byte)'.';
            }
            if (buffered == buffer.Length)
            {
                await stream.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
                buffered = 0;
            }
            buffer[buffered++] = value;
            atLineStart = value == '\n';
        }
        if (buffered > 0)
            await stream.WriteAsync(buffer.AsMemory(0, buffered), cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(".\r\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static int FindHeaderBodySeparator(byte[] message)
    {
        for (var index = 0; index <= message.Length - 4; index++)
        {
            if (message[index] == '\r'
                && message[index + 1] == '\n'
                && message[index + 2] == '\r'
                && message[index + 3] == '\n')
            {
                return index + 4;
            }
        }
        return -1;
    }

    private static int FindCrlf(byte[] message, int start)
    {
        for (var index = start; index < message.Length - 1; index++)
        {
            if (message[index] == '\r' && message[index + 1] == '\n')
                return index;
        }
        return -1;
    }
}
