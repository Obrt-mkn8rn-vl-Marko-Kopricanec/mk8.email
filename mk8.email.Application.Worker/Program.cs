using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using mk8.email.Application;
using mk8.email.Application.Interfaces;
using mk8.email.Application.Services;
using mk8.email.Application.Worker;
using mk8.email.Configuration;
using mk8.email.Contracts.Mail;
using mk8.email.Dav;
using mk8.email.Hosting;
using mk8.email.Infrastructure;
using mk8.email.Infrastructure.Data;
using mk8.email.Jmap;
using mk8.email.Messaging;
using Npgsql;

var drain = args.SequenceEqual(["--drain"]);
var prepare = args.SequenceEqual(["--prepare"]);
if (!drain && !prepare && !args.SequenceEqual(["--serve"]))
{
    await Console.Error.WriteLineAsync(
        "The Application Worker requires --serve, --prepare, or --drain.").ConfigureAwait(false);
    return 2;
}

try
{
    var isDevelopment = string.Equals(
        Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT"),
        "Development",
        StringComparison.Ordinal);
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
    builder.Services.AddMailApplicationWorker();
    builder.Services.AddSingleton<IOutboundMailRelay, OutboundSmtpPresentationClient>();
    if (environment.Dav.EnableDav)
        builder.Services.AddDavProtocol();
    if (environment.Jmap.EnableJmap)
    {
        builder.Services.AddSingleton<IJmapPushPresentationClient, JmapPushPresentationClient>();
        builder.Services.AddJmapApplication();
    }

    var workerId = string.IsNullOrWhiteSpace(environment.Messaging.WorkerId)
        ? $"application@{Environment.MachineName}"
        : environment.Messaging.WorkerId;

    builder.Services.AddDistributedMessaging(environment);
    builder.Services.AddSingleton(new ApplicationWorkerIdentity(
        workerId,
        TimeSpan.FromSeconds(Math.Max(1, environment.Messaging.LeaseSeconds / 3))));
    builder.Services.AddSingleton<MessagingSchemaInitializer>();
    builder.Services.AddHostedService(provider =>
        provider.GetRequiredService<MessagingSchemaInitializer>());
    builder.Services.AddSingleton<ApplicationRequestWorker>();
    builder.Services.AddHostedService(provider =>
        provider.GetRequiredService<ApplicationRequestWorker>());

    using var host = builder.Build();
    await DistributedRestoreActivationGuard.RequireReadyAsync(
        host.Services.GetRequiredService<NpgsqlDataSource>()).ConfigureAwait(false);
    if (!drain)
    {
        using var scope = host.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<MailRuntimeSchemaService>()
            .EnsureAsync().ConfigureAwait(false);
        await scope.ServiceProvider.GetRequiredService<MailQueueLargeObjectMigrationService>()
            .MigrateAsync().ConfigureAwait(false);
        await scope.ServiceProvider.GetRequiredService<MailboxMessageLargeObjectMigrationService>()
            .MigrateAsync().ConfigureAwait(false);
        await scope.ServiceProvider.GetRequiredService<SieveScriptLargeObjectMigrationService>()
            .MigrateAsync().ConfigureAwait(false);
        if (environment.Dav.EnableDav || environment.Jmap.EnableJmap)
        {
            await scope.ServiceProvider.GetRequiredService<DavResourceLargeObjectMigrationService>()
                .MigrateAsync().ConfigureAwait(false);
        }
        if (environment.Jmap.EnableJmap)
        {
            await scope.ServiceProvider.GetRequiredService<JmapBlobLargeObjectMigrationService>()
                .MigrateAsync().ConfigureAwait(false);
        }
    }
    if (prepare)
    {
        await host.Services.GetRequiredService<MessagingSchemaInitializer>()
            .StartAsync(CancellationToken.None).ConfigureAwait(false);
        // This is a stable machine-operator CLI status line, not localized UI text.
#pragma warning disable CA1303
        Console.WriteLine("The Application Worker schemas and Azure Blob references are prepared.");
#pragma warning restore CA1303
        return 0;
    }
    if (drain)
    {
        using var shutdown = new CancellationTokenSource();
        using var interrupt = PosixSignalRegistration.Create(PosixSignal.SIGINT, signal =>
        {
            signal.Cancel = true;
            shutdown.Cancel();
        });
        using var terminate = PosixSignalRegistration.Create(PosixSignal.SIGTERM, signal =>
        {
            signal.Cancel = true;
            shutdown.Cancel();
        });
        try
        {
            var queue = host.Services.GetRequiredService<MailQueueWorker>();
            var push = host.Services.GetService<IJmapPushWork>();
            var runner = new WorkerDrainRunner(
                host.Services.GetRequiredService<IApplicationRequestConsumer>(),
                host.Services.GetRequiredService<ApplicationRequestWorker>(),
                host.Services.GetRequiredService<ApplicationWorkerIdentity>(),
                queue.ProcessNextAsync,
                queue.CleanupCompletedAsync,
                push is null ? null : push.ProcessDueAsync);
            var result = await runner.RunAsync(shutdown.Token).ConfigureAwait(false);
            Console.WriteLine(
                $"The Application Worker drained {result.ApplicationRequests} requests "
                + $"and {result.MailMessages} queued messages.");
            return 0;
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
            return 0;
        }
    }

    await host.RunAsync().ConfigureAwait(false);
    return 0;
}
// The process boundary must turn all startup/runtime faults into a nonzero exit code.
#pragma warning disable CA1031
catch (Exception exception)
#pragma warning restore CA1031
{
    await Console.Error.WriteLineAsync(
        $"The Application Worker failed: {exception.GetBaseException().Message}").ConfigureAwait(false);
    return 1;
}
