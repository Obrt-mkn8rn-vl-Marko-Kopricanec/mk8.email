using System.Collections.Frozen;
using System.Text.Json;
using System.Text.Json.Nodes;
using mk8.email.Contracts.Messaging;

namespace mk8.email.Gateway.Protocols.Jmap;

internal static class GatewayContactCardGetCodec
{
    private static readonly JsonDocumentOptions CardDocumentOptions = new() { MaxDepth = 256 };

    internal sealed record Call(
        MailContactReadCommand Command,
        string AccountId,
        IReadOnlyList<string>? RequestedIds,
        FrozenSet<string>? Properties,
        int MaximumObjects);

    public static bool TryParse(JsonObject arguments, int maximumObjects,
        out Call? call, out string? failure)
    {
        call = null;
        failure = null;
        if (arguments.Any(item => item.Key is not ("accountId" or "ids" or "properties"))
            || arguments["accountId"] is not JsonValue accountNode
            || !accountNode.TryGetValue<string>(out var accountId)
            || accountId is null
            || !TryParseIds(arguments, out var requestedIds, out var cardIds)
            || !TryParseProperties(arguments, out var properties))
        {
            failure = "invalidArguments";
            return false;
        }
        if (requestedIds?.Length > maximumObjects)
        {
            failure = "requestTooLarge";
            return false;
        }
        if (properties is not null && properties.Any(property =>
            !GatewayContactCardPropertyPolicy.IsSupported(property)))
        {
            failure = "invalidArguments";
            return false;
        }
        var parseable = accountId.Length == 33 && accountId[0] == 'A'
            && Guid.TryParseExact(accountId.AsSpan(1), "N", out var parsedAccount);
        var accountGuid = parseable ? Guid.ParseExact(accountId.AsSpan(1), "N") : Guid.Empty;
        var eligible = parseable && string.Equals(accountId, $"A{accountGuid:N}", StringComparison.Ordinal);
        call = new(new(accountGuid, parseable, eligible, requestedIds is null ? null : cardIds),
            accountId, requestedIds, properties, maximumObjects);
        return true;
    }

    public static (MailOperationKind Operation, JsonObject Data) Render(Call call, MailContactReadResult result)
    {
        if (result is null || !Enum.IsDefined(result.Status) || result.Cards is null)
            throw new InvalidOperationException("The Application returned an invalid contact read.");
        if (result.Status == MailContactReadStatus.AccountNotFound)
            return Error("accountNotFound");
        if (result.Status == MailContactReadStatus.AccountNotSupported)
            return Error("accountNotSupportedByMethod");
        if (result.Status == MailContactReadStatus.RequestTooLarge)
            return Error("requestTooLarge");
        if (result.Status != MailContactReadStatus.Ok || result.State is null
            || result.Cards.Count > call.MaximumObjects)
            throw new InvalidOperationException("The Application returned an incomplete contact read.");
        var byId = new Dictionary<string, (MailContactCardSnapshot Snapshot, JsonObject Card)>(StringComparer.Ordinal);
        foreach (var snapshot in result.Cards)
        {
            if (snapshot is null || snapshot.Uid is null || snapshot.CardJson is null)
                throw new InvalidOperationException("The Application returned invalid contact data.");
            JsonObject card;
            try
            {
                card = JsonNode.Parse(snapshot.CardJson, documentOptions: CardDocumentOptions) as JsonObject
                    ?? throw new InvalidOperationException("The Application returned invalid contact data.");
            }
            catch (JsonException exception)
            {
                throw new InvalidOperationException("The Application returned malformed contact data.", exception);
            }
            if (card["@type"] is not JsonValue type || !type.TryGetValue<string>(out var typeName)
                || !string.Equals(typeName, "Card", StringComparison.Ordinal)
                || card["version"] is not JsonValue version || !version.TryGetValue<string>(out var versionName)
                || !string.Equals(versionName, "1.0", StringComparison.Ordinal)
                || card["uid"] is not JsonValue uid || !uid.TryGetValue<string>(out var cardUid)
                || !string.Equals(cardUid, snapshot.Uid, StringComparison.Ordinal)
                || card.ContainsKey("id") || card.ContainsKey("addressBookIds"))
                throw new InvalidOperationException("The Application returned inconsistent contact data.");
            if (!byId.TryAdd(CardId(snapshot.Id), (snapshot, card)))
                throw new InvalidOperationException("The Application returned duplicate contact data.");
        }
        var list = new JsonArray();
        var notFound = new JsonArray();
        foreach (var id in (call.RequestedIds ?? byId.Keys.ToArray()).Distinct(StringComparer.Ordinal))
        {
            if (byId.TryGetValue(id, out var item))
                list.Add(BuildCard(id, item.Snapshot.AddressBookId, item.Card, call.Properties));
            else
                notFound.Add(id);
        }
        return (MailOperationKind.ReadContacts, new JsonObject
        {
            ["accountId"] = call.AccountId,
            ["state"] = result.State,
            ["list"] = list,
            ["notFound"] = notFound,
        });
    }

    private static JsonObject BuildCard(
        string id, Guid addressBookId, JsonObject source, FrozenSet<string>? properties)
    {
        var result = new JsonObject { ["id"] = id };
        if (properties is null)
        {
            foreach (var property in source)
                result[property.Key] = property.Value?.DeepClone();
            result["addressBookIds"] = AddressBooks(addressBookId);
            return result;
        }
        foreach (var property in properties)
        {
            if (string.Equals(property, "id", StringComparison.Ordinal)) continue;
            if (string.Equals(property, "addressBookIds", StringComparison.Ordinal))
                result[property] = AddressBooks(addressBookId);
            else if (source.TryGetPropertyValue(property, out var value))
                result[property] = value?.DeepClone();
        }
        return result;
    }

    private static JsonObject AddressBooks(Guid addressBookId) =>
        new() { [$"D{addressBookId:N}"] = true };

    private static bool TryParseIds(JsonObject arguments, out string[]? ids, out Guid[] parsed)
    {
        ids = null;
        parsed = [];
        if (!arguments.TryGetPropertyValue("ids", out var node) || node is null)
            return true;
        if (node is not JsonArray array)
            return false;
        var requested = new List<string>(array.Count);
        var cardIds = new List<Guid>(array.Count);
        foreach (var item in array)
        {
            if (item is not JsonValue value || !value.TryGetValue<string>(out var id)
                || id is null || !GatewayJmapBatchCodec.IsId(id))
                return false;
            requested.Add(id);
            if (id.Length == 33 && id[0] == 'C'
                && Guid.TryParseExact(id.AsSpan(1), "N", out var parsedId)
                && string.Equals(id, CardId(parsedId), StringComparison.Ordinal))
                cardIds.Add(parsedId);
        }
        ids = requested.ToArray();
        parsed = cardIds.ToArray();
        return true;
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
                || name is null)
                return false;
            names.Add(name);
        }
        properties = names.ToFrozenSet(StringComparer.Ordinal);
        return true;
    }

    private static string CardId(Guid id) => $"C{id:N}";

    private static (MailOperationKind Operation, JsonObject Data) Error(string type) =>
        (MailOperationKind.Failure, new JsonObject { ["type"] = type });
}
