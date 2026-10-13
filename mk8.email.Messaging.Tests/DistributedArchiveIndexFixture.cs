using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Azure.Core.Pipeline;
using Azure.Storage;
using Azure.Storage.Blobs;

namespace mk8.email.Messaging.Tests;

// Reserved URI, synthetic signer and finite in-memory HTTP responses only.
// Signed fixture bytes are NOT an encrypted backup or adopted endpoint/key.
internal sealed class DistributedArchiveIndexFixture : IDisposable
{
    internal const string Container = "owned-archives";
    internal const string ArchiveId = "index-fixture";
    private readonly ArchiveHandler _handler;
    private readonly HttpClient _client;
    private readonly byte[] _cipher = "owned signed index fixture, not an encrypted backup"u8.ToArray();
    private byte[] _signature = [];
    internal DirectoryInfo Root { get; } = Directory.CreateTempSubdirectory("mk8-archive-index-");
    internal string Destination => Path.Combine(Root.FullName, "destination");
    internal string Verifier => Path.Combine(Root.FullName, "verification.pem");
    internal BlobServiceClient Service { get; }
    internal List<string> Requests => _handler.Requests;
    internal bool ConditionsMatched => _handler.ConditionsMatched;

    internal DistributedArchiveIndexFixture()
    {
        _handler = new ArchiveHandler();
        _client = new HttpClient(_handler, disposeHandler: false);
        Service = new BlobServiceClient(new Uri("https://account.blob.example.test/"),
            new StorageSharedKeyCredential("account", "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA="),
            new BlobClientOptions { Transport = new HttpClientTransport(_client) });
    }

    internal async Task PrepareAsync(int headLength, int bodyLength, CancellationToken token)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Private archive fixtures require Linux.");
        File.SetUnixFileMode(Root.FullName, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        using var signer = RSA.Create(3072);
        _signature = signer.SignHash(SHA256.HashData(_cipher), HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        await File.WriteAllTextAsync(Verifier, signer.ExportSubjectPublicKeyInfoPem(), token).ConfigureAwait(false);
        File.SetUnixFileMode(Verifier, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var index = JsonSerializer.SerializeToUtf8Bytes(new
        {
            SchemaVersion = 1,
            ArchiveId,
            CipherSha256 = Hash(_cipher),
            CipherLength = _cipher.LongLength,
            SignatureSha256 = Hash(_signature),
            SignatureLength = _signature.LongLength,
            VerificationKeySha256 = Hash(signer.ExportSubjectPublicKeyInfo()),
        });
        var bytes = bodyLength == 0 ? index : Pad(index, bodyLength);
        _handler.Objects.Add("complete.json", new ObjectBytes(bytes, headLength == 0 ? bytes.Length : headLength));
        _handler.Objects.Add("snapshot.tar.age", new ObjectBytes(_cipher, _cipher.Length));
        _handler.Objects.Add("snapshot.tar.age.sig", new ObjectBytes(_signature, _signature.Length));
    }

    internal async Task AssertFetchedAsync(CancellationToken token)
    {
        Assert.AreSequenceEqual(_cipher, await File.ReadAllBytesAsync(Path.Combine(Destination, "snapshot.tar.age"), token).ConfigureAwait(false));
        Assert.AreSequenceEqual(_signature, await File.ReadAllBytesAsync(Path.Combine(Destination, "snapshot.tar.age.sig"), token).ConfigureAwait(false));
        Assert.IsTrue(ConditionsMatched);
    }

    private static byte[] Pad(byte[] index, int length)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(length, index.Length);
        var result = new byte[length];
        index.CopyTo(result, 0);
        result.AsSpan(index.Length).Fill((byte)' ');
        return result;
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    public void Dispose()
    {
        try
        {
            _client.Dispose();
        }
        finally
        {
            _handler.Dispose();
        }
        Root.Delete(recursive: true);
        GC.SuppressFinalize(this);
    }

    private sealed record ObjectBytes(byte[] Bytes, int HeadLength);

    private sealed class ArchiveHandler : HttpMessageHandler
    {
        private readonly List<HttpResponseMessage> _responses = [];
        internal Dictionary<string, ObjectBytes> Objects { get; } = new(StringComparer.Ordinal);
        internal List<string> Requests { get; } = [];
        internal bool ConditionsMatched { get; private set; } = true;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = Path.GetFileName(request.RequestUri!.AbsolutePath);
            Requests.Add(request.Method.Method + " " + name);
            if (!Objects.TryGetValue(name, out var stored)) throw new InvalidOperationException("Unexpected controlled archive object.");
            var head = request.Method == HttpMethod.Head;
            if (!head && request.Method != HttpMethod.Get) throw new InvalidOperationException("Unexpected controlled archive method.");
            var tag = new EntityTagHeaderValue('"' + name + '"');
            if (!head) ConditionsMatched &= request.Headers.IfMatch.Contains(tag);
            var ranged = !head && request.Headers.Range is not null;
            var response = new HttpResponseMessage(ranged ? HttpStatusCode.PartialContent : HttpStatusCode.OK)
            { Content = new ByteArrayContent(head ? [] : stored.Bytes) };
            _responses.Add(response);
            response.Headers.ETag = tag;
            response.Headers.Add("x-ms-blob-type", "BlockBlob");
            response.Content.Headers.ContentLength = head ? stored.HeadLength : stored.Bytes.Length;
            response.Content.Headers.LastModified = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
            if (ranged) response.Content.Headers.ContentRange = new ContentRangeHeaderValue(0, stored.Bytes.Length - 1, stored.Bytes.Length);
            return Task.FromResult(response);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                foreach (var response in _responses) response.Dispose();
            base.Dispose(disposing);
        }
    }
}
