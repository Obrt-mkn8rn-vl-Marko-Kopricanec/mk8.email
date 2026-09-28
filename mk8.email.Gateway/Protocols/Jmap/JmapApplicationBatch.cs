using System.Text.Json.Serialization;
using mk8.email.Contracts.Messaging;

namespace mk8.email.Gateway.Protocols.Jmap;

// Presentation-side batch state, never a durable Worker contract.
internal sealed record JmapApplicationBatch(
    [property: JsonRequired] MailFeature[] Features,
    JmapApplicationCall[] Invocations,
    IReadOnlyDictionary<string, string>? CreatedIds = null);
