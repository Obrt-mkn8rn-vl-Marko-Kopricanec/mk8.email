using mk8.email.Contracts.Messaging;
using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using mk8.email.Infrastructure.Data;
using mk8.email.Configuration;
using mk8.email.Application.Services;
using mk8.email.Infrastructure.Models;

namespace mk8.email.Jmap;

internal sealed class VacationResponseSetMethod(
    JmapAccountService accounts,
    JmapVacationResponseService vacations,
    JmapStateService states,
    EnvironmentConfig environment) : IJmapMethod
{
    private static readonly HashSet<string> MutableProperties = new HashSet<string>(
        ["isEnabled", "fromDate", "toDate", "subject", "textBody", "htmlBody"],
        StringComparer.Ordinal);

    public MailOperationKind Operation => MailOperationKind.MutateVacationSettings;
    public MailFeature Feature => MailFeature.AutomaticReplies;

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
            || !TryDestroy(arguments, out var destroy)
            || !JmapMethodHelpers.AreValidCreationIds(create?.Keys)
            || !JmapMethodHelpers.AreValidIdReferences(update?.Keys, context)
            || !JmapMethodHelpers.AreValidIdReferences(destroy, context))
            return JmapMethodResponse.Error("invalidArguments");
        var operationCount = (create?.Count ?? 0) + (update?.Count ?? 0) + (destroy?.Count ?? 0);
        if (operationCount > environment.Jmap.MaxObjectsInSet)
            return JmapMethodResponse.Error("requestTooLarge");
        var account = await accounts.GetAccountAsync(context.User, accountId, cancellationToken).ConfigureAwait(false);
        if (account is null) return JmapMethodResponse.Error("accountNotFound");
        var response = await vacations.GetOrCreateAsync(account.InboxId, cancellationToken).ConfigureAwait(false);
        var oldState = await states.GetStateAsync(
            account.InboxId,
            JmapConstants.VacationResponseDataType,
            cancellationToken).ConfigureAwait(false);
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
                var current = await vacations.ToJsonAsync(response, null, cancellationToken).ConfigureAwait(false);
                if (!JmapMethodHelpers.TryApplyPatchAllowingUnchangedProperties(
                        current,
                        item.Value,
                        MutableProperties,
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
                if (!JmapVacationResponseService.TryParse(
                    patched,
                    environment.Limits.MaxMessageSizeBytes,
                    out var values,
                    out invalidProperties))
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
                await vacations.SetBodiesAsync(
                    response, values.TextBody, values.HtmlBody, cancellationToken).ConfigureAwait(false);
                response.UpdatedAt = DateTime.UtcNow;
                await vacations.SaveAsync(cancellationToken).ConfigureAwait(false);
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
        return new JmapMethodResponse(Operation, new JsonObject
        {
            ["accountId"] = accountId,
            ["oldState"] = oldState,
            ["newState"] = await states.GetStateAsync(
                account.InboxId,
                JmapConstants.VacationResponseDataType,
                cancellationToken).ConfigureAwait(false),
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
