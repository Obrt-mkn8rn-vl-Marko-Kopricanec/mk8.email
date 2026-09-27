using mk8.email.Contracts.Messaging;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using mk8.email.Infrastructure.Data;
using mk8.email.Configuration;
using mk8.email.Infrastructure.Models;

namespace mk8.email.Jmap;

internal sealed class ContactCardCopyMethod(
    JmapAccountService accounts,
    JmapContactStore contacts,
    JmapStateService states,
    EnvironmentConfig environment) : IJmapMethod
{
    public MailOperationKind Operation => MailOperationKind.CopyContacts;
    public MailFeature Feature => MailFeature.Contacts;

    public async Task<JmapMethodResponse> InvokeAsync(
        JmapInvocationContext context,
        JsonObject arguments,
        CancellationToken cancellationToken)
    {
        if (!JmapMethodHelpers.HasOnlyProperties(
                arguments, "fromAccountId", "ifFromInState", "accountId", "ifInState",
                "create", "onSuccessDestroyOriginal", "destroyFromIfInState")
            || !JmapMethodHelpers.TryGetRequiredString(arguments, "fromAccountId", out var fromAccountId)
            || !JmapMethodHelpers.TryGetRequiredString(arguments, "accountId", out var accountId)
            || string.Equals(fromAccountId, accountId, StringComparison.Ordinal)
            || !JmapMethodHelpers.TryGetOptionalString(arguments, "ifFromInState", out var ifFromInState)
            || !JmapMethodHelpers.TryGetOptionalString(arguments, "ifInState", out var ifInState)
            || !JmapMethodHelpers.TryGetOptionalString(
                arguments, "destroyFromIfInState", out _)
            || !JmapMethodHelpers.TryGetOptionalBoolean(
                arguments, "onSuccessDestroyOriginal", false, out _)
            || !JmapContactArguments.TryGetObjectMap(arguments, "create", out var create)
            || create is null
            || !JmapMethodHelpers.AreValidCreationIds(create.Keys))
        {
            return JmapMethodResponse.Error("invalidArguments");
        }
        if (create.Count > environment.Jmap.MaxObjectsInSet)
            return JmapMethodResponse.Error("requestTooLarge");

        var sourceGeneric = await accounts.GetAccountAsync(context.User, fromAccountId, cancellationToken).ConfigureAwait(false);
        if (sourceGeneric is null)
            return JmapMethodResponse.Error("fromAccountNotFound");
        var source = await accounts.GetContactAccountAsync(context.User, fromAccountId, cancellationToken).ConfigureAwait(false);
        if (source is null)
            return JmapMethodResponse.Error("fromAccountNotSupportedByMethod");
        var target = await accounts.GetContactAccountAsync(context.User, accountId, cancellationToken).ConfigureAwait(false);
        if (target is null)
            return JmapMethodResponse.Error(await accounts.GetContactAccountErrorAsync(
                context.User, accountId, cancellationToken).ConfigureAwait(false));

        await contacts.EnsureDefaultAddressBookAsync(context.User, cancellationToken).ConfigureAwait(false);
        var sourceState = await states.GetStateAsync(
            source.InboxId, JmapConstants.ContactCardDataType, cancellationToken).ConfigureAwait(false);
        var targetState = await states.GetStateAsync(
            target.InboxId, JmapConstants.ContactCardDataType, cancellationToken).ConfigureAwait(false);
        if (ifFromInState is not null && ifFromInState != sourceState
            || ifInState is not null && ifInState != targetState)
        {
            return JmapMethodResponse.Error("stateMismatch");
        }

        // mk8.email deliberately exposes its user-scoped CardDAV data as a
        // single JMAP Contacts account. If another contacts account is added
        // in future, this method is already registered and validates the
        // standard copy shape; today no authenticated session can have two
        // distinct supported contact accounts.
        return new JmapMethodResponse(Operation, new JsonObject
        {
            ["fromAccountId"] = fromAccountId,
            ["accountId"] = accountId,
            ["oldState"] = targetState,
            ["newState"] = targetState,
            ["created"] = null,
            ["notCreated"] = new JsonObject(create.Select(item =>
                KeyValuePair.Create<string, JsonNode?>(
                    item.Key,
                    JmapMethodHelpers.SetError("forbidden",
                        "No second JMAP Contacts account is available for copying."))
            )),
        });
    }
}
