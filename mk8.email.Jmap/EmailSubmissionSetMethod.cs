using mk8.email.Contracts.Messaging;
using System.Text;
using Microsoft.EntityFrameworkCore;
using MimeKit;
using mk8.email.Application.Interfaces;
using mk8.email.Contracts.Mail;
using mk8.email.Application.Services;
using mk8.email.Infrastructure.Data;
using mk8.email.Configuration;
using mk8.email.Infrastructure.Models;

namespace mk8.email.Jmap;

internal sealed class EmailSubmissionSetMethod(
    EmailDbContext database,
    JmapAccountService accounts,
    JmapIdentityService identities,
    JmapStateService states,
    ISenderAuthorizationService senderAuthorization,
    IEmailService emailService,
    EmailSetMethod emailSet,
    MailQueueContentService queueContent,
    MailboxMessageContentService mailboxContent,
    EnvironmentConfig environment) : IMailSubmissionMutationService
{
    public async Task<MailSubmissionMutationResult> MutateAsync(
        MailSubmissionMutationCommand command,
        AuthenticatedMailUser user,
        JmapInvocationContext context,
        CancellationToken cancellationToken)
    {
        var account = await accounts.GetAccountByInboxIdAsync(user, command.AccountId, cancellationToken)
            .ConfigureAwait(false);
        if (account is null)
            return new(MailSubmissionMutationStatus.AccountNotFound, null, null, [], [], [], null, null);
        await identities.EnsureDefaultAsync(account, cancellationToken).ConfigureAwait(false);
        var oldState = await states.GetStateAsync(
            account.InboxId,
            JmapConstants.EmailSubmissionDataType,
            cancellationToken).ConfigureAwait(false);
        if (command.IfInState is not null
            && !string.Equals(command.IfInState, oldState, StringComparison.Ordinal))
            return new(MailSubmissionMutationStatus.StateMismatch, null, null, [], [], [], null, null);

        var created = new List<MailSubmissionCreateOutcome>(command.Creates.Count);
        var updated = new List<MailSubmissionUpdateOutcome>(command.Updates.Count);
        var destroyed = new List<MailSubmissionDestroyOutcome>(command.Destroys.Count);
        var successful = new Dictionary<string, string>(StringComparer.Ordinal);
        await CreateItemsAsync(command.Creates, account, context, created, successful, cancellationToken).ConfigureAwait(false);
        await UpdateItemsAsync(command.Updates, account.InboxId, context, updated, successful, cancellationToken).ConfigureAwait(false);
        await DestroyItemsAsync(command.Destroys, account.InboxId, context, destroyed, successful, cancellationToken).ConfigureAwait(false);
        var newState = await states.GetStateAsync(account.InboxId,
            JmapConstants.EmailSubmissionDataType, cancellationToken).ConfigureAwait(false);
        var implicitCommand = BuildImplicitEmailSet(command, context, successful);
        var implicitResult = implicitCommand is null ? null
            : await emailSet.MutateAsync(implicitCommand, user, context, cancellationToken)
                .ConfigureAwait(false);
        return new(MailSubmissionMutationStatus.Ok, oldState, newState,
            created, updated, destroyed, implicitCommand, implicitResult);
    }

    private async Task CreateItemsAsync(IReadOnlyList<MailSubmissionCreate> items, JmapAccount account,
        JmapInvocationContext context, List<MailSubmissionCreateOutcome> created,
        Dictionary<string, string> successful, CancellationToken cancellationToken)
    {
        foreach (var item in items)
        {
            var result = await CreateAsync(account, context, item.CreationId, item.Draft,
                cancellationToken).ConfigureAwait(false);
            if (result.Error is not null)
            {
                created.Add(new(item.CreationId, null, result.Error));
                continue;
            }
            var submission = result.Submission!;
            var submissionId = JmapId.Submission(submission.Id);
            context.CreatedIds[item.CreationId] = submissionId;
            created.Add(new(item.CreationId, await SnapshotAsync(submission, cancellationToken)
                .ConfigureAwait(false), null));
            successful[submissionId] = submission.EmailId;
        }
    }

    private async Task UpdateItemsAsync(IReadOnlyList<MailSubmissionUpdate> items, Guid accountId,
        JmapInvocationContext context, List<MailSubmissionUpdateOutcome> updated,
        Dictionary<string, string> successful, CancellationToken cancellationToken)
    {
        foreach (var item in items)
        {
            var resolvedId = context.ResolveId(item.RequestedId);
            if (!JmapId.TryParseSubmission(resolvedId, out var id))
            {
                updated.Add(new(item.RequestedId, null, Failure(MailSubmissionMutationError.NotFound)));
                continue;
            }
            var submission = await database.JmapEmailSubmissions.FirstOrDefaultAsync(
                candidate => candidate.Id == id && candidate.AccountId == accountId,
                cancellationToken).ConfigureAwait(false);
            if (submission is null)
            {
                updated.Add(new(item.RequestedId, null, Failure(MailSubmissionMutationError.NotFound)));
                continue;
            }
            var current = await SnapshotAsync(submission, cancellationToken).ConfigureAwait(false);
            var error = MailSubmissionAssertions.Verify(current, item.Patch);
            if (error is not null)
            {
                updated.Add(new(item.RequestedId, null, error));
                continue;
            }
            updated.Add(new(item.RequestedId, id, null));
            successful[resolvedId!] = submission.EmailId;
        }
    }

    private async Task DestroyItemsAsync(IReadOnlyList<MailSubmissionDestroy> items, Guid accountId,
        JmapInvocationContext context, List<MailSubmissionDestroyOutcome> destroyed,
        Dictionary<string, string> successful, CancellationToken cancellationToken)
    {
        foreach (var item in items)
        {
            var resolvedId = context.ResolveId(item.RequestedId);
            if (!JmapId.TryParseSubmission(resolvedId, out var id))
            {
                destroyed.Add(new(item.RequestedId, null, Failure(MailSubmissionMutationError.NotFound)));
                continue;
            }
            var submission = await database.JmapEmailSubmissions.FirstOrDefaultAsync(
                candidate => candidate.Id == id && candidate.AccountId == accountId,
                cancellationToken).ConfigureAwait(false);
            if (submission is null)
            {
                destroyed.Add(new(item.RequestedId, null, Failure(MailSubmissionMutationError.NotFound)));
                continue;
            }
            successful[resolvedId!] = submission.EmailId;
            database.JmapEmailSubmissions.Remove(submission);
            await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            destroyed.Add(new(item.RequestedId, id, null));
        }
    }

    private Task<MailSubmissionSnapshot> SnapshotAsync(
        JmapEmailSubmissionDB submission, CancellationToken cancellationToken) =>
        MailSubmissionSnapshots.ReadAsync(database, submission, true, cancellationToken);

    private async Task<SubmissionCreateResult> CreateAsync(
        JmapAccount account,
        JmapInvocationContext context,
        string creationId,
        MailSubmissionDraft value,
        CancellationToken cancellationToken)
    {
        if (!JmapId.IsValidId(creationId)
            || value.InvalidFields || string.IsNullOrEmpty(value.IdentityReference) || string.IsNullOrEmpty(value.MessageReference)
            || !JmapId.TryParseIdentity(context.ResolveId(value.IdentityReference), out var identityId)
            || !JmapId.TryParseEmail(context.ResolveId(value.MessageReference), out var emailId))
        {
            return SubmissionCreateResult.Failed(MailSubmissionMutationError.InvalidProperties);
        }
        var identity = await database.JmapIdentities.AsNoTracking().FirstOrDefaultAsync(
            candidate => candidate.Id == identityId && candidate.AccountId == account.InboxId,
            cancellationToken).ConfigureAwait(false);
        var email = await database.Emails.AsNoTracking().FirstOrDefaultAsync(
            candidate => candidate.Id == emailId
                && candidate.Folder.InboxId == account.InboxId
                && !candidate.IsDeleted,
            cancellationToken).ConfigureAwait(false);
        if (identity is null || email is null)
            return SubmissionCreateResult.Failed(MailSubmissionMutationError.InvalidProperties);
        var rawBytes = await mailboxContent.ReadAsync(email, cancellationToken).ConfigureAwait(false);
        if (rawBytes.LongLength > environment.Limits.MaxMessageSizeBytes)
        {
            return new(null, new(MailSubmissionMutationError.TooLarge, null, null, null, environment.Limits.MaxMessageSizeBytes, null));
        }

        var envelope = await AuthorizeEnvelopeAsync(value.Envelope, rawBytes, identity.Email, context, cancellationToken).ConfigureAwait(false);
        if (envelope.Failure is not null) return new(null, envelope.Failure);
        var recipients = envelope.Envelope!.Recipients.Select(address => address.Address).ToArray();

        var envelopeRecipients = new List<MailEnvelopeRecipient>(recipients.Length);
        var forbiddenRecipients = new List<string>();
        foreach (var recipient in recipients)
        {
            var isLocal = await emailService.CanReceiveAsync(recipient, cancellationToken).ConfigureAwait(false);
            if (!isLocal && !environment.Smtp.AllowRelay)
                forbiddenRecipients.Add(recipient);
            envelopeRecipients.Add(new MailEnvelopeRecipient(recipient, isLocal));
        }
        if (forbiddenRecipients.Count > 0)
        {
            return new(null, new(MailSubmissionMutationError.InvalidRecipients, null, null, forbiddenRecipients.ToArray(), null, null));
        }

        if (!TryBuildDeliveryMessage(rawBytes, out var deliveryMessage))
            return SubmissionCreateResult.Failed(MailSubmissionMutationError.InvalidEmail);
        return await QueueAsync(account.InboxId, identity.Id, email, context, envelope.Envelope,
            envelopeRecipients, deliveryMessage, cancellationToken).ConfigureAwait(false);
    }

    private async Task<(MailSubmissionEnvelope? Envelope, MailSubmissionMutationFailure? Failure)> AuthorizeEnvelopeAsync(
        MailSubmissionEnvelopeDraft? draft, byte[] rawBytes, string identityEmail,
        JmapInvocationContext context, CancellationToken cancellationToken)
    {
        if (!MailSubmissionEmailValidator.TryValidate(rawBytes, out var invalidEmailProperties))
        {
            return (null, new(MailSubmissionMutationError.InvalidEmail, null, null, null, null, null, invalidEmailProperties));
        }
        var raw = Encoding.Latin1.GetString(rawBytes);
        if (!senderAuthorization.HasMatchingFromAddress(raw, identityEmail))
            return (null, Failure(MailSubmissionMutationError.ForbiddenFrom));
        var envelope = MailSubmissionEnvelopeBuilder.Build(draft, rawBytes, identityEmail,
            environment.Limits.MaxMessageSizeBytes);
        if (envelope.Failure is not null) return (null, envelope.Failure);
        var sender = envelope.Envelope!.Sender.Address;
        var recipients = envelope.Envelope.Recipients.Select(address => address.Address).ToArray();
        if (!await senderAuthorization.CanSendAsAsync(context.User.Username, sender, cancellationToken).ConfigureAwait(false))
            return (null, Failure(MailSubmissionMutationError.ForbiddenMailFrom));
        if (recipients.Length == 0)
            return (null, Failure(MailSubmissionMutationError.NoRecipients));
        if (recipients.Length > environment.Limits.MaxRecipientsPerMessage)
        {
            return (null, new(MailSubmissionMutationError.TooManyRecipients, null, null, null, null, environment.Limits.MaxRecipientsPerMessage));
        }

        return (envelope.Envelope, null);
    }

    private async Task<SubmissionCreateResult> QueueAsync(Guid accountId, Guid identityId, EmailDB email,
        JmapInvocationContext context, MailSubmissionEnvelope envelope,
        List<MailEnvelopeRecipient> envelopeRecipients, string deliveryMessage, CancellationToken cancellationToken)
    {
        // PostgreSQL timestamps retain microseconds. Return exactly the persisted instant.
        var clock = DateTime.UtcNow;
        var now = new DateTime(clock.Ticks - clock.Ticks % TimeSpan.TicksPerMicrosecond, DateTimeKind.Utc);
        var queueId = Guid.CreateVersion7();
        var queue = new MailQueueMessageDB
        {
            Id = queueId,
            EnvelopeSender = envelope.Sender.Address,
            AuthenticatedUser = context.User.Username,
            Direction = MailQueueDirections.Submission,
            State = MailQueueStates.Pending,
            ScanState = MailQueueScanStates.Pending,
            ReceivedAt = now,
            NextAttemptAt = now,
            SentCopyCreated = true,
        };
        await queueContent.SetAsync(queue, deliveryMessage, cancellationToken).ConfigureAwait(false);
        for (var recipientIndex = 0; recipientIndex < envelopeRecipients.Count; recipientIndex++)
        {
            var recipient = envelopeRecipients[recipientIndex];
            queue.Recipients.Add(new MailQueueRecipientDB
            {
                Id = Guid.CreateVersion7(),
                Recipient = recipient.Address,
                IsLocal = recipient.IsLocal,
                State = MailQueueRecipientStates.Pending,
                NextAttemptAt = now,
            });
        }
        var id = Guid.CreateVersion7();
        var submission = new JmapEmailSubmissionDB
        {
            Id = id,
            SubmissionObjectId = JmapId.Submission(id),
            AccountId = accountId,
            IdentityId = JmapId.Identity(identityId),
            EmailId = JmapId.Email(email.Id),
            ThreadId = JmapId.Thread(email.ThreadObjectId ?? email.Id.ToString("N")),
            QueueId = queueId,
            EnvelopeSender = envelope.Sender.Address,
            EnvelopeRecipients = envelope.Recipients.Select(address => address.Address).ToArray(),
            EnvelopeJson = MailSubmissionEnvelopeCache.Encode(envelope),
            UndoStatus = "final",
            SendAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        };
        await database.MailQueueMessages.AddAsync(queue, cancellationToken).ConfigureAwait(false);
        await database.JmapEmailSubmissions.AddAsync(submission, cancellationToken).ConfigureAwait(false);
        await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new SubmissionCreateResult(submission, null);
    }

    private static bool TryBuildDeliveryMessage(byte[] raw, out string value)
    {
        value = string.Empty;
        try
        {
            using var message = JmapEmailCodec.Parse(raw);
            message.Bcc.Clear();
            while (message.Headers.Contains(HeaderId.Bcc))
                message.Headers.Remove(HeaderId.Bcc);
            var format = FormatOptions.Default.Clone();
            format.NewLineFormat = NewLineFormat.Dos;
            using var stream = new MemoryStream();
            message.WriteTo(format, stream);
            value = Encoding.Latin1.GetString(stream.ToArray());
            return true;
        }
        catch (Exception exception) when (exception is FormatException or InvalidOperationException)
        {
            return false;
        }
    }

    private static MailMessageMutationCommand? BuildImplicitEmailSet(
        MailSubmissionMutationCommand command,
        JmapInvocationContext context,
        Dictionary<string, string> successful)
    {
        var emailUpdates = new Dictionary<string, List<MailMessagePatchFragment>>(StringComparer.Ordinal);
        foreach (var item in command.OnSuccessUpdates)
        {
            var submissionId = context.ResolveId(item.RequestedSubmissionId);
            if (submissionId is null || !successful.TryGetValue(submissionId, out var emailId))
                continue;
            if (!emailUpdates.TryGetValue(emailId, out var combined))
                emailUpdates[emailId] = combined = [];
            combined.AddRange(item.Fragments);
        }
        var emailDestroys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in command.OnSuccessDestroys)
        {
            var submissionId = context.ResolveId(item.RequestedSubmissionId);
            if (submissionId is not null && successful.TryGetValue(submissionId, out var emailId))
                emailDestroys.Add(emailId);
        }
        if (emailUpdates.Count == 0 && emailDestroys.Count == 0)
            return null;
        return new(command.AccountId, null, [], emailUpdates.Select(item =>
            new MailMessageUpdate(item.Key, MailMessagePatchMerger.Merge(item.Value))).ToArray(),
            emailDestroys.Select(id => new MailMessageDestroy(id)).ToArray());
    }

    private static MailSubmissionMutationFailure Failure(MailSubmissionMutationError type,
        IReadOnlyList<string>? properties = null) =>
        new(type, null, properties, null, null, null);



    private sealed record SubmissionCreateResult(JmapEmailSubmissionDB? Submission, MailSubmissionMutationFailure? Error)
    {
        public static SubmissionCreateResult Failed(MailSubmissionMutationError type) =>
            new(null, Failure(type));
    }
}
