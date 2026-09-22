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

internal sealed record JmapBlobContent(
    byte[] Content,
    string ContentType,
    string? Name,
    Guid SourceId,
    string? PartPrefix);

public sealed class JmapBlobService(
    EmailDbContext database,
    EnvironmentConfig environment,
    ILargeObjectStore objects,
    LargeObjectTransactionEffects transactionEffects,
    MailboxMessageContentService mailboxContent,
    ILogger<JmapBlobService> logger)
{
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> AccountLocks = new();

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
                    cancellationToken);
            return uploaded is null
                ? null
                : new JmapBlobContent(
                    await ReadContentAsync(uploaded, cancellationToken),
                    uploaded.ContentType,
                    uploaded.Name,
                    uploaded.Id,
                    null);
        }

        if (JmapId.TryParseRawBlob(blobId, out var emailId))
        {
            var email = await FindEmailAsync(accountId, emailId, cancellationToken);
            return email is null
                ? null
                : new JmapBlobContent(
                    await mailboxContent.ReadAsync(email, cancellationToken),
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

        var sourceEmail = await FindEmailAsync(accountId, sourceId, cancellationToken);
        if (sourceEmail is not null)
        {
            var rawMessage = await mailboxContent.ReadAsync(sourceEmail, cancellationToken);
            using var message = JmapEmailCodec.Parse(rawMessage);
            return ResolveBodyPart(
                message,
                sourceId,
                isPath ? partId : null,
                isHash ? pathHash : null,
                nestingDepth);
        }

        var sourceBlob = await database.JmapBlobs
            .AsNoTracking()
            .SingleOrDefaultAsync(blob => blob.Id == sourceId
                && blob.AccountId == accountId
                && blob.ExpiresAt > DateTime.UtcNow,
                cancellationToken);
        if (sourceBlob is null)
            return null;
        try
        {
            var sourceContent = await ReadContentAsync(sourceBlob, cancellationToken);
            using var message = JmapEmailCodec.Parse(sourceContent);
            return ResolveBodyPart(
                message,
                sourceId,
                isPath ? partId : null,
                isHash ? pathHash : null,
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
            await accountLock.WaitAsync(cancellationToken);
        }
        try
        {
            var hasCallerTransaction = database.Database.CurrentTransaction is not null;
            var id = Guid.CreateVersion7();
            var hash = Convert.ToHexStringLower(SHA256.HashData(content));
            await using var source = new MemoryStream(content, writable: false);
            var written = await objects.PutIfAbsentAsync(
                JmapBlobLargeObjectMigrationService.BuildObjectName(accountId, id),
                source,
                content.LongLength,
                hash,
                contentType,
                cancellationToken);
            try
            {
                var stored = await StoreLockedAsync(
                    id,
                    written.Reference,
                    accountId,
                    contentType,
                    name,
                    hasCallerTransaction,
                    cancellationToken);
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
                    await DeleteBestEffortAsync(written.Reference);
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
            ownedTransaction = await database.Database.BeginTransactionAsync(cancellationToken);
        var removedReferences = new List<LargeObjectReference>();
        var commitAttempted = false;
        try
        {
            await AcquireAccountLockAsync(accountId, cancellationToken);
            var now = DateTime.UtcNow;
            var expired = await database.JmapBlobs
                .Where(blob => blob.AccountId == accountId && blob.ExpiresAt <= now)
                .ToListAsync(cancellationToken);
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
                .ToListAsync(cancellationToken);
            var usedBytes = accountBlobs.Sum(blob => blob.SizeBytes);
            foreach (var existing in accountBlobs)
            {
                if (usedBytes + reference.Length
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
            database.JmapBlobs.Add(blob);
            await database.SaveChangesAsync(cancellationToken);
            if (ownedTransaction is not null)
            {
                commitAttempted = true;
                await ownedTransaction.CommitAsync(cancellationToken);
            }

            if (hasCallerTransaction)
            {
                foreach (var removedReference in removedReferences)
                    transactionEffects.DeleteOnCommit(removedReference);
            }
            else
            {
                foreach (var removedReference in removedReferences)
                    await DeleteBestEffortAsync(removedReference);
            }
            return blob;
        }
        catch (Exception exception)
        {
            if (ownedTransaction is not null)
            {
                try
                {
                    await ownedTransaction.RollbackAsync(CancellationToken.None);
                }
                catch (Exception rollbackException)
                {
                    logger.LogWarning(
                        rollbackException,
                        "Could not roll back a failed JMAP blob transaction");
                }
            }
            if (commitAttempted)
                throw new JmapBlobCommitOutcomeUnknownException(exception);
            throw;
        }
        finally
        {
            if (ownedTransaction is not null)
            {
                try
                {
                    await ownedTransaction.DisposeAsync();
                }
                catch (Exception disposeException)
                {
                    logger.LogWarning(
                        disposeException,
                        "Could not dispose a completed JMAP blob transaction");
                }
            }
        }
    }

    private async Task<byte[]> ReadContentAsync(
        JmapBlobDB blob,
        CancellationToken cancellationToken)
    {
        if (blob.Content is not null)
            return blob.Content;
        var reference = TryGetReference(blob)
            ?? throw new InvalidOperationException("The JMAP blob has no valid storage reference.");
        await using var destination = new MemoryStream();
        await objects.CopyToAsync(reference, destination, cancellationToken);
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
            cancellationToken);
    }

    private bool IsPostgreSql() => string.Equals(
        database.Database.ProviderName,
        "Npgsql.EntityFrameworkCore.PostgreSQL",
        StringComparison.Ordinal);

    private async Task DeleteBestEffortAsync(LargeObjectReference reference)
    {
        try
        {
            await objects.DeleteIfMatchAsync(reference, CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Could not delete unreferenced JMAP object {ObjectName}",
                reference.ObjectName);
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

    private sealed class JmapBlobCommitOutcomeUnknownException(Exception innerException)
        : Exception("The JMAP blob database commit outcome is unknown.", innerException);
}

internal sealed class BlobCopyMethod(
    JmapAccountService accounts,
    JmapBlobService blobs,
    EnvironmentConfig environment) : IJmapMethod
{
    public string Name => "Blob/copy";
    public string Capability => JmapConstants.CoreCapability;

    public async Task<JmapMethodResponse> InvokeAsync(
        JmapInvocationContext context,
        JsonObject arguments,
        CancellationToken cancellationToken)
    {
        if (!JmapMethodHelpers.HasOnlyProperties(arguments, "fromAccountId", "accountId", "blobIds")
            || !JmapMethodHelpers.TryGetRequiredString(arguments, "fromAccountId", out var fromAccountId)
            || !JmapMethodHelpers.TryGetRequiredString(arguments, "accountId", out var accountId)
            || string.Equals(fromAccountId, accountId, StringComparison.Ordinal)
            || !JmapEmailArguments.TryGetIds(arguments, "blobIds", false, out var blobIds)
            || blobIds is null)
        {
            return JmapMethodResponse.Error("invalidArguments");
        }
        if (blobIds.Count > environment.Jmap.MaxObjectsInSet)
            return JmapMethodResponse.Error("requestTooLarge");
        var sourceAccount = await accounts.GetAccountAsync(context.User, fromAccountId, cancellationToken);
        if (sourceAccount is null)
            return JmapMethodResponse.Error("fromAccountNotFound");
        var targetAccount = await accounts.GetAccountAsync(context.User, accountId, cancellationToken);
        if (targetAccount is null)
            return JmapMethodResponse.Error("accountNotFound");

        var copied = new JsonObject();
        var notCopied = new JsonObject();
        foreach (var blobId in blobIds.Distinct(StringComparer.Ordinal))
        {
            var source = await blobs.GetAsync(sourceAccount.InboxId, blobId, cancellationToken);
            if (source is null)
            {
                notCopied[blobId] = JmapMethodHelpers.SetError("notFound");
                continue;
            }
            if (source.Content.LongLength > environment.Jmap.MaxUploadSizeBytes)
            {
                notCopied[blobId] = JmapMethodHelpers.SetError("tooLarge");
                continue;
            }
            var stored = await blobs.StoreAsync(
                targetAccount.InboxId,
                source.Content,
                source.ContentType,
                source.Name,
                cancellationToken);
            copied[blobId] = stored.BlobId;
        }
        return new JmapMethodResponse(Name, new JsonObject
        {
            ["fromAccountId"] = fromAccountId,
            ["accountId"] = accountId,
            ["copied"] = copied.Count == 0 ? null : copied,
            ["notCopied"] = notCopied.Count == 0 ? null : notCopied,
        });
    }
}
