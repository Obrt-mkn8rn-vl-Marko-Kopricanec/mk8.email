using System.Buffers;
using System.Globalization;
using System.Text;

namespace mk8.email.MailWire;

internal sealed class ManageSieveWireReader(Stream stream)
{
    private const int MaximumPhysicalLineBytes = 16 * 1024;
    private const int MaximumTokens = 32;
    private const int MaximumLiterals = 4;
    private const int MaximumCommandBytes = SieveWireCapabilities.MaximumScriptBytes + 64 * 1024;
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private readonly byte[] _buffer = new byte[4096];
    private int _position;
    private int _count;

    public async ValueTask<ManageSieveCommand?> ReadCommandAsync(
        Func<CancellationToken, Task> acknowledgeSynchronizingLiteral,
        CancellationToken cancellationToken)
    {
        var tokens = await ReadTokensAsync(
            requireCommandAtom: true,
            acknowledgeSynchronizingLiteral,
            cancellationToken).ConfigureAwait(false);
        if (tokens is null)
            return null;

        var command = tokens[0];
        return new ManageSieveCommand(
            command.Value.ToUpperInvariant(),
            tokens.Skip(1).ToArray());
    }

    public async ValueTask<string?> ReadSaslResponseAsync(
        Func<CancellationToken, Task> acknowledgeSynchronizingLiteral,
        CancellationToken cancellationToken)
    {
        var tokens = await ReadTokensAsync(
            requireCommandAtom: false,
            acknowledgeSynchronizingLiteral,
            cancellationToken).ConfigureAwait(false);
        if (tokens is null)
            return null;
        if (tokens.Count != 1 || tokens[0].Kind != ManageSieveTokenKind.String)
            throw new ManageSieveProtocolException("A SASL response must be a single string.");
        return tokens[0].Value;
    }

    private async ValueTask<List<ManageSieveToken>?> ReadTokensAsync(
        bool requireCommandAtom,
        Func<CancellationToken, Task> acknowledgeSynchronizingLiteral,
        CancellationToken cancellationToken)
    {
        var firstLine = await ReadPhysicalLineAsync(cancellationToken).ConfigureAwait(false);
        if (firstLine is null)
            return null;

        var tokens = new List<ManageSieveToken>();
        var totalBytes = 0;
        var literalCount = 0;
        var segment = firstLine;
        var firstSegment = true;

        while (true)
        {
            totalBytes = checked(totalBytes + segment.Length);
            if (totalBytes > MaximumCommandBytes)
                throw new ManageSieveProtocolException("The command is too large.");

            var parsed = ParseSegment(segment, tokens, requireCommandAtom && firstSegment);
            if (tokens.Count > MaximumTokens)
                throw new ManageSieveProtocolException("The command has too many arguments.");
            if (parsed is null)
                break;

            literalCount++;
            if (literalCount > MaximumLiterals)
                throw new ManageSieveProtocolException(
                    "The command has too many literals.",
                    isFatal: parsed.Value.IsNonSynchronizing);
            if (parsed.Value.Length > SieveWireCapabilities.MaximumScriptBytes
                || totalBytes > MaximumCommandBytes - parsed.Value.Length)
            {
                throw new ManageSieveProtocolException(
                    "The literal exceeds the one-megabyte limit.",
                    isFatal: parsed.Value.IsNonSynchronizing,
                    responseCode: "QUOTA/MAXSIZE");
            }

            if (!parsed.Value.IsNonSynchronizing)
                await acknowledgeSynchronizingLiteral(cancellationToken).ConfigureAwait(false);

            var literal = await ReadLiteralAsync(parsed.Value.Length, cancellationToken)
                .ConfigureAwait(false);
            tokens.Add(new ManageSieveToken(ManageSieveTokenKind.String, literal));
            totalBytes += parsed.Value.Length;

            segment = await ReadPhysicalLineAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new ManageSieveProtocolException(
                    "The connection ended before the literal command was complete.",
                    isFatal: true);
            firstSegment = false;
        }

        if (tokens.Count == 0)
            throw new ManageSieveProtocolException("A command is required.");
        if (requireCommandAtom && tokens[0].Kind != ManageSieveTokenKind.Atom)
            throw new ManageSieveProtocolException("The command name must be an atom.");
        return tokens;
    }

    private async ValueTask<string> ReadLiteralAsync(int length, CancellationToken cancellationToken)
    {
        var literalBytes = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            await ReadExactlyAsync(literalBytes.AsMemory(0, length), cancellationToken)
                .ConfigureAwait(false);
            try
            {
                return StrictUtf8.GetString(literalBytes, 0, length);
            }
            catch (DecoderFallbackException)
            {
                throw new ManageSieveProtocolException(
                    "Literal strings must contain valid UTF-8.",
                    isFatal: true);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(literalBytes);
        }
    }

    private static LiteralMarker? ParseSegment(
        byte[] bytes,
        List<ManageSieveToken> tokens,
        bool requireCommandAtom)
    {
        string line;
        try
        {
            line = StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            throw new ManageSieveProtocolException("Commands must contain valid UTF-8.");
        }

        var index = 0;
        while (index < line.Length)
        {
            if (line[index] != ' ')
            {
                if (tokens.Count > 0)
                    throw new ManageSieveProtocolException("Command arguments must be separated by spaces.");
            }
            else
            {
                while (index < line.Length && line[index] == ' ')
                    index++;
                if (index == line.Length)
                    break;
            }

            if (line[index] == '"')
            {
                tokens.Add(new ManageSieveToken(
                    ManageSieveTokenKind.String,
                    ParseQuotedString(line, ref index)));
                continue;
            }

            if (line[index] == '{')
            {
                var marker = ParseLiteralMarker(line[index..]);
                if (requireCommandAtom && tokens.Count == 0)
                    throw new ManageSieveProtocolException("The command name must be an atom.");
                return marker;
            }

            var start = index;
            while (index < line.Length && line[index] != ' ')
                index++;
            var value = line[start..index];
            AddAtomOrNumber(value, tokens, requireCommandAtom);
        }

        return null;
    }

    private static void AddAtomOrNumber(
        string value,
        List<ManageSieveToken> tokens,
        bool requireCommandAtom)
    {
        if (tokens.Count == 0 && requireCommandAtom)
        {
            if (!IsAtom(value))
                throw new ManageSieveProtocolException("The command name is not a valid atom.");
            tokens.Add(new ManageSieveToken(ManageSieveTokenKind.Atom, value));
        }
        else if (value.All(character => character is >= '0' and <= '9'))
        {
            tokens.Add(new ManageSieveToken(ManageSieveTokenKind.Number, value));
        }
        else
        {
            throw new ManageSieveProtocolException("String arguments must be quoted or literal strings.");
        }
    }

    private static string ParseQuotedString(string line, ref int index)
    {
        index++;
        var value = new StringBuilder();
        while (index < line.Length)
        {
            var character = line[index++];
            if (character == '"')
                return value.ToString();
            if (character == '\\')
            {
                if (index == line.Length || line[index] is not ('"' or '\\'))
                    throw new ManageSieveProtocolException("The quoted string contains an invalid escape.");
                character = line[index++];
            }
            if (character is '\0' or '\r' or '\n')
                throw new ManageSieveProtocolException("The quoted string contains an invalid character.");
            value.Append(character);
        }

        throw new ManageSieveProtocolException("The quoted string is not terminated.");
    }

    private static LiteralMarker ParseLiteralMarker(string value)
    {
        if (value.Length < 3 || value[0] != '{' || value[^1] != '}')
            throw new ManageSieveProtocolException("The literal marker is invalid.");
        var isNonSynchronizing = value[^2] == '+';
        var digits = value.AsSpan(1, value.Length - (isNonSynchronizing ? 3 : 2));
        if (digits.IsEmpty
            || !ulong.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var length)
            || length > int.MaxValue)
        {
            throw new ManageSieveProtocolException(
                "The literal size is invalid.",
                isFatal: isNonSynchronizing);
        }
        return new LiteralMarker((int)length, isNonSynchronizing);
    }

    private static bool IsAtom(string value) =>
        value.Length > 0
        && value.All(character =>
            character is >= 'A' and <= 'Z'
                or >= 'a' and <= 'z'
                or >= '0' and <= '9'
                or '-' or '_');

    private async ValueTask<byte[]?> ReadPhysicalLineAsync(CancellationToken cancellationToken)
    {
        var bytes = new List<byte>(128);
        var isTooLong = false;
        while (true)
        {
            var value = await ReadByteAsync(cancellationToken).ConfigureAwait(false);
            if (value < 0)
            {
                if (bytes.Count == 0 && !isTooLong)
                    return null;
                throw new ManageSieveProtocolException(
                    "The connection ended in the middle of a command.",
                    isFatal: true);
            }
            if (value == '\n')
            {
                if (!isTooLong && bytes.Count > 0 && bytes[^1] == '\r')
                {
                    bytes.RemoveAt(bytes.Count - 1);
                    return bytes.ToArray();
                }
                throw new ManageSieveProtocolException(
                    isTooLong ? "The command line is too long." : "Commands must end with CRLF.");
            }

            if (isTooLong)
                continue;
            if (bytes.Count >= MaximumPhysicalLineBytes)
            {
                isTooLong = true;
                bytes.Clear();
                continue;
            }
            bytes.Add((byte)value);
        }
    }

    private async ValueTask<int> ReadByteAsync(CancellationToken cancellationToken)
    {
        if (_position >= _count)
        {
            _count = await stream.ReadAsync(_buffer, cancellationToken).ConfigureAwait(false);
            _position = 0;
            if (_count == 0)
                return -1;
        }
        return _buffer[_position++];
    }

    private async ValueTask ReadExactlyAsync(
        Memory<byte> destination,
        CancellationToken cancellationToken)
    {
        var copied = 0;
        if (_position < _count)
        {
            copied = Math.Min(destination.Length, _count - _position);
            _buffer.AsMemory(_position, copied).CopyTo(destination);
            _position += copied;
        }
        while (copied < destination.Length)
        {
            var read = await stream.ReadAsync(destination[copied..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new ManageSieveProtocolException(
                    "The connection ended in the middle of a literal.",
                    isFatal: true);
            }
            copied += read;
        }
    }

    private readonly record struct LiteralMarker(int Length, bool IsNonSynchronizing);
}
