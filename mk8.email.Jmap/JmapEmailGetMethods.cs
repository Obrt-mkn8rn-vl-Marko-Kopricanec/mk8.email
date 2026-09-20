using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using mk8.email.Infrastructure.Data;
using mk8.email.Infrastructure.Environment;

namespace mk8.email.Jmap;

internal static class JmapEmailArguments
{
    public static bool TryGetProjectionOptions(
        JsonObject arguments,
        IReadOnlyList<string> defaults,
        out JmapEmailProjectionOptions options)
    {
        options = null!;
        if (!JmapMethodHelpers.TryGetStringArray(arguments, "properties", true, out var properties)
            || !JmapMethodHelpers.TryGetStringArray(arguments, "bodyProperties", false, out var bodyProperties)
            || !JmapMethodHelpers.TryGetOptionalBoolean(arguments, "fetchTextBodyValues", false, out var fetchText)
            || !JmapMethodHelpers.TryGetOptionalBoolean(arguments, "fetchHTMLBodyValues", false, out var fetchHtml)
            || !JmapMethodHelpers.TryGetOptionalBoolean(arguments, "fetchAllBodyValues", false, out var fetchAll)
            || !JmapMethodHelpers.TryGetOptionalUnsignedInt(
                arguments,
                "maxBodyValueBytes",
                out var maximumBodyBytes,
                allowNull: false))
        {
            return false;
        }

        var selectedProperties = properties ?? defaults;
        var selectedBodyProperties = bodyProperties ?? JmapEmailCodec.DefaultBodyProperties;
        if (!JmapEmailCodec.TryValidateProperties(selectedProperties, bodyProperties: false, out _)
            || !JmapEmailCodec.TryValidateProperties(selectedBodyProperties, bodyProperties: true, out _))
        {
            return false;
        }

        options = new JmapEmailProjectionOptions(
            selectedProperties,
            selectedBodyProperties,
            fetchText,
            fetchHtml,
            fetchAll,
            checked((int)Math.Min(maximumBodyBytes ?? 0, int.MaxValue)));
        return true;
    }

    public static bool TryGetIds(
        JsonObject arguments,
        string property,
        JmapInvocationContext context,
        bool nullable,
        out IReadOnlyList<string>? ids)
        => JmapMethodHelpers.TryGetIdArray(
            arguments,
            property,
            context,
            nullable,
            out ids);
}

internal sealed class EmailGetMethod(
    EmailDbContext database,
    JmapAccountService accounts,
    JmapStateService states,
    EnvironmentConfig environment) : IJmapMethod
{
    public string Name => "Email/get";
    public string Capability => JmapConstants.MailCapability;

    public async Task<JmapMethodResponse> InvokeAsync(
        JmapInvocationContext context,
        JsonObject arguments,
        CancellationToken cancellationToken)
    {
        if (!JmapMethodHelpers.HasOnlyProperties(
                arguments,
                "accountId",
                "ids",
                "properties",
                "bodyProperties",
                "fetchTextBodyValues",
                "fetchHTMLBodyValues",
                "fetchAllBodyValues",
                "maxBodyValueBytes")
            || !JmapMethodHelpers.TryGetRequiredString(arguments, "accountId", out var accountId)
            || !JmapEmailArguments.TryGetProjectionOptions(
                arguments,
                JmapEmailCodec.DefaultProperties,
                out var options)
            || !JmapEmailArguments.TryGetIds(arguments, "ids", context, true, out var requestedIds))
        {
            return JmapMethodResponse.Error("invalidArguments");
        }
        if (requestedIds is { Count: > 0 } && requestedIds.Count > environment.Jmap.MaxObjectsInGet)
            return JmapMethodResponse.Error("requestTooLarge");

        var account = await accounts.GetAccountAsync(context.User, accountId, cancellationToken);
        if (account is null)
            return JmapMethodResponse.Error("accountNotFound");

        var query = database.Emails
            .AsNoTracking()
            .Where(email => email.Folder.InboxId == account.InboxId && !email.IsDeleted);
        Guid[]? parsedIds = null;
        if (requestedIds is not null)
        {
            parsedIds = requestedIds
                .Select(id => JmapId.TryParseEmail(id, out var parsedId) ? parsedId : Guid.Empty)
                .Where(id => id != Guid.Empty)
                .Distinct()
                .ToArray();
            query = query.Where(email => parsedIds.Contains(email.Id));
        }

        var emails = await query.ToListAsync(cancellationToken);
        if (requestedIds is null && emails.Count > environment.Jmap.MaxObjectsInGet)
            return JmapMethodResponse.Error("requestTooLarge");
        var byId = emails.ToDictionary(email => JmapId.Email(email.Id), StringComparer.Ordinal);
        var responseList = new JsonArray();
        var notFound = new JsonArray();
        foreach (var id in (requestedIds ?? byId.Keys.ToArray()).Distinct(StringComparer.Ordinal))
        {
            if (!byId.TryGetValue(id, out var email))
            {
                notFound.Add(id);
                continue;
            }

            using var message = JmapEmailCodec.Parse(email);
            responseList.Add(JmapEmailCodec.BuildEmail(
                message,
                options,
                email.Id,
                email));
        }

        return new JmapMethodResponse(Name, new JsonObject
        {
            ["accountId"] = accountId,
            ["state"] = await states.GetStateAsync(
                account.InboxId,
                JmapConstants.EmailDataType,
                cancellationToken),
            ["list"] = responseList,
            ["notFound"] = notFound,
        });
    }
}

internal sealed class EmailChangesMethod(
    JmapAccountService accounts,
    JmapStateService states,
    EnvironmentConfig environment) : IJmapMethod
{
    public string Name => "Email/changes";
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
            JmapConstants.EmailDataType,
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
