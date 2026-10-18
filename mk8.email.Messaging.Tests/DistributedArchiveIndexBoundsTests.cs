using mk8.email.Hosting;

namespace mk8.email.Messaging.Tests;

[TestClass]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1812", Justification = "MSTest discovers and executes these pinned-SDK no-network archive index controls by reflection.")]
internal sealed class DistributedArchiveIndexBoundsTests
{
    private static readonly string[] RefusedRequests = ["HEAD complete.json", "GET complete.json"];

    public TestContext? TestContext { get; set; }

    [TestMethod]
    [DataRow(0)]
    [DataRow(4096)]
    public async Task ExactAdmittedIndexDownloadsSignedObjectsAndPublishesDestination(int length)
    {
        RequireLinux();
        Assert.IsNotNull(TestContext);
        using var fixture = new DistributedArchiveIndexFixture();
        await fixture.PrepareAsync(length, length, TestContext.CancellationToken).ConfigureAwait(false);
        await FetchAsync(fixture, TestContext.CancellationToken).ConfigureAwait(false);
        await fixture.AssertFetchedAsync(TestContext.CancellationToken).ConfigureAwait(false);
        Assert.IsTrue(Directory.Exists(fixture.Destination));
        Assert.HasCount(2, Directory.EnumerateFiles(fixture.Destination));
        Assert.HasCount(0, Directory.EnumerateDirectories(fixture.Root.FullName, ".archive-download-*"));
    }

    [TestMethod]
    public async Task OversizedReturnedIndexRefusesBeforeStagingOrObjectRequests()
    {
        RequireLinux();
        Assert.IsNotNull(TestContext);
        using var fixture = new DistributedArchiveIndexFixture();
        await fixture.PrepareAsync(4096, 4097, TestContext.CancellationToken).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<NotSupportedException>(() => FetchAsync(fixture, TestContext.CancellationToken)).ConfigureAwait(false);
        AssertRefused(fixture);
    }

    [TestMethod]
    public async Task ShortValidReturnedIndexRefusesBeforeStagingOrObjectRequests()
    {
        RequireLinux();
        Assert.IsNotNull(TestContext);
        using var fixture = new DistributedArchiveIndexFixture();
        await fixture.PrepareAsync(4096, 4095, TestContext.CancellationToken).ConfigureAwait(false);
        var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => FetchAsync(fixture, TestContext.CancellationToken)).ConfigureAwait(false);
        Assert.AreEqual("The archive completion index length changed.", error.Message, StringComparer.Ordinal);
        AssertRefused(fixture);
    }

    private static Task FetchAsync(DistributedArchiveIndexFixture fixture, CancellationToken token) =>
        DistributedArchiveStore.FetchWithVerifierAsync(fixture.Service, DistributedArchiveIndexFixture.Container,
            DistributedArchiveIndexFixture.ArchiveId, fixture.Destination, fixture.Verifier, token);

    private static void AssertRefused(DistributedArchiveIndexFixture fixture)
    {
        Assert.IsFalse(Path.Exists(fixture.Destination));
        Assert.HasCount(0, Directory.EnumerateDirectories(fixture.Root.FullName));
        Assert.AreSequenceEqual(RefusedRequests, fixture.Requests, StringComparer.Ordinal);
        Assert.IsTrue(fixture.ConditionsMatched);
    }

    private static void RequireLinux()
    {
        if (!OperatingSystem.IsLinux()) Assert.Inconclusive("The actual archive path/permission contract requires Linux.");
    }
}
