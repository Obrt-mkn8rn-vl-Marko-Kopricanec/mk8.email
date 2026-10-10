using System.Runtime.ExceptionServices;
using Azure.Storage.Blobs;
using Microsoft.EntityFrameworkCore;

namespace mk8.email.Messaging.Tests;

internal sealed partial class NativeHostStartupTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task ProductionHostsJoinGracefulShutdownWithAnOpenNativeSession(bool cancelFirstWait)
    {
        Assert.IsNotNull(TestContext);
        if (!OperatingSystem.IsLinux()) throw new AssertInconclusiveException("The explicit owned pidfd fixture requires Linux.");
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
            directory = Directory.CreateTempSubdirectory("mk8-native-stop-");
            container = new BlobServiceClient(blob).GetBlobContainerClient("mk8-native-stop-" + Guid.NewGuid().ToString("N"));
            await RunHostsAsync(database, blob, container.Name, directory.FullName, unknownRecipient: false, hosts, scanners,
                injected: null, shutdownControl: (worker, gateway, port, token) =>
                    RequireGracefulShutdownAsync(worker, gateway, port, cancelFirstWait, token)).ConfigureAwait(false);
            var context = OpenContext(database.ConnectionString);
            await using var contextLifetime = context.ConfigureAwait(false);
            Assert.AreEqual(0, await context.MailQueueMessages.CountAsync(TestContext.CancellationToken).ConfigureAwait(false));
            Assert.AreEqual(0, await context.Emails.CountAsync(TestContext.CancellationToken).ConfigureAwait(false));
        }
        // Keep the observation and independently join actual shutdown/exit/read work.
#pragma warning disable CA1031
        catch (Exception exception)
#pragma warning restore CA1031
        { original = exception; }
        await RetireAsync(hosts, scanners, database, container, directory, original).ConfigureAwait(false);
        if (original is not null) ExceptionDispatchInfo.Capture(original).Throw();
        Assert.IsTrue(hosts.TrueForAll(host => host.Retired));
        foreach (var host in hosts)
            await Assert.ThrowsAsync<ObjectDisposedException>(() => host.RequestGracefulStopAsync(CancellationToken.None)).ConfigureAwait(false);
    }

    private static async Task RequireGracefulShutdownAsync(NativeHostProcess worker, NativeHostProcess gateway,
        int port, bool cancelFirstWait, CancellationToken token)
    {
        var client = await NativeSmtpClient.ConnectAsync(port, token).ConfigureAwait(false);
        await using var clientLifetime = client.ConfigureAwait(false);
        await client.GreetAsync(token).ConfigureAwait(false);
        AssertHostsRunning(worker, gateway);
        if (cancelFirstWait)
        {
            using var cancelled = new CancellationTokenSource();
            await cancelled.CancelAsync().ConfigureAwait(false);
            await Assert.ThrowsAsync<OperationCanceledException>(() => gateway.RequestGracefulStopAsync(cancelled.Token)).ConfigureAwait(false);
        }
        await Task.WhenAll(gateway.RequestGracefulStopAsync(token), gateway.RequestGracefulStopAsync(token)).ConfigureAwait(false);
        Assert.IsTrue(gateway.GracefulCompleting.IsCompletedSuccessfully);
        Assert.IsTrue(gateway.HasExited);
        // An idle native connection must close; no QUIT or abrupt fixture kill
        // substitutes for the actual hosted session's shutdown settlement.
        Assert.IsNull(await client.ReadAsync(token).ConfigureAwait(false));
        await Task.WhenAll(worker.RequestGracefulStopAsync(token), worker.RequestGracefulStopAsync(token)).ConfigureAwait(false);
        Assert.IsTrue(worker.GracefulCompleting.IsCompletedSuccessfully);
        Assert.IsTrue(worker.HasExited);
    }
}
