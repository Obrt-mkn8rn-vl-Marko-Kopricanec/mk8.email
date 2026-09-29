using mk8.email.Application.Interfaces;
using mk8.email.Contracts.Messaging;

namespace mk8.email.Jmap;

internal interface IMailBlobCopyService
{
    Task<MailBlobCopyResult> CopyAsync(
        MailBlobCopyCommand command,
        AuthenticatedMailUser user,
        CancellationToken cancellationToken);
}
