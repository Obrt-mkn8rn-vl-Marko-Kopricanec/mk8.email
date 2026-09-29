using mk8.email.Application.Interfaces;
using mk8.email.Contracts.Messaging;

namespace mk8.email.Jmap;

internal interface IMailContactReader
{
    Task<MailContactReadResult> ReadAsync(
        MailContactReadCommand command,
        AuthenticatedMailUser user,
        CancellationToken cancellationToken);
}
