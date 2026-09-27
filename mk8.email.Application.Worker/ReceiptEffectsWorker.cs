using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using mk8.email.Application.Services;

namespace mk8.email.Application.Worker;

internal sealed class ReceiptEffectsWorker(IServiceScopeFactory scopes) : BackgroundService
{
    internal async Task<bool> ProcessNextAsync(CancellationToken cancellationToken)
    {
        var scope = scopes.CreateAsyncScope();
        await using var scopeLifetime = scope.ConfigureAwait(false);
        return await scope.ServiceProvider.GetRequiredService<ApplicationOperationReceiptStore>()
            .DispatchNextEffectsAsync(cancellationToken).ConfigureAwait(false);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            if (!await ProcessNextAsync(stoppingToken).ConfigureAwait(false))
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken).ConfigureAwait(false);
        }
    }
}
