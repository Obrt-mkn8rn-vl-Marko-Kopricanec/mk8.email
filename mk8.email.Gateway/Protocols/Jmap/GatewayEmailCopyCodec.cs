using System.Text.Json.Nodes;
using mk8.email.Contracts.Messaging;

namespace mk8.email.Gateway.Protocols.Jmap;

internal static class GatewayEmailCopyCodec
{
    internal sealed record Call(MailCopyCommand Command, string SourceAccountId, string TargetAccountId);

    public static bool TryParse(JsonObject arguments, IReadOnlyDictionary<string, string> knownEntities,
        int maximumObjects, out Call? call, out string? failure)
    {
        call = null;
        failure = null;
        if (arguments.Any(item => item.Key is not ("fromAccountId" or "accountId"
                or "ifFromInState" or "ifInState" or "create" or "onSuccessDestroyOriginal"
                or "destroyFromIfInState"))
            || arguments["fromAccountId"] is not JsonValue sourceNode
            || !sourceNode.TryGetValue<string>(out var sourceAccountId) || sourceAccountId is null
            || arguments["accountId"] is not JsonValue targetNode
            || !targetNode.TryGetValue<string>(out var targetAccountId) || targetAccountId is null
            || string.Equals(sourceAccountId, targetAccountId, StringComparison.Ordinal)
            || !GatewayEmailImportCodec.TryOptionalState(arguments, "ifFromInState", out var ifFromInState)
            || !GatewayEmailImportCodec.TryOptionalState(arguments, "ifInState", out var ifInState)
            || !GatewayEmailImportCodec.TryOptionalState(arguments, "destroyFromIfInState", out var destroyFromIfInState)
            || !TryOptionalBoolean(arguments, "onSuccessDestroyOriginal", out var destroyOriginal)
            || arguments["create"] is not JsonObject creates
            || creates.Any(item => !GatewayJmapBatchCodec.IsId(item.Key) || item.Value is not JsonObject))
        {
            failure = "invalidArguments";
            return false;
        }
        if (creates.Count > maximumObjects)
        {
            failure = "requestTooLarge";
            return false;
        }
        var sourceGuid = ParseAccount(sourceAccountId);
        var targetGuid = ParseAccount(targetAccountId);
        var items = creates.Select(item => ParseItem(item.Key, (JsonObject)item.Value!, knownEntities)).ToArray();
        call = new(new(sourceGuid, targetGuid, ifFromInState, ifInState,
            destroyOriginal, destroyFromIfInState, items), sourceAccountId, targetAccountId);
        return true;
    }

    public static (MailOperationKind Operation, JsonObject Data) Render(Call call, MailCopyResult result)
    {
        if (result is null || !Enum.IsDefined(result.Status) || result.Items is null)
            throw new InvalidOperationException("The Application returned an invalid copy result.");
        if (result.Status == MailCopyStatus.SourceAccountNotFound) return Error("fromAccountNotFound");
        if (result.Status == MailCopyStatus.TargetAccountNotFound) return Error("accountNotFound");
        if (result.Status == MailCopyStatus.StateMismatch) return Error("stateMismatch");
        if (result.Status == MailCopyStatus.RequestTooLarge) return Error("requestTooLarge");
        ValidateSuccess(call, result);
        var created = new JsonObject();
        var notCreated = new JsonObject();
        foreach (var outcome in result.Items)
        {
            if (outcome.Error != MailCopyItemError.None)
            {
                if (outcome.EmailId is not null || outcome.StoredThreadId is not null || outcome.Size is not null)
                    throw new InvalidOperationException("The Application returned an inconsistent copy failure.");
                notCreated[outcome.CreationId] = ItemError(outcome.Error);
                continue;
            }
            if (outcome.EmailId is null || outcome.EmailId == Guid.Empty
                || string.IsNullOrEmpty(outcome.StoredThreadId) || outcome.Size is null or < 0)
                throw new InvalidOperationException("The Application returned an incomplete copied message.");
            var emailId = outcome.EmailId.Value;
            created[outcome.CreationId] = new JsonObject
            {
                ["id"] = $"E{emailId:N}",
                ["blobId"] = $"B{emailId:N}",
                ["threadId"] = GatewayEmailImportCodec.FormatThreadId(outcome.StoredThreadId),
                ["size"] = outcome.Size.Value,
            };
        }
        return (MailOperationKind.CopyMessages, new JsonObject
        {
            ["fromAccountId"] = call.SourceAccountId,
            ["accountId"] = call.TargetAccountId,
            ["oldState"] = result.OldTargetState,
            ["newState"] = result.NewTargetState,
            ["created"] = created.Count == 0 ? null : created,
            ["notCreated"] = notCreated.Count == 0 ? null : notCreated,
        });
    }

    public static IReadOnlyList<(MailOperationKind Operation, JsonObject Data)> RenderAdditional(
        Call call, MailCopyResult result)
    {
        if (result.Status != MailCopyStatus.Ok) return [];
        ValidateSuccess(call, result);
        if (result.Destroy is null) return [];
        if (!Enum.IsDefined(result.Destroy.Status)
            || result.Destroy.Destroyed is null || result.Destroy.NotFound is null)
            throw new InvalidOperationException("The Application returned an invalid source-deletion result.");
        if (result.Destroy.Status == MailCopyDestroyStatus.StateMismatch)
        {
            if (result.Destroy.Destroyed.Count != 0 || result.Destroy.NotFound.Count != 0)
                throw new InvalidOperationException("The Application returned inconsistent source deletion.");
            return [Error("stateMismatch")];
        }
        if (result.Destroy.Status != MailCopyDestroyStatus.Completed
            || result.Destroy.OldState is null || result.Destroy.NewState is null)
            throw new InvalidOperationException("The Application returned an incomplete source-deletion result.");
        var notDestroyed = new JsonObject();
        foreach (var id in result.Destroy.NotFound)
            notDestroyed[$"E{id:N}"] = new JsonObject { ["type"] = "notFound" };
        var response = new JsonObject
        {
            ["accountId"] = call.SourceAccountId,
            ["oldState"] = result.Destroy.OldState,
            ["newState"] = result.Destroy.NewState,
            ["created"] = null,
            ["updated"] = null,
            ["destroyed"] = result.Destroy.Destroyed.Count == 0 ? null
                : new JsonArray(result.Destroy.Destroyed.Select(id => (JsonNode?)JsonValue.Create($"E{id:N}")).ToArray()),
            ["notCreated"] = null,
            ["notUpdated"] = null,
            ["notDestroyed"] = notDestroyed.Count == 0 ? null : notDestroyed,
        };
        return [(MailOperationKind.MutateMessages, response)];
    }

    private static MailCopyItem ParseItem(string creationId, JsonObject value,
        IReadOnlyDictionary<string, string> knownEntities)
    {
        var sourceReference = value["id"] is JsonValue idNode
            && idNode.TryGetValue<string>(out var rawId)
                ? GatewayEmailImportCodec.Resolve(rawId, knownEntities) : null;
        var sourceId = sourceReference is { Length: 33 } && sourceReference[0] == 'E'
            && Guid.TryParseExact(sourceReference.AsSpan(1), "N", out var parsed)
                ? parsed : (Guid?)null;
        var initialInvalid = value.Any(item => item.Key is not ("id" or "mailboxIds" or "keywords" or "receivedAt"))
            || sourceId is null;
        var (mailboxId, mailboxIssue) = GatewayEmailImportCodec.ParseMailbox(value["mailboxIds"], knownEntities);
        IReadOnlyList<string>? keywords = null;
        var keywordIssue = MailMessageKeywordIssue.None;
        if (value.ContainsKey("keywords"))
            (keywords, keywordIssue) = GatewayEmailImportCodec.ParseKeywords(value);
        var receivedAt = GatewayEmailImportCodec.ParseReceivedAt(value, out var invalidReceivedAt);
        return new(creationId, sourceId, initialInvalid, mailboxId, mailboxIssue,
            keywords, keywordIssue, receivedAt, invalidReceivedAt);
    }

    private static void ValidateSuccess(Call call, MailCopyResult result)
    {
        if (result.Status != MailCopyStatus.Ok || result.OldTargetState is null
            || result.NewTargetState is null || result.Items.Count != call.Command.Items.Count)
            throw new InvalidOperationException("The Application returned an incomplete copy result.");
        var copiedSources = new HashSet<Guid>();
        for (var index = 0; index < result.Items.Count; index++)
        {
            var outcome = result.Items[index];
            if (outcome is null || !Enum.IsDefined(outcome.Error)
                || !string.Equals(outcome.CreationId, call.Command.Items[index].CreationId, StringComparison.Ordinal))
                throw new InvalidOperationException("The Application returned inconsistent copy items.");
            if (outcome.Error == MailCopyItemError.None)
            {
                if (call.Command.Items[index].SourceEmailId is not { } sourceId)
                    throw new InvalidOperationException("The Application copied an invalid source message.");
                copiedSources.Add(sourceId);
            }
        }
        var expectedDestroy = call.Command.DestroyOriginal && copiedSources.Count > 0;
        if (expectedDestroy != (result.Destroy is not null))
            throw new InvalidOperationException("The Application returned inconsistent source deletion.");
        if (result.Destroy is { Status: MailCopyDestroyStatus.Completed } destroyed)
        {
            var reported = destroyed.Destroyed.Concat(destroyed.NotFound).ToArray();
            if (reported.Length != copiedSources.Count || reported.Distinct().Count() != reported.Length
                || reported.Any(id => !copiedSources.Contains(id)))
                throw new InvalidOperationException("The Application reported unrelated source deletion.");
        }
    }

    private static Guid ParseAccount(string accountId) =>
        accountId.Length == 33 && accountId[0] == 'A'
            && Guid.TryParseExact(accountId.AsSpan(1), "N", out var id) ? id : Guid.Empty;

    private static bool TryOptionalBoolean(JsonObject arguments, string name, out bool value)
    {
        value = false;
        return !arguments.TryGetPropertyValue(name, out var node)
            || node is JsonValue json && json.TryGetValue(out value);
    }

    private static JsonObject ItemError(MailCopyItemError error)
    {
        var (type, properties) = error switch
        {
            MailCopyItemError.InvalidProperties => ("invalidProperties", Array.Empty<string>()),
            MailCopyItemError.NotFound => ("notFound", Array.Empty<string>()),
            MailCopyItemError.InvalidMailbox => ("invalidProperties", ["mailboxIds"]),
            MailCopyItemError.TooManyMailboxes => ("tooManyMailboxes", Array.Empty<string>()),
            MailCopyItemError.InvalidKeywords => ("invalidProperties", Array.Empty<string>()),
            MailCopyItemError.TooManyKeywords => ("tooManyKeywords", Array.Empty<string>()),
            MailCopyItemError.InvalidReceivedAt => ("invalidProperties", ["receivedAt"]),
            MailCopyItemError.TooLarge => ("tooLarge", Array.Empty<string>()),
            MailCopyItemError.OverQuota => ("overQuota", Array.Empty<string>()),
            MailCopyItemError.InvalidEmail => ("invalidEmail", Array.Empty<string>()),
            _ => throw new InvalidOperationException("The Application returned an invalid copy error."),
        };
        var response = new JsonObject { ["type"] = type };
        if (properties.Length > 0)
            response["properties"] = new JsonArray(properties.Select(property => (JsonNode?)JsonValue.Create(property)).ToArray());
        return response;
    }

    private static (MailOperationKind Operation, JsonObject Data) Error(string type) =>
        (MailOperationKind.Failure, new JsonObject { ["type"] = type });
}
