using System.Text.Json;
using mk8.email.Configuration;
using mk8.email.Contracts.Messaging;
using mk8.email.Messaging;

namespace mk8.email.Gateway.Protocols.Jmap;

internal sealed class GatewayPresentationWorker(
    IPresentationRequestConsumer requests,
    IGatewayTrafficJournal traffic,
    GatewayWebPushService webPush,
    EnvironmentConfig environment,
    ILogger<GatewayPresentationWorker> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly string WorkerId = $"presentation@{Environment.MachineName}";
    private readonly TimeSpan _leaseRenewalInterval = TimeSpan.FromSeconds(
        Math.Max(1, environment.Messaging.LeaseSeconds / 3));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var lease = await requests.WaitForRequestAsync(WorkerId, stoppingToken);
                await ProcessAsync(lease, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Gateway presentation request processing failed");
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
        }
    }

    private async Task ProcessAsync(
        ApplicationRequestLease lease,
        CancellationToken stoppingToken)
    {
        var trafficSessionId = Guid.CreateVersion7();
        using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var renewal = RenewLeaseAsync(lease, operationCancellation);
        try
        {
            await AppendInternalAsync(
                lease.Request,
                trafficSessionId,
                sequence: 0,
                GatewayTrafficDirections.Inbound,
                lease.Request.ContentType,
                lease.Request.Payload);
            var response = await DispatchAsync(
                lease.Request,
                trafficSessionId,
                operationCancellation.Token);
            await AppendInternalAsync(
                lease.Request,
                trafficSessionId,
                sequence: 3,
                GatewayTrafficDirections.Outbound,
                response.ContentType,
                response.Payload);
            operationCancellation.Cancel();
            await renewal;
            await requests.CompleteAsync(lease, response, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            operationCancellation.Cancel();
            await ObserveRenewalAsync(renewal);
        }
        catch (Exception exception)
        {
            operationCancellation.Cancel();
            await ObserveRenewalAsync(renewal);
            logger.LogError(
                exception,
                "Gateway presentation request {RequestId} failed",
                lease.Request.Id);
            try
            {
                await AppendInternalAsync(
                    lease.Request,
                    trafficSessionId,
                    sequence: 3,
                    GatewayTrafficDirections.Outbound,
                    "application/problem+json",
                    JsonSerializer.SerializeToUtf8Bytes(
                        new { code = "presentation-failed" },
                        JsonOptions));
            }
            catch (Exception journalException)
            {
                logger.LogError(
                    journalException,
                    "Could not journal the presentation failure for {RequestId}",
                    lease.Request.Id);
            }
            try
            {
                await requests.FailAsync(
                    lease,
                    "presentation-failed",
                    "The Gateway presentation operation failed.",
                    stoppingToken);
            }
            catch (Exception completionException)
            {
                logger.LogWarning(
                    completionException,
                    "Could not fail presentation request {RequestId}",
                    lease.Request.Id);
            }
        }
    }

    private async Task<ApplicationResponse> DispatchAsync(
        ApplicationRequest request,
        Guid trafficSessionId,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(
                request.Protocol,
                WebPushPresentationOperations.Protocol,
                StringComparison.Ordinal)
            || !string.Equals(request.ContentType, "application/json", StringComparison.Ordinal))
            return Error(request.Id, "unsupported-presentation-operation");

        try
        {
            switch (request.Operation)
            {
                case WebPushPresentationOperations.ValidateEndpoint:
                    {
                        var check = Deserialize<WebPushEndpointCheck>(request.Payload);
                        var isSafe = await webPush.IsSafeUrlAsync(check.Url, cancellationToken);
                        return Success(request.Id, new WebPushEndpointResult(isSafe));
                    }
                case WebPushPresentationOperations.Send:
                    {
                        var send = Deserialize<WebPushSendRequest>(request.Payload);
                        var result = await webPush.SendAsync(
                            send,
                            trafficSessionId,
                            request.Id,
                            cancellationToken);
                        return Success(request.Id, result);
                    }
                default:
                    return Error(request.Id, "unsupported-presentation-operation");
            }
        }
        catch (JsonException)
        {
            return Error(request.Id, "invalid-presentation-request");
        }
    }

    private async Task AppendInternalAsync(
        ApplicationRequest request,
        Guid trafficSessionId,
        long sequence,
        string direction,
        string contentType,
        byte[] payload)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await traffic.AppendAsync(
            new GatewayTrafficRecord(
                Guid.CreateVersion7(),
                trafficSessionId,
                sequence,
                direction,
                request.Protocol,
                contentType,
                payload,
                new Dictionary<string, string> { ["operation"] = request.Operation },
                DateTimeOffset.UtcNow,
                request.Id),
            timeout.Token);
    }

    private async Task RenewLeaseAsync(
        ApplicationRequestLease lease,
        CancellationTokenSource operationCancellation)
    {
        using var timer = new PeriodicTimer(_leaseRenewalInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(operationCancellation.Token))
            {
                if (await requests.RenewLeaseAsync(lease, operationCancellation.Token))
                    continue;
                operationCancellation.Cancel();
                return;
            }
        }
        catch (OperationCanceledException) when (operationCancellation.IsCancellationRequested)
        {
        }
    }

    private static async Task ObserveRenewalAsync(Task renewal)
    {
        try
        {
            await renewal;
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static T Deserialize<T>(byte[] payload) =>
        JsonSerializer.Deserialize<T>(payload, JsonOptions)
        ?? throw new JsonException("The presentation request body is empty.");

    private static ApplicationResponse Success<T>(Guid requestId, T value) =>
        new(
            requestId,
            "application/json",
            JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions),
            new Dictionary<string, string>());

    private static ApplicationResponse Error(Guid requestId, string code) =>
        new(
            requestId,
            "application/problem+json",
            JsonSerializer.SerializeToUtf8Bytes(new { code }, JsonOptions),
            new Dictionary<string, string>(),
            IsError: true,
            ErrorCode: code,
            ErrorDetail: "The presentation request is invalid or unsupported.");
}
