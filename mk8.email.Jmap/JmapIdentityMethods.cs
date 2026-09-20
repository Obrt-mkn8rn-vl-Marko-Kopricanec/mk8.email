using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using MimeKit;
using mk8.email.Infrastructure.Data;
using mk8.email.Infrastructure.Environment;
using mk8.email.Infrastructure.Models;

namespace mk8.email.Jmap;

internal sealed class JmapIdentityService(
    EmailDbContext database,
    JmapAccountService accounts)
{
    public async Task EnsureDefaultAsync(
        JmapAccount account,
        CancellationToken cancellationToken)
    {
        if (await database.JmapIdentities.AnyAsync(
            identity => identity.AccountId == account.InboxId,
            cancellationToken))
        {
            return;
        }
        var identity = new JmapIdentityDB
        {
            Id = account.InboxId,
            IdentityObjectId = JmapId.Identity(account.InboxId),
            AccountId = account.InboxId,
            Email = account.Address,
            Name = string.Empty,
            MayDelete = false,
        };
        database.JmapIdentities.Add(identity);
        await database.SaveChangesAsync(cancellationToken);
    }

    public async Task<bool> CanUseAddressAsync(
        JmapInvocationContext context,
        string address,
        CancellationToken cancellationToken)
    {
        if (!MailboxAddress.TryParse(address, out var mailbox)
            || !string.Equals(mailbox.Address, address, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        var accessible = await accounts.GetAccountsAsync(context.User, cancellationToken);
        return accessible.Any(account => string.Equals(
            account.Address,
            mailbox.Address,
            StringComparison.OrdinalIgnoreCase));
    }

    public static JsonObject ToJson(
        JmapIdentityDB identity,
        IReadOnlySet<string>? properties = null)
    {
        var result = new JsonObject { ["id"] = JmapId.Identity(identity.Id) };
        if (Wants("name")) result["name"] = identity.Name;
        if (Wants("email")) result["email"] = identity.Email;
        if (Wants("replyTo")) result["replyTo"] = ParseAddresses(identity.ReplyToJson);
        if (Wants("bcc")) result["bcc"] = ParseAddresses(identity.BccJson);
        if (Wants("textSignature")) result["textSignature"] = identity.TextSignature;
        if (Wants("htmlSignature")) result["htmlSignature"] = identity.HtmlSignature;
        if (Wants("mayDelete")) result["mayDelete"] = identity.MayDelete;
        return result;

        bool Wants(string name) => properties is null || properties.Contains(name);
    }

    public static bool TryParseMutable(
        JsonObject value,
        bool requireEmail,
        out IdentityValues parsed,
        out JsonObject? error)
    {
        parsed = default;
        error = null;
        if (!TryGetDefaultString(value, "name", out var name)
            || !TryGetDefaultString(value, "email", out var email)
            || !TryGetDefaultString(value, "textSignature", out var textSignature)
            || !TryGetDefaultString(value, "htmlSignature", out var htmlSignature)
            || requireEmail && string.IsNullOrWhiteSpace(email)
            || name is { Length: > 255 }
            || email is { Length: > 320 }
            || textSignature is { Length: > 100_000 }
            || htmlSignature is { Length: > 100_000 }
            || !TryAddressArray(value, "replyTo", out var replyTo)
            || !TryAddressArray(value, "bcc", out var bcc))
        {
            error = JmapMethodHelpers.SetError("invalidProperties");
            return false;
        }
        parsed = new IdentityValues(
            name,
            email,
            replyTo,
            bcc,
            textSignature,
            htmlSignature);
        return true;
    }

    private static bool TryGetDefaultString(
        JsonObject value,
        string name,
        out string result)
    {
        result = string.Empty;
        if (!value.TryGetPropertyValue(name, out var node))
            return true;
        return node is JsonValue jsonValue
            && jsonValue.TryGetValue<string>(out result!)
            && result is not null;
    }

    private static bool TryAddressArray(
        JsonObject value,
        string name,
        out string? json)
    {
        json = null;
        if (!value.TryGetPropertyValue(name, out var node) || node is null)
            return true;
        if (node is not JsonArray array)
            return false;
        var normalized = new JsonArray();
        foreach (var item in array)
        {
            if (item is not JsonObject address
                || !JmapMethodHelpers.TryGetRequiredString(address, "email", out var email)
                || !JmapMethodHelpers.TryGetOptionalString(address, "name", out var displayName)
                || address.Any(property => property.Key is not ("name" or "email"))
                || email.Length > 320
                || !MailboxAddress.TryParse(email, out var mailbox)
                || !string.Equals(mailbox.Address, email, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            normalized.Add(new JsonObject
            {
                ["name"] = displayName,
                ["email"] = email,
            });
        }
        json = normalized.ToJsonString(JmapJson.SerializerOptions);
        return true;
    }

    private static JsonNode? ParseAddresses(string? value) =>
        value is null ? null : JsonNode.Parse(value);

    internal readonly record struct IdentityValues(
        string Name,
        string? Email,
        string? ReplyToJson,
        string? BccJson,
        string TextSignature,
        string HtmlSignature);
}

internal sealed class IdentityGetMethod(
    EmailDbContext database,
    JmapAccountService accounts,
    JmapIdentityService identities,
    JmapStateService states,
    EnvironmentConfig environment) : IJmapMethod
{
    private static readonly IReadOnlySet<string> Properties = new HashSet<string>(
        ["id", "name", "email", "replyTo", "bcc", "textSignature", "htmlSignature", "mayDelete"],
        StringComparer.Ordinal);

    public string Name => "Identity/get";
    public string Capability => JmapConstants.SubmissionCapability;

    public async Task<JmapMethodResponse> InvokeAsync(
        JmapInvocationContext context,
        JsonObject arguments,
        CancellationToken cancellationToken)
    {
        if (!JmapMethodHelpers.HasOnlyProperties(arguments, "accountId", "ids", "properties")
            || !JmapMethodHelpers.TryGetRequiredString(arguments, "accountId", out var accountId)
            || !JmapMethodHelpers.TryGetStringArray(arguments, "properties", true, out var requestedProperties)
            || !JmapEmailArguments.TryGetIds(arguments, "ids", context, true, out var requestedIds))
        {
            return JmapMethodResponse.Error("invalidArguments");
        }
        var properties = requestedProperties?.ToHashSet(StringComparer.Ordinal);
        if (properties is not null && properties.Any(property => !Properties.Contains(property)))
            return JmapMethodResponse.Error("invalidArguments");
        if (requestedIds is { Count: > 0 } && requestedIds.Count > environment.Jmap.MaxObjectsInGet)
            return JmapMethodResponse.Error("requestTooLarge");
        var account = await accounts.GetAccountAsync(context.User, accountId, cancellationToken);
        if (account is null)
            return JmapMethodResponse.Error("accountNotFound");
        await identities.EnsureDefaultAsync(account, cancellationToken);
        var all = await database.JmapIdentities
            .AsNoTracking()
            .Where(identity => identity.AccountId == account.InboxId)
            .OrderBy(identity => identity.CreatedAt)
            .ToListAsync(cancellationToken);
        if (requestedIds is null && all.Count > environment.Jmap.MaxObjectsInGet)
            return JmapMethodResponse.Error("requestTooLarge");
        var byId = all.ToDictionary(identity => JmapId.Identity(identity.Id), StringComparer.Ordinal);
        var list = new JsonArray();
        var notFound = new JsonArray();
        foreach (var id in (requestedIds ?? byId.Keys.ToArray()).Distinct(StringComparer.Ordinal))
        {
            if (byId.TryGetValue(id, out var identity))
                list.Add(JmapIdentityService.ToJson(identity, properties));
            else
                notFound.Add(id);
        }
        return new JmapMethodResponse(Name, new JsonObject
        {
            ["accountId"] = accountId,
            ["state"] = await states.GetStateAsync(account.InboxId, JmapConstants.IdentityDataType, cancellationToken),
            ["list"] = list,
            ["notFound"] = notFound,
        });
    }
}

internal sealed class IdentityChangesMethod(
    JmapAccountService accounts,
    JmapIdentityService identities,
    JmapStateService states,
    EnvironmentConfig environment) : IJmapMethod
{
    public string Name => "Identity/changes";
    public string Capability => JmapConstants.SubmissionCapability;

    public async Task<JmapMethodResponse> InvokeAsync(
        JmapInvocationContext context,
        JsonObject arguments,
        CancellationToken cancellationToken)
    {
        if (!JmapMethodHelpers.HasOnlyProperties(arguments, "accountId", "sinceState", "maxChanges")
            || !JmapMethodHelpers.TryGetRequiredString(arguments, "accountId", out var accountId)
            || !JmapMethodHelpers.TryGetRequiredString(arguments, "sinceState", out var sinceState)
            || !JmapMethodHelpers.TryGetOptionalUnsignedInt(arguments, "maxChanges", out var maxChanges)
            || maxChanges == 0)
            return JmapMethodResponse.Error("invalidArguments");
        var account = await accounts.GetAccountAsync(context.User, accountId, cancellationToken);
        if (account is null) return JmapMethodResponse.Error("accountNotFound");
        await identities.EnsureDefaultAsync(account, cancellationToken);
        var changes = await states.GetChangesAsync(
            account.InboxId,
            JmapConstants.IdentityDataType,
            sinceState,
            maxChanges,
            environment.Jmap.MaxObjectsInGet,
            cancellationToken);
        if (changes is null) return JmapMethodResponse.Error("cannotCalculateChanges");
        return new JmapMethodResponse(Name, new JsonObject
        {
            ["accountId"] = accountId,
            ["oldState"] = changes.OldState,
            ["newState"] = changes.NewState,
            ["hasMoreChanges"] = changes.HasMoreChanges,
            ["created"] = JmapMethodHelpers.ToJsonArray(changes.Created),
            ["updated"] = JmapMethodHelpers.ToJsonArray(changes.Updated),
            ["destroyed"] = JmapMethodHelpers.ToJsonArray(changes.Destroyed),
        });
    }
}
