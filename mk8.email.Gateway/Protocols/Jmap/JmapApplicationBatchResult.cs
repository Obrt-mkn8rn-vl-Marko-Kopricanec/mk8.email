using mk8.email.Contracts.Messaging;

namespace mk8.email.Gateway.Protocols.Jmap;

internal sealed record JmapApplicationBatchResult(
    JmapApplicationInvocation[] Invocations,
    JmapApplicationProfile Profile,
    IReadOnlyDictionary<string, string>? CreatedIds = null);
