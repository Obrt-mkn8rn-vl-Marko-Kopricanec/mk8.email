using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;

namespace mk8.email.Application.Protocol;

internal static class TotpMfa
{
    private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
    private const int TimeStepSeconds = 30;
    private const int Modulus = 1_000_000;

    public static string EncodeSecret(ReadOnlySpan<byte> secret)
    {
        if (secret.IsEmpty)
            return string.Empty;

        var output = new char[(secret.Length * 8 + 4) / 5];
        var buffer = 0;
        var bits = 0;
        var outputIndex = 0;
        foreach (var value in secret)
        {
            buffer = (buffer << 8) | value;
            bits += 8;
            while (bits >= 5)
            {
                bits -= 5;
                output[outputIndex++] = Base32Alphabet[(buffer >> bits) & 31];
            }
        }
        if (bits > 0)
            output[outputIndex] = Base32Alphabet[(buffer << (5 - bits)) & 31];
        return new string(output);
    }

    public static bool TryDecodeSecret(string value, out byte[] secret)
    {
        secret = [];
        if (string.IsNullOrEmpty(value) || value.Length > 128)
            return false;

        var output = new byte[value.Length * 5 / 8];
        var buffer = 0;
        var bits = 0;
        var outputIndex = 0;
        foreach (var character in value)
        {
            var digit = Base32Alphabet.IndexOf(char.ToUpperInvariant(character));
            if (digit < 0)
                return false;
            buffer = (buffer << 5) | digit;
            bits += 5;
            if (bits < 8)
                continue;
            bits -= 8;
            if (outputIndex >= output.Length)
                return false;
            output[outputIndex++] = (byte)(buffer >> bits);
        }

        if (outputIndex != output.Length
            || bits > 0 && (buffer & ((1 << bits) - 1)) != 0)
        {
            return false;
        }
        secret = output;
        return true;
    }

    public static bool TryVerify(
        ReadOnlySpan<byte> secret,
        string code,
        DateTime utcNow,
        long? lastAcceptedTimeStep,
        out long acceptedTimeStep)
    {
        acceptedTimeStep = -1;
        if (code.Length != 6 || code.Any(character => character is < '0' or > '9'))
            return false;

        var current = new DateTimeOffset(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc))
            .ToUnixTimeSeconds() / TimeStepSeconds;
        for (var offset = -1; offset <= 1; offset++)
        {
            var candidate = current + offset;
            if (candidate <= (lastAcceptedTimeStep ?? -1))
                continue;
            var expected = ComputeCode(secret, candidate);
            if (CryptographicOperations.FixedTimeEquals(
                    System.Text.Encoding.ASCII.GetBytes(code),
                    System.Text.Encoding.ASCII.GetBytes(expected)))
            {
                acceptedTimeStep = candidate;
                return true;
            }
        }
        return false;
    }

    public static string ComputeCode(ReadOnlySpan<byte> secret, DateTime utcNow)
    {
        var timeStep = new DateTimeOffset(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc))
            .ToUnixTimeSeconds() / TimeStepSeconds;
        return ComputeCode(secret, timeStep);
    }

    private static string ComputeCode(ReadOnlySpan<byte> secret, long timeStep)
    {
        Span<byte> counter = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(counter, timeStep);
        var hash = HMACSHA1.HashData(secret, counter);
        var offset = hash[^1] & 0x0f;
        var binary = ((hash[offset] & 0x7f) << 24)
            | (hash[offset + 1] << 16)
            | (hash[offset + 2] << 8)
            | hash[offset + 3];
        return (binary % Modulus).ToString("D6", CultureInfo.InvariantCulture);
    }
}
