using System.Text.Json;
using Microsoft.Extensions.Logging;
using mk8.email.Contracts.Messaging;
using mk8.email.Jmap;
using mk8.email.Messaging;

namespace mk8.email.Application.Worker;

public sealed class JmapPushPresentationClient(
    IPresentationRequestClient requests,
    ILogger<JmapPushPresentationClient> logger) : IJmapPushPresentationClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<bool> IsSafeUrlAsync(string url, CancellationToken cancellationToken)
    {
        var request = CreateRequest(
            WebPushPresentationOperations.ValidateEndpoint,
            new WebPushEndpointCheck(url),
            DateTimeOffset.UtcNow.AddSeconds(20));
        var response = await requests.SendAsync(request, cancellationToken);
        return Deserialize<WebPushEndpointResult>(response).IsSafe;
    }

    public async Task<WebPushSendOutcome> SendAsync(
        string url,
        string? keysJson,
        DateTime expiresAt,
        byte[] payload,
        CancellationToken cancellationToken)
    {
        try
        {
            var target = BuildTarget(url, keysJson, expiresAt, payload);
            var request = CreateRequest(
                WebPushPresentationOperations.Send,
                target,
                DateTimeOffset.UtcNow.AddSeconds(45));
            var response = await requests.SendAsync(request, cancellationToken);
            return Deserialize<WebPushSendResult>(response).Outcome;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "JMAP Web Push presentation delivery did not complete");
            return WebPushSendOutcome.Failed;
        }
    }

    public async Task EnqueueVerificationAsync(
        string url,
        string? keysJson,
        DateTime expiresAt,
        byte[] payload,
        CancellationToken cancellationToken)
    {
        var target = BuildTarget(url, keysJson, expiresAt, payload);
        var deadline = DateTimeOffset.UtcNow.AddDays(1);
        if (target.ExpiresAt < deadline)
            deadline = target.ExpiresAt;
        if (deadline <= DateTimeOffset.UtcNow)
            return;
        await requests.EnqueueAsync(
            CreateRequest(WebPushPresentationOperations.Send, target, deadline),
            cancellationToken);
    }

    private static WebPushSendRequest BuildTarget(
        string url,
        string? keysJson,
        DateTime expiresAt,
        byte[] payload)
    {
        string? p256dh = null;
        string? auth = null;
        if (keysJson is not null)
        {
            using var keys = JsonDocument.Parse(keysJson);
            if (keys.RootElement.ValueKind != JsonValueKind.Object
                || !keys.RootElement.TryGetProperty("p256dh", out var publicKey)
                || !keys.RootElement.TryGetProperty("auth", out var secret))
                throw new InvalidOperationException("The stored Web Push keys are invalid.");
            p256dh = publicKey.GetString();
            auth = secret.GetString();
            if (p256dh is null || auth is null)
                throw new InvalidOperationException("The stored Web Push keys are invalid.");
        }
        return new WebPushSendRequest(
            url,
            p256dh,
            auth,
            new DateTimeOffset(DateTime.SpecifyKind(expiresAt, DateTimeKind.Utc)),
            payload);
    }

    private static ApplicationRequest CreateRequest<T>(
        string operation,
        T value,
        DateTimeOffset deadline)
    {
        var now = DateTimeOffset.UtcNow;
        var id = Guid.CreateVersion7();
        return new ApplicationRequest(
            id,
            Guid.CreateVersion7(),
            0,
            WebPushPresentationOperations.Protocol,
            operation,
            "application/json",
            JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions),
            new Dictionary<string, string>(),
            now,
            deadline,
            id.ToString("N"));
    }

    private static T Deserialize<T>(ApplicationResponse response)
    {
        if (response.IsError)
            throw new InvalidOperationException(response.ErrorCode ?? "The presentation request failed.");
        return JsonSerializer.Deserialize<T>(response.Payload, JsonOptions)
            ?? throw new JsonException("The presentation response is empty.");
    }
}
