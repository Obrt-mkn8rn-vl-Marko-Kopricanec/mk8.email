using System.Text.Json;
using mk8.email.Configuration;
using mk8.email.Contracts.Mail;
using mk8.email.Contracts.Messaging;
using mk8.email.Gateway.Protocols.Jmap;
using mk8.email.Messaging;
using mk8.email.Smtp.Presentation;

namespace mk8.email.Gateway.Protocols;

internal sealed class GatewayPresentationWorker(
    IPresentationRequestConsumer requests,
    IApplicationTransportControl transport,
    IGatewayTrafficJournal traffic,
    GatewayWebPushService webPush,
    ISmtpPresentationRelay smtpRelay,
    EnvironmentConfig environment,
    ILogger<GatewayPresentationWorker> logger) : BackgroundService
{
    private const int MaximumConcurrentOperations = 16;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly string WorkerId = $"presentation@{Environment.MachineName}";
    private readonly TimeSpan _leaseRenewalInterval = TimeSpan.FromSeconds(
        Math.Max(1, environment.Messaging.LeaseSeconds / 3));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var active = new HashSet<Task>();
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    if (!await transport.IsAvailableAsync(stoppingToken).ConfigureAwait(false))
                    {
                        await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken).ConfigureAwait(false);
                        continue;
                    }

                    active.RemoveWhere(task => task.IsCompleted);
                    if (active.Count >= MaximumConcurrentOperations)
                    {
                        await Task.WhenAny(active).ConfigureAwait(false);
                        active.RemoveWhere(task => task.IsCompleted);
                    }

                    var lease = await requests.WaitForRequestAsync(WorkerId, stoppingToken).ConfigureAwait(false);
                    active.Add(ProcessSafelyAsync(lease, stoppingToken));
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                // A leased presentation request must not terminate the always-on Gateway worker.
#pragma warning disable CA1031
                catch (Exception exception)
                {
#pragma warning restore CA1031
                    GatewayProtocolLog.PresentationProcessingFailed(logger, exception);
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        break;
                    }
                }
            }
        }
        finally
        {
            await Task.WhenAll(active).ConfigureAwait(false);
        }
    }

    private async Task ProcessSafelyAsync(
        ApplicationRequestLease lease,
        CancellationToken stoppingToken)
    {
        try
        {
            await ProcessAsync(lease, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        // The outer task boundary observes every failure before another lease is accepted.
#pragma warning disable CA1031
        catch (Exception exception)
        {
#pragma warning restore CA1031
            GatewayProtocolLog.PresentationUnexpectedFailure(logger, exception, lease.Request.Id);
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "MA0051", Justification = "The durable presentation boundary keeps its ordered validation, journaling and failure handling together.")]
    private async Task ProcessAsync(
        ApplicationRequestLease lease,
        CancellationToken stoppingToken)
    {
        var trafficSessionId = Guid.CreateVersion7();
        using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var remaining = lease.Request.Deadline - DateTimeOffset.UtcNow;
        operationCancellation.CancelAfter(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
        // The renewal task is cancelled and directly awaited in this method's finally block.
#pragma warning disable CA2025
        var renewal = RenewLeaseAsync(lease, operationCancellation);
#pragma warning restore CA2025
        try
        {
            await AppendInternalAsync(
                lease.Request,
                trafficSessionId,
                sequence: 0,
                GatewayTrafficDirections.Inbound,
                lease.Request.ContentType,
                lease.Request.Payload).ConfigureAwait(false);
            var response = await DispatchAsync(
                lease.Request,
                trafficSessionId,
                operationCancellation.Token).ConfigureAwait(false);
            await AppendInternalAsync(
                lease.Request,
                trafficSessionId,
                sequence: 3,
                GatewayTrafficDirections.Outbound,
                response.ContentType,
                response.Payload).ConfigureAwait(false);
            await operationCancellation.CancelAsync().ConfigureAwait(false);
            await renewal.ConfigureAwait(false);
            await requests.CompleteAsync(lease, response, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        // Any operation failure must be reported through the durable fail path.
#pragma warning disable CA1031
        catch (Exception exception)
        {
#pragma warning restore CA1031
            GatewayProtocolLog.PresentationFailed(logger, exception, lease.Request.Id);
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
                        JsonOptions)).ConfigureAwait(false);
            }
            // Keep the original operation failure even if its failure trace cannot be journaled.
#pragma warning disable CA1031
            catch (Exception journalException)
            {
#pragma warning restore CA1031
                GatewayProtocolLog.PresentationJournalFailure(logger, journalException, lease.Request.Id);
            }
            try
            {
                await requests.FailAsync(
                    lease,
                    "presentation-failed",
                    "The Gateway presentation operation failed.",
                    stoppingToken).ConfigureAwait(false);
            }
            // The completion failure is secondary to the original presentation failure.
#pragma warning disable CA1031
            catch (Exception completionException)
            {
#pragma warning restore CA1031
                GatewayProtocolLog.PresentationCompletionFailure(logger, completionException, lease.Request.Id);
            }
        }
        finally
        {
            await operationCancellation.CancelAsync().ConfigureAwait(false);
            try
            {
                await renewal.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (operationCancellation.IsCancellationRequested)
            {
            }
        }
    }

    private async Task<ApplicationResponse> DispatchAsync(
        ApplicationRequest request,
        Guid trafficSessionId,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(request.ContentType, "application/json", StringComparison.Ordinal))
            return Error(request.Id, "unsupported-presentation-operation");

        try
        {
            if (string.Equals(request.Protocol, SmtpPresentationOperations.Protocol, StringComparison.Ordinal))
            {
                if (!string.Equals(request.Operation, SmtpPresentationOperations.Relay, StringComparison.Ordinal))
                    return Error(request.Id, "unsupported-presentation-operation");
                var relayRequest = Deserialize<SmtpRelayPresentationRequest>(request.Payload);
                var result = await smtpRelay.RelayAsync(
                    relayRequest,
                    request.Id,
                    cancellationToken).ConfigureAwait(false);
                return Success(request.Id, result);
            }

            if (!string.Equals(
                    request.Protocol,
                    WebPushPresentationOperations.Protocol,
                    StringComparison.Ordinal))
                return Error(request.Id, "unsupported-presentation-operation");

            switch (request.Operation)
            {
                case WebPushPresentationOperations.ValidateEndpoint:
                    {
                        var check = Deserialize<WebPushEndpointCheck>(request.Payload);
                        var isSafe = await webPush.IsSafeUrlAsync(check.Url, cancellationToken).ConfigureAwait(false);
                        return Success(request.Id, new WebPushEndpointResult(isSafe));
                    }
                case WebPushPresentationOperations.Send:
                    {
                        var send = Deserialize<WebPushSendRequest>(request.Payload);
                        var result = await webPush.SendAsync(
                            send,
                            trafficSessionId,
                            request.Id,
                            cancellationToken).ConfigureAwait(false);
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
                new Dictionary<string, string>(StringComparer.Ordinal) { ["operation"] = request.Operation },
                DateTimeOffset.UtcNow,
                request.Id),
            timeout.Token).ConfigureAwait(false);
    }

    private async Task RenewLeaseAsync(
        ApplicationRequestLease lease,
        CancellationTokenSource operationCancellation)
    {
        using var timer = new PeriodicTimer(_leaseRenewalInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(operationCancellation.Token).ConfigureAwait(false))
            {
                if (await requests.RenewLeaseAsync(lease, operationCancellation.Token).ConfigureAwait(false))
                    continue;
                await operationCancellation.CancelAsync().ConfigureAwait(false);
                return;
            }
        }
        catch (OperationCanceledException) when (operationCancellation.IsCancellationRequested)
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
            new Dictionary<string, string>(StringComparer.Ordinal));

    private static ApplicationResponse Error(Guid requestId, string code) =>
        new(
            requestId,
            "application/problem+json",
            JsonSerializer.SerializeToUtf8Bytes(new { code }, JsonOptions),
            new Dictionary<string, string>(StringComparer.Ordinal),
            IsError: true,
            ErrorCode: code,
            ErrorDetail: "The presentation request is invalid or unsupported.");
}
