using System.Security.Cryptography;
using mk8.email.Contracts.Storage;
using mk8.email.Hosting;
using Npgsql;

namespace mk8.email.Messaging.Tests;

[TestClass]
[TestCategory("PostgreSQL")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1812", Justification = "MSTest DiscoverInternals instantiates this test class by reflection; focused discovery is verified by executed test counts.")]
internal sealed class PostgresBlobDeletionBarrierTests
{
    [TestMethod]
    public async Task ExclusiveBackupLeaseBlocksPhysicalDeletionButNotBlobReads()
    {
        var database = (await RequirePostgresAsync().ConfigureAwait(false));
        await using var databaseLifetime = database.ConfigureAwait(false);
        var backupSource = NpgsqlDataSource.Create(database.ConnectionString);
        await using var backupSourceLifetime = backupSource.ConfigureAwait(false);
        var applicationSource = NpgsqlDataSource.Create(database.ConnectionString);
        await using var applicationSourceLifetime = applicationSource.ConfigureAwait(false);
        var raw = new InMemoryLargeObjectStore();
        var coordinated = new PostgresCoordinatedLargeObjectStore(applicationSource, raw);
        var content = "backup keeps these bytes"u8.ToArray();
        var sha256 = Convert.ToHexStringLower(SHA256.HashData(content));
        var upload = new MemoryStream(content, writable: false);
        await using var uploadLifetime = upload.ConfigureAwait(false);
        var written = await coordinated.PutIfAbsentAsync(
            "backup/live", upload, content.LongLength, sha256, "text/plain").ConfigureAwait(false);

        var lease = (await PostgresBlobDeletionBarrier.AcquireExclusiveAsync(backupSource).ConfigureAwait(false));
        await using var leaseLifetime = lease.ConfigureAwait(false);
        var deletion = coordinated.DeleteIfMatchAsync(written.Reference);
        await WaitForAdvisoryWaitAsync(backupSource).ConfigureAwait(false);
        Assert.IsFalse(deletion.IsCompleted);

        {
            var copied = new MemoryStream();
            await using var copiedLifetime = copied.ConfigureAwait(false);
            await coordinated.CopyToAsync(written.Reference, copied).ConfigureAwait(false);
            CollectionAssert.AreEqual(content, copied.ToArray());
        }

        await lease.DisposeAsync().ConfigureAwait(false);
        Assert.IsTrue(await deletion.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false));
        Assert.AreEqual(0, raw.ObjectCount);
    }

    [TestMethod]
    public async Task BackupLeaseWaitsForEarlierPhysicalDeletionToFinish()
    {
        var database = (await RequirePostgresAsync().ConfigureAwait(false));
        await using var databaseLifetime = database.ConfigureAwait(false);
        var backupSource = NpgsqlDataSource.Create(database.ConnectionString);
        await using var backupSourceLifetime = backupSource.ConfigureAwait(false);
        var applicationSource = NpgsqlDataSource.Create(database.ConnectionString);
        await using var applicationSourceLifetime = applicationSource.ConfigureAwait(false);
        var inner = new BlockingDeleteStore();
        var coordinated = new PostgresCoordinatedLargeObjectStore(applicationSource, inner);
        var reference = new LargeObjectReference(
            LargeObjectProviders.AzureBlob, "backup/earlier", 0,
            new string('a', 64), "etag");

        var deletion = coordinated.DeleteIfMatchAsync(reference);
        await inner.Started.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        // Finally releases the test gate, joins this task and disposes its returned lease before either source lifetime ends.
#pragma warning disable CA2025
        var acquiring = PostgresBlobDeletionBarrier.AcquireExclusiveAsync(backupSource);
#pragma warning restore CA2025
        try
        {
            await WaitForAdvisoryWaitAsync(backupSource).ConfigureAwait(false);
            Assert.IsFalse(acquiring.IsCompleted);
        }
        finally
        {
            inner.Release.TrySetResult(true);
            Assert.IsTrue(await deletion.ConfigureAwait(false));
            var lease = await acquiring.ConfigureAwait(false);
            await lease.DisposeAsync().ConfigureAwait(false);
        }

        Assert.IsTrue(inner.Finished);
    }

    [TestMethod]
    public async Task FailedPhysicalDeletionReleasesSharedLease()
    {
        var database = (await RequirePostgresAsync().ConfigureAwait(false));
        await using var databaseLifetime = database.ConfigureAwait(false);
        var backupSource = NpgsqlDataSource.Create(database.ConnectionString);
        await using var backupSourceLifetime = backupSource.ConfigureAwait(false);
        var applicationSource = NpgsqlDataSource.Create(database.ConnectionString);
        await using var applicationSourceLifetime = applicationSource.ConfigureAwait(false);
        var coordinated = new PostgresCoordinatedLargeObjectStore(
            applicationSource, new ThrowingDeleteStore());
        var reference = new LargeObjectReference(
            LargeObjectProviders.AzureBlob, "backup/failed", 0,
            new string('a', 64), "etag");

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            coordinated.DeleteIfMatchAsync(reference)).ConfigureAwait(false);
        var lease = (await PostgresBlobDeletionBarrier
            .AcquireExclusiveAsync(backupSource).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false));
        await using var leaseLifetime = lease.ConfigureAwait(false);
    }

    private static async Task WaitForAdvisoryWaitAsync(NpgsqlDataSource source)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            var connection = (await source.OpenConnectionAsync().ConfigureAwait(false));
            await using var connectionLifetime = connection.ConfigureAwait(false);
            var command = connection.CreateCommand();
            await using var commandLifetime = command.ConfigureAwait(false);
            command.CommandText = """
                SELECT count(*) FROM pg_stat_activity
                WHERE datname = current_database()
                    AND wait_event_type = 'Lock'
                    AND wait_event = 'advisory'
                """;
            if ((long)(await command.ExecuteScalarAsync().ConfigureAwait(false))! > 0)
                return;
            await Task.Delay(20).ConfigureAwait(false);
        }

        Assert.Fail("No PostgreSQL session waited on the Blob deletion barrier.");
    }

    private static async Task<PostgresTestDatabase> RequirePostgresAsync()
    {
        var database = await PostgresTestDatabase.TryCreateAsync().ConfigureAwait(false);
        if (database is null)
            Assert.Inconclusive("Set MK8_EMAIL_TEST_POSTGRES.");
        return database!;
    }

    private sealed class BlockingDeleteStore : ILargeObjectStore
    {
        public TaskCompletionSource<bool> Started { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> Release { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Finished { get; private set; }
        public string Provider => LargeObjectProviders.AzureBlob;

        public Task<LargeObjectWriteResult> PutIfAbsentAsync(
            string objectName,
            Stream content,
            long length,
            string sha256,
            string contentType,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task CopyToAsync(
            LargeObjectReference reference,
            Stream destination,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public async Task<bool> DeleteIfMatchAsync(
            LargeObjectReference reference,
            CancellationToken cancellationToken = default)
        {
            Started.TrySetResult(true);
            await Release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            Finished = true;
            return true;
        }
    }

    private sealed class ThrowingDeleteStore : ILargeObjectStore
    {
        public string Provider => LargeObjectProviders.AzureBlob;

        public Task<LargeObjectWriteResult> PutIfAbsentAsync(
            string objectName,
            Stream content,
            long length,
            string sha256,
            string contentType,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task CopyToAsync(
            LargeObjectReference reference,
            Stream destination,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> DeleteIfMatchAsync(
            LargeObjectReference reference,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The simulated Blob deletion failed.");
    }
}
