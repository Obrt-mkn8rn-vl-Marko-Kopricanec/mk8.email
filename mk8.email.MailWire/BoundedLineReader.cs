using System.Text;

namespace mk8.email.MailWire;

internal readonly record struct BoundedLine(string? Value, bool IsTooLong);

internal sealed class BoundedLineReader(TextReader reader)
{
    private readonly char[] _buffer = new char[4096];
    private int _position;
    private int _count;

    public async ValueTask<BoundedLine> ReadLineAsync(int maxCharacters, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxCharacters);

        var line = new StringBuilder(Math.Min(maxCharacters, 256));
        var isTooLong = false;

        while (true)
        {
            if (_position >= _count)
            {
                _count = await reader.ReadAsync(_buffer.AsMemory(), cancellationToken);
                _position = 0;

                if (_count == 0)
                {
                    if (line.Length == 0 && !isTooLong)
                        return new BoundedLine(null, false);

                    return isTooLong
                        ? new BoundedLine(null, true)
                        : new BoundedLine(RemoveCarriageReturn(line), false);
                }
            }

            var newlineIndex = Array.IndexOf(_buffer, '\n', _position, _count - _position);
            var segmentEnd = newlineIndex >= 0 ? newlineIndex : _count;
            var segmentLength = segmentEnd - _position;

            if (!isTooLong)
            {
                if (line.Length > maxCharacters - segmentLength)
                {
                    isTooLong = true;
                    line.Clear();
                }
                else
                {
                    line.Append(_buffer, _position, segmentLength);
                }
            }

            _position = newlineIndex >= 0 ? newlineIndex + 1 : _count;
            if (newlineIndex < 0)
                continue;

            return isTooLong
                ? new BoundedLine(null, true)
                : new BoundedLine(RemoveCarriageReturn(line), false);
        }
    }

    public async ValueTask<int> ReadAsync(
        Memory<char> destination,
        CancellationToken cancellationToken)
    {
        if (destination.IsEmpty)
            return 0;

        var copied = 0;
        if (_position < _count)
        {
            copied = Math.Min(destination.Length, _count - _position);
            _buffer.AsMemory(_position, copied).CopyTo(destination);
            _position += copied;
            if (copied == destination.Length)
                return copied;
        }

        return copied + await reader.ReadAsync(destination[copied..], cancellationToken);
    }

    private static string RemoveCarriageReturn(StringBuilder line)
    {
        if (line.Length > 0 && line[^1] == '\r')
            line.Length--;

        return line.ToString();
    }
}
