using Microsoft.EntityFrameworkCore;
using System.Text.Json;
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
    public async Task<MailMessageContentResult> ReadContentAsync(
        MailMessageContentCommand command, AuthenticatedMailUser user, CancellationToken cancellationToken)
    {
        var account = await accounts.GetAccountByInboxIdAsync(user, command.AccountId, cancellationToken).ConfigureAwait(false);
        if (account is null) return new(MailMessageReadStatus.AccountNotFound, null, []);
        // The caller's complete invocation holds the gate-first write coordination
        // transaction, so snapshots, content ownership and returned state agree.
        var emails = await database.Emails.AsNoTracking()
            .Where(email => command.MessageIds.Contains(email.Id) && email.Folder.InboxId == account.InboxId && !email.IsDeleted)
            .ToDictionaryAsync(email => email.Id, cancellationToken).ConfigureAwait(false);
        var items = new List<MailMessageContentItem>(command.MessageIds.Count);
        long encodedBytes = GatewayHttpPayloadBudget.MetadataBytes;
        foreach (var id in command.MessageIds)
        {
            MailMessageContentItem item;
            if (!emails.TryGetValue(id, out var email)) item = new(id, MailMessageContentStatus.NotFound, null, default);
            else if (email.SizeBytes <= 0 || email.SizeBytes > command.MaximumBytes || email.SizeBytes > environment.Limits.MaxMessageSizeBytes)
                item = new(id, MailMessageContentStatus.TooLarge, null, default);
            else if (content.TryGetReference(email) is null && email.RawMessage is null)
                item = new(id, MailMessageContentStatus.NotParsable, null, default);
            else
            {
                // Azure downloads verify the reference's declared length and ETag
                // before transfer; ReadAsync also verifies length and SHA-256.
                var raw = await content.ReadAsync(email, cancellationToken).ConfigureAwait(false);
                if (raw.Length > command.MaximumBytes) throw new InvalidOperationException("The native message length is invalid.");
                try
                {
                    using var message = JmapEmailCodec.Parse(raw);
                    item = new(id, MailMessageContentStatus.Ok,
                        JmapEmailCodec.Capture(message, id, raw.Length, command.IncludeText, email), raw);
                }
                catch (FormatException) { item = new(id, MailMessageContentStatus.NotParsable, null, default); }
            }
            var node = JsonSerializer.SerializeToNode(item, JsonSerializerOptions.Web)!;
            encodedBytes += JsonSerializer.SerializeToUtf8Bytes(ApplicationValueCodec.Encode(node), JsonSerializerOptions.Web).Length;
            if (encodedBytes > environment.Messaging.MaxPayloadBytes)
                return new(MailMessageReadStatus.RequestTooLarge, null, []);
            items.Add(item);
        }
        var state = await states.GetStateAsync(account.InboxId, JmapConstants.EmailDataType, cancellationToken).ConfigureAwait(false);
        return new(MailMessageReadStatus.Ok, state, items);
    }

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
