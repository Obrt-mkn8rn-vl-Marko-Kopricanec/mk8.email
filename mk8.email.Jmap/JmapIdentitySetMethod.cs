using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using mk8.email.Infrastructure.Data;
using mk8.email.Infrastructure.Environment;
using mk8.email.Infrastructure.Models;

namespace mk8.email.Jmap;

internal sealed class IdentitySetMethod(
    EmailDbContext database,
    JmapAccountService accounts,
    JmapIdentityService identities,
    JmapStateService states,
    EnvironmentConfig environment) : IJmapMethod
{
    private static readonly IReadOnlySet<string> CreateProperties = new HashSet<string>(
        ["name", "email", "replyTo", "bcc", "textSignature", "htmlSignature"],
        StringComparer.Ordinal);
    private static readonly IReadOnlySet<string> UpdateProperties = new HashSet<string>(
        ["name", "replyTo", "bcc", "textSignature", "htmlSignature"],
        StringComparer.Ordinal);

    public string Name => "Identity/set";
    public string Capability => JmapConstants.SubmissionCapability;

    public async Task<JmapMethodResponse> InvokeAsync(
        JmapInvocationContext context,
        JsonObject arguments,
        CancellationToken cancellationToken)
    {
        if (!JmapMethodHelpers.HasOnlyProperties(
                arguments,
                "accountId",
                "ifInState",
                "create",
                "update",
                "destroy")
            || !JmapMethodHelpers.TryGetRequiredString(arguments, "accountId", out var accountId)
            || !JmapMethodHelpers.TryGetOptionalString(arguments, "ifInState", out var ifInState)
            || !JmapEmailMutationHelpers.TryGetObjectMap(arguments, "create", false, out var create)
            || !JmapEmailMutationHelpers.TryGetObjectMap(arguments, "update", false, out var update)
            || !TryDestroy(arguments, out var destroy)
            || !JmapMethodHelpers.AreValidCreationIds(create?.Keys)
            || !JmapMethodHelpers.AreValidIdReferences(update?.Keys)
            || !JmapMethodHelpers.AreValidIdReferences(destroy))
            return JmapMethodResponse.Error("invalidArguments");
        var operationCount = (create?.Count ?? 0) + (update?.Count ?? 0) + (destroy?.Count ?? 0);
        if (operationCount > environment.Jmap.MaxObjectsInSet)
            return JmapMethodResponse.Error("requestTooLarge");
        var account = await accounts.GetAccountAsync(context.User, accountId, cancellationToken);
        if (account is null) return JmapMethodResponse.Error("accountNotFound");
        await identities.EnsureDefaultAsync(account, cancellationToken);
        var oldState = await states.GetStateAsync(account.InboxId, JmapConstants.IdentityDataType, cancellationToken);
        if (ifInState is not null && !string.Equals(ifInState, oldState, StringComparison.Ordinal))
            return JmapMethodResponse.Error("stateMismatch");

        var created = new JsonObject();
        var updated = new JsonObject();
        var destroyed = new JsonArray();
        var notCreated = new JsonObject();
        var notUpdated = new JsonObject();
        var notDestroyed = new JsonObject();
        if (create is not null)
        {
            foreach (var item in create)
            {
                JmapIdentityService.IdentityValues values = default;
                JsonObject? parseError = null;
                if (!JmapId.IsValidId(item.Key)
                    || item.Value.Any(property => !CreateProperties.Contains(property.Key))
                    || !JmapIdentityService.TryParseMutable(
                        item.Value,
                        requireEmail: true,
                        out values,
                        out parseError))
                {
                    notCreated[item.Key] = parseError ?? JmapMethodHelpers.SetError("invalidProperties");
                    continue;
                }
                if (!await identities.CanUseAddressAsync(context, values.Email!, cancellationToken))
                {
                    notCreated[item.Key] = JmapMethodHelpers.SetError("forbiddenFrom");
                    continue;
                }
                var id = Guid.CreateVersion7();
                var identity = new JmapIdentityDB
                {
                    Id = id,
                    IdentityObjectId = JmapId.Identity(id),
                    AccountId = account.InboxId,
                    Name = values.Name,
                    Email = values.Email!,
                    ReplyToJson = values.ReplyToJson,
                    BccJson = values.BccJson,
                    TextSignature = values.TextSignature,
                    HtmlSignature = values.HtmlSignature,
                    MayDelete = true,
                };
                database.JmapIdentities.Add(identity);
                await database.SaveChangesAsync(cancellationToken);
                var wireId = JmapId.Identity(id);
                context.CreatedIds[item.Key] = wireId;
                created[item.Key] = new JsonObject
                {
                    ["id"] = wireId,
                    ["name"] = identity.Name,
                    ["replyTo"] = identity.ReplyToJson is null ? null : JsonNode.Parse(identity.ReplyToJson),
                    ["bcc"] = identity.BccJson is null ? null : JsonNode.Parse(identity.BccJson),
                    ["textSignature"] = identity.TextSignature,
                    ["htmlSignature"] = identity.HtmlSignature,
                    ["mayDelete"] = true,
                };
            }
        }
        if (update is not null)
        {
            foreach (var item in update)
            {
                var resolvedId = context.ResolveId(item.Key);
                if (!JmapId.TryParseIdentity(resolvedId, out var id))
                {
                    notUpdated[item.Key] = JmapMethodHelpers.SetError("notFound");
                    continue;
                }
                var identity = await database.JmapIdentities.SingleOrDefaultAsync(
                    candidate => candidate.Id == id && candidate.AccountId == account.InboxId,
                    cancellationToken);
                if (identity is null)
                {
                    notUpdated[item.Key] = JmapMethodHelpers.SetError("notFound");
                    continue;
                }
                var current = JmapIdentityService.ToJson(identity);
                JmapIdentityService.IdentityValues values = default;
                JsonObject? parseError = null;
                if (!JmapMethodHelpers.TryApplyPatchAllowingUnchangedProperties(
                        current,
                        item.Value,
                        UpdateProperties,
                        out var result,
                        out var invalidProperties))
                {
                    notUpdated[item.Key] = JmapMethodHelpers.SetError("invalidPatch");
                    continue;
                }
                if (invalidProperties.Count > 0)
                {
                    notUpdated[item.Key] = JmapMethodHelpers.SetError(
                        "invalidProperties",
                        properties: invalidProperties);
                    continue;
                }
                if (!JmapIdentityService.TryParseMutable(
                        result,
                        requireEmail: true,
                        out values,
                        out parseError))
                {
                    notUpdated[item.Key] = parseError ?? JmapMethodHelpers.SetError("invalidProperties");
                    continue;
                }
                identity.Name = values.Name;
                identity.ReplyToJson = values.ReplyToJson;
                identity.BccJson = values.BccJson;
                identity.TextSignature = values.TextSignature;
                identity.HtmlSignature = values.HtmlSignature;
                identity.UpdatedAt = DateTime.UtcNow;
                await database.SaveChangesAsync(cancellationToken);
                updated[resolvedId!] = null;
            }
        }
        if (destroy is not null)
        {
            foreach (var requestedId in destroy.Distinct(StringComparer.Ordinal))
            {
                var resolvedId = context.ResolveId(requestedId);
                if (!JmapId.TryParseIdentity(resolvedId, out var id))
                {
                    notDestroyed[requestedId] = JmapMethodHelpers.SetError("notFound");
                    continue;
                }
                var identity = await database.JmapIdentities.SingleOrDefaultAsync(
                    candidate => candidate.Id == id && candidate.AccountId == account.InboxId,
                    cancellationToken);
                if (identity is null)
                    notDestroyed[requestedId] = JmapMethodHelpers.SetError("notFound");
                else if (!identity.MayDelete)
                    notDestroyed[requestedId] = JmapMethodHelpers.SetError("forbidden");
                else
                {
                    database.JmapIdentities.Remove(identity);
                    await database.SaveChangesAsync(cancellationToken);
                    destroyed.Add(resolvedId);
                }
            }
        }
        return new JmapMethodResponse(Name, new JsonObject
        {
            ["accountId"] = accountId,
            ["oldState"] = oldState,
            ["newState"] = await states.GetStateAsync(account.InboxId, JmapConstants.IdentityDataType, cancellationToken),
            ["created"] = created.Count == 0 ? null : created,
            ["updated"] = updated.Count == 0 ? null : updated,
            ["destroyed"] = destroyed.Count == 0 ? null : destroyed,
            ["notCreated"] = notCreated.Count == 0 ? null : notCreated,
            ["notUpdated"] = notUpdated.Count == 0 ? null : notUpdated,
            ["notDestroyed"] = notDestroyed.Count == 0 ? null : notDestroyed,
        });
    }

    private static bool TryDestroy(JsonObject arguments, out IReadOnlyList<string>? values)
    {
        values = null;
        if (!arguments.TryGetPropertyValue("destroy", out var node) || node is null) return true;
        if (node is not JsonArray array) return false;
        var result = new List<string>(array.Count);
        foreach (var item in array)
        {
            if (item is not JsonValue value || !value.TryGetValue<string>(out var id) || id is null)
                return false;
            result.Add(id);
        }
        values = result;
        return true;
    }
}
