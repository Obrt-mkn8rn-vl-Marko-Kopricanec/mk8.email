using System.Globalization;
using System.Security.Cryptography;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using mk8.email.Storage;

namespace mk8.email.Messaging.Tests;

[TestClass]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1812", Justification = "MSTest discovers and executes this compatible-endpoint fixture by reflection.")]
internal sealed class AzureBlobReplacementSafetyTests
{
    public TestContext? TestContext { get; set; }

    [TestMethod]
    [TestCategory("AzureBlobCompatible")]
    [DataRow("missing-hash")]
    [DataRow("wrong-hash")]
    [DataRow("missing-length")]
    [DataRow("wrong-length")]
    [DataRow("wrong-content-length")]
    public async Task CorruptCollisionRefusesReplayAndCopyWithoutChangingStoredBytes(string corruption)
    {
        Assert.IsNotNull(TestContext);
        var cancellationToken = TestContext.CancellationToken;
        var fixture = CreateFixture();
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        await fixture.Container.CreateAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        var original = "immutable payload"u8.ToArray();
        var hash = Hash(original);
        var metadata = Metadata(original, hash);
        var stored = Corrupt(corruption, original, metadata);
        var response = await fixture.Blob.UploadAsync(BinaryData.FromBytes(stored),
            new BlobUploadOptions { Metadata = metadata }, cancellationToken).ConfigureAwait(false);
        var input = new MemoryStream(original, writable: false);
        await using var inputLifetime = input.ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => fixture.Store.PutIfAbsentAsync(
            "object", input, original.LongLength, hash, "application/octet-stream", cancellationToken)).ConfigureAwait(false);
        var reference = new mk8.email.Contracts.Storage.LargeObjectReference(
            fixture.Store.Provider, "object", original.LongLength, hash, response.Value.ETag.ToString());
        var destination = new MemoryStream();
        await using var destinationLifetime = destination.ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            fixture.Store.CopyToAsync(reference, destination, cancellationToken)).ConfigureAwait(false);
        Assert.AreEqual(0L, destination.Length);
        await AssertStoredAsync(fixture.Blob, stored, response.Value.ETag, cancellationToken).ConfigureAwait(false);
    }

    [TestMethod]
    [TestCategory("AzureBlobCompatible")]
    public async Task StaleReferenceCannotReadOrDeleteReplacement()
    {
        Assert.IsNotNull(TestContext);
        var cancellationToken = TestContext.CancellationToken;
        var fixture = CreateFixture();
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        await fixture.Container.CreateAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        var original = "original payload"u8.ToArray();
        var input = new MemoryStream(original, writable: false);
        await using var inputLifetime = input.ConfigureAwait(false);
        var first = await fixture.Store.PutIfAbsentAsync(
            "object", input, original.LongLength, Hash(original), "application/octet-stream", cancellationToken).ConfigureAwait(false);
        var replacement = "replacement payload"u8.ToArray();
        var hash = Hash(replacement);
        var replaced = await fixture.Blob.UploadAsync(BinaryData.FromBytes(replacement),
            new BlobUploadOptions { Metadata = Metadata(replacement, hash) }, cancellationToken).ConfigureAwait(false);
        Assert.AreNotEqual(first.Reference.EntityTag, replaced.Value.ETag.ToString(), StringComparer.Ordinal);
        var destination = new MemoryStream();
        await using var destinationLifetime = destination.ConfigureAwait(false);
        var failure = await Assert.ThrowsExactlyAsync<RequestFailedException>(() =>
            fixture.Store.CopyToAsync(first.Reference, destination, cancellationToken)).ConfigureAwait(false);
        Assert.AreEqual(412, failure.Status);
        Assert.AreEqual(0L, destination.Length);
        Assert.IsFalse(await fixture.Store.DeleteIfMatchAsync(first.Reference, cancellationToken).ConfigureAwait(false));
        await AssertStoredAsync(fixture.Blob, replacement, replaced.Value.ETag, cancellationToken).ConfigureAwait(false);
        await AssertReplacementAsync(fixture.Store, replacement, hash, replaced.Value.ETag, cancellationToken).ConfigureAwait(false);
    }

    private static async Task AssertReplacementAsync(
        AzureBlobLargeObjectStore store, byte[] content, string hash, ETag tag, CancellationToken cancellationToken)
    {
        var input = new MemoryStream(content, writable: false);
        await using var inputLifetime = input.ConfigureAwait(false);
        var replay = await store.PutIfAbsentAsync(
            "object", input, content.LongLength, hash, "application/octet-stream", cancellationToken).ConfigureAwait(false);
        Assert.IsFalse(replay.Created);
        Assert.AreEqual(tag.ToString(), replay.Reference.EntityTag, StringComparer.Ordinal);
        var destination = new MemoryStream();
        await using var destinationLifetime = destination.ConfigureAwait(false);
        await store.CopyToAsync(replay.Reference, destination, cancellationToken).ConfigureAwait(false);
        Assert.AreSequenceEqual(content, destination.ToArray());
        Assert.IsTrue(await store.DeleteIfMatchAsync(replay.Reference, cancellationToken).ConfigureAwait(false));
        Assert.IsFalse(await store.DeleteIfMatchAsync(replay.Reference, cancellationToken).ConfigureAwait(false));
    }

    private static async Task AssertStoredAsync(BlobClient blob, byte[] content, ETag tag, CancellationToken cancellationToken)
    {
        var downloaded = await blob.DownloadContentAsync(
            new BlobDownloadOptions { Conditions = new BlobRequestConditions { IfMatch = tag } }, cancellationToken).ConfigureAwait(false);
        Assert.AreSequenceEqual(content, downloaded.Value.Content.ToArray());
        Assert.AreEqual(tag, downloaded.Value.Details.ETag);
    }

    private static string Hash(byte[] content) => Convert.ToHexStringLower(SHA256.HashData(content));

    private static Dictionary<string, string> Metadata(byte[] content, string hash) => new(StringComparer.Ordinal)
    {
        ["mk8sha256"] = hash,
        ["mk8length"] = content.LongLength.ToString(CultureInfo.InvariantCulture),
    };

    private static byte[] Corrupt(string corruption, byte[] original, Dictionary<string, string> metadata)
    {
        switch (corruption)
        {
            case "missing-hash": metadata.Remove("mk8sha256"); break;
            case "wrong-hash": metadata["mk8sha256"] = new string('0', 64); break;
            case "missing-length": metadata.Remove("mk8length"); break;
            case "wrong-length": metadata["mk8length"] = "1"; break;
            case "wrong-content-length": return "short"u8.ToArray();
            default: throw new ArgumentOutOfRangeException(nameof(corruption));
        }
        return original;
    }

    private static Fixture CreateFixture()
    {
        var connection = Environment.GetEnvironmentVariable("MK8_EMAIL_TEST_AZURE_BLOB_CONNECTION");
        if (string.IsNullOrWhiteSpace(connection))
        {
            Assert.Inconclusive("Set MK8_EMAIL_TEST_AZURE_BLOB_CONNECTION to an owned compatible test endpoint.");
        }
        return new Fixture(new BlobServiceClient(connection));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public Fixture(BlobServiceClient service)
        {
            Container = service.GetBlobContainerClient($"mk8-replacement-{Guid.NewGuid():N}");
            Store = new AzureBlobLargeObjectStore(service,
                new AzureBlobLargeObjectStoreOptions { ContainerName = Container.Name, ObjectPrefix = "fixture" });
            Blob = Container.GetBlobClient("fixture/object");
        }

        public BlobContainerClient Container { get; }
        public BlobClient Blob { get; }
        public AzureBlobLargeObjectStore Store { get; }

        // Cleanup owns its wait independently of cancellation of the test body.
        public async ValueTask DisposeAsync() =>
            await Container.DeleteIfExistsAsync(cancellationToken: CancellationToken.None).ConfigureAwait(false);
    }
}
