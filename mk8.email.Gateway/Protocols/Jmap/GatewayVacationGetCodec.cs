using System.Collections.Frozen;
using System.Text.Json.Nodes;
using mk8.email.Contracts.Messaging;

namespace mk8.email.Gateway.Protocols.Jmap;

internal static class GatewayVacationGetCodec
{
    private static readonly HashSet<string> AllowedProperties = new(
        ["id", "isEnabled", "fromDate", "toDate", "subject", "textBody", "htmlBody"],
        StringComparer.Ordinal);

    internal sealed record Call(
        MailVacationReadCommand Command,
        string AccountId,
        IReadOnlyList<string>? RequestedIds,
        IReadOnlySet<string>? Properties);

    public static bool TryParse(JsonObject arguments, int maximumObjects, out Call? call, out string? failure)
    {
        call = null;
        failure = null;
        if (arguments.Any(property => property.Key is not ("accountId" or "ids" or "properties"))
            || arguments["accountId"] is not JsonValue accountNode
            || !accountNode.TryGetValue<string>(out var accountId)
            || accountId is null
            || !TryParseProperties(arguments, out var properties)
            || !TryParseIds(arguments, out var requestedIds))
        {
            failure = "invalidArguments";
            return false;
        }
        if (requestedIds?.Length > maximumObjects)
        {
            failure = "requestTooLarge";
            return false;
        }
        if (accountId.Length != 33 || accountId[0] != 'A'
            || !Guid.TryParseExact(accountId.AsSpan(1), "N", out var accountGuid))
        {
            failure = "accountNotFound";
            return false;
        }
        var includeSingleton = requestedIds is null || requestedIds.Contains("singleton", StringComparer.Ordinal);
        var includeBodies = includeSingleton
            && (properties is null || properties.Contains("textBody") || properties.Contains("htmlBody"));
        call = new(new(accountGuid, includeSingleton, includeBodies), accountId, requestedIds, properties);
        return true;
    }

    public static (MailOperationKind Operation, JsonObject Data) Render(Call call, MailVacationReadResult result)
    {
        if (result is null || !Enum.IsDefined(result.Status))
            throw new InvalidOperationException("The Application returned an invalid vacation read result.");
        if (result.Status == MailVacationReadStatus.AccountNotFound)
            return Error("accountNotFound");
        if (result.State is null || call.Command.IncludeSingleton != (result.Vacation is not null))
            throw new InvalidOperationException("The Application returned an incomplete vacation read result.");

        var list = new JsonArray();
        if (result.Vacation is not null)
            list.Add(Build(result.Vacation, call.Properties));
        var notFound = new JsonArray();
        if (call.RequestedIds is not null)
        {
            foreach (var id in call.RequestedIds.Where(id => !string.Equals(id, "singleton", StringComparison.Ordinal))
                         .Distinct(StringComparer.Ordinal))
                notFound.Add(id);
        }
        return (MailOperationKind.ReadVacationSettings, new JsonObject
        {
            ["accountId"] = call.AccountId,
            ["state"] = result.State,
            ["list"] = list,
            ["notFound"] = notFound,
        });
    }

    private static JsonObject Build(MailVacationSnapshot vacation, IReadOnlySet<string>? properties)
    {
        var result = new JsonObject { ["id"] = "singleton" };
        if (Wants("isEnabled")) result["isEnabled"] = vacation.IsEnabled;
        if (Wants("fromDate")) result["fromDate"] = FormatDate(vacation.FromDate);
        if (Wants("toDate")) result["toDate"] = FormatDate(vacation.ToDate);
        if (Wants("subject")) result["subject"] = vacation.Subject;
        if (Wants("textBody")) result["textBody"] = vacation.TextBody;
        if (Wants("htmlBody")) result["htmlBody"] = vacation.HtmlBody;
        return result;

        bool Wants(string name) => properties is null || properties.Contains(name);
    }

    private static string? FormatDate(DateTime? value) =>
        value is null ? null : GatewayJmapDateCodec.FormatUtc(value.Value);

    private static bool TryParseProperties(JsonObject arguments, out FrozenSet<string>? properties)
    {
        properties = null;
        if (!arguments.TryGetPropertyValue("properties", out var node) || node is null)
            return true;
        if (node is not JsonArray array)
            return false;
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in array)
        {
            if (item is not JsonValue value || !value.TryGetValue<string>(out var name)
                || name is null || !AllowedProperties.Contains(name))
                return false;
            names.Add(name);
        }
        properties = names.ToFrozenSet(StringComparer.Ordinal);
        return true;
    }

    private static bool TryParseIds(JsonObject arguments, out string[]? ids)
    {
        ids = null;
        if (!arguments.TryGetPropertyValue("ids", out var node) || node is null)
            return true;
        if (node is not JsonArray array)
            return false;
        var requested = new List<string>(array.Count);
        foreach (var item in array)
        {
            if (item is not JsonValue value || !value.TryGetValue<string>(out var id)
                || id is null || !GatewayJmapBatchCodec.IsId(id))
                return false;
            requested.Add(id);
        }
        ids = requested.ToArray();
        return true;
    }

    private static (MailOperationKind Operation, JsonObject Data) Error(string type) =>
        (MailOperationKind.Failure, new JsonObject { ["type"] = type });
}
