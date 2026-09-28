using mk8.email.Application.Interfaces;
using mk8.email.Contracts.Messaging;

namespace mk8.email.Jmap;

internal interface IMailFolderReader
{
    Task<MailFolderReadResult> ReadAsync(
        MailFolderReadCommand command,
        AuthenticatedMailUser user,
        CancellationToken cancellationToken);
}
