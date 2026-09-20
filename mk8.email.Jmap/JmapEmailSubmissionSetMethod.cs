using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using MimeKit;
using mk8.email.Application.Interfaces;
using mk8.email.Infrastructure.Data;
using mk8.email.Infrastructure.Environment;
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
    EnvironmentConfig environment) : IJmapMethod
{
    private static readonly ParserOptions StrictAddressParserOptions = new()
    {
        AddressParserComplianceMode = RfcComplianceMode.Strict,
        AllowAddressesWithoutDomain = false,
        AllowUnquotedCommasInAddresses = false,
    };
    private static readonly IReadOnlySet<string> CreateProperties = new HashSet<string>(
        ["identityId", "emailId", "envelope"],
        StringComparer.Ordinal);
    private static readonly IReadOnlySet<string> UpdateProperties = new HashSet<string>(
        ["undoStatus"],
        StringComparer.Ordinal);
    private static readonly IReadOnlyDictionary<string, string> SingletonHeaders =
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
    private static readonly IReadOnlySet<string> SingletonMimeHeaders = new HashSet<string>(
        [
            "MIME-Version", "Content-Type", "Content-Transfer-Encoding",
            "Content-Disposition", "Content-ID", "Content-Description",
            "Content-Language", "Content-Location",
        ],
        StringComparer.OrdinalIgnoreCase);
    private static readonly IReadOnlyDictionary<string, string> ResentHeaderProperties =
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

    public string Name => "EmailSubmission/set";
    public string Capability => JmapConstants.SubmissionCapability;

    public async Task<JmapMethodResponse> InvokeAsync(
        JmapInvocationContext context,
        JsonObject arguments,
        CancellationToken cancellationToken)
    {
        if (!JmapMethodHelpers.HasOnlyProperties(
                arguments,
                "accountId",
                "ifInState",
                "create",
                "update",
                "destroy",
                "onSuccessUpdateEmail",
                "onSuccessDestroyEmail")
            || !JmapMethodHelpers.TryGetRequiredString(arguments, "accountId", out var accountId)
            || !JmapMethodHelpers.TryGetOptionalString(arguments, "ifInState", out var ifInState)
            || !JmapEmailMutationHelpers.TryGetObjectMap(arguments, "create", false, out var create)
            || !JmapEmailMutationHelpers.TryGetObjectMap(arguments, "update", false, out var update)
            || !TryStringArray(arguments, "destroy", out var destroy)
            || !JmapEmailMutationHelpers.TryGetObjectMap(
                arguments,
                "onSuccessUpdateEmail",
                false,
                out var onSuccessUpdate)
            || !TryStringArray(arguments, "onSuccessDestroyEmail", out var onSuccessDestroy)
            || !JmapMethodHelpers.AreValidCreationIds(create?.Keys)
            || !JmapMethodHelpers.AreValidIdReferences(update?.Keys)
            || !JmapMethodHelpers.AreValidIdReferences(destroy)
            || !JmapMethodHelpers.AreValidIdReferences(onSuccessUpdate?.Keys)
            || !JmapMethodHelpers.AreValidIdReferences(onSuccessDestroy))
        {
            return JmapMethodResponse.Error("invalidArguments");
        }
        var operationCount = (create?.Count ?? 0) + (update?.Count ?? 0) + (destroy?.Count ?? 0);
        if (operationCount > environment.Jmap.MaxObjectsInSet)
            return JmapMethodResponse.Error("requestTooLarge");
        var account = await accounts.GetAccountAsync(context.User, accountId, cancellationToken);
        if (account is null) return JmapMethodResponse.Error("accountNotFound");
        await identities.EnsureDefaultAsync(account, cancellationToken);
        var oldState = await states.GetStateAsync(
            account.InboxId,
            JmapConstants.EmailSubmissionDataType,
            cancellationToken);
        if (ifInState is not null && !string.Equals(ifInState, oldState, StringComparison.Ordinal))
            return JmapMethodResponse.Error("stateMismatch");

        var created = new JsonObject();
        var updated = new JsonObject();
        var destroyed = new JsonArray();
        var notCreated = new JsonObject();
        var notUpdated = new JsonObject();
        var notDestroyed = new JsonObject();
        var successful = new Dictionary<string, string>(StringComparer.Ordinal);

        if (create is not null)
        {
            foreach (var item in create)
            {
                var result = await CreateAsync(
                    account,
                    context,
                    item.Key,
                    item.Value,
                    cancellationToken);
                if (result.Error is not null)
                {
                    notCreated[item.Key] = result.Error;
                    continue;
                }
                var submissionId = JmapId.Submission(result.Submission!.Id);
                context.CreatedIds[item.Key] = submissionId;
                created[item.Key] = new JsonObject { ["id"] = submissionId };
                successful[submissionId] = result.Submission.EmailId;
            }
        }

        if (update is not null)
        {
            foreach (var item in update)
            {
                var resolvedId = context.ResolveId(item.Key);
                if (!JmapId.TryParseSubmission(resolvedId, out var id))
                {
                    notUpdated[item.Key] = JmapMethodHelpers.SetError("notFound");
                    continue;
                }
                var submission = await database.JmapEmailSubmissions.SingleOrDefaultAsync(
                    candidate => candidate.Id == id && candidate.AccountId == account.InboxId,
                    cancellationToken);
                if (submission is null)
                {
                    notUpdated[item.Key] = JmapMethodHelpers.SetError("notFound");
                    continue;
                }
                var current = await JmapEmailSubmissionJson.BuildAsync(
                    database,
                    submission,
                    null,
                    cancellationToken);
                if (!JmapMethodHelpers.TryApplyPatchAllowingUnchangedProperties(
                        current,
                        item.Value,
                        UpdateProperties,
                        out var patched,
                        out var invalidProperties))
                {
                    notUpdated[item.Key] = JmapMethodHelpers.SetError("invalidPatch");
                    continue;
                }
                if (invalidProperties.Count > 0)
                {
                    notUpdated[item.Key] = JmapMethodHelpers.SetError(
                        "invalidProperties",
                        properties: invalidProperties);
                    continue;
                }
                if (!TryReadUndoStatus(patched, out var requestedStatus))
                {
                    notUpdated[item.Key] = JmapMethodHelpers.SetError(
                        "invalidProperties",
                        properties: ["undoStatus"]);
                    continue;
                }
                if (requestedStatus == "canceled" && submission.UndoStatus != "pending")
                {
                    notUpdated[item.Key] = JmapMethodHelpers.SetError("cannotUnsend");
                    continue;
                }
                if (requestedStatus != submission.UndoStatus)
                {
                    notUpdated[item.Key] = JmapMethodHelpers.SetError("cannotUnsend");
                    continue;
                }
                updated[resolvedId!] = null;
                successful[resolvedId!] = submission.EmailId;
            }
        }

        if (destroy is not null)
        {
            foreach (var requestedId in destroy.Distinct(StringComparer.Ordinal))
            {
                var resolvedId = context.ResolveId(requestedId);
                if (!JmapId.TryParseSubmission(resolvedId, out var id))
                {
                    notDestroyed[requestedId] = JmapMethodHelpers.SetError("notFound");
                    continue;
                }
                var submission = await database.JmapEmailSubmissions.SingleOrDefaultAsync(
                    candidate => candidate.Id == id && candidate.AccountId == account.InboxId,
                    cancellationToken);
                if (submission is null)
                {
                    notDestroyed[requestedId] = JmapMethodHelpers.SetError("notFound");
                    continue;
                }
                successful[resolvedId!] = submission.EmailId;
                database.JmapEmailSubmissions.Remove(submission);
                await database.SaveChangesAsync(cancellationToken);
                destroyed.Add(resolvedId);
            }
        }

        var response = new JsonObject
        {
            ["accountId"] = accountId,
            ["oldState"] = oldState,
            ["newState"] = await states.GetStateAsync(
                account.InboxId,
                JmapConstants.EmailSubmissionDataType,
                cancellationToken),
            ["created"] = created.Count == 0 ? null : created,
            ["updated"] = updated.Count == 0 ? null : updated,
            ["destroyed"] = destroyed.Count == 0 ? null : destroyed,
            ["notCreated"] = notCreated.Count == 0 ? null : notCreated,
            ["notUpdated"] = notUpdated.Count == 0 ? null : notUpdated,
            ["notDestroyed"] = notDestroyed.Count == 0 ? null : notDestroyed,
        };

        var implicitArguments = BuildImplicitEmailSet(
            accountId,
            context,
            successful,
            onSuccessUpdate,
            onSuccessDestroy);
        if (implicitArguments is null)
            return new JmapMethodResponse(Name, response);
        var implicitResponse = await emailSet.InvokeAsync(context, implicitArguments, cancellationToken);
        return new JmapMethodResponse(Name, response, [implicitResponse]);
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
            cancellationToken);
        var email = await database.Emails.AsNoTracking().SingleOrDefaultAsync(
            candidate => candidate.Id == emailId
                && candidate.Folder.InboxId == account.InboxId
                && !candidate.IsDeleted,
            cancellationToken);
        if (identity is null || email is null)
            return SubmissionCreateResult.Failed("invalidProperties");
        if (email.SizeBytes > environment.Limits.MaxMessageSizeBytes)
        {
            var error = JmapMethodHelpers.SetError("tooLarge");
            error["maxSize"] = environment.Limits.MaxMessageSizeBytes;
            return new SubmissionCreateResult(null, error);
        }

        var rawBytes = JmapEmailCodec.GetRawBytes(email);
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
        if (!await senderAuthorization.CanSendAsAsync(context.User.Username, sender, cancellationToken))
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
        foreach (var recipient in recipients)
        {
            var isLocal = await emailService.CanReceiveAsync(recipient, cancellationToken);
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
            RawMessage = deliveryMessage,
            AuthenticatedUser = context.User.Username,
            Direction = MailQueueDirections.Submission,
            State = MailQueueStates.Pending,
            ScanState = MailQueueScanStates.Pending,
            ReceivedAt = now,
            NextAttemptAt = now,
            SentCopyCreated = true,
        };
        foreach (var recipient in envelopeRecipients)
        {
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
        database.MailQueueMessages.Add(queue);
        database.JmapEmailSubmissions.Add(submission);
        await database.SaveChangesAsync(cancellationToken);
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
                || !MimeKit.Utils.DateUtils.TryParse(dateHeaders[0].Value, out _))
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

    private static Header[] Headers(HeaderList headers, string name) =>
        headers.Where(header => header.Field.Equals(name, StringComparison.OrdinalIgnoreCase))
            .ToArray();

    private static void ValidateAddressHeader(
        HeaderList headers,
        string headerName,
        string propertyName,
        bool allowEmpty,
        ISet<string> invalid)
    {
        var matching = Headers(headers, headerName);
        if (matching.Length == 1
            && !TryParseAddressList(matching[0].Value, allowEmpty, false, out _))
        {
            invalid.Add(propertyName);
        }
    }

    private static void ValidateResentHeaders(HeaderList headers, ISet<string> invalid)
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
            value => MimeKit.Utils.DateUtils.TryParse(value, out _),
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
        ISet<string> invalid)
    {
        if (headers.Any(header => header.Field.Equals(headerName, StringComparison.OrdinalIgnoreCase)
            && !isValid(header.Value)))
        {
            invalid.Add(ResentHeaderProperties[headerName]);
        }
    }

    private static void ValidateResentCardinality(
        IReadOnlyList<Header> headers,
        ISet<string> invalid)
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

    private static bool CanPartitionResentBlocks(IReadOnlyList<Header> headers)
    {
        var reachable = new bool[headers.Count + 1];
        reachable[0] = true;
        for (var start = 0; start < headers.Count; start++)
        {
            if (!reachable[start])
                continue;
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Header? from = null;
            var hasDate = false;
            var hasSender = false;
            for (var end = start; end < headers.Count; end++)
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
        if (allowEmpty && IsCfwsOnly(value))
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

    private static bool IsCfwsOnly(string value)
    {
        var index = 0;
        while (index < value.Length)
        {
            var character = value[index++];
            if (character is ' ' or '\t' or '\r' or '\n')
                continue;
            if (character != '(')
                return false;

            var depth = 1;
            while (depth > 0)
            {
                if (index >= value.Length)
                    return false;
                character = value[index++];
                if (character == '\\')
                {
                    if (index >= value.Length)
                        return false;
                    index++;
                }
                else if (character == '(')
                {
                    depth++;
                }
                else if (character == ')')
                {
                    depth--;
                }
                else if (character < ' ' && character is not ('\t' or '\r' or '\n'))
                {
                    return false;
                }
            }
        }
        return true;
    }

    private static void ValidateMessageIdsHeader(
        HeaderList headers,
        string headerName,
        string propertyName,
        bool requireSingle,
        ISet<string> invalid)
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

    private static JsonObject? BuildImplicitEmailSet(
        string accountId,
        JmapInvocationContext context,
        IReadOnlyDictionary<string, string> successful,
        IReadOnlyDictionary<string, JsonObject>? updates,
        IReadOnlyList<string>? destroys)
    {
        var emailUpdates = new JsonObject();
        if (updates is not null)
        {
            foreach (var item in updates)
            {
                var submissionId = context.ResolveId(item.Key);
                if (submissionId is null || !successful.TryGetValue(submissionId, out var emailId))
                    continue;
                if (emailUpdates[emailId] is not JsonObject combined)
                {
                    combined = new JsonObject();
                    emailUpdates[emailId] = combined;
                }
                foreach (var patch in item.Value)
                    combined[patch.Key] = patch.Value?.DeepClone();
            }
        }
        var emailDestroys = new JsonArray();
        if (destroys is not null)
        {
            foreach (var item in destroys)
            {
                var submissionId = context.ResolveId(item);
                if (submissionId is not null
                    && successful.TryGetValue(submissionId, out var emailId)
                    && !emailDestroys.Any(node => node?.GetValue<string>() == emailId))
                    emailDestroys.Add(emailId);
            }
        }
        if (emailUpdates.Count == 0 && emailDestroys.Count == 0)
            return null;
        return new JsonObject
        {
            ["accountId"] = accountId,
            ["update"] = emailUpdates.Count == 0 ? null : emailUpdates,
            ["destroy"] = emailDestroys.Count == 0 ? null : emailDestroys,
        };
    }

    private static bool TryStringArray(
        JsonObject arguments,
        string name,
        out IReadOnlyList<string>? values)
    {
        values = null;
        if (!arguments.TryGetPropertyValue(name, out var node) || node is null) return true;
        if (node is not JsonArray array) return false;
        var result = new List<string>();
        foreach (var item in array)
        {
            if (item is not JsonValue value || !value.TryGetValue<string>(out var text) || text is null)
                return false;
            result.Add(text);
        }
        values = result;
        return true;
    }

    private sealed record SubmissionCreateResult(JmapEmailSubmissionDB? Submission, JsonObject? Error)
    {
        public static SubmissionCreateResult Failed(string type) =>
            new(null, JmapMethodHelpers.SetError(type));
    }
}
