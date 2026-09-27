using mk8.email.Contracts.Messaging;

namespace mk8.email.Gateway.Protocols.Jmap;

internal static class GatewayJmapFailureCodec
{
    public static GatewayJmapProblem Render(MailApplicationFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        if (!Enum.IsDefined(failure.Kind) || !Enum.IsDefined(failure.Limit)
            || failure.Kind == MailFailureKind.None
            || (failure.Kind == MailFailureKind.ResourceLimit) != (failure.Limit != MailResourceLimit.None))
            throw new InvalidOperationException("Application returned an invalid mail failure.");
        return failure.Kind switch
        {
            MailFailureKind.MalformedBatch => new("urn:ietf:params:jmap:error:notRequest", "Invalid JMAP request", failure.Detail),
            MailFailureKind.UnsupportedFeature => new("urn:ietf:params:jmap:error:unknownCapability", "Unknown capability", failure.Detail),
            MailFailureKind.InvalidSelection => new("urn:ietf:params:jmap:error:invalidArguments", "Invalid event source parameters", failure.Detail),
            MailFailureKind.ResourceLimit => new("urn:ietf:params:jmap:error:limit",
                failure.Limit == MailResourceLimit.UploadSize ? "Upload failed" : "Request limit exceeded",
                failure.Detail, LimitName(failure.Limit)),
            _ => throw new InvalidOperationException("Application returned an unsupported mail failure."),
        };
    }

    private static string LimitName(MailResourceLimit limit) => limit switch
    {
        MailResourceLimit.OperationCount => "maxCallsInRequest",
        MailResourceLimit.RequestConcurrency => "maxConcurrentRequests",
        MailResourceLimit.UploadConcurrency => "maxConcurrentUpload",
        MailResourceLimit.UploadSize => "maxSizeUpload",
        _ => throw new InvalidOperationException("Application returned an unsupported resource limit."),
    };
}
