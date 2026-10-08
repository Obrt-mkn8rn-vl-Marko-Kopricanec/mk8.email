using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace mk8.email.Gateway.Protocols.Ews;

internal static class GatewayEwsItemSyncState
{
    private const int HeaderBytes = 82;
    private const int ItemBytes = 17;
    private static readonly byte[] Magic = [0x4d, 0x4b, 0x38, 0x49, 0x53, 0x59, 0x4e, 1];
    internal const int MaximumEncodedLength = ((HeaderBytes + GatewayEwsRequestParser.MaximumReferences * ItemBytes + 2) / 3) * 4;
    internal sealed record Cursor(Guid Account, Guid Folder, string State, byte[] Shape, IReadOnlyDictionary<Guid, bool> ReadStates);

    internal static byte[] Shape(IReadOnlySet<string> properties) => SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', properties.Order(StringComparer.Ordinal))));

    internal static string Encode(Guid account, Guid folder, string state, byte[] shape, IReadOnlyDictionary<Guid, bool> items)
    {
        if (account == Guid.Empty || folder == Guid.Empty || !GatewayEwsFolderSyncState.TrySequence(state, out var sequence)
            || shape.Length != 32 || items.Count > GatewayEwsRequestParser.MaximumReferences || items.ContainsKey(Guid.Empty))
            throw new InvalidOperationException("Invalid EWS item synchronization state.");
        var bytes = new byte[HeaderBytes + items.Count * ItemBytes];
        Magic.CopyTo(bytes, 0);
        account.ToByteArray().CopyTo(bytes, 8);
        folder.ToByteArray().CopyTo(bytes, 24);
        BinaryPrimitives.WriteInt64BigEndian(bytes.AsSpan(40, 8), sequence);
        shape.CopyTo(bytes, 48);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(80, 2), checked((ushort)items.Count));
        var index = HeaderBytes;
        foreach (var item in items.OrderBy(entry => entry.Key))
        {
            item.Key.ToByteArray().CopyTo(bytes, index);
            bytes[index + 16] = item.Value ? (byte)1 : (byte)0;
            index += ItemBytes;
        }
        return Convert.ToBase64String(bytes);
    }

    internal static bool TryDecode(string value, out Cursor? cursor)
    {
        cursor = null;
        if (value.Length is < 4 or > MaximumEncodedLength) return false;
        var bytes = new byte[HeaderBytes + GatewayEwsRequestParser.MaximumReferences * ItemBytes];
        if (!Convert.TryFromBase64String(value, bytes, out var length) || length < HeaderBytes
            || !bytes.AsSpan(0, Magic.Length).SequenceEqual(Magic)
            || !string.Equals(Convert.ToBase64String(bytes.AsSpan(0, length)), value, StringComparison.Ordinal)) return false;
        var account = new Guid(bytes.AsSpan(8, 16));
        var folder = new Guid(bytes.AsSpan(24, 16));
        var sequence = BinaryPrimitives.ReadInt64BigEndian(bytes.AsSpan(40, 8));
        var count = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(80, 2));
        if (account == Guid.Empty || folder == Guid.Empty || sequence < 0 || count > GatewayEwsRequestParser.MaximumReferences
            || length != HeaderBytes + count * ItemBytes) return false;
        var items = new Dictionary<Guid, bool>();
        var previous = Guid.Empty;
        for (var index = HeaderBytes; index < length; index += ItemBytes)
        {
            var id = new Guid(bytes.AsSpan(index, 16));
            if (id == Guid.Empty || previous.CompareTo(id) >= 0 || bytes[index + 16] > 1) return false;
            items.Add(id, bytes[index + 16] == 1);
            previous = id;
        }
        cursor = new(account, folder, "s" + sequence.ToString(CultureInfo.InvariantCulture), bytes.AsSpan(48, 32).ToArray(), items);
        return true;
    }
}
