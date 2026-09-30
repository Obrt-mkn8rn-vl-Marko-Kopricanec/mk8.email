using Microsoft.EntityFrameworkCore;
using mk8.email.Application.Interfaces;
using mk8.email.Configuration;
using mk8.email.Contracts.Messaging;
using mk8.email.Infrastructure.Data;

namespace mk8.email.Jmap;

internal sealed class MailSubmissionReader(
    EmailDbContext database,
    JmapAccountService accounts,
    JmapStateService states,
    EnvironmentConfig environment) : IMailSubmissionReader
{
    public async Task<MailSubmissionReadResult> ReadAsync(
        MailSubmissionReadCommand command,
        AuthenticatedMailUser user,
        CancellationToken cancellationToken)
    {
        if (command.SubmissionIds?.Count > environment.Jmap.MaxObjectsInGet)
            return new(MailSubmissionReadStatus.RequestTooLarge, null, []);
        var account = await accounts.GetAccountByInboxIdAsync(user, command.AccountId, cancellationToken)
            .ConfigureAwait(false);
        if (account is null)
            return new(MailSubmissionReadStatus.AccountNotFound, null, []);

        var query = database.JmapEmailSubmissions.AsNoTracking()
            .Where(submission => submission.AccountId == account.InboxId);
        if (command.SubmissionIds is not null)
        {
            var ids = command.SubmissionIds.ToArray();
            query = query.Where(submission => ids.Contains(submission.Id));
        }
        var rows = await query.OrderBy(submission => submission.CreatedAt)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (command.SubmissionIds is null && rows.Count > environment.Jmap.MaxObjectsInGet)
            return new(MailSubmissionReadStatus.RequestTooLarge, null, []);

        var snapshots = new List<MailSubmissionSnapshot>(rows.Count);
        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            snapshots.Add(await MailSubmissionSnapshots.ReadAsync(database, row,
                command.IncludeDeliveryStatus, cancellationToken).ConfigureAwait(false));
        }
        var state = await states.GetStateAsync(account.InboxId, JmapConstants.EmailSubmissionDataType,
            cancellationToken).ConfigureAwait(false);
        return new(MailSubmissionReadStatus.Ok, state, snapshots);
    }

}
