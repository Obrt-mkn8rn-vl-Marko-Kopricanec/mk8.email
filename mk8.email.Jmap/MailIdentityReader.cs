using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using mk8.email.Application.Interfaces;
using mk8.email.Configuration;
using mk8.email.Contracts.Messaging;
using mk8.email.Infrastructure.Data;
using mk8.email.Infrastructure.Models;

namespace mk8.email.Jmap;

internal sealed class MailIdentityReader(
    EmailDbContext database,
    JmapAccountService accounts,
    JmapIdentityService identities,
    JmapStateService states,
    EnvironmentConfig environment) : IMailIdentityReader
{
    private static readonly JsonSerializerOptions AddressOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
    };

    public async Task<MailIdentityReadResult> ReadAsync(
        MailIdentityReadCommand command,
        AuthenticatedMailUser user,
        CancellationToken cancellationToken)
    {
        var account = await accounts.GetAccountByInboxIdAsync(user, command.AccountId, cancellationToken)
            .ConfigureAwait(false);
        if (account is null)
            return Error(MailIdentityReadStatus.AccountNotFound);
        if (command.IdentityIds?.Count > environment.Jmap.MaxObjectsInGet)
            return Error(MailIdentityReadStatus.RequestTooLarge);

        await identities.EnsureDefaultAsync(account, cancellationToken).ConfigureAwait(false);
        var all = await database.JmapIdentities.AsNoTracking()
            .Where(identity => identity.AccountId == account.InboxId)
            .OrderBy(identity => identity.CreatedAt)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (command.IdentityIds is null && all.Count > environment.Jmap.MaxObjectsInGet)
            return Error(MailIdentityReadStatus.RequestTooLarge);

        HashSet<Guid>? requested = command.IdentityIds?.ToHashSet();
        var snapshots = all.Where(identity => requested is null || requested.Contains(identity.Id))
            .Select(identity => new MailIdentitySnapshot(identity.Id, identity.Name, identity.Email,
                DecodeAddresses(identity.ReplyToJson), DecodeAddresses(identity.BccJson),
                identity.TextSignature, identity.HtmlSignature, identity.MayDelete))
            .ToArray();
        var state = await states.GetStateAsync(account.InboxId, JmapConstants.IdentityDataType,
            cancellationToken).ConfigureAwait(false);
        return new(MailIdentityReadStatus.Ok, state, snapshots);
    }

    private static MailIdentityAddressListSnapshot? DecodeAddresses(string? json)
    {
        if (json is null)
            return null;
        var addresses = JsonSerializer.Deserialize<MailIdentityAddressSnapshot[]>(json, AddressOptions)
            ?? throw new InvalidOperationException("The stored identity address list is invalid.");
        if (addresses.Any(address => address is null || address.Email is null))
            throw new InvalidOperationException("The stored identity address list is incomplete.");
        return new(addresses);
    }

    private static MailIdentityReadResult Error(MailIdentityReadStatus status) => new(status, null, []);
}
