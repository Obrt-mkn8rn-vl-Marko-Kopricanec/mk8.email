using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace mk8.email.Gateway.Protocols.Jmap;

internal static class JmapPushEncryption
{
    public static bool TryValidateKeys(string p256dh, string auth)
    {
        try
        {
            var publicKey = DecodeBase64Url(p256dh);
            var authSecret = DecodeBase64Url(auth);
            if (publicKey.Length != 65 || publicKey[0] != 4 || authSecret.Length != 16)
                return false;
            using var peer = CreatePeer(publicKey);
            _ = peer.ExportParameters(false);
            return true;
        }
        catch (Exception exception) when (exception is FormatException or CryptographicException)
        {
            return false;
        }
    }

    public static byte[] Encrypt(
        ReadOnlySpan<byte> plaintext,
        string receiverPublicKey,
        string authenticationSecret)
    {
        var receiverBytes = DecodeBase64Url(receiverPublicKey);
        var auth = DecodeBase64Url(authenticationSecret);
        if (receiverBytes.Length != 65 || receiverBytes[0] != 4 || auth.Length != 16)
            throw new CryptographicException("Invalid Web Push receiver keys.");

        using var receiver = CreatePeer(receiverBytes);
        using var sender = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var sharedSecret = sender.DeriveRawSecretAgreement(receiver.PublicKey);
        var senderParameters = sender.ExportParameters(false);
        var senderPublic = new byte[65];
        senderPublic[0] = 4;
        senderParameters.Q.X!.CopyTo(senderPublic, 1);
        senderParameters.Q.Y!.CopyTo(senderPublic, 33);

        var keyInfoPrefix = Encoding.ASCII.GetBytes("WebPush: info\0");
        var keyInfo = new byte[keyInfoPrefix.Length + receiverBytes.Length + senderPublic.Length];
        keyInfoPrefix.CopyTo(keyInfo, 0);
        receiverBytes.CopyTo(keyInfo, keyInfoPrefix.Length);
        senderPublic.CopyTo(keyInfo, keyInfoPrefix.Length + receiverBytes.Length);
        var inputKeyMaterial = Expand(Extract(auth, sharedSecret), keyInfo, 32);
        var salt = RandomNumberGenerator.GetBytes(16);
        var pseudoRandomKey = Extract(salt, inputKeyMaterial);
        var contentEncryptionKey = Expand(
            pseudoRandomKey,
            Encoding.ASCII.GetBytes("Content-Encoding: aes128gcm\0"),
            16);
        var nonce = Expand(
            pseudoRandomKey,
            Encoding.ASCII.GetBytes("Content-Encoding: nonce\0"),
            12);

        var recordPlaintext = new byte[plaintext.Length + 1];
        plaintext.CopyTo(recordPlaintext);
        recordPlaintext[^1] = 2;
        var ciphertext = new byte[recordPlaintext.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(contentEncryptionKey, 16))
            aes.Encrypt(nonce, recordPlaintext, ciphertext, tag);

        var recordSize = checked((uint)Math.Max(4096, ciphertext.Length + tag.Length + 1));
        var result = new byte[16 + 4 + 1 + senderPublic.Length + ciphertext.Length + tag.Length];
        salt.CopyTo(result, 0);
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(16, 4), recordSize);
        result[20] = checked((byte)senderPublic.Length);
        senderPublic.CopyTo(result, 21);
        ciphertext.CopyTo(result, 21 + senderPublic.Length);
        tag.CopyTo(result, 21 + senderPublic.Length + ciphertext.Length);
        return result;
    }

    private static ECDiffieHellman CreatePeer(byte[] publicKey) =>
        ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint
            {
                X = publicKey.AsSpan(1, 32).ToArray(),
                Y = publicKey.AsSpan(33, 32).ToArray(),
            },
        });

    private static byte[] Extract(ReadOnlySpan<byte> salt, ReadOnlySpan<byte> input)
    {
        using var hmac = new HMACSHA256(salt.ToArray());
        return hmac.ComputeHash(input.ToArray());
    }

    private static byte[] Expand(ReadOnlySpan<byte> key, ReadOnlySpan<byte> info, int length)
    {
        using var hmac = new HMACSHA256(key.ToArray());
        var output = new byte[length];
        var previous = Array.Empty<byte>();
        var written = 0;
        byte counter = 1;
        while (written < length)
        {
            var input = new byte[previous.Length + info.Length + 1];
            previous.CopyTo(input, 0);
            info.CopyTo(input.AsSpan(previous.Length));
            input[^1] = counter++;
            previous = hmac.ComputeHash(input);
            var copy = Math.Min(previous.Length, length - written);
            previous.AsSpan(0, copy).CopyTo(output.AsSpan(written));
            written += copy;
        }
        return output;
    }

    private static byte[] DecodeBase64Url(string value)
    {
        if (string.IsNullOrEmpty(value)
            || value.Any(character => !char.IsAsciiLetterOrDigit(character)
                && character is not '-' and not '_'))
            throw new FormatException("Invalid base64url value.");
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += (padded.Length % 4) switch
        {
            0 => string.Empty,
            2 => "==",
            3 => "=",
            _ => throw new FormatException("Invalid base64url length."),
        };
        return Convert.FromBase64String(padded);
    }
}
