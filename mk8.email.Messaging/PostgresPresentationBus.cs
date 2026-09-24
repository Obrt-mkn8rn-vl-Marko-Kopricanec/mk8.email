using Microsoft.Extensions.Logging;
using mk8.email.Contracts.Storage;
using Npgsql;

namespace mk8.email.Messaging;

public sealed class PostgresPresentationBus : PostgresApplicationBus,
    IPresentationRequestClient,
    IPresentationRequestConsumer
{
    public PostgresPresentationBus(
        NpgsqlDataSource dataSource,
        IMessagingPayloadProtector protector,
        PostgresMessagingOptions? options = null,
        TimeProvider? timeProvider = null,
        ILargeObjectStore? largeObjectStore = null,
        ILogger<PostgresApplicationBus>? logger = null)
        : base(
            dataSource,
            protector,
            options,
            timeProvider,
            largeObjectStore,
            logger,
            PostgresRequestLane.Presentation)
    {
    }
}
