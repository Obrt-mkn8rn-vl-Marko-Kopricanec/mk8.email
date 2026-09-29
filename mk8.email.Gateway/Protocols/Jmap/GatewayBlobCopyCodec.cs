using System.Text.Json.Nodes;
using mk8.email.Contracts.Messaging;

namespace mk8.email.Gateway.Protocols.Jmap;

internal static class GatewayBlobCopyCodec
{
    internal sealed record Call(
        MailBlobCopyCommand Command,
        string FromAccountId,
        string AccountId,
        IReadOnlyList<string> RequestedBlobIds);

    public static bool TryParse(JsonObject arguments, int maximumObjects, out Call? call, out string? failure)
    {
        call = null;
        failure = null;
        if (arguments.Any(property => property.Key is not ("fromAccountId" or "accountId" or "blobIds"))
            || arguments["fromAccountId"] is not JsonValue fromNode
            || !fromNode.TryGetValue<string>(out var fromAccountId)
            || fromAccountId is null
            || arguments["accountId"] is not JsonValue accountNode
            || !accountNode.TryGetValue<string>(out var accountId)
            || accountId is null
            || string.Equals(fromAccountId, accountId, StringComparison.Ordinal)
            || !TryParseIds(arguments, out var blobIds))
        {
            failure = "invalidArguments";
            return false;
        }
        if (blobIds.Length > maximumObjects)
        {
            failure = "requestTooLarge";
            return false;
        }
        call = new(new(ParseAccountOrEmpty(fromAccountId), ParseAccountOrEmpty(accountId), blobIds),
            fromAccountId, accountId, blobIds);
        return true;
    }

    public static (MailOperationKind Operation, JsonObject Data) Render(Call call, MailBlobCopyResult result)
    {
        if (result is null || !Enum.IsDefined(result.Status) || result.Items is null)
            throw new InvalidOperationException("The Application returned an invalid blob copy result.");
        if (result.Status == MailBlobCopyStatus.RequestTooLarge)
            return Error("requestTooLarge");
        if (result.Status == MailBlobCopyStatus.FromAccountNotFound)
            return Error("fromAccountNotFound");
        if (result.Status == MailBlobCopyStatus.AccountNotFound)
            return Error("accountNotFound");

        var byId = new Dictionary<string, MailBlobCopyItemResult>(StringComparer.Ordinal);
        foreach (var item in result.Items)
        {
            if (item is null || item.BlobId is null || !Enum.IsDefined(item.Status)
                || !byId.TryAdd(item.BlobId, item))
                throw new InvalidOperationException("The Application returned invalid blob copy item data.");
        }
        var requested = call.RequestedBlobIds.Distinct(StringComparer.Ordinal).ToArray();
        if (byId.Count != requested.Length)
            throw new InvalidOperationException("The Application returned an incomplete blob copy result.");
        var copied = new JsonObject();
        var notCopied = new JsonObject();
        foreach (var id in requested)
        {
            if (!byId.TryGetValue(id, out var item))
                throw new InvalidOperationException("The Application omitted a requested blob copy result.");
            switch (item.Status)
            {
                case MailBlobCopyItemStatus.Copied when item.CopiedBlobId is not null
                    && GatewayJmapBatchCodec.IsId(item.CopiedBlobId):
                    copied[id] = item.CopiedBlobId;
                    break;
                case MailBlobCopyItemStatus.NotFound when item.CopiedBlobId is null:
                    notCopied[id] = new JsonObject { ["type"] = "notFound" };
                    break;
                case MailBlobCopyItemStatus.TooLarge when item.CopiedBlobId is null:
                    notCopied[id] = new JsonObject { ["type"] = "tooLarge" };
                    break;
                default:
                    throw new InvalidOperationException("The Application returned an inconsistent blob copy item.");
            }
        }
        return (MailOperationKind.CopyBinaryObjects, new JsonObject
        {
            ["fromAccountId"] = call.FromAccountId,
            ["accountId"] = call.AccountId,
            ["copied"] = copied.Count == 0 ? null : copied,
            ["notCopied"] = notCopied.Count == 0 ? null : notCopied,
        });
    }

    private static Guid ParseAccountOrEmpty(string value) =>
        value.Length == 33 && value[0] == 'A'
            && Guid.TryParseExact(value.AsSpan(1), "N", out var id)
                ? id
                : Guid.Empty;

    private static bool TryParseIds(JsonObject arguments, out string[] ids)
    {
        ids = [];
        if (arguments["blobIds"] is not JsonArray array)
            return false;
        var values = new List<string>(array.Count);
        foreach (var item in array)
        {
            if (item is not JsonValue value || !value.TryGetValue<string>(out var id)
                || id is null || !GatewayJmapBatchCodec.IsId(id))
                return false;
            values.Add(id);
        }
        ids = values.ToArray();
        return true;
    }

    private static (MailOperationKind Operation, JsonObject Data) Error(string type) =>
        (MailOperationKind.Failure, new JsonObject { ["type"] = type });
}
