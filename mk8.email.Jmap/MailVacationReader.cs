using mk8.email.Application.Interfaces;
using mk8.email.Application.Services;
using mk8.email.Contracts.Messaging;

namespace mk8.email.Jmap;

internal sealed class MailVacationReader(
    JmapAccountService accounts,
    JmapVacationResponseService vacations,
    VacationResponseContentService content,
    JmapStateService states) : IMailVacationReader
{
    public async Task<MailVacationReadResult> ReadAsync(
        MailVacationReadCommand command,
        AuthenticatedMailUser user,
        CancellationToken cancellationToken)
    {
        var account = await accounts.GetAccountByInboxIdAsync(user, command.AccountId, cancellationToken)
            .ConfigureAwait(false);
        if (account is null)
            return new(MailVacationReadStatus.AccountNotFound, null, null);

        var response = await vacations.GetOrCreateAsync(account.InboxId, cancellationToken).ConfigureAwait(false);
        MailVacationSnapshot? snapshot = null;
        if (command.IncludeSingleton)
        {
            var bodies = command.IncludeBodies
                ? await content.ReadAsync(response, cancellationToken).ConfigureAwait(false)
                : default;
            snapshot = new(response.IsEnabled, response.FromDate, response.ToDate,
                response.Subject, bodies.TextBody, bodies.HtmlBody);
        }
        var state = await states.GetStateAsync(account.InboxId, JmapConstants.VacationResponseDataType,
            cancellationToken).ConfigureAwait(false);
        return new(MailVacationReadStatus.Ok, state, snapshot);
    }
}
