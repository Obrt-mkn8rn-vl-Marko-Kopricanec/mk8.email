using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MimeKit;
using mk8.email.Application.Interfaces;
using mk8.email.Contracts.Messaging;
using mk8.email.Infrastructure.Data;
using mk8.email.Infrastructure.Models;

namespace mk8.email.Jmap;

internal sealed class MailIdentityMutationService(
    EmailDbContext database,
    JmapAccountService accounts,
    JmapIdentityService identities,
    JmapStateService states) : IMailIdentityMutationService
{
    private static readonly JsonSerializerOptions AddressOptions = new(JsonSerializerDefaults.Web);

    public async Task<MailIdentityMutationResult> MutateAsync(
        MailIdentityMutationCommand command, AuthenticatedMailUser user, CancellationToken cancellationToken)
    {
        var account = await accounts.GetAccountByInboxIdAsync(user, command.AccountId, cancellationToken)
            .ConfigureAwait(false);
        if (account is null)
            return Empty(MailIdentityMutationStatus.AccountNotFound);
        await identities.EnsureDefaultAsync(account, cancellationToken).ConfigureAwait(false);
        var oldState = await states.GetStateAsync(account.InboxId, JmapConstants.IdentityDataType,
            cancellationToken).ConfigureAwait(false);
        if (command.IfInState is not null && !string.Equals(command.IfInState, oldState, StringComparison.Ordinal))
            return Empty(MailIdentityMutationStatus.StateMismatch);

        var created = new List<MailIdentityCreateOutcome>(command.Creates.Count);
        var updated = new List<MailIdentityUpdateOutcome>(command.Updates.Count);
        var destroyed = new List<MailIdentityDestroyOutcome>(command.Destroys.Count);
        var createdIds = new Dictionary<string, Guid>(StringComparer.Ordinal);
        for (var index = 0; index < command.Creates.Count; index++)
        {
            var outcome = await CreateOneAsync(command.Creates[index], account.InboxId, user,
                cancellationToken).ConfigureAwait(false);
            created.Add(outcome);
            if (outcome.IdentityId is { } id) createdIds[outcome.CreationId] = id;
        }
        var destroyIds = command.Destroys.Select(item => Resolve(item.Target, createdIds))
            .Where(id => id is not null).Select(id => id!.Value).ToHashSet();
        for (var index = 0; index < command.Updates.Count; index++)
            updated.Add(await UpdateOneAsync(command.Updates[index], account.InboxId, createdIds,
                destroyIds, cancellationToken).ConfigureAwait(false));
        for (var index = 0; index < command.Destroys.Count; index++)
            destroyed.Add(await DestroyOneAsync(command.Destroys[index], account.InboxId, createdIds,
                cancellationToken).ConfigureAwait(false));
        var newState = await states.GetStateAsync(account.InboxId, JmapConstants.IdentityDataType,
            cancellationToken).ConfigureAwait(false);
        return new(MailIdentityMutationStatus.Ok, oldState, newState, created, updated, destroyed);
    }

    private async Task<MailIdentityCreateOutcome> CreateOneAsync(
        MailIdentityCreate item, Guid accountId, AuthenticatedMailUser user, CancellationToken token)
    {
        var values = item.Values;
        if (values is null) return new(item.CreationId, null, MailIdentityMutationError.Skipped);
        if (!ValidValues(values, requireEmail: true))
            return new(item.CreationId, null, MailIdentityMutationError.InvalidProperties);
        if (!await identities.CanUseAddressAsync(user, values.Email!, token).ConfigureAwait(false))
            return new(item.CreationId, null, MailIdentityMutationError.ForbiddenFrom);
        var id = Guid.CreateVersion7();
        var identity = new JmapIdentityDB
        {
            Id = id,
            IdentityObjectId = JmapId.Identity(id),
            AccountId = accountId,
            Name = values.Name,
            Email = values.Email!,
            ReplyToJson = EncodeAddresses(values.ReplyTo),
            BccJson = EncodeAddresses(values.Bcc),
            TextSignature = values.TextSignature,
            HtmlSignature = values.HtmlSignature,
            MayDelete = true,
        };
        await database.JmapIdentities.AddAsync(identity, token).ConfigureAwait(false);
        await database.SaveChangesAsync(token).ConfigureAwait(false);
        return new(item.CreationId, id, MailIdentityMutationError.None);
    }

    private async Task<MailIdentityUpdateOutcome> UpdateOneAsync(
        MailIdentityUpdate item, Guid accountId, Dictionary<string, Guid> created,
        HashSet<Guid> destroyIds, CancellationToken token)
    {
        var id = Resolve(item.Target, created);
        if (id is null) return new(item.RequestedId, null, MailIdentityMutationError.NotFound, null);
        var identity = await database.JmapIdentities.FirstOrDefaultAsync(
            candidate => candidate.Id == id.Value && candidate.AccountId == accountId,
            token).ConfigureAwait(false);
        if (identity is null) return new(item.RequestedId, null, MailIdentityMutationError.NotFound, null);
        if (destroyIds.Contains(id.Value))
            return new(item.RequestedId, null, MailIdentityMutationError.WillDestroy, null);
        if (item.Patch is null) return new(item.RequestedId, null, MailIdentityMutationError.Skipped, null);
        var immutable = ImmutableMismatches(identity, item.Patch);
        if (immutable.Count > 0)
            return new(item.RequestedId, null, MailIdentityMutationError.InvalidProperties, immutable);
        if (!TryApply(identity, item.Patch))
            return new(item.RequestedId, null, MailIdentityMutationError.InvalidProperties, null);
        await database.SaveChangesAsync(token).ConfigureAwait(false);
        return new(item.RequestedId, id, MailIdentityMutationError.None, null);
    }

    private async Task<MailIdentityDestroyOutcome> DestroyOneAsync(
        MailIdentityDestroy item, Guid accountId, Dictionary<string, Guid> created, CancellationToken token)
    {
        var id = Resolve(item.Target, created);
        if (id is null) return new(item.RequestedId, null, MailIdentityMutationError.NotFound);
        var identity = await database.JmapIdentities.FirstOrDefaultAsync(
            candidate => candidate.Id == id.Value && candidate.AccountId == accountId,
            token).ConfigureAwait(false);
        if (identity is null) return new(item.RequestedId, null, MailIdentityMutationError.NotFound);
        if (!identity.MayDelete) return new(item.RequestedId, null, MailIdentityMutationError.Forbidden);
        database.JmapIdentities.Remove(identity);
        await database.SaveChangesAsync(token).ConfigureAwait(false);
        return new(item.RequestedId, id, MailIdentityMutationError.None);
    }

    private static bool TryApply(JmapIdentityDB identity, MailIdentityPatch patch)
    {
        var revised = new MailIdentityValues(
            patch.SetName ? patch.Name ?? string.Empty : identity.Name,
            identity.Email,
            patch.SetReplyTo ? patch.ReplyTo : DecodeAddresses(identity.ReplyToJson),
            patch.SetBcc ? patch.Bcc : DecodeAddresses(identity.BccJson),
            patch.SetTextSignature ? patch.TextSignature ?? string.Empty : identity.TextSignature,
            patch.SetHtmlSignature ? patch.HtmlSignature ?? string.Empty : identity.HtmlSignature);
        if (!ValidValues(revised, requireEmail: true)) return false;
        identity.Name = revised.Name;
        identity.ReplyToJson = EncodeAddresses(revised.ReplyTo);
        identity.BccJson = EncodeAddresses(revised.Bcc);
        identity.TextSignature = revised.TextSignature;
        identity.HtmlSignature = revised.HtmlSignature;
        identity.UpdatedAt = DateTime.UtcNow;
        return true;
    }

    private static MailIdentityMutationResult Empty(MailIdentityMutationStatus status) =>
        new(status, null, null, [], [], []);

    private static Guid? Resolve(MailIdentityTarget target, Dictionary<string, Guid> created)
    {
        if (target.ExistingId is not null) return target.ExistingId;
        return target.CreatedKey is not null && created.TryGetValue(target.CreatedKey, out var id)
            ? id : null;
    }

    private static bool ValidValues(MailIdentityValues values, bool requireEmail) =>
        values.Name is { Length: <= 255 }
        && values.TextSignature is { Length: <= 100_000 }
        && values.HtmlSignature is { Length: <= 100_000 }
        && values.Email is { Length: <= 320 }
        && (!requireEmail || !string.IsNullOrWhiteSpace(values.Email))
        && ValidAddresses(values.ReplyTo) && ValidAddresses(values.Bcc);

    private static bool ValidAddresses(MailIdentityAddressListSnapshot? addresses) =>
        addresses is null || addresses.Addresses is not null && addresses.Addresses.All(address =>
            address is not null && address.Email is { Length: <= 320 }
            && MailboxAddress.TryParse(address.Email, out var mailbox)
            && string.Equals(mailbox.Address, address.Email, StringComparison.OrdinalIgnoreCase));

    private static List<MailIdentityImmutableField> ImmutableMismatches(
        JmapIdentityDB identity, MailIdentityPatch patch)
    {
        var fields = new List<MailIdentityImmutableField>(3);
        if (patch.AssertId && !string.Equals(patch.Id, identity.IdentityObjectId, StringComparison.Ordinal))
            fields.Add(MailIdentityImmutableField.Id);
        if (patch.AssertEmail && !string.Equals(patch.Email, identity.Email, StringComparison.Ordinal))
            fields.Add(MailIdentityImmutableField.Email);
        if (patch.AssertMayDelete && patch.MayDelete != identity.MayDelete)
            fields.Add(MailIdentityImmutableField.MayDelete);
        return fields;
    }

    private static string? EncodeAddresses(MailIdentityAddressListSnapshot? addresses) =>
        addresses is null ? null : JsonSerializer.Serialize(addresses.Addresses, AddressOptions);

    private static MailIdentityAddressListSnapshot? DecodeAddresses(string? json) =>
        json is null ? null : new(JsonSerializer.Deserialize<MailIdentityAddressSnapshot[]>(json, AddressOptions)
            ?? throw new InvalidOperationException("Stored identity addresses are invalid."));
}
