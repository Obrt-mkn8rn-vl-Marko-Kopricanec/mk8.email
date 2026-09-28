using mk8.email.Contracts.Messaging;
using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using mk8.email.Infrastructure.Data;
using mk8.email.Configuration;
using mk8.email.Application.Services;
using mk8.email.Infrastructure.Models;

namespace mk8.email.Jmap;

internal sealed class VacationResponseGetMethod(
    JmapAccountService accounts,
    JmapVacationResponseService vacations,
    JmapStateService states,
    EnvironmentConfig environment) : IJmapMethod
{
    private static readonly HashSet<string> Properties = new HashSet<string>(
        ["id", "isEnabled", "fromDate", "toDate", "subject", "textBody", "htmlBody"],
        StringComparer.Ordinal);

    public MailOperationKind Operation => MailOperationKind.ReadVacationSettings;
    public MailFeature Feature => MailFeature.AutomaticReplies;

    public async Task<JmapMethodResponse> InvokeAsync(
        JmapInvocationContext context,
        JsonObject arguments,
        CancellationToken cancellationToken)
    {
        if (!JmapMethodHelpers.HasOnlyProperties(arguments, "accountId", "ids", "properties")
            || !JmapMethodHelpers.TryGetRequiredString(arguments, "accountId", out var accountId)
            || !JmapMethodHelpers.TryGetStringArray(arguments, "properties", true, out var requestedProperties)
            || !JmapEmailArguments.TryGetIds(arguments, "ids", true, out var requestedIds))
            return JmapMethodResponse.Error("invalidArguments");
        var properties = requestedProperties?.ToHashSet(StringComparer.Ordinal);
        if (properties is not null && properties.Any(property => !Properties.Contains(property)))
            return JmapMethodResponse.Error("invalidArguments");
        if (requestedIds is { Count: > 0 }
            && requestedIds.Count > environment.Jmap.MaxObjectsInGet)
        {
            return JmapMethodResponse.Error("requestTooLarge");
        }
        var account = await accounts.GetAccountAsync(context.User, accountId, cancellationToken).ConfigureAwait(false);
        if (account is null) return JmapMethodResponse.Error("accountNotFound");
        var response = await vacations.GetOrCreateAsync(account.InboxId, cancellationToken).ConfigureAwait(false);
        var include = requestedIds is null || requestedIds.Contains("singleton", StringComparer.Ordinal);
        var list = new JsonArray();
        if (include) list.Add(await vacations.ToJsonAsync(response, properties, cancellationToken).ConfigureAwait(false));
        var notFound = new JsonArray();
        if (requestedIds is not null)
        {
            foreach (var id in requestedIds.Where(id => !string.Equals(id, "singleton", StringComparison.Ordinal)).Distinct(StringComparer.Ordinal))
                notFound.Add(id);
        }
        return new JmapMethodResponse(Operation, new JsonObject
        {
            ["accountId"] = accountId,
            ["state"] = await states.GetStateAsync(
                account.InboxId,
                JmapConstants.VacationResponseDataType,
                cancellationToken).ConfigureAwait(false),
            ["list"] = list,
            ["notFound"] = notFound,
        });
    }
}
