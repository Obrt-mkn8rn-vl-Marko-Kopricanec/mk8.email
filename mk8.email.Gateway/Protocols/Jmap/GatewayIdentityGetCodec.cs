using System.Collections.Frozen;
using System.Text.Json.Nodes;
using mk8.email.Contracts.Messaging;

namespace mk8.email.Gateway.Protocols.Jmap;

internal static class GatewayIdentityGetCodec
{
    private static readonly HashSet<string> AllowedProperties = new(
        ["id", "name", "email", "replyTo", "bcc", "textSignature", "htmlSignature", "mayDelete"],
        StringComparer.Ordinal);

    internal sealed record Call(
        MailIdentityReadCommand Command,
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
            || !TryParseIds(arguments, out var requestedIds, out var identityIds)
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
        if (accountId.Length != 33 || accountId[0] != 'A'
            || !Guid.TryParseExact(accountId.AsSpan(1), "N", out var accountGuid))
        {
            failure = "accountNotFound";
            return false;
        }
        call = new(new(accountGuid, requestedIds is null ? null : identityIds),
            accountId, requestedIds, properties, maximumObjects);
        return true;
    }

    public static (MailOperationKind Operation, JsonObject Data) Render(Call call, MailIdentityReadResult result)
    {
        if (result is null || !Enum.IsDefined(result.Status) || result.Identities is null)
            throw new InvalidOperationException("The Application returned an invalid identity read result.");
        if (result.Status == MailIdentityReadStatus.AccountNotFound)
            return Error("accountNotFound");
        if (result.Status == MailIdentityReadStatus.RequestTooLarge)
            return Error("requestTooLarge");
        if (result.State is null || result.Identities.Count > call.MaximumObjects)
            throw new InvalidOperationException("The Application returned an incomplete identity read result.");

        var byId = new Dictionary<string, MailIdentitySnapshot>(StringComparer.Ordinal);
        foreach (var identity in result.Identities)
        {
            if (identity.Id == Guid.Empty || identity.Name is null || identity.Email is null
                || identity.TextSignature is null || identity.HtmlSignature is null
                || !byId.TryAdd(FormatId(identity.Id), identity))
                throw new InvalidOperationException("The Application returned invalid identity data.");
        }
        var list = new JsonArray();
        var notFound = new JsonArray();
        foreach (var id in (call.RequestedIds ?? byId.Keys.ToArray()).Distinct(StringComparer.Ordinal))
        {
            if (byId.TryGetValue(id, out var identity))
                list.Add(Build(identity, call.Properties));
            else
                notFound.Add(id);
        }
        return (MailOperationKind.ReadSenderIdentities, new JsonObject
        {
            ["accountId"] = call.AccountId,
            ["state"] = result.State,
            ["list"] = list,
            ["notFound"] = notFound,
        });
    }

    private static JsonObject Build(MailIdentitySnapshot identity, IReadOnlySet<string>? properties)
    {
        var result = new JsonObject { ["id"] = FormatId(identity.Id) };
        if (Wants("name")) result["name"] = identity.Name;
        if (Wants("email")) result["email"] = identity.Email;
        if (Wants("replyTo")) result["replyTo"] = ToJson(identity.ReplyTo);
        if (Wants("bcc")) result["bcc"] = ToJson(identity.Bcc);
        if (Wants("textSignature")) result["textSignature"] = identity.TextSignature;
        if (Wants("htmlSignature")) result["htmlSignature"] = identity.HtmlSignature;
        if (Wants("mayDelete")) result["mayDelete"] = identity.MayDelete;
        return result;

        bool Wants(string name) => properties is null || properties.Contains(name);
    }

    private static JsonArray? ToJson(MailIdentityAddressListSnapshot? addresses)
    {
        if (addresses is null)
            return null;
        var result = new JsonArray();
        foreach (var address in addresses.Addresses)
        {
            if (address is null || address.Email is null)
                throw new InvalidOperationException("The Application returned an invalid identity address.");
            result.Add(new JsonObject { ["name"] = address.Name, ["email"] = address.Email });
        }
        return result;
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

    private static bool TryParseIds(JsonObject arguments, out string[]? ids, out Guid[] identityIds)
    {
        ids = null;
        identityIds = [];
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
            if (id.Length == 33 && id[0] == 'I'
                && Guid.TryParseExact(id.AsSpan(1), "N", out var identityId)
                && string.Equals(id, FormatId(identityId), StringComparison.Ordinal))
                parsed.Add(identityId);
        }
        ids = requested.ToArray();
        identityIds = parsed.ToArray();
        return true;
    }

    private static string FormatId(Guid id) => $"I{id:N}";

    private static (MailOperationKind Operation, JsonObject Data) Error(string type) =>
        (MailOperationKind.Failure, new JsonObject { ["type"] = type });
}
