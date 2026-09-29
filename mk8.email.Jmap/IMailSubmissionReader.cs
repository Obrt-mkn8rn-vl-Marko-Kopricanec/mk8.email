using mk8.email.Application.Interfaces;
using mk8.email.Contracts.Messaging;

namespace mk8.email.Jmap;

internal interface IMailSubmissionReader
{
    Task<MailSubmissionReadResult> ReadAsync(
        MailSubmissionReadCommand command,
        AuthenticatedMailUser user,
        CancellationToken cancellationToken);
}
