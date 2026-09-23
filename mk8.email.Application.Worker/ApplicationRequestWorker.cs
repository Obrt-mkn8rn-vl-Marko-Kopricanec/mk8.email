using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using mk8.email.Application.Interfaces;
using mk8.email.Messaging;

namespace mk8.email.Application.Worker;

public sealed class ApplicationRequestWorker(
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
                lease = await requests.WaitForRequestAsync(identity.WorkerId, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }

            await ProcessLeaseAsync(lease, stoppingToken);
        }
    }

    internal async Task ProcessLeaseAsync(
        ApplicationRequestLease lease,
        CancellationToken stoppingToken)
    {
        using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var leaseLost = false;
        var renewTask = RenewLeaseAsync(lease, operationCancellation, () => leaseLost = true);
        try
        {
            using var scope = scopeFactory.CreateScope();
            var dispatcher = scope.ServiceProvider.GetRequiredService<IApplicationRequestDispatcher>();
            var response = await dispatcher.DispatchAsync(lease.Request, operationCancellation.Token);
            operationCancellation.Cancel();
            await ObserveRenewalAsync(renewTask);
            if (leaseLost)
                throw new ApplicationRequestLeaseLostException(lease.Request.Id);
            await requests.CompleteAsync(lease, response, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            operationCancellation.Cancel();
            await ObserveRenewalAsync(renewTask);
        }
        catch (OperationCanceledException) when (leaseLost)
        {
            operationCancellation.Cancel();
            await ObserveRenewalAsync(renewTask);
            logger.LogWarning(
                "Application request {RequestId} was cancelled after losing its lease",
                lease.Request.Id);
        }
        catch (ApplicationRequestLeaseLostException exception)
        {
            operationCancellation.Cancel();
            await ObserveRenewalAsync(renewTask);
            logger.LogWarning(
                exception,
                "Application request {RequestId} lost its lease",
                lease.Request.Id);
        }
        catch (Exception exception)
        {
            operationCancellation.Cancel();
            await ObserveRenewalAsync(renewTask);
            logger.LogError(
                exception,
                "Application request {RequestId} failed",
                lease.Request.Id);
            try
            {
                await requests.FailAsync(
                    lease,
                    "application-failed",
                    "The application operation failed.",
                    stoppingToken);
            }
            catch (ApplicationRequestLeaseLostException leaseException)
            {
                logger.LogWarning(
                    leaseException,
                    "Application request {RequestId} failed after losing its lease",
                    lease.Request.Id);
            }
        }
    }

    private async Task RenewLeaseAsync(
        ApplicationRequestLease lease,
        CancellationTokenSource operationCancellation,
        Action markLeaseLost)
    {
        using var timer = new PeriodicTimer(identity.LeaseRenewalInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(operationCancellation.Token))
            {
                if (await requests.RenewLeaseAsync(lease, operationCancellation.Token))
                    continue;
                markLeaseLost();
                operationCancellation.Cancel();
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
            await renewalTask;
        }
        catch (OperationCanceledException)
        {
        }
    }
}

public sealed record ApplicationWorkerIdentity(
    string WorkerId,
    TimeSpan LeaseRenewalInterval);
