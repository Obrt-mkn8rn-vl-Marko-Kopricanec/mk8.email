using Azure.Storage.Blobs;
using mk8.email.Contracts.Storage;
using mk8.email.Hosting;
using mk8.email.Storage;
using Npgsql;

namespace mk8.email.Messaging.Tests;

[TestClass]
public sealed class DistributedBackendProbeTests
{
    [TestMethod]
    public async Task CorruptBlobReadFailsClosedAndStillDeletesTheCanary()
    {
        var objects = new CorruptingObjectStore();

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => DistributedBackendProbe.ProbeObjectStorageAsync(objects));

        Assert.AreEqual(1, objects.DeleteCount);
    }

    [TestMethod]
    public async Task CorruptBlobReadAndCleanupFailureReportBothErrors()
    {
        var objects = new CorruptingObjectStore(failDelete: true);

        var failure = await Assert.ThrowsExactlyAsync<AggregateException>(
            () => DistributedBackendProbe.ProbeObjectStorageAsync(objects));

        Assert.AreEqual(2, failure.InnerExceptions.Count);
        Assert.IsInstanceOfType<InvalidOperationException>(failure.InnerExceptions[0]);
        Assert.IsInstanceOfType<InvalidOperationException>(failure.InnerExceptions[1]);
        Assert.AreEqual(1, objects.DeleteCount);
    }

    [TestMethod]
    [TestCategory("PostgreSQL")]
    [TestCategory("AzureBlobCompatible")]
    public async Task RemoteDatabaseAndAzureBlobCanaryRoundTripWithoutLeavingAnObject()
    {
        var connectionString = Environment.GetEnvironmentVariable(
            "MK8_EMAIL_TEST_AZURE_BLOB_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Assert.Inconclusive("Set MK8_EMAIL_TEST_AZURE_BLOB_CONNECTION.");
            return;
        }

        await using var database = await PostgresTestDatabase.TryCreateAsync();
        if (database is null)
        {
            Assert.Inconclusive("Set MK8_EMAIL_TEST_POSTGRES.");
            return;
        }

        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        var service = new BlobServiceClient(connectionString);
        var container = service.GetBlobContainerClient($"mk8-probe-{Guid.NewGuid():N}");
        var objects = new AzureBlobLargeObjectStore(
            service,
            new AzureBlobLargeObjectStoreOptions
            {
                ContainerName = container.Name,
                CreateContainerIfMissing = true,
            });
        try
        {
            await DistributedBackendProbe.ProbeAsync(dataSource, objects);

            await foreach (var _ in container.GetBlobsAsync())
                Assert.Fail("The backend probe left an Azure Blob object behind.");
        }
        finally
        {
            await container.DeleteIfExistsAsync();
        }
    }

    private sealed class CorruptingObjectStore(bool failDelete = false) : ILargeObjectStore
    {
        public string Provider => LargeObjectProviders.AzureBlob;
        public int DeleteCount { get; private set; }

        public Task<LargeObjectWriteResult> PutIfAbsentAsync(
            string objectName,
            Stream content,
            long length,
            string sha256,
            string contentType,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new LargeObjectWriteResult(
                new LargeObjectReference(Provider, objectName, length, sha256, "etag"),
                Created: true));

        public async Task CopyToAsync(
            LargeObjectReference reference,
            Stream destination,
            CancellationToken cancellationToken = default) =>
            await destination.WriteAsync(new byte[checked((int)reference.Length)], cancellationToken);

        public Task<bool> DeleteIfMatchAsync(
            LargeObjectReference reference,
            CancellationToken cancellationToken = default)
        {
            DeleteCount++;
            if (failDelete)
                throw new InvalidOperationException("The canary cleanup failed.");
            return Task.FromResult(true);
        }
    }
}
