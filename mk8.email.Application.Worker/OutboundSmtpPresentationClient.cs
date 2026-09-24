using System.Text.Json;
using Microsoft.Extensions.Logging;
using mk8.email.Configuration;
using mk8.email.Contracts.Mail;
using mk8.email.Contracts.Messaging;
using mk8.email.Messaging;

namespace mk8.email.Application.Worker;

internal sealed partial class OutboundSmtpPresentationClient(
    IPresentationRequestClient requests,
    EnvironmentConfig environment,
    ILogger<OutboundSmtpPresentationClient> logger) : IOutboundMailRelay
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<OutboundDeliveryResult> RelayAsync(
        string sender,
        string recipient,
        string rawMessage,
        OutboundMailOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var now = DateTimeOffset.UtcNow;
            var requestId = Guid.CreateVersion7();
            var attemptSeconds = Math.Clamp(environment.Limits.ConnectionTimeoutSeconds, 10, 60);
            var request = new ApplicationRequest(
                requestId,
                Guid.CreateVersion7(),
                0,
                SmtpPresentationOperations.Protocol,
                SmtpPresentationOperations.Relay,
                "application/json",
                JsonSerializer.SerializeToUtf8Bytes(
                    new SmtpRelayPresentationRequest(sender, recipient, rawMessage, options),
                    JsonOptions),
                new Dictionary<string, string>(StringComparer.Ordinal),
                now,
                now.AddSeconds(5 * attemptSeconds + 30),
                requestId.ToString("N"));
            var response = await requests.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.IsError)
                return TemporaryFailure();
            return JsonSerializer.Deserialize<OutboundDeliveryResult>(response.Payload, JsonOptions)
                ?? TemporaryFailure();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        // Gateway transport can fail through several backends; delivery must remain retryable.
#pragma warning disable CA1031
        catch (Exception exception)
#pragma warning restore CA1031
        {
            LogRelayFailure(logger, exception);
            return TemporaryFailure();
        }
    }

    private static OutboundDeliveryResult TemporaryFailure() => new(
        OutboundDeliveryStatus.TemporaryFailure,
        "The outbound SMTP presentation service is temporarily unavailable.",
        EnhancedStatusCode: "4.4.2");

    [LoggerMessage(
        EventId = 1201,
        Level = LogLevel.Warning,
        Message = "Gateway outbound SMTP delivery did not complete")]
    private static partial void LogRelayFailure(ILogger logger, Exception exception);
}
