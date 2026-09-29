using mk8.email.Contracts.Messaging;

namespace mk8.email.Jmap;

internal interface IMailFolderMutationService
{
    Task<MailFolderMutationResult> MutateAsync(
        MailFolderMutationCommand command,
        JmapInvocationContext context,
        CancellationToken cancellationToken);
}
