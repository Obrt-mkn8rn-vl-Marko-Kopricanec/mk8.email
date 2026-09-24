using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using mk8.email.Application.Interfaces;
using mk8.email.Messaging;

namespace mk8.email.Application.Worker;

internal sealed partial class ApplicationRequestWorker(
    IApplicationRequestConsumer requests,
    IServiceScopeFactory scopeFactory,
    ApplicationWorkerIdentity identity,
    ILogger<ApplicationRequestWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            ApplicationRequestLease lease;
            try
            {
                lease = await requests.WaitForRequestAsync(identity.WorkerId, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }

            await ProcessLeaseAsync(lease, stoppingToken).ConfigureAwait(false);
        }
    }

    internal async Task ProcessLeaseAsync(
        ApplicationRequestLease lease,
        CancellationToken stoppingToken)
    {
        var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var leaseLost = false;
        var renewTask = RenewLeaseAsync(lease, operationCancellation, () => leaseLost = true);
        try
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var dispatcher = scope.ServiceProvider.GetRequiredService<IApplicationRequestDispatcher>();
                var response = await dispatcher.DispatchAsync(lease.Request, operationCancellation.Token).ConfigureAwait(false);
                await StopRenewalAsync(operationCancellation, renewTask).ConfigureAwait(false);
                if (leaseLost)
                    throw new ApplicationRequestLeaseLostException(lease.Request.Id);
                await requests.CompleteAsync(lease, response, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                await StopRenewalAsync(operationCancellation, renewTask).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (leaseLost)
            {
                await StopRenewalAsync(operationCancellation, renewTask).ConfigureAwait(false);
                LogLeaseCancellation(logger, lease.Request.Id);
            }
            catch (ApplicationRequestLeaseLostException exception)
            {
                await StopRenewalAsync(operationCancellation, renewTask).ConfigureAwait(false);
                LogLeaseLoss(logger, exception, lease.Request.Id);
            }
            // This is the durable request boundary: unexpected handler faults must yield a retryable response.
#pragma warning disable CA1031
            catch (Exception exception)
#pragma warning restore CA1031
            {
                await StopRenewalAsync(operationCancellation, renewTask).ConfigureAwait(false);
                LogRequestFailure(logger, exception, lease.Request.Id);
                await FailRequestAsync(lease, stoppingToken).ConfigureAwait(false);
            }
        }
        finally
        {
            try
            {
                await StopRenewalAsync(operationCancellation, renewTask).ConfigureAwait(false);
            }
            finally
            {
                operationCancellation.Dispose();
            }
        }
    }

    private async Task FailRequestAsync(
        ApplicationRequestLease lease,
        CancellationToken stoppingToken)
    {
        try
        {
            await requests.FailAsync(
                lease,
                "application-failed",
                "The application operation failed.",
                stoppingToken).ConfigureAwait(false);
        }
        catch (ApplicationRequestLeaseLostException leaseException)
        {
            LogFailureAfterLeaseLoss(logger, leaseException, lease.Request.Id);
        }
    }

    private static async Task StopRenewalAsync(
        CancellationTokenSource operationCancellation,
        Task renewalTask)
    {
        await operationCancellation.CancelAsync().ConfigureAwait(false);
        // The renewal task was created by ProcessLeaseAsync; this is its shutdown join.
#pragma warning disable VSTHRD003
        await ObserveRenewalAsync(renewalTask).ConfigureAwait(false);
#pragma warning restore VSTHRD003
    }

    private async Task RenewLeaseAsync(
        ApplicationRequestLease lease,
        CancellationTokenSource operationCancellation,
        Action markLeaseLost)
    {
        using var timer = new PeriodicTimer(identity.LeaseRenewalInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(operationCancellation.Token).ConfigureAwait(false))
            {
                if (await requests.RenewLeaseAsync(lease, operationCancellation.Token).ConfigureAwait(false))
                    continue;
                markLeaseLost();
                await operationCancellation.CancelAsync().ConfigureAwait(false);
                return;
            }
        }
        catch (OperationCanceledException) when (operationCancellation.IsCancellationRequested)
        {
        }
    }

    private static async Task ObserveRenewalAsync(Task renewalTask)
    {
        try
        {
            // The caller starts this renewal task and this method joins it before token disposal.
#pragma warning disable VSTHRD003
            await renewalTask.ConfigureAwait(false);
#pragma warning restore VSTHRD003
        }
        catch (OperationCanceledException)
        {
        }
    }

    [LoggerMessage(
        EventId = 1001,
        Level = LogLevel.Warning,
        Message = "Application request {RequestId} was cancelled after losing its lease")]
    private static partial void LogLeaseCancellation(ILogger logger, Guid requestId);

    [LoggerMessage(
        EventId = 1002,
        Level = LogLevel.Warning,
        Message = "Application request {RequestId} lost its lease")]
    private static partial void LogLeaseLoss(ILogger logger, Exception exception, Guid requestId);

    [LoggerMessage(
        EventId = 1003,
        Level = LogLevel.Error,
        Message = "Application request {RequestId} failed")]
    private static partial void LogRequestFailure(ILogger logger, Exception exception, Guid requestId);

    [LoggerMessage(
        EventId = 1004,
        Level = LogLevel.Warning,
        Message = "Application request {RequestId} failed after losing its lease")]
    private static partial void LogFailureAfterLeaseLoss(ILogger logger, Exception exception, Guid requestId);
}
