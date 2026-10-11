using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Azure.Storage.Blobs;

namespace mk8.email.Messaging.Tests;

internal sealed partial class NativeHostStartupTests
{
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task ProductionSubmissionRejectsWrongTlsIdentityBeforeCredentials(bool implicitTls, bool wrongName)
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
            directory = Directory.CreateTempSubdirectory("mk8-native-tls-identity-");
            container = new BlobServiceClient(blob).GetBlobContainerClient("mk8-native-tls-identity-" + Guid.NewGuid().ToString("N"));
            await RunHostsAsync(database, blob, container.Name, directory.FullName, unknownRecipient: false,
                hosts, scanners, injected: null, implicitTls,
                submissionControl: async (port, certificate, scanned, token) =>
                {
                    await RequireTlsIdentityRefusalAsync(port, certificate, implicitTls, wrongName, token).ConfigureAwait(false);
                    Assert.IsFalse(scanned.IsCompleted);
                    // A fresh admitted session on the SAME listener still reaches real Worker
                    // authentication, RSET/QUIT and the inherited zero-mail-effects checks.
                    await RunSubmissionSessionAsync(database.ConnectionString, port, certificate, implicitTls, token).ConfigureAwait(false);
                    Assert.IsFalse(scanned.IsCompleted);
                }).ConfigureAwait(false);
        }
        // Keep the actual observation while the inherited owner joins all child/read work.
#pragma warning disable CA1031
        catch (Exception exception)
#pragma warning restore CA1031
        { original = exception; }
        await RetireAsync(hosts, scanners, database, container, directory, original).ConfigureAwait(false);
        if (original is not null) ExceptionDispatchInfo.Capture(original).Throw();
        Assert.IsTrue(hosts.TrueForAll(host => host.Retired));
    }

    private static async Task RequireTlsIdentityRefusalAsync(int port, string certificatePath,
        bool implicitTls, bool wrongName, CancellationToken token)
    {
        using var client = new TcpClient(AddressFamily.InterNetwork);
        await client.ConnectAsync(IPAddress.Loopback, port, token).ConfigureAwait(false);
        if (!implicitTls) await RequireStartTlsAsync(client.GetStream(), token).ConfigureAwait(false);
        using var expected = X509CertificateLoader.LoadPkcs12FromFile(certificatePath, password: null);
        var expectedPin = expected.GetCertHashString(HashAlgorithmName.SHA256);
        // A deterministic different pin and reserved mismatching name are negative
        // fixture inputs, not public PKI, trust-store changes or deployment identities.
        var requiredPin = wrongName ? expectedPin : new string(expectedPin[0] == '0' ? '1' : '0', expectedPin.Length);
        var observedErrors = SslPolicyErrors.None;
        string? observedPin = null;
        var tls = new SslStream(client.GetStream(), leaveInnerStreamOpen: true, (_, certificate, _, errors) =>
        {
            observedErrors = errors;
            observedPin = certificate?.GetCertHashString(HashAlgorithmName.SHA256);
            return certificate is not null && errors is SslPolicyErrors.None or SslPolicyErrors.RemoteCertificateChainErrors
                && string.Equals(observedPin, requiredPin, StringComparison.Ordinal);
        });
        await using var tlsLifetime = tls.ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<AuthenticationException>(() => tls.AuthenticateAsClientAsync(
            new SslClientAuthenticationOptions { TargetHost = wrongName ? "wrong.example.test" : "email.example.test" }, token))
            .ConfigureAwait(false);
        Assert.IsFalse(tls.IsAuthenticated);
        Assert.AreEqual(expectedPin, observedPin, StringComparer.Ordinal);
        Assert.AreEqual(wrongName, observedErrors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch));
        Assert.AreEqual(wrongName, string.Equals(observedPin, requiredPin, StringComparison.Ordinal));
        // No application writer/credentials/envelope/DATA exists on this rejected stream.
    }
}
