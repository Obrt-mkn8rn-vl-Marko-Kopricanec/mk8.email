using mk8.email.Contracts.Messaging;

namespace mk8.email.Jmap;

internal sealed record MailPushVerification(string Url, string? KeysJson, DateTime ExpiresAt,
    JmapPushMessage Message);
