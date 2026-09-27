using mk8.email.Contracts.Messaging;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using mk8.email.Infrastructure.Data;
using mk8.email.Configuration;
using mk8.email.Infrastructure.Models;

namespace mk8.email.Jmap;

internal sealed class AddressBookGetMethod(
    JmapAccountService accounts,
    JmapContactStore contacts,
    JmapStateService states,
    EnvironmentConfig environment) : IJmapMethod
{
    public MailOperationKind Operation => MailOperationKind.ReadAddressBooks;
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
        IReadOnlySet<string>? properties = propertyList?.ToHashSet(StringComparer.Ordinal);
        if (properties is not null && properties.Any(property => !JmapAddressBookJson.Properties.Contains(property)))
            return JmapMethodResponse.Error("invalidArguments");

        var account = await accounts.GetContactAccountAsync(context.User, accountId, cancellationToken).ConfigureAwait(false);
        if (account is null)
            return JmapMethodResponse.Error(await accounts.GetContactAccountErrorAsync(
                context.User, accountId, cancellationToken).ConfigureAwait(false));
        await contacts.EnsureDefaultAddressBookAsync(context.User, cancellationToken).ConfigureAwait(false);
        var books = await contacts.LoadAddressBooksAsync(account.UserId, false, cancellationToken).ConfigureAwait(false);
        if (requestedIds is null && books.Count > environment.Jmap.MaxObjectsInGet)
            return JmapMethodResponse.Error("requestTooLarge");
        var byId = books.ToDictionary(book => JmapId.AddressBook(book.Id), StringComparer.Ordinal);
        var list = new JsonArray();
        var notFound = new JsonArray();
        foreach (var id in (requestedIds ?? byId.Keys.ToArray()).Distinct(StringComparer.Ordinal))
        {
            if (byId.TryGetValue(id, out var book))
                list.Add(JmapContactStore.BuildAddressBook(book, properties));
            else
                notFound.Add(id);
        }
        return new JmapMethodResponse(Operation, new JsonObject
        {
            ["accountId"] = accountId,
            ["state"] = await states.GetStateAsync(
                account.InboxId,
                JmapConstants.AddressBookDataType,
                cancellationToken).ConfigureAwait(false),
            ["list"] = list,
            ["notFound"] = notFound,
        });
    }
}
