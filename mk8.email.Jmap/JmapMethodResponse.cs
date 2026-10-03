using System.Text.Json;
using System.Text.Json.Nodes;
using mk8.email.Contracts.Messaging;

namespace mk8.email.Jmap;

public sealed record JmapMethodResponse(
    MailOperationKind Operation,
    JsonObject Arguments,
    IReadOnlyList<JmapMethodResponse>? AdditionalResponses = null)
{
    private static readonly JsonSerializerOptions FailureJsonOptions = new(JsonSerializerDefaults.Web);

    public static JmapMethodResponse Failure(MailOperationFailureReason reason, string? explanation = null)
    {
        if (!Enum.IsDefined(reason) || reason == MailOperationFailureReason.None)
            throw new ArgumentOutOfRangeException(nameof(reason));
        var arguments = JsonSerializer.SerializeToNode(new MailOperationFailure(reason, explanation), FailureJsonOptions)!.AsObject();
        return new JmapMethodResponse(MailOperationKind.Failure, arguments);
    }
}
