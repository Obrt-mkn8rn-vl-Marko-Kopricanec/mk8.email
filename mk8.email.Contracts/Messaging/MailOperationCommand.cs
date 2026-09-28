using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace mk8.email.Contracts.Messaging;

public sealed record MailOperationCommand(
    [property: JsonRequired] IReadOnlyList<MailFeature> Features,
    [property: JsonRequired] MailOperationKind Operation,
    [property: JsonRequired] JsonObject Arguments,
    [property: JsonRequired] IReadOnlyDictionary<string, string> ReferenceAliases,
    IReadOnlyDictionary<string, string>? KnownEntities = null);
