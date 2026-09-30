using System.Text.Json.Nodes;
using mk8.email.Contracts.Messaging;

namespace mk8.email.Gateway.Protocols.Jmap;

internal static class GatewaySubmissionSetCodec
{
    internal sealed record Call(MailSubmissionMutationCommand Command, string AccountId,
        IReadOnlyDictionary<string, JsonObject> CreateRequests);

    public static bool TryParse(JsonObject arguments, int maximumObjects,
        out Call? call, out string? failure)
    {
        call = null;
        failure = null;
        if (arguments.Any(item => item.Key is not ("accountId" or "ifInState" or "create" or "update"
                or "destroy" or "onSuccessUpdateEmail" or "onSuccessDestroyEmail"))
            || arguments["accountId"] is not JsonValue accountNode
            || !accountNode.TryGetValue<string>(out var accountId) || accountId is null
            || !TryOptionalString(arguments, "ifInState", out var ifInState)
            || !TryMap(arguments, "create", creation: true, out var creates)
            || !TryMap(arguments, "update", creation: false, out var updates)
            || !TryStrings(arguments, "destroy", out var destroys)
            || !TryMap(arguments, "onSuccessUpdateEmail", creation: false, out var onSuccessUpdates)
            || !TryStrings(arguments, "onSuccessDestroyEmail", out var onSuccessDestroys))
        {
            failure = "invalidArguments";
            return false;
        }
        if (creates.Count + updates.Count + destroys.Count > maximumObjects)
        {
            failure = "requestTooLarge";
            return false;
        }
        var parsedAccount = accountId.Length == 33 && accountId[0] == 'A'
            && Guid.TryParseExact(accountId.AsSpan(1), "N", out var id) ? id : Guid.Empty;
        call = new(new(parsedAccount, ifInState,
            creates.Select(item => new MailSubmissionCreate(item.Key, GatewaySubmissionMutationCodec.Draft(item.Value))).ToArray(),
            updates.Select(item => new MailSubmissionUpdate(item.Key, GatewaySubmissionMutationCodec.Patch(item.Value))).ToArray(),
            destroys.Distinct(StringComparer.Ordinal).Select(value => new MailSubmissionDestroy(value)).ToArray(),
            onSuccessUpdates.Select(item => new MailSubmissionEmailUpdate(item.Key,
                GatewayEmailPatchCodec.ParseFragments(item.Value))).ToArray(),
            onSuccessDestroys.Select(value => new MailSubmissionEmailDestroy(value)).ToArray()),
            accountId, creates);
        return true;
    }

    public static (MailOperationKind Operation, JsonObject Data) Render(Call call, MailSubmissionMutationResult result)
    {
        if (result is null || !Enum.IsDefined(result.Status)
            || result.Created is null || result.Updated is null || result.Destroyed is null)
            throw new InvalidOperationException("The Application returned an invalid submission mutation.");
        if (result.Status != MailSubmissionMutationStatus.Ok
            && (result.OldState is not null || result.NewState is not null
                || result.Created.Count != 0 || result.Updated.Count != 0 || result.Destroyed.Count != 0
                || result.ImplicitMessageCommand is not null || result.ImplicitMessageResult is not null))
            throw new InvalidOperationException("The Application returned an inconsistent submission failure.");
        if (result.Status == MailSubmissionMutationStatus.AccountNotFound) return Error("accountNotFound");
        if (result.Status == MailSubmissionMutationStatus.StateMismatch) return Error("stateMismatch");
        if (result.Status != MailSubmissionMutationStatus.Ok || result.OldState is null || result.NewState is null
            || result.Created.Count != call.Command.Creates.Count
            || result.Updated.Count != call.Command.Updates.Count
            || result.Destroyed.Count != call.Command.Destroys.Count)
            throw new InvalidOperationException("The Application returned incomplete submission outcomes.");
        var (created, notCreated) = RenderCreates(call, result.Created);
        var (updated, notUpdated) = RenderUpdates(call, result.Updated);
        var (destroyed, notDestroyed) = RenderDestroys(call, result.Destroyed);
        return (MailOperationKind.MutateSubmissions, new JsonObject
        {
            ["accountId"] = call.AccountId,
            ["oldState"] = result.OldState,
            ["newState"] = result.NewState,
            ["created"] = created.Count == 0 ? null : created,
            ["updated"] = updated.Count == 0 ? null : updated,
            ["destroyed"] = destroyed.Count == 0 ? null : destroyed,
            ["notCreated"] = notCreated.Count == 0 ? null : notCreated,
            ["notUpdated"] = notUpdated.Count == 0 ? null : notUpdated,
            ["notDestroyed"] = notDestroyed.Count == 0 ? null : notDestroyed,
        });
    }

    public static IReadOnlyList<(MailOperationKind Operation, JsonObject Data)> RenderAdditional(
        Call call, MailSubmissionMutationResult result)
    {
        if (result.ImplicitMessageCommand is null && result.ImplicitMessageResult is null) return [];
        var command = result.ImplicitMessageCommand;
        if (result.Status != MailSubmissionMutationStatus.Ok || command is null
            || result.ImplicitMessageResult is null
            || command.AccountId != call.Command.AccountId || command.IfInState is not null
            || command.Creates is null || command.Creates.Count != 0
            || command.Updates is null || command.Destroys is null)
            throw new InvalidOperationException("The Application returned an invalid implicit message mutation.");
        return [GatewayEmailSetCodec.Render(new GatewayEmailSetCodec.Call(command, call.AccountId),
            result.ImplicitMessageResult)];
    }

    private static (JsonObject Created, JsonObject NotCreated) RenderCreates(
        Call call, IReadOnlyList<MailSubmissionCreateOutcome> outcomes)
    {
        var created = new JsonObject();
        var notCreated = new JsonObject();
        for (var index = 0; index < outcomes.Count; index++)
        {
            var item = outcomes[index];
            if (item is null || !string.Equals(item.CreationId, call.Command.Creates[index].CreationId,
                    StringComparison.Ordinal)
                || (item.Submission is null) == (item.Failure is null))
                throw new InvalidOperationException("The Application returned an inconsistent submission creation.");
            if (item.Failure is not null)
            {
                notCreated[item.CreationId] = RenderFailure(item.Failure);
                continue;
            }
            var submission = item.Submission!;
            if (submission.Id == Guid.Empty || submission.IdentityId is null || submission.EmailId is null
                || submission.ThreadId is null || submission.EnvelopeSender is null
                || submission.EnvelopeRecipients is null || submission.DeliveryRecipients is null)
                throw new InvalidOperationException("The Application returned an incomplete created submission.");
            var response = GatewaySubmissionGetCodec.Build(submission, null);
            response.Remove("identityId");
            response.Remove("emailId");
            if (call.CreateRequests[item.CreationId]["envelope"] is { } requestedEnvelope
                && JsonNode.DeepEquals(requestedEnvelope, response["envelope"]))
                response.Remove("envelope");
            created[item.CreationId] = response;
        }
        return (created, notCreated);
    }

    private static (JsonObject Updated, JsonObject NotUpdated) RenderUpdates(
        Call call, IReadOnlyList<MailSubmissionUpdateOutcome> outcomes)
    {
        var updated = new JsonObject();
        var notUpdated = new JsonObject();
        for (var index = 0; index < outcomes.Count; index++)
        {
            var item = outcomes[index];
            if (item is null || !string.Equals(item.RequestedId, call.Command.Updates[index].RequestedId,
                    StringComparison.Ordinal)
                || (item.SubmissionId is null) == (item.Failure is null)
                || item.SubmissionId == Guid.Empty)
                throw new InvalidOperationException("The Application returned an inconsistent submission update.");
            if (item.Failure is not null) notUpdated[item.RequestedId] = RenderFailure(item.Failure);
            else updated[FormatId(item.SubmissionId!.Value)] = null;
        }
        return (updated, notUpdated);
    }

    private static (JsonArray Destroyed, JsonObject NotDestroyed) RenderDestroys(
        Call call, IReadOnlyList<MailSubmissionDestroyOutcome> outcomes)
    {
        var destroyed = new JsonArray();
        var notDestroyed = new JsonObject();
        for (var index = 0; index < outcomes.Count; index++)
        {
            var item = outcomes[index];
            if (item is null || !string.Equals(item.RequestedId, call.Command.Destroys[index].RequestedId,
                    StringComparison.Ordinal)
                || (item.SubmissionId is null) == (item.Failure is null)
                || item.SubmissionId == Guid.Empty)
                throw new InvalidOperationException("The Application returned an inconsistent submission deletion.");
            if (item.Failure is not null) notDestroyed[item.RequestedId] = RenderFailure(item.Failure);
            else destroyed.Add(FormatId(item.SubmissionId!.Value));
        }
        return (destroyed, notDestroyed);
    }

    private static JsonObject RenderFailure(MailSubmissionMutationFailure failure)
    {
        if (failure is null || !Enum.IsDefined(failure.Error)
            || failure.Error == MailSubmissionMutationError.None)
            throw new InvalidOperationException("The Application returned an invalid submission failure.");
        var type = failure.Error switch
        {
            MailSubmissionMutationError.InvalidProperties => "invalidProperties",
            MailSubmissionMutationError.InvalidPatch => "invalidPatch",
            MailSubmissionMutationError.NotFound => "notFound",
            MailSubmissionMutationError.CannotUnsend => "cannotUnsend",
            MailSubmissionMutationError.InvalidEmail => "invalidEmail",
            MailSubmissionMutationError.ForbiddenFrom => "forbiddenFrom",
            MailSubmissionMutationError.ForbiddenMailFrom => "forbiddenMailFrom",
            MailSubmissionMutationError.NoRecipients => "noRecipients",
            MailSubmissionMutationError.TooManyRecipients => "tooManyRecipients",
            MailSubmissionMutationError.InvalidRecipients => "invalidRecipients",
            MailSubmissionMutationError.TooLarge => "tooLarge",
            _ => throw new InvalidOperationException("The Application returned an unknown submission failure."),
        };
        var error = new JsonObject { ["type"] = type };
        if (!string.IsNullOrWhiteSpace(failure.Description)) error["description"] = failure.Description;
        if (failure.Properties is not null) error["properties"] = ToArray(failure.Properties);
        if (failure.EmailIssues is not null)
        {
            if (failure.Error != MailSubmissionMutationError.InvalidEmail || failure.Properties is not null)
                throw new InvalidOperationException("The Application returned inconsistent MIME issues.");
            error["properties"] = ToArray(failure.EmailIssues.Select(EmailIssue).Order(StringComparer.Ordinal));
        }
        if (failure.InvalidRecipients is not null) error["invalidRecipients"] = ToArray(failure.InvalidRecipients);
        if (failure.MaxSize is not null) error["maxSize"] = checked((int)failure.MaxSize.Value);
        if (failure.MaxRecipients is not null) error["maxRecipients"] = failure.MaxRecipients.Value;
        return error;
    }

    private static JsonArray ToArray(IEnumerable<string> values)
    {
        var result = new JsonArray();
        foreach (var value in values) result.Add(value);
        return result;
    }

    private static string EmailIssue(MailSubmissionEmailIssue issue) => issue switch
    {
        MailSubmissionEmailIssue.RawHeaders => "headers",
        MailSubmissionEmailIssue.BodyTree => "bodyStructure",
        MailSubmissionEmailIssue.HeaderDate => "sentAt",
        MailSubmissionEmailIssue.From => "from",
        MailSubmissionEmailIssue.Sender => "sender",
        MailSubmissionEmailIssue.ReplyTo => "replyTo",
        MailSubmissionEmailIssue.To => "to",
        MailSubmissionEmailIssue.Cc => "cc",
        MailSubmissionEmailIssue.Bcc => "bcc",
        MailSubmissionEmailIssue.MessageId => "messageId",
        MailSubmissionEmailIssue.InReplyTo => "inReplyTo",
        MailSubmissionEmailIssue.References => "references",
        MailSubmissionEmailIssue.Subject => "subject",
        MailSubmissionEmailIssue.ResentDate => "header:Resent-Date:asDate:all",
        MailSubmissionEmailIssue.ResentFrom => "header:Resent-From:asAddresses:all",
        MailSubmissionEmailIssue.ResentSender => "header:Resent-Sender:asAddresses:all",
        MailSubmissionEmailIssue.ResentTo => "header:Resent-To:asAddresses:all",
        MailSubmissionEmailIssue.ResentCc => "header:Resent-Cc:asAddresses:all",
        MailSubmissionEmailIssue.ResentBcc => "header:Resent-Bcc:asAddresses:all",
        MailSubmissionEmailIssue.ResentMessageId => "header:Resent-Message-ID:asMessageIds:all",
        MailSubmissionEmailIssue.ResentReplyTo => "header:Resent-Reply-To:asAddresses:all",
        _ => throw new InvalidOperationException("The Application returned an unknown MIME issue."),
    };

    private static bool TryMap(JsonObject arguments, string name, bool creation,
        out Dictionary<string, JsonObject> result)
    {
        result = new(StringComparer.Ordinal);
        if (!arguments.TryGetPropertyValue(name, out var node) || node is null) return true;
        if (node is not JsonObject map || map.Any(item => item.Value is not JsonObject
            || !IsReference(item.Key, creation))) return false;
        result = map.ToDictionary(item => item.Key, item => (JsonObject)item.Value!, StringComparer.Ordinal);
        return true;
    }

    private static bool TryStrings(JsonObject arguments, string name, out IReadOnlyList<string> result)
    {
        result = [];
        if (!arguments.TryGetPropertyValue(name, out var node) || node is null) return true;
        if (node is not JsonArray array || array.Any(item => item is not JsonValue value
            || !value.TryGetValue<string>(out var text) || text is null || !IsReference(text, creation: false)))
            return false;
        result = array.Select(item => item!.GetValue<string>()).ToArray();
        return true;
    }

    private static bool TryOptionalString(JsonObject arguments, string name, out string? value)
    {
        value = null;
        return !arguments.TryGetPropertyValue(name, out var node) || node is null
            || node is JsonValue scalar && scalar.TryGetValue<string>(out value);
    }

    private static bool IsReference(string value, bool creation) =>
        GatewayJmapBatchCodec.IsId(value)
        || !creation && value.Length > 1 && value[0] == '#'
            && GatewayJmapBatchCodec.IsId(value[1..]);

    private static string FormatId(Guid id) => $"S{id:N}";

    private static (MailOperationKind Operation, JsonObject Data) Error(string type) =>
        (MailOperationKind.Failure, new JsonObject { ["type"] = type });
}
