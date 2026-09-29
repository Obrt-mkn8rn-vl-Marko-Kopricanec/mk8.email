using mk8.email.Application.Interfaces;
using mk8.email.Contracts.Messaging;

namespace mk8.email.Jmap;

internal interface IMailContactMutationService
{
    Task<MailContactMutationResult> MutateAsync(MailContactMutationCommand command,
        AuthenticatedMailUser user, CancellationToken cancellationToken);
}
