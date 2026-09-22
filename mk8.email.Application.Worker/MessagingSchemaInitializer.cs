using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using mk8.email.Application.Interfaces;
using mk8.email.Messaging;

namespace mk8.email.Application.Worker;

public sealed class MessagingSchemaInitializer(
    IApplicationTransportControl transport,
    IServiceScopeFactory scopeFactory) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await transport.InitializeAsync(cancellationToken);
        using var scope = scopeFactory.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ISeederService>().SeedAsync();
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
