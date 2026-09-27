using System.Text.Json.Nodes;
using mk8.email.Contracts.Messaging;

namespace mk8.email.Jmap;

public sealed record JmapMethodResponse(
    MailOperationKind Operation,
    JsonObject Arguments,
    IReadOnlyList<JmapMethodResponse>? AdditionalResponses = null)
{
    public static JmapMethodResponse Error(string type, string? description = null)
    {
        var arguments = new JsonObject { ["type"] = type };
        if (!string.IsNullOrEmpty(description))
            arguments["description"] = description;
        return new JmapMethodResponse(MailOperationKind.Failure, arguments);
    }
}
