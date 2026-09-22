using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using mk8.email.Infrastructure.Data;
using mk8.email.Configuration;

namespace mk8.email.Jmap;

internal sealed class ThreadGetMethod(
    EmailDbContext database,
    JmapAccountService accounts,
    JmapStateService states,
    EnvironmentConfig environment) : IJmapMethod
{
    private static readonly IReadOnlySet<string> Properties = new HashSet<string>(
        ["id", "emailIds"],
        StringComparer.Ordinal);

    public string Name => "Thread/get";
    public string Capability => JmapConstants.MailCapability;

    public async Task<JmapMethodResponse> InvokeAsync(
        JmapInvocationContext context,
        JsonObject arguments,
        CancellationToken cancellationToken)
    {
        if (!JmapMethodHelpers.HasOnlyProperties(arguments, "accountId", "ids", "properties")
            || !JmapMethodHelpers.TryGetRequiredString(arguments, "accountId", out var accountId)
            || !JmapMethodHelpers.TryGetStringArray(arguments, "properties", true, out var requestedProperties))
        {
            return JmapMethodResponse.Error("invalidArguments");
        }
        var properties = requestedProperties?.ToHashSet(StringComparer.Ordinal);
        if (properties is not null && properties.Any(property => !Properties.Contains(property)))
            return JmapMethodResponse.Error("invalidArguments");
        if (!JmapMethodHelpers.TryGetIdArray(
                arguments,
                "ids",
                true,
                out var requestedIds))
            return JmapMethodResponse.Error("invalidArguments");
        if (requestedIds is { Count: > 0 } && requestedIds.Count > environment.Jmap.MaxObjectsInGet)
            return JmapMethodResponse.Error("requestTooLarge");

        var account = await accounts.GetAccountAsync(context.User, accountId, cancellationToken);
        if (account is null)
            return JmapMethodResponse.Error("accountNotFound");
        var emails = await database.Emails
            .AsNoTracking()
            .Where(email => email.Folder.InboxId == account.InboxId && !email.IsDeleted)
            .OrderBy(email => email.ReceivedAt)
            .ThenBy(email => email.Id)
            .Select(email => new
            {
                email.Id,
                email.ThreadObjectId,
            })
            .ToListAsync(cancellationToken);
        var threads = emails
            .GroupBy(
                email => JmapId.Thread(email.ThreadObjectId ?? email.Id.ToString("N")),
                StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Select(email => JmapId.Email(email.Id)).ToArray(),
                StringComparer.Ordinal);
        if (requestedIds is null && threads.Count > environment.Jmap.MaxObjectsInGet)
            return JmapMethodResponse.Error("requestTooLarge");

        var list = new JsonArray();
        var notFound = new JsonArray();
        foreach (var id in (requestedIds ?? threads.Keys.ToArray()).Distinct(StringComparer.Ordinal))
        {
            if (!threads.TryGetValue(id, out var emailIds))
            {
                notFound.Add(id);
                continue;
            }
            var value = new JsonObject { ["id"] = id };
            if (properties is null || properties.Contains("emailIds"))
                value["emailIds"] = JmapMethodHelpers.ToJsonArray(emailIds);
            list.Add(value);
        }

        return new JmapMethodResponse(Name, new JsonObject
        {
            ["accountId"] = accountId,
            ["state"] = await states.GetStateAsync(
                account.InboxId,
                JmapConstants.ThreadDataType,
                cancellationToken),
            ["list"] = list,
            ["notFound"] = notFound,
        });
    }

}

internal sealed class ThreadChangesMethod(
    JmapAccountService accounts,
    JmapStateService states,
    EnvironmentConfig environment) : IJmapMethod
{
    public string Name => "Thread/changes";
    public string Capability => JmapConstants.MailCapability;

    public async Task<JmapMethodResponse> InvokeAsync(
        JmapInvocationContext context,
        JsonObject arguments,
        CancellationToken cancellationToken)
    {
        if (!JmapMethodHelpers.HasOnlyProperties(arguments, "accountId", "sinceState", "maxChanges")
            || !JmapMethodHelpers.TryGetRequiredString(arguments, "accountId", out var accountId)
            || !JmapMethodHelpers.TryGetRequiredString(arguments, "sinceState", out var sinceState)
            || !JmapMethodHelpers.TryGetOptionalUnsignedInt(arguments, "maxChanges", out var maxChanges)
            || maxChanges == 0)
        {
            return JmapMethodResponse.Error("invalidArguments");
        }
        var account = await accounts.GetAccountAsync(context.User, accountId, cancellationToken);
        if (account is null)
            return JmapMethodResponse.Error("accountNotFound");
        var changes = await states.GetChangesAsync(
            account.InboxId,
            JmapConstants.ThreadDataType,
            sinceState,
            maxChanges,
            environment.Jmap.MaxObjectsInGet,
            cancellationToken);
        if (changes is null)
            return JmapMethodResponse.Error("cannotCalculateChanges");
        return new JmapMethodResponse(Name, new JsonObject
        {
            ["accountId"] = accountId,
            ["oldState"] = changes.OldState,
            ["newState"] = changes.NewState,
            ["hasMoreChanges"] = changes.HasMoreChanges,
            ["created"] = JmapMethodHelpers.ToJsonArray(changes.Created),
            ["updated"] = JmapMethodHelpers.ToJsonArray(changes.Updated),
            ["destroyed"] = JmapMethodHelpers.ToJsonArray(changes.Destroyed),
        });
    }
}
