using System.Text.Json.Nodes;
using mk8.email.Contracts.Messaging;

namespace mk8.email.Gateway.Protocols.Jmap;

internal static class GatewayMailboxChangesCodec
{
    private const long MaximumInteger = 9_007_199_254_740_991;

    internal sealed record Call(MailFolderChangesCommand Command, string AccountId);

    public static bool TryParse(JsonObject arguments, out Call? call, out string? failure)
    {
        call = null;
        failure = null;
        if (arguments.Any(property => property.Key is not ("accountId" or "sinceState" or "maxChanges"))
            || !TryGetString(arguments, "accountId", out var accountId)
            || !TryGetString(arguments, "sinceState", out var sinceState)
            || !TryGetMaxChanges(arguments, out var maxChanges))
        {
            failure = "invalidArguments";
            return false;
        }

        if (accountId!.Length != 33 || accountId[0] != 'A'
            || !Guid.TryParseExact(accountId.AsSpan(1), "N", out var accountGuid))
        {
            failure = "accountNotFound";
            return false;
        }

        call = new(new(accountGuid, sinceState!, maxChanges), accountId);
        return true;
    }

    public static (MailOperationKind Operation, JsonObject Data) Render(Call call, MailFolderChangesResult result)
    {
        if (result is null || !Enum.IsDefined(result.Status)
            || result.CreatedKeys is null || result.UpdatedKeys is null || result.DestroyedKeys is null)
            throw new InvalidOperationException("The Application returned an invalid folder changes result.");
        if (result.Status == MailFolderChangesStatus.AccountNotFound)
            return Error("accountNotFound");
        if (result.Status == MailFolderChangesStatus.CannotCalculateChanges)
            return Error("cannotCalculateChanges");
        if (result.OldState is null || result.NewState is null
            || !string.Equals(result.OldState, call.Command.SinceState, StringComparison.Ordinal)
            || !ValidKeys(result.CreatedKeys) || !ValidKeys(result.UpdatedKeys) || !ValidKeys(result.DestroyedKeys))
            throw new InvalidOperationException("The Application returned incomplete folder changes.");

        return (MailOperationKind.ReadFolderChanges, new JsonObject
        {
            ["accountId"] = call.AccountId,
            ["oldState"] = result.OldState,
            ["newState"] = result.NewState,
            ["hasMoreChanges"] = result.HasMoreChanges,
            ["created"] = ToArray(result.CreatedKeys),
            ["updated"] = ToArray(result.UpdatedKeys),
            ["destroyed"] = ToArray(result.DestroyedKeys),
            ["updatedProperties"] = null,
        });
    }

    private static bool TryGetString(JsonObject arguments, string key, out string? value)
    {
        value = null;
        return arguments[key] is JsonValue node && node.TryGetValue<string>(out value) && value is not null;
    }

    private static bool TryGetMaxChanges(JsonObject arguments, out long? value)
    {
        value = null;
        if (!arguments.TryGetPropertyValue("maxChanges", out var node) || node is null)
            return true;
        if (node is not JsonValue scalar)
            return false;
        long parsed;
        if (scalar.TryGetValue<long>(out parsed)) { }
        else if (scalar.TryGetValue<int>(out var signed)) parsed = signed;
        else if (scalar.TryGetValue<uint>(out var unsigned)) parsed = unsigned;
        else if (scalar.TryGetValue<ulong>(out var wide) && wide <= long.MaxValue) parsed = (long)wide;
        else return false;
        if (parsed is < 1 or > MaximumInteger)
            return false;
        value = parsed;
        return true;
    }

    private static bool ValidKeys(IReadOnlyList<string> keys) =>
        keys.All(key => key is not null && GatewayJmapBatchCodec.IsId(key));

    private static JsonArray ToArray(IReadOnlyList<string> values) => new(values.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());

    private static (MailOperationKind Operation, JsonObject Data) Error(string type) =>
        (MailOperationKind.Failure, new JsonObject { ["type"] = type });
}
