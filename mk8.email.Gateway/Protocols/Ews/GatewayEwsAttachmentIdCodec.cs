using System.Buffers.Binary;
using System.Security.Cryptography;

namespace mk8.email.Gateway.Protocols.Ews;

internal static class GatewayEwsAttachmentIdCodec
{
    internal const int MaximumAttachments = 128;

    internal static string Encode(Guid account, Guid message, ReadOnlySpan<byte> hash, int position)
    {
        if (account == Guid.Empty) throw new ArgumentException("Invalid attachment account.", nameof(account));
        if (message == Guid.Empty) throw new ArgumentException("Invalid attachment parent.", nameof(message));
        if (hash.Length != SHA256.HashSizeInBytes) throw new ArgumentException("Invalid attachment content hash.", nameof(hash));
        ArgumentOutOfRangeException.ThrowIfNegative(position);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(position, MaximumAttachments);
        Span<byte> bytes = stackalloc byte[74];
        "mk8a1:"u8.CopyTo(bytes);
        account.TryWriteBytes(bytes[6..22]);
        message.TryWriteBytes(bytes[22..38]);
        hash.CopyTo(bytes[38..70]);
        BinaryPrimitives.WriteInt32BigEndian(bytes[70..], position);
        return Convert.ToBase64String(bytes);
    }

    internal static bool TryDecode(string value, out Guid account, out Guid message, out string hash, out int position)
    {
        account = message = Guid.Empty;
        hash = "";
        position = -1;
        Span<byte> bytes = stackalloc byte[74];
        if (value.Length != 100 || !Convert.TryFromBase64String(value, bytes, out var length) || length != bytes.Length
            || !bytes[..6].SequenceEqual("mk8a1:"u8) || !string.Equals(value, Convert.ToBase64String(bytes), StringComparison.Ordinal)) return false;
        account = new Guid(bytes[6..22]);
        message = new Guid(bytes[22..38]);
        position = BinaryPrimitives.ReadInt32BigEndian(bytes[70..]);
        if (account == Guid.Empty || message == Guid.Empty || position < 0 || position >= MaximumAttachments) return false;
        hash = Convert.ToHexStringLower(bytes[38..70]);
        return true;
    }
}
