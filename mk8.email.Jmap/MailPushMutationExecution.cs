using mk8.email.Contracts.Messaging;

namespace mk8.email.Jmap;

internal sealed record MailPushMutationExecution(MailPushSubscriptionMutationResult Result,
    IReadOnlyList<MailPushVerification> Verifications);
