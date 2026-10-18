using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Storage;
using Azure.Storage.Blobs;
using mk8.email.Storage;

namespace mk8.email.Messaging.Tests;

[TestClass]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1812", Justification = "MSTest discovers and executes these no-network endpoint admission controls by reflection.")]
internal sealed class AzureBlobEndpointAdmissionTests
{
    // Reserved DNS identities, loopback URI classification and synthetic key bytes
    // are constructor fixtures, never requested or approved deployment endpoints.
    private const string Container = "owned-objects";
    private const string SyntheticKey = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=";

    [TestMethod]
    [DataRow("http://account.blob.example.test/")]
    [DataRow("http://localhost:10000/account")]
    [DataRow("http://192.0.2.17:10000/account")]
    [DataRow("http://[2001:db8::17]:10000/account")]
    public void NonliteralOrNonloopbackHttpRefusesBeforeAnyBlobRequest(string endpoint)
    {
        // TEST-NET and documentation IPv6 values classify non-loopback; no
        // address is resolved, bound, contacted or approved by these controls.
        var transport = new NoNetworkTransport();
        var service = CreateService(endpoint, transport);
        var before = service.Uri;
        var error = Assert.ThrowsExactly<ArgumentException>(() => new AzureBlobLargeObjectStore(service,
            new AzureBlobLargeObjectStoreOptions { ContainerName = Container, CreateContainerIfMissing = true }));
        Assert.AreEqual("serviceClient", error.ParamName, StringComparer.Ordinal);
        Assert.StartsWith("Azure Blob transport requires HTTPS or explicit literal loopback HTTP.", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(endpoint, error.Message, StringComparison.Ordinal);
        Assert.AreEqual(before, service.Uri);
        Assert.AreEqual(0, transport.Requests);
    }

    [TestMethod]
    [DataRow("https://blob.example.test/account")]
    [DataRow("https://blob.example.test/account/")]
    [DataRow("https://blob.example.test/owned-objects")]
    [DataRow("https://blob.example.test/account/nested")]
    public void AdapterRefusesContainerLossBeforeAnyRequest(string endpoint)
    {
        var transport = new NoNetworkTransport();
        var service = CreateService(endpoint, transport);
        var container = service.GetBlobContainerClient(Container);
        var objectClient = container.GetBlobClient("mail/probe.bin");
        Assert.AreNotEqual(container.Uri.AbsolutePath + "/mail/probe.bin", objectClient.Uri.AbsolutePath, StringComparer.Ordinal);
        var error = Assert.ThrowsExactly<ArgumentException>(() => new AzureBlobLargeObjectStore(
            service, new AzureBlobLargeObjectStoreOptions { ContainerName = Container, CreateContainerIfMissing = true }));
        Assert.AreEqual("serviceClient", error.ParamName, StringComparer.Ordinal);
        Assert.DoesNotContain(endpoint, error.Message, StringComparison.Ordinal);
        Assert.AreEqual(0, transport.Requests);
    }

    [TestMethod]
    [DataRow("https://account.blob.example.test/")]
    [DataRow("https://blob.example.test/")]
    [DataRow("http://127.0.0.1:10000/account")]
    [DataRow("https://127.0.0.1:10000/account")]
    [DataRow("https://[::1]:10000/account")]
    public void AdapterAdmitsOnlyPreservedContainerConstruction(string endpoint)
    {
        var transport = new NoNetworkTransport();
        var service = CreateService(endpoint, transport);
        var container = service.GetBlobContainerClient(Container);
        var objectClient = container.GetBlobClient("mail/probe.bin");
        Assert.AreEqual(container.Uri.AbsolutePath + "/mail/probe.bin", objectClient.Uri.AbsolutePath, StringComparer.Ordinal);
        var store = new AzureBlobLargeObjectStore(service, new AzureBlobLargeObjectStoreOptions { ContainerName = Container });
        Assert.AreEqual("azure-blob", store.Provider, StringComparer.Ordinal);
        Assert.AreEqual(0, transport.Requests);
    }

    [TestMethod]
    public void ConnectionStringFactoryRefusesAmbiguousContainerConstruction()
    {
        const string connection = "DefaultEndpointsProtocol=https;AccountName=account;AccountKey=" + SyntheticKey
            + ";BlobEndpoint=https://blob.example.invalid/account;";
        var error = Assert.ThrowsExactly<ArgumentException>(() => AzureBlobLargeObjectStore.FromConnectionString(
            connection, new AzureBlobLargeObjectStoreOptions { ContainerName = Container }));
        Assert.AreEqual("serviceClient", error.ParamName, StringComparer.Ordinal);
        Assert.DoesNotContain(SyntheticKey, error.Message, StringComparison.Ordinal);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void SasConstructionRetainsQueryAndRefusesOnlyAmbiguousPath(bool ambiguous)
    {
        var transport = new NoNetworkTransport();
        var endpoint = new Uri(ambiguous
            ? "https://blob.example.test/account?sv=2026-06-06&sig=fixture-only"
            : "https://blob.example.test/?sv=2026-06-06&sig=fixture-only");
        var service = new BlobServiceClient(endpoint, new BlobClientOptions { Transport = transport });
        var container = service.GetBlobContainerClient(Container);
        var before = container.Uri.Query;
        Assert.Contains("sig=fixture-only", before, StringComparison.Ordinal);
        if (ambiguous)
        {
            var error = Assert.ThrowsExactly<ArgumentException>(() => new AzureBlobLargeObjectStore(
                service, new AzureBlobLargeObjectStoreOptions { ContainerName = Container }));
            Assert.DoesNotContain("fixture-only", error.Message, StringComparison.Ordinal);
        }
        else
        {
            var store = new AzureBlobLargeObjectStore(service, new AzureBlobLargeObjectStoreOptions { ContainerName = Container });
            Assert.AreEqual("azure-blob", store.Provider, StringComparer.Ordinal);
        }
        Assert.AreEqual(before, container.Uri.Query, StringComparer.Ordinal);
        Assert.AreEqual(0, transport.Requests);
    }

    private static BlobServiceClient CreateService(string endpoint, NoNetworkTransport transport) =>
        new(new Uri(endpoint), new StorageSharedKeyCredential("account", SyntheticKey), new BlobClientOptions { Transport = transport });

    private sealed class NoNetworkTransport : HttpPipelineTransport
    {
        public int Requests { get; private set; }
        public override Request CreateRequest() => HttpClientTransport.Shared.CreateRequest();

        public override void Process(HttpMessage message)
        {
            Requests++;
            throw new InvalidOperationException("No network request is admitted by this constructor control.");
        }

        public override ValueTask ProcessAsync(HttpMessage message)
        {
            Requests++;
            return ValueTask.FromException(new InvalidOperationException("No network request is admitted by this constructor control."));
        }
    }
}
