using System.Security.Cryptography;
using mk8.email.Contracts.Storage;

namespace mk8.email.Messaging;

internal sealed class ProtectedPayloadStorage(
    PostgresMessagingOptions options,
    ILargeObjectStore? largeObjectStore)
{
    private const string EncryptedContentType = "application/vnd.mk8.encrypted-payload";

    public async Task<StoredProtectedPayload> StoreAsync(
        ProtectedPayload payload,
        string objectNamePrefix,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var hash = MessagingValues.Sha256(payload.Ciphertext);
        if (payload.Ciphertext.Length <= options.InlinePayloadThresholdBytes)
        {
            return new StoredProtectedPayload(
                payload.KeyId,
                payload.Ciphertext,
                null,
                payload.Nonce,
                payload.Tag,
                hash,
                payload.Ciphertext.LongLength);
        }

        var store = largeObjectStore
            ?? throw new InvalidOperationException(
                "An Azure Blob-compatible large-object store is required for this payload.");
        if (!string.Equals(store.Provider, LargeObjectProviders.AzureBlob, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Messaging large objects must use the Azure Blob Storage protocol.");
        }

        var objectName = $"{objectNamePrefix}/{hash}";
        var content = new MemoryStream(payload.Ciphertext, writable: false);
        await using var contentLifetime = content.ConfigureAwait(false);
        var result = await store.PutIfAbsentAsync(
            objectName,
            content,
            payload.Ciphertext.LongLength,
            hash,
            EncryptedContentType,
            cancellationToken).ConfigureAwait(false);
        VerifyReference(result.Reference, objectName, payload.Ciphertext.LongLength, hash);
        return new StoredProtectedPayload(
            payload.KeyId,
            null,
            result.Reference,
            payload.Nonce,
            payload.Tag,
            hash,
            payload.Ciphertext.LongLength,
            result.Created);
    }

    public async Task<ProtectedPayload> LoadAsync(
        StoredProtectedPayload payload,
        string description,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (payload.Length < 0 || payload.Length > options.MaxPayloadBytes)
            throw new InvalidOperationException($"The stored {description} length is invalid.");

        byte[] ciphertext;
        if (payload.InlineCiphertext is not null)
        {
            if (payload.LargeObject is not null || payload.InlineCiphertext.LongLength != payload.Length)
                throw new InvalidOperationException($"The stored {description} location is invalid.");
            ciphertext = payload.InlineCiphertext;
        }
        else
        {
            var reference = payload.LargeObject
                ?? throw new InvalidOperationException($"The stored {description} location is missing.");
            var store = largeObjectStore
                ?? throw new InvalidOperationException(
                    $"The Azure Blob-compatible store for {description} is unavailable.");
            if (!string.Equals(store.Provider, LargeObjectProviders.AzureBlob, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"The large-object store for {description} does not use Azure Blob protocol.");
            }
            VerifyReference(reference, reference.ObjectName, payload.Length, payload.Sha256);
            var destination = new MemoryStream(checked((int)payload.Length));
            await using var destinationLifetime = destination.ConfigureAwait(false);
            await store.CopyToAsync(reference, destination, cancellationToken).ConfigureAwait(false);
            ciphertext = destination.ToArray();
            if (ciphertext.LongLength != payload.Length)
                throw new InvalidOperationException($"The stored {description} length changed.");
        }

        if (!CryptographicOperations.FixedTimeEquals(
                SHA256.HashData(ciphertext),
                Convert.FromHexString(payload.Sha256)))
        {
            throw new InvalidOperationException($"The stored {description} ciphertext hash is invalid.");
        }

        return new ProtectedPayload(payload.KeyId, ciphertext, payload.Nonce, payload.Tag);
    }

    public async Task DeleteIfCreatedAsync(
        StoredProtectedPayload payload,
        CancellationToken cancellationToken)
    {
        if (!payload.LargeObjectCreated || payload.LargeObject is null || largeObjectStore is null)
            return;
        _ = await largeObjectStore.DeleteIfMatchAsync(payload.LargeObject, cancellationToken).ConfigureAwait(false);
    }

    private static void VerifyReference(
        LargeObjectReference reference,
        string objectName,
        long length,
        string sha256)
    {
        if (!string.Equals(reference.Provider, LargeObjectProviders.AzureBlob, StringComparison.Ordinal)
            || !string.Equals(reference.ObjectName, objectName, StringComparison.Ordinal)
            || reference.Length != length
            || !string.Equals(reference.Sha256, sha256, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(reference.EntityTag))
        {
            throw new InvalidOperationException("The Azure Blob large-object reference is invalid.");
        }
    }
}
