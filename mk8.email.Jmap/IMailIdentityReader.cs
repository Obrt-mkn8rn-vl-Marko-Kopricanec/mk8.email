using mk8.email.Application.Interfaces;
using mk8.email.Contracts.Messaging;

namespace mk8.email.Jmap;

internal interface IMailIdentityReader
{
    Task<MailIdentityReadResult> ReadAsync(
        MailIdentityReadCommand command,
        AuthenticatedMailUser user,
        CancellationToken cancellationToken);
}
