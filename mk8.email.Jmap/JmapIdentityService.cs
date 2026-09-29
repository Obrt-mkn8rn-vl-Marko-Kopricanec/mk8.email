using Microsoft.EntityFrameworkCore;
using MimeKit;
using mk8.email.Application.Interfaces;
using mk8.email.Infrastructure.Data;
using mk8.email.Infrastructure.Models;

namespace mk8.email.Jmap;

internal sealed class JmapIdentityService(
    EmailDbContext database,
    JmapAccountService accounts)
{
    public async Task EnsureDefaultAsync(JmapAccount account, CancellationToken cancellationToken)
    {
        if (await database.JmapIdentities.AnyAsync(identity => identity.AccountId == account.InboxId,
                cancellationToken).ConfigureAwait(false)) return;
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
            .Select(entry => entry.Entity).ToHashSet();
        await database.JmapIdentities.AddAsync(identity, cancellationToken).ConfigureAwait(false);
        try
        {
            await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            // Concurrent first reads race; the deterministic primary key lets
            // the loser observe the winning default after detaching its insert.
            database.Entry(identity).State = EntityState.Detached;
            DetachPendingChanges(preexistingChanges);
            if (!await database.JmapIdentities.AnyAsync(
                    candidate => candidate.AccountId == account.InboxId,
                    cancellationToken).ConfigureAwait(false)) throw;
        }
    }

    private void DetachPendingChanges(HashSet<JmapChangeDB> preexistingChanges)
    {
        foreach (var entry in database.ChangeTracker.Entries<JmapChangeDB>()
                     .Where(entry => entry.State == EntityState.Added
                         && !preexistingChanges.Contains(entry.Entity)))
            entry.State = EntityState.Detached;
    }

    public async Task<bool> CanUseAddressAsync(
        AuthenticatedMailUser user, string address, CancellationToken cancellationToken)
    {
        if (!MailboxAddress.TryParse(address, out var mailbox)
            || !string.Equals(mailbox.Address, address, StringComparison.OrdinalIgnoreCase))
            return false;
        var accessible = await accounts.GetAccountsAsync(user, cancellationToken).ConfigureAwait(false);
        return accessible.Any(account => string.Equals(account.Address, mailbox.Address,
            StringComparison.OrdinalIgnoreCase));
    }
}
