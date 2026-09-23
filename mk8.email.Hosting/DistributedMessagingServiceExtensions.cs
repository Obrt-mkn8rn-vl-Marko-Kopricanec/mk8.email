using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using mk8.email.Configuration;
using mk8.email.Contracts.Storage;
using mk8.email.Messaging;
using mk8.email.Storage;
using Npgsql;

namespace mk8.email.Hosting;

public static class DistributedMessagingServiceExtensions
{
    public static IServiceCollection AddAzureBlobObjectStorage(
        this IServiceCollection services,
        EnvironmentConfig environment)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(environment);
        if (!string.Equals(
                environment.ObjectStorage.Provider,
                LargeObjectProviders.AzureBlob,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "ObjectStorage.Provider must be azure-blob.");
        }
        if (string.IsNullOrWhiteSpace(environment.ObjectStorage.ConnectionString))
        {
            throw new InvalidOperationException(
                "ObjectStorage.ConnectionString is required.");
        }

        services.TryAddSingleton(_ => NpgsqlDataSource.Create(environment.BuildConnectionString()));
        services.TryAddSingleton(_ =>
            AzureBlobLargeObjectStore.FromConnectionString(
                environment.ObjectStorage.ConnectionString,
                new AzureBlobLargeObjectStoreOptions
                {
                    ContainerName = environment.ObjectStorage.ContainerName,
                    ObjectPrefix = environment.ObjectStorage.ObjectPrefix,
                    CreateContainerIfMissing = environment.ObjectStorage.CreateContainerIfMissing,
                }));
        services.TryAddSingleton<ILargeObjectStore>(serviceProvider =>
            new PostgresCoordinatedLargeObjectStore(
                serviceProvider.GetRequiredService<NpgsqlDataSource>(),
                serviceProvider.GetRequiredService<AzureBlobLargeObjectStore>()));
        return services;
    }

    public static IServiceCollection AddDistributedMessaging(
        this IServiceCollection services,
        EnvironmentConfig environment)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(environment);
        if (!environment.Messaging.Enabled)
            throw new InvalidOperationException("Messaging.Enabled must be true.");

        var options = new PostgresMessagingOptions
        {
            MaxPayloadBytes = environment.Messaging.MaxPayloadBytes,
            InlinePayloadThresholdBytes = environment.Messaging.InlinePayloadThresholdBytes,
            LeaseDuration = TimeSpan.FromSeconds(environment.Messaging.LeaseSeconds),
            NotificationFallbackInterval = TimeSpan.FromSeconds(
                environment.Messaging.NotificationFallbackSeconds),
        };

        services.AddSingleton(options);
        services.TryAddSingleton(_ => NpgsqlDataSource.Create(environment.BuildConnectionString()));
        services.AddSingleton<IMessagingPayloadProtector>(_ =>
        {
            var activeKey = new MessagingEncryptionKey(
                environment.Messaging.EncryptionKeyId,
                Convert.FromBase64String(environment.Messaging.EncryptionKey));
            var decryptionKeys = environment.Messaging.DecryptionKeys.Select(key =>
                new MessagingEncryptionKey(key.Id, Convert.FromBase64String(key.Key)));
            return new AesGcmPayloadProtector(activeKey, decryptionKeys);
        });
        services.AddAzureBlobObjectStorage(environment);
        services.AddSingleton(serviceProvider => new PostgresApplicationBus(
            serviceProvider.GetRequiredService<NpgsqlDataSource>(),
            serviceProvider.GetRequiredService<IMessagingPayloadProtector>(),
            serviceProvider.GetRequiredService<PostgresMessagingOptions>(),
            largeObjectStore: serviceProvider.GetRequiredService<ILargeObjectStore>()));
        services.AddSingleton<IApplicationRequestClient>(serviceProvider =>
            serviceProvider.GetRequiredService<PostgresApplicationBus>());
        services.AddSingleton<IApplicationRequestConsumer>(serviceProvider =>
            serviceProvider.GetRequiredService<PostgresApplicationBus>());
        services.AddSingleton(serviceProvider => new PostgresPresentationBus(
            serviceProvider.GetRequiredService<NpgsqlDataSource>(),
            serviceProvider.GetRequiredService<IMessagingPayloadProtector>(),
            serviceProvider.GetRequiredService<PostgresMessagingOptions>(),
            largeObjectStore: serviceProvider.GetRequiredService<ILargeObjectStore>()));
        services.AddSingleton<IPresentationRequestClient>(serviceProvider =>
            serviceProvider.GetRequiredService<PostgresPresentationBus>());
        services.AddSingleton<IPresentationRequestConsumer>(serviceProvider =>
            serviceProvider.GetRequiredService<PostgresPresentationBus>());
        services.AddSingleton<IGatewayTrafficJournal>(serviceProvider =>
            new PostgresGatewayTrafficJournal(
                serviceProvider.GetRequiredService<NpgsqlDataSource>(),
                serviceProvider.GetRequiredService<IMessagingPayloadProtector>(),
                serviceProvider.GetRequiredService<PostgresMessagingOptions>(),
                serviceProvider.GetRequiredService<ILargeObjectStore>()));
        services.AddSingleton<IPop3MaildropLeaseStore, PostgresPop3MaildropLeaseStore>();
        services.AddSingleton<IApplicationTransportControl, PostgresApplicationTransportControl>();
        return services;
    }
}
