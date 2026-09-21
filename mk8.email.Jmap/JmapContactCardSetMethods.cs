using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using mk8.email.Infrastructure.Data;
using mk8.email.Infrastructure.Environment;
using mk8.email.Infrastructure.Models;

namespace mk8.email.Jmap;

internal sealed class ContactCardSetMethod(
    EmailDbContext database,
    JmapAccountService accounts,
    JmapContactStore contacts,
    JmapStateService states,
    EnvironmentConfig environment) : IJmapMethod
{
    public string Name => "ContactCard/set";
    public string Capability => JmapConstants.ContactsCapability;

    public async Task<JmapMethodResponse> InvokeAsync(
        JmapInvocationContext context,
        JsonObject arguments,
        CancellationToken cancellationToken)
    {
        if (!JmapMethodHelpers.HasOnlyProperties(
                arguments, "accountId", "ifInState", "create", "update", "destroy")
            || !JmapMethodHelpers.TryGetRequiredString(arguments, "accountId", out var accountId)
            || !JmapMethodHelpers.TryGetOptionalString(arguments, "ifInState", out var ifInState)
            || !JmapContactArguments.TryGetObjectMap(arguments, "create", out var creates)
            || !JmapContactArguments.TryGetObjectMap(arguments, "update", out var updates)
            || !JmapContactArguments.TryGetDestroy(arguments, out var destroys)
            || !JmapMethodHelpers.AreValidCreationIds(creates?.Keys)
            || !JmapMethodHelpers.AreValidIdReferences(updates?.Keys)
            || !JmapMethodHelpers.AreValidIdReferences(destroys))
        {
            return JmapMethodResponse.Error("invalidArguments");
        }
        var operationCount = (creates?.Count ?? 0) + (updates?.Count ?? 0) + (destroys?.Count ?? 0);
        if (operationCount > environment.Jmap.MaxObjectsInSet)
            return JmapMethodResponse.Error("requestTooLarge");

        var account = await accounts.GetContactAccountAsync(context.User, accountId, cancellationToken);
        if (account is null)
            return JmapMethodResponse.Error(await accounts.GetContactAccountErrorAsync(
                context.User, accountId, cancellationToken));
        await contacts.EnsureDefaultAddressBookAsync(context.User, cancellationToken);
        var oldState = await states.GetStateAsync(
            account.InboxId,
            JmapConstants.ContactCardDataType,
            cancellationToken);
        if (ifInState is not null && !string.Equals(ifInState, oldState, StringComparison.Ordinal))
            return JmapMethodResponse.Error("stateMismatch");

        var books = await contacts.LoadAddressBooksAsync(account.UserId, true, cancellationToken);
        var cards = await contacts.LoadCardsAsync(account.UserId, true, cancellationToken);
        var booksById = books.ToDictionary(book => JmapId.AddressBook(book.Id), StringComparer.Ordinal);
        var cardsById = cards.ToDictionary(card => card.Id, StringComparer.Ordinal);
        var cardCountsByUid = cards
            .GroupBy(card => card.Card["uid"]!.GetValue<string>(), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var counts = cards.GroupBy(card => card.Resource.CollectionId)
            .ToDictionary(group => group.Key, group => group.Count());

        var created = new JsonObject();
        var updated = new JsonObject();
        var destroyed = new JsonArray();
        var notCreated = new JsonObject();
        var notUpdated = new JsonObject();
        var notDestroyed = new JsonObject();
        var destroySet = (destroys ?? []).Select(context.ResolveId)
            .Where(id => id is not null).ToHashSet(StringComparer.Ordinal);

        if (creates is not null)
        {
            foreach (var item in creates)
            {
                if (item.Value.ContainsKey("id"))
                {
                    notCreated[item.Key] = Invalid(["id"]);
                    continue;
                }
                if (!TryResolveAddressBook(
                        item.Value,
                        context,
                        booksById,
                        out var book,
                        out var addressBookError))
                {
                    notCreated[item.Key] = addressBookError;
                    continue;
                }
                if (counts.GetValueOrDefault(book!.Id) >= environment.Dav.MaxResourcesPerCollection)
                {
                    notCreated[item.Key] = JmapMethodHelpers.SetError("overQuota");
                    continue;
                }
                var storedRequest = (JsonObject)item.Value.DeepClone();
                storedRequest.Remove("id");
                storedRequest.Remove("addressBookIds");
                var prepared = await contacts.PrepareCardAsync(
                    account.InboxId,
                    storedRequest,
                    cancellationToken);
                if (prepared.Card is null)
                {
                    notCreated[item.Key] = Invalid(prepared.InvalidProperties);
                    continue;
                }
                var uid = prepared.Card["uid"]!.GetValue<string>();
                if (cardCountsByUid.GetValueOrDefault(uid) > 0)
                {
                    notCreated[item.Key] = Invalid(["uid"]);
                    continue;
                }

                var id = Guid.CreateVersion7();
                var now = DateTime.UtcNow;
                var resource = JmapContactStore.StoreCreatedCard(
                    database, book, id, prepared.Card, now);
                var view = new JmapContactCardView(resource, prepared.Card);
                var idString = view.Id;
                cardsById[idString] = view;
                cardCountsByUid[uid] = cardCountsByUid.GetValueOrDefault(uid) + 1;
                counts[book.Id] = counts.GetValueOrDefault(book.Id) + 1;
                context.CreatedIds[item.Key] = idString;
                created[item.Key] = new JsonObject { ["id"] = idString };
            }
        }

        if (updates is not null)
        {
            foreach (var item in updates)
            {
                var resolved = context.ResolveId(item.Key);
                if (resolved is not null && destroySet.Contains(resolved))
                {
                    notUpdated[item.Key] = JmapMethodHelpers.SetError("willDestroy");
                    continue;
                }
                if (resolved is null || !cardsById.TryGetValue(resolved, out var existing))
                {
                    notUpdated[item.Key] = JmapMethodHelpers.SetError("notFound");
                    continue;
                }
                var current = JmapContactStore.BuildContactCard(existing);
                if (!JmapMethodHelpers.TryApplyPatch(current, item.Value, out var patched))
                {
                    notUpdated[item.Key] = JmapMethodHelpers.SetError("invalidPatch");
                    continue;
                }
                if (!string.Equals(
                        patched["id"]?.GetValue<string>(),
                        existing.Id,
                        StringComparison.Ordinal))
                {
                    notUpdated[item.Key] = Invalid(["id"]);
                    continue;
                }
                if (!TryResolveAddressBook(
                        patched,
                        context,
                        booksById,
                        out var targetBook,
                        out var addressBookError))
                {
                    notUpdated[item.Key] = addressBookError;
                    continue;
                }
                if (targetBook!.Id != existing.Resource.CollectionId
                    && counts.GetValueOrDefault(targetBook.Id) >= environment.Dav.MaxResourcesPerCollection)
                {
                    notUpdated[item.Key] = JmapMethodHelpers.SetError("overQuota");
                    continue;
                }
                var storedRequest = (JsonObject)patched.DeepClone();
                storedRequest.Remove("id");
                storedRequest.Remove("addressBookIds");
                var prepared = await contacts.PrepareCardAsync(
                    account.InboxId,
                    storedRequest,
                    cancellationToken);
                if (prepared.Card is null)
                {
                    notUpdated[item.Key] = Invalid(prepared.InvalidProperties);
                    continue;
                }
                var oldUid = existing.Card["uid"]!.GetValue<string>();
                var newUid = prepared.Card["uid"]!.GetValue<string>();
                if (!string.Equals(oldUid, newUid, StringComparison.Ordinal)
                    && cardCountsByUid.GetValueOrDefault(newUid) > 0)
                {
                    notUpdated[item.Key] = Invalid(["uid"]);
                    continue;
                }
                var oldBook = books.Single(book => book.Id == existing.Resource.CollectionId);
                JmapContactStore.StoreUpdatedCard(
                    database,
                    existing.Resource,
                    oldBook,
                    targetBook,
                    prepared.Card,
                    DateTime.UtcNow);
                if (oldBook.Id != targetBook.Id)
                {
                    counts[oldBook.Id] = counts.GetValueOrDefault(oldBook.Id) - 1;
                    counts[targetBook.Id] = counts.GetValueOrDefault(targetBook.Id) + 1;
                }
                if (!string.Equals(oldUid, newUid, StringComparison.Ordinal))
                {
                    DecrementUidCount(cardCountsByUid, oldUid);
                    cardCountsByUid[newUid] = cardCountsByUid.GetValueOrDefault(newUid) + 1;
                }
                cardsById[resolved] = new JmapContactCardView(existing.Resource, prepared.Card);
                updated[resolved] = null;
            }
        }

        if (destroys is not null)
        {
            foreach (var requested in destroys)
            {
                var resolved = context.ResolveId(requested);
                if (resolved is null || !cardsById.TryGetValue(resolved, out var existing))
                {
                    notDestroyed[requested] = JmapMethodHelpers.SetError("notFound");
                    continue;
                }
                var book = books.Single(candidate => candidate.Id == existing.Resource.CollectionId);
                JmapContactStore.DestroyCard(database, existing.Resource, book, DateTime.UtcNow);
                cardsById.Remove(resolved);
                DecrementUidCount(cardCountsByUid, existing.Card["uid"]!.GetValue<string>());
                counts[book.Id] = counts.GetValueOrDefault(book.Id) - 1;
                destroyed.Add(resolved);
            }
        }

        await database.SaveChangesAsync(cancellationToken);
        return new JmapMethodResponse(Name, new JsonObject
        {
            ["accountId"] = accountId,
            ["oldState"] = oldState,
            ["newState"] = await states.GetStateAsync(
                account.InboxId,
                JmapConstants.ContactCardDataType,
                cancellationToken),
            ["created"] = created.Count == 0 ? null : created,
            ["updated"] = updated.Count == 0 ? null : updated,
            ["destroyed"] = destroyed.Count == 0 ? null : destroyed,
            ["notCreated"] = notCreated.Count == 0 ? null : notCreated,
            ["notUpdated"] = notUpdated.Count == 0 ? null : notUpdated,
            ["notDestroyed"] = notDestroyed.Count == 0 ? null : notDestroyed,
        });
    }

    internal static bool TryResolveAddressBook(
        JsonObject value,
        JmapInvocationContext context,
        IReadOnlyDictionary<string, DavCollectionDB> books,
        out DavCollectionDB? book,
        out JsonObject error)
    {
        book = null;
        error = Invalid(["addressBookIds"]);
        if (value["addressBookIds"] is not JsonObject ids || ids.Count != 1)
            return false;
        var item = ids.Single();
        if (item.Value is not JsonValue includedValue
            || !includedValue.TryGetValue<bool>(out var included)
            || !included)
        {
            return false;
        }
        var resolved = context.ResolveId(item.Key);
        if (resolved is null || !books.TryGetValue(resolved, out book))
            return false;
        return true;
    }

    private static JsonObject Invalid(IEnumerable<string> properties) =>
        JmapMethodHelpers.SetError("invalidProperties", properties: properties);

    private static void DecrementUidCount(Dictionary<string, int> counts, string uid)
    {
        var remaining = counts.GetValueOrDefault(uid) - 1;
        if (remaining > 0)
            counts[uid] = remaining;
        else
            counts.Remove(uid);
    }
}

internal sealed class ContactCardCopyMethod(
    JmapAccountService accounts,
    JmapContactStore contacts,
    JmapStateService states,
    EnvironmentConfig environment) : IJmapMethod
{
    public string Name => "ContactCard/copy";
    public string Capability => JmapConstants.ContactsCapability;

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

        var sourceGeneric = await accounts.GetAccountAsync(context.User, fromAccountId, cancellationToken);
        if (sourceGeneric is null)
            return JmapMethodResponse.Error("fromAccountNotFound");
        var source = await accounts.GetContactAccountAsync(context.User, fromAccountId, cancellationToken);
        if (source is null)
            return JmapMethodResponse.Error("fromAccountNotSupportedByMethod");
        var target = await accounts.GetContactAccountAsync(context.User, accountId, cancellationToken);
        if (target is null)
            return JmapMethodResponse.Error(await accounts.GetContactAccountErrorAsync(
                context.User, accountId, cancellationToken));

        await contacts.EnsureDefaultAddressBookAsync(context.User, cancellationToken);
        var sourceState = await states.GetStateAsync(
            source.InboxId, JmapConstants.ContactCardDataType, cancellationToken);
        var targetState = await states.GetStateAsync(
            target.InboxId, JmapConstants.ContactCardDataType, cancellationToken);
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
        return new JmapMethodResponse(Name, new JsonObject
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
