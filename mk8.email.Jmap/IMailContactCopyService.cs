using mk8.email.Application.Interfaces;
using mk8.email.Contracts.Messaging;

namespace mk8.email.Jmap;

internal interface IMailContactCopyService
{
    Task<MailContactCopyResult> CopyAsync(
        MailContactCopyCommand command,
        AuthenticatedMailUser user,
        CancellationToken cancellationToken);
}
