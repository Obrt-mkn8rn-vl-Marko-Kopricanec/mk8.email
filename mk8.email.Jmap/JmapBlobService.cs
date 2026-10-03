using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using mk8.email.Application.Services;
using mk8.email.Configuration;
using mk8.email.Contracts.Storage;
using mk8.email.Infrastructure.Data;
using mk8.email.Infrastructure.Models;

namespace mk8.email.Jmap;

public sealed class JmapBlobService(
    EmailDbContext database,
    EnvironmentConfig environment,
    ILargeObjectStore objects,
    LargeObjectTransactionEffects transactionEffects,
    MailboxMessageContentService mailboxContent,
    ILogger<JmapBlobService> logger)
{
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> AccountLocks = new();
    private static readonly Action<ILogger, Exception?> RollbackWarning = LoggerMessage.Define(
        LogLevel.Warning, new EventId(1210, "BlobRollback"), "Could not roll back a failed JMAP blob transaction");
    private static readonly Action<ILogger, Exception?> DisposeWarning = LoggerMessage.Define(
        LogLevel.Warning, new EventId(1211, "BlobDispose"), "Could not dispose a completed JMAP blob transaction");
    private static readonly Action<ILogger, string, Exception?> DeleteWarning = LoggerMessage.Define<string>(
        LogLevel.Warning, new EventId(1212, "BlobDelete"), "Could not delete unreferenced JMAP object {ObjectName}");

    internal async Task<JmapBlobContent?> GetAsync(
        Guid accountId,
        string blobId,
        CancellationToken cancellationToken)
    {
        if (JmapId.TryParseUploadedBlob(blobId, out var uploadedId))
        {
            var uploaded = await database.JmapBlobs
                .AsNoTracking()
                .SingleOrDefaultAsync(blob => blob.Id == uploadedId
                    && blob.AccountId == accountId
                    && blob.ExpiresAt > DateTime.UtcNow,
                    cancellationToken).ConfigureAwait(false);
            return uploaded is null
                ? null
                : new JmapBlobContent(
                    await ReadContentAsync(uploaded, cancellationToken).ConfigureAwait(false),
                    uploaded.ContentType,
                    uploaded.Name,
                    uploaded.Id,
                    null);
        }

        if (JmapId.TryParseRawBlob(blobId, out var emailId))
        {
            var email = await FindEmailAsync(accountId, emailId, cancellationToken).ConfigureAwait(false);
            return email is null
                ? null
                : new JmapBlobContent(
                    await mailboxContent.ReadAsync(email, cancellationToken).ConfigureAwait(false),
                    "message/rfc822",
                    null,
                    emailId,
                    null);
        }

        var isPath = JmapId.TryParseBodyPartBlob(blobId, out var sourceId, out var partId);
        var nestingDepth = 0;
        byte[] pathHash = [];
        var isHash = !isPath && JmapId.TryParseHashedBodyPartBlob(
            blobId,
            out sourceId,
            out nestingDepth,
            out pathHash);
        if (!isPath && !isHash)
            return null;

        return await GetBodyPartAsync(accountId, sourceId, isPath ? partId : null, isHash ? pathHash : null,
            nestingDepth, cancellationToken).ConfigureAwait(false);
    }

    private async Task<JmapBlobContent?> GetBodyPartAsync(Guid accountId, Guid sourceId, string? partId,
        byte[]? pathHash, int nestingDepth, CancellationToken cancellationToken)
    {
        var sourceEmail = await FindEmailAsync(accountId, sourceId, cancellationToken).ConfigureAwait(false);
        if (sourceEmail is not null)
        {
            var rawMessage = await mailboxContent.ReadAsync(sourceEmail, cancellationToken).ConfigureAwait(false);
            using var message = JmapEmailCodec.Parse(rawMessage);
            return ResolveBodyPart(
                message,
                sourceId,
                partId,
                pathHash,
                nestingDepth);
        }

        var sourceBlob = await database.JmapBlobs
            .AsNoTracking()
            .SingleOrDefaultAsync(blob => blob.Id == sourceId
                && blob.AccountId == accountId
                && blob.ExpiresAt > DateTime.UtcNow,
                cancellationToken).ConfigureAwait(false);
        if (sourceBlob is null)
            return null;
        try
        {
            var sourceContent = await ReadContentAsync(sourceBlob, cancellationToken).ConfigureAwait(false);
            using var message = JmapEmailCodec.Parse(sourceContent);
            return ResolveBodyPart(
                message,
                sourceId,
                partId,
                pathHash,
                nestingDepth);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static JmapBlobContent? ResolveBodyPart(
        MimeKit.MimeMessage message,
        Guid sourceId,
        string? partId,
        byte[]? pathHash,
        int nestingDepth)
    {
        if (partId is not null)
        {
            return JmapEmailCodec.TryGetPartContent(
                message,
                partId,
                out var content,
                out var contentType,
                out var name)
                    ? new JmapBlobContent(content, contentType, name, sourceId, partId)
                    : null;
        }
        return pathHash is not null
            && JmapEmailCodec.TryGetPartContentByHash(
                message,
                pathHash,
                nestingDepth,
                out var resolvedPartId,
                out var hashedContent,
                out var hashedContentType,
                out var hashedName)
                ? new JmapBlobContent(
                    hashedContent,
                    hashedContentType,
                    hashedName,
                    sourceId,
                    resolvedPartId)
                : null;
    }

    internal async Task<JmapBlobDB> StoreAsync(
        Guid accountId,
        byte[] content,
        string contentType,
        string? name,
        CancellationToken cancellationToken)
    {
        SemaphoreSlim? accountLock = null;
        if (!IsPostgreSql())
        {
            accountLock = AccountLocks.GetOrAdd(accountId, static _ => new SemaphoreSlim(1, 1));
            await accountLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        try
        {
            var hasCallerTransaction = database.Database.CurrentTransaction is not null;
            var id = Guid.CreateVersion7();
            var hash = Convert.ToHexStringLower(SHA256.HashData(content));
            var source = new MemoryStream(content, writable: false);
            await using var sourceLifetime = source.ConfigureAwait(false);
            var written = await objects.PutIfAbsentAsync(
                JmapBlobLargeObjectMigrationService.BuildObjectName(accountId, id),
                source,
                content.LongLength,
                hash,
                contentType,
                cancellationToken).ConfigureAwait(false);
            try
            {
                var stored = await StoreLockedAsync(
                    id,
                    written.Reference,
                    accountId,
                    contentType,
                    name,
                    hasCallerTransaction,
                    cancellationToken).ConfigureAwait(false);
                if (hasCallerTransaction && written.Created)
                    transactionEffects.DeleteOnRollback(written.Reference);
                return stored;
            }
            catch (JmapBlobCommitOutcomeUnknownException)
            {
                throw;
            }
            catch
            {
                if (written.Created)
                    await DeleteBestEffortAsync(written.Reference).ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            accountLock?.Release();
        }
    }

    private async Task<JmapBlobDB> StoreLockedAsync(
        Guid id,
        LargeObjectReference reference,
        Guid accountId,
        string contentType,
        string? name,
        bool hasCallerTransaction,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(objects.Provider, LargeObjectProviders.AzureBlob, StringComparison.Ordinal)
            || !string.Equals(reference.Provider, LargeObjectProviders.AzureBlob, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "JMAP blobs require an Azure Blob-compatible object store.");
        }

        IDbContextTransaction? ownedTransaction = null;
        if (database.Database.IsRelational() && database.Database.CurrentTransaction is null)
            ownedTransaction = await database.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var removedReferences = new List<LargeObjectReference>();
        var commitAttempted = false;
        try
        {
            await AcquireAccountLockAsync(accountId, cancellationToken).ConfigureAwait(false);
            var now = DateTime.UtcNow;
            removedReferences = await PruneBlobsAsync(accountId, reference.Length, now, cancellationToken)
                .ConfigureAwait(false);
            var blob = CreateBlob(id, reference, accountId, contentType, name, now);
            await database.JmapBlobs.AddAsync(blob, cancellationToken).ConfigureAwait(false);
            await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            if (ownedTransaction is not null)
            {
                commitAttempted = true;
                await ownedTransaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }

            if (hasCallerTransaction)
            {
                for (var referenceIndex = 0; referenceIndex < removedReferences.Count; referenceIndex++)
                    transactionEffects.DeleteOnCommit(removedReferences[referenceIndex]);
            }
            else
            {
                for (var referenceIndex = 0; referenceIndex < removedReferences.Count; referenceIndex++)
                    await DeleteBestEffortAsync(removedReferences[referenceIndex]).ConfigureAwait(false);
            }
            return blob;
        }
        catch (Exception exception)
        {
            await RollbackBestEffortAsync(ownedTransaction).ConfigureAwait(false);
            if (commitAttempted)
                throw new JmapBlobCommitOutcomeUnknownException(exception);
            throw;
        }
        finally
        {
            await DisposeBestEffortAsync(ownedTransaction).ConfigureAwait(false);
        }
    }

    private async Task<List<LargeObjectReference>> PruneBlobsAsync(
        Guid accountId, long incomingLength, DateTime now, CancellationToken cancellationToken)
    {
        var removedReferences = new List<LargeObjectReference>();
        var expired = await database.JmapBlobs
            .Where(blob => blob.AccountId == accountId && blob.ExpiresAt <= now)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (expired.Count > 0)
        {
            removedReferences.AddRange(expired
                .Select(TryGetReference)
                .Where(candidate => candidate is not null)
                .Select(candidate => candidate!));
            database.JmapBlobs.RemoveRange(expired);
        }
        var accountBlobs = await database.JmapBlobs
            .Where(blob => blob.AccountId == accountId && blob.ExpiresAt > now)
            .OrderBy(blob => blob.CreatedAt)
            .ThenBy(blob => blob.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var usedBytes = accountBlobs.Sum(blob => blob.SizeBytes);
        for (var blobIndex = 0; blobIndex < accountBlobs.Count; blobIndex++)
        {
            var existing = accountBlobs[blobIndex];
            if (usedBytes + incomingLength
                <= environment.Jmap.MaxUnreferencedBlobBytesPerAccount)
            {
                break;
            }
            var removed = TryGetReference(existing);
            if (removed is not null)
                removedReferences.Add(removed);
            database.JmapBlobs.Remove(existing);
            usedBytes -= existing.SizeBytes;
        }
        return removedReferences;
    }

    private JmapBlobDB CreateBlob(Guid id, LargeObjectReference reference, Guid accountId,
        string contentType, string? name, DateTime now)
    {
        var blob = new JmapBlobDB
        {
            Id = id,
            BlobId = JmapId.UploadedBlob(id),
            AccountId = accountId,
            ContentType = contentType,
            Name = name,
            SizeBytes = reference.Length,
            CreatedAt = now,
            ExpiresAt = now.AddHours(environment.Jmap.UploadRetentionHours),
        };
        JmapBlobLargeObjectMigrationService.ApplyReference(blob, reference);
        return blob;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031",
        Justification = "Compensating rollback must not mask the original write or ambiguous-commit failure.")]
    private async Task RollbackBestEffortAsync(IDbContextTransaction? transaction)
    {
        if (transaction is null) return;
        try { await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false); }
        catch (Exception exception) { RollbackWarning(logger, exception); }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031",
        Justification = "Disposal of an already completed transaction must not alter its recorded outcome.")]
    private async Task DisposeBestEffortAsync(IDbContextTransaction? transaction)
    {
        if (transaction is null) return;
        try { await transaction.DisposeAsync().ConfigureAwait(false); }
        catch (Exception exception) { DisposeWarning(logger, exception); }
    }

    private async Task<byte[]> ReadContentAsync(
        JmapBlobDB blob,
        CancellationToken cancellationToken)
    {
        if (blob.Content is not null)
            return blob.Content;
        var reference = TryGetReference(blob)
            ?? throw new InvalidOperationException("The JMAP blob has no valid storage reference.");
        var destination = new MemoryStream();
        await using var destinationLifetime = destination.ConfigureAwait(false);
        await objects.CopyToAsync(reference, destination, cancellationToken).ConfigureAwait(false);
        return destination.ToArray();
    }

    private LargeObjectReference? TryGetReference(JmapBlobDB blob)
    {
        if (blob.ObjectProvider is null
            || blob.ObjectName is null
            || blob.ObjectSha256 is null
            || blob.ObjectEntityTag is null)
        {
            return null;
        }
        if (!string.Equals(blob.ObjectProvider, objects.Provider, StringComparison.Ordinal)
            || !string.Equals(blob.ObjectProvider, LargeObjectProviders.AzureBlob, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The JMAP blob storage provider is not supported.");
        }
        return new LargeObjectReference(
            blob.ObjectProvider,
            blob.ObjectName,
            blob.SizeBytes,
            blob.ObjectSha256,
            blob.ObjectEntityTag);
    }

    private async Task AcquireAccountLockAsync(
        Guid accountId,
        CancellationToken cancellationToken)
    {
        if (!IsPostgreSql())
            return;
        if (database.Database.CurrentTransaction is null)
            throw new InvalidOperationException("The JMAP blob quota lock requires a transaction.");

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes($"jmap-blob-quota:{accountId:D}"));
        var key = BinaryPrimitives.ReadInt64BigEndian(digest);
        await database.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock({key})",
            cancellationToken).ConfigureAwait(false);
    }

    private bool IsPostgreSql() => string.Equals(
        database.Database.ProviderName,
        "Npgsql.EntityFrameworkCore.PostgreSQL",
        StringComparison.Ordinal);

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031",
        Justification = "Post-commit orphan cleanup is best effort; a cleanup failure cannot turn a committed blob into a failed write.")]
    private async Task DeleteBestEffortAsync(LargeObjectReference reference)
    {
        try
        {
            await objects.DeleteIfMatchAsync(reference, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            DeleteWarning(logger, reference.ObjectName, exception);
        }
    }

    private Task<EmailDB?> FindEmailAsync(
        Guid accountId,
        Guid emailId,
        CancellationToken cancellationToken) =>
        database.Emails
            .AsNoTracking()
            .SingleOrDefaultAsync(email => email.Id == emailId
                && email.Folder.InboxId == accountId
                && !email.IsDeleted,
                cancellationToken);

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1032", Justification = "Only an ambiguous database commit with its cause may construct this private sentinel.")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1064", Justification = "This private transaction sentinel must not be exposed as a public exception.")]
    private sealed class JmapBlobCommitOutcomeUnknownException(Exception innerException)
        : Exception("The JMAP blob database commit outcome is unknown.", innerException);
}
