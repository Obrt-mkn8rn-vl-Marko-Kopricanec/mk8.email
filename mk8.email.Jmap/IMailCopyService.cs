using mk8.email.Application.Interfaces;
using mk8.email.Contracts.Messaging;

namespace mk8.email.Jmap;

internal interface IMailCopyService
{
    Task<MailCopyResult> CopyAsync(MailCopyCommand command,
        AuthenticatedMailUser user, CancellationToken cancellationToken);
}
