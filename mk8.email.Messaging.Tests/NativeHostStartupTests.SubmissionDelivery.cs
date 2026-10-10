using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Azure.Storage.Blobs;
using Microsoft.EntityFrameworkCore;
using mk8.email.Contracts.Enums;
using mk8.email.Contracts.Storage;
using mk8.email.Infrastructure.Models;
using mk8.email.MailWire;
using mk8.email.Storage;

namespace mk8.email.Messaging.Tests;

internal sealed partial class NativeHostStartupTests
{
    private const string SubmissionRaw = "From: receiver@example.test\r\nTo: receiver@example.test\r\nSubject: owned authenticated local delivery\r\n\r\noriginal submission body\r\n";
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task ProductionTlsSubmissionPersistsSentAndLocalInboxCopies(bool implicitTls)
    {
        Assert.IsNotNull(TestContext);
        var blob = Environment.GetEnvironmentVariable("MK8_EMAIL_TEST_AZURE_BLOB_CONNECTION");
        if (string.IsNullOrWhiteSpace(blob)) throw new AssertInconclusiveException("An owned Blob development endpoint is required.");
        var database = await PostgresTestDatabase.TryCreateAsync().ConfigureAwait(false)
            ?? throw new AssertInconclusiveException("An owned PostgreSQL development endpoint is required.");
        DirectoryInfo? directory = null;
        BlobContainerClient? container = null;
        var hosts = new List<NativeHostProcess>();
        var scanners = new List<NativeHostScanner>();
        Exception? original = null;
        try
        {
            directory = Directory.CreateTempSubdirectory("mk8-native-submission-data-");
            container = new BlobServiceClient(blob).GetBlobContainerClient("mk8-native-submission-data-" + Guid.NewGuid().ToString("N"));
            await RunHostsAsync(database, blob, container.Name, directory.FullName, unknownRecipient: false, hosts, scanners, injected: null,
                implicitTls, submissionControl: async (port, certificate, scanned, token) =>
                {
                    await AddSentFolderAfterSeedAsync(database.ConnectionString, token).ConfigureAwait(false);
                    var queueId = await SubmitLocalDataAsync(port, certificate, implicitTls, token).ConfigureAwait(false);
                    var raw = await scanned.WaitAsync(token).ConfigureAwait(false);
                    Assert.EndsWith(SubmissionRaw, raw, StringComparison.Ordinal);
                    await AssertSubmissionDeliveredAsync(database.ConnectionString, blob, container.Name, queueId, raw, token).ConfigureAwait(false);
                }).ConfigureAwait(false);
        }
        // Preserve the primary failure while the shared owner joins every actual host and read.
#pragma warning disable CA1031
        catch (Exception exception)
#pragma warning restore CA1031
        { original = exception; }
        await RetireAsync(hosts, scanners, database, container, directory, original).ConfigureAwait(false);
        if (original is not null) ExceptionDispatchInfo.Capture(original).Throw();
        Assert.IsTrue(hosts.TrueForAll(host => host.Retired));
    }

    private static async Task AddSentFolderAfterSeedAsync(string connection, CancellationToken token)
    {
        var context = OpenContext(connection);
        await using var lifetime = context.ConfigureAwait(false);
        var inbox = await context.Inboxes.SingleAsync(token).ConfigureAwait(false);
        await context.Folders.AddAsync(new FolderDB { Id = Guid.CreateVersion7(), InboxId = inbox.Id, Name = DefaultFolders.Sent, JmapRole = "sent" }, token).ConfigureAwait(false);
        await context.SaveChangesAsync(token).ConfigureAwait(false);
        // No redirect/vacation policy or nonlocal recipient exists in this isolated database.
        Assert.AreEqual(0, await context.SieveScripts.CountAsync(token).ConfigureAwait(false));
        Assert.AreEqual(0, await context.JmapVacationResponses.CountAsync(token).ConfigureAwait(false));
    }

    private static async Task<Guid> SubmitLocalDataAsync(int port, string certificatePath, bool implicitTls, CancellationToken token)
    {
        using var client = new TcpClient(AddressFamily.InterNetwork);
        await client.ConnectAsync(IPAddress.Loopback, port, token).ConfigureAwait(false);
        if (!implicitTls) await RequireStartTlsAsync(client.GetStream(), token).ConfigureAwait(false);
        using var expected = X509CertificateLoader.LoadPkcs12FromFile(certificatePath, password: null);
        var pin = expected.GetCertHashString(HashAlgorithmName.SHA256);
        var tls = new SslStream(client.GetStream(), leaveInnerStreamOpen: true, (_, certificate, _, errors) =>
            certificate is not null && errors is SslPolicyErrors.None or SslPolicyErrors.RemoteCertificateChainErrors
            && string.Equals(certificate.GetCertHashString(HashAlgorithmName.SHA256), pin, StringComparison.Ordinal));
        await using var tlsLifetime = tls.ConfigureAwait(false);
        await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "email.example.test" }, token).ConfigureAwait(false);
        using var reader = new StreamReader(tls, MailWireEncoding.Instance, detectEncodingFromByteOrderMarks: false, 4096, leaveOpen: true);
        var writer = new StreamWriter(tls, MailWireEncoding.Instance, 4096, leaveOpen: true) { AutoFlush = true, NewLine = "\r\n" };
        await using var writerLifetime = writer.ConfigureAwait(false);
        if (implicitTls) Assert.StartsWith("220 ", await reader.ReadLineAsync(token).ConfigureAwait(false), StringComparison.Ordinal);
        await RequireGreetingAsync(reader, writer, token).ConfigureAwait(false);
        var valid = Convert.ToBase64String(Encoding.UTF8.GetBytes("\0receiver@example.test\0" + SubmissionPassword));
        await RequireReplyAsync(reader, writer, "AUTH PLAIN " + valid, "235 ", token).ConfigureAwait(false);
        await RequireReplyAsync(reader, writer, "MAIL FROM:<receiver@example.test>", "250 ", token).ConfigureAwait(false);
        await RequireReplyAsync(reader, writer, "RCPT TO:<outside@remote.test>", "550 ", token).ConfigureAwait(false);
        await RequireReplyAsync(reader, writer, "RCPT TO:<receiver@example.test> NOTIFY=NEVER", "250 ", token).ConfigureAwait(false);
        await RequireReplyAsync(reader, writer, "DATA", "354 ", token).ConfigureAwait(false);
        await writer.WriteAsync((SubmissionRaw + ".\r\n").AsMemory(), token).ConfigureAwait(false);
        var acknowledgement = await reader.ReadLineAsync(token).ConfigureAwait(false);
        Assert.IsNotNull(acknowledgement);
        const string prefix = "250 2.0.0 Queued as ";
        Assert.StartsWith(prefix, acknowledgement, StringComparison.Ordinal);
        var queueId = Guid.ParseExact(acknowledgement[prefix.Length..], "N");
        Assert.AreNotEqual(Guid.Empty, queueId);
        await RequireReplyAsync(reader, writer, "QUIT", "221 ", token).ConfigureAwait(false);
        return queueId;
    }

    private static async Task AssertSubmissionDeliveredAsync(string connection, string blob, string container, Guid queueId, string scanned, CancellationToken token)
    {
        while (true)
        {
            var context = OpenContext(connection);
            await using var lifetime = context.ConfigureAwait(false);
            var queue = await context.MailQueueMessages.AsNoTracking().Include(item => item.Recipients).SingleAsync(token).ConfigureAwait(false);
            if (string.Equals(queue.State, MailQueueStates.Completed, StringComparison.Ordinal))
            {
                Assert.AreEqual(MailQueueDirections.Submission, queue.Direction, StringComparer.Ordinal);
                Assert.AreEqual(queueId, queue.Id);
                Assert.IsNotNull(queue.AddedHeaders);
                Assert.Contains("DKIM-Signature: " + NativeHostScanner.SyntheticSubmissionSignature + "\r\n", queue.AddedHeaders, StringComparison.Ordinal);
                Assert.AreEqual("receiver@example.test", queue.AuthenticatedUser, StringComparer.Ordinal);
                Assert.IsTrue(queue.SentCopyCreated);
                var recipient = queue.Recipients.Single();
                Assert.IsTrue(recipient.IsLocal);
                Assert.AreEqual("NEVER", recipient.DsnNotify, StringComparer.Ordinal);
                Assert.AreEqual("receiver@example.test", recipient.Recipient, StringComparer.Ordinal);
                Assert.AreEqual(MailQueueRecipientStates.Delivered, recipient.State, StringComparer.Ordinal);
                var emails = await context.Emails.AsNoTracking().Include(item => item.Folder).ToListAsync(token).ConfigureAwait(false);
                Assert.HasCount(2, emails);
                var sent = emails.Single(item => string.Equals(item.Folder.Name, DefaultFolders.Sent, StringComparison.Ordinal));
                var inbox = emails.Single(item => string.Equals(item.Folder.Name, DefaultFolders.Inbox, StringComparison.Ordinal));
                Assert.AreEqual(queue.Id, sent.QueueDeliveryId);
                Assert.AreEqual(recipient.Id, inbox.QueueDeliveryId);
                Assert.IsTrue(sent.IsRead);
                Assert.IsFalse(inbox.IsRead);
                var store = AzureBlobLargeObjectStore.FromConnectionString(blob, new AzureBlobLargeObjectStoreOptions { ContainerName = container });
                await AssertStoredBytesAsync(store, new LargeObjectReference(LargeObjectProviders.AzureBlob, queue.RawMessageObjectName!, queue.RawMessageSizeBytes,
                    queue.RawMessageObjectSha256!, queue.RawMessageObjectEntityTag!), scanned, token).ConfigureAwait(false);
                foreach (var email in emails)
                {
                    Assert.AreEqual(1, email.Uid);
                    Assert.IsGreaterThan(0L, email.ModSeq);
                    await AssertStoredBytesAsync(store, new LargeObjectReference(email.RawMessageObjectProvider!, email.RawMessageObjectName!, email.SizeBytes,
                        email.RawMessageObjectSha256!, email.RawMessageObjectEntityTag!), queue.AddedHeaders + scanned, token).ConfigureAwait(false);
                }
                return;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(100), token).ConfigureAwait(false);
        }
    }

    private static async Task AssertStoredBytesAsync(AzureBlobLargeObjectStore store, LargeObjectReference reference, string expected, CancellationToken token)
    {
        var bytes = new MemoryStream();
        await using var lifetime = bytes.ConfigureAwait(false);
        await store.CopyToAsync(reference, bytes, token).ConfigureAwait(false);
        Assert.AreSequenceEqual(MailWireEncoding.Instance.GetBytes(expected), bytes.ToArray());
    }
}
