using System.Collections.Concurrent;
using System.Security.Cryptography;
using mk8.email.Contracts.Storage;

namespace mk8.email.Application.Tests;

internal sealed class InMemoryLargeObjectStore : ILargeObjectStore
{
    private readonly ConcurrentDictionary<string, StoredObject> objects =
        new(StringComparer.Ordinal);

    public string Provider => LargeObjectProviders.AzureBlob;

    public int Count => objects.Count;

    public bool Contains(string objectName) => objects.ContainsKey(objectName);

    public async Task<LargeObjectWriteResult> PutIfAbsentAsync(
        string objectName,
        Stream content,
        long length,
        string sha256,
        string contentType,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(objectName);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        await using var copy = new MemoryStream();
        await content.CopyToAsync(copy, cancellationToken);
        var bytes = copy.ToArray();
        var actualHash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        if (bytes.LongLength != length
            || !string.Equals(actualHash, sha256, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The test object failed its integrity check.");
        }

        var candidate = new StoredObject(
            bytes,
            new LargeObjectReference(
                Provider,
                objectName,
                length,
                sha256,
                $"\"test-{Guid.CreateVersion7():N}\""));
        var stored = objects.GetOrAdd(objectName, candidate);
        if (stored.Reference.Length != length
            || !string.Equals(stored.Reference.Sha256, sha256, StringComparison.Ordinal)
            || !stored.Content.AsSpan().SequenceEqual(bytes))
        {
            throw new InvalidOperationException("The test object name already contains different content.");
        }
        return new LargeObjectWriteResult(stored.Reference, ReferenceEquals(stored, candidate));
    }

    public async Task CopyToAsync(
        LargeObjectReference reference,
        Stream destination,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(destination);
        if (!objects.TryGetValue(reference.ObjectName, out var stored))
            throw new FileNotFoundException("The test object does not exist.", reference.ObjectName);
        if (stored.Reference != reference)
            throw new InvalidOperationException("The test object reference failed its integrity check.");
        await destination.WriteAsync(stored.Content, cancellationToken);
    }

    public Task<bool> DeleteIfMatchAsync(
        LargeObjectReference reference,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reference);
        cancellationToken.ThrowIfCancellationRequested();
        if (!objects.TryGetValue(reference.ObjectName, out var stored)
            || stored.Reference != reference)
        {
            return Task.FromResult(false);
        }
        var removed = ((ICollection<KeyValuePair<string, StoredObject>>)objects)
            .Remove(new KeyValuePair<string, StoredObject>(reference.ObjectName, stored));
        return Task.FromResult(removed);
    }

    private sealed record StoredObject(byte[] Content, LargeObjectReference Reference);
}
