using System.Runtime.ExceptionServices;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using mk8.email.Application.Interfaces;
using Microsoft.Extensions.Hosting;

namespace mk8.email.Application.Tests;

internal sealed partial class TransportSecurityTests
{
    [TestMethod]
    [Timeout(15_000, CooperativeCancellation = true)]
    public async Task ControlledPop3EofOracleJoinsActualSessionBeforeForbiddenEffects()
    {
        // Distinct finite authentication input isolates the EOF oracle; it is
        // NOT genuine password/MFA verification or repair of the failed old row.
        Assert.IsNotNull(TestContext);
        var authentication = new EofFixtureAuthenticator();
        var port = ReservePort();
        var server = await ServerFixture.StartPop3Async(CreateEnvironment(pop3ImplicitTlsPort: port), port,
            authenticationDecorator: _ => authentication).ConfigureAwait(false);
        await using var serverLifetime = server.ConfigureAwait(false);
        authentication.User = await server.GetOwnedFixtureUserAsync(TestContext.CancellationToken).ConfigureAwait(false);
        await server.SeedPop3MessagesAsync().ConfigureAwait(false);
        var expectedReply = await server.GetOwnedPop3MaildropReplyAsync(TestContext.CancellationToken).ConfigureAwait(false);
        {
            var connection = await ProtocolConnection.ConnectAsync(port).ConfigureAwait(false);
            await using var connectionLifetime = connection.ConfigureAwait(false);
            await connection.UpgradeToTlsAsync("email.tenant.example.test").ConfigureAwait(false);
            Assert.StartsWith("+OK ", await connection.ReadLineAsync().ConfigureAwait(false), StringComparison.Ordinal);
            var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"\0{TestUsername}\0{TestPassword}"));
            await connection.WriteLineAsync($"AUTH PLAIN {credentials}").ConfigureAwait(false);
            Assert.AreEqual(expectedReply, await connection.ReadLineAsync().ConfigureAwait(false), StringComparer.Ordinal);
            await connection.WriteLineAsync("DELE 2").ConfigureAwait(false);
            Assert.StartsWith("+OK ", await connection.ReadLineAsync().ConfigureAwait(false), StringComparison.Ordinal);
        }
        await server.JoinPop3EofSessionsAsync(TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(1, authentication.Calls);
        Assert.AreEqual(2, await server.CountStoredEmailsAsync().ConfigureAwait(false));
        Assert.AreEqual(0, await server.CountExpungedUidsAsync().ConfigureAwait(false));
    }

    private sealed class EofFixtureAuthenticator : IMailAuthenticator
    {
        internal AuthenticatedMailUser? User { get; set; }
        internal int Calls { get; private set; }
        public Task<AuthenticatedMailUser?> AuthenticateAsync(string username, string password,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            if (!string.Equals(username, TestUsername, StringComparison.Ordinal)
                || !string.Equals(password, TestPassword, StringComparison.Ordinal))
            {
                return Task.FromResult<AuthenticatedMailUser?>(null);
            }
            return Task.FromResult<AuthenticatedMailUser?>(User
                ?? throw new InvalidOperationException("The explicit owned fixture identity is not ready."));
        }
        public Task<AuthenticatedMailUser?> AuthenticatePrimaryAsync(string username, string password,
            CancellationToken cancellationToken = default) => AuthenticateAsync(username, password, cancellationToken);
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    [Timeout(10_000, CooperativeCancellation = true)]
    public async Task FixtureObservationTimeoutRetainsActualStopAndAllRetirementFaults(bool stopFault, bool disposalFault)
    {
        Assert.IsNotNull(TestContext);
        var host = new ControlledStopHost();
        var original = new AssertFailedException("Controlled initiating fixture observation.");
        var stopError = new IOException("Controlled late actual stop fault.");
        var disposalError = new InvalidOperationException("Controlled first-owner disposal fault.");
        var retiredHost = false;
        var retiredDependencies = false;
        var completing = RetireHostedServiceAsync(host,
            () =>
            {
                retiredHost = true;
                return disposalFault ? ValueTask.FromException(disposalError) : ValueTask.CompletedTask;
            },
            () => { retiredDependencies = true; return ValueTask.CompletedTask; },
            () => throw original, TimeSpan.Zero);
        try
        {
            await host.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.CancellationToken).ConfigureAwait(false);
            Assert.IsFalse(completing.IsCompleted);
            Assert.IsFalse(retiredHost);
            Assert.IsFalse(retiredDependencies);
        }
        finally
        {
            if (stopFault) host.Body.TrySetException(stopError);
            else host.Body.TrySetResult();
            await completing.WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None)
                .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            Assert.IsTrue(completing.IsCompleted, "Pending actual work forbids borrowed retirement.");
        }
        var outcome = completing.Exception;
        Assert.IsNotNull(outcome);
        var errors = outcome.Flatten().InnerExceptions;
        Assert.Contains(original, errors);
        Assert.HasCount(1, errors.OfType<TimeoutException>().ToArray());
        if (stopFault) Assert.Contains(stopError, errors);
        if (disposalFault) Assert.Contains(disposalError, errors);
        Assert.HasCount(2 + (stopFault ? 1 : 0) + (disposalFault ? 1 : 0), errors);
        Assert.IsTrue(retiredHost && retiredDependencies);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ProtocolAcquisitionFailureDisposesActualTcpClient(bool constructionFailure)
    {
        Socket? acquired = null;
        var expected = new IOException("Controlled connect fault.");
        // Both injected branches refuse BEFORE ownership can be transferred:
        // the actual TcpClient is independently closed in ConnectAsync's finally.
#pragma warning disable CA2000
        var connecting = ProtocolConnection.ConnectAsync(0, (client, _, _) =>
        {
            acquired = client.Client;
            return constructionFailure ? Task.CompletedTask : Task.FromException(expected);
        });
#pragma warning restore CA2000
        // This method starts and observes the actual acquisition task above.
#pragma warning disable VSTHRD003
        if (constructionFailure)
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => connecting).ConfigureAwait(false);
        else
            Assert.AreSame(expected, await Assert.ThrowsExactlyAsync<IOException>(() => connecting).ConfigureAwait(false));
#pragma warning restore VSTHRD003
        Assert.IsNotNull(acquired);
        Assert.IsTrue(acquired.SafeHandle.IsClosed);
    }

    [TestMethod]
    [Timeout(10_000, CooperativeCancellation = true)]
    public async Task ProtocolAuthenticationFailureDisposesUntransferredActualSslStream()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var connection = await ProtocolConnection.ConnectAsync(((IPEndPoint)listener.LocalEndpoint).Port).ConfigureAwait(false);
        await using var connectionLifetime = connection.ConfigureAwait(false);
        using var accepted = await listener.AcceptTcpClientAsync(CancellationToken.None).ConfigureAwait(false);
        SslStream? candidate = null;
        var original = new AuthenticationException("Controlled candidate authentication fault before transfer.");
        var observed = await Assert.ThrowsExactlyAsync<AuthenticationException>(() => connection.UpgradeToTlsAsync(
            "mail.example.invalid", (stream, _, _) =>
            {
                candidate = stream;
                return Task.FromException(original);
            })).ConfigureAwait(false);
        Assert.AreSame(original, observed);
        Assert.IsNotNull(candidate);
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => candidate.ReadAsync(new byte[1], CancellationToken.None)
            .AsTask()).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task IndependentOwnerCleanupAttemptsEveryOwnerAndRetainsOriginalAndBothFaults()
    {
        var original = new AssertFailedException("Controlled original assertion.");
        var first = new IOException("Controlled reader cleanup fault.");
        var second = new InvalidOperationException("Controlled writer cleanup fault.");
        var attempts = new List<int>();
        var actual = await Assert.ThrowsExactlyAsync<AggregateException>(() => DisposeEveryOwnerAsync([
            () => { attempts.Add(1); return ValueTask.FromException(first); },
            () => { attempts.Add(2); return ValueTask.FromException(second); },
            () => { attempts.Add(3); return ValueTask.CompletedTask; },
        ], original)).ConfigureAwait(false);
        Assert.AreSequenceEqual<int>([1, 2, 3], attempts);
        Assert.AreSequenceEqual<Exception>([original, first, second], actual.InnerExceptions);
    }

    private sealed class ControlledStopHost : IHostedService
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Body { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken)
        {
            Assert.AreEqual(CancellationToken.None, cancellationToken);
            Entered.TrySetResult();
            // The outer control unconditionally releases and joins this actual shared body.
#pragma warning disable VSTHRD003
            return Body.Task;
#pragma warning restore VSTHRD003
        }
    }

    private static async Task RetireHostedServiceAsync(IHostedService host,
        Func<ValueTask> retireHost, Func<ValueTask> retireDependencies, Action diagnostic,
        TimeSpan? observationLimit = null)
    {
        var failures = new List<Exception>();
        try
        {
            diagnostic();
        }
        catch (Exception error) when (!IsFatalAuthenticationControl(error))
        {
            failures.Add(error);
        }
        finally
        {
            var stopping = StopOwnedHostAsync(host);
            try
            {
                await stopping.WaitAsync(observationLimit ?? TimeSpan.FromSeconds(5), CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception observation) when (!IsFatalAuthenticationControl(observation))
            {
                failures.Add(observation);
            }
            // A timed-out OBSERVATION is still failed verification. Independently
            // join the actual stop before disposing either borrowed owner.
            await stopping.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            AddTaskFailures(stopping, failures);
            await AttemptCleanupAsync(retireHost, failures).ConfigureAwait(false);
            await AttemptCleanupAsync(retireDependencies, failures).ConfigureAwait(false);
        }
        ThrowCleanupFailures(failures);
    }

    private static async Task StopOwnedHostAsync(IHostedService host) =>
        await host.StopAsync(CancellationToken.None).ConfigureAwait(false);

    private static async Task DisposeEveryOwnerAsync(IEnumerable<Func<ValueTask>> cleanup, Exception? original = null)
    {
        var failures = new List<Exception>();
        if (original is not null) failures.Add(original);
        foreach (var release in cleanup)
            await AttemptCleanupAsync(release, failures).ConfigureAwait(false);
        ThrowCleanupFailures(failures);
    }

    private static async Task AttemptCleanupAsync(Func<ValueTask> release, List<Exception> failures)
    {
        Task? completing = null;
        try
        {
            completing = release().AsTask();
            await completing.ConfigureAwait(false);
        }
        catch (Exception failure) when (!IsFatalAuthenticationControl(failure))
        {
            if (completing?.Exception is { } inventory)
                failures.AddRange(inventory.Flatten().InnerExceptions);
            else failures.Add(failure);
        }
    }

    private static void AddTaskFailures(Task completing, List<Exception> failures)
    {
        if (completing.Exception is { } inventory)
            failures.AddRange(inventory.Flatten().InnerExceptions);
    }

    private static void DisposeEveryOwner(IEnumerable<Action> cleanup)
    {
        var failures = new List<Exception>();
        foreach (var release in cleanup)
        {
            try { release(); }
            catch (Exception error) when (!IsFatalAuthenticationControl(error)) { failures.Add(error); }
        }
        ThrowCleanupFailures(failures);
    }

    private static void ThrowCleanupFailures(List<Exception> failures)
    {
        var distinct = failures.Distinct<Exception>(ReferenceEqualityComparer.Instance).ToArray();
        if (distinct.Length == 1) ExceptionDispatchInfo.Capture(distinct[0]).Throw();
        if (distinct.Length > 1) throw new AggregateException(distinct);
    }
}
