using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using mk8.email.Infrastructure.Data;
using mk8.email.Infrastructure.Environment;
using mk8.email.Infrastructure.Models;

namespace mk8.email.Jmap;

internal sealed class JmapVacationResponseService(EmailDbContext database)
{
    public Task SaveAsync(CancellationToken cancellationToken) =>
        database.SaveChangesAsync(cancellationToken);

    public async Task<JmapVacationResponseDB> GetOrCreateAsync(
        Guid accountId,
        CancellationToken cancellationToken)
    {
        var response = await database.JmapVacationResponses.SingleOrDefaultAsync(
            item => item.AccountId == accountId,
            cancellationToken);
        if (response is not null)
            return response;

        response = new JmapVacationResponseDB
        {
            AccountId = accountId,
            IsEnabled = false,
            UpdatedAt = DateTime.UtcNow,
        };
        database.JmapVacationResponses.Add(response);
        await database.SaveChangesAsync(cancellationToken);
        return response;
    }

    public static JsonObject ToJson(
        JmapVacationResponseDB response,
        IReadOnlySet<string>? properties = null)
    {
        var result = new JsonObject { ["id"] = "singleton" };
        if (Wants("isEnabled")) result["isEnabled"] = response.IsEnabled;
        if (Wants("fromDate")) result["fromDate"] = FormatDate(response.FromDate);
        if (Wants("toDate")) result["toDate"] = FormatDate(response.ToDate);
        if (Wants("subject")) result["subject"] = response.Subject;
        if (Wants("textBody")) result["textBody"] = response.TextBody;
        if (Wants("htmlBody")) result["htmlBody"] = response.HtmlBody;
        return result;

        bool Wants(string property) => properties is null || properties.Contains(property);
    }

    public static bool TryParse(
        JsonObject value,
        int maximumBodyBytes,
        out VacationValues parsed,
        out IReadOnlyList<string> invalidProperties)
    {
        parsed = default;
        var invalid = new List<string>();
        if (!TryBoolean(value, "isEnabled", out var isEnabled)) invalid.Add("isEnabled");
        if (!TryUtcDate(value, "fromDate", out var fromDate)) invalid.Add("fromDate");
        if (!TryUtcDate(value, "toDate", out var toDate)) invalid.Add("toDate");
        if (!TryNullableString(value, "subject", out var subject) || subject is { Length: > 998 })
            invalid.Add("subject");
        if (!TryNullableString(value, "textBody", out var textBody)
            || textBody is not null && System.Text.Encoding.UTF8.GetByteCount(textBody) > maximumBodyBytes)
            invalid.Add("textBody");
        if (!TryNullableString(value, "htmlBody", out var htmlBody)
            || htmlBody is not null && System.Text.Encoding.UTF8.GetByteCount(htmlBody) > maximumBodyBytes)
            invalid.Add("htmlBody");
        var combinedBodyBytes = System.Text.Encoding.UTF8.GetByteCount(textBody ?? string.Empty)
            + System.Text.Encoding.UTF8.GetByteCount(htmlBody ?? string.Empty);
        if (combinedBodyBytes > Math.Max(0, maximumBodyBytes - 16_384))
        {
            invalid.Add("textBody");
            invalid.Add("htmlBody");
        }
        if (fromDate is not null && toDate is not null && fromDate >= toDate)
        {
            invalid.Add("fromDate");
            invalid.Add("toDate");
        }
        invalidProperties = invalid.Distinct(StringComparer.Ordinal).ToArray();
        if (invalidProperties.Count > 0)
            return false;
        parsed = new VacationValues(isEnabled, fromDate, toDate, subject, textBody, htmlBody);
        return true;
    }

    private static string? FormatDate(DateTime? value) =>
        value is null ? null : JmapDate.FormatUtc(value.Value);

    private static bool TryBoolean(JsonObject value, string name, out bool result)
    {
        result = false;
        return value[name] is JsonValue node && node.TryGetValue(out result);
    }

    private static bool TryUtcDate(JsonObject value, string name, out DateTime? result)
    {
        result = null;
        if (!value.TryGetPropertyValue(name, out var node) || node is null)
            return true;
        if (node is not JsonValue jsonValue
            || !jsonValue.TryGetValue<string>(out var text)
            || text is null
            || !JmapDate.TryParseUtcDate(text, out var parsed))
            return false;
        result = parsed.UtcDateTime;
        return true;
    }

    private static bool TryNullableString(JsonObject value, string name, out string? result)
    {
        result = null;
        if (!value.TryGetPropertyValue(name, out var node) || node is null)
            return true;
        return node is JsonValue jsonValue && jsonValue.TryGetValue(out result);
    }

    internal readonly record struct VacationValues(
        bool IsEnabled,
        DateTime? FromDate,
        DateTime? ToDate,
        string? Subject,
        string? TextBody,
        string? HtmlBody);
}

internal sealed class VacationResponseGetMethod(
    JmapAccountService accounts,
    JmapVacationResponseService vacations,
    JmapStateService states) : IJmapMethod
{
    private static readonly IReadOnlySet<string> Properties = new HashSet<string>(
        ["id", "isEnabled", "fromDate", "toDate", "subject", "textBody", "htmlBody"],
        StringComparer.Ordinal);

    public string Name => "VacationResponse/get";
    public string Capability => JmapConstants.VacationResponseCapability;

    public async Task<JmapMethodResponse> InvokeAsync(
        JmapInvocationContext context,
        JsonObject arguments,
        CancellationToken cancellationToken)
    {
        if (!JmapMethodHelpers.HasOnlyProperties(arguments, "accountId", "ids", "properties")
            || !JmapMethodHelpers.TryGetRequiredString(arguments, "accountId", out var accountId)
            || !JmapMethodHelpers.TryGetStringArray(arguments, "properties", true, out var requestedProperties)
            || !JmapEmailArguments.TryGetIds(arguments, "ids", context, true, out var requestedIds))
            return JmapMethodResponse.Error("invalidArguments");
        var properties = requestedProperties?.ToHashSet(StringComparer.Ordinal);
        if (properties is not null && properties.Any(property => !Properties.Contains(property)))
            return JmapMethodResponse.Error("invalidArguments");
        var account = await accounts.GetAccountAsync(context.User, accountId, cancellationToken);
        if (account is null) return JmapMethodResponse.Error("accountNotFound");
        var response = await vacations.GetOrCreateAsync(account.InboxId, cancellationToken);
        var include = requestedIds is null || requestedIds.Contains("singleton", StringComparer.Ordinal);
        var list = new JsonArray();
        if (include) list.Add(JmapVacationResponseService.ToJson(response, properties));
        var notFound = new JsonArray();
        if (requestedIds is not null)
        {
            foreach (var id in requestedIds.Where(id => id != "singleton").Distinct(StringComparer.Ordinal))
                notFound.Add(id);
        }
        return new JmapMethodResponse(Name, new JsonObject
        {
            ["accountId"] = accountId,
            ["state"] = await states.GetStateAsync(
                account.InboxId,
                JmapConstants.VacationResponseDataType,
                cancellationToken),
            ["list"] = list,
            ["notFound"] = notFound,
        });
    }
}

internal sealed class VacationResponseSetMethod(
    JmapAccountService accounts,
    JmapVacationResponseService vacations,
    JmapStateService states,
    EnvironmentConfig environment) : IJmapMethod
{
    private static readonly IReadOnlySet<string> MutableProperties = new HashSet<string>(
        ["isEnabled", "fromDate", "toDate", "subject", "textBody", "htmlBody"],
        StringComparer.Ordinal);

    public string Name => "VacationResponse/set";
    public string Capability => JmapConstants.VacationResponseCapability;

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
                "destroy")
            || !JmapMethodHelpers.TryGetRequiredString(arguments, "accountId", out var accountId)
            || !JmapMethodHelpers.TryGetOptionalString(arguments, "ifInState", out var ifInState)
            || !JmapEmailMutationHelpers.TryGetObjectMap(arguments, "create", false, out var create)
            || !JmapEmailMutationHelpers.TryGetObjectMap(arguments, "update", false, out var update)
            || !TryDestroy(arguments, out var destroy))
            return JmapMethodResponse.Error("invalidArguments");
        var account = await accounts.GetAccountAsync(context.User, accountId, cancellationToken);
        if (account is null) return JmapMethodResponse.Error("accountNotFound");
        var response = await vacations.GetOrCreateAsync(account.InboxId, cancellationToken);
        var oldState = await states.GetStateAsync(
            account.InboxId,
            JmapConstants.VacationResponseDataType,
            cancellationToken);
        if (ifInState is not null && !string.Equals(ifInState, oldState, StringComparison.Ordinal))
            return JmapMethodResponse.Error("stateMismatch");

        var notCreated = new JsonObject();
        if (create is not null)
        {
            foreach (var item in create)
                notCreated[item.Key] = JmapMethodHelpers.SetError("singleton");
        }
        var updated = new JsonObject();
        var notUpdated = new JsonObject();
        if (update is not null)
        {
            foreach (var item in update)
            {
                var resolvedId = context.ResolveId(item.Key);
                if (resolvedId != "singleton")
                {
                    notUpdated[item.Key] = JmapMethodHelpers.SetError("notFound");
                    continue;
                }
                var invalid = item.Value.KeysForPatch()
                    .Where(property => !MutableProperties.Contains(property))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                var current = JmapVacationResponseService.ToJson(response);
                current.Remove("id");
                if (invalid.Length > 0)
                {
                    notUpdated[item.Key] = JmapMethodHelpers.SetError("invalidProperties", properties: invalid);
                    continue;
                }
                if (!JmapMethodHelpers.TryApplyPatch(current, item.Value, out var patched))
                {
                    notUpdated[item.Key] = JmapMethodHelpers.SetError("invalidPatch");
                    continue;
                }
                if (!JmapVacationResponseService.TryParse(
                    patched,
                    environment.Limits.MaxMessageSizeBytes,
                    out var values,
                    out var invalidProperties))
                {
                    notUpdated[item.Key] = JmapMethodHelpers.SetError(
                        "invalidProperties",
                        properties: invalidProperties);
                    continue;
                }
                response.IsEnabled = values.IsEnabled;
                response.FromDate = values.FromDate;
                response.ToDate = values.ToDate;
                response.Subject = values.Subject;
                response.TextBody = values.TextBody;
                response.HtmlBody = values.HtmlBody;
                response.UpdatedAt = DateTime.UtcNow;
                await vacations.SaveAsync(cancellationToken);
                updated["singleton"] = null;
            }
        }
        var notDestroyed = new JsonObject();
        if (destroy is not null)
        {
            foreach (var id in destroy.Distinct(StringComparer.Ordinal))
                notDestroyed[id] = JmapMethodHelpers.SetError(
                    context.ResolveId(id) == "singleton" ? "singleton" : "notFound");
        }
        return new JmapMethodResponse(Name, new JsonObject
        {
            ["accountId"] = accountId,
            ["oldState"] = oldState,
            ["newState"] = await states.GetStateAsync(
                account.InboxId,
                JmapConstants.VacationResponseDataType,
                cancellationToken),
            ["created"] = null,
            ["updated"] = updated.Count == 0 ? null : updated,
            ["destroyed"] = null,
            ["notCreated"] = notCreated.Count == 0 ? null : notCreated,
            ["notUpdated"] = notUpdated.Count == 0 ? null : notUpdated,
            ["notDestroyed"] = notDestroyed.Count == 0 ? null : notDestroyed,
        });
    }

    private static bool TryDestroy(JsonObject arguments, out IReadOnlyList<string>? values)
    {
        values = null;
        if (!arguments.TryGetPropertyValue("destroy", out var node) || node is null) return true;
        if (node is not JsonArray array) return false;
        var result = new List<string>(array.Count);
        foreach (var item in array)
        {
            if (item is not JsonValue value || !value.TryGetValue<string>(out var id) || id is null)
                return false;
            result.Add(id);
        }
        values = result;
        return true;
    }
}
