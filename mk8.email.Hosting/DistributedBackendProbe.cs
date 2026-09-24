using System.Security.Cryptography;
using System.Runtime.ExceptionServices;
using mk8.email.Contracts.Storage;
using Npgsql;

namespace mk8.email.Hosting;

public static class DistributedBackendProbe
{
    public static async Task ProbeAsync(
        NpgsqlDataSource dataSource,
        ILargeObjectStore objects,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(objects);
        if (!string.Equals(objects.Provider, LargeObjectProviders.AzureBlob, StringComparison.Ordinal))
            throw new InvalidOperationException("Distributed storage must use the Azure Blob protocol.");

        var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            var command = connection.CreateCommand();
            await using (command.ConfigureAwait(false))
            {
                command.CommandText = "SELECT 1";
                if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not 1)
                    throw new InvalidOperationException("The distributed PostgreSQL probe failed.");
            }
        }

        await ProbeObjectStorageAsync(objects, cancellationToken).ConfigureAwait(false);
    }

    // Keep probe-write, readback, and cleanup exception preservation together.
#pragma warning disable MA0051
    public static async Task ProbeObjectStorageAsync(
        ILargeObjectStore objects,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(objects);
#pragma warning restore MA0051
        if (!string.Equals(objects.Provider, LargeObjectProviders.AzureBlob, StringComparison.Ordinal))
            throw new InvalidOperationException("Distributed storage must use the Azure Blob protocol.");

        var content = RandomNumberGenerator.GetBytes(262_145);
        var hash = Convert.ToHexStringLower(SHA256.HashData(content));
        var name = $"health/distributed/{Guid.CreateVersion7():N}";
        LargeObjectWriteResult? written = null;
        Exception? probeFailure = null;
        try
        {
            var source = new MemoryStream(content, writable: false);
            await using (source.ConfigureAwait(false))
            {
                written = await objects.PutIfAbsentAsync(
                name,
                source,
                content.LongLength,
                hash,
                "application/octet-stream",
                cancellationToken).ConfigureAwait(false);
                if (!written.Created)
                    throw new InvalidOperationException("The distributed Blob probe object already existed.");

                var destination = new MemoryStream();
                await using var destinationLifetime = destination.ConfigureAwait(false);
                await objects.CopyToAsync(written.Reference, destination, cancellationToken).ConfigureAwait(false);
                if (!content.AsSpan().SequenceEqual(destination.ToArray()))
                    throw new InvalidOperationException("The distributed Blob probe read back different bytes.");
            }
        }
        // Capture any probe failure so cleanup still runs without masking either failure.
#pragma warning disable CA1031
        catch (Exception exception)
#pragma warning restore CA1031
        {
            probeFailure = exception;
        }

        Exception? cleanupFailure = null;
        try
        {
            using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            if (written?.Created == true
                && !await objects.DeleteIfMatchAsync(written.Reference, cleanupTimeout.Token).ConfigureAwait(false))
            {
                throw new InvalidOperationException("The distributed Blob probe object could not be removed.");
            }
        }
        // Cleanup failures are also retained for a complete probe diagnosis.
#pragma warning disable CA1031
        catch (Exception exception)
#pragma warning restore CA1031
        {
            cleanupFailure = exception;
        }

        if (probeFailure is not null && cleanupFailure is not null)
            throw new AggregateException("The distributed Blob probe and cleanup both failed.", probeFailure, cleanupFailure);
        if (probeFailure is not null)
            ExceptionDispatchInfo.Capture(probeFailure).Throw();
        if (cleanupFailure is not null)
            ExceptionDispatchInfo.Capture(cleanupFailure).Throw();
    }
}
