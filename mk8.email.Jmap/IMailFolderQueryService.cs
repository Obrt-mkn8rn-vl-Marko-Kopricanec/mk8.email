using mk8.email.Application.Interfaces;
using mk8.email.Contracts.Messaging;

namespace mk8.email.Jmap;

internal interface IMailFolderQueryService
{
    Task<MailFolderQueryResult> QueryAsync(
        MailFolderQueryCommand command,
        AuthenticatedMailUser user,
        CancellationToken cancellationToken);

    Task<MailFolderQueryChangesResult> QueryChangesAsync(
        MailFolderQueryChangesCommand command,
        AuthenticatedMailUser user,
        CancellationToken cancellationToken);
}
