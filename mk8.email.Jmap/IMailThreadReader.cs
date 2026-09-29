using mk8.email.Application.Interfaces;
using mk8.email.Contracts.Messaging;

namespace mk8.email.Jmap;

internal interface IMailThreadReader
{
    Task<MailThreadReadResult> ReadAsync(
        MailThreadReadCommand command,
        AuthenticatedMailUser user,
        CancellationToken cancellationToken);
}
