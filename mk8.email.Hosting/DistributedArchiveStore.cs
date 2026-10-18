using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace mk8.email.Hosting;

/// <summary>
/// Publishes only signed, encrypted snapshot artifacts to an Azure Blob-compatible
/// store. The small completion index is written last; an incomplete upload is not
/// a recoverable backup and can be retried with the same archive identifier.
/// </summary>
public static partial class DistributedArchiveStore
{
    private const string CipherName = "snapshot.tar.age";
    private const string SignatureName = "snapshot.tar.age.sig";
    private const string IndexName = "complete.json";
    private const int MaximumIndexBytes = 4096;

    // Kept source-compatible, but the retired locator cannot read a key or perform I/O.
    public static Task PublishAsync(
        BlobServiceClient service,
        string containerName,
        string archiveId,
        string archiveDirectory,
        string verificationKeyPath,
        CancellationToken cancellationToken = default) =>
        Task.FromException(new InvalidOperationException("Archive key-file inputs are retired; supply an owned snapshot verifier."));

    /// <summary>Publishes with a borrowed verifier; retain it unchanged and undisposed until this task settles.</summary>
    public static async Task PublishWithVerifierAsync(
        BlobServiceClient service,
        string containerName,
        string archiveId,
        string archiveDirectory,
        RSA verifier,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(service);
        ValidateArchiveId(archiveId);
        RequirePrivateDirectory(archiveDirectory);
        var cipher = Path.Combine(archiveDirectory, CipherName);
        var signature = Path.Combine(archiveDirectory, SignatureName);
        RequirePrivateFile(cipher);
        RequirePrivateFile(signature);
        if (Directory.EnumerateFileSystemEntries(archiveDirectory).Count() != 2)
            throw new InvalidOperationException("The sealed archive contains unexpected entries.");

        RequireVerifier(verifier);
        var cipherDigest = await DigestFileAsync(cipher, cancellationToken).ConfigureAwait(false);
        var signatureDigest = await DigestFileAsync(signature, cancellationToken).ConfigureAwait(false);
        if (cipherDigest.Length <= 0
            || signatureDigest.Length is <= 0 or > 4096)
            throw new InvalidOperationException("The sealed archive size is invalid.");
        var signatureBytes = await File.ReadAllBytesAsync(signature, cancellationToken).ConfigureAwait(false);
        if (!verifier.VerifyHash(cipherDigest.Hash, signatureBytes,
                HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
            throw new CryptographicException("The sealed archive signature is invalid.");

        var index = new ArchiveIndex(
            1, archiveId,
            Convert.ToHexStringLower(cipherDigest.Hash), cipherDigest.Length,
            Convert.ToHexStringLower(signatureDigest.Hash), signatureDigest.Length,
            Convert.ToHexStringLower(SHA256.HashData(verifier.ExportSubjectPublicKeyInfo())));
        var container = GetAdmittedContainer(service, containerName);
        await container.CreateIfNotExistsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        var root = $"distributed-archives/v1/{archiveId}/";
        await PublishFileAsync(container.GetBlobClient(root + CipherName), cipher,
            cipherDigest, cancellationToken).ConfigureAwait(false);
        await PublishFileAsync(container.GetBlobClient(root + SignatureName), signature,
            signatureDigest, cancellationToken).ConfigureAwait(false);

        var indexBytes = JsonSerializer.SerializeToUtf8Bytes(index);
        var indexBlob = container.GetBlobClient(root + IndexName);
        try
        {
            var source = new MemoryStream(indexBytes, writable: false);
            await using (source.ConfigureAwait(false))
            {
                await indexBlob.UploadAsync(source, new BlobUploadOptions
                {
                    Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All },
                    HttpHeaders = new BlobHttpHeaders { ContentType = "application/json" },
                }, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (RequestFailedException exception) when (exception.Status is 409 or 412)
        {
            var existing = await ReadIndexBytesAsync(indexBlob, cancellationToken).ConfigureAwait(false);
            if (!CryptographicOperations.FixedTimeEquals(existing, indexBytes))
                throw new InvalidOperationException(
                    "The archive identifier already has a different completion index.");
        }
        var confirmed = await ReadIndexBytesAsync(indexBlob, cancellationToken).ConfigureAwait(false);
        if (!CryptographicOperations.FixedTimeEquals(confirmed, indexBytes))
            throw new InvalidOperationException("The archive completion index did not round-trip.");
    }

    // Kept source-compatible, but the retired locator cannot read a key or perform I/O.
    public static Task FetchAsync(
        BlobServiceClient service,
        string containerName,
        string archiveId,
        string destinationDirectory,
        string verificationKeyPath,
        CancellationToken cancellationToken = default) =>
        Task.FromException(new InvalidOperationException("Archive key-file inputs are retired; supply an owned snapshot verifier."));

    /// <summary>Fetches with a borrowed verifier; retain it unchanged and undisposed until this task settles.</summary>
    public static async Task FetchWithVerifierAsync(
        BlobServiceClient service,
        string containerName,
        string archiveId,
        string destinationDirectory,
        RSA verifier,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(service);
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("Distributed archive transport requires Linux.");
        ValidateArchiveId(archiveId);
        if (!Path.IsPathFullyQualified(destinationDirectory)
            || !string.Equals(Path.GetFullPath(destinationDirectory), destinationDirectory, StringComparison.Ordinal))
            throw new ArgumentException("An absolute canonical archive destination is required.", nameof(destinationDirectory));
        var parent = Path.GetDirectoryName(destinationDirectory)
            ?? throw new ArgumentException("The archive destination has no parent.", nameof(destinationDirectory));
        RequirePrivateDirectory(parent);
        if (Path.Exists(destinationDirectory))
            throw new IOException("The archive destination already exists.");

        RequireVerifier(verifier);
        var keyHash = Convert.ToHexStringLower(
            SHA256.HashData(verifier.ExportSubjectPublicKeyInfo()));
        var root = $"distributed-archives/v1/{archiveId}/";
        var container = GetAdmittedContainer(service, containerName);
        var indexBytes = await ReadIndexBytesAsync(
            container.GetBlobClient(root + IndexName), cancellationToken).ConfigureAwait(false);
        var index = ParseIndex(indexBytes, archiveId, keyHash);

        var stage = Path.Combine(parent, $".archive-download-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stage, UnixFileMode.UserRead
            | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            await FetchFileAsync(container.GetBlobClient(root + CipherName),
                Path.Combine(stage, CipherName), index.CipherLength,
                index.CipherSha256, cancellationToken).ConfigureAwait(false);
            await FetchFileAsync(container.GetBlobClient(root + SignatureName),
                Path.Combine(stage, SignatureName), index.SignatureLength,
                index.SignatureSha256, cancellationToken).ConfigureAwait(false);
            var cipherHash = Convert.FromHexString(index.CipherSha256);
            var signature = await File.ReadAllBytesAsync(
                Path.Combine(stage, SignatureName), cancellationToken).ConfigureAwait(false);
            if (!verifier.VerifyHash(cipherHash, signature,
                    HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
                throw new CryptographicException("The downloaded archive signature is invalid.");
            Directory.Move(stage, destinationDirectory);
        }
        catch
        {
            if (Directory.Exists(stage))
                Directory.Delete(stage, recursive: true);
            throw;
        }
    }

    private static BlobContainerClient GetAdmittedContainer(BlobServiceClient service, string containerName)
    {
        var container = service.GetBlobContainerClient(containerName);
        var endpoint = container.Uri;
        var loopback = IPAddress.TryParse(endpoint.Host, out var literal) && IPAddress.IsLoopback(literal);
        if (!string.IsNullOrEmpty(endpoint.UserInfo) || !string.IsNullOrEmpty(endpoint.Fragment)
            || (!string.Equals(endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal)
                && !(loopback && string.Equals(endpoint.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal))))
        {
            throw new ArgumentException("Archive transport requires HTTPS or explicit literal loopback HTTP.", nameof(service));
        }
        // Object-client construction reparses this URI. Never publish or fetch
        // an archive when a DNS account path would discard its configured container.
        var address = new BlobUriBuilder(container.Uri, trimBlobNameSlashes: false);
        if (!string.Equals(address.BlobContainerName, containerName, StringComparison.Ordinal)
            || !string.IsNullOrEmpty(address.BlobName))
        {
            throw new ArgumentException(
                "The Azure Blob endpoint must preserve the archive container in object URLs.",
                nameof(service));
        }
        return container;
    }

    public static string ReadPrivateConnectionString(string path) =>
        throw new InvalidOperationException("Separate connection-string files are retired; use the protected startup snapshot.");

    private static async Task PublishFileAsync(
        BlobClient blob, string path, FileDigest expected,
        CancellationToken cancellationToken)
    {
        try
        {
            var source = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using (source.ConfigureAwait(false))
            {
                await blob.UploadAsync(source, new BlobUploadOptions
                {
                    Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All },
                    HttpHeaders = new BlobHttpHeaders
                    {
                        ContentType = "application/octet-stream",
                    },
                }, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (RequestFailedException exception) when (exception.Status is 409 or 412)
        {
            // A retry may find this immutable object already present. Read it back below.
        }

        var actual = await DigestBlobAsync(blob, cancellationToken).ConfigureAwait(false);
        if (actual.Length != expected.Length
            || !CryptographicOperations.FixedTimeEquals(actual.Hash, expected.Hash))
            throw new InvalidOperationException(
                "The uploaded Azure Blob object does not match the signed local artifact.");
    }

    private static async Task FetchFileAsync(
        BlobClient blob, string path, long expectedLength, string expectedSha256,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("Distributed archive transport requires Linux.");
        var properties = (await blob.GetPropertiesAsync(
            cancellationToken: cancellationToken).ConfigureAwait(false)).Value;
        if (properties.ContentLength != expectedLength)
            throw new InvalidOperationException("The remote archive length changed.");
        var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write,
                         FileShare.None, 81920, FileOptions.Asynchronous);
        await using (file.ConfigureAwait(false))
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            await blob.DownloadToAsync(file, new BlobDownloadToOptions
            {
                Conditions = new BlobRequestConditions { IfMatch = properties.ETag },
            }, cancellationToken).ConfigureAwait(false);
            // Flush(true) is the synchronous fsync durability barrier; FlushAsync cannot replace it.
#pragma warning disable CA1849
            file.Flush(flushToDisk: true);
#pragma warning restore CA1849
        }
        var actual = await DigestFileAsync(path, cancellationToken).ConfigureAwait(false);
        if (actual.Length != expectedLength
            || !CryptographicOperations.FixedTimeEquals(
                actual.Hash, Convert.FromHexString(expectedSha256)))
            throw new InvalidOperationException("The downloaded archive failed SHA-256 verification.");
    }

    private static async Task<FileDigest> DigestBlobAsync(
        BlobClient blob, CancellationToken cancellationToken)
    {
        var properties = (await blob.GetPropertiesAsync(
            cancellationToken: cancellationToken).ConfigureAwait(false)).Value;
        using var hash = SHA256.Create();
        var sink = new CryptoStream(Stream.Null, hash,
            CryptoStreamMode.Write, leaveOpen: true);
        await using (sink.ConfigureAwait(false))
        {
            await blob.DownloadToAsync(sink, new BlobDownloadToOptions
            {
                Conditions = new BlobRequestConditions { IfMatch = properties.ETag },
            }, cancellationToken).ConfigureAwait(false);
            await sink.FlushFinalBlockAsync(cancellationToken).ConfigureAwait(false);
            return new FileDigest(hash.Hash!, properties.ContentLength);
        }
    }

    private static async Task<FileDigest> DigestFileAsync(
        string path, CancellationToken cancellationToken)
    {
        var source = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using (source.ConfigureAwait(false))
        {
            var length = source.Length;
            var hash = await SHA256.HashDataAsync(source, cancellationToken).ConfigureAwait(false);
            return new FileDigest(hash, length);
        }
    }

    private static async Task<byte[]> ReadIndexBytesAsync(
        BlobClient blob, CancellationToken cancellationToken)
    {
        var properties = (await blob.GetPropertiesAsync(
            cancellationToken: cancellationToken).ConfigureAwait(false)).Value;
        if (properties.ContentLength is <= 0 or > MaximumIndexBytes)
            throw new InvalidOperationException("The archive completion index is invalid.");
        // Capacity alone is not a limit: use a non-expandable buffer so a
        // changed/malformed GET cannot grow past the admitted HEAD length.
        var target = new MemoryStream(new byte[(int)properties.ContentLength], writable: true);
        await using (target.ConfigureAwait(false))
        {
            target.SetLength(0);
            await blob.DownloadToAsync(target, new BlobDownloadToOptions
            {
                Conditions = new BlobRequestConditions { IfMatch = properties.ETag },
            }, cancellationToken).ConfigureAwait(false);
            if (target.Length != properties.ContentLength)
                throw new InvalidOperationException("The archive completion index length changed.");
            return target.ToArray();
        }
    }

    private static ArchiveIndex ParseIndex(byte[] bytes, string archiveId, string keyHash)
    {
        using var document = JsonDocument.Parse(bytes);
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || document.RootElement.EnumerateObject().Count() != 7)
            throw new InvalidOperationException("The archive completion index is malformed.");
        var index = JsonSerializer.Deserialize<ArchiveIndex>(bytes)
            ?? throw new InvalidOperationException("The archive completion index is missing.");
        if (index.SchemaVersion != 1
            || !string.Equals(index.ArchiveId, archiveId, StringComparison.Ordinal)
            || !string.Equals(index.VerificationKeySha256, keyHash, StringComparison.Ordinal)
            || index.CipherLength <= 0
            || index.SignatureLength is <= 0 or > 4096
            || !Sha256Pattern().IsMatch(index.CipherSha256 ?? "")
            || !Sha256Pattern().IsMatch(index.SignatureSha256 ?? ""))
            throw new InvalidOperationException("The archive completion index is invalid.");
        return index;
    }

    private static void RequireVerifier(RSA verifier)
    {
        ArgumentNullException.ThrowIfNull(verifier);
        if (verifier.KeySize < 3072)
            throw new CryptographicException("The archive verification key is too weak.");
    }

    private static void ValidateArchiveId(string archiveId)
    {
        if (archiveId is null || !ArchiveIdPattern().IsMatch(archiveId))
            throw new ArgumentException("The archive identifier is invalid.", nameof(archiveId));
    }

    private static void RequirePrivateDirectory(string path)
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("Distributed archive transport requires Linux.");
        if (!Path.IsPathFullyQualified(path)
            || !string.Equals(Path.GetFullPath(path), path, StringComparison.Ordinal)
            || !Directory.Exists(path)
            || File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)
            || File.GetUnixFileMode(path) != (UnixFileMode.UserRead
                | UnixFileMode.UserWrite | UnixFileMode.UserExecute)
            || !string.Equals(RunUtility("/usr/bin/realpath", ["-e", "--", path]), path, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "The archive directory must be canonical and mode 0700.");
        RequireOwnedPath(path, singleLinked: false);
    }

    private static void RequirePrivateFile(string path)
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("Distributed archive transport requires Linux.");
        RequirePrivateDirectory(Path.GetDirectoryName(path)
            ?? throw new ArgumentException("The private file has no parent.", nameof(path)));
        if (!File.Exists(path)
            || File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)
            || File.GetUnixFileMode(path) != (UnixFileMode.UserRead | UnixFileMode.UserWrite))
            throw new InvalidOperationException("The archive file must be regular and mode 0600.");
        RequireOwnedPath(path, singleLinked: true);
    }

    private static void RequireOwnedPath(string path, bool singleLinked)
    {
        var fields = RunUtility("/usr/bin/stat", ["-c", "%u %h", "--", path])
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var currentUser = RunUtility("/usr/bin/id", ["-u"]);
        if (fields.Length != 2
            || !uint.TryParse(fields[0], CultureInfo.InvariantCulture, out var owner)
            || !uint.TryParse(currentUser, CultureInfo.InvariantCulture, out var userId)
            || owner != userId
            || !uint.TryParse(fields[1], CultureInfo.InvariantCulture, out var links)
            || singleLinked && links != 1)
            throw new InvalidOperationException(
                "Archive inputs must be caller-owned and private files must be single-linked.");
    }

    private static string RunUtility(string executable, IReadOnlyList<string> arguments)
    {
        var start = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("The archive path verifier could not start.");
        var output = process.StandardOutput.ReadToEnd();
        _ = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException("The archive path verifier failed.");
        return output.TrimEnd('\r', '\n');
    }

    private sealed record FileDigest(byte[] Hash, long Length);

    private sealed record ArchiveIndex(
        int SchemaVersion,
        string ArchiveId,
        string CipherSha256,
        long CipherLength,
        string SignatureSha256,
        long SignatureLength,
        string VerificationKeySha256);

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,126}[a-z0-9]$", RegexOptions.None, 100)]
    private static partial Regex ArchiveIdPattern();

    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.None, 100)]
    private static partial Regex Sha256Pattern();

}
