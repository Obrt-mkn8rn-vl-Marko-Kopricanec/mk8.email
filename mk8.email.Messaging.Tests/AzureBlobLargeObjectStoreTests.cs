using System.Security.Cryptography;
using Azure.Storage.Blobs;
using mk8.email.Storage;

namespace mk8.email.Messaging.Tests;

[TestClass]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1812", Justification = "MSTest DiscoverInternals instantiates this test class by reflection; focused discovery is verified by executed test counts.")]
internal sealed class AzureBlobLargeObjectStoreTests
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
            var firstStream = new MemoryStream(content, writable: false);
            await using var firstStreamLifetime = firstStream.ConfigureAwait(false);
            var first = await store.PutIfAbsentAsync(
                "objects/round-trip",
                firstStream,
                content.LongLength,
                hash,
                "application/octet-stream").ConfigureAwait(false);
            Assert.IsTrue(first.Created);

            var secondStream = new MemoryStream(content, writable: false);
            await using var secondStreamLifetime = secondStream.ConfigureAwait(false);
            var second = await store.PutIfAbsentAsync(
                "objects/round-trip",
                secondStream,
                content.LongLength,
                hash,
                "application/octet-stream").ConfigureAwait(false);
            Assert.IsFalse(second.Created);
            Assert.AreEqual(first.Reference, second.Reference);

            var destination = new MemoryStream();
            await using var destinationLifetime = destination.ConfigureAwait(false);
            await store.CopyToAsync(first.Reference, destination).ConfigureAwait(false);
            CollectionAssert.AreEqual(content, destination.ToArray());
            Assert.IsTrue(await store.DeleteIfMatchAsync(first.Reference).ConfigureAwait(false));
            Assert.IsFalse(await store.DeleteIfMatchAsync(first.Reference).ConfigureAwait(false));
        }
        finally
        {
            await container.DeleteIfExistsAsync().ConfigureAwait(false);
        }
    }

    [TestMethod]
    public void AdapterRejectsInvalidContainerConfigurationBeforeNetworkAccess()
    {
        var serviceClient = new BlobServiceClient(
            "DefaultEndpointsProtocol=https;AccountName=example;"
            + "AccountKey=ZmFrZS1mYWtlLWZha2UtZmFrZS1mYWtlLWZha2U=;"
            + "BlobEndpoint=https://blob.example.invalid/;");

        var invalidContainer = Assert.ThrowsExactly<ArgumentException>(() =>
            new AzureBlobLargeObjectStore(
                serviceClient,
                new AzureBlobLargeObjectStoreOptions { ContainerName = "X" }));
        Assert.AreEqual("options", invalidContainer.ParamName, StringComparer.Ordinal);

        var invalidPrefix = Assert.ThrowsExactly<ArgumentException>(() =>
            new AzureBlobLargeObjectStore(
                serviceClient,
                new AzureBlobLargeObjectStoreOptions { ObjectPrefix = "/objects" }));
        Assert.AreEqual("options", invalidPrefix.ParamName, StringComparer.Ordinal);
    }
}
