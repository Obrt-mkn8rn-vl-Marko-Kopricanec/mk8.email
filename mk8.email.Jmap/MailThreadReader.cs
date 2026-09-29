using Microsoft.EntityFrameworkCore;
using mk8.email.Application.Interfaces;
using mk8.email.Contracts.Messaging;
using mk8.email.Infrastructure.Data;

namespace mk8.email.Jmap;

internal sealed class MailThreadReader(
    EmailDbContext database,
    JmapAccountService accounts,
    JmapStateService states) : IMailThreadReader
{
    public async Task<MailThreadReadResult> ReadAsync(
        MailThreadReadCommand command,
        AuthenticatedMailUser user,
        CancellationToken cancellationToken)
    {
        var account = await accounts.GetAccountByInboxIdAsync(user, command.AccountId, cancellationToken)
            .ConfigureAwait(false);
        if (account is null)
            return new(MailThreadReadStatus.AccountNotFound, null, []);

        var emails = await database.Emails.AsNoTracking()
            .Where(email => email.Folder.InboxId == account.InboxId && !email.IsDeleted)
            .OrderBy(email => email.ReceivedAt)
            .ThenBy(email => email.Id)
            .Select(email => new MailThreadEmailSnapshot(email.Id, email.ThreadObjectId))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var state = await states.GetStateAsync(account.InboxId, JmapConstants.ThreadDataType,
            cancellationToken).ConfigureAwait(false);
        return new(MailThreadReadStatus.Ok, state, emails);
    }
}
