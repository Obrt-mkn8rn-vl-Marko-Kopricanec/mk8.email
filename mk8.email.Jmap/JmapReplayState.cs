using mk8.email.Contracts.Messaging;

namespace mk8.email.Jmap;

internal sealed record JmapReplayState(MailOperationResponse Response, Dictionary<string, string> CreatedIds);
