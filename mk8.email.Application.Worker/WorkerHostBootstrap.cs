using Microsoft.Extensions.Hosting;
using mk8.email.Configuration;

namespace mk8.email.Application.Worker;

internal static class WorkerHostBootstrap
{
    internal static HostApplicationBuilder CreateBuilder() => Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings
    {
        Args = [],
        ApplicationName = typeof(WorkerHostBootstrap).Assembly.GetName().Name,
        EnvironmentName = Environments.Production,
        ContentRootPath = AppContext.BaseDirectory,
    });

    internal static string RequireWorkerIdentity(EnvironmentConfig configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (!configuration.Messaging.Enabled)
            throw new InvalidOperationException("Messaging.Enabled must be true for the Application Worker.");
        if (string.IsNullOrWhiteSpace(configuration.Messaging.WorkerId))
            throw new InvalidOperationException("Messaging.WorkerId must be explicitly configured for the Application Worker.");
        return configuration.Messaging.WorkerId;
    }
}
