using mk8.email.Contracts.Messaging;
using mk8.email.Infrastructure.Models;

namespace mk8.email.Jmap;

internal sealed record JmapStoredEmailResult(EmailDB? Email, MailMessageMutationFailure? Error);
