using System.Security.Cryptography;
using Azure.Storage.Blobs;
using mk8.email.Storage;

namespace mk8.email.Messaging.Tests;

[TestClass]
public sealed class AzureBlobLargeObjectStoreTests
{
    [TestMethod]
    [TestCategory("AzureBlobCompatible")]
    public async Task AdapterRoundTripsAgainstConfiguredCompatibleEndpoint()
    {
        var connectionString = Environment.GetEnvironmentVariable(
            "MK8_EMAIL_TEST_AZURE_BLOB_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Assert.Inconclusive(
                "Set MK8_EMAIL_TEST_AZURE_BLOB_CONNECTION to an Azure Blob-compatible test endpoint.");
            return;
        }

        var serviceClient = new BlobServiceClient(connectionString);
        var containerName = $"mk8-test-{Guid.NewGuid():N}";
        var container = serviceClient.GetBlobContainerClient(containerName);
        var store = new AzureBlobLargeObjectStore(
            serviceClient,
            new AzureBlobLargeObjectStoreOptions
            {
                ContainerName = containerName,
                ObjectPrefix = "protocol-test",
                CreateContainerIfMissing = true,
            });
        var content = RandomNumberGenerator.GetBytes(2048);
        var hash = Convert.ToHexStringLower(SHA256.HashData(content));

        try
        {
            await using var firstStream = new MemoryStream(content, writable: false);
            var first = await store.PutIfAbsentAsync(
                "objects/round-trip",
                firstStream,
                content.LongLength,
                hash,
                "application/octet-stream");
            Assert.IsTrue(first.Created);

            await using var secondStream = new MemoryStream(content, writable: false);
            var second = await store.PutIfAbsentAsync(
                "objects/round-trip",
                secondStream,
                content.LongLength,
                hash,
                "application/octet-stream");
            Assert.IsFalse(second.Created);
            Assert.AreEqual(first.Reference, second.Reference);

            await using var destination = new MemoryStream();
            await store.CopyToAsync(first.Reference, destination);
            CollectionAssert.AreEqual(content, destination.ToArray());
            Assert.IsTrue(await store.DeleteIfMatchAsync(first.Reference));
            Assert.IsFalse(await store.DeleteIfMatchAsync(first.Reference));
        }
        finally
        {
            await container.DeleteIfExistsAsync();
        }
    }

    [TestMethod]
    public void AdapterRejectsInvalidContainerConfigurationBeforeNetworkAccess()
    {
        var serviceClient = new BlobServiceClient(
            "DefaultEndpointsProtocol=https;AccountName=example;"
            + "AccountKey=ZmFrZS1mYWtlLWZha2UtZmFrZS1mYWtlLWZha2U=;"
            + "BlobEndpoint=https://blob.example.invalid/;");

        Assert.ThrowsExactly<ArgumentException>(() =>
            new AzureBlobLargeObjectStore(
                serviceClient,
                new AzureBlobLargeObjectStoreOptions { ContainerName = "X" }));
    }
}
