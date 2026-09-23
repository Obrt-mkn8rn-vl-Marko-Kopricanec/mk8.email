using System.Security.Cryptography;
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

        await using (var connection = await dataSource.OpenConnectionAsync(cancellationToken))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT 1";
            if (await command.ExecuteScalarAsync(cancellationToken) is not 1)
                throw new InvalidOperationException("The distributed PostgreSQL probe failed.");
        }

        await ProbeObjectStorageAsync(objects, cancellationToken);
    }

    public static async Task ProbeObjectStorageAsync(
        ILargeObjectStore objects,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(objects);
        if (!string.Equals(objects.Provider, LargeObjectProviders.AzureBlob, StringComparison.Ordinal))
            throw new InvalidOperationException("Distributed storage must use the Azure Blob protocol.");

        var content = RandomNumberGenerator.GetBytes(262_145);
        var hash = Convert.ToHexStringLower(SHA256.HashData(content));
        var name = $"health/distributed/{Guid.CreateVersion7():N}";
        LargeObjectWriteResult? written = null;
        try
        {
            await using var source = new MemoryStream(content, writable: false);
            written = await objects.PutIfAbsentAsync(
                name,
                source,
                content.LongLength,
                hash,
                "application/octet-stream",
                cancellationToken);
            if (!written.Created)
                throw new InvalidOperationException("The distributed Blob probe object already existed.");

            await using var destination = new MemoryStream();
            await objects.CopyToAsync(written.Reference, destination, cancellationToken);
            if (!content.AsSpan().SequenceEqual(destination.ToArray()))
                throw new InvalidOperationException("The distributed Blob probe read back different bytes.");
        }
        finally
        {
            using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            if (written?.Created == true
                && !await objects.DeleteIfMatchAsync(written.Reference, cleanupTimeout.Token))
            {
                throw new InvalidOperationException("The distributed Blob probe object could not be removed.");
            }
        }
    }
}
