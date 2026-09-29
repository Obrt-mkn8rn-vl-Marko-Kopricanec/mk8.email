using mk8.email.Application.Interfaces;
using mk8.email.Contracts.Messaging;

namespace mk8.email.Jmap;

internal interface IMailVacationReader
{
    Task<MailVacationReadResult> ReadAsync(
        MailVacationReadCommand command,
        AuthenticatedMailUser user,
        CancellationToken cancellationToken);
}
