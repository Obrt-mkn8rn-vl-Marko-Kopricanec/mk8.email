using System.Text.Json.Nodes;
using mk8.email.Contracts.Messaging;

namespace mk8.email.Gateway.Protocols.Jmap;

internal static class GatewayEmailReadCodec
{
    internal sealed record GetCall(MailMessageReadCommand Command, string AccountId,
        IReadOnlyList<string>? RequestedIds, int MaximumObjects);

    internal sealed record ParseCall(MailMessageParseCommand Command, string AccountId);

    public static bool TryParseGet(JsonObject arguments, int maximumObjects,
        out GetCall? call, out string? failure)
    {
        call = null;
        failure = null;
        if (!ValidProperties(arguments)
            || arguments["accountId"] is not JsonValue accountNode
            || !accountNode.TryGetValue<string>(out var accountId) || accountId is null
            || !TryIds(arguments, "ids", nullable: true, out var requestedIds)
            || !GatewayEmailProjectionCodec.TryParse(arguments, allowNullProperties: true,
                GatewayEmailProjectionCodec.GetDefaults, out var projection))
        {
            failure = "invalidArguments";
            return false;
        }
        if (requestedIds is { Count: > 0 } && requestedIds.Count > maximumObjects)
        {
            failure = "requestTooLarge";
            return false;
        }
        var ids = requestedIds?.Select(ParseEmail).Where(id => id != Guid.Empty)
            .Distinct().ToArray();
        call = new(new(ParseAccount(accountId), ids, projection), accountId,
            requestedIds?.Distinct(StringComparer.Ordinal).ToArray(), maximumObjects);
        return true;
    }

    public static bool TryParseParse(JsonObject arguments, int maximumObjects,
        out ParseCall? call, out string? failure)
    {
        call = null;
        failure = null;
        if (!ValidProperties(arguments, parse: true)
            || arguments["accountId"] is not JsonValue accountNode
            || !accountNode.TryGetValue<string>(out var accountId) || accountId is null
            || !TryIds(arguments, "blobIds", nullable: false, out var ids) || ids is null
            || !GatewayEmailProjectionCodec.TryParse(arguments, allowNullProperties: false,
                GatewayEmailProjectionCodec.ParseDefaults, out var projection))
        {
            failure = "invalidArguments";
            return false;
        }
        if (ids.Count > maximumObjects)
        {
            failure = "requestTooLarge";
            return false;
        }
        call = new(new(ParseAccount(accountId), ids.Distinct(StringComparer.Ordinal).ToArray(),
            projection), accountId);
        return true;
    }

    public static (MailOperationKind Operation, JsonObject Data) RenderGet(
        GetCall call, MailMessageReadResult result)
    {
        if (result is null || !Enum.IsDefined(result.Status) || result.Messages is null)
            throw new InvalidOperationException("The Application returned an invalid message read.");
        if (result.Status != MailMessageReadStatus.Ok
            && (result.State is not null || result.Messages.Count != 0))
            throw new InvalidOperationException("The Application returned an inconsistent message read failure.");
        if (result.Status == MailMessageReadStatus.AccountNotFound) return Error("accountNotFound");
        if (result.Status == MailMessageReadStatus.RequestTooLarge) return Error("requestTooLarge");
        if (result.Status != MailMessageReadStatus.Ok || result.State is null
            || result.Messages.Count > call.MaximumObjects)
            throw new InvalidOperationException("The Application returned an incomplete message read.");
        var requested = call.Command.MessageIds?.ToHashSet();
        var byId = new Dictionary<Guid, JsonObject>();
        foreach (var item in result.Messages)
        {
            if (item is null || item.MessageId == Guid.Empty || item.Value is null
                || requested is not null && !requested.Contains(item.MessageId)
                || ApplicationValueCodec.Decode(item.Value) is not JsonObject value
                || value["id"] is { } projectedId
                    && (projectedId is not JsonValue idNode
                        || !idNode.TryGetValue<string>(out var id)
                        || !string.Equals(id, FormatEmail(item.MessageId), StringComparison.Ordinal))
                || !byId.TryAdd(item.MessageId, value))
                throw new InvalidOperationException("The Application returned an unexpected message.");
        }
        var list = new JsonArray();
        var notFound = new JsonArray();
        foreach (var id in call.RequestedIds ?? result.Messages.Select(item => FormatEmail(item.MessageId)))
        {
            var parsed = ParseEmail(id);
            if (parsed == Guid.Empty || !string.Equals(id, FormatEmail(parsed), StringComparison.Ordinal)
                || !byId.TryGetValue(parsed, out var value))
                notFound.Add(id);
            else list.Add(value.DeepClone());
        }
        return (MailOperationKind.ReadMessages, new JsonObject
        {
            ["accountId"] = call.AccountId,
            ["state"] = result.State,
            ["list"] = list,
            ["notFound"] = notFound,
        });
    }

    public static (MailOperationKind Operation, JsonObject Data) RenderParse(
        ParseCall call, MailMessageParseResult result)
    {
        if (result is null || !Enum.IsDefined(result.Status) || result.Items is null)
            throw new InvalidOperationException("The Application returned an invalid message parse.");
        if (result.Status == MailMessageParseStatus.AccountNotFound)
        {
            if (result.Items.Count != 0)
                throw new InvalidOperationException("The Application returned an inconsistent parse failure.");
            return Error("accountNotFound");
        }
        if (result.Status != MailMessageParseStatus.Ok || result.Items.Count != call.Command.BlobIds.Count)
            throw new InvalidOperationException("The Application returned incomplete message parse outcomes.");
        var parsed = new JsonObject();
        var notParsable = new JsonArray();
        var notFound = new JsonArray();
        for (var index = 0; index < result.Items.Count; index++)
        {
            var item = result.Items[index];
            if (item is null || !Enum.IsDefined(item.Status)
                || !string.Equals(item.BlobId, call.Command.BlobIds[index], StringComparison.Ordinal)
                || (item.Status == MailMessageParseItemStatus.Parsed) != (item.Value is not null))
                throw new InvalidOperationException("The Application returned an inconsistent parse outcome.");
            switch (item.Status)
            {
                case MailMessageParseItemStatus.Parsed:
                    parsed[item.BlobId] = ApplicationValueCodec.Decode(item.Value!) as JsonObject
                        ?? throw new InvalidOperationException("The Application returned an invalid parsed message.");
                    break;
                case MailMessageParseItemStatus.NotParsable:
                    notParsable.Add(item.BlobId);
                    break;
                case MailMessageParseItemStatus.NotFound:
                    notFound.Add(item.BlobId);
                    break;
            }
        }
        return (MailOperationKind.ParseMessages, new JsonObject
        {
            ["accountId"] = call.AccountId,
            ["parsed"] = parsed.Count == 0 ? null : parsed,
            ["notParsable"] = notParsable.Count == 0 ? null : notParsable,
            ["notFound"] = notFound.Count == 0 ? null : notFound,
        });
    }

    private static bool ValidProperties(JsonObject arguments, bool parse = false) =>
        arguments.All(item => item.Key is "accountId" or "properties" or "bodyProperties"
            or "fetchTextBodyValues" or "fetchHTMLBodyValues" or "fetchAllBodyValues"
            or "maxBodyValueBytes" || string.Equals(item.Key, parse ? "blobIds" : "ids", StringComparison.Ordinal));

    private static bool TryIds(JsonObject arguments, string name, bool nullable,
        out IReadOnlyList<string>? values)
    {
        values = null;
        if (!arguments.TryGetPropertyValue(name, out var node) || node is null) return nullable;
        if (node is not JsonArray array || array.Any(item => item is not JsonValue scalar
            || !scalar.TryGetValue<string>(out var id) || id is null
            || !GatewayJmapBatchCodec.IsId(id))) return false;
        values = array.Select(item => item!.GetValue<string>()).ToArray();
        return true;
    }

    private static Guid ParseAccount(string value) => value.Length == 33 && value[0] == 'A'
        && Guid.TryParseExact(value.AsSpan(1), "N", out var id) ? id : Guid.Empty;

    private static Guid ParseEmail(string value) => value.Length == 33 && value[0] == 'E'
        && Guid.TryParseExact(value.AsSpan(1), "N", out var id) ? id : Guid.Empty;

    private static string FormatEmail(Guid id) => $"E{id:N}";

    private static (MailOperationKind Operation, JsonObject Data) Error(string type) =>
        (MailOperationKind.Failure, new JsonObject { ["type"] = type });
}
