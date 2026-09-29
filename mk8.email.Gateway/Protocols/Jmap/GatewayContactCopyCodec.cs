using System.Text.Json.Nodes;
using mk8.email.Contracts.Messaging;

namespace mk8.email.Gateway.Protocols.Jmap;

internal static class GatewayContactCopyCodec
{
    internal sealed record Call(
        MailContactCopyCommand Command,
        string SourceAccountId,
        string TargetAccountId);

    public static bool TryParse(JsonObject arguments, int maximumObjects,
        out Call? call, out string? failure)
    {
        call = null;
        failure = null;
        if (arguments.Any(item => item.Key is not (
                "fromAccountId" or "ifFromInState" or "accountId" or "ifInState"
                or "create" or "onSuccessDestroyOriginal" or "destroyFromIfInState"))
            || !TryRequiredString(arguments, "fromAccountId", out var sourceAccountId)
            || !TryRequiredString(arguments, "accountId", out var targetAccountId)
            || string.Equals(sourceAccountId, targetAccountId, StringComparison.Ordinal)
            || !TryOptionalString(arguments, "ifFromInState", out var ifFromInState)
            || !TryOptionalString(arguments, "ifInState", out var ifInState)
            || !TryOptionalString(arguments, "destroyFromIfInState", out _)
            || !TryOptionalBoolean(arguments, "onSuccessDestroyOriginal")
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
        var sourceParseable = TryParseAccountId(sourceAccountId!, out var sourceId);
        var targetParseable = TryParseAccountId(targetAccountId!, out var targetId);
        var sourceEligible = sourceParseable && string.Equals(sourceAccountId, $"A{sourceId:N}", StringComparison.Ordinal);
        var targetEligible = targetParseable && string.Equals(targetAccountId, $"A{targetId:N}", StringComparison.Ordinal);
        call = new(new(sourceId, sourceParseable, sourceEligible, targetId, targetParseable, targetEligible,
                ifFromInState, ifInState, creates.Select(item => item.Key).ToArray()),
            sourceAccountId!, targetAccountId!);
        return true;
    }

    public static (MailOperationKind Operation, JsonObject Data) Render(Call call, MailContactCopyResult result)
    {
        if (result is null || !Enum.IsDefined(result.Status))
            throw new InvalidOperationException("The Application returned invalid contact-copy data.");
        if (result.Status != MailContactCopyStatus.Ok)
        {
            var error = result.Status switch
            {
                MailContactCopyStatus.FromAccountNotFound => "fromAccountNotFound",
                MailContactCopyStatus.FromAccountNotSupported => "fromAccountNotSupportedByMethod",
                MailContactCopyStatus.AccountNotFound => "accountNotFound",
                MailContactCopyStatus.AccountNotSupported => "accountNotSupportedByMethod",
                MailContactCopyStatus.StateMismatch => "stateMismatch",
                _ => throw new InvalidOperationException("The Application returned an unknown contact-copy status."),
            };
            return (MailOperationKind.Failure, new JsonObject { ["type"] = error });
        }
        if (result.State is null)
            throw new InvalidOperationException("The Application returned no contact-copy state.");
        var notCreated = new JsonObject();
        foreach (var creationId in call.Command.CreationIds)
            notCreated[creationId] = new JsonObject
            {
                ["type"] = "forbidden",
                ["description"] = "No second JMAP Contacts account is available for copying.",
            };
        return (MailOperationKind.CopyContacts, new JsonObject
        {
            ["fromAccountId"] = call.SourceAccountId,
            ["accountId"] = call.TargetAccountId,
            ["oldState"] = result.State,
            ["newState"] = result.State,
            ["created"] = null,
            ["notCreated"] = notCreated,
        });
    }

    private static bool TryRequiredString(JsonObject arguments, string key, out string? value)
    {
        value = null;
        return arguments[key] is JsonValue node && node.TryGetValue(out value) && value is not null;
    }

    private static bool TryOptionalString(JsonObject arguments, string key, out string? value)
    {
        value = null;
        if (!arguments.TryGetPropertyValue(key, out var node) || node is null) return true;
        return node is JsonValue scalar && scalar.TryGetValue(out value);
    }

    private static bool TryOptionalBoolean(JsonObject arguments, string key)
    {
        if (!arguments.TryGetPropertyValue(key, out var node)) return true;
        return node is JsonValue scalar && scalar.TryGetValue<bool>(out _);
    }

    private static bool TryParseAccountId(string value, out Guid id)
    {
        id = Guid.Empty;
        return value.Length == 33 && value[0] == 'A' && Guid.TryParseExact(value.AsSpan(1), "N", out id);
    }
}
