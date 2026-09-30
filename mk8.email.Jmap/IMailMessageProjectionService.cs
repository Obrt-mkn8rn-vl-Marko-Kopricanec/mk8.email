using mk8.email.Application.Interfaces;
using mk8.email.Contracts.Messaging;

namespace mk8.email.Jmap;

internal interface IMailMessageProjectionService
{
    Task<MailMessageReadResult> ReadAsync(MailMessageReadCommand command,
        AuthenticatedMailUser user, CancellationToken cancellationToken);

    Task<MailMessageParseResult> ParseAsync(MailMessageParseCommand command,
        AuthenticatedMailUser user, CancellationToken cancellationToken);
}
