using System.Text.Json;
using mk8.email.Contracts.Messaging;
using mk8.email.Messaging;

namespace mk8.email.Gateway.ApplicationBridge;

public sealed class GatewayApplicationTransport(
    IApplicationRequestClient requests,
    IGatewayTrafficJournal traffic,
    GatewayApplicationOptions options) : IGatewayApplicationTransport
{
    private const string JsonContentType = "application/json";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<TResponse> SendAsync<TRequest, TResponse>(
        string protocol,
        string operation,
        TRequest value,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(protocol);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        var now = DateTimeOffset.UtcNow;
        var sessionId = Guid.CreateVersion7();
        var requestId = Guid.CreateVersion7();
        var payload = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["gateway-instance"] = options.InstanceId,
            ["operation"] = operation,
        };
        var request = new ApplicationRequest(
            requestId,
            sessionId,
            0,
            protocol,
            operation,
            JsonContentType,
            payload,
            metadata,
            now,
            now.Add(options.RequestTimeout),
            requestId.ToString("N"));
        try
        {
            await traffic.AppendAsync(new GatewayTrafficRecord(
                Guid.CreateVersion7(),
                sessionId,
                0,
                GatewayTrafficDirections.Inbound,
                protocol,
                JsonContentType,
                payload,
                metadata,
                now,
                requestId),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            throw new GatewayApplicationException(
                "traffic-journal-unavailable",
                "The gateway could not durably record the application request.",
                isUnavailable: true,
                exception);
        }

        ApplicationResponse response;
        try
        {
            response = await requests.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (ApplicationRequestExpiredException exception)
        {
            await AppendFailureAsync(protocol, sessionId, requestId, "application-timeout").ConfigureAwait(false);
            throw new GatewayApplicationException(
                "application-timeout",
                "The application worker did not respond before the request deadline.",
                isUnavailable: true,
                exception);
        }
        catch (ApplicationRequestFailedException exception)
        {
            await AppendFailureAsync(protocol, sessionId, requestId, exception.ErrorCode).ConfigureAwait(false);
            throw new GatewayApplicationException(
                exception.ErrorCode,
                "The application worker could not complete the request.",
                innerException: exception);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await AppendFailureAsync(protocol, sessionId, requestId, "gateway-request-cancelled").ConfigureAwait(false);
            throw;
        }
        catch (Exception exception)
        {
            await AppendFailureAsync(protocol, sessionId, requestId, "application-transport-failure").ConfigureAwait(false);
            throw new GatewayApplicationException(
                "application-transport-failure",
                "The application transport is unavailable.",
                isUnavailable: true,
                exception);
        }

        await AppendOutboundAsync(
            new GatewayTrafficRecord(
                Guid.CreateVersion7(),
                sessionId,
                1,
                GatewayTrafficDirections.Outbound,
                protocol,
                response.ContentType,
                response.Payload,
                response.Metadata,
                DateTimeOffset.UtcNow,
                requestId)).ConfigureAwait(false);
        if (response.IsError)
        {
            throw new GatewayApplicationException(
                response.ErrorCode ?? "application-error",
                response.ErrorDetail ?? "The application request was rejected.");
        }

        try
        {
            return JsonSerializer.Deserialize<TResponse>(response.Payload, JsonOptions)
                ?? throw new JsonException("The application response payload is empty.");
        }
        catch (JsonException exception)
        {
            throw new GatewayApplicationException(
                "invalid-application-response",
                "The application worker returned an invalid response.",
                innerException: exception);
        }
    }

    private async Task AppendFailureAsync(
        string protocol,
        Guid sessionId,
        Guid requestId,
        string errorCode)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(
            new { code = errorCode },
            JsonOptions);
        await AppendOutboundAsync(
            new GatewayTrafficRecord(
                Guid.CreateVersion7(),
                sessionId,
                1,
                GatewayTrafficDirections.Outbound,
                protocol,
                "application/problem+json",
                payload,
                new Dictionary<string, string>(StringComparer.Ordinal),
                DateTimeOffset.UtcNow,
                requestId)).ConfigureAwait(false);
    }

    private async Task AppendOutboundAsync(GatewayTrafficRecord record)
    {
        using var timeout = new CancellationTokenSource(options.TrafficJournalTimeout);
        try
        {
            await traffic.AppendAsync(record, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not GatewayApplicationException)
        {
            throw new GatewayApplicationException(
                "traffic-journal-unavailable",
                "The gateway could not durably record the application response.",
                isUnavailable: true,
                exception);
        }
    }
}
