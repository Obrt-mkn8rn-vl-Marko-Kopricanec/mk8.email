using mk8.email.Application.Interfaces;
using mk8.email.Contracts.Messaging;

namespace mk8.email.Jmap;

internal interface IMailSubmissionMutationService
{
    Task<MailSubmissionMutationResult> MutateAsync(
        MailSubmissionMutationCommand command,
        AuthenticatedMailUser user,
        JmapInvocationContext context,
        CancellationToken cancellationToken);
}
