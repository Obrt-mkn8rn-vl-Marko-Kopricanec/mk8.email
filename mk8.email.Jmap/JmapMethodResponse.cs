using System.Text.Json.Nodes;
using mk8.email.Application.Interfaces;

namespace mk8.email.Jmap;

public sealed record JmapMethodResponse(
    string Name,
    JsonObject Arguments,
    IReadOnlyList<JmapMethodResponse>? AdditionalResponses = null)
{
    public static JmapMethodResponse Error(string type, string? description = null)
    {
        var arguments = new JsonObject { ["type"] = type };
        if (!string.IsNullOrEmpty(description))
            arguments["description"] = description;
        return new JmapMethodResponse("error", arguments);
    }
}
