using System.Text;

namespace mk8.email.Gateway.Protocols.Ews;

internal static class GatewayEwsItemIdCodec
{
    // Separate item/folder domains. The embedded account is never authentication authority.
    public static string Encode(Guid account, Guid item) =>
        Convert.ToBase64String(Encoding.ASCII.GetBytes($"mk8i1:{account:N}:{item:N}"));

    public static bool TryDecode(string value, out Guid account, out Guid item)
    {
        account = item = Guid.Empty;
        Span<byte> bytes = stackalloc byte[71];
        return value.Length == 96 && Convert.TryFromBase64String(value, bytes, out var length)
            && length == bytes.Length && bytes[..6].SequenceEqual("mk8i1:"u8) && bytes[38] == (byte)':'
            && Guid.TryParseExact(Encoding.ASCII.GetString(bytes[6..38]), "N", out account) && account != Guid.Empty
            && Guid.TryParseExact(Encoding.ASCII.GetString(bytes[39..]), "N", out item) && item != Guid.Empty
            && string.Equals(value, Encode(account, item), StringComparison.Ordinal);
    }
}
