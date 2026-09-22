using System.Globalization;
using System.Text.RegularExpressions;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using mk8.email.Contracts.Storage;

namespace mk8.email.Storage;

public sealed partial class AzureBlobLargeObjectStore : ILargeObjectStore
{
    private const string HashMetadataKey = "mk8sha256";
    private readonly BlobContainerClient _container;
    private readonly AzureBlobLargeObjectStoreOptions _options;

    public AzureBlobLargeObjectStore(
        BlobServiceClient serviceClient,
        AzureBlobLargeObjectStoreOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(serviceClient);
        _options = options ?? new AzureBlobLargeObjectStoreOptions();
        _options.Validate();
        _container = serviceClient.GetBlobContainerClient(_options.ContainerName);
    }

    public string Provider => LargeObjectProviders.AzureBlob;

    public async Task<LargeObjectWriteResult> PutIfAbsentAsync(
        string objectName,
        Stream content,
        long length,
        string sha256,
        string contentType,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        ValidateObjectName(objectName);
        ValidateLengthAndHash(length, sha256);
        if (!content.CanRead)
            throw new ArgumentException("The large-object content stream must be readable.", nameof(content));
        if (string.IsNullOrWhiteSpace(contentType)
            || contentType.Length > 255
            || contentType.Any(char.IsControl))
        {
            throw new ArgumentException("The large-object content type is invalid.", nameof(contentType));
        }

        if (_options.CreateContainerIfMissing)
            await _container.CreateIfNotExistsAsync(cancellationToken: cancellationToken);

        var blob = _container.GetBlobClient(BuildBlobName(objectName));
        try
        {
            var response = await blob.UploadAsync(
                content,
                new BlobUploadOptions
                {
                    Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All },
                    HttpHeaders = new BlobHttpHeaders { ContentType = contentType },
                    Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        [HashMetadataKey] = sha256,
                        ["mk8length"] = length.ToString(CultureInfo.InvariantCulture),
                    },
                },
                cancellationToken);
            return new LargeObjectWriteResult(
                new LargeObjectReference(
                    Provider,
                    objectName,
                    length,
                    sha256,
                    response.Value.ETag.ToString()),
                Created: true);
        }
        catch (RequestFailedException exception) when (exception.Status is 409 or 412)
        {
            var properties = (await blob.GetPropertiesAsync(cancellationToken: cancellationToken)).Value;
            VerifyProperties(properties, length, sha256);
            return new LargeObjectWriteResult(
                new LargeObjectReference(
                    Provider,
                    objectName,
                    length,
                    sha256,
                    properties.ETag.ToString()),
                Created: false);
        }
    }

    public async Task CopyToAsync(
        LargeObjectReference reference,
        Stream destination,
        CancellationToken cancellationToken = default)
    {
        ValidateReference(reference);
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite)
            throw new ArgumentException("The large-object destination stream must be writable.", nameof(destination));

        var blob = _container.GetBlobClient(BuildBlobName(reference.ObjectName));
        var properties = (await blob.GetPropertiesAsync(
            conditions: new BlobRequestConditions { IfMatch = new ETag(reference.EntityTag) },
            cancellationToken: cancellationToken)).Value;
        VerifyProperties(properties, reference.Length, reference.Sha256);
        await blob.DownloadToAsync(
            destination,
            new BlobDownloadToOptions
            {
                Conditions = new BlobRequestConditions { IfMatch = new ETag(reference.EntityTag) },
            },
            cancellationToken);
    }

    public async Task<bool> DeleteIfMatchAsync(
        LargeObjectReference reference,
        CancellationToken cancellationToken = default)
    {
        ValidateReference(reference);
        var blob = _container.GetBlobClient(BuildBlobName(reference.ObjectName));
        try
        {
            var response = await blob.DeleteIfExistsAsync(
                DeleteSnapshotsOption.IncludeSnapshots,
                new BlobRequestConditions { IfMatch = new ETag(reference.EntityTag) },
                cancellationToken);
            return response.Value;
        }
        catch (RequestFailedException exception) when (exception.Status is 404 or 412)
        {
            return false;
        }
    }

    private string BuildBlobName(string objectName) =>
        string.IsNullOrEmpty(_options.ObjectPrefix)
            ? objectName
            : $"{_options.ObjectPrefix.TrimEnd('/')}/{objectName}";

    private void ValidateReference(LargeObjectReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        if (!string.Equals(reference.Provider, Provider, StringComparison.Ordinal))
            throw new ArgumentException("The large-object provider does not match this store.", nameof(reference));
        ValidateObjectName(reference.ObjectName);
        ValidateLengthAndHash(reference.Length, reference.Sha256);
        if (string.IsNullOrWhiteSpace(reference.EntityTag)
            || reference.EntityTag.Length > 256
            || reference.EntityTag.Contains('\0'))
        {
            throw new ArgumentException("The large-object entity tag is invalid.", nameof(reference));
        }
    }

    private static void ValidateObjectName(string objectName)
    {
        if (string.IsNullOrWhiteSpace(objectName)
            || objectName.Length > 1024
            || objectName.StartsWith("/", StringComparison.Ordinal)
            || objectName.EndsWith("/", StringComparison.Ordinal)
            || objectName.Contains("//", StringComparison.Ordinal)
            || objectName.Contains('\0'))
        {
            throw new ArgumentException("The large-object name is invalid.", nameof(objectName));
        }
    }

    private static void ValidateLengthAndHash(long length, string sha256)
    {
        if (length < 0)
            throw new ArgumentOutOfRangeException(nameof(length));
        if (sha256 is null || !Sha256Pattern().IsMatch(sha256))
            throw new ArgumentException("The large-object SHA-256 digest is invalid.", nameof(sha256));
    }

    private static void VerifyProperties(BlobProperties properties, long length, string sha256)
    {
        if (properties.ContentLength != length
            || !properties.Metadata.TryGetValue(HashMetadataKey, out var storedHash)
            || !string.Equals(storedHash, sha256, StringComparison.Ordinal)
            || !properties.Metadata.TryGetValue("mk8length", out var storedLength)
            || !long.TryParse(storedLength, CultureInfo.InvariantCulture, out var parsedLength)
            || parsedLength != length)
        {
            throw new InvalidOperationException("The Azure Blob large object failed its integrity check.");
        }
    }

    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Pattern();
}
