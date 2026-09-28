using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using mk8.email.Contracts.Messaging;

namespace mk8.email.Gateway.Protocols.Jmap;

internal sealed record JmapApplicationInvocation(
    [property: JsonRequired] MailOperationKind Operation,
    JsonObject Arguments,
    string CorrelationId);
