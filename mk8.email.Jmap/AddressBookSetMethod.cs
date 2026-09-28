using mk8.email.Contracts.Messaging;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using mk8.email.Infrastructure.Data;
using mk8.email.Configuration;
using mk8.email.Infrastructure.Models;

namespace mk8.email.Jmap;

internal sealed class AddressBookSetMethod(
    EmailDbContext database,
    JmapAccountService accounts,
    JmapContactStore contacts,
    JmapStateService states,
    EnvironmentConfig environment) : IJmapMethod
{
    private static readonly HashSet<string> MutableProperties = new HashSet<string>(
        ["name", "description", "sortOrder", "isSubscribed", "shareWith"],
        StringComparer.Ordinal);

    public MailOperationKind Operation => MailOperationKind.MutateAddressBooks;
    public MailFeature Feature => MailFeature.Contacts;

    public async Task<JmapMethodResponse> InvokeAsync(
        JmapInvocationContext context,
        JsonObject arguments,
        CancellationToken cancellationToken)
    {
        if (!JmapMethodHelpers.HasOnlyProperties(
                arguments,
                "accountId", "ifInState", "create", "update", "destroy",
                "onDestroyRemoveContents", "onSuccessSetIsDefault")
            || !JmapMethodHelpers.TryGetRequiredString(arguments, "accountId", out var accountId)
            || !JmapMethodHelpers.TryGetOptionalString(arguments, "ifInState", out var ifInState)
            || !JmapMethodHelpers.TryGetOptionalBoolean(
                arguments,
                "onDestroyRemoveContents",
                false,
                out var removeContents)
            || !JmapMethodHelpers.TryGetOptionalString(
                arguments,
                "onSuccessSetIsDefault",
                out var requestedDefault)
            || !JmapContactArguments.TryGetObjectMap(arguments, "create", out var creates)
            || !JmapContactArguments.TryGetObjectMap(arguments, "update", out var updates)
            || !JmapContactArguments.TryGetDestroy(arguments, out var destroys)
            || !JmapMethodHelpers.AreValidCreationIds(creates?.Keys)
            || !JmapMethodHelpers.AreValidIdReferences(updates?.Keys, context)
            || !JmapMethodHelpers.AreValidIdReferences(destroys, context)
            || requestedDefault is not null
                && (requestedDefault.Length == 0
                    || !context.TryGetReferenceKey(requestedDefault, out _)
                        && !JmapId.IsValidId(requestedDefault)))
        {
            return JmapMethodResponse.Error("invalidArguments");
        }

        var operationCount = (creates?.Count ?? 0) + (updates?.Count ?? 0) + (destroys?.Count ?? 0);
        if (operationCount > environment.Jmap.MaxObjectsInSet)
            return JmapMethodResponse.Error("requestTooLarge");
        var account = await accounts.GetContactAccountAsync(context.User, accountId, cancellationToken).ConfigureAwait(false);
        if (account is null)
            return JmapMethodResponse.Error(await accounts.GetContactAccountErrorAsync(
                context.User, accountId, cancellationToken).ConfigureAwait(false));
        await contacts.EnsureDefaultAddressBookAsync(context.User, cancellationToken).ConfigureAwait(false);
        var oldState = await states.GetStateAsync(
            account.InboxId,
            JmapConstants.AddressBookDataType,
            cancellationToken).ConfigureAwait(false);
        if (ifInState is not null && !string.Equals(ifInState, oldState, StringComparison.Ordinal))
            return JmapMethodResponse.Error("stateMismatch");

        var books = await contacts.LoadAddressBooksAsync(account.UserId, true, cancellationToken).ConfigureAwait(false);
        var byId = books.ToDictionary(book => JmapId.AddressBook(book.Id), StringComparer.Ordinal);
        var created = new JsonObject();
        var updated = new JsonObject();
        var destroyed = new JsonArray();
        var notCreated = new JsonObject();
        var notUpdated = new JsonObject();
        var notDestroyed = new JsonObject();
        var destroyedRequests = (destroys ?? []).Select(context.ResolveId)
            .Where(id => id is not null).ToHashSet(StringComparer.Ordinal);
        var collectionCount = await database.DavCollections.CountAsync(collection =>
            collection.UserId == account.UserId
            && collection.Slug != "schedule-inbox"
            && collection.Slug != "schedule-outbox",
            cancellationToken).ConfigureAwait(false);
        var remainingCapacity = Math.Max(
            0,
            environment.Dav.MaxCollectionsPerUser - collectionCount);
        var now = DateTime.UtcNow;

        if (creates is not null)
        {
            foreach (var item in creates)
            {
                if (remainingCapacity == 0)
                {
                    notCreated[item.Key] = JmapMethodHelpers.SetError("overQuota");
                    continue;
                }
                if (item.Value.TryGetPropertyValue("shareWith", out var requestedShares)
                    && requestedShares is not null)
                {
                    notCreated[item.Key] = JmapMethodHelpers.SetError("forbidden");
                    continue;
                }
                if (!TryParseBook(item.Value, true, out var parsed, out var invalid))
                {
                    notCreated[item.Key] = JmapMethodHelpers.SetError(
                        "invalidProperties", properties: invalid);
                    continue;
                }
                var id = Guid.CreateVersion7();
                var book = new DavCollectionDB
                {
                    Id = id,
                    UserId = account.UserId,
                    CollectionType = DavCollectionDB.AddressBookType,
                    Slug = "jmap-" + id.ToString("N"),
                    DisplayName = parsed.Name!,
                    Description = parsed.Description,
                    SortOrder = parsed.SortOrder,
                    IsDefault = false,
                    IsSubscribed = parsed.IsSubscribed,
                    Components = [],
                    CreatedAt = now,
                    UpdatedAt = now,
                };
                database.DavCollections.Add(book);
                books.Add(book);
                var idString = JmapId.AddressBook(id);
                byId[idString] = book;
                context.CreatedIds[item.Key] = idString;
                created[item.Key] = CreatedResponse(item.Value, book);
                remainingCapacity--;
            }
        }

        if (updates is not null)
        {
            foreach (var item in updates)
            {
                var resolved = context.ResolveId(item.Key);
                if (resolved is not null && destroyedRequests.Contains(resolved))
                {
                    notUpdated[item.Key] = JmapMethodHelpers.SetError("willDestroy");
                    continue;
                }
                if (resolved is null || !byId.TryGetValue(resolved, out var book))
                {
                    notUpdated[item.Key] = JmapMethodHelpers.SetError("notFound");
                    continue;
                }
                var current = JmapContactStore.BuildAddressBook(book);
                if (!JmapMethodHelpers.TryApplyPatchAllowingUnchangedProperties(
                        current,
                        item.Value,
                        MutableProperties,
                        out var patched,
                        out var immutable)
                    || immutable.Count > 0)
                {
                    notUpdated[item.Key] = JmapMethodHelpers.SetError(
                        "invalidProperties", properties: immutable.Count == 0 ? ["/"] : immutable);
                    continue;
                }
                if (patched.TryGetPropertyValue("shareWith", out var requestedShares)
                    && requestedShares is not null)
                {
                    notUpdated[item.Key] = JmapMethodHelpers.SetError("forbidden");
                    continue;
                }
                if (!TryParseBook(patched, false, out var parsed, out var invalid))
                {
                    notUpdated[item.Key] = JmapMethodHelpers.SetError(
                        "invalidProperties", properties: invalid);
                    continue;
                }
                book.DisplayName = parsed.Name!;
                book.Description = parsed.Description;
                book.SortOrder = parsed.SortOrder;
                book.IsSubscribed = parsed.IsSubscribed;
                book.UpdatedAt = now;
                updated[resolved] = null;
            }
        }

        if (destroys is not null)
        {
            foreach (var requested in destroys)
            {
                var resolved = context.ResolveId(requested);
                if (resolved is null || !byId.TryGetValue(resolved, out var book))
                {
                    notDestroyed[requested] = JmapMethodHelpers.SetError("notFound");
                    continue;
                }
                if (book.IsDefault || string.Equals(book.Slug, "default", StringComparison.Ordinal))
                {
                    notDestroyed[requested] = JmapMethodHelpers.SetError("forbidden");
                    continue;
                }
                var resources = await database.DavResources
                    .Where(resource => resource.CollectionId == book.Id)
                    .ToListAsync(cancellationToken).ConfigureAwait(false);
                if (resources.Count > 0 && !removeContents)
                {
                    notDestroyed[requested] = JmapMethodHelpers.SetError("addressBookHasContents");
                    continue;
                }
                if (resources.Count > 0)
                    database.DavResources.RemoveRange(resources);
                database.DavCollections.Remove(book);
                byId.Remove(resolved);
                destroyed.Add(resolved);
            }
        }

        if (notCreated.Count == 0 && notUpdated.Count == 0 && notDestroyed.Count == 0
            && requestedDefault is not null)
        {
            var resolvedDefault = context.ResolveId(requestedDefault);
            if (resolvedDefault is not null && byId.TryGetValue(resolvedDefault, out var nextDefault))
            {
                foreach (var book in books.Where(book => book.IsDefault && book.Id != nextDefault.Id))
                {
                    book.IsDefault = false;
                    book.UpdatedAt = now;
                    var id = JmapId.AddressBook(book.Id);
                    if (created.FirstOrDefault(entry =>
                            context.CreatedIds.TryGetValue(entry.Key, out var createdId)
                            && string.Equals(createdId, id, StringComparison.Ordinal)).Value is JsonObject createdBook)
                    {
                        createdBook["isDefault"] = false;
                        SetMayDelete(createdBook, !string.Equals(
                            book.Slug,
                            "default",
                            StringComparison.Ordinal));
                    }
                    else
                    {
                        updated[id] = new JsonObject
                        {
                            ["isDefault"] = false,
                            ["myRights"] = Rights(!string.Equals(
                                book.Slug,
                                "default",
                                StringComparison.Ordinal)),
                        };
                    }
                }
                if (!nextDefault.IsDefault)
                {
                    nextDefault.IsDefault = true;
                    nextDefault.UpdatedAt = now;
                    var createdEntry = context.CreatedIds.FirstOrDefault(entry => string.Equals(entry.Value, resolvedDefault, StringComparison.Ordinal));
                    if (!string.IsNullOrEmpty(createdEntry.Key)
                        && created[createdEntry.Key] is JsonObject createdBook)
                    {
                        createdBook["isDefault"] = true;
                        SetMayDelete(createdBook, false);
                    }
                    else
                    {
                        updated[resolvedDefault] = new JsonObject
                        {
                            ["isDefault"] = true,
                            ["myRights"] = Rights(false),
                        };
                    }
                }
            }
        }

        await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new JmapMethodResponse(Operation, new JsonObject
        {
            ["accountId"] = accountId,
            ["oldState"] = oldState,
            ["newState"] = await states.GetStateAsync(
                account.InboxId,
                JmapConstants.AddressBookDataType,
                cancellationToken).ConfigureAwait(false),
            ["created"] = created.Count == 0 ? null : created,
            ["updated"] = updated.Count == 0 ? null : updated,
            ["destroyed"] = destroyed.Count == 0 ? null : destroyed,
            ["notCreated"] = notCreated.Count == 0 ? null : notCreated,
            ["notUpdated"] = notUpdated.Count == 0 ? null : notUpdated,
            ["notDestroyed"] = notDestroyed.Count == 0 ? null : notDestroyed,
        });
    }

    private static JsonObject CreatedResponse(JsonObject request, DavCollectionDB book)
    {
        var response = new JsonObject
        {
            ["id"] = JmapId.AddressBook(book.Id),
            ["isDefault"] = false,
            ["shareWith"] = null,
            ["myRights"] = Rights(!string.Equals(
                book.Slug,
                "default",
                StringComparison.Ordinal)),
        };
        if (!request.ContainsKey("description")) response["description"] = null;
        if (!request.ContainsKey("sortOrder")) response["sortOrder"] = 0;
        if (!request.ContainsKey("isSubscribed")) response["isSubscribed"] = true;
        return response;
    }

    private static JsonObject Rights(bool mayDelete) => new()
    {
        ["mayRead"] = true,
        ["mayWrite"] = true,
        ["mayShare"] = false,
        ["mayDelete"] = mayDelete,
    };

    private static void SetMayDelete(JsonObject book, bool mayDelete)
    {
        if (book["myRights"] is JsonObject rights)
            rights["mayDelete"] = mayDelete;
    }

    private static bool TryParseBook(
        JsonObject value,
        bool create,
        out ParsedAddressBook parsed,
        out IReadOnlyList<string> invalidProperties)
    {
        var invalid = new HashSet<string>(StringComparer.Ordinal);
        string? name = null;
        if (!value.TryGetPropertyValue("name", out var nameNode)
            || nameNode is not JsonValue nameValue
            || !nameValue.TryGetValue<string>(out name)
            || string.IsNullOrEmpty(name)
            || !JmapJson.ContainsOnlyUnicodeScalars(name))
        {
            invalid.Add("name");
        }
        else
        {
            try
            {
                name = name.Normalize(NormalizationForm.FormC);
            }
            catch (ArgumentException)
            {
                invalid.Add("name");
            }
            if (name is not null && Encoding.UTF8.GetByteCount(name) > 255)
                invalid.Add("name");
        }

        string? description = null;
        if (value.TryGetPropertyValue("description", out var descriptionNode)
            && descriptionNode is not null
            && (descriptionNode is not JsonValue descriptionValue
                || !descriptionValue.TryGetValue<string>(out description)
                || description is null
                || description.Length > 1024))
        {
            invalid.Add("description");
        }
        long sortOrder = 0;
        if (!JmapMethodHelpers.TryGetOptionalUnsignedInt(value, "sortOrder", out var parsedOrder)
            || parsedOrder is > int.MaxValue)
        {
            invalid.Add("sortOrder");
        }
        else
        {
            sortOrder = parsedOrder ?? 0;
        }
        if (!JmapMethodHelpers.TryGetOptionalBoolean(value, "isSubscribed", true, out var subscribed))
            invalid.Add("isSubscribed");
        if (value.TryGetPropertyValue("shareWith", out var shareWith) && shareWith is not null)
            invalid.Add("shareWith");
        if (create)
        {
            foreach (var property in value.Select(item => item.Key).Where(property => property is not
                         ("name" or "description" or "sortOrder" or "isSubscribed" or "shareWith")))
            {
                invalid.Add(property);
            }
        }
        parsed = new ParsedAddressBook(name, description, checked((int)sortOrder), subscribed);
        invalidProperties = invalid.Order(StringComparer.Ordinal).ToArray();
        return invalid.Count == 0;
    }

    private sealed record ParsedAddressBook(
        string? Name,
        string? Description,
        int SortOrder,
        bool IsSubscribed);
}
