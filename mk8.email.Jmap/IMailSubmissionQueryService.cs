using mk8.email.Application.Interfaces;
using mk8.email.Contracts.Messaging;

namespace mk8.email.Jmap;

internal interface IMailSubmissionQueryService
{
    Task<MailSubmissionQueryResult> QueryAsync(
        MailSubmissionQueryCommand command,
        AuthenticatedMailUser user,
        CancellationToken cancellationToken);

    Task<MailSubmissionQueryChangesResult> QueryChangesAsync(
        MailSubmissionQueryChangesCommand command,
        AuthenticatedMailUser user,
        CancellationToken cancellationToken);
}
