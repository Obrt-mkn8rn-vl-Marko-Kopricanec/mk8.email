namespace mk8.email.Contracts.Messaging;

public sealed record MailOperationResult(
    MailOperationResponse Response,
    IReadOnlyDictionary<string, string> KnownEntities,
    JmapApplicationProfile Profile);
