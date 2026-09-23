using System.Diagnostics;
using System.Globalization;
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

    public static async Task PublishAsync(
        BlobServiceClient service,
        string containerName,
        string archiveId,
        string archiveDirectory,
        string verificationKeyPath,
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

        using var verifier = LoadVerifier(verificationKeyPath);
        var cipherDigest = await DigestFileAsync(cipher, cancellationToken);
        var signatureDigest = await DigestFileAsync(signature, cancellationToken);
        if (cipherDigest.Length <= 0
            || signatureDigest.Length is <= 0 or > 4096)
            throw new InvalidOperationException("The sealed archive size is invalid.");
        var signatureBytes = await File.ReadAllBytesAsync(signature, cancellationToken);
        if (!verifier.VerifyHash(cipherDigest.Hash, signatureBytes,
                HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
            throw new CryptographicException("The sealed archive signature is invalid.");

        var index = new ArchiveIndex(
            1, archiveId,
            Convert.ToHexStringLower(cipherDigest.Hash), cipherDigest.Length,
            Convert.ToHexStringLower(signatureDigest.Hash), signatureDigest.Length,
            Convert.ToHexStringLower(SHA256.HashData(verifier.ExportSubjectPublicKeyInfo())));
        var container = service.GetBlobContainerClient(containerName);
        await container.CreateIfNotExistsAsync(cancellationToken: cancellationToken);
        var root = $"distributed-archives/v1/{archiveId}/";
        await PublishFileAsync(container.GetBlobClient(root + CipherName), cipher,
            cipherDigest, cancellationToken);
        await PublishFileAsync(container.GetBlobClient(root + SignatureName), signature,
            signatureDigest, cancellationToken);

        var indexBytes = JsonSerializer.SerializeToUtf8Bytes(index);
        var indexBlob = container.GetBlobClient(root + IndexName);
        try
        {
            await using var source = new MemoryStream(indexBytes, writable: false);
            await indexBlob.UploadAsync(source, new BlobUploadOptions
            {
                Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All },
                HttpHeaders = new BlobHttpHeaders { ContentType = "application/json" },
            }, cancellationToken);
        }
        catch (RequestFailedException exception) when (exception.Status is 409 or 412)
        {
            var existing = await ReadIndexBytesAsync(indexBlob, cancellationToken);
            if (!CryptographicOperations.FixedTimeEquals(existing, indexBytes))
                throw new InvalidOperationException(
                    "The archive identifier already has a different completion index.");
        }
        var confirmed = await ReadIndexBytesAsync(indexBlob, cancellationToken);
        if (!CryptographicOperations.FixedTimeEquals(confirmed, indexBytes))
            throw new InvalidOperationException("The archive completion index did not round-trip.");
    }

    public static async Task FetchAsync(
        BlobServiceClient service,
        string containerName,
        string archiveId,
        string destinationDirectory,
        string verificationKeyPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(service);
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("Distributed archive transport requires Linux.");
        ValidateArchiveId(archiveId);
        if (!Path.IsPathFullyQualified(destinationDirectory)
            || Path.GetFullPath(destinationDirectory) != destinationDirectory)
            throw new ArgumentException("An absolute canonical archive destination is required.");
        var parent = Path.GetDirectoryName(destinationDirectory)
            ?? throw new ArgumentException("The archive destination has no parent.");
        RequirePrivateDirectory(parent);
        if (Path.Exists(destinationDirectory))
            throw new IOException("The archive destination already exists.");

        using var verifier = LoadVerifier(verificationKeyPath);
        var keyHash = Convert.ToHexStringLower(
            SHA256.HashData(verifier.ExportSubjectPublicKeyInfo()));
        var root = $"distributed-archives/v1/{archiveId}/";
        var container = service.GetBlobContainerClient(containerName);
        var indexBytes = await ReadIndexBytesAsync(
            container.GetBlobClient(root + IndexName), cancellationToken);
        var index = ParseIndex(indexBytes, archiveId, keyHash);

        var stage = Path.Combine(parent, $".archive-download-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stage, UnixFileMode.UserRead
            | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            await FetchFileAsync(container.GetBlobClient(root + CipherName),
                Path.Combine(stage, CipherName), index.CipherLength,
                index.CipherSha256, cancellationToken);
            await FetchFileAsync(container.GetBlobClient(root + SignatureName),
                Path.Combine(stage, SignatureName), index.SignatureLength,
                index.SignatureSha256, cancellationToken);
            var cipherHash = Convert.FromHexString(index.CipherSha256);
            var signature = await File.ReadAllBytesAsync(
                Path.Combine(stage, SignatureName), cancellationToken);
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

    public static string ReadPrivateConnectionString(string path)
    {
        RequirePrivateFile(path);
        if (new FileInfo(path).Length > 16 * 1024)
            throw new InvalidOperationException("The Blob connection-string file is too large.");
        var value = File.ReadAllText(path).TrimEnd('\r', '\n');
        if (string.IsNullOrWhiteSpace(value) || value.Contains('\n') || value.Contains('\r'))
            throw new InvalidOperationException("The Blob connection-string file is invalid.");
        return value;
    }

    private static async Task PublishFileAsync(
        BlobClient blob, string path, FileDigest expected,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var source = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await blob.UploadAsync(source, new BlobUploadOptions
            {
                Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All },
                HttpHeaders = new BlobHttpHeaders
                {
                    ContentType = "application/octet-stream",
                },
            }, cancellationToken);
        }
        catch (RequestFailedException exception) when (exception.Status is 409 or 412)
        {
            // A retry may find this immutable object already present. Read it back below.
        }

        var actual = await DigestBlobAsync(blob, cancellationToken);
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
            cancellationToken: cancellationToken)).Value;
        if (properties.ContentLength != expectedLength)
            throw new InvalidOperationException("The remote archive length changed.");
        await using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write,
                         FileShare.None, 81920, FileOptions.Asynchronous))
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            await blob.DownloadToAsync(file, new BlobDownloadToOptions
            {
                Conditions = new BlobRequestConditions { IfMatch = properties.ETag },
            }, cancellationToken);
            file.Flush(flushToDisk: true);
        }
        var actual = await DigestFileAsync(path, cancellationToken);
        if (actual.Length != expectedLength
            || !CryptographicOperations.FixedTimeEquals(
                actual.Hash, Convert.FromHexString(expectedSha256)))
            throw new InvalidOperationException("The downloaded archive failed SHA-256 verification.");
    }

    private static async Task<FileDigest> DigestBlobAsync(
        BlobClient blob, CancellationToken cancellationToken)
    {
        var properties = (await blob.GetPropertiesAsync(
            cancellationToken: cancellationToken)).Value;
        using var hash = SHA256.Create();
        await using var sink = new CryptoStream(Stream.Null, hash,
            CryptoStreamMode.Write, leaveOpen: true);
        await blob.DownloadToAsync(sink, new BlobDownloadToOptions
        {
            Conditions = new BlobRequestConditions { IfMatch = properties.ETag },
        }, cancellationToken);
        sink.FlushFinalBlock();
        return new FileDigest(hash.Hash!, properties.ContentLength);
    }

    private static async Task<FileDigest> DigestFileAsync(
        string path, CancellationToken cancellationToken)
    {
        await using var source = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var length = source.Length;
        var hash = await SHA256.HashDataAsync(source, cancellationToken);
        return new FileDigest(hash, length);
    }

    private static async Task<byte[]> ReadIndexBytesAsync(
        BlobClient blob, CancellationToken cancellationToken)
    {
        var properties = (await blob.GetPropertiesAsync(
            cancellationToken: cancellationToken)).Value;
        if (properties.ContentLength is <= 0 or > MaximumIndexBytes)
            throw new InvalidOperationException("The archive completion index is invalid.");
        await using var target = new MemoryStream((int)properties.ContentLength);
        await blob.DownloadToAsync(target, new BlobDownloadToOptions
        {
            Conditions = new BlobRequestConditions { IfMatch = properties.ETag },
        }, cancellationToken);
        return target.ToArray();
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
            || index.ArchiveId != archiveId
            || index.VerificationKeySha256 != keyHash
            || index.CipherLength <= 0
            || index.SignatureLength is <= 0 or > 4096
            || !Sha256Pattern().IsMatch(index.CipherSha256 ?? "")
            || !Sha256Pattern().IsMatch(index.SignatureSha256 ?? ""))
            throw new InvalidOperationException("The archive completion index is invalid.");
        return index;
    }

    private static RSA LoadVerifier(string path)
    {
        RequirePrivateFile(path);
        if (new FileInfo(path).Length > 64 * 1024)
            throw new CryptographicException("The archive verification key is too large.");
        var key = RSA.Create();
        try
        {
            key.ImportFromPem(File.ReadAllText(path));
            if (key.KeySize < 3072)
                throw new CryptographicException("The archive verification key is too weak.");
            return key;
        }
        catch
        {
            key.Dispose();
            throw;
        }
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
            || Path.GetFullPath(path) != path
            || !Directory.Exists(path)
            || File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)
            || File.GetUnixFileMode(path) != (UnixFileMode.UserRead
                | UnixFileMode.UserWrite | UnixFileMode.UserExecute)
            || RunUtility("/usr/bin/realpath", ["-e", "--", path]) != path)
            throw new InvalidOperationException(
                "The archive directory must be canonical and mode 0700.");
        RequireOwnedPath(path, singleLinked: false);
    }

    private static void RequirePrivateFile(string path)
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("Distributed archive transport requires Linux.");
        RequirePrivateDirectory(Path.GetDirectoryName(path)
            ?? throw new ArgumentException("The private file has no parent."));
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

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,126}[a-z0-9]$")]
    private static partial Regex ArchiveIdPattern();

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex Sha256Pattern();

}
