using mk8.email.Contracts.Messaging;
using System.Text;
using System.Text.Json.Nodes;
using mk8.email.Configuration;

namespace mk8.email.Jmap;

internal sealed class ContactCardGetMethod(
    JmapAccountService accounts,
    JmapContactStore contacts,
    JmapStateService states,
    EnvironmentConfig environment) : IJmapMethod
{
    public MailOperationKind Operation => MailOperationKind.ReadContacts;
    public MailFeature Feature => MailFeature.Contacts;

    public async Task<JmapMethodResponse> InvokeAsync(
        JmapInvocationContext context,
        JsonObject arguments,
        CancellationToken cancellationToken)
    {
        if (!JmapMethodHelpers.HasOnlyProperties(arguments, "accountId", "ids", "properties")
            || !JmapMethodHelpers.TryGetRequiredString(arguments, "accountId", out var accountId)
            || !JmapMethodHelpers.TryGetIdArray(arguments, "ids", true, out var requestedIds)
            || !JmapMethodHelpers.TryGetStringArray(arguments, "properties", true, out var propertyList))
        {
            return JmapMethodResponse.Error("invalidArguments");
        }
        if (requestedIds is { Count: > 0 } && requestedIds.Count > environment.Jmap.MaxObjectsInGet)
            return JmapMethodResponse.Error("requestTooLarge");
        if (propertyList is not null
            && propertyList.Any(property => !JmapContactValidator.IsSupportedCardProperty(property)))
        {
            return JmapMethodResponse.Error("invalidArguments");
        }
        var account = await accounts.GetContactAccountAsync(context.User, accountId, cancellationToken).ConfigureAwait(false);
        if (account is null)
            return JmapMethodResponse.Error(await accounts.GetContactAccountErrorAsync(
                context.User, accountId, cancellationToken).ConfigureAwait(false));
        await contacts.EnsureDefaultAddressBookAsync(context.User, cancellationToken).ConfigureAwait(false);
        var cards = await contacts.LoadCardsAsync(account.UserId, false, cancellationToken).ConfigureAwait(false);
        if (requestedIds is null && cards.Count > environment.Jmap.MaxObjectsInGet)
            return JmapMethodResponse.Error("requestTooLarge");
        var byId = cards.ToDictionary(card => card.Id, StringComparer.Ordinal);
        var properties = propertyList?.ToHashSet(StringComparer.Ordinal);
        var list = new JsonArray();
        var notFound = new JsonArray();
        foreach (var id in (requestedIds ?? byId.Keys.ToArray()).Distinct(StringComparer.Ordinal))
        {
            if (byId.TryGetValue(id, out var card))
                list.Add(JmapContactStore.BuildContactCard(card, properties));
            else
                notFound.Add(id);
        }
        return new JmapMethodResponse(Operation, new JsonObject
        {
            ["accountId"] = accountId,
            ["state"] = await states.GetStateAsync(
                account.InboxId,
                JmapConstants.ContactCardDataType,
                cancellationToken).ConfigureAwait(false),
            ["list"] = list,
            ["notFound"] = notFound,
        });
    }
}
