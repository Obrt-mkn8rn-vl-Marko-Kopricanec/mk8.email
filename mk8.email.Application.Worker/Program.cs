using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using mk8.email.Application;
using mk8.email.Application.Worker;
using mk8.email.Contracts.Storage;
using mk8.email.Infrastructure;
using mk8.email.Infrastructure.Environment;
using mk8.email.Messaging;
using mk8.email.Storage;
using Npgsql;

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

    var messagingOptions = new PostgresMessagingOptions
    {
        MaxPayloadBytes = environment.Messaging.MaxPayloadBytes,
        InlinePayloadThresholdBytes = environment.Messaging.InlinePayloadThresholdBytes,
        LeaseDuration = TimeSpan.FromSeconds(environment.Messaging.LeaseSeconds),
        NotificationFallbackInterval = TimeSpan.FromSeconds(
            environment.Messaging.NotificationFallbackSeconds),
    };
    var workerId = string.IsNullOrWhiteSpace(environment.Messaging.WorkerId)
        ? $"application@{Environment.MachineName}"
        : environment.Messaging.WorkerId;

    builder.Services.AddSingleton(messagingOptions);
    builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(environment.BuildConnectionString()));
    builder.Services.AddSingleton<IMessagingPayloadProtector>(_ =>
    {
        var activeKey = new MessagingEncryptionKey(
            environment.Messaging.EncryptionKeyId,
            Convert.FromBase64String(environment.Messaging.EncryptionKey));
        var decryptionKeys = environment.Messaging.DecryptionKeys.Select(key =>
            new MessagingEncryptionKey(key.Id, Convert.FromBase64String(key.Key)));
        return new AesGcmPayloadProtector(activeKey, decryptionKeys);
    });
    builder.Services.AddSingleton<ILargeObjectStore>(_ =>
        AzureBlobLargeObjectStore.FromConnectionString(
            environment.ObjectStorage.ConnectionString,
            new AzureBlobLargeObjectStoreOptions
            {
                ContainerName = environment.ObjectStorage.ContainerName,
                ObjectPrefix = environment.ObjectStorage.ObjectPrefix,
                CreateContainerIfMissing = environment.ObjectStorage.CreateContainerIfMissing,
            }));
    builder.Services.AddSingleton(serviceProvider => new PostgresApplicationBus(
        serviceProvider.GetRequiredService<NpgsqlDataSource>(),
        serviceProvider.GetRequiredService<IMessagingPayloadProtector>(),
        serviceProvider.GetRequiredService<PostgresMessagingOptions>(),
        largeObjectStore: serviceProvider.GetRequiredService<ILargeObjectStore>()));
    builder.Services.AddSingleton<IApplicationRequestConsumer>(serviceProvider =>
        serviceProvider.GetRequiredService<PostgresApplicationBus>());
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
