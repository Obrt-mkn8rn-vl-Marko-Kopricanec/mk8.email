using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using MimeKit;
using mk8.email.Infrastructure.Data;
using mk8.email.Configuration;
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
            cancellationToken).ConfigureAwait(false))
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
        var preexistingChanges = database.ChangeTracker.Entries<JmapChangeDB>()
            .Select(entry => entry.Entity)
            .ToHashSet();
        database.JmapIdentities.Add(identity);
        try
        {
            await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            // Two requests may both observe that the lazy default is absent.
            // The deterministic primary key makes one insert win; treat the
            // other as success once the winning row is visible.
            database.Entry(identity).State = EntityState.Detached;
            DetachPendingChanges(preexistingChanges);
            if (!await database.JmapIdentities.AnyAsync(
                    candidate => candidate.AccountId == account.InboxId,
                    cancellationToken).ConfigureAwait(false))
            {
                throw;
            }
        }
    }

    private void DetachPendingChanges(HashSet<JmapChangeDB> preexistingChanges)
    {
        foreach (var entry in database.ChangeTracker.Entries<JmapChangeDB>()
                     .Where(entry => entry.State == EntityState.Added
                         && !preexistingChanges.Contains(entry.Entity)))
        {
            entry.State = EntityState.Detached;
        }
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
        var accessible = await accounts.GetAccountsAsync(context.User, cancellationToken).ConfigureAwait(false);
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
