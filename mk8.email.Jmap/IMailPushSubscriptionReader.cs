using mk8.email.Application.Interfaces;
using mk8.email.Contracts.Messaging;

namespace mk8.email.Jmap;

internal interface IMailPushSubscriptionReader
{
    Task<MailPushSubscriptionReadResult> ReadAsync(
        MailPushSubscriptionReadCommand command,
        AuthenticatedMailUser user,
        CancellationToken cancellationToken);
}
