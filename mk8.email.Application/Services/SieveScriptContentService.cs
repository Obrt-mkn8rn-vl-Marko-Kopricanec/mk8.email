using System.Security.Cryptography;
using System.Text;
using mk8.email.Application.Protocol;
using mk8.email.Contracts.Storage;
using mk8.email.Infrastructure.Models;

namespace mk8.email.Application.Services;

public sealed class SieveScriptContentService(
    ILargeObjectStore objects,
    LargeObjectTransactionEffects transactionEffects)
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public async Task SetAsync(
        SieveScriptDB script,
        string content,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(script);
        ArgumentNullException.ThrowIfNull(content);
        EnsureAzureBlobProvider();
        if (script.Id == Guid.Empty)
            throw new InvalidOperationException("The Sieve script identifier is not valid.");

        var bytes = StrictUtf8.GetBytes(content);
        if (bytes.Length is 0 or > SieveScript.MaximumScriptBytes)
            throw new InvalidOperationException("The Sieve script size is invalid.");
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var previous = TryGetReference(script);
        var source = new MemoryStream(bytes, writable: false);
        await using (source.ConfigureAwait(false))
        {
            // A unique write key keeps a failed concurrent update from deleting a
            // committed update's object, even when both scripts contain identical bytes.
            var written = await objects.PutIfAbsentAsync(
            BuildObjectName(script.Id, hash, Guid.CreateVersion7()),
            source,
            bytes.LongLength,
            hash,
            "application/sieve",
            cancellationToken).ConfigureAwait(false);
            ApplyReference(script, written.Reference);
            if (written.Created)
                transactionEffects.DeleteOnRollback(written.Reference);
            if (previous is not null && previous != written.Reference)
                transactionEffects.DeleteOnCommit(previous);
        }
    }

    public async Task<string> ReadAsync(
        SieveScriptDB script,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(script);
        if (script.Content is not null)
        {
            var legacySize = StrictUtf8.GetByteCount(script.Content);
            if (legacySize is 0 or > SieveScript.MaximumScriptBytes)
                throw new InvalidOperationException($"Sieve script {script.Id:D} has an invalid size.");
            if (script.SizeBytes > 0
                && legacySize != script.SizeBytes)
            {
                throw new InvalidOperationException(
                    $"Sieve script {script.Id:D} failed its length check.");
            }
            return script.Content;
        }

        EnsureAzureBlobProvider();
        var reference = TryGetReference(script)
            ?? throw new InvalidOperationException(
                $"Sieve script {script.Id:D} has no valid storage reference.");
        if (script.SizeBytes is <= 0 or > SieveScript.MaximumScriptBytes)
            throw new InvalidOperationException($"Sieve script {script.Id:D} has an invalid size.");
        var destination = new MemoryStream();
        await using (destination.ConfigureAwait(false))
        {
            await objects.CopyToAsync(reference, destination, cancellationToken).ConfigureAwait(false);
            var bytes = destination.ToArray();
            if (bytes.Length != script.SizeBytes
                || !CryptographicOperations.FixedTimeEquals(
                    SHA256.HashData(bytes),
                    Convert.FromHexString(reference.Sha256)))
            {
                throw new InvalidOperationException(
                    $"Sieve script {script.Id:D} failed its content integrity check.");
            }
            return StrictUtf8.GetString(bytes);
        }
    }

    public LargeObjectReference? TryGetReference(SieveScriptDB script)
    {
        ArgumentNullException.ThrowIfNull(script);
        if (script.ObjectProvider is null
            || script.ObjectName is null
            || script.ObjectSha256 is null
            || script.ObjectEntityTag is null)
        {
            return null;
        }
        if (!string.Equals(script.ObjectProvider, LargeObjectProviders.AzureBlob, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Sieve script {script.Id:D} does not use Azure Blob-compatible storage.");
        }
        return new LargeObjectReference(
            script.ObjectProvider,
            script.ObjectName,
            script.SizeBytes,
            script.ObjectSha256,
            script.ObjectEntityTag);
    }

    public void DeleteOnCommit(SieveScriptDB script)
    {
        var reference = TryGetReference(script);
        if (reference is not null)
            transactionEffects.DeleteOnCommit(reference);
    }

    public static string BuildObjectName(Guid scriptId, string sha256, Guid writeId) =>
        $"sieve/scripts/{scriptId:N}/{sha256}/{writeId:N}";

    public static void ApplyReference(
        SieveScriptDB script,
        LargeObjectReference reference)
    {
        if (!string.Equals(reference.Provider, LargeObjectProviders.AzureBlob, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Sieve script content requires the Azure Blob storage provider.");
        }
        script.Content = null;
        script.SizeBytes = checked((int)reference.Length);
        script.ObjectProvider = reference.Provider;
        script.ObjectName = reference.ObjectName;
        script.ObjectSha256 = reference.Sha256;
        script.ObjectEntityTag = reference.EntityTag;
    }

    private void EnsureAzureBlobProvider()
    {
        if (!string.Equals(objects.Provider, LargeObjectProviders.AzureBlob, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Sieve script content requires an Azure Blob-compatible object store.");
        }
    }
}
