using System.Security.Cryptography;
using mk8.email.Contracts.Storage;
using mk8.email.Hosting;
using Npgsql;

namespace mk8.email.Messaging.Tests;

[TestClass]
[TestCategory("PostgreSQL")]
public sealed class PostgresBlobDeletionBarrierTests
{
    [TestMethod]
    public async Task ExclusiveBackupLeaseBlocksPhysicalDeletionButNotBlobReads()
    {
        await using var database = await RequirePostgresAsync();
        await using var backupSource = NpgsqlDataSource.Create(database.ConnectionString);
        await using var applicationSource = NpgsqlDataSource.Create(database.ConnectionString);
        var raw = new InMemoryLargeObjectStore();
        var coordinated = new PostgresCoordinatedLargeObjectStore(applicationSource, raw);
        var content = "backup keeps these bytes"u8.ToArray();
        var sha256 = Convert.ToHexStringLower(SHA256.HashData(content));
        await using var upload = new MemoryStream(content, writable: false);
        var written = await coordinated.PutIfAbsentAsync(
            "backup/live", upload, content.LongLength, sha256, "text/plain");

        await using var lease = await PostgresBlobDeletionBarrier.AcquireExclusiveAsync(backupSource);
        var deletion = coordinated.DeleteIfMatchAsync(written.Reference);
        await WaitForAdvisoryWaitAsync(backupSource);
        Assert.IsFalse(deletion.IsCompleted);

        await using (var copied = new MemoryStream())
        {
            await coordinated.CopyToAsync(written.Reference, copied);
            CollectionAssert.AreEqual(content, copied.ToArray());
        }

        await lease.DisposeAsync();
        Assert.IsTrue(await deletion.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.AreEqual(0, raw.ObjectCount);
    }

    [TestMethod]
    public async Task BackupLeaseWaitsForEarlierPhysicalDeletionToFinish()
    {
        await using var database = await RequirePostgresAsync();
        await using var backupSource = NpgsqlDataSource.Create(database.ConnectionString);
        await using var applicationSource = NpgsqlDataSource.Create(database.ConnectionString);
        var inner = new BlockingDeleteStore();
        var coordinated = new PostgresCoordinatedLargeObjectStore(applicationSource, inner);
        var reference = new LargeObjectReference(
            LargeObjectProviders.AzureBlob, "backup/earlier", 0,
            new string('a', 64), "etag");

        var deletion = coordinated.DeleteIfMatchAsync(reference);
        await inner.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var acquiring = PostgresBlobDeletionBarrier.AcquireExclusiveAsync(backupSource);
        try
        {
            await WaitForAdvisoryWaitAsync(backupSource);
            Assert.IsFalse(acquiring.IsCompleted);
        }
        finally
        {
            inner.Release.TrySetResult(true);
        }

        Assert.IsTrue(await deletion.WaitAsync(TimeSpan.FromSeconds(5)));
        await using var lease = await acquiring.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsTrue(inner.Finished);
    }

    [TestMethod]
    public async Task FailedPhysicalDeletionReleasesSharedLease()
    {
        await using var database = await RequirePostgresAsync();
        await using var backupSource = NpgsqlDataSource.Create(database.ConnectionString);
        await using var applicationSource = NpgsqlDataSource.Create(database.ConnectionString);
        var coordinated = new PostgresCoordinatedLargeObjectStore(
            applicationSource, new ThrowingDeleteStore());
        var reference = new LargeObjectReference(
            LargeObjectProviders.AzureBlob, "backup/failed", 0,
            new string('a', 64), "etag");

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            coordinated.DeleteIfMatchAsync(reference));
        await using var lease = await PostgresBlobDeletionBarrier
            .AcquireExclusiveAsync(backupSource).WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static async Task WaitForAdvisoryWaitAsync(NpgsqlDataSource source)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            await using var connection = await source.OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT count(*) FROM pg_stat_activity
                WHERE datname = current_database()
                    AND wait_event_type = 'Lock'
                    AND wait_event = 'advisory'
                """;
            if ((long)(await command.ExecuteScalarAsync())! > 0)
                return;
            await Task.Delay(20);
        }

        Assert.Fail("No PostgreSQL session waited on the Blob deletion barrier.");
    }

    private static async Task<PostgresTestDatabase> RequirePostgresAsync()
    {
        var database = await PostgresTestDatabase.TryCreateAsync();
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
            await Release.Task.WaitAsync(cancellationToken);
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
