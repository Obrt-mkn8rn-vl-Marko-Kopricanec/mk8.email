namespace mk8.email.Contracts.Messaging;

public static class WebPushPresentationOperations
{
    public const string Protocol = "webpush";
    public const string ValidateEndpoint = "webpush.validate-endpoint";
    public const string Send = "webpush.send";
}

public sealed record WebPushEndpointCheck(string Url);

public sealed record WebPushEndpointResult(bool IsSafe);

public sealed record WebPushSendRequest(
    string Url,
    string? P256dh,
    string? Auth,
    DateTimeOffset ExpiresAt,
    byte[] Payload);

public enum WebPushSendOutcome
{
    Success,
    Gone,
    RateLimited,
    Failed,
}

public sealed record WebPushSendResult(WebPushSendOutcome Outcome);
