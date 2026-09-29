using mk8.email.Application.Interfaces;
using mk8.email.Configuration;
using mk8.email.Contracts.Messaging;

namespace mk8.email.Jmap;

internal sealed class MailBlobCopyService(
    JmapAccountService accounts,
    JmapBlobService blobs,
    EnvironmentConfig environment) : IMailBlobCopyService
{
    public async Task<MailBlobCopyResult> CopyAsync(
        MailBlobCopyCommand command,
        AuthenticatedMailUser user,
        CancellationToken cancellationToken)
    {
        if (command.BlobIds is null)
            throw new InvalidOperationException("The blob copy command omitted its object IDs.");
        if (command.BlobIds.Count > environment.Jmap.MaxObjectsInSet)
            return new(MailBlobCopyStatus.RequestTooLarge, []);

        var sourceAccount = await accounts.GetAccountByInboxIdAsync(user, command.FromAccountId, cancellationToken)
            .ConfigureAwait(false);
        if (sourceAccount is null)
            return new(MailBlobCopyStatus.FromAccountNotFound, []);
        var targetAccount = await accounts.GetAccountByInboxIdAsync(user, command.AccountId, cancellationToken)
            .ConfigureAwait(false);
        if (targetAccount is null)
            return new(MailBlobCopyStatus.AccountNotFound, []);

        var results = new List<MailBlobCopyItemResult>();
        foreach (var blobId in command.BlobIds.Distinct(StringComparer.Ordinal))
        {
            var source = await blobs.GetAsync(sourceAccount.InboxId, blobId, cancellationToken).ConfigureAwait(false);
            if (source is null)
            {
                results.Add(new(blobId, MailBlobCopyItemStatus.NotFound, null));
                continue;
            }
            if (source.Content.LongLength > environment.Jmap.MaxUploadSizeBytes)
            {
                results.Add(new(blobId, MailBlobCopyItemStatus.TooLarge, null));
                continue;
            }
            var stored = await blobs.StoreAsync(targetAccount.InboxId, source.Content,
                source.ContentType, source.Name, cancellationToken).ConfigureAwait(false);
            results.Add(new(blobId, MailBlobCopyItemStatus.Copied, stored.BlobId));
        }
        return new(MailBlobCopyStatus.Ok, results);
    }
}
