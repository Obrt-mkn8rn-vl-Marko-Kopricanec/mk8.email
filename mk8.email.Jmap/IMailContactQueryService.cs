using mk8.email.Application.Interfaces;
using mk8.email.Contracts.Messaging;

namespace mk8.email.Jmap;

internal interface IMailContactQueryService
{
    Task<MailContactQueryResult> QueryAsync(
        MailContactQueryCommand command,
        AuthenticatedMailUser user,
        CancellationToken cancellationToken);

    Task<MailContactQueryChangesResult> QueryChangesAsync(
        MailContactQueryChangesCommand command,
        AuthenticatedMailUser user,
        CancellationToken cancellationToken);
}
