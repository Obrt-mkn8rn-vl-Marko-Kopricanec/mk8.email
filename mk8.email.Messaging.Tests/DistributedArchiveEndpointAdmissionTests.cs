using System.Security.Cryptography;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Storage;
using Azure.Storage.Blobs;
using mk8.email.Hosting;

namespace mk8.email.Messaging.Tests;

[TestClass]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1812", Justification = "MSTest discovers and executes these no-network archive admission controls by reflection.")]
internal sealed class DistributedArchiveEndpointAdmissionTests
{
    private const string Container = "owned-archives";
    private const string ArchiveId = "fixture-archive";
    public TestContext? TestContext { get; set; }

    [TestMethod]
    [DataRow(false, "http://account.blob.example.test/")]
    [DataRow(true, "http://account.blob.example.test/")]
    [DataRow(false, "http://localhost:10000/account")]
    [DataRow(true, "http://localhost:10000/account")]
    [DataRow(false, "http://192.0.2.17:10000/account")]
    [DataRow(true, "http://192.0.2.17:10000/account")]
    [DataRow(false, "http://[2001:db8::17]:10000/account")]
    [DataRow(true, "http://[2001:db8::17]:10000/account")]
    public async Task NonliteralOrNonloopbackHttpRefusesBeforeArchiveRequestsOrStaging(bool fetch, string endpoint)
    {
        RequireLinux();
        Assert.IsNotNull(TestContext);
        using var fixture = new ArchiveFixture();
        await fixture.PrepareAsync(TestContext.CancellationToken).ConfigureAwait(false);
        var transport = new StoppedTransport();
        var service = CreateService(endpoint, transport);
        var before = service.Uri;
        var error = await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
            InvokeAsync(fetch, service, fixture, TestContext.CancellationToken)).ConfigureAwait(false);
        Assert.AreEqual("service", error.ParamName, StringComparer.Ordinal);
        Assert.StartsWith("Archive transport requires HTTPS or explicit literal loopback HTTP.", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(endpoint, error.Message, StringComparison.Ordinal);
        Assert.AreEqual(before, service.Uri);
        Assert.AreEqual(0, transport.Requests);
        Assert.IsFalse(Path.Exists(fixture.Destination));
        Assert.HasCount(1, Directory.EnumerateFileSystemEntries(fixture.Root.FullName));
    }

    [TestMethod]
    [DataRow(false, "https://blob.example.test/account")]
    [DataRow(true, "https://blob.example.test/account")]
    [DataRow(false, "https://blob.example.test/account/")]
    [DataRow(true, "https://blob.example.test/account/")]
    [DataRow(false, "https://blob.example.test/owned-archives")]
    [DataRow(true, "https://blob.example.test/owned-archives")]
    [DataRow(false, "https://blob.example.test/account/nested")]
    [DataRow(true, "https://blob.example.test/account/nested")]
    public async Task AmbiguousEndpointRefusesBeforeArchiveNetworkOrDestinationEffects(bool fetch, string endpoint)
    {
        RequireLinux();
        Assert.IsNotNull(TestContext);
        using var fixture = new ArchiveFixture();
        await fixture.PrepareAsync(TestContext.CancellationToken).ConfigureAwait(false);
        var transport = new StoppedTransport();
        var service = CreateService(endpoint, transport);
        var error = await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
            InvokeAsync(fetch, service, fixture, TestContext.CancellationToken)).ConfigureAwait(false);
        Assert.AreEqual("service", error.ParamName, StringComparer.Ordinal);
        Assert.DoesNotContain(endpoint, error.Message, StringComparison.Ordinal);
        Assert.AreEqual(0, transport.Requests);
        Assert.IsFalse(Path.Exists(fixture.Destination));
        Assert.HasCount(1, Directory.EnumerateFileSystemEntries(fixture.Root.FullName));
    }

    [TestMethod]
    [DataRow(false, "https://account.blob.example.test/")]
    [DataRow(true, "https://account.blob.example.test/")]
    [DataRow(false, "https://blob.example.test/")]
    [DataRow(true, "https://blob.example.test/")]
    [DataRow(false, "http://127.0.0.1:10000/account")]
    [DataRow(true, "http://127.0.0.1:10000/account")]
    public async Task PreservedContainerReachesOnlyTheControlledFirstRequest(bool fetch, string endpoint)
    {
        RequireLinux();
        Assert.IsNotNull(TestContext);
        using var fixture = new ArchiveFixture();
        await fixture.PrepareAsync(TestContext.CancellationToken).ConfigureAwait(false);
        var transport = new StoppedTransport();
        var service = CreateService(endpoint, transport);
        var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            InvokeAsync(fetch, service, fixture, TestContext.CancellationToken)).ConfigureAwait(false);
        Assert.AreSame(transport.Fault, error);
        Assert.AreEqual(1, transport.Requests);
        Assert.AreEqual(service.GetBlobContainerClient(Container).Uri.AbsolutePath
            + (fetch ? $"/distributed-archives/v1/{ArchiveId}/complete.json" : ""),
            transport.FirstPath, StringComparer.Ordinal);
        Assert.IsFalse(Path.Exists(fixture.Destination));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task PinnedSdkIpv6RequestConstructionRefusesBeforeTransport(bool fetch)
    {
        RequireLinux();
        Assert.IsNotNull(TestContext);
        using var fixture = new ArchiveFixture();
        await fixture.PrepareAsync(TestContext.CancellationToken).ConfigureAwait(false);
        var transport = new StoppedTransport();
        var service = CreateService("https://[::1]:10000/account", transport);
        // Container construction preserves this shape, but the pinned SDK's
        // request URI parser does not admit it. This is NOT IPv6 network support.
        await Assert.ThrowsExactlyAsync<FormatException>(() =>
            InvokeAsync(fetch, service, fixture, TestContext.CancellationToken)).ConfigureAwait(false);
        Assert.AreEqual(0, transport.Requests);
        Assert.IsFalse(Path.Exists(fixture.Destination));
        Assert.HasCount(1, Directory.EnumerateFileSystemEntries(fixture.Root.FullName));
    }

    [TestMethod]
    public async Task RetiredFileEntryPointsRefuseBeforeArchiveOrTransportEffects()
    {
        if (!OperatingSystem.IsLinux()) throw new AssertInconclusiveException("The private archive fixture requires Linux.");
        Assert.IsNotNull(TestContext);
        using var fixture = new ArchiveFixture();
        await fixture.PrepareAsync(TestContext.CancellationToken).ConfigureAwait(false);
        var path = Path.Combine(fixture.Root.FullName, "retired-verifier.pem");
        var value = fixture.Verifier.ExportSubjectPublicKeyInfoPem();
        await File.WriteAllTextAsync(path, value, TestContext.CancellationToken).ConfigureAwait(false);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var transport = new StoppedTransport();
        var service = CreateService("https://account.blob.example.test/", transport);
        var publish = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => DistributedArchiveStore.PublishAsync(
            service, Container, ArchiveId, fixture.SealedDirectory, path, TestContext.CancellationToken)).ConfigureAwait(false);
        var fetch = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => DistributedArchiveStore.FetchAsync(
            service, Container, ArchiveId, fixture.Destination, path, TestContext.CancellationToken)).ConfigureAwait(false);
        Assert.AreEqual("Archive key-file inputs are retired; supply an owned snapshot verifier.", publish.Message, StringComparer.Ordinal);
        Assert.AreEqual(publish.Message, fetch.Message, StringComparer.Ordinal);
        var connection = Assert.ThrowsExactly<InvalidOperationException>(() => DistributedArchiveStore.ReadPrivateConnectionString(path));
        Assert.AreEqual("Separate connection-string files are retired; use the protected startup snapshot.", connection.Message, StringComparer.Ordinal);
        Assert.AreEqual(0, transport.Requests);
        Assert.IsFalse(Path.Exists(fixture.Destination));
        Assert.AreEqual(value, await File.ReadAllTextAsync(path, TestContext.CancellationToken).ConfigureAwait(false), StringComparer.Ordinal);
    }

    private static Task InvokeAsync(bool fetch, BlobServiceClient service, ArchiveFixture fixture, CancellationToken token) =>
        fetch
            ? DistributedArchiveStore.FetchWithVerifierAsync(service, Container, ArchiveId, fixture.Destination, fixture.Verifier, token)
            : DistributedArchiveStore.PublishWithVerifierAsync(service, Container, ArchiveId, fixture.SealedDirectory, fixture.Verifier, token);

    private static BlobServiceClient CreateService(string endpoint, StoppedTransport transport) =>
        new(new Uri(endpoint), new StorageSharedKeyCredential("account", "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA="),
            new BlobClientOptions { Transport = transport });

    private static void RequireLinux()
    {
        if (!OperatingSystem.IsLinux()) Assert.Inconclusive("The archive private-path contract requires Linux.");
    }

    // Reserved DNS/loopback URI shapes, synthetic keys and signed fixture bytes
    // test admission only. No endpoint/account/PKI/encryption or backup is adopted.
    private sealed class ArchiveFixture : IDisposable
    {
        private RSA? _verifier;
        public DirectoryInfo Root { get; } = Directory.CreateTempSubdirectory("mk8-archive-admission-");
        public string SealedDirectory => Path.Combine(Root.FullName, "sealed");
        public string Destination => Path.Combine(Root.FullName, "destination");
        public RSA Verifier => _verifier ?? throw new InvalidOperationException("The fixture verifier has not been prepared.");

        public async Task PrepareAsync(CancellationToken token)
        {
            if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Private archive fixtures require Linux.");
            File.SetUnixFileMode(Root.FullName, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Directory.CreateDirectory(SealedDirectory,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            using var signer = RSA.Create(3072);
            var cipher = "signed admission fixture; not an encrypted backup"u8.ToArray();
            var signature = signer.SignHash(SHA256.HashData(cipher), HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
            await WritePrivateAsync(Path.Combine("sealed", "snapshot.tar.age"), cipher, token).ConfigureAwait(false);
            await WritePrivateAsync(Path.Combine("sealed", "snapshot.tar.age.sig"), signature, token).ConfigureAwait(false);
            _verifier = RSA.Create();
            _verifier.ImportSubjectPublicKeyInfo(signer.ExportSubjectPublicKeyInfo(), out _);
        }

        private async Task WritePrivateAsync(string name, byte[] bytes, CancellationToken token)
        {
            if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Private archive fixtures require Linux.");
            var path = Path.Combine(Root.FullName, name);
            await File.WriteAllBytesAsync(path, bytes, token).ConfigureAwait(false);
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        public void Dispose()
        {
            try { _verifier?.Dispose(); }
            finally { Root.Delete(recursive: true); }
            GC.SuppressFinalize(this);
        }
    }

    private sealed class StoppedTransport : HttpPipelineTransport
    {
        public InvalidOperationException Fault { get; } = new("Controlled first archive request; no network is sent.");
        public int Requests { get; private set; }
        public string? FirstPath { get; private set; }
        public override Request CreateRequest() => HttpClientTransport.Shared.CreateRequest();

        public override void Process(HttpMessage message)
        {
            Requests++;
            FirstPath ??= message.Request.Uri.ToUri().AbsolutePath;
            throw Fault;
        }

        public override ValueTask ProcessAsync(HttpMessage message)
        {
            Requests++;
            FirstPath ??= message.Request.Uri.ToUri().AbsolutePath;
            return ValueTask.FromException(Fault);
        }
    }
}
