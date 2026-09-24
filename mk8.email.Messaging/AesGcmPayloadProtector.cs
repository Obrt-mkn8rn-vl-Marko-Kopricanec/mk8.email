using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace mk8.email.Messaging;

public sealed partial class AesGcmPayloadProtector : IMessagingPayloadProtector, IDisposable
{
    private const int KeySize = 32;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private readonly string _activeKeyId;
    private readonly Dictionary<string, byte[]> _keys;
    private bool _disposed;

    public AesGcmPayloadProtector(
        MessagingEncryptionKey activeKey,
        IEnumerable<MessagingEncryptionKey>? decryptionKeys = null)
    {
        ArgumentNullException.ThrowIfNull(activeKey);
        var validatedActiveKey = ValidateAndClone(activeKey);
        _activeKeyId = validatedActiveKey.Id;
        _keys = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [_activeKeyId] = validatedActiveKey.Key,
        };

        foreach (var key in decryptionKeys ?? [])
        {
            var validated = ValidateAndClone(key);
            if (!_keys.TryAdd(validated.Id, validated.Key))
                CryptographicOperations.ZeroMemory(validated.Key);
        }
    }

    public ProtectedPayload Protect(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> associatedData)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSize];
        using var aes = new AesGcm(_keys[_activeKeyId], TagSize);
        aes.Encrypt(nonce, plaintext, ciphertext, tag, associatedData);
        return new ProtectedPayload(_activeKeyId, ciphertext, nonce, tag);
    }

    public byte[] Unprotect(ProtectedPayload payload, ReadOnlySpan<byte> associatedData)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(payload);
        if (!_keys.TryGetValue(payload.KeyId, out var key))
            throw new CryptographicException("The messaging encryption key is unavailable.");
        if (payload.Nonce.Length != NonceSize || payload.Tag.Length != TagSize)
            throw new CryptographicException("The protected messaging payload is malformed.");

        var plaintext = new byte[payload.Ciphertext.Length];
        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(payload.Nonce, payload.Ciphertext, payload.Tag, plaintext, associatedData);
        return plaintext;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        foreach (var key in _keys.Values)
            CryptographicOperations.ZeroMemory(key);
        _keys.Clear();
        _disposed = true;
    }

    private static MessagingEncryptionKey ValidateAndClone(MessagingEncryptionKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (string.IsNullOrWhiteSpace(key.Id)
            || key.Id.Length > 64
            || !KeyIdPattern().IsMatch(key.Id))
        {
            throw new ArgumentException("The messaging encryption key identifier is invalid.", nameof(key));
        }
        if (key.Key is not { Length: KeySize })
            throw new ArgumentException("A messaging encryption key must contain 32 bytes.", nameof(key));
        return new MessagingEncryptionKey(key.Id, (byte[])key.Key.Clone());
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$", RegexOptions.CultureInvariant, 100)]
    private static partial Regex KeyIdPattern();
}
