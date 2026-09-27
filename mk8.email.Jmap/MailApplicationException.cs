using mk8.email.Contracts.Messaging;

namespace mk8.email.Jmap;

internal sealed class MailApplicationException(MailApplicationFailure failure) : Exception(failure.Detail ?? failure.Kind.ToString())
{
    public MailApplicationFailure Failure { get; } = failure;
}
