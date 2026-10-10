using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Runtime.ExceptionServices;
using System.Text;
using Azure.Storage.Blobs;
using Microsoft.EntityFrameworkCore;
using mk8.email.MailWire;
using mk8.email.Utils;

namespace mk8.email.Messaging.Tests;

internal sealed partial class NativeHostStartupTests
{
    private const string SubmissionPassword = "owned-submission-fixture-only";

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task ProductionSubmissionRequiresVerifiedTlsAndWorkerAuthentication(bool implicitTls)
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
            directory = Directory.CreateTempSubdirectory("mk8-native-submission-");
            container = new BlobServiceClient(blob).GetBlobContainerClient("mk8-native-submission-" + Guid.NewGuid().ToString("N"));
            await RunHostsAsync(database, blob, container.Name, directory.FullName, unknownRecipient: false, hosts, scanners, injected: null, implicitTls).ConfigureAwait(false);
        }
        // Retain the primary observation while the shared owner independently joins every child.
#pragma warning disable CA1031
        catch (Exception exception)
#pragma warning restore CA1031
        { original = exception; }
        await RetireAsync(hosts, scanners, database, container, directory, original).ConfigureAwait(false);
        if (original is not null) ExceptionDispatchInfo.Capture(original).Throw();
        Assert.IsTrue(hosts.TrueForAll(host => host.Retired));
    }

    private static string CreateSubmissionCertificate(string directory)
    {
        // A finite reserved DNS identity and an owned self-signed certificate are not public PKI inputs.
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=email.example.test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("email.example.test");
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(certificateAuthority: false, hasPathLengthConstraint: false, pathLengthConstraint: 0, critical: false));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        var path = Path.Combine(directory, "submission.pfx");
        File.WriteAllBytes(path, certificate.Export(X509ContentType.Pkcs12));
        return path;
    }

    private static async Task SetSubmissionPasswordAsync(string connection, CancellationToken token)
    {
        var context = OpenContext(connection);
        await using var lifetime = context.ConfigureAwait(false);
        var user = await context.Users.SingleAsync(token).ConfigureAwait(false);
        user.PasswordHash = PasswordHasher.Hash(SubmissionPassword);
        await context.SaveChangesAsync(token).ConfigureAwait(false);
    }

    private static async Task RunSubmissionSessionAsync(string connection, int port, string certificatePath, bool implicitTls, CancellationToken token)
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
        await RequireReplyAsync(reader, writer, "MAIL FROM:<receiver@example.test>", "530 ", token).ConfigureAwait(false);
        await RequireReplyAsync(reader, writer, "RCPT TO:<receiver@example.test>", "503 ", token).ConfigureAwait(false);
        await RequireReplyAsync(reader, writer, "DATA", "503 ", token).ConfigureAwait(false);
        var bad = Convert.ToBase64String(Encoding.UTF8.GetBytes("\0receiver@example.test\0wrong-fixture-password"));
        await RequireReplyAsync(reader, writer, "AUTH PLAIN " + bad, "535 ", token).ConfigureAwait(false);
        var valid = Convert.ToBase64String(Encoding.UTF8.GetBytes("\0receiver@example.test\0" + SubmissionPassword));
        await RequireReplyAsync(reader, writer, "AUTH PLAIN " + valid, "235 ", token).ConfigureAwait(false);
        await RequireReplyAsync(reader, writer, "MAIL FROM:<sender@remote.test>", "553 ", token).ConfigureAwait(false);
        await RequireReplyAsync(reader, writer, "MAIL FROM:<receiver@example.test>", "250 ", token).ConfigureAwait(false);
        await RequireReplyAsync(reader, writer, "RSET", "250 ", token).ConfigureAwait(false);
        await RequireReplyAsync(reader, writer, "QUIT", "221 ", token).ConfigureAwait(false);
        var context = OpenContext(connection);
        await using var contextLifetime = context.ConfigureAwait(false);
        Assert.AreEqual(0, await context.MailQueueMessages.CountAsync(token).ConfigureAwait(false));
        Assert.AreEqual(0, await context.Emails.CountAsync(token).ConfigureAwait(false));
    }

    private static async Task RequireStartTlsAsync(Stream stream, CancellationToken token)
    {
        using var reader = new StreamReader(stream, MailWireEncoding.Instance, detectEncodingFromByteOrderMarks: false, 4096, leaveOpen: true);
        var writer = new StreamWriter(stream, MailWireEncoding.Instance, 4096, leaveOpen: true) { AutoFlush = true, NewLine = "\r\n" };
        await using var lifetime = writer.ConfigureAwait(false);
        Assert.StartsWith("220 ", await reader.ReadLineAsync(token).ConfigureAwait(false), StringComparison.Ordinal);
        await RequireGreetingAsync(reader, writer, token).ConfigureAwait(false);
        await RequireReplyAsync(reader, writer, "AUTH PLAIN", "538 ", token).ConfigureAwait(false);
        await RequireReplyAsync(reader, writer, "MAIL FROM:<receiver@example.test>", "530 ", token).ConfigureAwait(false);
        await RequireReplyAsync(reader, writer, "STARTTLS", "220 ", token).ConfigureAwait(false);
    }

    private static async Task RequireGreetingAsync(StreamReader reader, StreamWriter writer, CancellationToken token)
    {
        await writer.WriteLineAsync("EHLO submission.example.test".AsMemory(), token).ConfigureAwait(false);
        string? line;
        do
        {
            line = await reader.ReadLineAsync(token).ConfigureAwait(false);
            Assert.IsNotNull(line);
            Assert.StartsWith("250", line, StringComparison.Ordinal);
        } while (line.StartsWith("250-", StringComparison.Ordinal));
    }

    private static async Task RequireReplyAsync(StreamReader reader, StreamWriter writer, string command, string expected, CancellationToken token)
    {
        await writer.WriteLineAsync(command.AsMemory(), token).ConfigureAwait(false);
        Assert.StartsWith(expected, await reader.ReadLineAsync(token).ConfigureAwait(false), StringComparison.Ordinal);
    }
}
