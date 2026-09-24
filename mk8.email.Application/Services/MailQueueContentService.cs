using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using mk8.email.Application.Protocol;
using mk8.email.Contracts.Storage;
using mk8.email.Infrastructure.Models;

namespace mk8.email.Application.Services;

public sealed class MailQueueContentService(
    ILargeObjectStore objects,
    LargeObjectTransactionEffects transactionEffects,
    ILogger<MailQueueContentService> logger)
{
    public async Task SetAsync(
        MailQueueMessageDB message,
        string rawMessage,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(rawMessage);
        EnsureAzureBlobProvider();
        if (message.Id == Guid.Empty)
            throw new InvalidOperationException("The queue message identifier is not valid.");
        if (rawMessage.Any(character => character > byte.MaxValue))
        {
            throw new InvalidOperationException(
                "The queue message is not in the mail wire byte representation.");
        }

        var content = MailWireEncoding.Instance.GetBytes(rawMessage);
        var hash = Convert.ToHexStringLower(SHA256.HashData(content));
        var previous = TryGetReference(message);
        var source = new MemoryStream(content, writable: false);
        await using (source.ConfigureAwait(false))
        {
            var written = await objects.PutIfAbsentAsync(
            BuildObjectName(message.Id),
            source,
            content.LongLength,
            hash,
            "message/rfc822",
            cancellationToken).ConfigureAwait(false);

            ApplyReference(message, written.Reference);
            if (written.Created)
                transactionEffects.DeleteOnRollback(written.Reference);
            if (previous is not null && previous != written.Reference)
                transactionEffects.DeleteOnCommit(previous);
        }
    }

    public async Task<string> ReadAsync(
        MailQueueMessageDB message,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.RawMessage is not null)
        {
            if (message.RawMessage.Any(character => character > byte.MaxValue))
            {
                throw new InvalidOperationException(
                    $"Legacy queue message {message.Id:D} is not a mail wire byte representation.");
            }
            var actualLength = MailWireEncoding.Instance.GetByteCount(message.RawMessage);
            if (message.RawMessageSizeBytes != actualLength)
            {
                throw new InvalidOperationException(
                    $"Queue message {message.Id:D} has inconsistent length metadata.");
            }
            return message.RawMessage;
        }

        EnsureAzureBlobProvider();
        var reference = TryGetReference(message)
            ?? throw new InvalidOperationException(
                $"Queue message {message.Id:D} has no valid storage reference.");
        var destination = new MemoryStream();
        await using (destination.ConfigureAwait(false))
        {
            await objects.CopyToAsync(reference, destination, cancellationToken).ConfigureAwait(false);
            var content = destination.ToArray();
            if (content.LongLength != message.RawMessageSizeBytes)
            {
                throw new InvalidOperationException(
                    $"Queue message {message.Id:D} failed its length check.");
            }
            if (!CryptographicOperations.FixedTimeEquals(
                    SHA256.HashData(content),
                    Convert.FromHexString(reference.Sha256)))
            {
                throw new InvalidOperationException(
                    $"Queue message {message.Id:D} failed its content hash check.");
            }
            return MailWireEncoding.Instance.GetString(content);
        }
    }

    public LargeObjectReference? TryGetReference(MailQueueMessageDB message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.RawMessageObjectProvider is null
            || message.RawMessageObjectName is null
            || message.RawMessageObjectSha256 is null
            || message.RawMessageObjectEntityTag is null)
        {
            return null;
        }
        if (!string.Equals(
                message.RawMessageObjectProvider,
                LargeObjectProviders.AzureBlob,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Queue message {message.Id:D} does not use Azure Blob-compatible storage.");
        }
        return new LargeObjectReference(
            message.RawMessageObjectProvider,
            message.RawMessageObjectName,
            message.RawMessageSizeBytes,
            message.RawMessageObjectSha256,
            message.RawMessageObjectEntityTag);
    }

    public async Task DeleteBestEffortAsync(LargeObjectReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        try
        {
            await objects.DeleteIfMatchAsync(reference, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            ApplicationServiceLog.QueueObjectDeleteFailed(logger, exception, reference.ObjectName);
        }
    }

    public static string BuildObjectName(Guid queueId) =>
        $"mail/queue/{queueId:N}/raw.eml";

    public static void ApplyReference(
        MailQueueMessageDB message,
        LargeObjectReference reference)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(reference);
        if (!string.Equals(reference.Provider, LargeObjectProviders.AzureBlob, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Mail queue content requires the Azure Blob storage provider.");
        }
        message.RawMessage = null;
        message.RawMessageSizeBytes = reference.Length;
        message.RawMessageObjectProvider = reference.Provider;
        message.RawMessageObjectName = reference.ObjectName;
        message.RawMessageObjectSha256 = reference.Sha256;
        message.RawMessageObjectEntityTag = reference.EntityTag;
    }

    private void EnsureAzureBlobProvider()
    {
        if (!string.Equals(objects.Provider, LargeObjectProviders.AzureBlob, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Mail queue content requires an Azure Blob-compatible object store.");
        }
    }
}
