using System.Buffers.Binary;
using System.Globalization;

namespace mk8.email.Gateway.Protocols.Ews;

internal static class GatewayEwsFolderSyncState
{
    private const int HeaderBytes = 35;
    private const int FolderBytes = 32;
    private static readonly byte[] Magic = [0x4d, 0x4b, 0x38, 0x46, 0x53, 0x59, 0x4e, 1];
    internal const int MaximumEncodedLength = ((HeaderBytes + GatewayEwsClient.MaximumGraphSize * FolderBytes + 2) / 3) * 4;
    internal sealed record Cursor(Guid Account, string State, bool ScopeSpecified, IReadOnlyDictionary<Guid, Guid?> Parents);

    internal static string Encode(Guid account, string state, IReadOnlyDictionary<Guid, Guid?> parents, bool scopeSpecified = false)
    {
        if (account == Guid.Empty || !TrySequence(state, out var sequence) || !ValidParents(parents))
            throw new InvalidOperationException("Invalid EWS folder synchronization state.");
        var bytes = new byte[HeaderBytes + parents.Count * FolderBytes];
        Magic.CopyTo(bytes, 0);
        account.ToByteArray().CopyTo(bytes, 8);
        BinaryPrimitives.WriteInt64BigEndian(bytes.AsSpan(24, 8), sequence);
        bytes[32] = scopeSpecified ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(33, 2), checked((ushort)parents.Count));
        var index = HeaderBytes;
        foreach (var folder in parents.OrderBy(entry => entry.Key))
        {
            folder.Key.ToByteArray().CopyTo(bytes, index);
            (folder.Value ?? Guid.Empty).ToByteArray().CopyTo(bytes, index + 16);
            index += FolderBytes;
        }
        return Convert.ToBase64String(bytes);
    }

    internal static bool TryDecode(string value, out Cursor? cursor)
    {
        cursor = null;
        if (value.Length is < 4 or > MaximumEncodedLength) return false;
        var bytes = new byte[HeaderBytes + GatewayEwsClient.MaximumGraphSize * FolderBytes];
        if (!Convert.TryFromBase64String(value, bytes, out var count) || count < HeaderBytes
            || !bytes.AsSpan(0, Magic.Length).SequenceEqual(Magic)
            || !string.Equals(Convert.ToBase64String(bytes.AsSpan(0, count)), value, StringComparison.Ordinal)) return false;
        var account = new Guid(bytes.AsSpan(8, 16));
        var sequence = BinaryPrimitives.ReadInt64BigEndian(bytes.AsSpan(24, 8));
        var folderCount = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(33, 2));
        if (account == Guid.Empty || sequence < 0 || bytes[32] > 1 || folderCount > GatewayEwsClient.MaximumGraphSize
            || count != HeaderBytes + folderCount * FolderBytes) return false;
        var parents = new Dictionary<Guid, Guid?>();
        var previous = Guid.Empty;
        for (var index = HeaderBytes; index < count; index += FolderBytes)
        {
            var id = new Guid(bytes.AsSpan(index, 16));
            var parent = new Guid(bytes.AsSpan(index + 16, 16));
            if (id == Guid.Empty || previous.CompareTo(id) >= 0) return false;
            parents.Add(id, parent == Guid.Empty ? null : parent);
            previous = id;
        }
        if (!ValidParents(parents)) return false;
        cursor = new(account, "s" + sequence.ToString(CultureInfo.InvariantCulture), bytes[32] == 1, parents);
        return true;
    }

    private static bool ValidParents(IReadOnlyDictionary<Guid, Guid?> parents)
    {
        if (parents.Count > GatewayEwsClient.MaximumGraphSize || parents.ContainsKey(Guid.Empty)) return false;
        var finished = new HashSet<Guid>();
        foreach (var folder in parents)
        {
            var visited = new HashSet<Guid>();
            var current = folder.Key;
            while (!finished.Contains(current))
            {
                if (!visited.Add(current) || !parents.TryGetValue(current, out var parent)) return false;
                if (parent is null) break;
                current = parent.Value;
            }
            finished.UnionWith(visited);
        }
        return true;
    }

    internal static bool TrySequence(string? state, out long sequence)
    {
        sequence = 0;
        return state is { Length: >= 2 and <= 20 } && state[0] == 's'
            && long.TryParse(state.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out sequence)
            && string.Equals(state, "s" + sequence.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }
}
