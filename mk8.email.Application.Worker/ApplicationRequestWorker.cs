using Microsoft.Extensions.DependencyInjection;
using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using mk8.email.Application.Interfaces;
using mk8.email.Application.Services;
using mk8.email.Contracts.Messaging;
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
        EnsureCurrentContract(lease.Request);
        var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var leaseLost = false;
        var dispatchCompleted = false;
        var renewTask = RenewLeaseAsync(lease, operationCancellation, () => leaseLost = true);
        try
        {
            try
            {
                await DispatchAndCompleteAsync(lease, operationCancellation, renewTask,
                    () => dispatchCompleted = true, () => leaseLost, stoppingToken).ConfigureAwait(false);
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
            // Only dispatch failures are business failures. Leave publication failures
            // reclaimable under the SAME request ID; an acknowledged commit is already
            // completed and must likewise never be overwritten by FailAsync.
#pragma warning disable CA1031
            catch (Exception exception)
#pragma warning restore CA1031
            {
                await StopRenewalAsync(operationCancellation, renewTask).ConfigureAwait(false);
                await ReportProcessingFailureAsync(lease, exception, dispatchCompleted, stoppingToken).ConfigureAwait(false);
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

    private async Task DispatchAndCompleteAsync(ApplicationRequestLease lease,
        CancellationTokenSource operationCancellation, Task renewalTask,
        Action markDispatched, Func<bool> leaseLost, CancellationToken stoppingToken)
    {
        Exception? failure = null;
        var scope = scopeFactory.CreateScope();
        try
        {
            using (scope)
            {
                try
                {
                    var dispatcher = scope.ServiceProvider.GetRequiredService<IApplicationRequestDispatcher>();
                    var response = await dispatcher.DispatchAsync(lease.Request, operationCancellation.Token).ConfigureAwait(false);
                    markDispatched();
                    // The outer owner started this SAME renewal; join it before
                    // response publication and scope retirement.
#pragma warning disable VSTHRD003
                    await StopRenewalAsync(operationCancellation, renewalTask).ConfigureAwait(false);
#pragma warning restore VSTHRD003
                    if (leaseLost()) throw new ApplicationRequestLeaseLostException(lease.Request.Id);
                    await requests.CompleteAsync(lease, response, stoppingToken).ConfigureAwait(false);
                }
#pragma warning disable CA1031 // Preserve the actual dispatch/publication failure before independently retiring its scope.
                catch (Exception original) when (!IsFatal(original)) { failure = original; }
#pragma warning restore CA1031
            }
        }
#pragma warning disable CA1031 // A secondary scope fault must not erase a matching receipt-recovery signal.
        catch (Exception disposal) when (!IsFatal(disposal))
#pragma warning restore CA1031
        {
            failure = failure is null ? disposal : new AggregateException(failure, disposal);
        }
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static bool IsFatal(Exception error) => error is
        OutOfMemoryException or StackOverflowException or AccessViolationException;

    private static void EnsureCurrentContract(ApplicationRequest request)
    {
        // An upgrade race leaves the lease retryable for its original role,
        // rather than publishing a terminal unknown-operation response.
        if (DistributedContractVersions.SupersededOperations.Contains(request.Operation))
            throw new InvalidOperationException("A superseded application operation requires its original Worker.");
    }

    private async Task ReportProcessingFailureAsync(ApplicationRequestLease lease, Exception exception,
        bool dispatchCompleted, CancellationToken stoppingToken)
    {
        if (dispatchCompleted || RequiresReceiptRecovery(exception, lease.Request.Id))
        {
            LogResponsePublicationFailure(logger, exception, lease.Request.Id);
            return;
        }
        LogRequestFailure(logger, exception, lease.Request.Id);
        await FailRequestAsync(lease, stoppingToken).ConfigureAwait(false);
    }

    private static bool RequiresReceiptRecovery(Exception failure, Guid operationId) => failure switch
    {
        DurableOAuthRecoveryRequiredException recovery => recovery.OperationId == operationId,
        AggregateException inventory => inventory.Flatten().InnerExceptions
            .Any(error => RequiresReceiptRecovery(error, operationId)),
        _ => false,
    };

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

    [LoggerMessage(
        EventId = 1005,
        Level = LogLevel.Error,
        Message = "Application request {RequestId} has a dispatched or uncertain durable outcome; preserve the same operation for receipt recovery")]
    private static partial void LogResponsePublicationFailure(ILogger logger, Exception exception, Guid requestId);
}
