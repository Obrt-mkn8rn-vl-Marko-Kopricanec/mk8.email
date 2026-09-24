using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using mk8.email.Contracts.Storage;
using mk8.email.Infrastructure.Models;

namespace mk8.email.Application.Services;

public sealed class DavResourceContentService(
    ILargeObjectStore objects,
    LargeObjectTransactionEffects transactionEffects,
    ILogger<DavResourceContentService> logger)
{
    public async Task SetAsync(
        DavResourceDB resource,
        byte[] content,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(content);
        EnsureAzureBlobProvider();
        if (resource.Id == Guid.Empty)
            throw new InvalidOperationException("The DAV resource identifier is not valid.");

        var hash = Convert.ToHexStringLower(SHA256.HashData(content));
        var previous = TryGetReference(resource);
        var source = new MemoryStream(content, writable: false);
        await using (source.ConfigureAwait(false))
        {
            var written = await objects.PutIfAbsentAsync(
            BuildObjectName(resource.Id, hash),
            source,
            content.LongLength,
            hash,
            resource.ContentType,
            cancellationToken).ConfigureAwait(false);
            ApplyReference(resource, written.Reference);
            if (written.Created)
                transactionEffects.DeleteOnRollback(written.Reference);
            if (previous is not null && previous != written.Reference)
                transactionEffects.DeleteOnCommit(previous);
        }
    }

    public async Task<byte[]> ReadAsync(
        DavResourceDB resource,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resource);
        byte[] content;
        if (resource.Content is not null)
        {
            content = resource.Content.ToArray();
        }
        else
        {
            EnsureAzureBlobProvider();
            var reference = TryGetReference(resource)
                ?? throw new InvalidOperationException(
                    $"DAV resource {resource.Id:D} has no valid storage reference.");
            var destination = new MemoryStream();
            await using (destination.ConfigureAwait(false))
            {
                await objects.CopyToAsync(reference, destination, cancellationToken).ConfigureAwait(false);
                content = destination.ToArray();
            }
        }

        if (content.LongLength != resource.SizeBytes)
        {
            throw new InvalidOperationException(
                $"DAV resource {resource.Id:D} failed its length check.");
        }
        var hash = SHA256.HashData(content);
        if (!CryptographicOperations.FixedTimeEquals(
                hash,
                Convert.FromHexString(resource.Etag)))
        {
            throw new InvalidOperationException(
                $"DAV resource {resource.Id:D} failed its content hash check.");
        }
        return content;
    }

    public LargeObjectReference? TryGetReference(DavResourceDB resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        if (resource.ObjectProvider is null
            || resource.ObjectName is null
            || resource.ObjectSha256 is null
            || resource.ObjectEntityTag is null)
        {
            return null;
        }
        if (!string.Equals(resource.ObjectProvider, LargeObjectProviders.AzureBlob, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"DAV resource {resource.Id:D} does not use Azure Blob-compatible storage.");
        }
        return new LargeObjectReference(
            resource.ObjectProvider,
            resource.ObjectName,
            resource.SizeBytes,
            resource.ObjectSha256,
            resource.ObjectEntityTag);
    }

    public void DeleteOnCommit(DavResourceDB resource)
    {
        var reference = TryGetReference(resource);
        if (reference is not null)
            transactionEffects.DeleteOnCommit(reference);
    }

    public static string BuildObjectName(Guid resourceId, string sha256) =>
        $"dav/resources/{resourceId:N}/{sha256}";

    public static void ApplyReference(
        DavResourceDB resource,
        LargeObjectReference reference)
    {
        if (!string.Equals(reference.Provider, LargeObjectProviders.AzureBlob, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "DAV resource content requires the Azure Blob storage provider.");
        }
        resource.Content = null;
        resource.SizeBytes = checked((int)reference.Length);
        resource.ObjectProvider = reference.Provider;
        resource.ObjectName = reference.ObjectName;
        resource.ObjectSha256 = reference.Sha256;
        resource.ObjectEntityTag = reference.EntityTag;
    }

    public async Task DeleteBestEffortAsync(LargeObjectReference reference)
    {
        try
        {
            await objects.DeleteIfMatchAsync(reference, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Could not delete unreferenced DAV object {ObjectName}",
                reference.ObjectName);
        }
    }

    private void EnsureAzureBlobProvider()
    {
        if (!string.Equals(objects.Provider, LargeObjectProviders.AzureBlob, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "DAV resource content requires an Azure Blob-compatible object store.");
        }
    }
}
