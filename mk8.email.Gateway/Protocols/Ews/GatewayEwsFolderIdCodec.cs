using System.Text;

namespace mk8.email.Gateway.Protocols.Ews;

internal static class GatewayEwsFolderIdCodec
{
    // An identifier is not a credential. Every decoded account is authorized by Worker.
    public static string Encode(Guid account, Guid folder) =>
        Convert.ToBase64String(Encoding.ASCII.GetBytes($"mk8e1:{account:N}:{folder:N}"));

    public static bool TryDecode(string value, out Guid account, out Guid folder)
    {
        account = folder = Guid.Empty;
        Span<byte> bytes = stackalloc byte[71];
        if (value.Length != 96 || !Convert.TryFromBase64String(value, bytes, out var length)
            || length != bytes.Length || !bytes[..6].SequenceEqual("mk8e1:"u8) || bytes[38] != (byte)':'
            || !Guid.TryParseExact(Encoding.ASCII.GetString(bytes[6..38]), "N", out account)
            || account == Guid.Empty
            || !Guid.TryParseExact(Encoding.ASCII.GetString(bytes[39..]), "N", out folder))
            return false;
        return string.Equals(value, Encode(account, folder), StringComparison.Ordinal);
    }
}
