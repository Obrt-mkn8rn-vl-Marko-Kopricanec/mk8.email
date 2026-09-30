using System.Text.Json.Nodes;
using mk8.email.Contracts.Messaging;

namespace mk8.email.Gateway.Protocols.Jmap;

internal static class GatewayEmailSetCodec
{
    internal sealed record Call(MailMessageMutationCommand Command, string AccountId);

    public static bool TryParse(JsonObject arguments, int maximumObjects,
        out Call? call, out string? failure)
    {
        call = null;
        failure = null;
        if (arguments.Any(item => item.Key is not ("accountId" or "ifInState" or "create" or "update" or "destroy"))
            || arguments["accountId"] is not JsonValue accountNode
            || !accountNode.TryGetValue<string>(out var accountId) || accountId is null
            || !TryOptionalString(arguments, "ifInState", out var ifInState)
            || !TryMap(arguments, "create", creation: true, out var creates)
            || !TryMap(arguments, "update", creation: false, out var updates)
            || !TryDestroy(arguments, out var destroys))
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
            creates.Select(item => new MailMessageCreate(item.Key, ApplicationValueCodec.Encode(item.Value))).ToArray(),
            updates.Select(item => new MailMessageUpdate(item.Key, ApplicationValueCodec.Encode(item.Value))).ToArray(),
            destroys.Distinct(StringComparer.Ordinal).Select(id => new MailMessageDestroy(id)).ToArray()),
            accountId);
        return true;
    }

    public static (MailOperationKind Operation, JsonObject Data) Render(
        Call call, MailMessageMutationResult result)
    {
        if (result is null || !Enum.IsDefined(result.Status)
            || result.Created is null || result.Updated is null || result.Destroyed is null)
            throw new InvalidOperationException("The Application returned an invalid message mutation.");
        if (result.Status != MailMessageMutationStatus.Ok
            && (result.OldState is not null || result.NewState is not null
                || result.Created.Count != 0 || result.Updated.Count != 0 || result.Destroyed.Count != 0))
            throw new InvalidOperationException("The Application returned an inconsistent message mutation failure.");
        if (result.Status == MailMessageMutationStatus.AccountNotFound) return Error("accountNotFound");
        if (result.Status == MailMessageMutationStatus.StateMismatch) return Error("stateMismatch");
        if (result.Status != MailMessageMutationStatus.Ok || result.OldState is null || result.NewState is null
            || result.Created.Count != call.Command.Creates.Count
            || result.Updated.Count != call.Command.Updates.Count
            || result.Destroyed.Count != call.Command.Destroys.Count)
            throw new InvalidOperationException("The Application returned incomplete message mutation outcomes.");
        var (created, notCreated) = RenderCreates(call, result.Created);
        var (updated, notUpdated) = RenderUpdates(call, result.Updated);
        var (destroyed, notDestroyed) = RenderDestroys(call, result.Destroyed);
        return (MailOperationKind.MutateMessages, new JsonObject
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

    private static (JsonObject Created, JsonObject NotCreated) RenderCreates(
        Call call, IReadOnlyList<MailMessageCreateOutcome> outcomes)
    {
        var created = new JsonObject();
        var notCreated = new JsonObject();
        for (var index = 0; index < outcomes.Count; index++)
        {
            var item = outcomes[index];
            if (item is null || !string.Equals(item.CreationId, call.Command.Creates[index].CreationId,
                    StringComparison.Ordinal)
                || (item.Message is null) == (item.Failure is null))
                throw new InvalidOperationException("The Application returned an inconsistent message creation.");
            if (item.Failure is not null)
            {
                notCreated[item.CreationId] = RenderFailure(item.Failure);
                continue;
            }
            var message = item.Message!;
            if (message.Id == Guid.Empty || string.IsNullOrEmpty(message.StoredThreadId) || message.SizeBytes < 0)
                throw new InvalidOperationException("The Application returned an invalid created message.");
            created[item.CreationId] = new JsonObject
            {
                ["id"] = FormatId(message.Id),
                ["blobId"] = $"B{message.Id:N}",
                ["threadId"] = GatewayThreadGetCodec.FormatThreadId(message.StoredThreadId),
                ["size"] = message.SizeBytes,
            };
        }
        return (created, notCreated);
    }

    private static (JsonObject Updated, JsonObject NotUpdated) RenderUpdates(
        Call call, IReadOnlyList<MailMessageUpdateOutcome> outcomes)
    {
        var updated = new JsonObject();
        var notUpdated = new JsonObject();
        for (var index = 0; index < outcomes.Count; index++)
        {
            var item = outcomes[index];
            if (item is null || !string.Equals(item.RequestedId, call.Command.Updates[index].RequestedId,
                    StringComparison.Ordinal)
                || (item.MessageId is null) == (item.Failure is null)
                || item.MessageId == Guid.Empty)
                throw new InvalidOperationException("The Application returned an inconsistent message update.");
            if (item.Failure is not null) notUpdated[item.RequestedId] = RenderFailure(item.Failure);
            else updated[FormatId(item.MessageId!.Value)] = null;
        }
        return (updated, notUpdated);
    }

    private static (JsonArray Destroyed, JsonObject NotDestroyed) RenderDestroys(
        Call call, IReadOnlyList<MailMessageDestroyOutcome> outcomes)
    {
        var destroyed = new JsonArray();
        var notDestroyed = new JsonObject();
        for (var index = 0; index < outcomes.Count; index++)
        {
            var item = outcomes[index];
            if (item is null || !string.Equals(item.RequestedId, call.Command.Destroys[index].RequestedId,
                    StringComparison.Ordinal)
                || (item.MessageId is null) == (item.Failure is null)
                || item.MessageId == Guid.Empty)
                throw new InvalidOperationException("The Application returned an inconsistent message deletion.");
            if (item.Failure is not null) notDestroyed[item.RequestedId] = RenderFailure(item.Failure);
            else destroyed.Add(FormatId(item.MessageId!.Value));
        }
        return (destroyed, notDestroyed);
    }

    private static JsonObject RenderFailure(MailMessageMutationFailure failure)
    {
        if (failure is null || !Enum.IsDefined(failure.Error)
            || failure.Error == MailMessageMutationError.None)
            throw new InvalidOperationException("The Application returned an invalid message mutation failure.");
        var type = failure.Error switch
        {
            MailMessageMutationError.InvalidProperties => "invalidProperties",
            MailMessageMutationError.InvalidPatch => "invalidPatch",
            MailMessageMutationError.NotFound => "notFound",
            MailMessageMutationError.TooManyMailboxes => "tooManyMailboxes",
            MailMessageMutationError.BlobNotFound => "blobNotFound",
            MailMessageMutationError.TooManyKeywords => "tooManyKeywords",
            MailMessageMutationError.InvalidEmail => "invalidEmail",
            MailMessageMutationError.TooLarge => "tooLarge",
            MailMessageMutationError.OverQuota => "overQuota",
            _ => throw new InvalidOperationException("The Application returned an unknown message mutation failure."),
        };
        var error = new JsonObject { ["type"] = type };
        if (!string.IsNullOrWhiteSpace(failure.Description)) error["description"] = failure.Description;
        if (failure.Properties is not null) error["properties"] = ToArray(failure.Properties);
        if (failure.MissingBlobIds is not null) error["notFound"] = ToArray(failure.MissingBlobIds);
        return error;
    }

    private static JsonArray ToArray(IEnumerable<string> values)
    {
        var result = new JsonArray();
        foreach (var value in values) result.Add(value);
        return result;
    }

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

    private static bool TryDestroy(JsonObject arguments, out IReadOnlyList<string> values)
    {
        values = [];
        if (!arguments.TryGetPropertyValue("destroy", out var node) || node is null) return true;
        if (node is not JsonArray array || array.Any(item => item is not JsonValue scalar
            || !scalar.TryGetValue<string>(out var id) || id is null || !IsReference(id, creation: false)))
            return false;
        values = array.Select(item => item!.GetValue<string>()).ToArray();
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

    private static string FormatId(Guid id) => $"E{id:N}";

    private static (MailOperationKind Operation, JsonObject Data) Error(string type) =>
        (MailOperationKind.Failure, new JsonObject { ["type"] = type });
}
