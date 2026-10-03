using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using mk8.email.Contracts.Messaging;

namespace mk8.email.Gateway.Protocols.Jmap;

internal static class GatewayMailOperationFailureCodec
{
    private static readonly JsonSerializerOptions FailureJsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static MailOperationFailure Decode(JsonObject data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var failure = data.Deserialize<MailOperationFailure>(FailureJsonOptions)
            ?? throw new InvalidOperationException("The Application returned an incomplete operation failure.");
        if (!Enum.IsDefined(failure.Reason) || failure.Reason == MailOperationFailureReason.None)
            throw new InvalidOperationException("The Application returned an invalid operation failure reason.");
        return failure;
    }

    public static JsonObject Render(MailOperationFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        var type = failure.Reason switch
        {
            MailOperationFailureReason.NotSupported => "unknownMethod",
            MailOperationFailureReason.InternalFailure => "serverFail",
            MailOperationFailureReason.PartiallyCompleted => "serverPartialFail",
            _ => throw new InvalidOperationException("The Application returned an invalid operation failure reason."),
        };
        var result = new JsonObject { ["type"] = type };
        if (!string.IsNullOrEmpty(failure.Explanation))
            result["description"] = failure.Explanation;
        return result;
    }
}
