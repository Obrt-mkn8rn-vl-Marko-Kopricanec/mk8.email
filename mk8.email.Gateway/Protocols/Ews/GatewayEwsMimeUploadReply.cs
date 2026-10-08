using mk8.email.Contracts.Messaging;

namespace mk8.email.Gateway.Protocols.Ews;

internal static class GatewayEwsMimeUploadReply
{
    public static string Decode(JmapApplicationResult result, int length)
    {
        if (result.Content is not null || result.OperationResult is not null || result.Profile is not null
            || result.Changes is not null || result.Cursor is not null) throw Invalid();
        if (!string.Equals(result.Outcome, JmapApplicationOutcomes.Ok, StringComparison.Ordinal))
        {
            if (result.Failure is not null || result.BlobId is not null || result.Size is not null || result.ContentType is not null) throw Invalid();
            throw result.Outcome switch
            {
                JmapApplicationOutcomes.Unauthorized => new GatewayEwsRequestException("ErrorAccessDenied", status: StatusCodes.Status401Unauthorized),
                JmapApplicationOutcomes.NotFound => new GatewayEwsRequestException("ErrorFolderNotFound"),
                _ => Invalid(),
            };
        }
        if (result.Failure is not null)
        {
            if (result.BlobId is not null || result.Size is not null || result.ContentType is not null
                || result.Failure.Kind != MailFailureKind.ResourceLimit) throw Invalid();
            throw result.Failure.Limit switch
            {
                MailResourceLimit.UploadSize => new GatewayEwsRequestException("ErrorDataSizeLimitExceeded"),
                MailResourceLimit.UploadConcurrency => new GatewayEwsRequestException("ErrorServerBusy", status: StatusCodes.Status503ServiceUnavailable),
                _ => Invalid(),
            };
        }
        var blob = result.BlobId;
        if (blob is not { Length: 33 } || blob[0] != 'U' || !Guid.TryParseExact(blob.AsSpan(1), "N", out var id)
            || id == Guid.Empty || !string.Equals(blob, $"U{id:N}", StringComparison.Ordinal)
            || result.Size != length || !string.Equals(result.ContentType, "message/rfc822", StringComparison.Ordinal)) throw Invalid();
        return blob;
    }

    private static InvalidOperationException Invalid() => new("The EWS MIME upload reply is invalid.");
}
