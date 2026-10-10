using System.Runtime.ExceptionServices;
using System.Text;
using mk8.email.Application.Interfaces;

namespace mk8.email.Application.Tests;

internal sealed partial class TransportSecurityTests
{
    public TestContext? TestContext { get; set; }

    [TestMethod]
    [DataRow("smtp", false, false)]
    [DataRow("smtp", true, false)]
    [DataRow("smtp", false, true)]
    [DataRow("smtp", true, true)]
    [DataRow("imap", false, false)]
    [DataRow("imap", true, false)]
    [DataRow("imap", false, true)]
    [DataRow("imap", true, true)]
    [DataRow("pop3", false, false)]
    [DataRow("pop3", true, false)]
    [DataRow("pop3", false, true)]
    [DataRow("pop3", true, true)]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task NativeAuthenticationReadCancellationIsObservedWithoutCancellingServerWork(
        string protocol, bool afterInner, bool injectAssertion)
    {
        Assert.IsNotNull(TestContext);
        var held = new HeldNativeAuthentication(afterInner);
        var port = ReservePort();
        var server = await StartObservedNativeServerAsync(protocol, port, held).ConfigureAwait(false);
        await using var serverLifetime = server.ConfigureAwait(false);
        var connection = await ProtocolConnection.ConnectAsync(port).ConfigureAwait(false);
        await using var connectionLifetime = connection.ConfigureAwait(false);
        var injected = new AssertFailedException("controlled assertion while native authentication is held");
        Exception? failure = null;
        try
        {
            await BeginHeldNativeAuthenticationAsync(protocol, connection).ConfigureAwait(false);
            await held.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.CancellationToken).ConfigureAwait(false);
            if (injectAssertion) throw injected;
            await Assert.ThrowsAsync<OperationCanceledException>(connection.ReadLineAsync).ConfigureAwait(false);
            Assert.IsFalse(held.Completing.IsCompleted);
            Assert.IsTrue(server.AuthenticationObserver.WritePoint(AuthenticationPoint.ReadCancelled));
            var events = server.AuthenticationObserver.Snapshot(AuthenticationPoint.ReadCancelled).Events;
            Assert.HasCount(1, events);
            Assert.AreEqual(AuthenticationPhase.Entered, events[0].Phase);
        }
        catch (Exception error) when (!IsFatalAuthenticationControl(error))
        {
            failure = error;
        }
        finally
        {
            held.ReleaseAndSeal();
            failure = await ObserveOwnedAuthenticationCleanupAsync(held.Completing, failure).ConfigureAwait(false);
        }
        AssertControlledAuthenticationOutcome(injectAssertion, injected, failure);
        Assert.IsTrue(held.Completing.IsCompletedSuccessfully);
        await AssertNativeAuthenticationReplyAsync(protocol, connection).ConfigureAwait(false);
        AssertAuthenticationEvents(server.AuthenticationObserver, primary: false, AuthenticationPhase.Returned);
        Assert.AreEqual(0, server.MailQueue.EnqueueCalls);
    }

    private Task<ServerFixture> StartObservedNativeServerAsync(string protocol, int port, HeldNativeAuthentication held) =>
        protocol switch
        {
            "smtp" => ServerFixture.StartSmtpAsync(CreateEnvironment(smtpImplicitTlsPort: port), port,
                authenticationDecorator: inner => new HeldNativeMailAuthenticator(inner, held)),
            "imap" => ServerFixture.StartImapAsync(CreateEnvironment(imapImplicitTlsPort: port), port,
                authenticationDecorator: inner => new HeldNativeMailAuthenticator(inner, held)),
            "pop3" => ServerFixture.StartPop3Async(CreateEnvironment(pop3ImplicitTlsPort: port), port,
                authenticationDecorator: inner => new HeldNativeMailAuthenticator(inner, held)),
            _ => throw new ArgumentOutOfRangeException(nameof(protocol)),
        };

    private static async Task BeginHeldNativeAuthenticationAsync(string protocol, ProtocolConnection connection)
    {
        await connection.UpgradeToTlsAsync("email.tenant.example.test").ConfigureAwait(false);
        await connection.ReadLineAsync().ConfigureAwait(false);
        if (string.Equals(protocol, "imap", StringComparison.Ordinal))
        {
            await connection.WriteLineAsync($"a1 LOGIN \"{TestUsername}\" \"{TestPassword}\"").ConfigureAwait(false);
        }
        else
        {
            if (string.Equals(protocol, "smtp", StringComparison.Ordinal))
            {
                await connection.WriteLineAsync("EHLO controlled.example").ConfigureAwait(false);
                await connection.ReadSmtpResponseAsync().ConfigureAwait(false);
            }
            var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"\0{TestUsername}\0{TestPassword}"));
            await connection.WriteLineAsync($"AUTH PLAIN {credentials}").ConfigureAwait(false);
        }
    }

    private static async Task AssertNativeAuthenticationReplyAsync(string protocol, ProtocolConnection connection)
    {
        var expected = protocol switch
        {
            "smtp" => "235 ",
            "imap" => "a1 OK ",
            "pop3" => "+OK ",
            _ => throw new ArgumentOutOfRangeException(nameof(protocol)),
        };
        Assert.StartsWith(expected, await connection.ReadLineAsync().ConfigureAwait(false), StringComparison.Ordinal);
    }

    private static async Task<Exception?> ObserveOwnedAuthenticationCleanupAsync(Task owned, Exception? original)
    {
        try
        {
            await owned.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            return original;
        }
        catch (Exception cleanup) when (!IsFatalAuthenticationControl(cleanup))
        {
            return original is null ? cleanup : new AggregateException(original, cleanup);
        }
    }

    private static bool IsFatalAuthenticationControl(Exception error) =>
        error is OutOfMemoryException or StackOverflowException or AccessViolationException;

    private static void AssertControlledAuthenticationOutcome(
        bool injectAssertion, Exception injected, Exception? failure)
    {
        // A failed entry/cleanup stage is not the intended injected assertion.
        // Surface its actual fault inventory before testing the positive control.
        if (failure is not null && (!injectAssertion || !ReferenceEquals(injected, failure)))
            ExceptionDispatchInfo.Capture(failure).Throw();
        if (injectAssertion) Assert.AreSame(injected, failure);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void NativeAuthenticationPostconditionPreservesUnexpectedOriginalFailure(bool injectAssertion)
    {
        var expected = new AssertFailedException("controlled expected injection");
        var original = new TimeoutException("controlled failure before injection");
        var actual = Assert.Throws<TimeoutException>(() =>
            AssertControlledAuthenticationOutcome(injectAssertion, expected, original));
        Assert.AreSame(original, actual);
    }

    [TestMethod]
    public void NativeAuthenticationPostconditionPreservesOriginalAndOwnedFaultInventory()
    {
        var original = new AssertFailedException("controlled actual injection");
        var owned = new InvalidOperationException("controlled owned task failure");
        var inventory = new AggregateException(original, owned);
        var actual = Assert.Throws<AggregateException>(() =>
            AssertControlledAuthenticationOutcome(injectAssertion: true, original, inventory));
        Assert.AreSame(inventory, actual);
        Assert.AreSame(original, actual.InnerExceptions[0]);
        Assert.AreSame(owned, actual.InnerExceptions[1]);
    }

    [TestMethod]
    public void NativeAuthenticationPostconditionAdmitsOnlyTheIntendedControlOutcomes()
    {
        var injected = new AssertFailedException("controlled actual injection");
        AssertControlledAuthenticationOutcome(injectAssertion: false, injected, failure: null);
        AssertControlledAuthenticationOutcome(injectAssertion: true, injected, injected);
        Assert.Throws<AssertFailedException>(() =>
            AssertControlledAuthenticationOutcome(injectAssertion: true, injected, failure: null));
    }

    [TestMethod]
    public async Task NativeAuthenticationEntryWaitCancellationDoesNotCancelTheOwnedBody()
    {
        using var entryCancellation = new CancellationTokenSource();
        var innerBody = new TaskCompletionSource<AuthenticatedMailUser?>(TaskCreationOptions.RunContinuationsAsynchronously);
        // This control creates the body and completes/joins it through the held proxy in finally.
#pragma warning disable VSTHRD003
        var inner = new ControlledMailAuthenticator(() => innerBody.Task);
#pragma warning restore VSTHRD003
        var held = new HeldNativeAuthentication(afterInner: true);
        var completing = held.InvokeAsync(inner, primary: false, "user", "password", CancellationToken.None);
        Exception? failure = null;
        try
        {
            await entryCancellation.CancelAsync().ConfigureAwait(false);
            var actual = await Assert.ThrowsAsync<OperationCanceledException>(() =>
                held.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), entryCancellation.Token)).ConfigureAwait(false);
            Assert.AreEqual(entryCancellation.Token, actual.CancellationToken);
            Assert.IsFalse(completing.IsCompleted);
            Assert.IsFalse(held.Entered.Task.IsCompleted);
            Assert.IsNotNull(inner.Inputs);
            Assert.AreEqual(CancellationToken.None, inner.Inputs.Cancellation);
        }
        catch (Exception error) when (!IsFatalAuthenticationControl(error))
        {
            failure = error;
        }
        finally
        {
            innerBody.TrySetResult(null);
            held.ReleaseAndSeal();
            failure = await ObserveOwnedAuthenticationCleanupAsync(completing, failure).ConfigureAwait(false);
        }
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        Assert.IsTrue(completing.IsCompletedSuccessfully);
        Assert.IsTrue(held.Entered.Task.IsCompletedSuccessfully);
    }

    [TestMethod]
    public async Task NativeAuthenticationCleanupRetainsOriginalAndOwnedFault()
    {
        var original = new AssertFailedException("controlled original assertion");
        var fault = new InvalidOperationException("controlled authentication task fault");
        var actual = await ObserveOwnedAuthenticationCleanupAsync(Task.FromException(fault), original).ConfigureAwait(false);
        var aggregate = actual as AggregateException;
        Assert.IsNotNull(aggregate);
        Assert.HasCount(2, aggregate.InnerExceptions);
        Assert.AreSame(original, aggregate.InnerExceptions[0]);
        Assert.AreSame(fault, aggregate.InnerExceptions[1]);
    }

    [TestMethod]
    public async Task NativeAuthenticationCleanupPreservesOriginalAfterCompletedWork()
    {
        var original = new AssertFailedException("controlled original assertion");
        Assert.AreSame(original, await ObserveOwnedAuthenticationCleanupAsync(Task.CompletedTask, original).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task NativeAuthenticationUnusedGateSealsBeforeAnyLateInvocation()
    {
        var held = new HeldNativeAuthentication(afterInner: false);
        var calls = 0;
        var inner = new ControlledMailAuthenticator(() =>
        {
            calls++;
            return Task.FromResult<AuthenticatedMailUser?>(null);
        });
        var retained = held.Completing;
        held.ReleaseAndSeal();
        Assert.IsNull(await retained.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false));
        Assert.AreSame(retained, held.Completing);
        Assert.Throws<InvalidOperationException>(() => held.InvokeAsync(inner, primary: false, "user", "password", CancellationToken.None));
        Assert.AreEqual(0, calls);
        Assert.IsFalse(held.Entered.Task.IsCompleted);
    }

    private sealed class HeldNativeMailAuthenticator(IMailAuthenticator inner, HeldNativeAuthentication held) : IMailAuthenticator
    {
        public Task<AuthenticatedMailUser?> AuthenticateAsync(
            string username, string password, CancellationToken cancellationToken = default) =>
            held.InvokeAsync(inner, primary: false, username, password, cancellationToken);
        public Task<AuthenticatedMailUser?> AuthenticatePrimaryAsync(
            string username, string password, CancellationToken cancellationToken = default) =>
            held.InvokeAsync(inner, primary: true, username, password, cancellationToken);
    }

    private sealed class HeldNativeAuthentication
    {
        private readonly TaskCompletionSource<Task<AuthenticatedMailUser?>> _body = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly bool _afterInner;
        private int _state;
        public HeldNativeAuthentication(bool afterInner)
        {
            _afterInner = afterInner;
            // Publish the immutable actual-body proxy before entry can become observable.
            Completing = _body.Task.Unwrap();
        }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<AuthenticatedMailUser?> Completing { get; }

        public Task<AuthenticatedMailUser?> InvokeAsync(
            IMailAuthenticator inner, bool primary, string username, string password, CancellationToken token)
        {
            if (Interlocked.CompareExchange(ref _state, 1, 0) != 0)
                throw new InvalidOperationException("The controlled authentication gate cannot be reused.");
            _body.SetResult(InvokeHeldAsync(inner, primary, username, password, token));
            // This proxy owns the body started here; cleanup retains it before entry can be observed.
#pragma warning disable VSTHRD003
            return Completing;
#pragma warning restore VSTHRD003
        }

        public void ReleaseAndSeal()
        {
            // A failure before invocation leaves no body to join; prevent any late controlled entry.
            if (Interlocked.CompareExchange(ref _state, 2, 0) == 0)
                _body.SetResult(Task.FromResult<AuthenticatedMailUser?>(null));
            _release.TrySetResult();
        }

        private async Task<AuthenticatedMailUser?> InvokeHeldAsync(
            IMailAuthenticator inner, bool primary, string username, string password, CancellationToken token)
        {
            AuthenticatedMailUser? result = null;
            if (_afterInner)
                result = await InvokeObservedAuthenticationAsync(inner, primary, username, password, token).ConfigureAwait(false);
            Entered.TrySetResult();
            await _release.Task.WaitAsync(token).ConfigureAwait(false);
            return _afterInner ? result
                : await InvokeObservedAuthenticationAsync(inner, primary, username, password, token).ConfigureAwait(false);
        }
    }
}
