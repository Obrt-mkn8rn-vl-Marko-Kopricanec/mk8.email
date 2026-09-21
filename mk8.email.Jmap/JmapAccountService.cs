using Microsoft.EntityFrameworkCore;
using mk8.email.Application.Interfaces;
using mk8.email.Infrastructure.Data;

namespace mk8.email.Jmap;

public sealed record JmapAccount(
    Guid InboxId,
    Guid UserId,
    string Username,
    string Address,
    long QuotaBytes);

public sealed class JmapAccountService(EmailDbContext database)
{
    public async Task<IReadOnlyList<JmapAccount>> GetAccountsAsync(
        AuthenticatedMailUser user,
        CancellationToken cancellationToken = default)
    {
        return await database.Inboxes
            .AsNoTracking()
            .Where(inbox => inbox.OwnerId == user.Id
                && inbox.AliasForInboxId == null
                && inbox.Name != "*"
                && inbox.Owner.IsActive
                && inbox.Address.IsActive
                && inbox.Address.Company.IsActive)
            .OrderBy(inbox => (inbox.Name + "@" + inbox.Address.Domain) == user.Username ? 0 : 1)
            .ThenBy(inbox => inbox.Address.Domain)
            .ThenBy(inbox => inbox.Name)
            .Select(inbox => new JmapAccount(
                inbox.Id,
                inbox.OwnerId,
                inbox.Owner.Username,
                inbox.Name + "@" + inbox.Address.Domain,
                inbox.Owner.QuotaBytes))
            .ToListAsync(cancellationToken);
    }

    public async Task<JmapAccount?> GetAccountAsync(
        AuthenticatedMailUser user,
        string? accountId,
        CancellationToken cancellationToken = default)
    {
        if (!JmapId.TryParseAccount(accountId, out var inboxId))
            return null;

        return await database.Inboxes
            .AsNoTracking()
            .Where(inbox => inbox.Id == inboxId
                && inbox.OwnerId == user.Id
                && inbox.AliasForInboxId == null
                && inbox.Name != "*"
                && inbox.Owner.IsActive
                && inbox.Address.IsActive
                && inbox.Address.Company.IsActive)
            .Select(inbox => new JmapAccount(
                inbox.Id,
                inbox.OwnerId,
                inbox.Owner.Username,
                inbox.Name + "@" + inbox.Address.Domain,
                inbox.Owner.QuotaBytes))
            .SingleOrDefaultAsync(cancellationToken);
    }

    public async Task<JmapAccount?> GetContactAccountAsync(
        AuthenticatedMailUser user,
        string? accountId,
        CancellationToken cancellationToken = default)
    {
        var primary = (await GetAccountsAsync(user, cancellationToken)).FirstOrDefault();
        return primary is not null
            && string.Equals(JmapId.Account(primary.InboxId), accountId, StringComparison.Ordinal)
                ? primary
                : null;
    }

    public async Task<string> GetContactAccountErrorAsync(
        AuthenticatedMailUser user,
        string? accountId,
        CancellationToken cancellationToken = default) =>
        await GetAccountAsync(user, accountId, cancellationToken) is null
            ? "accountNotFound"
            : "accountNotSupportedByMethod";
}
