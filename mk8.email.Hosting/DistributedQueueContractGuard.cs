using mk8.email.Contracts.Messaging;
using Npgsql;

namespace mk8.email.Hosting;

public static class DistributedQueueContractGuard
{
    public static async Task<bool> IsCompatibleAsync(
        NpgsqlDataSource dataSource, CancellationToken cancellationToken = default)
    {
        try
        {
            await RequireCompatibleAsync(dataSource, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (exception is NpgsqlException or TimeoutException or InvalidOperationException)
        {
            return false;
        }
    }

    public static async Task RequireCompatibleAsync(
        NpgsqlDataSource dataSource, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var connectionLifetime = connection.ConfigureAwait(false);
        if (!await HasQueueSchemaAsync(connection, cancellationToken).ConfigureAwait(false))
            return;

        var queued = connection.CreateCommand();
        await using (queued.ConfigureAwait(false))
        {
            queued.CommandText = """
                SELECT EXISTS (
                    SELECT 1 FROM public.application_requests
                    WHERE state IN ('pending', 'processing') AND operation = ANY (@operations)
                    UNION ALL
                    SELECT 1 FROM public.presentation_requests
                    WHERE state IN ('pending', 'processing') AND operation = ANY (@operations))
                """;
            queued.Parameters.AddWithValue("operations", DistributedContractVersions.SupersededOperations.ToArray());
            if (await queued.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not false)
            {
                throw new InvalidOperationException(
                    "Superseded distributed work must be drained with its original roles or explicitly reconciled before upgrade.");
            }
        }
    }

    private static async Task<bool> HasQueueSchemaAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        var schema = connection.CreateCommand();
        await using (schema.ConfigureAwait(false))
        {
            schema.CommandText = """
                SELECT to_regclass('public.application_requests') IS NOT NULL,
                       to_regclass('public.presentation_requests') IS NOT NULL
                """;
            var reader = await schema.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await using var readerLifetime = reader.ConfigureAwait(false);
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            var applicationExists = reader.GetBoolean(0);
            var presentationExists = reader.GetBoolean(1);
            if (!applicationExists && !presentationExists)
                return false; // A new installation has no queued work to reconcile.
            if (!applicationExists || !presentationExists)
                throw new InvalidOperationException("The distributed queue schema is incomplete.");
        }

        return true;
    }
}
