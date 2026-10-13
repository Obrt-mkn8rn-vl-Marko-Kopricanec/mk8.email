using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using mk8.email.Configuration;
using mk8.email.Contracts.Messaging;
using mk8.email.Gateway.Protocols.Pop3;
using mk8.email.Imap.Presentation;

namespace mk8.email.Messaging.Tests;

[TestClass]
[DoNotParallelize]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1812", Justification = "MSTest executes these owned native listener/session controls by reflection.")]
internal sealed class NativeListenerOwnershipTests
{
    public TestContext? TestContext { get; set; }
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task NativeCompletionJoinsTheAcceptedSessionOnStopOrSiblingBindFault(bool pop3, bool bindFault)
    {
        Assert.IsNotNull(TestContext);
        using var occupied = new TcpListener(IPAddress.Any, 0);
        occupied.Start();
        var blockedPort = ((IPEndPoint)occupied.LocalEndpoint).Port;
        var port = SmtpListenerFixture.ReservePort();
        var config = Configuration(port, blockedPort, bindFault);
        var services = new ServiceCollection().BuildServiceProvider();
        await using var serviceLifetime = services.ConfigureAwait(false);
        using var signals = new StartSignals();
        var journal = new HeldGreetingJournal();
        using BackgroundService server = pop3
            ? new Pop3ServerService(services.GetRequiredService<IServiceScopeFactory>(), config,
                new ListenerLogger<Pop3ServerService>(signals), new UnusedLeaseStore(), journal)
            : new ImapServerService(services.GetRequiredService<IServiceScopeFactory>(), config,
                new ListenerLogger<ImapServerService>(signals), journal);
        using var client = new TcpClient();
        Task? completing = null;
        Task? stopping = null;
        try
        {
            await server.StartAsync(CancellationToken.None).ConfigureAwait(false);
            completing = server.ExecuteTask ?? throw new InvalidOperationException("Missing actual hosted execution.");
            await signals.Started.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.CancellationToken).ConfigureAwait(false);
            await client.ConnectAsync(IPAddress.Loopback, port, CancellationToken.None).ConfigureAwait(false);
            signals.Release.Set();
            await journal.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.CancellationToken).ConfigureAwait(false);
            if (!bindFault)
            {
                using var canceledCaller = new CancellationTokenSource();
                await canceledCaller.CancelAsync().ConfigureAwait(false);
                await Assert.ThrowsAsync<OperationCanceledException>(() => server.StopAsync(canceledCaller.Token)).ConfigureAwait(false);
            }
            await journal.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.CancellationToken).ConfigureAwait(false);
            stopping = server.StopAsync(CancellationToken.None);
            Assert.IsFalse(completing.IsCompleted, "A held actual session must prevent hosted completion.");
            Assert.IsFalse(stopping.IsCompleted, "The uncanceled shared stop must still join that session.");
            journal.Release.TrySetResult();
            await ObserveCompletionAsync(completing, stopping, bindFault).ConfigureAwait(false);
            if (!bindFault) await server.StopAsync(CancellationToken.None).ConfigureAwait(false);
            await AssertRetiredSessionAsync(client, journal, TestContext.CancellationToken).ConfigureAwait(false);
        }
        finally
        {
            signals.Release.Set();
            journal.Release.TrySetResult();
            stopping ??= server.StopAsync(CancellationToken.None);
            var owned = Task.WhenAll(stopping, completing ?? Task.CompletedTask);
            await owned.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            Assert.IsTrue(owned.IsCompleted, "Unsettled native work forbids dependency retirement.");
        }
    }

    private static async Task AssertRetiredSessionAsync(TcpClient client, HeldGreetingJournal journal, CancellationToken token)
    {
        Assert.IsTrue(journal.Settled);
        var eof = await client.GetStream().ReadAsync(new byte[1], CancellationToken.None)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(2), token).ConfigureAwait(false);
        Assert.AreEqual(0, eof);
    }

    private static async Task ObserveCompletionAsync(Task completing, Task stopping, bool bindFault)
    {
        if (bindFault)
        {
            // Both handles are the actual service bodies retained by the caller.
#pragma warning disable VSTHRD003
            var failure = await Assert.ThrowsExactlyAsync<AggregateException>(() =>
                completing.WaitAsync(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
#pragma warning restore VSTHRD003
            Assert.IsTrue(failure.Flatten().InnerExceptions.All(error =>
                error is SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse, }));
#pragma warning disable VSTHRD003
            var stopFailure = await Assert.ThrowsExactlyAsync<AggregateException>(() => stopping).ConfigureAwait(false);
#pragma warning restore VSTHRD003
            Assert.Contains(failure, stopFailure.InnerExceptions);
        }
        else
        {
            await completing.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            await stopping.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
    }

    private static EnvironmentConfig Configuration(int port, int blockedPort, bool bindFault) => new()
    {
        Smtp = new SmtpConfig { Hostname = "mail.example.test" },
        Imap = new ImapConfig { EnableImap = true, Port = port, EnableImplicitTls = bindFault, ImplicitTlsPort = blockedPort },
        Pop3 = new Pop3Config { EnablePop3 = true, Port = port, EnableImplicitTls = bindFault, ImplicitTlsPort = blockedPort },
    };

    private sealed class StartSignals : IDisposable
    {
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ManualResetEventSlim Release { get; } = new(initialState: false);
        internal int Starts;
        public void Dispose() => Release.Dispose();
    }

    private sealed class ListenerLogger<T>(StartSignals signals) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (eventId.Id is not (2 or 1001) || Interlocked.Increment(ref signals.Starts) != 1) return;
            signals.Started.TrySetResult();
            if (!signals.Release.Wait(TimeSpan.FromSeconds(5), CancellationToken.None))
                throw new TimeoutException("The owned first-listener publication gate was not released.");
        }
    }

    private sealed class HeldGreetingJournal : IGatewayTrafficJournal
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Settled { get; private set; }
        public async Task AppendAsync(GatewayTrafficRecord record, CancellationToken cancellationToken = default)
        {
            var registration = cancellationToken.Register(() => Cancelled.TrySetResult());
            await using var registrationLifetime = registration.ConfigureAwait(false);
            Entered.TrySetResult();
            try
            {
                // The outer fixture always releases and joins this actual journal body before retiring services.
#pragma warning disable VSTHRD003
                await Release.Task.ConfigureAwait(false);
#pragma warning restore VSTHRD003
                cancellationToken.ThrowIfCancellationRequested();
            }
            finally { Settled = true; }
        }
        public Task<IReadOnlyList<GatewayTrafficRecord>> ReadSessionAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class UnusedLeaseStore : IPop3MaildropLeaseStore
    {
        public Task<Pop3MaildropLease?> TryAcquireAsync(Guid userId, TimeSpan lifetime, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> RenewAsync(Pop3MaildropLease lease, TimeSpan lifetime, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ReleaseAsync(Pop3MaildropLease lease, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
