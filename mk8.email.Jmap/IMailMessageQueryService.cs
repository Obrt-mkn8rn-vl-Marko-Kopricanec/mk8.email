using mk8.email.Application.Interfaces;
using mk8.email.Contracts.Messaging;

namespace mk8.email.Jmap;

internal interface IMailMessageQueryService
{
    Task<MailMessageQueryResult> QueryAsync(
        MailMessageQueryCommand command, AuthenticatedMailUser user, CancellationToken cancellationToken);

    Task<MailMessageQueryChangesResult> QueryChangesAsync(
        MailMessageQueryChangesCommand command, AuthenticatedMailUser user, CancellationToken cancellationToken);
}
