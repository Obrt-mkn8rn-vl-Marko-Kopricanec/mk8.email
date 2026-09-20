using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using mk8.email.Infrastructure.Data;
using mk8.email.Infrastructure.Environment;
using mk8.email.Infrastructure.Models;

namespace mk8.email.Jmap;

internal sealed record JmapBlobContent(
    byte[] Content,
    string ContentType,
    string? Name);

internal sealed class JmapBlobService(
    EmailDbContext database,
    EnvironmentConfig environment)
{
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> AccountLocks = new();

    public async Task<JmapBlobContent?> GetAsync(
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
                : new JmapBlobContent(uploaded.Content, uploaded.ContentType, uploaded.Name);
        }

        if (JmapId.TryParseRawBlob(blobId, out var emailId))
        {
            var email = await FindEmailAsync(accountId, emailId, cancellationToken);
            return email is null
                ? null
                : new JmapBlobContent(
                    JmapEmailCodec.GetRawBytes(email),
                    "message/rfc822",
                    null);
        }

        if (!JmapId.TryParseBodyPartBlob(blobId, out var sourceId, out var partId))
            return null;

        var sourceEmail = await FindEmailAsync(accountId, sourceId, cancellationToken);
        if (sourceEmail is not null)
        {
            using var message = JmapEmailCodec.Parse(sourceEmail);
            return JmapEmailCodec.TryGetPartContent(
                message,
                partId,
                out var content,
                out var contentType,
                out var name)
                    ? new JmapBlobContent(content, contentType, name)
                    : null;
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
            using var message = JmapEmailCodec.Parse(sourceBlob.Content);
            return JmapEmailCodec.TryGetPartContent(
                message,
                partId,
                out var content,
                out var contentType,
                out var name)
                    ? new JmapBlobContent(content, contentType, name)
                    : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    public async Task<JmapBlobDB> StoreAsync(
        Guid accountId,
        byte[] content,
        string contentType,
        string? name,
        CancellationToken cancellationToken)
    {
        var accountLock = AccountLocks.GetOrAdd(accountId, static _ => new SemaphoreSlim(1, 1));
        await accountLock.WaitAsync(cancellationToken);
        try
        {
            return await StoreLockedAsync(
                accountId,
                content,
                contentType,
                name,
                cancellationToken);
        }
        finally
        {
            accountLock.Release();
        }
    }

    private async Task<JmapBlobDB> StoreLockedAsync(
        Guid accountId,
        byte[] content,
        string contentType,
        string? name,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var expired = await database.JmapBlobs
            .Where(blob => blob.ExpiresAt <= now)
            .ToListAsync(cancellationToken);
        if (expired.Count > 0)
            database.JmapBlobs.RemoveRange(expired);
        var accountBlobs = await database.JmapBlobs
            .Where(blob => blob.AccountId == accountId && blob.ExpiresAt > now)
            .OrderBy(blob => blob.CreatedAt)
            .ThenBy(blob => blob.Id)
            .ToListAsync(cancellationToken);
        var usedBytes = accountBlobs.Sum(blob => blob.SizeBytes);
        foreach (var existing in accountBlobs)
        {
            if (usedBytes + content.LongLength
                <= environment.Jmap.MaxUnreferencedBlobBytesPerAccount)
            {
                break;
            }
            database.JmapBlobs.Remove(existing);
            usedBytes -= existing.SizeBytes;
        }
        var id = Guid.CreateVersion7();
        var blob = new JmapBlobDB
        {
            Id = id,
            BlobId = JmapId.UploadedBlob(id),
            AccountId = accountId,
            Content = content,
            ContentType = contentType,
            Name = name,
            SizeBytes = content.LongLength,
            CreatedAt = now,
            ExpiresAt = now.AddHours(environment.Jmap.UploadRetentionHours),
        };
        database.JmapBlobs.Add(blob);
        await database.SaveChangesAsync(cancellationToken);
        return blob;
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
            || !JmapEmailArguments.TryGetIds(arguments, "blobIds", context, false, out var blobIds)
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
