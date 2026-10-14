using mk8.email.Contracts.Messaging;
using mk8.email.Imap.Presentation;
using mk8.email.Messaging;

namespace mk8.email.Application.Tests;

internal sealed partial class TransportSecurityTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [Timeout(20_000, CooperativeCancellation = true)]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "MA0051", Justification = "Keep the actual authenticated IDLE child hold, initiating exit, pending retirement and unconditional independent joins in one ordered control.")]
    public async Task ActualImapIdleRetainsHeldJournalChildBeforeShutdownRetirement(bool pollWriteFailure)
    {
        // Finite explicit owned identity isolates IDLE ownership, not password/MFA
        // verification or any historical native timeout/provider diagnosis.
        Assert.IsNotNull(TestContext);
        var authentication = new EofFixtureAuthenticator();
        var journal = new HeldIdleJournal(pollWriteFailure);
        var logger = new CapturingLogger<ImapServerService>();
        var port = ReservePort();
        var server = await ServerFixture.StartImapAsync(CreateEnvironment(imapImplicitTlsPort: port), port,
            logger: logger, journal: journal, authenticationDecorator: _ => authentication).ConfigureAwait(false);
        ProtocolConnection? connection = null;
        Task? stopping = null;
        Task? retiring = null;
        Exception? original = null;
        try
        {
            authentication.User = await server.GetOwnedFixtureUserAsync(TestContext.CancellationToken).ConfigureAwait(false);
            await server.SeedInboxMessagesForSearchAsync().ConfigureAwait(false);
            // Assigned before any later operation. The unconditional finally
            // independently attempts this exact owner's async disposal after
            // child settlement, retaining initiating and all cleanup faults.
#pragma warning disable CA2000
            connection = await ProtocolConnection.ConnectAsync(port).ConfigureAwait(false);
#pragma warning restore CA2000
            await PrepareOwnedIdleAsync(connection).ConfigureAwait(false);
            journal.Arm();
            await connection.WriteLineAsync("DONE").ConfigureAwait(false);
            await journal.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.CancellationToken).ConfigureAwait(false);
            if (pollWriteFailure)
                await server.ChangeOwnedInboxFlagForIdleAsync(TestContext.CancellationToken).ConfigureAwait(false);
            else
                stopping = server.StopImapAsync();
            // Poll cadence remains the actual unchanged five seconds. Its write
            // fault must cancel the actual child before StopAsync is requested.
            await journal.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(7), TestContext.CancellationToken).ConfigureAwait(false);
            Assert.IsFalse(journal.Settled.Task.IsCompleted);
            Assert.DoesNotContain(entry => ContainsIdleFault(entry.Exception, journal.WriteFault), logger.Entries);
            stopping ??= server.StopImapAsync();
            retiring = server.DisposeAsync().AsTask();
            Assert.IsFalse(stopping.IsCompleted);
            Assert.IsFalse(server.ImapExecutionTask.IsCompleted);
            Assert.IsFalse(retiring.IsCompleted);
            Assert.IsFalse(server.DependenciesRetirementEntered);
        }
#pragma warning disable CA1031 // Retain the actual initiating assertion while every independently owned operation settles below.
        catch (Exception error) when (!IsFatalAuthenticationControl(error)) { original = error; }
#pragma warning restore CA1031
        finally
        {
            journal.Release.TrySetResult();
            var cleanup = new List<Func<ValueTask>>();
            // These are actual tasks started by THIS control, not observations
            // that cancel or retire the work. No timeout declares settlement.
            if (stopping is not null) cleanup.Add(() => new ValueTask(stopping));
            if (retiring is not null) cleanup.Add(() => new ValueTask(retiring));
            else cleanup.Add(server.DisposeAsync);
            if (connection is not null) cleanup.Add(connection.DisposeAsync);
            await DisposeEveryOwnerAsync(cleanup, original).ConfigureAwait(false);
        }
        Assert.IsTrue(journal.Settled.Task.IsCompletedSuccessfully);
        Assert.IsTrue(server.ImapExecutionTask.IsCompleted);
        Assert.IsTrue(server.DependenciesRetirementEntered);
        Assert.AreEqual(1, authentication.Calls);
        Assert.AreEqual(pollWriteFailure ? 1 : 0, journal.WriteFaults);
        if (pollWriteFailure)
        {
            Assert.Contains(entry => ContainsIdleFault(entry.Exception, journal.WriteFault), logger.Entries,
                "Original write-fault reference must survive actual child settlement. Logged exception types: "
                + string.Join(", ", logger.Entries.Select(entry => entry.Exception?.GetType().Name ?? "none")));
        }
    }

    private static bool ContainsIdleFault(Exception? observed, Exception expected)
    {
        if (ReferenceEquals(observed, expected)) return true;
        if (observed is AggregateException inventory)
            return inventory.Flatten().InnerExceptions.Any(error => ContainsIdleFault(error, expected));
        return observed?.InnerException is { } inner && ContainsIdleFault(inner, expected);
    }

    private static async Task PrepareOwnedIdleAsync(ProtocolConnection connection)
    {
        await connection.UpgradeToTlsAsync("email.tenant.example.test").ConfigureAwait(false);
        Assert.StartsWith("* OK ", await connection.ReadLineAsync().ConfigureAwait(false), StringComparison.Ordinal);
        await connection.WriteLineAsync($"a1 LOGIN \"{TestUsername}\" \"{TestPassword}\"").ConfigureAwait(false);
        Assert.StartsWith("a1 OK", await connection.ReadLineAsync().ConfigureAwait(false), StringComparison.Ordinal);
        await connection.WriteLineAsync("a2 SELECT INBOX").ConfigureAwait(false);
        var selected = await ReadUntilTaggedResponseAsync(connection, "a2").ConfigureAwait(false);
        Assert.StartsWith("a2 OK", selected[^1], StringComparison.Ordinal);
        await connection.WriteLineAsync("a3 IDLE").ConfigureAwait(false);
        Assert.AreEqual("+ idling", await connection.ReadLineAsync().ConfigureAwait(false), StringComparer.Ordinal);
    }

    private sealed class HeldIdleJournal(bool pollWriteFailure) : IGatewayTrafficJournal
    {
        private int _armed;
        private int _holding;
        private int _writeFaults;
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Settled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal IOException WriteFault { get; } = new("Controlled actual IDLE poll response journal fault.");
        internal int WriteFaults => Volatile.Read(ref _writeFaults);
        internal void Arm() => Volatile.Write(ref _armed, 1);

        public Task AppendAsync(GatewayTrafficRecord record, CancellationToken cancellationToken = default)
        {
            if (Volatile.Read(ref _armed) != 0 && string.Equals(record.Direction, GatewayTrafficDirections.Inbound, StringComparison.Ordinal)
                && Interlocked.CompareExchange(ref _holding, 1, 0) == 0)
            {
                return HoldReadAsync(cancellationToken);
            }
            if (pollWriteFailure && Volatile.Read(ref _holding) != 0
                && string.Equals(record.Direction, GatewayTrafficDirections.Outbound, StringComparison.Ordinal)
                && Interlocked.CompareExchange(ref _writeFaults, 1, 0) == 0)
            {
                return Task.FromException(WriteFault);
            }
            return Task.CompletedTask;
        }

        private async Task HoldReadAsync(CancellationToken token)
        {
            var registration = token.Register(() => Cancelled.TrySetResult());
            await using var registrationLifetime = registration.ConfigureAwait(false);
            Entered.TrySetResult();
            try
            {
                // Independently owned finite release: cancellation is observed
                // but cannot stand in for the actual read/journal settlement.
#pragma warning disable VSTHRD003
                await Release.Task.ConfigureAwait(false);
#pragma warning restore VSTHRD003
                token.ThrowIfCancellationRequested();
            }
            finally { Settled.TrySetResult(); }
        }

        public Task<IReadOnlyList<GatewayTrafficRecord>> ReadSessionAsync(Guid sessionId,
            CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<GatewayTrafficRecord>>([]);
    }
}
