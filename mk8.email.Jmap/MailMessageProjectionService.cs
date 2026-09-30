using Microsoft.EntityFrameworkCore;
using mk8.email.Application.Interfaces;
using mk8.email.Application.Services;
using mk8.email.Configuration;
using mk8.email.Contracts.Messaging;
using mk8.email.Infrastructure.Data;

namespace mk8.email.Jmap;

internal sealed class MailMessageProjectionService(
    EmailDbContext database,
    JmapAccountService accounts,
    JmapStateService states,
    MailboxMessageContentService content,
    JmapBlobService blobs,
    EnvironmentConfig environment) : IMailMessageProjectionService
{
    public async Task<MailMessageReadResult> ReadAsync(
        MailMessageReadCommand command, AuthenticatedMailUser user,
        CancellationToken cancellationToken)
    {
        var account = await accounts.GetAccountByInboxIdAsync(user, command.AccountId, cancellationToken)
            .ConfigureAwait(false);
        if (account is null)
            return new(MailMessageReadStatus.AccountNotFound, null, []);
        var query = database.Emails.AsNoTracking()
            .Where(email => email.Folder.InboxId == account.InboxId && !email.IsDeleted);
        if (command.MessageIds is not null)
        {
            var requested = command.MessageIds.Distinct().ToArray();
            query = query.Where(email => requested.Contains(email.Id));
        }
        var emails = await query.ToListAsync(cancellationToken).ConfigureAwait(false);
        if (command.MessageIds is null && emails.Count > environment.Jmap.MaxObjectsInGet)
            return new(MailMessageReadStatus.RequestTooLarge, null, []);
        var messages = new List<MailMessageProjectedItem>(emails.Count);
        for (var index = 0; index < emails.Count; index++)
        {
            var email = emails[index];
            var raw = await content.ReadAsync(email, cancellationToken).ConfigureAwait(false);
            using var message = JmapEmailCodec.Parse(raw);
            var value = JmapEmailCodec.Capture(message, email.Id, raw.LongLength, command.IncludeText, email);
            messages.Add(new(email.Id, value));
        }
        var state = await states.GetStateAsync(account.InboxId, JmapConstants.EmailDataType,
            cancellationToken).ConfigureAwait(false);
        return new(MailMessageReadStatus.Ok, state, messages);
    }

    public async Task<MailMessageParseResult> ParseAsync(
        MailMessageParseCommand command, AuthenticatedMailUser user,
        CancellationToken cancellationToken)
    {
        var account = await accounts.GetAccountByInboxIdAsync(user, command.AccountId, cancellationToken)
            .ConfigureAwait(false);
        if (account is null) return new(MailMessageParseStatus.AccountNotFound, []);
        var items = new List<MailMessageParseItem>(command.BlobIds.Count);
        foreach (var blobId in command.BlobIds.Distinct(StringComparer.Ordinal))
        {
            var blob = await blobs.GetAsync(account.InboxId, blobId, cancellationToken).ConfigureAwait(false);
            if (blob is null)
            {
                items.Add(new(blobId, MailMessageParseItemStatus.NotFound, null));
                continue;
            }
            try
            {
                using var message = JmapEmailCodec.Parse(blob.Content);
                var value = JmapEmailCodec.Capture(message, blob.SourceId, blob.Content.LongLength,
                    command.IncludeText, uploadedContentId: blobId, partPrefix: blob.PartPrefix);
                items.Add(new(blobId, MailMessageParseItemStatus.Parsed, value));
            }
            catch (FormatException)
            {
                items.Add(new(blobId, MailMessageParseItemStatus.NotParsable, null));
            }
        }
        return new(MailMessageParseStatus.Ok, items);
    }

}
