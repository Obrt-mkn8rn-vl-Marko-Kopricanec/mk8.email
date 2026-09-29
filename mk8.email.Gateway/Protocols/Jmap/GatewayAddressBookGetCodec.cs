using System.Collections.Frozen;
using System.Text.Json.Nodes;
using mk8.email.Contracts.Messaging;

namespace mk8.email.Gateway.Protocols.Jmap;

internal static class GatewayAddressBookGetCodec
{
    private static readonly HashSet<string> AllowedProperties = new(
        ["id", "name", "description", "sortOrder", "isDefault", "isSubscribed", "shareWith", "myRights"],
        StringComparer.Ordinal);

    internal sealed record Call(
        MailAddressBookReadCommand Command,
        string AccountId,
        IReadOnlyList<string>? RequestedIds,
        IReadOnlySet<string>? Properties,
        int MaximumObjects);

    public static bool TryParse(JsonObject arguments, int maximumObjects, out Call? call, out string? failure)
    {
        call = null;
        failure = null;
        if (arguments.Any(property => property.Key is not ("accountId" or "ids" or "properties"))
            || arguments["accountId"] is not JsonValue accountNode
            || !accountNode.TryGetValue<string>(out var accountId)
            || accountId is null
            || !TryParseIds(arguments, out var requestedIds, out var bookIds))
        {
            failure = "invalidArguments";
            return false;
        }
        if (requestedIds?.Length > maximumObjects)
        {
            failure = "requestTooLarge";
            return false;
        }
        if (!TryParseProperties(arguments, out var properties))
        {
            failure = "invalidArguments";
            return false;
        }
        if (accountId.Length != 33 || accountId[0] != 'A'
            || !Guid.TryParseExact(accountId.AsSpan(1), "N", out var accountGuid))
        {
            failure = "accountNotFound";
            return false;
        }
        var eligible = string.Equals(accountId, FormatId('A', accountGuid), StringComparison.Ordinal);
        call = new(new(accountGuid, requestedIds is null ? null : bookIds, eligible),
            accountId, requestedIds, properties, maximumObjects);
        return true;
    }

    public static (MailOperationKind Operation, JsonObject Data) Render(Call call, MailAddressBookReadResult result)
    {
        if (result is null || !Enum.IsDefined(result.Status) || result.Books is null)
            throw new InvalidOperationException("The Application returned an invalid address-book read result.");
        if (result.Status == MailAddressBookReadStatus.AccountNotFound)
            return Error("accountNotFound");
        if (result.Status == MailAddressBookReadStatus.AccountNotSupported)
            return Error("accountNotSupportedByMethod");
        if (result.Status == MailAddressBookReadStatus.RequestTooLarge)
            return Error("requestTooLarge");
        if (result.State is null || result.Books.Count > call.MaximumObjects)
            throw new InvalidOperationException("The Application returned an incomplete address-book read result.");

        var byId = new Dictionary<string, MailAddressBookSnapshot>(StringComparer.Ordinal);
        foreach (var book in result.Books)
        {
            if (book.Id == Guid.Empty || book.Name is null
                || !byId.TryAdd(FormatId('D', book.Id), book))
                throw new InvalidOperationException("The Application returned invalid address-book data.");
        }
        var list = new JsonArray();
        var notFound = new JsonArray();
        foreach (var id in (call.RequestedIds ?? byId.Keys.ToArray()).Distinct(StringComparer.Ordinal))
        {
            if (byId.TryGetValue(id, out var book))
                list.Add(Build(book, call.Properties));
            else
                notFound.Add(id);
        }
        return (MailOperationKind.ReadAddressBooks, new JsonObject
        {
            ["accountId"] = call.AccountId,
            ["state"] = result.State,
            ["list"] = list,
            ["notFound"] = notFound,
        });
    }

    private static JsonObject Build(MailAddressBookSnapshot book, IReadOnlySet<string>? properties)
    {
        var result = new JsonObject { ["id"] = FormatId('D', book.Id) };
        if (Wants("name")) result["name"] = book.Name;
        if (Wants("description")) result["description"] = book.Description;
        if (Wants("sortOrder")) result["sortOrder"] = book.SortOrder;
        if (Wants("isDefault")) result["isDefault"] = book.IsDefault;
        if (Wants("isSubscribed")) result["isSubscribed"] = book.IsSubscribed;
        if (Wants("shareWith")) result["shareWith"] = null;
        if (Wants("myRights")) result["myRights"] = new JsonObject
        {
            ["mayRead"] = true,
            ["mayWrite"] = true,
            ["mayShare"] = false,
            ["mayDelete"] = !book.IsProtected,
        };
        return result;

        bool Wants(string name) => properties is null || properties.Contains(name);
    }

    private static bool TryParseProperties(JsonObject arguments, out FrozenSet<string>? properties)
    {
        properties = null;
        if (!arguments.TryGetPropertyValue("properties", out var node) || node is null)
            return true;
        if (node is not JsonArray array)
            return false;
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in array)
        {
            if (item is not JsonValue value || !value.TryGetValue<string>(out var name)
                || name is null || !AllowedProperties.Contains(name))
                return false;
            names.Add(name);
        }
        properties = names.ToFrozenSet(StringComparer.Ordinal);
        return true;
    }

    private static bool TryParseIds(JsonObject arguments, out string[]? ids, out Guid[] bookIds)
    {
        ids = null;
        bookIds = [];
        if (!arguments.TryGetPropertyValue("ids", out var node) || node is null)
            return true;
        if (node is not JsonArray array)
            return false;
        var requested = new List<string>(array.Count);
        var parsed = new List<Guid>(array.Count);
        foreach (var item in array)
        {
            if (item is not JsonValue value || !value.TryGetValue<string>(out var id)
                || id is null || !GatewayJmapBatchCodec.IsId(id))
                return false;
            requested.Add(id);
            if (id.Length == 33 && id[0] == 'D'
                && Guid.TryParseExact(id.AsSpan(1), "N", out var bookId)
                && string.Equals(id, FormatId('D', bookId), StringComparison.Ordinal))
                parsed.Add(bookId);
        }
        ids = requested.ToArray();
        bookIds = parsed.ToArray();
        return true;
    }

    private static string FormatId(char prefix, Guid id) => $"{prefix}{id:N}";

    private static (MailOperationKind Operation, JsonObject Data) Error(string type) =>
        (MailOperationKind.Failure, new JsonObject { ["type"] = type });
}
