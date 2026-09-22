using System.Collections.Concurrent;
using System.Security.Cryptography;
using mk8.email.Contracts.Storage;

namespace mk8.email.Messaging.Tests;

internal sealed class InMemoryLargeObjectStore : ILargeObjectStore
{
    private readonly ConcurrentDictionary<string, StoredObject> _objects = new(StringComparer.Ordinal);
    private int _putCount;
    private int _deleteCount;

    public string Provider => LargeObjectProviders.AzureBlob;
    public int PutCount => Volatile.Read(ref _putCount);
    public int DeleteCount => Volatile.Read(ref _deleteCount);
    public int ObjectCount => _objects.Count;

    public async Task<LargeObjectWriteResult> PutIfAbsentAsync(
        string objectName,
        Stream content,
        long length,
        string sha256,
        string contentType,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _putCount);
        await using var destination = new MemoryStream();
        await content.CopyToAsync(destination, cancellationToken);
        var bytes = destination.ToArray();
        var actualHash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        if (bytes.LongLength != length || !string.Equals(actualHash, sha256, StringComparison.Ordinal))
            throw new InvalidOperationException("The test large object is invalid.");
        var candidate = new StoredObject(bytes, sha256, $"\"{sha256}\"", contentType);
        var stored = _objects.GetOrAdd(objectName, candidate);
        if (!ReferenceEquals(candidate, stored)
            && (!stored.Content.AsSpan().SequenceEqual(bytes)
                || !string.Equals(stored.Sha256, sha256, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("A different test object already uses that name.");
        }
        return new LargeObjectWriteResult(
            new LargeObjectReference(Provider, objectName, length, sha256, stored.EntityTag),
            ReferenceEquals(candidate, stored));
    }

    public async Task CopyToAsync(
        LargeObjectReference reference,
        Stream destination,
        CancellationToken cancellationToken = default)
    {
        if (!_objects.TryGetValue(reference.ObjectName, out var stored)
            || !string.Equals(reference.Provider, Provider, StringComparison.Ordinal)
            || !string.Equals(reference.EntityTag, stored.EntityTag, StringComparison.Ordinal)
            || !string.Equals(reference.Sha256, stored.Sha256, StringComparison.Ordinal)
            || reference.Length != stored.Content.LongLength)
        {
            throw new InvalidOperationException("The test large object is unavailable or changed.");
        }
        await destination.WriteAsync(stored.Content, cancellationToken);
    }

    public Task<bool> DeleteIfMatchAsync(
        LargeObjectReference reference,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_objects.TryGetValue(reference.ObjectName, out var stored)
            || !string.Equals(reference.EntityTag, stored.EntityTag, StringComparison.Ordinal))
        {
            return Task.FromResult(false);
        }
        var removed = _objects.TryRemove(reference.ObjectName, out var removedObject)
            && ReferenceEquals(stored, removedObject);
        if (removed)
            Interlocked.Increment(ref _deleteCount);
        return Task.FromResult(removed);
    }

    public void Remove(string objectName) => _objects.TryRemove(objectName, out _);

    public void Corrupt(string objectName)
    {
        if (!_objects.TryGetValue(objectName, out var stored))
            throw new InvalidOperationException("The test large object does not exist.");
        var corrupted = (byte[])stored.Content.Clone();
        corrupted[0] ^= 0xff;
        _objects[objectName] = stored with { Content = corrupted };
    }

    private sealed record StoredObject(
        byte[] Content,
        string Sha256,
        string EntityTag,
        string ContentType);
}
