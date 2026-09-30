using mk8.email.Contracts.Messaging;
using System.Text;
using System.Text.Json.Nodes;
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
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly ParserOptions StrictAddressParserOptions = new()
    {
        AddressParserComplianceMode = RfcComplianceMode.Strict,
        AllowAddressesWithoutDomain = false,
        AllowUnquotedCommasInAddresses = false,
    };
    private static readonly HashSet<string> CreateProperties = new HashSet<string>(
        ["identityId", "emailId", "envelope"],
        StringComparer.Ordinal);
    private static readonly HashSet<string> UpdateProperties = new HashSet<string>(
        ["undoStatus"],
        StringComparer.Ordinal);
    private static readonly Dictionary<string, string> SingletonHeaders =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Date"] = "sentAt",
            ["From"] = "from",
            ["Sender"] = "sender",
            ["Reply-To"] = "replyTo",
            ["To"] = "to",
            ["Cc"] = "cc",
            ["Bcc"] = "bcc",
            ["Message-ID"] = "messageId",
            ["In-Reply-To"] = "inReplyTo",
            ["References"] = "references",
            ["Subject"] = "subject",
        };
    private static readonly HashSet<string> SingletonMimeHeaders = new HashSet<string>(
        [
            "MIME-Version", "Content-Type", "Content-Transfer-Encoding",
            "Content-Disposition", "Content-ID", "Content-Description",
            "Content-Language", "Content-Location",
        ],
        StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> ResentHeaderProperties =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Resent-Date"] = "header:Resent-Date:asDate:all",
            ["Resent-From"] = "header:Resent-From:asAddresses:all",
            ["Resent-Sender"] = "header:Resent-Sender:asAddresses:all",
            ["Resent-To"] = "header:Resent-To:asAddresses:all",
            ["Resent-Cc"] = "header:Resent-Cc:asAddresses:all",
            ["Resent-Bcc"] = "header:Resent-Bcc:asAddresses:all",
            ["Resent-Message-ID"] = "header:Resent-Message-ID:asMessageIds:all",
        };

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
        foreach (var item in command.Creates)
        {
            var draft = ApplicationValueCodec.Decode(item.Draft) as JsonObject
                ?? throw new InvalidOperationException("The submission draft is not an object.");
            var result = await CreateAsync(account, context, item.CreationId, draft,
                cancellationToken).ConfigureAwait(false);
            if (result.Error is not null)
            {
                created.Add(new(item.CreationId, null, Failure(result.Error)));
                continue;
            }
            var submission = result.Submission!;
            var submissionId = JmapId.Submission(submission.Id);
            context.CreatedIds[item.CreationId] = submissionId;
            created.Add(new(item.CreationId, await SnapshotAsync(submission, cancellationToken)
                .ConfigureAwait(false), null));
            successful[submissionId] = submission.EmailId;
        }
        foreach (var item in command.Updates)
        {
            var resolvedId = context.ResolveId(item.RequestedId);
            if (!JmapId.TryParseSubmission(resolvedId, out var id))
            {
                updated.Add(new(item.RequestedId, null, Failure("notFound")));
                continue;
            }
            var submission = await database.JmapEmailSubmissions.SingleOrDefaultAsync(
                candidate => candidate.Id == id && candidate.AccountId == account.InboxId,
                cancellationToken).ConfigureAwait(false);
            if (submission is null)
            {
                updated.Add(new(item.RequestedId, null, Failure("notFound")));
                continue;
            }
            var current = await JmapEmailSubmissionJson.BuildAsync(database, submission, null,
                cancellationToken).ConfigureAwait(false);
            var patch = ApplicationValueCodec.Decode(item.Patch) as JsonObject
                ?? throw new InvalidOperationException("The submission patch is not an object.");
            if (!JmapMethodHelpers.TryApplyPatchAllowingUnchangedProperties(
                    current, patch, UpdateProperties, out var patched, out var invalidProperties))
            {
                updated.Add(new(item.RequestedId, null, Failure("invalidPatch")));
                continue;
            }
            if (invalidProperties.Count > 0)
            {
                updated.Add(new(item.RequestedId, null,
                    Failure("invalidProperties", invalidProperties)));
                continue;
            }
            if (!TryReadUndoStatus(patched, out var requestedStatus))
            {
                updated.Add(new(item.RequestedId, null,
                    Failure("invalidProperties", ["undoStatus"])));
                continue;
            }
            if (string.Equals(requestedStatus, "canceled", StringComparison.Ordinal)
                && !string.Equals(submission.UndoStatus, "pending", StringComparison.Ordinal)
                || !string.Equals(requestedStatus, submission.UndoStatus, StringComparison.Ordinal))
            {
                updated.Add(new(item.RequestedId, null, Failure("cannotUnsend")));
                continue;
            }
            updated.Add(new(item.RequestedId, id, null));
            successful[resolvedId!] = submission.EmailId;
        }
        foreach (var item in command.Destroys)
        {
            var resolvedId = context.ResolveId(item.RequestedId);
            if (!JmapId.TryParseSubmission(resolvedId, out var id))
            {
                destroyed.Add(new(item.RequestedId, null, Failure("notFound")));
                continue;
            }
            var submission = await database.JmapEmailSubmissions.SingleOrDefaultAsync(
                candidate => candidate.Id == id && candidate.AccountId == account.InboxId,
                cancellationToken).ConfigureAwait(false);
            if (submission is null)
            {
                destroyed.Add(new(item.RequestedId, null, Failure("notFound")));
                continue;
            }
            successful[resolvedId!] = submission.EmailId;
            database.JmapEmailSubmissions.Remove(submission);
            await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            destroyed.Add(new(item.RequestedId, id, null));
        }
        var newState = await states.GetStateAsync(account.InboxId,
            JmapConstants.EmailSubmissionDataType, cancellationToken).ConfigureAwait(false);
        var implicitCommand = BuildImplicitEmailSet(command, context, successful);
        var implicitResult = implicitCommand is null ? null
            : await emailSet.MutateAsync(implicitCommand, user, context, cancellationToken)
                .ConfigureAwait(false);
        return new(MailSubmissionMutationStatus.Ok, oldState, newState,
            created, updated, destroyed, implicitCommand, implicitResult);
    }

    private async Task<MailSubmissionSnapshot> SnapshotAsync(
        JmapEmailSubmissionDB submission, CancellationToken cancellationToken)
    {
        var recipients = await database.MailQueueRecipients.AsNoTracking()
            .Where(recipient => recipient.MessageId == submission.QueueId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return new(submission.Id, submission.IdentityId, submission.EmailId,
            submission.ThreadId, submission.EnvelopeJson, submission.EnvelopeSender,
            submission.EnvelopeRecipients.ToArray(), submission.SendAt, submission.UndoStatus,
            recipients.Select(recipient => new MailSubmissionDeliverySnapshot(recipient.Recipient,
                MailSubmissionDeliveryState.Pending, null)).ToArray());
    }

    private async Task<SubmissionCreateResult> CreateAsync(
        JmapAccount account,
        JmapInvocationContext context,
        string creationId,
        JsonObject value,
        CancellationToken cancellationToken)
    {
        if (!JmapId.IsValidId(creationId)
            || value.Any(property => !CreateProperties.Contains(property.Key))
            || !JmapMethodHelpers.TryGetRequiredString(value, "identityId", out var requestedIdentityId)
            || !JmapMethodHelpers.TryGetRequiredString(value, "emailId", out var requestedEmailId)
            || !JmapId.TryParseIdentity(context.ResolveId(requestedIdentityId), out var identityId)
            || !JmapId.TryParseEmail(context.ResolveId(requestedEmailId), out var emailId))
        {
            return SubmissionCreateResult.Failed("invalidProperties");
        }
        var identity = await database.JmapIdentities.AsNoTracking().SingleOrDefaultAsync(
            candidate => candidate.Id == identityId && candidate.AccountId == account.InboxId,
            cancellationToken).ConfigureAwait(false);
        var email = await database.Emails.AsNoTracking().SingleOrDefaultAsync(
            candidate => candidate.Id == emailId
                && candidate.Folder.InboxId == account.InboxId
                && !candidate.IsDeleted,
            cancellationToken).ConfigureAwait(false);
        if (identity is null || email is null)
            return SubmissionCreateResult.Failed("invalidProperties");
        var rawBytes = await mailboxContent.ReadAsync(email, cancellationToken).ConfigureAwait(false);
        if (rawBytes.LongLength > environment.Limits.MaxMessageSizeBytes)
        {
            var error = JmapMethodHelpers.SetError("tooLarge");
            error["maxSize"] = environment.Limits.MaxMessageSizeBytes;
            return new SubmissionCreateResult(null, error);
        }

        if (!TryValidateSubmissionEmail(rawBytes, out var invalidEmailProperties))
        {
            return new SubmissionCreateResult(
                null,
                JmapMethodHelpers.SetError(
                    "invalidEmail",
                    properties: invalidEmailProperties));
        }
        var raw = Encoding.Latin1.GetString(rawBytes);
        if (!senderAuthorization.HasMatchingFromAddress(raw, identity.Email))
            return SubmissionCreateResult.Failed("forbiddenFrom");
        if (!TryBuildEnvelope(
                value["envelope"],
                rawBytes,
                identity.Email,
                environment.Limits.MaxMessageSizeBytes,
                out var sender,
                out var recipients,
                out var envelopeJson,
                out var envelopeError))
            return new SubmissionCreateResult(null, envelopeError);
        if (!await senderAuthorization.CanSendAsAsync(context.User.Username, sender, cancellationToken).ConfigureAwait(false))
            return SubmissionCreateResult.Failed("forbiddenMailFrom");
        if (recipients.Count == 0)
            return SubmissionCreateResult.Failed("noRecipients");
        if (recipients.Count > environment.Limits.MaxRecipientsPerMessage)
        {
            var error = JmapMethodHelpers.SetError("tooManyRecipients");
            error["maxRecipients"] = environment.Limits.MaxRecipientsPerMessage;
            return new SubmissionCreateResult(null, error);
        }

        var envelopeRecipients = new List<MailEnvelopeRecipient>(recipients.Count);
        var forbiddenRecipients = new List<string>();
        for (var recipientIndex = 0; recipientIndex < recipients.Count; recipientIndex++)
        {
            var recipient = recipients[recipientIndex];
            var isLocal = await emailService.CanReceiveAsync(recipient, cancellationToken).ConfigureAwait(false);
            if (!isLocal && !environment.Smtp.AllowRelay)
                forbiddenRecipients.Add(recipient);
            envelopeRecipients.Add(new MailEnvelopeRecipient(recipient, isLocal));
        }
        if (forbiddenRecipients.Count > 0)
        {
            var error = JmapMethodHelpers.SetError("invalidRecipients");
            error["invalidRecipients"] = JmapMethodHelpers.ToJsonArray(forbiddenRecipients);
            return new SubmissionCreateResult(null, error);
        }

        if (!TryBuildDeliveryMessage(rawBytes, out var deliveryMessage))
            return SubmissionCreateResult.Failed("invalidEmail");
        var now = DateTime.UtcNow;
        var queueId = Guid.CreateVersion7();
        var queue = new MailQueueMessageDB
        {
            Id = queueId,
            EnvelopeSender = sender,
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
            AccountId = account.InboxId,
            IdentityId = JmapId.Identity(identity.Id),
            EmailId = JmapId.Email(email.Id),
            ThreadId = JmapId.Thread(email.ThreadObjectId ?? email.Id.ToString("N")),
            QueueId = queueId,
            EnvelopeSender = sender,
            EnvelopeRecipients = recipients.ToArray(),
            EnvelopeJson = envelopeJson,
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

    private static bool TryBuildEnvelope(
        JsonNode? node,
        byte[] raw,
        string identityEmail,
        long maximumMessageSize,
        out string sender,
        out List<string> recipients,
        out string envelopeJson,
        out JsonObject? error)
    {
        sender = identityEmail;
        recipients = [];
        envelopeJson = string.Empty;
        error = null;
        if (node is null)
        {
            try
            {
                using var message = JmapEmailCodec.Parse(raw);
                sender = message.Sender?.Address
                    ?? message.From.Mailboxes.FirstOrDefault()?.Address
                    ?? identityEmail;
                if (!string.Equals(sender, identityEmail, StringComparison.OrdinalIgnoreCase))
                    sender = identityEmail;
                recipients.AddRange(message.To.Mailboxes
                    .Concat(message.Cc.Mailboxes)
                    .Concat(message.Bcc.Mailboxes)
                    .Select(mailbox => mailbox.Address)
                    .Distinct(StringComparer.OrdinalIgnoreCase));
                var invalidGeneratedRecipients = recipients
                    .Where(recipient => !IsValidEnvelopeAddress(recipient, allowEmpty: false))
                    .ToArray();
                if (invalidGeneratedRecipients.Length > 0)
                {
                    error = JmapMethodHelpers.SetError("invalidRecipients");
                    error["invalidRecipients"] = JmapMethodHelpers.ToJsonArray(invalidGeneratedRecipients);
                    return false;
                }
                envelopeJson = BuildEnvelopeJson(
                    sender,
                    null,
                    recipients.Select(recipient => (recipient, (JsonObject?)null)))
                    .ToJsonString(JmapJson.SerializerOptions);
                return true;
            }
            catch (FormatException)
            {
                error = JmapMethodHelpers.SetError("invalidEmail");
                return false;
            }
        }
        if (node is not JsonObject envelope
            || envelope.Any(item => item.Key is not ("mailFrom" or "rcptTo"))
            || !TryEnvelopeAddress(
                envelope["mailFrom"],
                allowEmpty: false,
                allowSize: true,
                raw.LongLength,
                maximumMessageSize,
                out sender,
                out var senderParameters,
                out _)
            || envelope["rcptTo"] is not JsonArray recipientArray)
        {
            error = JmapMethodHelpers.SetError("invalidProperties", properties: ["envelope"]);
            return false;
        }
        var invalid = new List<string>();
        var hasInvalidProperties = false;
        var normalizedRecipients = new List<(string Email, JsonObject? Parameters)>(recipientArray.Count);
        foreach (var item in recipientArray)
        {
            if (!TryEnvelopeAddress(
                    item,
                    allowEmpty: false,
                    allowSize: false,
                    raw.LongLength,
                    maximumMessageSize,
                    out var recipient,
                    out var parameters,
                    out var addressError))
            {
                if (addressError == EnvelopeAddressError.InvalidEmail)
                    invalid.Add(TryReadEnvelopeEmail(item));
                else
                    hasInvalidProperties = true;
            }
            else
            {
                recipients.Add(recipient);
                normalizedRecipients.Add((recipient, parameters));
            }
        }
        if (hasInvalidProperties)
        {
            error = JmapMethodHelpers.SetError("invalidProperties", properties: ["envelope"]);
            return false;
        }
        if (invalid.Count > 0)
        {
            error = JmapMethodHelpers.SetError("invalidRecipients");
            error["invalidRecipients"] = JmapMethodHelpers.ToJsonArray(invalid);
            return false;
        }
        envelopeJson = BuildEnvelopeJson(sender, senderParameters, normalizedRecipients)
            .ToJsonString(JmapJson.SerializerOptions);
        return true;
    }

    private static JsonObject BuildEnvelopeJson(
        string sender,
        JsonObject? senderParameters,
        IEnumerable<(string Email, JsonObject? Parameters)> recipients)
    {
        var rcptTo = new JsonArray();
        foreach (var recipient in recipients)
        {
            rcptTo.Add(new JsonObject
            {
                ["email"] = recipient.Email,
                ["parameters"] = recipient.Parameters?.DeepClone(),
            });
        }
        return new JsonObject
        {
            ["mailFrom"] = new JsonObject
            {
                ["email"] = sender,
                ["parameters"] = senderParameters?.DeepClone(),
            },
            ["rcptTo"] = rcptTo,
        };
    }

    private static bool TryValidateSubmissionEmail(
        byte[] raw,
        out IReadOnlyList<string> invalidProperties)
    {
        var invalid = new HashSet<string>(StringComparer.Ordinal);
        invalidProperties = [];
        ValidateWireFormat(raw, invalid);
        try
        {
            using var message = JmapEmailCodec.Parse(raw);
            foreach (var singleton in SingletonHeaders)
            {
                var count = message.Headers.Count(header => header.Field.Equals(
                    singleton.Key,
                    StringComparison.OrdinalIgnoreCase));
                if (count > 1)
                    invalid.Add(singleton.Value);
            }

            var dateHeaders = Headers(message.Headers, "Date");
            if (dateHeaders.Length != 1
                || !JmapDate.IsValidRfc5322DateTime(dateHeaders[0].Value))
            {
                invalid.Add("sentAt");
            }

            var fromHeaders = Headers(message.Headers, "From");
            InternetAddressList? from = null;
            if (fromHeaders.Length != 1
                || !InternetAddressList.TryParse(
                    StrictAddressParserOptions,
                    fromHeaders[0].Value,
                    out from)
                || from.Count == 0
                || from.Any(address => address is not MailboxAddress))
            {
                invalid.Add("from");
            }

            var senderHeaders = Headers(message.Headers, "Sender");
            if (senderHeaders.Length == 1
                && !MailboxAddress.TryParse(
                    StrictAddressParserOptions,
                    senderHeaders[0].Value,
                    out _))
            {
                invalid.Add("sender");
            }
            if (from is not null && from.Mailboxes.Skip(1).Any() && senderHeaders.Length != 1)
                invalid.Add("sender");

            ValidateAddressHeader(message.Headers, "Reply-To", "replyTo", false, invalid);
            ValidateAddressHeader(message.Headers, "To", "to", false, invalid);
            ValidateAddressHeader(message.Headers, "Cc", "cc", false, invalid);
            ValidateAddressHeader(message.Headers, "Bcc", "bcc", true, invalid);
            ValidateMessageIdsHeader(
                message.Headers,
                "Message-ID",
                "messageId",
                requireSingle: true,
                invalid);
            ValidateMessageIdsHeader(
                message.Headers,
                "In-Reply-To",
                "inReplyTo",
                requireSingle: false,
                invalid);
            ValidateMessageIdsHeader(
                message.Headers,
                "References",
                "references",
                requireSingle: false,
                invalid);
            ValidateResentHeaders(message.Headers, invalid);

            if (message.Headers
                .Where(header => SingletonMimeHeaders.Contains(header.Field))
                .GroupBy(header => header.Field, StringComparer.OrdinalIgnoreCase)
                .Any(group => group.Count() > 1)
                || HasDuplicateMimeHeaders(message.Body))
            {
                invalid.Add("bodyStructure");
            }
        }
        catch (FormatException)
        {
            invalid.Add("bodyStructure");
        }

        invalidProperties = invalid.Order(StringComparer.Ordinal).ToArray();
        return invalidProperties.Count == 0;
    }

    private static void ValidateWireFormat(byte[] raw, HashSet<string> invalid)
    {
        var inHeaders = true;
        var hasHeader = false;
        var lineStart = 0;
        for (var index = 0; index < raw.Length; index++)
        {
            if (raw[index] == (byte)'\r')
            {
                if (index + 1 >= raw.Length || raw[index + 1] != (byte)'\n')
                {
                    invalid.Add(inHeaders ? "headers" : "bodyStructure");
                    continue;
                }

                var line = raw.AsSpan(lineStart, index - lineStart);
                ValidateWireLine(line, inHeaders, ref hasHeader, invalid);
                if (inHeaders && line.Length == 0)
                    inHeaders = false;
                index++;
                lineStart = index + 1;
            }
            else if (raw[index] == (byte)'\n')
            {
                invalid.Add(inHeaders ? "headers" : "bodyStructure");
                var line = raw.AsSpan(lineStart, index - lineStart);
                ValidateWireLine(line, inHeaders, ref hasHeader, invalid);
                if (inHeaders && line.Length == 0)
                    inHeaders = false;
                lineStart = index + 1;
            }
        }

        if (lineStart < raw.Length)
        {
            ValidateWireLine(raw.AsSpan(lineStart), inHeaders, ref hasHeader, invalid);
            if (inHeaders)
                invalid.Add("headers");
        }
    }

    private static void ValidateWireLine(
        ReadOnlySpan<byte> line,
        bool inHeaders,
        ref bool hasHeader,
        HashSet<string> invalid)
    {
        if (line.Length > 998)
            invalid.Add(inHeaders ? "headers" : "bodyStructure");
        if (!inHeaders)
        {
            if (line.Contains((byte)0))
                invalid.Add("bodyStructure");
            return;
        }
        if (line.Length == 0)
            return;

        var valueStart = 0;
        if (line[0] is (byte)' ' or (byte)'\t')
        {
            if (!hasHeader || IsWhitespaceOnly(line))
                invalid.Add("headers");
        }
        else
        {
            var colon = line.IndexOf((byte)':');
            if (colon <= 0 || !IsValidFieldName(line[..colon]))
            {
                invalid.Add("headers");
                return;
            }
            hasHeader = true;
            valueStart = colon + 1;
        }

        var value = line[valueStart..];
        if (HasInvalidHeaderValueByte(value))
        {
            invalid.Add("headers");
            return;
        }
        try
        {
            _ = StrictUtf8.GetCharCount(value);
        }
        catch (DecoderFallbackException)
        {
            invalid.Add("headers");
        }
    }

    private static bool IsWhitespaceOnly(ReadOnlySpan<byte> value)
    {
        foreach (ref readonly var character in value)
        {
            if (character is not ((byte)' ' or (byte)'\t'))
                return false;
        }
        return true;
    }

    private static bool IsValidFieldName(ReadOnlySpan<byte> value)
    {
        foreach (ref readonly var character in value)
        {
            if (character is < 33 or > 126)
                return false;
        }
        return true;
    }

    private static bool HasInvalidHeaderValueByte(ReadOnlySpan<byte> value)
    {
        foreach (ref readonly var character in value)
        {
            if (character != (byte)'\t' && (character < 32 || character == 127))
                return true;
        }
        return false;
    }

    private static Header[] Headers(HeaderList headers, string name) =>
        headers.Where(header => header.Field.Equals(name, StringComparison.OrdinalIgnoreCase))
            .ToArray();

    private static void ValidateAddressHeader(
        HeaderList headers,
        string headerName,
        string propertyName,
        bool allowEmpty,
        HashSet<string> invalid)
    {
        var matching = Headers(headers, headerName);
        if (matching.Length == 1
            && !TryParseAddressList(matching[0].Value, allowEmpty, false, out _))
        {
            invalid.Add(propertyName);
        }
    }

    private static void ValidateResentHeaders(HeaderList headers, HashSet<string> invalid)
    {
        var resent = headers
            .Where(header => ResentHeaderProperties.ContainsKey(header.Field))
            .ToArray();
        if (resent.Length == 0)
        {
            if (Headers(headers, "Resent-Reply-To").Length > 0)
                invalid.Add("header:Resent-Reply-To:asAddresses:all");
            return;
        }

        ValidateEveryHeader(
            resent,
            "Resent-Date",
            JmapDate.IsValidRfc5322DateTime,
            invalid);
        ValidateEveryHeader(
            resent,
            "Resent-From",
            value => TryParseAddressList(value, false, true, out _),
            invalid);
        ValidateEveryHeader(
            resent,
            "Resent-Sender",
            value => MailboxAddress.TryParse(StrictAddressParserOptions, value, out _),
            invalid);
        ValidateEveryHeader(
            resent,
            "Resent-To",
            value => TryParseAddressList(value, false, false, out _),
            invalid);
        ValidateEveryHeader(
            resent,
            "Resent-Cc",
            value => TryParseAddressList(value, false, false, out _),
            invalid);
        ValidateEveryHeader(
            resent,
            "Resent-Bcc",
            value => TryParseAddressList(value, true, false, out _),
            invalid);
        ValidateEveryHeader(
            resent,
            "Resent-Message-ID",
            value => JmapEmailCodec.IsValidMessageIdsHeader(value, true),
            invalid);

        ValidateResentCardinality(resent, invalid);
        if (!CanPartitionResentBlocks(resent))
            invalid.Add("headers");
        if (Headers(headers, "Resent-Reply-To").Length > 0)
            invalid.Add("header:Resent-Reply-To:asAddresses:all");
    }

    private static void ValidateEveryHeader(
        IEnumerable<Header> headers,
        string headerName,
        Func<string, bool> isValid,
        HashSet<string> invalid)
    {
        if (headers.Any(header => header.Field.Equals(headerName, StringComparison.OrdinalIgnoreCase)
            && !isValid(header.Value)))
        {
            invalid.Add(ResentHeaderProperties[headerName]);
        }
    }

    private static void ValidateResentCardinality(
        IReadOnlyList<Header> headers,
        HashSet<string> invalid)
    {
        var dateCount = CountHeaders(headers, "Resent-Date");
        var fromHeaders = headers
            .Where(header => header.Field.Equals("Resent-From", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var blockCount = Math.Max(1, Math.Max(dateCount, fromHeaders.Length));
        if (dateCount < blockCount)
            invalid.Add(ResentHeaderProperties["Resent-Date"]);
        if (fromHeaders.Length < blockCount)
            invalid.Add(ResentHeaderProperties["Resent-From"]);

        foreach (var name in ResentHeaderProperties.Keys
            .Where(name => name is not ("Resent-Date" or "Resent-From")))
        {
            if (CountHeaders(headers, name) > blockCount)
                invalid.Add(ResentHeaderProperties[name]);
        }

        var multiFromCount = fromHeaders.Count(ResentFromRequiresSender);
        if (CountHeaders(headers, "Resent-Sender") < multiFromCount)
            invalid.Add(ResentHeaderProperties["Resent-Sender"]);
    }

    private static int CountHeaders(IEnumerable<Header> headers, string name) =>
        headers.Count(header => header.Field.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static bool CanPartitionResentBlocks(Header[] headers)
    {
        var reachable = new bool[headers.Length + 1];
        reachable[0] = true;
        for (var start = 0; start < headers.Length; start++)
        {
            if (!reachable[start])
                continue;
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Header? from = null;
            var hasDate = false;
            var hasSender = false;
            for (var end = start; end < headers.Length; end++)
            {
                var header = headers[end];
                if (!names.Add(header.Field))
                    break;
                if (header.Field.Equals("Resent-Date", StringComparison.OrdinalIgnoreCase))
                    hasDate = true;
                else if (header.Field.Equals("Resent-From", StringComparison.OrdinalIgnoreCase))
                    from = header;
                else if (header.Field.Equals("Resent-Sender", StringComparison.OrdinalIgnoreCase))
                    hasSender = true;

                if (hasDate && from is not null && (!ResentFromRequiresSender(from) || hasSender))
                    reachable[end + 1] = true;
            }
        }
        return reachable[^1];
    }

    private static bool ResentFromRequiresSender(Header header) =>
        TryParseAddressList(header.Value, false, true, out var addresses)
        && addresses.Mailboxes.Skip(1).Any();

    private static bool TryParseAddressList(
        string value,
        bool allowEmpty,
        bool mailboxesOnly,
        out InternetAddressList addresses)
    {
        if (allowEmpty && JmapEmailCodec.IsHeaderCfwsOnly(value))
        {
            addresses = new InternetAddressList();
            return true;
        }
        if (!InternetAddressList.TryParse(
                StrictAddressParserOptions,
                value,
                out var parsedAddresses)
            || parsedAddresses is null)
        {
            addresses = new InternetAddressList();
            return false;
        }
        addresses = parsedAddresses;
        return (allowEmpty || addresses.Count > 0)
            && (!mailboxesOnly || addresses.All(address => address is MailboxAddress));
    }

    private static void ValidateMessageIdsHeader(
        HeaderList headers,
        string headerName,
        string propertyName,
        bool requireSingle,
        HashSet<string> invalid)
    {
        var matching = Headers(headers, headerName);
        if (matching.Length == 1
            && !JmapEmailCodec.IsValidMessageIdsHeader(matching[0].Value, requireSingle))
        {
            invalid.Add(propertyName);
        }
    }

    private static bool HasDuplicateMimeHeaders(MimeEntity? entity)
    {
        if (entity is null)
            return false;
        if (entity.Headers
            .Where(header => SingletonMimeHeaders.Contains(header.Field))
            .GroupBy(header => header.Field, StringComparer.OrdinalIgnoreCase)
            .Any(group => group.Count() > 1))
        {
            return true;
        }
        return entity is Multipart multipart
            && multipart.Any(HasDuplicateMimeHeaders);
    }

    private static bool TryEnvelopeAddress(
        JsonNode? node,
        bool allowEmpty,
        bool allowSize,
        long messageSize,
        long maximumMessageSize,
        out string email,
        out JsonObject? normalizedParameters,
        out EnvelopeAddressError error)
    {
        email = string.Empty;
        normalizedParameters = null;
        error = EnvelopeAddressError.None;
        if (node is not JsonObject address
            || !JmapMethodHelpers.TryGetRequiredString(address, "email", out email))
        {
            error = EnvelopeAddressError.InvalidProperties;
            return false;
        }
        if (address.Any(item => item.Key is not ("email" or "parameters")))
        {
            error = EnvelopeAddressError.InvalidProperties;
            return false;
        }
        if (!IsValidEnvelopeAddress(email, allowEmpty))
        {
            error = EnvelopeAddressError.InvalidEmail;
            return false;
        }

        if (!address.TryGetPropertyValue("parameters", out var parameterNode)
            || parameterNode is null)
        {
            return true;
        }
        if (parameterNode is not JsonObject parameters)
        {
            error = EnvelopeAddressError.InvalidProperties;
            return false;
        }
        normalizedParameters = new JsonObject();
        var parameterNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var parameter in parameters)
        {
            if (!parameterNames.Add(parameter.Key)
                || !allowSize
                || !parameter.Key.Equals("SIZE", StringComparison.OrdinalIgnoreCase)
                || parameter.Value is not JsonValue value
                || !value.TryGetValue<string>(out var sizeText)
                || sizeText.Length == 0
                || sizeText.Any(character => !char.IsAsciiDigit(character))
                || !long.TryParse(
                    sizeText,
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var declaredSize)
                || declaredSize < messageSize
                || declaredSize > maximumMessageSize)
            {
                error = EnvelopeAddressError.InvalidProperties;
                return false;
            }
            normalizedParameters["SIZE"] = sizeText;
        }
        return true;
    }

    private static bool IsValidEnvelopeAddress(string email, bool allowEmpty)
    {
        if (allowEmpty && email.Length == 0)
            return true;
        if (email.Length > 254 || !email.All(char.IsAscii))
            return false;
        var separator = email.LastIndexOf('@');
        return separator is > 0 and <= 64
            && email.Length - separator - 1 is > 0 and <= 255
            && MailboxAddress.TryParse(email, out var mailbox)
            && string.Equals(mailbox.Address, email, StringComparison.OrdinalIgnoreCase);
    }

    private static string TryReadEnvelopeEmail(JsonNode? node) =>
        node is JsonObject address
        && address["email"] is JsonValue value
        && value.TryGetValue<string>(out var email)
            ? email
            : string.Empty;

    private enum EnvelopeAddressError
    {
        None,
        InvalidEmail,
        InvalidProperties,
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

    private static bool TryReadUndoStatus(JsonObject value, out string status)
    {
        status = string.Empty;
        if (value["undoStatus"] is not JsonValue statusValue
            || !statusValue.TryGetValue<string>(out status!)
            || status is not ("pending" or "final" or "canceled"))
            return false;
        return true;
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

    private static MailSubmissionMutationFailure Failure(string type,
        IReadOnlyList<string>? properties = null) =>
        new(ParseError(type), null, properties, null, null, null);

    private static MailSubmissionMutationFailure Failure(JsonObject error)
    {
        var type = error["type"]?.GetValue<string>()
            ?? throw new InvalidOperationException("The submission failure is missing its kind.");
        return new(ParseError(type), error["description"]?.GetValue<string>(),
            ReadStrings(error["properties"]), ReadStrings(error["invalidRecipients"]),
            error["maxSize"]?.GetValue<int>(), error["maxRecipients"]?.GetValue<int>());
    }

    private static string[]? ReadStrings(JsonNode? node) =>
        node is JsonArray array ? array.Select(item => item!.GetValue<string>()).ToArray() : null;

    private static MailSubmissionMutationError ParseError(string type) => type switch
    {
        "invalidProperties" => MailSubmissionMutationError.InvalidProperties,
        "invalidPatch" => MailSubmissionMutationError.InvalidPatch,
        "notFound" => MailSubmissionMutationError.NotFound,
        "cannotUnsend" => MailSubmissionMutationError.CannotUnsend,
        "invalidEmail" => MailSubmissionMutationError.InvalidEmail,
        "forbiddenFrom" => MailSubmissionMutationError.ForbiddenFrom,
        "forbiddenMailFrom" => MailSubmissionMutationError.ForbiddenMailFrom,
        "noRecipients" => MailSubmissionMutationError.NoRecipients,
        "tooManyRecipients" => MailSubmissionMutationError.TooManyRecipients,
        "invalidRecipients" => MailSubmissionMutationError.InvalidRecipients,
        "tooLarge" => MailSubmissionMutationError.TooLarge,
        _ => throw new InvalidOperationException("The submission failure has an unknown kind."),
    };

    private sealed record SubmissionCreateResult(JmapEmailSubmissionDB? Submission, JsonObject? Error)
    {
        public static SubmissionCreateResult Failed(string type) =>
            new(null, JmapMethodHelpers.SetError(type));
    }
}
