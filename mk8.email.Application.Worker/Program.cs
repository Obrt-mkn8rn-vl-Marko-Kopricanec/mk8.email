using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using mk8.email.Application;
using mk8.email.Application.Worker;
using mk8.email.Configuration;
using mk8.email.Hosting;
using mk8.email.Infrastructure;
using mk8.email.Jmap;
using mk8.email.Messaging;

if (!args.SequenceEqual(["--serve"]))
{
    Console.Error.WriteLine("The Application Worker requires the --serve command.");
    return 2;
}

try
{
    var isDevelopment = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") == "Development";
    var environment = EnvironmentLoader.Load(
        isDevelopment,
        EnvironmentValidationRole.ApplicationWorker);
    if (!environment.Messaging.Enabled)
        throw new InvalidOperationException("Messaging.Enabled must be true for the Application Worker.");

    var builder = Host.CreateApplicationBuilder(args: []);
    builder.Logging.ClearProviders();
    builder.Logging.AddJsonConsole();
    builder.Logging.AddFilter("Microsoft.EntityFrameworkCore.Database.Command", LogLevel.Warning);
    builder.Services.AddInfrastructure(environment);
    builder.Services.AddApplication();
    if (environment.Jmap.EnableJmap)
        builder.Services.AddJmapApplication();

    var workerId = string.IsNullOrWhiteSpace(environment.Messaging.WorkerId)
        ? $"application@{Environment.MachineName}"
        : environment.Messaging.WorkerId;

    builder.Services.AddDistributedMessaging(environment);
    builder.Services.AddSingleton(new ApplicationWorkerIdentity(
        workerId,
        TimeSpan.FromSeconds(Math.Max(1, environment.Messaging.LeaseSeconds / 3))));
    builder.Services.AddHostedService<MessagingSchemaInitializer>();
    builder.Services.AddHostedService<ApplicationRequestWorker>();

    await builder.Build().RunAsync();
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"The Application Worker failed: {exception.GetBaseException().Message}");
    return 1;
}
