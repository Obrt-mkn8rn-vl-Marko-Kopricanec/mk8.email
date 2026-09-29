using mk8.email.Application.Interfaces;
using mk8.email.Contracts.Messaging;

namespace mk8.email.Jmap;

internal interface IMailPushSubscriptionMutationService
{
    Task<MailPushMutationExecution> MutateAsync(MailPushSubscriptionMutationCommand command,
        AuthenticatedMailUser user, CancellationToken cancellationToken);
}
