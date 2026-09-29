using mk8.email.Application.Interfaces;
using mk8.email.Contracts.Messaging;

namespace mk8.email.Jmap;

internal interface IMailVacationMutator
{
    Task<MailVacationSetResult> SetAsync(
        MailVacationSetCommand command,
        AuthenticatedMailUser user,
        CancellationToken cancellationToken);
}
