using System.Diagnostics.CodeAnalysis;
using mk8.email.Contracts.Messaging;

namespace mk8.email.Jmap;

[SuppressMessage("Design", "CA1032", Justification = "An internal failure exception requires its typed failure payload.")]
[SuppressMessage("Design", "CA1064", Justification = "This internal dispatch exception is never part of the public API.")]
internal sealed class MailApplicationException(MailApplicationFailure failure) : Exception(failure.Detail ?? failure.Kind.ToString())
{
    public MailApplicationFailure Failure { get; } = failure;
}
