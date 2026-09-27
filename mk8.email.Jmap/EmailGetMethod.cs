using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using mk8.email.Application.Services;
using mk8.email.Infrastructure.Data;
using mk8.email.Configuration;

namespace mk8.email.Jmap;

internal sealed class EmailGetMethod(
    EmailDbContext database,
    JmapAccountService accounts,
    JmapStateService states,
    MailboxMessageContentService content,
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
                allowNullProperties: true,
                out var options)
            || !JmapEmailArguments.TryGetIds(arguments, "ids", true, out var requestedIds))
        {
            return JmapMethodResponse.Error("invalidArguments");
        }
        if (requestedIds is { Count: > 0 } && requestedIds.Count > environment.Jmap.MaxObjectsInGet)
            return JmapMethodResponse.Error("requestTooLarge");

        var account = await accounts.GetAccountAsync(context.User, accountId, cancellationToken).ConfigureAwait(false);
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

        var emails = await query.ToListAsync(cancellationToken).ConfigureAwait(false);
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

            var rawMessage = await content.ReadAsync(email, cancellationToken).ConfigureAwait(false);
            using var message = JmapEmailCodec.Parse(rawMessage);
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
                cancellationToken).ConfigureAwait(false),
            ["list"] = responseList,
            ["notFound"] = notFound,
        });
    }
}
